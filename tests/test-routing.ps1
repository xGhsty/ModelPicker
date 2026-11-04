$prompts = @(
    "Cześć, jak się masz?",
    "Przetłumacz na angielski: Dzień dobry, miło Cię poznać.",
    "Wyjaśnij krótko, czym różni się lista od tablicy w C#.",
    "Napisz w C# funkcję sprawdzającą, czy string jest palindromem.",
    "Zaprojektuj architekturę systemu rezerwacji biletów lotniczych obsługującego 10 mln użytkowników: mikroserwisy, bazy danych, cache, kolejki, spójność danych przy overbookingu, odporność na awarie. Uzasadnij każdą decyzję.",
    "Udowodnij, że istnieje nieskończenie wiele liczb pierwszych postaci 4k+3, a następnie przeanalizuj, czy analogiczny dowód elementarny działa dla 4k+1."
)
foreach ($p in $prompts) {
    $body = @{ messages = @(@{ role = "user"; content = $p }); max_tokens = 300 } | ConvertTo-Json -Depth 3
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod -Method Post http://localhost:5080/v1/chat/completions `
        -Headers @{ Authorization = "Bearer dev-key-jan" } `
        -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 600
    $sw.Stop()
    $a = $r.routing.analysis
    [pscustomobject]@{
        Prompt     = $p.Substring(0, [Math]::Min(45, $p.Length))
        Difficulty = $a.difficulty
        Category   = $a.category
        EstSec     = $a.estimated_local_seconds
        Target     = $r.routing.target
        RealSec    = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        Reason     = $r.routing.reason
    }
}
