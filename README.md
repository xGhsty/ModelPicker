# ModelPicker

Lokalny gateway dla modeli językowych. Dla każdego zapytania sam decyduje, czy wykonać je na modelu lokalnym (przez Ollamę), czy wysłać do chmury (Claude). Proste zadania zostają na Twoim komputerze i nic nie kosztują, trudne albo zbyt wolne lokalnie trafiają do chmury.

Gateway udostępnia API zgodne z OpenAI (`/v1/chat/completions`), więc można do niego podłączyć narzędzia, które obsługują OpenAI: wtyczki do IDE, SDK, własne skrypty.

## Jak podejmowana jest decyzja

1. Mały model klasyfikujący (`qwen3:1.7b`, na procesorze) ocenia trudność zadania (1 do 5) i spodziewaną długość odpowiedzi.
2. Gateway szacuje, ile zajęłaby odpowiedź lokalnie. Korzysta z prędkości zmierzonej na tym komputerze przy starcie i z długości odpowiedzi, których uczy się na bieżąco.
3. Zadanie idzie do chmury, gdy:
   - trudność jest równa progowi lub wyższa (domyślnie 4),
   - szacowany czas lokalny przekracza limit (domyślnie 30 s),
   - zadanie nie mieści się w kontekście modelu lokalnego.
4. Zadanie zostaje lokalnie, gdy użytkownik nie ma budżetu na chmurę albo chmura nie jest skonfigurowana.
5. Jeśli wybrany model zawiedzie, odpowiada drugi (fallback).

Przed wysłaniem do chmury starsza część długiej rozmowy jest streszczana lokalnie, żeby zużyć mniej płatnych tokenów.

## Wymagania

- Windows 10 lub 11, PowerShell 5.1 lub nowszy
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com/download)

Brakujące .NET i Ollamę skrypt instalacyjny może doinstalować przez `winget`.

## Instalacja

```powershell
git clone https://github.com/xGhsty/ModelPicker.git
cd ModelPicker
.\install.ps1
```

Skrypt:

1. sprawdza wymagania i proponuje instalację tego, czego brakuje,
2. dodaje komendę `modelpicker` do profilu PowerShella,
3. skanuje sprzęt i pomaga wybrać model lokalny, który zmieści się w pamięci karty graficznej (zalecany jest największy model mieszczący się w całości na GPU), a potem pobiera go razem z klasyfikatorem.

Skrypt można uruchamiać wielokrotnie. Jeśli Windows blokuje uruchamianie skryptów, użyj:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Po instalacji otwórz nowe okno PowerShella.

## Użycie

| Komenda | Opis |
|---|---|
| `modelpicker` | Uruchamia gateway i czat w konsoli. Chmura jest symulowana (bez kosztów). |
| `modelpicker -FakeCloud` | Zapytania "do chmury" przechodzą przez prawdziwego klienta API, ale trafiają do małego modelu w Ollamie. Darmowy test całej ścieżki. |
| `modelpicker -Real` | Odpowiada prawdziwy Claude. Wymaga zmiennej `ANTHROPIC_API_KEY`. |
| `modelpicker -Key dev-key-intern` | Czat jako użytkownik bez budżetu na chmurę. |
| `modelpicker -Restart` | Restartuje gateway, np. po zmianach w kodzie. |
| `modelpicker -KeepRunning` | Po wyjściu z czatu zostawia gateway i modele w pamięci. |
| `modelpicker-stop` | Zatrzymuje gateway i zwalnia modele z pamięci RAM i VRAM. |
| `modelpicker setup` | Ponowny skan sprzętu i wybór modelu lokalnego. |
| `modelpicker setup -List` | Pokazuje, które modele pasują do sprzętu, bez zmian w konfiguracji. |

Przy pierwszym uruchomieniu `modelpicker` sam przeprowadza konfigurację, jeśli nie była wykonana.

Komendy w czacie:

| Komenda | Opis |
|---|---|
| `/local`, `/cloud`, `/auto` | Wymusza model lokalny, chmurę albo przywraca automatyczny wybór. |
| `/reset` | Nowa rozmowa. |
| `/stats` | Zmierzona prędkość sprzętu i zużycie tokenów. |
| `/exit` | Wyjście. Gateway zostaje zatrzymany, a modele zwolnione z pamięci. |

