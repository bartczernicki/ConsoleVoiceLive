# ConsoleVoiceLive

Azure Speech and Voice Live samples in two .NET 10 projects:

- `ConsoleVoiceLive`: console menu for Text-To-Speech and VoiceLiveAssistant.
- `ConsoleLiveWeb`: Blazor Web App for browser-based Text-To-Speech, Voice Live, text-to-avatar, and voice-live-avatar experiments.

## Prerequisites

- .NET 10 SDK.
- An Azure AI Speech or Foundry Speech-capable resource.
- A Speech endpoint like `https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com`.
- A Speech resource key.
- An Azure OpenAI deployment for Agent Text to Speech.
- macOS voice capture/playback for the console VoiceLiveAssistant uses SoX:

```zsh
brew install sox
```

The Blazor app keeps Speech and WebIQ keys server-side. Do not put keys in JavaScript, Razor markup, or committed source.

## Local Secrets

Both projects load `secrets.appsettings.json` with `optional: false`, so the file must exist before running. These files are ignored by `.gitignore` and should stay local.

Use placeholders in source examples and put real values only in your local `secrets.appsettings.json` files.

### Console App Secrets

Create `ConsoleVoiceLive/secrets.appsettings.json`:

```json
{
  "Speech": {
    "Endpoint": "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com",
    "Key": "YOUR_SPEECH_KEY"
  }
}
```

The console app reads non-secret defaults from `ConsoleVoiceLive/appsettings.json`:

```json
{
  "Speech": {
    "VoiceName": "en-US-Ava:DragonHDLatestNeural",
    "Model": "gpt-realtime-1.5",
    "Instructions": "You are a helpful AI assistant. Respond naturally and conversationally. Keep your responses concise but engaging."
  }
}
```

### Blazor App Secrets

Create `ConsoleLiveWeb/secrets.appsettings.json`:

```json
{
  "Speech": {
    "Endpoint": "https://YOUR_RESOURCE_NAME.cognitiveservices.azure.com",
    "Key": "YOUR_SPEECH_KEY"
  },
  "WebIQ-MCP": {
    "headers": {
      "x-apikey": "YOUR_WEBIQ_MCP_API_KEY"
    }
  },
  "AzureOpenAI": {
    "Endpoint": "https://YOUR_AZURE_OPENAI_RESOURCE.openai.azure.com",
    "APIKey": "YOUR_AZURE_OPENAI_KEY",
    "ModelDeploymentName": "YOUR_DEPLOYMENT_NAME"
  }
}
```

`WebIQ-MCP` is only needed for WebIQ grounded lookup features. Speech-only pages need only `Speech:Endpoint` and `Speech:Key`.
`AzureOpenAI` is only needed for the Agent Text to Speech page.

The Blazor app reads non-secret defaults from `ConsoleLiveWeb/appsettings.json`, including voice, model, instructions, TTS prosody defaults, and avatar defaults.

## Build

From the repo root:

```zsh
dotnet restore ConsoleVoiceLive/ConsoleVoiceLive.csproj
dotnet restore ConsoleLiveWeb/ConsoleLiveWeb.csproj

dotnet build ConsoleVoiceLive/ConsoleVoiceLive.csproj
dotnet build ConsoleLiveWeb/ConsoleLiveWeb.csproj
```

## Run The Console App

```zsh
dotnet run --project ConsoleVoiceLive/ConsoleVoiceLive.csproj
```

Menu options:

- `1. Text-To-Speech`: type text and play synthesized speech through the default speaker.
- `2. VoiceLiveAssistant`: speak through the microphone and hear the real-time assistant response.

For option `2`, install SoX and allow microphone access if macOS prompts. Press `Ctrl+C` to stop the session.

## Run The Blazor App

```zsh
dotnet run --project ConsoleLiveWeb/ConsoleLiveWeb.csproj
```

Open the local URL printed by `dotnet run`.

Useful routes:

- `/text-to-speech`
- `/voice-live`
- `/text-to-videoavatar`
- `/voice-live-avatar`
- `/agent-text-to-speech`

## Troubleshooting

If a port is already in use, find and stop the process:

```zsh
lsof -nP -iTCP:5202 -sTCP:LISTEN
kill <PID>
```

If the process is suspended and does not exit:

```zsh
kill -9 <PID>
```

If Speech calls fail, confirm:

- `secrets.appsettings.json` exists in the project you are running.
- `Speech:Endpoint` is the full `https://...cognitiveservices.azure.com` endpoint.
- `Speech:Key` is a valid key for that resource.
- The selected voice/model/avatar feature is supported by the resource region and SKU.

If WebIQ lookup fails, confirm:

- `WebIQ-MCP:headers:x-apikey` is set in `ConsoleLiveWeb/secrets.appsettings.json`.
- The WebIQ key is not the placeholder value.
- The key is never committed or rendered in the browser.

If Agent Text to Speech fails, confirm:

- `AzureOpenAI:Endpoint`, `AzureOpenAI:APIKey`, and `AzureOpenAI:ModelDeploymentName` are set in `ConsoleLiveWeb/secrets.appsettings.json`.
- The endpoint is the Azure OpenAI resource endpoint, not the Speech endpoint.
- The deployment name points to a model that supports the Azure OpenAI Responses API.
