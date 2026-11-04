# ModelPicker installer for Windows.
#   .\install.ps1              check requirements, register the "modelpicker" command, choose a local model
#   .\install.ps1 -SkipSetup   only the first two steps
#
# Safe to run again: it never adds the profile line twice and only offers what is missing.
param([switch]$SkipSetup)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot
$launcher = Join-Path $root "scripts\modelpicker.ps1"
$check = [char]0x2713

function Write-Ok([string]$Text) { Write-Host "  $check $Text" -ForegroundColor Green }
function Write-Missing([string]$Text) { Write-Host "  x $Text" -ForegroundColor Yellow }

function Confirm-Step([string]$Question) {
    $answer = Read-Host "    $Question [T/n]"
    return -not ($answer -match '^(n|nie|no)$')
}

function Update-SessionPath {
    # winget changes PATH for new windows only; reload it so this session sees the new tools.
    $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
}

function Install-WithWinget([string]$Id, [string]$Name, [string]$Url) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host "    Brak winget. Zainstaluj $Name ręcznie: $Url" -ForegroundColor Red
        return $false
    }
    if (-not (Confirm-Step "Zainstalować $Name przez winget?")) {
        Write-Host "    Pominięto. Instalacja ręczna: $Url" -ForegroundColor DarkGray
        return $false
    }
    winget install --id $Id --exact --accept-source-agreements --accept-package-agreements
    Update-SessionPath
    return $true
}

Write-Host ""
Write-Host "Instalacja ModelPicker" -ForegroundColor Cyan
Write-Host ""

# 1. Requirements
Write-Host "Wymagania:"
$ok = $true

$sdk = $null
if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdk = (dotnet --list-sdks) | Where-Object { $_ -match '^10\.' } | Select-Object -First 1 }
if ($sdk) {
    Write-Ok ".NET SDK $($sdk.Split(' ')[0])"
} else {
    Write-Missing ".NET 10 SDK nie jest zainstalowany"
    if (Install-WithWinget "Microsoft.DotNet.SDK.10" ".NET 10 SDK" "https://dotnet.microsoft.com/download") { Write-Ok ".NET 10 SDK zainstalowany" } else { $ok = $false }
}

if (Get-Command ollama -ErrorAction SilentlyContinue) {
    # 'ollama --version' also warns on stderr when the server is not running; in PowerShell 5.1 that would
    # stop the script under ErrorActionPreference=Stop, so it is read with errors allowed.
    $versionText = & { $ErrorActionPreference = "Continue"; ollama --version 2>&1 } | Out-String
    $version = if ($versionText -match '(\d+\.\d+\.\d+)') { $Matches[1] } else { "" }
    Write-Ok ("Ollama $version".TrimEnd())
} else {
    Write-Missing "Ollama nie jest zainstalowana"
    if (Install-WithWinget "Ollama.Ollama" "Ollama" "https://ollama.com/download") { Write-Ok "Ollama zainstalowana" } else { $ok = $false }
}

if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    Write-Ok "Karta NVIDIA: $((nvidia-smi --query-gpu=name --format=csv,noheader) | Select-Object -First 1)"
} else {
    Write-Host "  - Nie wykryto karty NVIDIA. ModelPicker zadziała, ale modele lokalne będą liczone na procesorze (wolno)." -ForegroundColor DarkGray
}

if (-not $ok) {
    Write-Host ""
    Write-Host "Brakuje wymaganych programów. Zainstaluj je, otwórz nowe okno PowerShella i uruchom .\install.ps1 ponownie." -ForegroundColor Red
    exit 1
}

# 2. Execution policy: the profile (and with it the "modelpicker" command) loads only when scripts are allowed.
Write-Host ""
Write-Host "Komenda modelpicker:"
# The policy new windows will get: the Process scope is skipped, because "-ExecutionPolicy Bypass"
# used to start this installer applies to this run only. Nothing set at all means Restricted on Windows.
$policy = (Get-ExecutionPolicy -List | Where-Object { $_.Scope -ne "Process" -and $_.ExecutionPolicy -ne "Undefined" } |
    Select-Object -First 1).ExecutionPolicy
if (-not $policy) { $policy = "Restricted" }
if ("$policy" -in "Restricted", "AllSigned") {
    Write-Missing "Uruchamianie skryptów jest zablokowane (ExecutionPolicy: $policy)"
    if (Confirm-Step "Zezwolić na lokalne skrypty dla Twojego konta (Set-ExecutionPolicy RemoteSigned -Scope CurrentUser)?") {
        Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
        Write-Ok "ExecutionPolicy: RemoteSigned (tylko Twoje konto)"
    } else {
        Write-Host "    Bez tego komenda modelpicker nie załaduje się w nowych oknach." -ForegroundColor Red
        exit 1
    }
}

# 3. Register the command in the PowerShell profile (replaces a line pointing to an older copy of the launcher).
# Guarded, so a moved or deleted repository does not print an error in every new PowerShell window.
$line = "if (Test-Path '$launcher') { . '$launcher' }"
$profileDir = Split-Path $PROFILE
if (-not (Test-Path $profileDir)) { New-Item -ItemType Directory -Path $profileDir -Force | Out-Null }
$existing = if (Test-Path $PROFILE) { @(Get-Content $PROFILE -Encoding UTF8) } else { @() }
if ($existing -contains $line) {
    Write-Ok "Już dodana do profilu ($PROFILE)"
} else {
    $kept = @($existing | Where-Object { $_ -notmatch 'modelpicker\.ps1' })
    $kept += $line
    Set-Content -Path $PROFILE -Value $kept -Encoding UTF8
    Write-Ok "Dodana do profilu ($PROFILE)"
}
. $launcher

# 4. Hardware scan and local model
if ($SkipSetup) {
    Write-Host ""
    Write-Host "Gotowe. Wybór modelu lokalnego: modelpicker setup" -ForegroundColor Cyan
    exit 0
}
Write-Host ""
Write-Host "Dobór modelu lokalnego do sprzętu:" -ForegroundColor Cyan
if (-not (Test-OllamaUp)) {
    Start-Process ollama -ArgumentList "serve" -WindowStyle Hidden
    for ($i = 0; $i -lt 30 -and -not (Test-OllamaUp); $i++) { Start-Sleep -Seconds 1 }
}
modelpicker setup

Write-Host ""
Write-Host "Gotowe. Otwórz nowe okno PowerShella i wpisz: modelpicker" -ForegroundColor Cyan
