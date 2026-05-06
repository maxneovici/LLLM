# LLLM

Local-only Ollama workbench with chat, tool calling, PDF/image OCR, invoice extraction, local voice input, and embedded vector memory/RAG.

## Run

```bash
dotnet run --urls http://127.0.0.1:5288
```

Open `http://127.0.0.1:5288`.

## Local Memory And RAG

LLLM stores memory locally in an embedded SQLite file. There is no Docker database or background DB server.

```text
App_Data/memory/lllm-memory.sqlite
```

Embeddings are generated through local Ollama using `nomic-embed-text:latest`. Uploaded PDF/image/text documents are chunked, embedded, and searchable from the Local Memory panel. Relevant memory and document chunks are automatically retrieved before each chat turn and shown in the assistant response under `Memory Used`.

## PWA Install

Open LLLM in Chrome, Edge, or Safari and use the browser's install/add-to-dock option. The installed PWA still talks only to the local LLLM server on loopback.

## Publish Binary

Create a self-contained Apple Silicon binary:

```bash
scripts/publish-macos.sh
```

Run it directly:

```bash
artifacts/lllm-osx-arm64/LLLM --urls http://127.0.0.1:5288
```

The executable is `artifacts/lllm-osx-arm64/LLLM`.

## Run At Login

After publishing, install the macOS LaunchAgent:

```bash
scripts/install-launch-agent.sh
```

Remove it:

```bash
scripts/uninstall-launch-agent.sh
```

Logs are written under `App_Data/logs`.

Check the LaunchAgent status:

```bash
launchctl list | grep com.maxneovici.lllm
```

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

Use the `Mic` button to record. LLLM transcribes short local chunks into the prompt box and auto-sends after a few seconds of silence.
