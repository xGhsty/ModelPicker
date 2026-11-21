# Interactive chat through the ModelPicker gateway. Keeps the conversation history,
# so you can see routing change between messages and context compression kick in.
#   .\scripts\chat.ps1                      (uses dev-key-jan)
#   .\scripts\chat.ps1 -Key dev-key-intern  (user without cloud budget)
#
# The gateway never calls a cloud API itself. When a task needs the cloud, it hands the (compressed)
# conversation back here; you pick a provider (Claude Code, Codex) and its CLI runs on this computer
# with your own login, exactly like typing "claude" or "codex" in a terminal. Not logged in = login screen.
param(
    [string]$Key = "dev-key-jan",
    [string]$Url = "http://localhost:5080"
)

[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$utf8 = New-Object Text.UTF8Encoding($false)
$history = New-Object System.Collections.ArrayList
$mode = "auto"
$lastProvider = $null

# Cloud providers this chat knows how to run. The gateway decides which of them are allowed (Gateway:Cloud:Providers).
$cloudProviders = [ordered]@{
    claude = @{ Name = "Claude Code"; Command = "claude"; Install = "npm install -g @anthropic-ai/claude-code" }
    codex  = @{ Name = "Codex";       Command = "codex";  Install = "npm install -g @openai/codex" }
}

Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(15)

# Rotating circle in Windows Terminal; | / - \ in the old console, whose font may lack the circle glyphs.
$spinner = if ($env:WT_SESSION) { @([char]0x25D0, [char]0x25D3, [char]0x25D1, [char]0x25D2) } else { @('|', '/', '-', '\') }

function Show-Waiting([string]$Label, [Diagnostics.Stopwatch]$Stopwatch, [int]$Tick) {
    Write-Host ("`r  {0} {1}... {2}s   " -f $spinner[$Tick % $spinner.Count], $Label, [int]$Stopwatch.Elapsed.TotalSeconds) -NoNewline -ForegroundColor DarkGray
}

function Clear-Waiting {
    $width = try { [Console]::WindowWidth - 1 } catch { 79 }
    Write-Host ("`r" + (" " * $width) + "`r") -NoNewline
}

function Get-ErrorText($err) {
    if ($err.Exception.InnerException) { $err.Exception.InnerException.Message } else { $err.Exception.Message }
}

# Sends the conversation to the gateway. Returns the local answer, a cloud handoff, or an error.
function Send-Chat([string]$RouteMode, [Diagnostics.Stopwatch]$Stopwatch) {
    $out = @{ Answer = $null; Final = $null; Handoff = $null; Failure = $null; Usage = $null }
    $body = @{ model = $RouteMode; messages = $history; stream = $true } | ConvertTo-Json -Depth 4
    $waitLabel = "analizuję zadanie"
    try {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Url/v1/chat/completions")
        $req.Headers.Add("Authorization", "Bearer $Key")
        $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json")
        $send = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        $tick = 0
        while (-not $send.Wait(100)) { Show-Waiting $waitLabel $Stopwatch ($tick++) }
        $resp = $send.Result
        if (-not $resp.IsSuccessStatusCode) { throw "HTTP $([int]$resp.StatusCode): $($resp.Content.ReadAsStringAsync().Result)" }
        $reader = New-Object IO.StreamReader($resp.Content.ReadAsStreamAsync().Result, [Text.Encoding]::UTF8)

        while ($true) {
            # Waiting in 100 ms steps keeps the spinner moving and Ctrl+C responsive.
            $read = $reader.ReadLineAsync()
            while (-not $read.Wait(100)) { Show-Waiting $waitLabel $Stopwatch ($tick++) }
            $line = $read.Result
            if ($null -eq $line -or $line -eq "data: [DONE]") { break }
            if (-not $line.StartsWith("data: ")) { continue }
            $chunk = $line.Substring(6) | ConvertFrom-Json

            if ($chunk.error) { $out.Failure = $chunk.error.message; break }
            if ($chunk.object -eq "modelpicker.cloud_handoff") { $out.Handoff = $chunk; continue }
            if ($chunk.usage) { $out.Usage = $chunk.usage }  # the closing chunk
            if ($chunk.routing.phase -eq "decided") {
                # The decision, shown as soon as it is made.
                $rt = $chunk.routing
                Clear-Waiting
                $color = if ($rt.target -eq "local") { "Green" } else { "Magenta" }
                $where = if ($rt.target -eq "local") { "local | $($chunk.model)" } else { "cloud" }
                Write-Host ("[{0}] {1} | trudność {2}, szac. lokalnie {3}s (decyzja po {4:F1}s)" -f `
                    $where, $rt.reason, $rt.analysis.difficulty, $rt.analysis.estimated_local_seconds, $Stopwatch.Elapsed.TotalSeconds) -ForegroundColor $color
                $waitLabel = if ($rt.target -eq "local") { "lokalny model pisze odpowiedź" } else { "przygotowuję kontekst dla chmury" }
            }
            elseif ($chunk.choices -and $chunk.choices[0].delta.content) {
                $out.Answer = $chunk.choices[0].delta.content
                $out.Final = $chunk
            }
        }
    }
    catch { $out.Failure = Get-ErrorText $_ }
    Clear-Waiting
    $out
}

function Get-CliPath([string]$Name) {
    # The npm shim (claude.cmd) or a native .exe; the extensionless shell script next to it cannot be started on Windows.
    Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in ".exe", ".cmd", ".bat" } |
        Select-Object -First 1 -ExpandProperty Source
}

function Test-ProviderLogin([string]$Id, [string]$Exe) {
    switch ($Id) {
        "claude" { try { [bool]((& $Exe auth status --json 2>$null | Out-String) | ConvertFrom-Json).loggedIn } catch { $false } }
        "codex"  { & $Exe login status *> $null; $LASTEXITCODE -eq 0 }
        default  { $false }
    }
}

# Same as typing the CLI in a terminal while logged out: its own login screen, in this window.
function Invoke-ProviderLogin([string]$Id, [string]$Exe) {
    switch ($Id) {
        "claude" { & $Exe auth login }
        "codex"  { & $Exe login }
    }
}

# A CLI takes one prompt, so the history (already compressed by the gateway) is written out as a transcript.
function ConvertTo-CloudPrompt($Messages) {
    $msgs = @($Messages)
    if ($msgs.Count -eq 1 -and $msgs[0].role -eq "user") { return $msgs[0].content }
    $sb = New-Object Text.StringBuilder
    $system = @($msgs | Where-Object { $_.role -eq "system" } | ForEach-Object { $_.content })
    if ($system.Count -gt 0) { [void]$sb.AppendLine("Instructions:").AppendLine(($system -join "`n`n")).AppendLine() }
    [void]$sb.AppendLine("Conversation so far. Reply as the assistant to the last user message only.").AppendLine()
    foreach ($m in $msgs | Where-Object { $_.role -ne "system" }) {
        $label = if ($m.role -eq "assistant") { "### Assistant" } else { "### User" }
        [void]$sb.AppendLine($label).AppendLine($m.content).AppendLine()
    }
    $sb.ToString()
}

# Runs the provider's CLI non-interactively (prompt on stdin, JSON on stdout), with a spinner while it works.
function Invoke-CloudProvider([string]$Id, [string]$Exe, [string]$Prompt, [Diagnostics.Stopwatch]$Stopwatch) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = switch ($Id) {
        # A chat answer, not an agent session: no tools, so nothing on this computer is read or changed.
        "claude" { '-p --output-format json --tools ""' }
        "codex"  { 'exec --json --skip-git-repo-check --sandbox read-only -' }
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    # A neutral folder: started in a project folder, Claude Code would load that project's CLAUDE.md
    # and memory, and answer chat questions about "your project".
    $workDir = Join-Path $env:TEMP "modelpicker-cloud"
    if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
    $psi.WorkingDirectory = $workDir

    $p = [Diagnostics.Process]::Start($psi)
    try {
        $bytes = $utf8.GetBytes($Prompt)
        $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $p.StandardInput.Close()
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        $tick = 0
        while (-not $p.WaitForExit(100)) { Show-Waiting "$($cloudProviders[$Id].Name) pisze odpowiedź" $Stopwatch ($tick++) }
        $p.WaitForExit()
        $text = $stdout.Result
        $errText = $stderr.Result.Trim()
    }
    finally {
        # Ctrl+C: the .cmd shim's child (node) must go too, hence /T.
        if (-not $p.HasExited) { & taskkill /PID $p.Id /T /F *> $null }
        Clear-Waiting
    }

    $r = @{ Content = $null; Model = $cloudProviders[$Id].Name; InputTokens = 0; OutputTokens = 0 }
    switch ($Id) {
        "claude" {
            try { $j = $text | ConvertFrom-Json } catch { throw "Claude Code nie zwrócił odpowiedzi: $(if ($errText) { $errText } else { $text })" }
            if ($j.is_error) { throw "Claude Code: $($j.result)" }
            $r.Content = $j.result
            $model = @($j.modelUsage.PSObject.Properties.Name) | Select-Object -First 1
            if ($model) { $r.Model = "Claude Code ($model)" }
            $u = $j.usage
            $r.InputTokens = [int]$u.input_tokens + [int]$u.cache_creation_input_tokens + [int]$u.cache_read_input_tokens
            $r.OutputTokens = [int]$u.output_tokens
        }
        "codex" {
            $failure = $null
            foreach ($line in $text -split "`r?`n") {
                if (-not $line.StartsWith("{")) { continue }
                try { $e = $line | ConvertFrom-Json } catch { continue }
                switch ($e.type) {
                    "item.completed" { if ($e.item.type -eq "agent_message") { $r.Content = $e.item.text } }
                    "turn.completed" { $r.InputTokens = [int]$e.usage.input_tokens; $r.OutputTokens = [int]$e.usage.output_tokens }
                    "turn.failed"    { $failure = $e.error.message }
                    "error"          { $failure = $e.message }
                }
            }
            if ($failure -and -not $r.Content) { throw "Codex: $failure" }
        }
    }
    if (-not $r.Content) { throw "$($cloudProviders[$Id].Name) zakończył się bez odpowiedzi (kod $($p.ExitCode)). $errText" }
    $r
}

# The box shown when the gateway priced a task as cloud work. Returns a provider id or "local".
function Select-CloudProvider($Handoff) {
    $allowed = @($Handoff.providers | Where-Object { $cloudProviders.Contains($_) })
    $rt = $Handoff.routing
    Write-Host ""
    Write-Host "+-- Zadanie zostało wycenione na użycie chmury " -ForegroundColor Magenta
    Write-Host "|   Powód: $($rt.reason)" -ForegroundColor Magenta
    Write-Host ("|   Trudność {0}, szacowany czas lokalnie {1}s, ~{2} tokenów" -f `
        $rt.analysis.difficulty, $rt.analysis.estimated_local_seconds, ($rt.analysis.estimated_input_tokens + $rt.analysis.estimated_output_tokens)) -ForegroundColor Magenta
    $labels = @(foreach ($id in $allowed) {
        $p = $cloudProviders[$id]
        if (Get-CliPath $p.Command) { $p.Name } else { "$($p.Name) (nie zainstalowany)" }
    }) + "Lokalnie mimo to (wolniej, bez kosztów)"
    $ids = @($allowed) + "local"
    # The cursor starts on the provider chosen last time.
    $start = if ($lastProvider -and $ids -contains $lastProvider) { [array]::IndexOf($ids, $lastProvider) } else { 0 }
    $choice = $ids[(Read-MenuChoice $labels $start)]
    Write-Host "+--" -ForegroundColor Magenta
    $choice
}

# Arrow-key menu: Up/Down moves, Enter selects (digits jump straight to an option). Returns the chosen index.
function Read-MenuChoice([string[]]$Labels, [int]$Selected = 0) {
    if ([Console]::IsInputRedirected) {
        # No key presses to read (input piped in, e.g. by tests): a number, Enter for the highlighted option.
        for ($i = 0; $i -lt $Labels.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $Labels[$i]) }
        while ($true) {
            $choice = (Read-Host "Wybierz [Enter = $($Selected + 1)]").Trim()
            if ($choice -eq "") { return $Selected }
            $n = 0
            if ([int]::TryParse($choice, [ref]$n) -and $n -ge 1 -and $n -le $Labels.Count) { return $n - 1 }
        }
    }

    Write-Host "  (strzałki: wybór, Enter: zatwierdź)" -ForegroundColor DarkGray
    $width = try { [Console]::WindowWidth - 1 } catch { 79 }
    $top = -1
    $cursorVisible = try { [Console]::CursorVisible } catch { $true }
    try { [Console]::CursorVisible = $false } catch { }
    try {
        while ($true) {
            if ($top -ge 0) { [Console]::SetCursorPosition(0, $top) }
            for ($i = 0; $i -lt $Labels.Count; $i++) {
                $line = if ($i -eq $Selected) { "  > $($Labels[$i])" } else { "    $($Labels[$i])" }
                $line = $line.PadRight($width)
                if ($i -eq $Selected) { Write-Host $line -ForegroundColor Black -BackgroundColor Cyan }
                else { Write-Host $line -ForegroundColor White }
            }
            # Measured after drawing: if the window scrolled while drawing, the menu moved up with it.
            if ($top -lt 0) { $top = [Console]::CursorTop - $Labels.Count }

            $key = [Console]::ReadKey($true)
            switch ($key.Key) {
                "UpArrow"   { $Selected = ($Selected - 1 + $Labels.Count) % $Labels.Count }
                "DownArrow" { $Selected = ($Selected + 1) % $Labels.Count }
                "Home"      { $Selected = 0 }
                "End"       { $Selected = $Labels.Count - 1 }
                "Enter"     { return $Selected }
                default {
                    $n = [int]$key.KeyChar - [int][char]'0'
                    if ($n -ge 1 -and $n -le $Labels.Count) { $Selected = $n - 1 }
                }
            }
        }
    }
    finally {
        try { [Console]::CursorVisible = $cursorVisible } catch { }
    }
}

# Makes sure the provider's CLI is installed and logged in. Returns its path, or $null to go back to the menu.
function Resolve-CloudProvider([string]$Id) {
    $p = $cloudProviders[$Id]
    $exe = Get-CliPath $p.Command
    if (-not $exe) {
        Write-Host "$($p.Name) nie jest zainstalowany. Zainstaluj: $($p.Install), potem otwórz nowe okno." -ForegroundColor Red
        return $null
    }
    if (Test-ProviderLogin $Id $exe) { return $exe }
    Write-Host "Nie jesteś zalogowany do $($p.Name) - otwieram logowanie." -ForegroundColor Yellow
    Invoke-ProviderLogin $Id $exe
    if (Test-ProviderLogin $Id $exe) { Write-Host "Zalogowano do $($p.Name)." -ForegroundColor Green; return $exe }
    Write-Host "Logowanie do $($p.Name) nie powiodło się." -ForegroundColor Red
    $null
}

function Send-CloudUsage([string]$Id, $Result) {
    try {
        $body = @{ provider = $Id; model = $Result.Model; input_tokens = $Result.InputTokens; output_tokens = $Result.OutputTokens } | ConvertTo-Json
        $r = Invoke-RestMethod -Method Post "$Url/v1/usage" -Headers @{ Authorization = "Bearer $Key" } `
            -ContentType "application/json; charset=utf-8" -Body $utf8.GetBytes($body) -TimeoutSec 10
        $r.remaining_cloud_tokens
    } catch { $null }
}

function Show-Stats {
    try { $s = Invoke-RestMethod "$Url/stats" -Headers @{ Authorization = "Bearer $Key" } -TimeoutSec 5 }
    catch { Write-Host "Nie udało się pobrać statystyk: $(Get-ErrorText $_)" -ForegroundColor Red; return }
    $h = $s.hardware
    $row = { param($label, $value) Write-Host ("  {0,-16} " -f $label) -NoNewline -ForegroundColor DarkGray; Write-Host $value }

    Write-Host ""
    Write-Host "Model lokalny" -ForegroundColor Cyan
    & $row "model" "$($s.models.local) (klasyfikator: $($s.models.classifier))"
    $status = if ($s.ready) { "gotowy" } else { "rozgrzewa się" }
    & $row "stan" $status
    $measured = if ($h.samples -gt 0) { "pomiarów: $($h.samples)" } else { "wartości domyślne, jeszcze bez pomiaru" }
    & $row "prędkość" ("{0} tok/s pisanie, {1:N0} tok/s czytanie ({2})" -f $h.generation_tokens_per_second, $h.prompt_tokens_per_second, $measured)

    if ($s.me) {
        $m = $s.me
        $u = $m.usage
        Write-Host ""
        Write-Host ("Twoje zużycie - {0}, {1}" -f $m.user, (Get-Date -Format "MMMM yyyy")) -ForegroundColor Cyan
        $localReq = if ($u) { $u.localRequests } else { 0 }; $localTok = if ($u) { $u.localTokens } else { 0 }
        $cloudReq = if ($u) { $u.cloudRequests } else { 0 }; $cloudTok = if ($u) { $u.cloudTokens } else { 0 }
        & $row "lokalnie" ("{0} zapytań, {1:N0} tokenów (za darmo)" -f $localReq, $localTok)
        & $row "chmura" ("{0} zapytań, {1:N0} tokenów" -f $cloudReq, $cloudTok)
        if ($m.monthly_cloud_token_budget -gt 0) {
            $pct = [Math]::Round(100 * $m.remaining_cloud_tokens / $m.monthly_cloud_token_budget)
            & $row "budżet chmury" ("zostało {0:N0} z {1:N0} tokenów ({2}%)" -f $m.remaining_cloud_tokens, $m.monthly_cloud_token_budget, $pct)
        } else {
            & $row "budżet chmury" "brak dostępu do chmury"
        }
    }
}

# Models answer in markdown; printed raw it is full of #, ** and ``` marks. This renders the common parts
# with console colours (no ANSI codes, so it also works in the old console): headings, bold, inline code,
# code blocks, lists, quotes, rules and tables.
$script:InlinePattern = '(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\[[^\]]+\]\([^)\s]+\))'

function Write-InlineParts([string]$Text, [ConsoleColor]$Color = "Gray") {
    foreach ($part in [regex]::Split($Text, $script:InlinePattern)) {
        if ($part -eq "") { continue }
        if ($part -match '^`(.+)`$') { Write-Host $Matches[1] -NoNewline -ForegroundColor Yellow }
        elseif ($part -match '^(\*\*|__)(.+)\1$') { Write-Host $Matches[2] -NoNewline -ForegroundColor White }
        elseif ($part -match '^\[(.+)\]\((.+)\)$') {
            Write-Host $Matches[1] -NoNewline -ForegroundColor $Color
            Write-Host " ($($Matches[2]))" -NoNewline -ForegroundColor DarkGray
        }
        else {
            # Single-asterisk italics cannot be shown in a console: just drop the marks.
            Write-Host ([regex]::Replace($part, '(?<![\w*])\*(?=\S)([^*]+?)(?<=\S)\*(?![\w*])', '$1')) -NoNewline -ForegroundColor $Color
        }
    }
}

function Write-Inline([string]$Text, [ConsoleColor]$Color = "Gray") {
    Write-InlineParts $Text $Color
    Write-Host ""
}

# Length as displayed, i.e. without the inline marks.
function Get-PlainLength([string]$Text) {
    ([regex]::Replace($Text, '`([^`]+)`|\*\*([^*]+)\*\*|__([^_]+)__', '$1$2$3')).Length
}

function Write-MarkdownTable([string[]]$Rows) {
    $cells = @(foreach ($r in $Rows) {
        $t = $r.Trim()
        if ($t -match '^\|?\s*:?-{2,}') { continue }  # the |---|---| separator row
        , @($t.Trim('|').Split('|') | ForEach-Object { $_.Trim() })
    })
    if ($cells.Count -eq 0) { return }
    $cols = ($cells | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
    $widths = @(for ($c = 0; $c -lt $cols; $c++) {
        ($cells | ForEach-Object { if ($c -lt $_.Count) { Get-PlainLength $_[$c] } else { 0 } } | Measure-Object -Maximum).Maximum
    })
    for ($r = 0; $r -lt $cells.Count; $r++) {
        Write-Host "  " -NoNewline
        for ($c = 0; $c -lt $cols; $c++) {
            $cell = if ($c -lt $cells[$r].Count) { $cells[$r][$c] } else { "" }
            $pad = " " * ($widths[$c] - (Get-PlainLength $cell))
            if ($r -eq 0) { Write-Host ([regex]::Replace($cell, '`|\*\*|__', '') + $pad) -NoNewline -ForegroundColor Cyan }
            else { Write-InlineParts $cell; Write-Host $pad -NoNewline }
            if ($c -lt $cols - 1) { Write-Host "  │  " -NoNewline -ForegroundColor DarkGray }
        }
        Write-Host ""
        if ($r -eq 0) { Write-Host ("  " + (($widths | ForEach-Object { "─" * $_ }) -join "──┼──")) -ForegroundColor DarkGray }
    }
}

function Write-Markdown([string]$Text) {
    $lines = @($Text -split "`r?`n")
    $inCode = $false
    $table = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -le $lines.Count; $i++) {
        $line = if ($i -lt $lines.Count) { $lines[$i] } else { $null }

        # A table ends at the first line that is not a table row.
        if ($table.Count -gt 0 -and ($null -eq $line -or -not $line.TrimStart().StartsWith("|"))) {
            Write-MarkdownTable $table.ToArray()
            $table.Clear()
        }
        if ($null -eq $line) { break }

        if ($line -match '^\s*(```|~~~)\s*(\S*)') {
            if (-not $inCode) {
                $lang = if ($Matches[2]) { $Matches[2] } else { "kod" }
                Write-Host "  ┌─ $lang" -ForegroundColor DarkGray
            } else {
                Write-Host "  └─" -ForegroundColor DarkGray
            }
            $inCode = -not $inCode
            continue
        }
        if ($inCode) {
            Write-Host "  │ " -NoNewline -ForegroundColor DarkGray
            Write-Host $line -ForegroundColor Yellow
            continue
        }
        if ($line.TrimStart().StartsWith("|")) { $table.Add($line); continue }

        # Closing hashes only after a space ("## Title ##"), so "C#" keeps its #.
        if ($line -match '^(#{1,6})\s+(.*?)(\s+#+)?\s*$') {
            $level = $Matches[1].Length
            $title = [regex]::Replace($Matches[2], '`|\*\*|__', '')
            if ($level -eq 1) {
                Write-Host $title -ForegroundColor Cyan
                Write-Host ("═" * $title.Length) -ForegroundColor DarkCyan
            } elseif ($level -eq 2) {
                Write-Host $title -ForegroundColor Cyan
                Write-Host ("─" * $title.Length) -ForegroundColor DarkCyan
            } else {
                Write-Host $title -ForegroundColor DarkCyan
            }
        }
        elseif ($line -match '^\s*([-*_])(\s*\1){2,}\s*$') {
            $width = try { [Math]::Min([Console]::WindowWidth - 1, 60) } catch { 60 }
            Write-Host ("─" * $width) -ForegroundColor DarkGray
        }
        elseif ($line -match '^(\s*)[-*+]\s+(.*)$') {
            Write-Host ("  " + $Matches[1] + "• ") -NoNewline -ForegroundColor Cyan
            Write-Inline $Matches[2]
        }
        elseif ($line -match '^(\s*)(\d+)[.)]\s+(.*)$') {
            Write-Host ("  " + $Matches[1] + $Matches[2] + ". ") -NoNewline -ForegroundColor Cyan
            Write-Inline $Matches[3]
        }
        elseif ($line -match '^\s*>\s?(.*)$') {
            Write-Host "  ┃ " -NoNewline -ForegroundColor DarkGray
            Write-Inline $Matches[1] DarkGray
        }
        else {
            Write-Inline $line
        }
    }
    if ($inCode) { Write-Host "  └─" -ForegroundColor DarkGray }  # an unclosed block (e.g. a cut-off answer)
}

function Show-Compression($Routing) {
    if ($Routing.compression) {
        $c = $Routing.compression
        if ($c.applied) { Write-Host ("[kompresja] {0} -> {1} tokenów (oszczędność {2}), {3}s" -f $c.original_tokens, $c.compressed_tokens, $c.saved_tokens, $c.seconds) -ForegroundColor Yellow }
        else { Write-Host "[kompresja pominięta] $($c.note)" -ForegroundColor DarkYellow }
    }
    if ($Routing.fallback_used) { Write-Host "[fallback] model lokalny zawiódł, zadanie przekazane do chmury" -ForegroundColor Red }
}

# Who the key belongs to: the key itself is a password, so it is never printed.
$me = try { (Invoke-RestMethod "$Url/stats" -Headers @{ Authorization = "Bearer $Key" } -TimeoutSec 5).me } catch { $null }
if ($me) {
    $access = if ($me.monthly_cloud_token_budget -gt 0) { "budżet chmury {0:N0} tokenów/mies." -f $me.monthly_cloud_token_budget } else { "bez dostępu do chmury" }
    Write-Host "ModelPicker - użytkownik: $($me.user) ($access)" -ForegroundColor Cyan
} else {
    Write-Host "ModelPicker - gateway nie zna tego klucza, zapytania zostaną odrzucone (sprawdź Gateway:ApiKeys)." -ForegroundColor Red
}
Write-Host "Komendy: /reset - nowa rozmowa, /stats - statystyki, /local /cloud /auto - wymuś routing, /exit - koniec" -ForegroundColor DarkCyan
while ($true) {
    $q = Read-Host "`nTy [$mode]"
    if ($q -eq "/exit") { break }
    if ($q -eq "/reset") { $history.Clear(); Write-Host "Nowa rozmowa." -ForegroundColor Cyan; continue }
    if ($q -eq "/stats") { Show-Stats; continue }
    if ($q -in "/local", "/cloud", "/auto") { $mode = $q.TrimStart("/"); Write-Host "Tryb routingu: $mode" -ForegroundColor Cyan; continue }
    if ([string]::IsNullOrWhiteSpace($q)) { continue }

    [void]$history.Add(@{ role = "user"; content = $q })
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = Send-Chat $mode $sw
    $answer = $null

    if ($res.Handoff) {
        Show-Compression $res.Handoff.routing
        while ($true) {
            $choice = Select-CloudProvider $res.Handoff
            if ($choice -eq "local") {
                $sw.Restart()
                $res = Send-Chat "local" $sw
                break
            }
            $exe = Resolve-CloudProvider $choice
            if (-not $exe) { continue }
            $script:lastProvider = $choice

            $sw.Restart()
            try {
                $cloud = Invoke-CloudProvider $choice $exe (ConvertTo-CloudPrompt $res.Handoff.messages) $sw
            }
            catch {
                Write-Host "Błąd: $(Get-ErrorText $_)" -ForegroundColor Red
                continue  # back to the menu: another provider, or local
            }
            $answer = $cloud.Content
            $remaining = Send-CloudUsage $choice $cloud
            $footer = "{0} · {1:F1} s · {2:N0} wej. / {3:N0} wyj. tokenów" -f $cloud.Model, $sw.Elapsed.TotalSeconds, $cloud.InputTokens, $cloud.OutputTokens
            if ($null -ne $remaining) { $footer += " · budżet chmury: zostało {0:N0}" -f $remaining }
            $footerColor = "Magenta"
            $res = $null
            break
        }
    }

    if ($res) {
        if ($res.Failure -or $null -eq $res.Answer) {
            Write-Host "Błąd: $(if ($res.Failure) { $res.Failure } else { 'brak odpowiedzi' })" -ForegroundColor Red
            $history.RemoveAt($history.Count - 1)
            continue
        }
        $answer = $res.Answer
        $footer = "{0} (lokalnie) · {1:F1} s" -f $res.Final.model, $sw.Elapsed.TotalSeconds
        if ($res.Usage) { $footer += " · {0:N0} wej. / {1:N0} wyj. tokenów" -f $res.Usage.prompt_tokens, $res.Usage.completion_tokens }
        $footerColor = "Green"
    }
    $sw.Stop()

    [void]$history.Add(@{ role = "assistant"; content = $answer })
    Write-Host ""
    Write-Markdown $answer
    Write-Host ""
    Write-Host "-- $footer" -ForegroundColor $footerColor
}
