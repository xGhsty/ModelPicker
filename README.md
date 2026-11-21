# ModelPicker

A local gateway for language models. For every request it decides on its own whether to run it on a local model (through Ollama) or send it to the cloud. Simple tasks stay on your computer and cost nothing; hard ones, or ones that would be too slow locally, go to the cloud.

The gateway is not tied to any one cloud API or API key. When a task needs the cloud, the chat tells you so and asks which provider to use: **Claude Code** (`claude`) or **Codex** (`codex`). The chosen CLI runs on your computer with your own login, just like typing `claude` in a terminal: if you are not logged in, its login screen appears first.

The gateway exposes an OpenAI-compatible API (`/v1/chat/completions`), so you can connect any tool that supports OpenAI: IDE plugins, SDKs, your own scripts.

## How the decision is made

1. A small classifier model (`qwen3:1.7b`, running on the CPU) rates the task's difficulty (1 to 5) and the expected length of the answer.
2. The gateway estimates how long the answer would take locally. It uses the speed measured on this computer at startup and the answer lengths it keeps learning as it runs (the first real answer of each length already replaces the default guess).
3. The task goes to the cloud when:
   - the task is about files or a project on your computer ("describe this project", "what does Program.cs do"): the local model cannot read them, the cloud CLI can,
   - the difficulty is at or above the threshold (4 by default),
   - the estimated local time exceeds the limit (30 s by default),
   - the task does not fit in the local model's context.
4. The task stays local when the user has no cloud budget or no cloud provider is configured.
5. If the local model fails, the task is handed to the cloud (fallback). If a cloud provider fails, the chat lets you pick another one or run the task locally.

Before a task is handed to the cloud, the older part of a long conversation is summarized locally to use fewer cloud tokens.

## Requirements

