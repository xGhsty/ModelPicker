# Launcher for the ModelPicker test environment. Loaded from the PowerShell profile,
# so in any new PowerShell window you can just type:
#   modelpicker                  chat as jan.kowalski; tasks priced as cloud work ask which provider to use
#                                (Claude Code or Codex CLI, with your own login - not logged in = login screen)
#   modelpicker -Key dev-key-intern   user without cloud budget (always local)
#   modelpicker -Restart         restart the gateway (e.g. after code changes)
#   modelpicker -KeepRunning     on exit, leave the gateway and models loaded
#   modelpicker-stop             stop the gateway and unload its models from RAM/VRAM
#   modelpicker logs             follow the gateway's log (it runs in the background, without a window)
#   modelpicker setup            scan the hardware and choose the local model (runs automatically on first start)
#   modelpicker setup -List      only show which models fit this hardware
#   modelpicker help (-h)        list all commands and the main settings
#   mp, mp-stop                  short forms of modelpicker and modelpicker-stop (same parameters)
#
# Leaving the chat (/exit or Ctrl+C) runs modelpicker-stop automatically.
# Closing the window with X cannot be intercepted - then run modelpicker-stop in a new window,
# or Ollama frees the models by itself after Gateway:Ollama:KeepAlive (appsettings.json).

# The .NET project (and its appsettings*.json) lives in src\ModelPicker under the repository root.
$script:ModelPickerProject = Join-Path (Split-Path $PSScriptRoot -Parent) "src\ModelPicker"
$script:ModelPickerUrl = "http://localhost:5080"
# 127.0.0.1, not localhost: on Windows "localhost" tries IPv6 (::1) first, and Ollama listens only on IPv4,
# so every new connection would wait ~2 s for the fallback.
$script:OllamaUrl = "http://127.0.0.1:11434"
# The gateway runs hidden; its console output goes here instead of a second window.
$script:GatewayLog = Join-Path $env:TEMP "modelpicker-gateway.log"

function Test-ModelPickerUp {
    try { Invoke-RestMethod "$script:ModelPickerUrl/stats" -TimeoutSec 2 | Out-Null; $true } catch { $false }
}

function Write-Progress-Dots([string]$Text, [int]$Tick, [Diagnostics.Stopwatch]$Stopwatch) {
    # Redraws one line in place: 1-6 dots cycling, plus elapsed time.
    $dots = "." * (($Tick % 6) + 1)
    Write-Host ("`r{0} {1,-6} {2}s " -f $Text, $dots, [int]$Stopwatch.Elapsed.TotalSeconds) -NoNewline -ForegroundColor Cyan
}

function Complete-Progress-Dots([string]$Text, [Diagnostics.Stopwatch]$Stopwatch, [string]$Extra = "") {
    # Replaces the dots with a green check mark (U+2713, a plain text glyph, not an emoji) and ends the line.
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $check = [char]0x2713
    $suffix = if ($Extra) { " ($Extra)" } else { "" }
    Write-Host ("`r{0} {1} {2}s{3}      " -f $Text, $check, [int]$Stopwatch.Elapsed.TotalSeconds, $suffix) -ForegroundColor Green
}

function Get-ModelPickerModels {
    # Models the gateway uses, read from appsettings.json and appsettings.Local.json.
    $models = @()
    try {
        $cfg = Get-Content (Join-Path $script:ModelPickerProject "appsettings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        $models += $cfg.Gateway.Ollama.Model, $cfg.Gateway.Ollama.ClassifierModel
    } catch { }
    try {
        $local = Get-Content (Join-Path $script:ModelPickerProject "appsettings.Local.json") -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $models += $local.Gateway.Ollama.Model
    } catch { }
    $models | Where-Object { $_ } | Select-Object -Unique
}

function Stop-ModelPickerGateway {
    # Kills the process listening on 5080 plus the 'dotnet run' that launched it.
    $conn = Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $conn) { return $false }
    $procId = $conn.OwningProcess
    for ($i = 0; $i -lt 3 -and $procId; $i++) {
        $p = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
        if (-not $p) { break }
        $isOurs = $i -eq 0 -or $p.Name -eq "dotnet.exe" -or ($p.CommandLine -like "*ModelPicker gateway*")
        if (-not $isOurs) { break }
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        $procId = $p.ParentProcessId
    }
    # A gateway window from older launcher versions (recognised by its title in the command line).
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*ModelPicker gateway*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $true
}

