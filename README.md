# LLLM

Local-only Ollama workbench with chat, tool calling, PDF/image OCR, invoice extraction, and optional local voice input.

## Run

```bash
dotnet run --urls http://127.0.0.1:5288
```

Open `http://127.0.0.1:5288`.

## Local Voice Input

Voice input uses local `whisper.cpp`; no browser speech API or cloud service is used.

One setup path on Apple Silicon:

```bash
brew install whisper-cpp ffmpeg
mkdir -p models
curl -L -o models/ggml-base.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin
```

Fastest model option:

```bash
curl -L -o models/ggml-tiny.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin
```

Then update `appsettings.json` if needed:

```json
{
  "LocalAi": {
    "Speech": {
      "WhisperExecutable": "/opt/homebrew/bin/whisper-cli",
      "ModelPath": "models/ggml-base.en.bin",
      "FfmpegExecutable": "/opt/homebrew/bin/ffmpeg"
    }
  }
}
```

Use the `Mic` button to record. The transcript is inserted into the prompt box for review before sending.
