# ModelPicker

A local gateway for language models. For every request it decides on its own whether to run it on a local model (through Ollama) or send it to the cloud (Claude). Simple tasks stay on your computer and cost nothing; hard ones, or ones that would be too slow locally, go to the cloud.

The gateway exposes an OpenAI-compatible API (`/v1/chat/completions`), so you can connect any tool that supports OpenAI: IDE plugins, SDKs, your own scripts.

## How the decision is made

1. A small classifier model (`qwen3:1.7b`, running on the CPU) rates the task's difficulty (1 to 5) and the expected length of the answer.
2. The gateway estimates how long the answer would take locally. It uses the speed measured on this computer at startup and the answer lengths it keeps learning as it runs.
3. The task goes to the cloud when:
   - the difficulty is at or above the threshold (4 by default),
   - the estimated local time exceeds the limit (30 s by default),
   - the task does not fit in the local model's context.
4. The task stays local when the user has no cloud budget or the cloud is not configured.
5. If the chosen model fails, the other one answers (fallback).

Before a request is sent to the cloud, the older part of a long conversation is summarized locally to use fewer paid tokens.

## Requirements

- Windows 10 or 11, PowerShell 5.1 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com/download)

The install script can install a missing .NET SDK and Ollama through `winget`.

## Installation

```powershell
git clone https://github.com/xGhsty/ModelPicker.git
cd ModelPicker
.\install.ps1
```

The script:

1. checks the requirements and offers to install whatever is missing,
2. adds the `modelpicker` command to your PowerShell profile,
3. scans the hardware and helps you pick a local model that fits in your graphics card's memory (the largest model that fits entirely on the GPU is recommended), then downloads it together with the classifier.

The script can be run as many times as you like. If Windows blocks running scripts, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

After installation, open a new PowerShell window.

## Usage

| Command | Description |
|---|---|
| `modelpicker` | Starts the gateway and a console chat. The cloud is simulated (no cost). |
| `modelpicker -FakeCloud` | "Cloud" requests go through the real API client but are answered by a small model in Ollama. A free test of the whole path. |
| `modelpicker -Real` | The real Claude answers. Requires the `ANTHROPIC_API_KEY` variable. |
| `modelpicker -Key dev-key-intern` | Chat as a user with no cloud budget. |
| `modelpicker -Restart` | Restarts the gateway, e.g. after code changes. |
| `modelpicker -KeepRunning` | Leaves the gateway and models in memory after you exit the chat. |
| `modelpicker-stop` | Stops the gateway and unloads the models from RAM and VRAM. |
| `modelpicker setup` | Rescans the hardware and lets you choose the local model again. |
| `modelpicker setup -List` | Shows which models fit the hardware, without changing the configuration. |

On first launch, `modelpicker` runs the setup by itself if it has not been done yet.

Chat commands:

| Command | Description |
|---|---|
| `/local`, `/cloud`, `/auto` | Forces the local model or the cloud, or restores automatic selection. |
| `/reset` | Starts a new conversation. |
| `/stats` | Measured hardware speed and token usage. |
| `/exit` | Exits. The gateway is stopped and the models are unloaded from memory. |

Set the Anthropic API key once for your account:

```powershell
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', 'sk-ant-...', 'User')
```

## API

The gateway listens on `http://localhost:5080`.

```powershell
$body = @{
    model    = "auto"   # "auto", "local" or "cloud"
    messages = @(@{ role = "user"; content = "What is 2+2?" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Method Post http://localhost:5080/v1/chat/completions `
    -Headers @{ Authorization = "Bearer dev-key-jan" } `
    -ContentType "application/json" -Body $body
```

The response uses the OpenAI format with an extra `routing` field: where the request went, why, the estimates, and compression details.

With `"stream": true` the response arrives as server-sent events. The first event contains the routing decision and is sent immediately, before the answer is generated.

`GET /stats` returns the measured hardware speed, gateway readiness, and token usage.

## Configuration

The main settings are in `src/ModelPicker/appsettings.json`, in the `Gateway` section:

| Setting | Meaning |
|---|---|
| `Ollama.Model` | The local model. Usually set by `modelpicker setup`. |
| `Ollama.ClassifierModel` | The model that rates task difficulty. |
| `Cloud.Model` | The cloud model. |
| `Routing.CloudDifficultyThreshold` | The difficulty from which a task goes to the cloud. |
| `Routing.MaxLocalLatencySeconds` | The maximum acceptable local response time. |
| `Compression.*` | When and how to summarize the history before sending it to the cloud. |
| `ApiKeys` | User keys and their monthly cloud token budgets (0 means no cloud access). |

The `dev-key-jan` and `dev-key-intern` keys are for testing only. Do not put real keys in `appsettings.json`, because that file is in the repository. Put them in `appsettings.Local.json` (the `Gateway.ApiKeys` section), which overrides `appsettings.json` and is not committed. Keep the Anthropic API key in the `ANTHROPIC_API_KEY` environment variable, not in files.

### Local files

These files are created in `src/ModelPicker/` separately on each computer and are not added to the repository:

| File | Contents |
|---|---|
| `appsettings.Local.json` | The local model chosen by `modelpicker setup`. |
| `modelpicker-stats.json` | Local model answer lengths that the gateway learns from to estimate response time. Contains only numbers and hashes of the questions, not their text. |

## Repository structure

| Path | Contents |
|---|---|
| `install.ps1` | The installer. |
| `scripts/` | The launcher (the `modelpicker` command) and the console chat. |
| `src/ModelPicker/` | The gateway: application code and the `appsettings` files. |
| `tests/` | Scripts that test routing and compression against a running gateway. |

## Limitations

- The launcher and installer work only on Windows. The gateway itself is a .NET application and can also run on Linux and macOS (`dotnet run --project src/ModelPicker`).
- GPU detection supports only NVIDIA cards. On other cards the wizard assumes there is no GPU and suggests a small model.
- Token usage is kept in memory, so restarting the gateway resets the monthly budget counters.
- The answer arrives in full once it is ready. For now, streaming only sends the routing decision ahead of time; it does not stream the answer word by word.