Klucz Anthropic API ustawia się raz dla swojego konta:

```powershell
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', 'sk-ant-...', 'User')
```

## API

Gateway nasłuchuje na `http://localhost:5080`.

```powershell
$body = @{
    model    = "auto"   # "auto", "local" albo "cloud"
    messages = @(@{ role = "user"; content = "Ile to 2+2?" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Method Post http://localhost:5080/v1/chat/completions `
    -Headers @{ Authorization = "Bearer dev-key-jan" } `
    -ContentType "application/json" -Body $body
```

Odpowiedź ma format OpenAI z dodatkowym polem `routing`: gdzie trafiło zapytanie, dlaczego, szacunki i informacje o kompresji.

Z `"stream": true` odpowiedź przychodzi jako server-sent events. Pierwsze zdarzenie zawiera decyzję o routingu i jest wysyłane od razu, zanim powstanie odpowiedź.

`GET /stats` zwraca zmierzoną prędkość sprzętu, gotowość gatewaya i zużycie tokenów.

## Konfiguracja

Główne ustawienia są w `src/ModelPicker/appsettings.json`, w sekcji `Gateway`:

| Ustawienie | Znaczenie |
|---|---|
| `Ollama.Model` | Model lokalny. Zwykle ustawiany przez `modelpicker setup`. |
| `Ollama.ClassifierModel` | Model oceniający trudność zadań. |
| `Cloud.Model` | Model w chmurze. |
| `Routing.CloudDifficultyThreshold` | Od jakiej trudności zadanie idzie do chmury. |
| `Routing.MaxLocalLatencySeconds` | Maksymalny akceptowany czas odpowiedzi lokalnej. |
| `Compression.*` | Kiedy i jak streszczać historię przed wysłaniem do chmury. |
| `ApiKeys` | Klucze użytkowników i ich miesięczne budżety tokenów w chmurze (0 oznacza brak dostępu do chmury). |

Klucze `dev-key-jan` i `dev-key-intern` służą tylko do testów. Prawdziwych kluczy nie wpisuj do `appsettings.json`, bo ten plik jest w repozytorium. Umieść je w `appsettings.Local.json` (sekcja `Gateway.ApiKeys`), który nadpisuje `appsettings.json` i nie jest commitowany. Klucz Anthropic API trzymaj w zmiennej środowiskowej `ANTHROPIC_API_KEY`, nie w plikach.

### Pliki lokalne

Te pliki powstają w `src/ModelPicker/` na każdym komputerze osobno i nie trafiają do repozytorium:

| Plik | Zawartość |
|---|---|
| `appsettings.Local.json` | Model lokalny wybrany przez `modelpicker setup`. |
| `modelpicker-stats.json` | Długości odpowiedzi modelu lokalnego, z których gateway uczy się szacować czas. Zawiera tylko liczby i skróty (hash) pytań, bez ich treści. |

## Struktura repozytorium

| Ścieżka | Zawartość |
|---|---|
| `install.ps1` | Instalator. |
| `scripts/` | Launcher (komenda `modelpicker`) i czat w konsoli. |
| `src/ModelPicker/` | Gateway: kod aplikacji i pliki `appsettings`. |
| `tests/` | Skrypty testujące routing i kompresję na działającym gatewayu. |

## Ograniczenia

- Launcher i instalator działają tylko na Windowsie. Sam gateway jest aplikacją .NET i można go uruchomić także na Linuxie i macOS (`dotnet run --project src/ModelPicker`).
- Wykrywanie GPU obsługuje tylko karty NVIDIA. Na innych kartach kreator uzna, że nie ma GPU, i zaproponuje mały model.
- Zużycie tokenów jest trzymane w pamięci, więc restart gatewaya zeruje miesięczne liczniki budżetów.
- Odpowiedź przychodzi w całości, gdy jest gotowa. Strumień przesyła na razie tylko decyzję o routingu z wyprzedzeniem, bez pisania odpowiedzi słowo po słowie.
