using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.Configure<LocalAiOptions>(builder.Configuration.GetSection("LocalAi"));
builder.Services.AddHttpClient("ollama", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalAiOptions>>().Value;

    if (options.RequireLoopback && !IsLoopback(options.BaseUrl))
    {
        throw new InvalidOperationException("LocalAi:BaseUrl must be localhost/loopback when LocalAi:RequireLoopback is true.");
    }

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

builder.Services.AddSingleton<AppState>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddSingleton<DocumentProcessor>();
builder.Services.AddSingleton<SpeechTranscriber>();
builder.Services.AddSingleton<LocalToolRegistry>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (OllamaClient ollama, CancellationToken cancellationToken) =>
{
    var healthy = await ollama.IsHealthyAsync(cancellationToken);
    return Results.Ok(new { healthy });
});

app.MapGet("/api/models", async (OllamaClient ollama, CancellationToken cancellationToken) =>
{
    var models = await ollama.GetModelsAsync(cancellationToken);
    return Results.Ok(models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase));
});

app.MapGet("/api/documents/current", (AppState state) => Results.Ok(state.CurrentDocument));

app.MapPost("/api/documents", async (HttpRequest request, AppState state, IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ErrorResponse("Expected multipart form data."));
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new ErrorResponse("Upload a PDF or image file."));
    }

    var kind = DocumentProcessor.GetSupportedFileKind(file.FileName);
    var uploadsDirectory = Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
    Directory.CreateDirectory(uploadsDirectory);

    var fileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{Path.GetExtension(file.FileName).ToLowerInvariant()}";
    var filePath = Path.Combine(uploadsDirectory, fileName);

    await using (var stream = File.Create(filePath))
    {
        await file.CopyToAsync(stream, cancellationToken);
    }

    var document = new UploadedDocument(
        Id: Path.GetFileNameWithoutExtension(fileName),
        OriginalName: file.FileName,
        Path: filePath,
        Kind: kind,
        SizeBytes: file.Length,
        UploadedAt: DateTimeOffset.UtcNow);

    state.CurrentDocument = document;
    return Results.Ok(document);
});

app.MapPost("/api/transcribe", async (HttpRequest request, SpeechTranscriber transcriber, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ErrorResponse("Expected multipart form data."));
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("audio");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new ErrorResponse("Record audio before transcribing."));
    }

    var result = await transcriber.TranscribeAsync(file, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/chat", async (ChatRequest request, OllamaClient ollama, LocalToolRegistry tools, AppState state, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Model))
    {
        return Results.BadRequest(new ErrorResponse("Model is required."));
    }

    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new ErrorResponse("Message is required."));
    }

    var messages = new List<OllamaMessage>
    {
        new("system", BuildSystemPrompt(request.SystemPrompt, tools.GetTools())),
        new("user", request.Message)
    };

    var toolResults = new List<ToolResult>();
    OllamaChatResponse response = default!;

    for (var step = 0; step < 4; step++)
    {
        response = await ollama.ChatAsync(new OllamaChatRequest(
            Model: request.Model,
            Stream: false,
            Messages: messages,
            Options: new OllamaOptions(Temperature: request.Temperature ?? 0.7, TopP: 0.95, TopK: 64),
            Tools: tools.GetTools().Select(ToOllamaTool).ToList(),
            Think: request.EnableThinking), cancellationToken);

        var content = response.Message?.Content ?? string.Empty;
        var nativeToolCalls = response.Message?.ToolCalls?.Where(call => !string.IsNullOrWhiteSpace(call.Function?.Name)).ToList() ?? [];
        var fallbackToolCall = nativeToolCalls.Count == 0 && TryParseToolCall(content, out var parsedToolCall) ? parsedToolCall : null;

        if (nativeToolCalls.Count == 0 && fallbackToolCall is null)
        {
            return Results.Ok(new ChatResponse(
                Response: content,
                Reasoning: response.Message?.Thinking ?? response.Message?.Reasoning,
                Stats: OllamaStats.FromChatResponse(response),
                ToolResults: toolResults));
        }

        messages.Add(response.Message ?? new OllamaMessage("assistant", content));

        if (nativeToolCalls.Count > 0)
        {
            foreach (var toolCall in nativeToolCalls)
            {
                var result = await tools.InvokeAsync(toolCall.Function!.Name!, toolCall.Function.Arguments ?? new Dictionary<string, JsonElement>(), request.Model, cancellationToken);
                toolResults.Add(result);
                messages.Add(new OllamaMessage("tool", result.Content, Name: result.Tool));
            }
        }
        else if (fallbackToolCall is not null)
        {
            var result = await tools.InvokeAsync(fallbackToolCall.Tool, fallbackToolCall.Arguments, request.Model, cancellationToken);
            toolResults.Add(result);
            messages.Add(new OllamaMessage("user", $"Tool {result.Tool} returned this result:\n{result.Content}\n\nUse the tool result to answer the original question. Do not emit another tool call unless another tool is required."));
        }
    }

    return Results.Ok(new ChatResponse(
        Response: response.Message?.Content ?? "Stopped after maximum tool-calling steps.",
        Reasoning: response.Message?.Thinking ?? response.Message?.Reasoning,
        Stats: OllamaStats.FromChatResponse(response),
        ToolResults: toolResults));
});

