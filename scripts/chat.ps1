# Interactive chat through the ModelPicker gateway. Keeps the conversation history,
# so you can see routing change between messages and context compression kick in.
#   .\scripts\chat.ps1                      (uses dev-key-jan)
#   .\scripts\chat.ps1 -Key dev-key-intern  (user without cloud budget)
param(
    [string]$Key = "dev-key-jan",
    [string]$Url = "http://localhost:5080"
)

[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$history = New-Object System.Collections.ArrayList
$mode = "auto"

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

Write-Host "ModelPicker chat ($Url, klucz: $Key). Komendy: /reset - nowa rozmowa, /stats - zużycie, /local /cloud /auto - wymuś routing, /exit - koniec" -ForegroundColor Cyan
while ($true) {
    $q = Read-Host "`nTy [$mode]"
    if ($q -eq "/exit") { break }
    if ($q -eq "/reset") { $history.Clear(); Write-Host "Nowa rozmowa." -ForegroundColor Cyan; continue }
    if ($q -eq "/stats") { Invoke-RestMethod "$Url/stats" | ConvertTo-Json -Depth 5; continue }
    if ($q -in "/local", "/cloud", "/auto") { $mode = $q.TrimStart("/"); Write-Host "Tryb routingu: $mode" -ForegroundColor Cyan; continue }
    if ([string]::IsNullOrWhiteSpace($q)) { continue }

    [void]$history.Add(@{ role = "user"; content = $q })
    $body = @{ model = $mode; messages = $history; stream = $true } | ConvertTo-Json -Depth 4
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $answer = $null; $final = $null; $failure = $null
    $waitLabel = "analizuję zadanie"
    try {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Url/v1/chat/completions")
        $req.Headers.Add("Authorization", "Bearer $Key")
        $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json")
        $send = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        $tick = 0
        while (-not $send.Wait(100)) { Show-Waiting $waitLabel $sw ($tick++) }
        $resp = $send.Result
        if (-not $resp.IsSuccessStatusCode) { throw "HTTP $([int]$resp.StatusCode): $($resp.Content.ReadAsStringAsync().Result)" }
        $reader = New-Object IO.StreamReader($resp.Content.ReadAsStreamAsync().Result, [Text.Encoding]::UTF8)

        while ($true) {
            # Waiting in 100 ms steps keeps the spinner moving and Ctrl+C responsive.
            $read = $reader.ReadLineAsync()
            while (-not $read.Wait(100)) { Show-Waiting $waitLabel $sw ($tick++) }
            $line = $read.Result
            if ($null -eq $line -or $line -eq "data: [DONE]") { break }
            if (-not $line.StartsWith("data: ")) { continue }
            $chunk = $line.Substring(6) | ConvertFrom-Json

            if ($chunk.error) { $failure = $chunk.error.message; break }
            if ($chunk.routing.phase -eq "decided") {
                # The decision, shown as soon as it is made.
                $rt = $chunk.routing
                Clear-Waiting
                $color = if ($rt.target -eq "local") { "Green" } else { "Magenta" }
                Write-Host ("[{0} | {1}] {2} | trudność {3}, szac. lokalnie {4}s (decyzja po {5:F1}s)" -f `
                    $rt.target, $chunk.model, $rt.reason, $rt.analysis.difficulty, $rt.analysis.estimated_local_seconds, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
                $waitLabel = if ($rt.target -eq "local") { "lokalny model pisze odpowiedź" } else { "czekam na odpowiedź z chmury" }
            }
            elseif ($chunk.choices -and $chunk.choices[0].delta.content) {
                $answer = $chunk.choices[0].delta.content
                $final = $chunk
            }
        }
    }
    catch {
        $failure = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    }
    Clear-Waiting
    $sw.Stop()

    if ($failure -or $null -eq $answer) {
        Write-Host "Błąd: $(if ($failure) { $failure } else { 'brak odpowiedzi' })" -ForegroundColor Red
        $history.RemoveAt($history.Count - 1)
        continue
    }
    [void]$history.Add(@{ role = "assistant"; content = $answer })

    $rt = $final.routing
    $color = if ($rt.target -eq "local") { "Green" } else { "Magenta" }
    Write-Host ("[gotowe] {0}, odpowiedź po {1:F1}s" -f $final.model, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
    if ($rt.compression) {
        $c = $rt.compression
        if ($c.applied) { Write-Host ("[kompresja] {0} -> {1} tokenów (oszczędność {2}), {3}s" -f $c.original_tokens, $c.compressed_tokens, $c.saved_tokens, $c.seconds) -ForegroundColor Yellow }
        else { Write-Host "[kompresja pominięta] $($c.note)" -ForegroundColor DarkYellow }
    }
    if ($rt.fallback_used) { Write-Host "[fallback] pierwszy wybór zawiódł, odpowiedział drugi provider ($($rt.target))" -ForegroundColor Red }
    Write-Host "`nModel: $answer"
}
