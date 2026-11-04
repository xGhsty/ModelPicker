$messages = @(
    @{ role = "system"; content = "Jesteś doświadczonym architektem oprogramowania .NET. Odpowiadaj po polsku." },
    @{ role = "user"; content = @"
Robię projekt BiblioTech - system do zarządzania biblioteką w C# (.NET 10, ASP.NET Core, SQL Server 2022). Wymagania od klienta:
- czytelnik może mieć maksymalnie 5 wypożyczeń naraz,
- standardowy okres wypożyczenia to 21 dni, można przedłużyć raz o 14 dni,
- kara za spóźnienie wynosi 0,50 zł za każdy dzień, maksymalnie 30 zł za egzemplarz,
- rezerwacja książki jest ważna 3 dni od momentu, gdy książka wróci do biblioteki,
- bibliotekarz musi widzieć historię wypożyczeń czytelnika z ostatnich 2 lat (RODO - starsze dane anonimizujemy).
Termin oddania projektu: 15 grudnia. Zespół: 3 osoby (ja - backend, Ania - frontend w React, Tomek - baza danych i testy).
Od czego zacząć z modelem domeny?
"@ },
    @{ role = "assistant"; content = @"
Zacznij od wydzielenia agregatów, bo reguły biznesowe są tu dość ścisłe.

1. Agregat Czytelnik (Reader): Id, numer karty, dane kontaktowe, status (aktywny / zablokowany). Reguła limitu 5 wypożyczeń powinna być sprawdzana w domenie, a nie tylko w bazie - metoda Reader.CanBorrow() zwraca false, gdy liczba aktywnych wypożyczeń >= 5 lub czytelnik ma nieopłacone kary powyżej 30 zł łącznie.

2. Agregat Egzemplarz (Copy) oddzielony od Książki (Book). Książka to metadane (ISBN, tytuł, autorzy), egzemplarz to fizyczny obiekt z kodem kreskowym i statusem: dostępny, wypożyczony, zarezerwowany, wycofany. To rozróżnienie jest kluczowe, bo rezerwujesz książkę, a wypożyczasz egzemplarz.

3. Agregat Wypożyczenie (Loan): CopyId, ReaderId, LoanDate, DueDate (LoanDate + 21 dni), ExtendedOnce (bool), ReturnDate. Metoda Extend() rzuca DomainException, jeśli ExtendedOnce == true albo egzemplarz ma oczekującą rezerwację. Kara liczona metodą CalculateFine(DateOnly today): min(0,50 zł * dni spóźnienia, 30 zł).

4. Rezerwacja (Reservation): BookId, ReaderId, CreatedAt, ReadyAt (kiedy egzemplarz wrócił), ExpiresAt = ReadyAt + 3 dni. Wygasanie rezerwacji obsłuż zadaniem w tle (BackgroundService co godzinę), nie przy odczycie.

5. Historia i RODO: nie usuwaj wypożyczeń, tylko po 2 latach zastąp ReaderId pseudonimem w osobnym zadaniu. Dzięki temu statystyki (np. najpopularniejsze książki) dalej działają.

Decyzje techniczne, które polecam od razu:
- Entity Framework Core bez lazy loading (jawne Include), żeby uniknąć problemu N+1 przy historii.
- Pieniądze jako decimal z typem wartości Money, nigdy double.
- Daty jako DateOnly dla terminów, DateTimeOffset dla zdarzeń.
- Testy jednostkowe reguł domeny (Tomek) przed endpointami - to najtańszy sposób na wyłapanie błędów w liczeniu kar.

Podział pracy: Ty robisz domenę i API, Tomek schemat + migracje + testy reguł, Ania może już zacząć od makiet ekranu wypożyczenia i historii czytelnika.
"@ },
    @{ role = "user"; content = @"
Super, wdrożyliśmy to. Ale mamy błąd przy migracji, Tomek dostaje:
"The DELETE statement conflicted with the REFERENCE constraint FK_Loans_Copies_CopyId"
przy próbie wycofania egzemplarza (status wycofany). Postanowiliśmy też, że zamiast fizycznego usuwania egzemplarzy używamy soft delete. Ania pyta, czy endpoint historii ma mieć paginację, bo czytelnik może mieć kilkaset wypożyczeń.
"@ },
    @{ role = "assistant"; content = @"
Błąd FK_Loans_Copies_CopyId wynika z tego, że wycofanie egzemplarza jest realizowane jako DELETE, a egzemplarz ma powiązane wypożyczenia. Skoro zdecydowaliście się na soft delete, to rozwiązanie jest proste:

1. Dodaj do Copy pole IsWithdrawn (bool) i WithdrawnAt (DateTimeOffset?). Wycofanie to zmiana statusu, nie DELETE.
2. W konfiguracji EF Core ustaw globalny filtr: modelBuilder.Entity<Copy>().HasQueryFilter(c => !c.IsWithdrawn). Pamiętaj, że w historii wypożyczeń chcesz widzieć także wycofane egzemplarze, więc tam użyj IgnoreQueryFilters().
3. Zmień zachowanie klucza obcego na DeleteBehavior.Restrict, żeby przypadkowe fizyczne usunięcie zawsze kończyło się błędem, a nie kaskadowym kasowaniem historii (to byłoby niezgodne z wymaganiem 2 lat historii).
4. Migracja: Tomek powinien dodać kolumny z wartością domyślną false i usunąć z kodu wszystkie wywołania Remove() na Copy.

Paginacja historii: tak, zdecydowanie. Proponuję paginację kursorem (keyset) po (LoanDate DESC, Id DESC), a nie OFFSET, bo przy kilkuset rekordach i filtrowaniu po czytelniku keyset jest stabilny przy dopisywaniu nowych wypożyczeń. Rozmiar strony domyślnie 20, maksymalnie 100. Endpoint: GET /api/readers/{id}/loans?cursor=...&pageSize=20. Odpowiedź zawiera items i nextCursor (null na ostatniej stronie). Dodaj indeks złożony IX_Loans_ReaderId_LoanDate_Id - Tomek może to zrobić w tej samej migracji.
"@ },
    @{ role = "user"; content = "Dzięki, działa. Jeszcze jedno: jak testować liczenie kar, żeby testy nie zależały od dzisiejszej daty?" },
    @{ role = "assistant"; content = "Wstrzykuj czas przez TimeProvider (wbudowany w .NET 8+). W testach używaj FakeTimeProvider z pakietu Microsoft.Extensions.TimeProvider.Testing i ustawiaj datę ręcznie, np. 21 dni + 3 dni spóźnienia = oczekiwana kara 1,50 zł; 100 dni spóźnienia = 30 zł (limit)." },
    @{ role = "user"; content = "Ok. Ostatnia rzecz na dziś: dostaliśmy informację, że BiblioTech ma docelowo obsługiwać sieć 50 bibliotek miejskich. Na podstawie wszystkiego, co ustaliliśmy, zaprojektuj pełną architekturę systemu dla sieci 50 bibliotek z replikacją danych między oddziałami i trybem offline w razie utraty połączenia. Uzasadnij decyzje." }
)

$body = @{ messages = $messages; max_tokens = 1000 } | ConvertTo-Json -Depth 4
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-RestMethod -Method Post http://localhost:5080/v1/chat/completions `
    -Headers @{ Authorization = "Bearer dev-key-jan" } `
    -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 600
$sw.Stop()

$c = $r.routing.compression
"Route:       $($r.routing.target) ($($r.routing.reason))"
"Difficulty:  $($r.routing.analysis.difficulty)"
"Compression: applied=$($c.applied), $($c.note)"
"Tokens:      $($c.original_tokens) -> $($c.compressed_tokens) (saved $($c.saved_tokens)), summary took $($c.seconds)s, total $([Math]::Round($sw.Elapsed.TotalSeconds,1))s"
"Cloud reply: $($r.choices[0].message.content)"
""
"--- SUMMARY ---"
$c.summary
""
"--- FACTS FROM OLDER MESSAGES KEPT IN SUMMARY? ---"
$facts = [ordered]@{
    "limit 5 wypozyczen" = "5"; "21 dni" = "21"; "przedluzenie 14 dni" = "14"; "kara 0,50 zl" = "0[,.]50"; "max 30 zl" = "30";
    "rezerwacja 3 dni" = "3 dni"; "historia 2 lata / RODO" = "RODO|2 lat"; "termin 15 grudnia" = "15 grud"; "zespol Ania/Tomek" = "Ania|Tomek";
    "SQL Server 2022" = "SQL Server"; "FK_Loans_Copies_CopyId" = "FK_Loans_Copies_CopyId"; "soft delete" = "soft delete|IsWithdrawn";
    "bez lazy loading" = "lazy"; "paginacja keyset" = "keyset|kursor"; "Money/decimal" = "decimal|Money"
}
foreach ($f in $facts.GetEnumerator()) { "{0,-26} {1}" -f $f.Key, $(if ($c.summary -match $f.Value) { "TAK" } else { "nie" }) }