app.MapPost("/api/chat/stream", async (ChatRequest request, HttpResponse httpResponse, OllamaClient ollama, LocalToolRegistry tools, CancellationToken cancellationToken) =>
{
    httpResponse.ContentType = "application/x-ndjson";

    if (string.IsNullOrWhiteSpace(request.Model))
    {
        await WriteStreamEventAsync(httpResponse, new StreamEvent("error", Error: "Model is required."), cancellationToken);
        return;
    }

    if (string.IsNullOrWhiteSpace(request.Message))
    {
        await WriteStreamEventAsync(httpResponse, new StreamEvent("error", Error: "Message is required."), cancellationToken);
        return;
    }

    var messages = new List<OllamaMessage>
    {
        new("system", BuildSystemPrompt(request.SystemPrompt, tools.GetTools())),
        new("user", request.Message)
    };

    OllamaChatResponse response = default!;

    for (var step = 0; step < 4; step++)
    {
        response = await ollama.ChatStreamAsync(new OllamaChatRequest(
            Model: request.Model,
            Stream: true,
            Messages: messages,
            Options: new OllamaOptions(Temperature: request.Temperature ?? 0.7, TopP: 0.95, TopK: 64),
            Tools: tools.GetTools().Select(ToOllamaTool).ToList(),
            Think: request.EnableThinking), async chunk =>
            {
                if (!string.IsNullOrEmpty(chunk.Message?.Thinking))
                {
                    await WriteStreamEventAsync(httpResponse, new StreamEvent("reasoning", Text: chunk.Message.Thinking), cancellationToken);
                }

                if (!string.IsNullOrEmpty(chunk.Message?.Reasoning))
                {
                    await WriteStreamEventAsync(httpResponse, new StreamEvent("reasoning", Text: chunk.Message.Reasoning), cancellationToken);
                }

                if (!string.IsNullOrEmpty(chunk.Message?.Content))
                {
                    await WriteStreamEventAsync(httpResponse, new StreamEvent("content", Text: chunk.Message.Content), cancellationToken);
                }
            }, cancellationToken);

        await WriteStreamEventAsync(httpResponse, new StreamEvent("stats", Stats: OllamaStats.FromChatResponse(response)), cancellationToken);

        var content = response.Message?.Content ?? string.Empty;
        var nativeToolCalls = response.Message?.ToolCalls?.Where(call => !string.IsNullOrWhiteSpace(call.Function?.Name)).ToList() ?? [];
        var fallbackToolCall = nativeToolCalls.Count == 0 && TryParseToolCall(content, out var parsedToolCall) ? parsedToolCall : null;

        if (nativeToolCalls.Count == 0 && fallbackToolCall is null)
        {
            await WriteStreamEventAsync(httpResponse, new StreamEvent("done"), cancellationToken);
            return;
        }

        messages.Add(response.Message ?? new OllamaMessage("assistant", content));

        if (nativeToolCalls.Count > 0)
        {
            foreach (var toolCall in nativeToolCalls)
            {
                var result = await tools.InvokeAsync(toolCall.Function!.Name!, toolCall.Function.Arguments ?? new Dictionary<string, JsonElement>(), request.Model, cancellationToken);
                await WriteStreamEventAsync(httpResponse, new StreamEvent("tool", ToolResult: result), cancellationToken);
                messages.Add(new OllamaMessage("tool", result.Content, Name: result.Tool));
            }
        }
        else if (fallbackToolCall is not null)
        {
            var result = await tools.InvokeAsync(fallbackToolCall.Tool, fallbackToolCall.Arguments, request.Model, cancellationToken);
            await WriteStreamEventAsync(httpResponse, new StreamEvent("tool", ToolResult: result), cancellationToken);
            messages.Add(new OllamaMessage("user", $"Tool {result.Tool} returned this result:\n{result.Content}\n\nUse the tool result to answer the original question. Do not emit another tool call unless another tool is required."));
        }
    }

    await WriteStreamEventAsync(httpResponse, new StreamEvent("content", Text: response.Message?.Content ?? "Stopped after maximum tool-calling steps."), cancellationToken);
    await WriteStreamEventAsync(httpResponse, new StreamEvent("done"), cancellationToken);
});