- Windows 10 or 11, PowerShell 5.1 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com/download)
- For cloud tasks, at least one provider CLI: [Claude Code](https://docs.claude.com/en/docs/claude-code) (`npm install -g @anthropic-ai/claude-code`) or [Codex](https://github.com/openai/codex) (`npm install -g @openai/codex`)

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
| `modelpicker` | Starts the gateway in the background (no extra window) and a console chat. |
| `modelpicker logs` | Follows the gateway's log (`%TEMP%\modelpicker-gateway.log`); run it in a second window. |
| `modelpicker help` (or `-h`) | Lists all commands, the chat shortcuts and the main settings with their current defaults. |
| `modelpicker -Key dev-key-intern` | Chat as a user with no cloud budget. |
| `modelpicker -Restart` | Restarts the gateway, e.g. after code changes. |
| `modelpicker -KeepRunning` | Leaves the gateway and models in memory after you exit the chat. |
| `modelpicker-stop` | Stops the gateway and unloads the models from RAM and VRAM. |
| `modelpicker setup` | Rescans the hardware and lets you choose the local model again (arrow keys and Enter, Esc cancels). A running gateway keeps working; it is stopped only when you actually switch to another model. |
| `modelpicker setup -List` | Shows which models fit the hardware, without changing the configuration. |

Every command also has a short form: `mp` instead of `modelpicker` (e.g. `mp setup`, `mp -KeepRunning`) and `mp-stop` instead of `modelpicker-stop`. In PowerShell, `mp` is normally a built-in alias for `Move-ItemProperty`; the launcher replaces it in your sessions (the `Move-ItemProperty` command itself still works).

On first launch, `modelpicker` runs the setup by itself if it has not been done yet.

Chat commands. Type `/` to get the list under the input line: typing filters it, the arrow keys choose, Enter runs, Tab completes, Esc closes.

| Command | Description |
|---|---|
| `/local`, `/cloud`, `/auto` | Forces the local model or the cloud, or restores automatic selection. |
| `/reset` | Starts a new conversation. |
| `/stats` | The local model, its measured speed, your usage this month and the remaining cloud budget. |
| `/help` | Commands and keyboard shortcuts. |
| `/exit` | Exits. The gateway is stopped and the models are unloaded from memory. |

Type `!` at the start of the line to switch to terminal mode, as in Claude Code: `!git status` or `!dotnet test` runs in the project folder, the output is shown and added to the conversation, so the next question can refer to it. Backspace on an empty line goes back to the chat. The up and down arrows recall earlier entries.

The chat runs in the folder where you typed `mp`. The cloud CLI starts there too, so it knows that project (its `CLAUDE.md`, memory and files) and can read the files, without changing anything (Claude Code gets only the `Read`, `Grep` and `Glob` tools, Codex runs in a read-only sandbox).

When a task is priced as cloud work, the chat shows the reason and a choice:

```
+-- Zadanie zostało wycenione na użycie chmury
|   Powód: difficulty 4 >= threshold 4
  (strzałki: wybór, Enter: zatwierdź)
  > Claude Code
    Codex
    Lokalnie mimo to (wolniej, bez kosztów)
+--
```

Move with the arrow keys and confirm with Enter; the cursor starts on the provider chosen last time. The chat checks that the CLI is installed and logged in (`claude auth status`, `codex login status`); if not, it opens the CLI's own login (`claude auth login`, `codex login`) in the same window. The CLI then answers non-interactively with its tools turned off (Claude Code) or in a read-only sandbox (Codex), so nothing on the computer is changed. The tokens it reports are counted against the monthly cloud budget.

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

When the task stays local, the response uses the OpenAI format with an extra `routing` field: where the request went, why, the estimates.

When the task needs the cloud, the gateway does not answer it. It returns a handoff instead (`"object": "modelpicker.cloud_handoff"`), with the allowed `providers`, the conversation to send (`messages`, already compressed) and the `routing` details. The client runs the provider itself and reports what it spent:

```powershell
Invoke-RestMethod -Method Post http://localhost:5080/v1/usage `
    -Headers @{ Authorization = "Bearer dev-key-jan" } -ContentType "application/json" `
    -Body '{ "provider": "claude", "model": "claude-opus-5-5", "input_tokens": 3600, "output_tokens": 120 }'
```

With `"stream": true` the response arrives as server-sent events. The first event contains the routing decision and is sent immediately; a cloud task then ends with the handoff event.

`GET /stats` returns the measured hardware speed, gateway readiness, and token usage.

## Configuration

The main settings are in `src/ModelPicker/appsettings.json`, in the `Gateway` section:

| Setting | Meaning |
|---|---|
| `Ollama.Model` | The local model. Usually set by `modelpicker setup`. |
| `Ollama.ClassifierModel` | The model that rates task difficulty. |
| `Cloud.Providers` | Cloud providers the chat may offer (`claude`, `codex`). An empty list keeps every task local. |
| `Routing.CloudDifficultyThreshold` | The difficulty from which a task goes to the cloud. |
| `Routing.MaxLocalLatencySeconds` | The maximum acceptable local response time. |
| `Compression.*` | When and how to summarize the history before sending it to the cloud. |
| `ApiKeys` | User keys and their monthly cloud token budgets (0 means no cloud access). |

The `dev-key-jan` and `dev-key-intern` keys are for testing only. Do not put real keys in `appsettings.json`, because that file is in the repository. Put them in `appsettings.Local.json` (the `Gateway.ApiKeys` section), which overrides `appsettings.json` and is not committed. The gateway needs no cloud API key: each user logs in to their own provider CLI.

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
- Cloud usage is reported by the client, so the budget relies on the client being honest. Claude Code counts its own system prompt as input (about 3,600 tokens per answer).
- Clients that only speak the OpenAI API (IDE plugins, SDKs) get local answers, but cannot follow a cloud handoff on their own.
- The answer arrives in full once it is ready. For now, streaming only sends the routing decision ahead of time; it does not stream the answer word by word.