function modelpicker-stop {
    # 1. Gateway
    $stopped = Stop-ModelPickerGateway
    if ($stopped) { Write-Host "Gateway zatrzymany." -ForegroundColor Cyan }

    # 2. Unload only ModelPicker's models (keep_alive = 0); other Ollama models stay untouched.
    # Repeated a few times: a model loaded with different options (e.g. classifier on CPU) may need a second pass.
    $ours = @(Get-ModelPickerModels)
    $unloaded = @()
    for ($pass = 0; $pass -lt 3; $pass++) {
        try { $loaded = @((Invoke-RestMethod "$script:OllamaUrl/api/ps" -TimeoutSec 3).models.name) } catch { $loaded = @() }
        $toUnload = @($loaded | Where-Object { $ours -contains $_ })
        if ($toUnload.Count -eq 0) { break }
        foreach ($m in $toUnload) {
            try {
                $body = @{ model = $m; keep_alive = 0 } | ConvertTo-Json
                Invoke-RestMethod -Method Post "$script:OllamaUrl/api/generate" -ContentType "application/json" -Body $body -TimeoutSec 30 | Out-Null
                if ($unloaded -notcontains $m) { Write-Host "Zwolniono z pamięci: $m" -ForegroundColor Cyan; $unloaded += $m }
            } catch {
                Write-Host "Nie udało się zwolnić $m : $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        Start-Sleep -Seconds 1
    }
    if (-not $stopped -and $unloaded.Count -eq 0) {
        Write-Host "Nic do zwolnienia - gateway nie działał, modele nie były załadowane." -ForegroundColor DarkGray
    }
}

function Show-ModelPickerLog {
    if (-not (Test-Path $script:GatewayLog)) { Write-Host "Brak logu - gateway nie był jeszcze uruchamiany ($script:GatewayLog)." -ForegroundColor DarkGray; return }
    Write-Host "Log gatewaya ($script:GatewayLog), Ctrl+C kończy podgląd:" -ForegroundColor Cyan
    Get-Content $script:GatewayLog -Encoding UTF8 -Tail 40 -Wait
}

function Show-ModelPickerHelp {
    $c = "Cyan"; $g = "DarkGray"
    Write-Host ""
    Write-Host "ModelPicker - komendy w terminalu (mp = modelpicker)" -ForegroundColor $c
    @(
        @("mp", "start: gateway w tle + czat w bieżącym folderze (projekcie)"),
        @("mp -Restart", "restart gatewaya, np. po zmianie kodu albo ustawień"),
        @("mp -KeepRunning", "po wyjściu z czatu modele zostają w pamięci (szybszy następny start)"),
        @("mp -Key <klucz>", "czat jako inny użytkownik (klucze w appsettings.json)"),
        @("mp setup", "skan sprzętu i wybór modelu lokalnego"),
        @("mp setup -List", "tylko pokaż, które modele pasują do tego sprzętu"),
        @("mp logs", "podgląd logu gatewaya na żywo"),
        @("mp-stop", "zatrzymaj gateway i zwolnij modele z RAM/VRAM"),
        @("mp help", "ta lista")
    ) | ForEach-Object { Write-Host ("  {0,-18}" -f $_[0]) -NoNewline -ForegroundColor White; Write-Host $_[1] -ForegroundColor $g }

    Write-Host ""
    Write-Host "W czacie" -ForegroundColor $c
    Write-Host "  /           lista komend czatu (/stats, /reset, /local, /cloud, /auto, /help, /exit)" -ForegroundColor $g
    Write-Host "  !komenda    tryb terminala w folderze projektu, wynik trafia do rozmowy" -ForegroundColor $g

    Write-Host ""
    Write-Host "Ustawienia (po zmianie: mp -Restart)" -ForegroundColor $c
    Write-Host "  $(Join-Path $script:ModelPickerProject 'appsettings.json'), sekcja Gateway:" -ForegroundColor $g
    @(
        @("Routing:MaxLocalLatencySeconds", "ile sekund wolno czekać na model lokalny, zanim zadanie pójdzie do chmury (30)"),
        @("Routing:CloudDifficultyThreshold", "od jakiej trudności (1-5) zadanie idzie do chmury (4)"),
        @("Ollama:ContextWindow", "kontekst modelu lokalnego w tokenach (8192)"),
        @("Ollama:KeepAlive", "jak długo Ollama trzyma model w pamięci (30m)"),
        @("Ollama:Think", "tryb myślenia modelu lokalnego: dokładniej, ale wolniej (false)"),
        @("Cloud:Providers", "providerzy chmury do wyboru (claude, codex)"),
        @("Compression:Enabled", "streszczanie długiej rozmowy przed wysłaniem do chmury (true)")
    ) | ForEach-Object { Write-Host ("  {0,-34}" -f $_[0]) -NoNewline -ForegroundColor White; Write-Host $_[1] -ForegroundColor $g }
    Write-Host "  Model lokalny wybrany przez mp setup jest w appsettings.Local.json." -ForegroundColor $g
    Write-Host ""
}

function Test-OllamaUp {
    try { Invoke-RestMethod "$script:OllamaUrl/api/version" -TimeoutSec 5 | Out-Null; $true } catch { $false }
}

function Get-ModelPickerSetupProblem {
    # Returns why ModelPicker is not ready on this machine, or $null when it is.
    $localFile = Join-Path $script:ModelPickerProject "appsettings.Local.json"
    if (-not (Test-Path $localFile)) { return "Pierwsze uruchomienie na tym komputerze" }

    try {
        $cfg = Get-Content (Join-Path $script:ModelPickerProject "appsettings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        $local = Get-Content $localFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $installed = @((Invoke-RestMethod "$script:OllamaUrl/api/tags" -TimeoutSec 5).models.name)
    } catch { return $null }  # cannot check - let the gateway report the real error

    $model = if ($local.Gateway.Ollama.Model) { $local.Gateway.Ollama.Model } else { $cfg.Gateway.Ollama.Model }
    if ($installed -notcontains $model) { return "Model lokalny $model nie jest zainstalowany w Ollamie" }
    $classifier = $cfg.Gateway.Ollama.ClassifierModel
    if ($classifier -and $installed -notcontains $classifier) { return "Brakuje modelu klasyfikatora $classifier" }
    $null
}

function Get-ModelPickerLocalModel {
    try { (Get-Content (Join-Path $script:ModelPickerProject "appsettings.Local.json") -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json).Gateway.Ollama.Model } catch { $null }
}

function Invoke-ModelPickerSetup([switch]$List) {
    # A running gateway is left alone: a chat in another window keeps working if the setup is cancelled.
    # It is restarted only when a new model was actually chosen (it reads the model at startup).
    $modelBefore = Get-ModelPickerLocalModel
    # Build first with an animation (dotnet run would otherwise sit silent for a few seconds).
    $log = Join-Path $env:TEMP "modelpicker-build.log"
    # -tl:off: the MSBuild terminal logger draws live progress straight to the console, bypassing the redirect.
    $proc = Start-Process dotnet -ArgumentList "build", "-nologo", "-v", "q", "-tl:off" -WorkingDirectory $script:ModelPickerProject `
        -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $proc.Handle  # keeps ExitCode available after the process ends
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $tick = 0
    while (-not $proc.HasExited) {
        Write-Progress-Dots "Przygotowuję ModelPicker" ($tick++) $sw
        Start-Sleep -Milliseconds 200
    }
    $dll = Join-Path $script:ModelPickerProject "bin\Debug\net10.0\ModelPicker.dll"
    $buildLog = (Get-Content $log, "$log.err" -ErrorAction SilentlyContinue) -join "`n"
    if ($proc.ExitCode -ne 0 -and $buildLog -match "MSB3027|MSB3021" -and (Test-Path $dll)) {
        # Only copying failed: another ModelPicker (e.g. setup in a second window) holds the file.
        Write-Host ""
        Write-Host "ModelPicker działa w innym oknie (plik zajęty) - używam poprzedniej kompilacji." -ForegroundColor DarkYellow
    } elseif ($proc.ExitCode -ne 0) {
        Write-Host ""
        Write-Host "Kompilacja nie powiodła się:" -ForegroundColor Red
        Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 20
        return
    } else {
        Complete-Progress-Dots "Przygotowuję ModelPicker" $sw
    }

    # Run the built app directly: even with --no-build, 'dotnet run' evaluates the project and prints a progress line.
    $setupArgs = @($dll, "setup")
    if ($List) { $setupArgs += "--list" }
    Push-Location $script:ModelPickerProject
    try { & dotnet @setupArgs } finally { Pop-Location }

    $modelAfter = Get-ModelPickerLocalModel
    if (-not $List -and $modelAfter -and $modelAfter -ne $modelBefore -and (Test-ModelPickerUp)) {
        Stop-ModelPickerGateway | Out-Null
        Write-Host "Gateway działał na poprzednim modelu ($modelBefore) - zatrzymany. Uruchom mp, żeby wczytał $modelAfter." -ForegroundColor Yellow
    }
}

function modelpicker {
    param(
        [string]$Command = "",
        [switch]$List,
        [string]$Key = "dev-key-jan",
        [switch]$Restart,
        [switch]$KeepRunning,
        [Alias("h")][switch]$Help
    )

    if ($Help -or $Command -in "help", "/?", "?", "--help") { Show-ModelPickerHelp; return }
    if ($Command -eq "setup") { Invoke-ModelPickerSetup -List:$List; return }
    if ($Command -eq "logs") { Show-ModelPickerLog; return }
    if ($Command) { Write-Host "Nieznana komenda: $Command. Wszystkie komendy: mp help" -ForegroundColor Red; return }

    # 0. Prerequisites
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "Brak .NET SDK - ModelPicker go potrzebuje. Zainstaluj: winget install Microsoft.DotNet.SDK.10" -ForegroundColor Red
        Write-Host "(albo https://dotnet.microsoft.com/download), potem otwórz nowe okno." -ForegroundColor DarkGray
        return
    }
    if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
        Write-Host "Brak Ollamy - ModelPicker uruchamia przez nią modele lokalne. Zainstaluj: winget install Ollama.Ollama" -ForegroundColor Red
        Write-Host "(albo https://ollama.com/download), potem otwórz nowe okno." -ForegroundColor DarkGray
        return
    }

    # 1. Ollama
    if (-not (Test-OllamaUp)) {
        Start-Process ollama -ArgumentList "serve" -WindowStyle Hidden
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $tick = 0
        while (-not (Test-OllamaUp)) {
            if ($sw.Elapsed.TotalSeconds -gt 30) { Write-Host ""; Write-Host "Ollama nie wystartowała w 30 s." -ForegroundColor Red; return }
            Write-Progress-Dots "Uruchamiam Ollamę" ($tick++) $sw
            Start-Sleep -Milliseconds 500
        }
        Complete-Progress-Dots "Uruchamiam Ollamę" $sw
    }

    # 2. Is ModelPicker set up on this machine? If not, run the setup automatically.
    $problem = Get-ModelPickerSetupProblem
    if ($problem) {
        Write-Host "$problem - uruchamiam przygotowanie ModelPickera." -ForegroundColor Cyan
        Write-Host "(Możesz je powtórzyć w każdej chwili komendą: modelpicker setup)" -ForegroundColor DarkGray
        Invoke-ModelPickerSetup
        $problem = Get-ModelPickerSetupProblem
        if ($problem) {
            Write-Host "$problem - ModelPicker nie może wystartować. Uruchom ponownie: modelpicker setup" -ForegroundColor Red
            return
        }
    }

    # 3. Gateway
    if ($Restart) { if (Stop-ModelPickerGateway) { Start-Sleep -Seconds 1 } }

    if (-not (Test-ModelPickerUp)) {
        # In the background, without a window; the log is in a file (modelpicker logs).
        $gateway = Start-Process dotnet -ArgumentList "run", "--no-launch-profile" -WorkingDirectory $script:ModelPickerProject `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $script:GatewayLog -RedirectStandardError "$script:GatewayLog.err"

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $tick = 0
        while (-not (Test-ModelPickerUp)) {
            if ($gateway.HasExited -or $sw.Elapsed.TotalSeconds -gt 120) {
                Write-Host ""
                $why = if ($gateway.HasExited) { "zakończył się przy starcie" } else { "nie wystartował w 2 min" }
                Write-Host "Gateway $why. Ostatnie linie logu ($script:GatewayLog):" -ForegroundColor Red
                Get-Content $script:GatewayLog, "$script:GatewayLog.err" -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 20
                return
            }
            Write-Progress-Dots "Czekam na start gatewaya" ($tick++) $sw
            Start-Sleep -Seconds 1
        }
        Complete-Progress-Dots "Czekam na start gatewaya" $sw
    }

    # Wait for the gateway's warm-up (models loaded into memory, speed measured).
    $stats = Invoke-RestMethod "$script:ModelPickerUrl/stats" -TimeoutSec 5
    if ($null -ne $stats.ready -and -not $stats.ready) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $tick = 0
        while (-not $stats.ready -and $sw.Elapsed.TotalSeconds -lt 300) {
            Write-Progress-Dots "Rozgrzewam modele (ładowanie do pamięci + pomiar prędkości)" ($tick++) $sw
            Start-Sleep -Seconds 1
            try { $stats = Invoke-RestMethod "$script:ModelPickerUrl/stats" -TimeoutSec 5 } catch { }
        }
        if ($stats.ready) {
            Complete-Progress-Dots "Rozgrzewam modele (ładowanie do pamięci + pomiar prędkości)" $sw ("prędkość: {0} tok/s" -f $stats.hardware.generation_tokens_per_second)
        } else {
            Write-Host ""
            Write-Host "Rozgrzewka trwa ponad 5 min - otwieram czat, pierwsze pytanie może być wolne." -ForegroundColor Yellow
        }
    }

    Write-Host ""
    Write-Host "Zadania wycenione na chmurę zapytają o providera: Claude Code (claude) albo Codex (codex)." -ForegroundColor Yellow
    Write-Host "Używany jest Twój login w danym CLI; jeśli go nie ma, pojawi się ekran logowania." -ForegroundColor DarkGray
    Write-Host "  Wszystkie komendy i ustawienia: mp help (w drugim oknie), w czacie: /help" -ForegroundColor DarkGray
    Write-Host ""

    # 4. Chat; cleanup runs on /exit and on Ctrl+C.
    try {
        # The folder "mp" was typed in: the cloud CLI runs there, so it knows that project.
        $projectDir = (Get-Location -PSProvider FileSystem).ProviderPath
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "chat.ps1") -Key $Key -Url $script:ModelPickerUrl -ProjectDir $projectDir
    }
    finally {
        if ($KeepRunning) {
            Write-Host "Gateway i modele zostają w pamięci (-KeepRunning). Zwolnisz je: modelpicker-stop" -ForegroundColor DarkGray
        } else {
            Write-Host "`nSprzątam..." -ForegroundColor Cyan
            modelpicker-stop
        }
    }
}

# Short forms: "mp" = modelpicker, "mp-stop" = modelpicker-stop.
# PowerShell already has "mp" as a built-in alias for Move-ItemProperty, and an alias wins over a function,
# so that alias is removed first (only in this session; Move-ItemProperty itself keeps working).
if (Test-Path Alias:mp) { Remove-Item Alias:mp -Force }
Set-Alias -Name mp -Value modelpicker -Scope Global
Set-Alias -Name mp-stop -Value modelpicker-stop -Scope Global