app.MapPost("/api/ocr", async (DocumentActionRequest request, DocumentProcessor processor, AppState state, CancellationToken cancellationToken) =>
{
    var document = state.CurrentDocument;

    if (document is null)
    {
        return Results.BadRequest(new ErrorResponse("Upload a document first."));
    }

    var result = await processor.ProcessAsync(document, request.Model, DocumentOperation.Ocr, request.Pages ?? 3, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/invoice", async (DocumentActionRequest request, DocumentProcessor processor, AppState state, CancellationToken cancellationToken) =>
{
    var document = state.CurrentDocument;

    if (document is null)
    {
        return Results.BadRequest(new ErrorResponse("Upload a document first."));
    }

    var result = await processor.ProcessAsync(document, request.Model, DocumentOperation.Invoice, request.Pages ?? 3, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static string BuildSystemPrompt(string? systemPrompt, IReadOnlyList<LocalTool> tools)
{
    var basePrompt = string.IsNullOrWhiteSpace(systemPrompt)
        ? "You are LLLM, a concise local-only assistant running against Ollama."
        : systemPrompt;
    var toolDescriptions = string.Join(Environment.NewLine, tools.Select(tool => $"- {tool.Name}: {tool.Description}. Arguments JSON schema: {tool.ParametersJsonSchema}"));
    var toolCallExample = "{\"tool\":\"tool_name\",\"arguments\":{}}";

    return $"""
{basePrompt}

Local-only constraints:
- Use local Ollama models and local tools only.
- Do not suggest cloud AI services unless the user explicitly asks.

Available tools:
{toolDescriptions}

Tool protocol fallback:
- Prefer native Ollama tool calls when available.
- If native tool calls are unavailable, respond with exactly one JSON object and no markdown: {toolCallExample}
- After a tool result is supplied, answer normally.
""";
}

static OllamaTool ToOllamaTool(LocalTool tool) => new(
    Type: "function",
    Function: new OllamaFunctionTool(
        Name: tool.Name,
        Description: tool.Description,
        Parameters: JsonNode.Parse(tool.ParametersJsonSchema) ?? new JsonObject()));

static bool TryParseToolCall(string content, out ParsedToolCall toolCall)
{
    toolCall = default!;
    var trimmed = content.Trim();

    if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
    {
        return false;
    }

    try
    {
        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;

        if (!root.TryGetProperty("tool", out var toolElement) || toolElement.GetString() is not { Length: > 0 } tool)
        {
            return false;
        }

        var arguments = root.TryGetProperty("arguments", out var argumentsElement)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsElement.GetRawText(), JsonDefaults.Options) ?? []
            : [];

        toolCall = new ParsedToolCall(tool, arguments);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool IsLoopback(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}

static async Task WriteStreamEventAsync(HttpResponse response, StreamEvent streamEvent, CancellationToken cancellationToken)
{
    await response.WriteAsync(JsonSerializer.Serialize(streamEvent, JsonDefaults.Options), cancellationToken);
    await response.WriteAsync("\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

public sealed class AppState
{
    public UploadedDocument? CurrentDocument { get; set; }
}

public sealed class OllamaClient(IHttpClientFactory httpClientFactory)
{
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient("ollama").GetAsync("/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var response = await httpClientFactory.CreateClient("ollama").GetFromJsonAsync<OllamaModelsResponse>("/api/tags", JsonDefaults.Options, cancellationToken)
            ?? new OllamaModelsResponse([]);
        return response.Models;
    }

    public async Task<string> GetLoadedModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient("ollama").GetAsync("/api/ps", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            return $"Ollama /api/ps returned {(int)response.StatusCode}: {error}";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("models", out var models) || models.GetArrayLength() == 0)
        {
            return "No Ollama models are currently loaded in memory.";
        }

        var lines = new List<string>();

        foreach (var model in models.EnumerateArray())
        {
            var name = model.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : "unknown";
            var size = model.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeBytes)
                ? $" {Math.Round(sizeBytes / 1024d / 1024d / 1024d, 2)} GB"
                : string.Empty;
            var until = model.TryGetProperty("expires_at", out var expiresElement) ? $" until {expiresElement.GetString()}" : string.Empty;
            lines.Add($"{name}{size}{until}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public async Task<OllamaChatResponse> ChatAsync(OllamaChatRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient("ollama").PostAsJsonAsync("/api/chat", request, JsonDefaults.Options, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama chat returned {(int)response.StatusCode}: {error}");
        }

        return await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty chat response.");
    }

    public async Task<OllamaChatResponse> ChatStreamAsync(OllamaChatRequest request, Func<OllamaChatResponse, Task> onChunk, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request, options: JsonDefaults.Options)
        };

        using var response = await httpClientFactory.CreateClient("ollama").SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama chat returned {(int)response.StatusCode}: {error}");
        }

        var model = request.Model;
        var role = "assistant";
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<OllamaToolCall>();
        OllamaChatResponse? lastChunk = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonDefaults.Options);

            if (chunk is null)
            {
                continue;
            }

            lastChunk = chunk;
            model = chunk.Model ?? model;

            if (chunk.Message is not null)
            {
                role = string.IsNullOrWhiteSpace(chunk.Message.Role) ? role : chunk.Message.Role;
                content.Append(chunk.Message.Content);
                thinking.Append(chunk.Message.Thinking);
                reasoning.Append(chunk.Message.Reasoning);

                if (chunk.Message.ToolCalls is { Count: > 0 })
                {
                    toolCalls.AddRange(chunk.Message.ToolCalls);
                }
            }

            await onChunk(chunk);
        }

        return new OllamaChatResponse(
            Model: model,
            Message: new OllamaMessage(role, content.ToString(), thinking.ToString(), reasoning.ToString(), toolCalls.Count > 0 ? toolCalls : null),
            TotalDuration: lastChunk?.TotalDuration,
            LoadDuration: lastChunk?.LoadDuration,
            PromptEvalCount: lastChunk?.PromptEvalCount,
            PromptEvalDuration: lastChunk?.PromptEvalDuration,
            EvalCount: lastChunk?.EvalCount,
            EvalDuration: lastChunk?.EvalDuration);
    }

    public async Task<OllamaGenerateResponse> GenerateAsync(OllamaGenerateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient("ollama").PostAsJsonAsync("/api/generate", request, JsonDefaults.Options, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama generate returned {(int)response.StatusCode}: {error}");
        }

        return await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty generate response.");
    }
}

public sealed class LocalToolRegistry(AppState state, DocumentProcessor processor)
{
    private readonly IReadOnlyList<LocalTool> _tools =
    [
        new("get_current_time", "Returns the current local date and time.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}"),
        new("show_current_document", "Shows the current uploaded document metadata.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}"),
        new("list_local_models", "Lists local Ollama models currently available.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}"),
        new("show_loaded_models", "Shows Ollama models currently loaded in memory.", "{\"type\":\"object\",\"properties\":{},\"required\":[]}"),
        new("summarize_current_document", "Runs OCR on the uploaded document and asks for a concise summary.", "{\"type\":\"object\",\"properties\":{\"pages\":{\"type\":\"integer\"}},\"required\":[]}"),
        new("ocr_current_document", "Runs OCR on the uploaded PDF/image using the active model.", "{\"type\":\"object\",\"properties\":{\"pages\":{\"type\":\"integer\"}},\"required\":[]}"),
        new("extract_invoice", "Extracts invoice JSON from the uploaded PDF/image using the active model.", "{\"type\":\"object\",\"properties\":{\"pages\":{\"type\":\"integer\"}},\"required\":[]}")
    ];

    public IReadOnlyList<LocalTool> GetTools() => _tools;

    public async Task<ToolResult> InvokeAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, string model, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string content;

        switch (name)
        {
            case "get_current_time":
                content = DateTimeOffset.Now.ToString("O");
                break;
            case "show_current_document":
                content = state.CurrentDocument is null ? "No document is uploaded." : JsonSerializer.Serialize(state.CurrentDocument, JsonDefaults.Options);
                break;
            case "list_local_models":
                content = await ListLocalModelsAsync(cancellationToken);
                break;
            case "show_loaded_models":
                content = await ShowLoadedModelsAsync(cancellationToken);
                break;
            case "summarize_current_document":
                content = await ProcessCurrentDocumentAsync(model, DocumentOperation.Summary, arguments, cancellationToken);
                break;
            case "ocr_current_document":
                content = await ProcessCurrentDocumentAsync(model, DocumentOperation.Ocr, arguments, cancellationToken);
                break;
            case "extract_invoice":
                content = await ProcessCurrentDocumentAsync(model, DocumentOperation.Invoice, arguments, cancellationToken);
                break;
            default:
                content = $"Tool '{name}' is not registered.";
                break;
        }

        stopwatch.Stop();
        return new ToolResult(name, content, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<string> ProcessCurrentDocumentAsync(string model, DocumentOperation operation, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        if (state.CurrentDocument is null)
        {
            return "No document is uploaded. Drag and drop a PDF/image first.";
        }

        var pages = arguments.TryGetValue("pages", out var pagesElement) && pagesElement.TryGetInt32(out var value) ? value : 3;
        var result = await processor.ProcessAsync(state.CurrentDocument, model, operation, pages, cancellationToken);
        return JsonSerializer.Serialize(result, JsonDefaults.Options);
    }

    private async Task<string> ListLocalModelsAsync(CancellationToken cancellationToken)
    {
        var models = await processor.GetModelsAsync(cancellationToken);
        return string.Join(Environment.NewLine, models.Select(model => $"{model.Name} {Math.Round(model.Size / 1024d / 1024d / 1024d, 2)} GB"));
    }

    private async Task<string> ShowLoadedModelsAsync(CancellationToken cancellationToken)
    {
        var loaded = await processor.GetLoadedModelsAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(loaded) ? "No Ollama models are currently loaded in memory." : loaded;
    }
}

public sealed class DocumentProcessor(OllamaClient ollama, IWebHostEnvironment environment)
{
    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync(CancellationToken cancellationToken) =>
        await ollama.GetModelsAsync(cancellationToken);

    public async Task<string> GetLoadedModelsAsync(CancellationToken cancellationToken) =>
        await ollama.GetLoadedModelsAsync(cancellationToken);

    public static string GetSupportedFileKind(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => "pdf",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" => "image",
            _ => throw new InvalidOperationException($"Unsupported file type '{extension}'. Use a PDF or image file.")
        };
    }

    public async Task<DocumentProcessResponse> ProcessAsync(UploadedDocument document, string model, DocumentOperation operation, int pages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Model is required.");
        }

        var stopwatch = Stopwatch.StartNew();
        var imagePaths = await PrepareImageInputsAsync(document, Math.Clamp(pages, 1, 10), cancellationToken);
        var pageResults = new List<DocumentPageResult>();

        try
        {
            for (var index = 0; index < imagePaths.Count; index++)
            {
                var prompt = BuildDocumentPrompt(operation, index + 1, imagePaths.Count);
                var imageBytes = await File.ReadAllBytesAsync(imagePaths[index], cancellationToken);
                var result = await ollama.GenerateAsync(new OllamaGenerateRequest(
                    Model: model,
                    Prompt: prompt,
                    Stream: false,
                    Images: [Convert.ToBase64String(imageBytes)],
                    Options: new OllamaOptions(Temperature: 0.0, TopP: 0.9, TopK: 40)), cancellationToken);

                pageResults.Add(new DocumentPageResult(index + 1, result.Response.Trim(), OllamaStats.FromGenerateResponse(result)));
            }
        }
        finally
        {
            foreach (var imagePath in imagePaths.Where(path => !string.Equals(path, document.Path, StringComparison.Ordinal)))
            {
                TryDeleteFile(imagePath);
            }
        }

        stopwatch.Stop();
        return new DocumentProcessResponse(operation.ToString(), document, stopwatch.Elapsed.TotalMilliseconds, pageResults);
    }

    private async Task<IReadOnlyList<string>> PrepareImageInputsAsync(UploadedDocument document, int pageLimit, CancellationToken cancellationToken)
    {
        if (document.Kind == "image")
        {
            return [document.Path];
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("PDF rendering currently uses macOS sips. Upload an image or run on macOS.");
        }

        var tempDirectory = Path.Combine(environment.ContentRootPath, "App_Data", "rendered", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var outputPrefix = Path.Combine(tempDirectory, "page");
        var result = await RunProcessAsync("sips", ["-s", "format", "png", document.Path, "--out", outputPrefix], environment.ContentRootPath, TimeSpan.FromMinutes(2), cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to render PDF with sips: {result.StandardError}");
        }

        var files = Directory.EnumerateFiles(tempDirectory)
            .Where(path => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(pageLimit)
            .ToList();

        if (files.Count == 0 && File.Exists(outputPrefix))
        {
            files.Add(outputPrefix);
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("PDF conversion produced no image files.");
        }

        return files;
    }

    private static string BuildDocumentPrompt(DocumentOperation operation, int page, int totalPages) => operation switch
    {
        DocumentOperation.Ocr => $"Extract all visible text from this document image. Preserve useful line breaks. Mark unclear text as [unclear]. Return only extracted text. Page {page} of {totalPages}.",
        DocumentOperation.Invoice => $"Extract invoice data from this document image. Return strict JSON with fields: vendorName, vendorAddress, invoiceNumber, invoiceDate, dueDate, currency, subtotal, tax, total, paymentTerms, purchaseOrderNumber, lineItems. lineItems must be an array with description, quantity, unitPrice, amount. Use null for missing values. Do not include markdown. Page {page} of {totalPages}.",
        DocumentOperation.Summary => $"Summarize this document page. Include key entities, dates, amounts, obligations, and action items if visible. Be concise. Page {page} of {totalPages}.",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return new ProcessResult(-1, string.Empty, $"Process timed out after {timeout.TotalSeconds:0} seconds: {fileName}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class SpeechTranscriber(Microsoft.Extensions.Options.IOptions<LocalAiOptions> options, IWebHostEnvironment environment)
{
    private readonly LocalSpeechOptions _speech = options.Value.Speech;

    public async Task<TranscriptionResponse> TranscribeAsync(IFormFile audio, CancellationToken cancellationToken)
    {
        if (!_speech.Enabled)
        {
            throw new InvalidOperationException("Local speech transcription is disabled. Set LocalAi:Speech:Enabled=true.");
        }

        if (string.IsNullOrWhiteSpace(_speech.WhisperExecutable) || !File.Exists(_speech.WhisperExecutable))
        {
            throw new InvalidOperationException($"Whisper executable not found: {_speech.WhisperExecutable}. Configure LocalAi:Speech:WhisperExecutable.");
        }

        if (string.IsNullOrWhiteSpace(_speech.ModelPath) || !File.Exists(_speech.ModelPath))
        {
            throw new InvalidOperationException($"Whisper model not found: {_speech.ModelPath}. Configure LocalAi:Speech:ModelPath.");
        }

        var audioDirectory = Path.Combine(environment.ContentRootPath, "App_Data", "audio");
        Directory.CreateDirectory(audioDirectory);

        var inputPath = Path.Combine(audioDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(audio.FileName)}");
        var wavPath = Path.ChangeExtension(inputPath, ".wav");
        var outputPrefix = Path.Combine(audioDirectory, Guid.NewGuid().ToString("N"));
        var stopwatch = Stopwatch.StartNew();

        await using (var stream = File.Create(inputPath))
        {
            await audio.CopyToAsync(stream, cancellationToken);
        }

        try
        {
            await ConvertAudioToWavAsync(inputPath, wavPath, cancellationToken);
            var arguments = new List<string>
            {
                "-m", _speech.ModelPath,
                "-f", wavPath,
                "-otxt",
                "-of", outputPrefix,
                "-nt"
            };

            if (!string.IsNullOrWhiteSpace(_speech.Language))
            {
                arguments.Add("-l");
                arguments.Add(_speech.Language);
            }

            var result = await RunProcessAsync(_speech.WhisperExecutable, arguments, environment.ContentRootPath, TimeSpan.FromSeconds(_speech.TimeoutSeconds), cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"whisper.cpp failed: {result.StandardError}");
            }

            var textPath = $"{outputPrefix}.txt";
            var transcript = File.Exists(textPath)
                ? await File.ReadAllTextAsync(textPath, cancellationToken)
                : result.StandardOutput;

            stopwatch.Stop();
            return new TranscriptionResponse(transcript.Trim(), stopwatch.Elapsed.TotalMilliseconds, _speech.ModelPath);
        }
        finally
        {
            TryDeleteFile(inputPath);
            TryDeleteFile(wavPath);
            TryDeleteFile($"{outputPrefix}.txt");
        }
    }

    private async Task ConvertAudioToWavAsync(string inputPath, string wavPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_speech.FfmpegExecutable) && File.Exists(_speech.FfmpegExecutable))
        {
            var result = await RunProcessAsync(
                _speech.FfmpegExecutable,
                ["-y", "-i", inputPath, "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wavPath],
                environment.ContentRootPath,
                TimeSpan.FromSeconds(_speech.TimeoutSeconds),
                cancellationToken);

            if (result.ExitCode == 0)
            {
                return;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var result = await RunProcessAsync(
                "afconvert",
                ["-f", "WAVE", "-d", "LEI16@16000", "-c", "1", inputPath, wavPath],
                environment.ContentRootPath,
                TimeSpan.FromSeconds(_speech.TimeoutSeconds),
                cancellationToken);

            if (result.ExitCode == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("Could not convert browser audio to 16 kHz mono WAV. Install ffmpeg or run on macOS with afconvert.");
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return new ProcessResult(-1, string.Empty, $"Process timed out after {timeout.TotalSeconds:0} seconds: {fileName}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class LocalAiOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434";
    public bool RequireLoopback { get; init; } = true;
    public int RequestTimeoutSeconds { get; init; } = 900;
    public LocalSpeechOptions Speech { get; init; } = new();
}

public sealed class LocalSpeechOptions
{
    public bool Enabled { get; init; } = true;
    public string WhisperExecutable { get; init; } = "/opt/homebrew/bin/whisper-cli";
    public string ModelPath { get; init; } = "models/ggml-base.en.bin";
    public string Language { get; init; } = "en";
    public string FfmpegExecutable { get; init; } = "/opt/homebrew/bin/ffmpeg";
    public int TimeoutSeconds { get; init; } = 120;
}

public enum DocumentOperation
{
    Ocr,
    Invoice,
    Summary
}

public sealed record ErrorResponse(string Error);
public sealed record StreamEvent(string Type, string? Text = null, ToolResult? ToolResult = null, OllamaStats? Stats = null, string? Error = null);
public sealed record ChatRequest(string Model, string Message, string? SystemPrompt, bool EnableThinking = true, double? Temperature = null);
public sealed record ChatResponse(string Response, string? Reasoning, OllamaStats? Stats, IReadOnlyList<ToolResult> ToolResults);
public sealed record TranscriptionResponse(string Text, double DurationMs, string ModelPath);
public sealed record DocumentActionRequest(string Model, int? Pages = null);
public sealed record UploadedDocument(string Id, string OriginalName, string Path, string Kind, long SizeBytes, DateTimeOffset UploadedAt);
public sealed record DocumentProcessResponse(string Operation, UploadedDocument Document, double TotalDurationMs, IReadOnlyList<DocumentPageResult> Pages);
public sealed record DocumentPageResult(int Page, string Text, OllamaStats? Stats);
public sealed record LocalTool(string Name, string Description, string ParametersJsonSchema);
public sealed record ToolResult(string Tool, string Content, double DurationMs);
public sealed record ParsedToolCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record OllamaModelsResponse([property: JsonPropertyName("models")] IReadOnlyList<OllamaModel> Models);
public sealed record OllamaModel([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("modified_at")] DateTimeOffset ModifiedAt, [property: JsonPropertyName("size")] long Size);
public sealed record OllamaChatRequest([property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("stream")] bool Stream, [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage> Messages, [property: JsonPropertyName("options")] OllamaOptions Options, [property: JsonPropertyName("tools")] IReadOnlyList<OllamaTool>? Tools = null, [property: JsonPropertyName("think")] bool? Think = null);
public sealed record OllamaMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string? Content, [property: JsonPropertyName("thinking")] string? Thinking = null, [property: JsonPropertyName("reasoning")] string? Reasoning = null, [property: JsonPropertyName("tool_calls")] IReadOnlyList<OllamaToolCall>? ToolCalls = null, [property: JsonPropertyName("name")] string? Name = null);
public sealed record OllamaOptions([property: JsonPropertyName("temperature")] double Temperature, [property: JsonPropertyName("top_p")] double TopP, [property: JsonPropertyName("top_k")] int TopK);
public sealed record OllamaChatResponse([property: JsonPropertyName("model")] string? Model, [property: JsonPropertyName("message")] OllamaMessage? Message, [property: JsonPropertyName("total_duration")] long? TotalDuration = null, [property: JsonPropertyName("load_duration")] long? LoadDuration = null, [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount = null, [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration = null, [property: JsonPropertyName("eval_count")] int? EvalCount = null, [property: JsonPropertyName("eval_duration")] long? EvalDuration = null);
public sealed record OllamaGenerateRequest([property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("prompt")] string Prompt, [property: JsonPropertyName("stream")] bool Stream, [property: JsonPropertyName("images")] IReadOnlyList<string> Images, [property: JsonPropertyName("options")] OllamaOptions Options);
public sealed record OllamaGenerateResponse([property: JsonPropertyName("response")] string Response, [property: JsonPropertyName("total_duration")] long? TotalDuration = null, [property: JsonPropertyName("load_duration")] long? LoadDuration = null, [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount = null, [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration = null, [property: JsonPropertyName("eval_count")] int? EvalCount = null, [property: JsonPropertyName("eval_duration")] long? EvalDuration = null);
public sealed record OllamaTool([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("function")] OllamaFunctionTool Function);
public sealed record OllamaFunctionTool([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("parameters")] JsonNode Parameters);
public sealed record OllamaToolCall([property: JsonPropertyName("function")] OllamaToolCallFunction? Function);
public sealed record OllamaToolCallFunction([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, JsonElement>? Arguments);

public sealed record OllamaStats(double? TotalMs, double? LoadMs, int? PromptTokens, double? PromptEvalMs, int? ResponseTokens, double? ResponseEvalMs)
{
    public static OllamaStats? FromChatResponse(OllamaChatResponse response) => FromRaw(response.TotalDuration, response.LoadDuration, response.PromptEvalCount, response.PromptEvalDuration, response.EvalCount, response.EvalDuration);
    public static OllamaStats? FromGenerateResponse(OllamaGenerateResponse response) => FromRaw(response.TotalDuration, response.LoadDuration, response.PromptEvalCount, response.PromptEvalDuration, response.EvalCount, response.EvalDuration);

    private static OllamaStats? FromRaw(long? total, long? load, int? promptTokens, long? promptEval, int? responseTokens, long? responseEval)
    {
        if (total is null && load is null && promptTokens is null && promptEval is null && responseTokens is null && responseEval is null)
        {
            return null;
        }

        return new OllamaStats(ToMs(total), ToMs(load), promptTokens, ToMs(promptEval), responseTokens, ToMs(responseEval));
    }

    private static double? ToMs(long? nanoseconds) => nanoseconds is null ? null : Math.Round(nanoseconds.Value / 1_000_000d, 2);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
