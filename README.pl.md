# TaskWall

Tablica zadań na pulpicie Windows: dzień, tydzień, dwa tygodnie, miesiąc albo rok, zegar z kalendarzem i matowe szkło z rozmytej tapety, w stylu [Todowall](https://github.com/Dragonify73/Todowall). Kod jest napisany od zera.

Do tego: dwa konta (Praca / Prywatne) z synchronizacją przez folder w chmurze, backlog z Notion (automatyczna synchronizacja albo import CSV), alarmy i spotkania z przypomnieniami, spotkania i święta z Kalendarza Google, oznaczenia dni (np. HO), powtarzane zadania i oznaczenia, checklisty, sprytne dodawanie, wyszukiwarka, estymacje, kategorie, archiwum, cofanie, eksport CSV. Interfejs po polsku albo angielsku.

*English: [README.md](README.md).* Wcześniej program nazywał się DeskWall: instalator TaskWall sam usuwa starą wersję i przenosi jej ustawienia, a foldery z danymi zostają tam, gdzie były.

## Instalacja

Pobierz `TaskWall-Setup.exe` z [Releases](https://github.com/Falcrum/TaskWall/releases) i uruchom. Instaluje dla bieżącego użytkownika, bez uprawnień administratora i bez .NET:

- program trafia do `%LOCALAPPDATA%\Programs\TaskWall`,
- skrót pojawia się w menu Start, a wpis w *Ustawienia → Aplikacje* (stamtąd odinstalujesz; także Ustawienia TaskWall → Info albo `TaskWall.exe --uninstall`).

Deinstalator pyta osobno o ustawienia tego komputera i o wszystkie zapisane dane.

## Budowanie

```powershell
.\build.ps1           # dist\TaskWall.exe (wymaga .NET 9 Desktop Runtime) + dist\TaskWall-Setup.exe
.\build.ps1 -NoSetup  # tylko dist\TaskWall.exe
```

Aplikacja nie ma okna na pasku zadań. Steruje się nią ikoną w zasobniku (lewy przycisk: karta „Dziś”, prawy: menu), przyciskami na górnym pasku albo prawym przyciskiem na pasku tablicy lub zegarze.

## Obsługa

| Akcja | Jak |
|---|---|
| Konta | PRACA / PRYWATNE na środku górnego paska albo Ctrl+1 / Ctrl+2. Każde konto ma własny folder, kalendarze, Notion, kategorie, oznaczenia dni, archiwum i alarmy |
| Widoki | DZIEŃ · 1 TYDZIEŃ · 2 TYGODNIE · MIESIĄC · ROK (zapamiętywany). ‹ › przesuwa o dzień, tydzień, miesiąc albo rok |
| Dodaj zadanie | „+ dodaj zadanie” pod dniem. Enter dodaje i otwiera kolejne pole, Esc zamyka. Kliknięcie kategorii pod polem wstawia np. `[ART]` |
| Sprytne dodawanie | `jutro`, `pt`, `w pt`, `za 3 dni`, `14.10` na początku albo na końcu (też `tomorrow`, `fri`, `in 3 days`), `2h` / `30 min` (estymacja), `#art` (kategoria). Podgląd pokazuje, co zostało rozpoznane |
| Szybko z dowolnego miejsca | **Ctrl+Shift+Spacja**: tablica wyskakuje z polem na dziś. Lewy klik na ikonie w zasobniku: karta „Dziś” |
| Alarm | ikona zegara na górnym pasku, prawy przycisk na dniu → Dodaj alarm (w ten dzień), „+ ALARM” w karcie Dziś albo menu zasobnika. Godzina: `15:30`, `9`, `za 20 min`. Powtarzanie: codziennie, w dni robocze, co tydzień, co 2 tygodnie, co miesiąc |
| Spotkanie | kliknij kategorię Meeting pod polem nowego zadania albo wpisz `[Meeting] 14-15:30 Sprint` (też `#meeting o 10 30 min`). Okno: tytuł, od–do, przypomnienie, powtarzanie. Godziny spotkań liczą się do sumy dnia |
| Zrobione | kliknij kółko. Kolor obwódki to priorytet z Notion |
| Edytuj | kliknij tekst (dla zadań z Notion: prawy przycisk → Edytuj tekst) |
| Checklista | prawy przycisk → Dodaj listę kontrolną. Chip „☑ 2/5” rozwija listę |
| Przenieś | przeciągnij na dzień, w inne miejsce listy, na BACKLOG albo ARCHIWUM. **Ctrl** tworzy kopię |
| Przeciągnij z zewnątrz | link z przeglądarki lub Notion (zadanie z linkiem; „(9+)” i „\| Notion” są usuwane, `[VFX]` rozpoznane), zaznaczony tekst (każda linia to zadanie), plik CSV/ZIP z Notion (import) |
| Estymacja | prawy przycisk → Estymacja. W rogu dnia licznik zrobionych, pod nim suma godzin (powyżej 8h na pomarańczowo) |
| Oznaczenia dni | prawy przycisk na dniu. Lista per konto w Ustawienia → Konta. „Powtarzaj oznaczenie…”: np. HO w każdy piątek albo urlop od–do |
| Powtarzanie zadań | prawy przycisk → Powtarzaj: szybkie wzorce albo „Powtarzaj…”: co N dni/tygodni/miesięcy/lat, wybrane dni tygodnia, okres od–do. ✕ na wystąpieniu pomija tylko ten dzień |
| Wyczyść | WYCZYŚĆ na górnym pasku przenosi zakończone zadania z widocznego okresu i backlogu do archiwum |
| Archiwum | ✕ po najechaniu, środkowy przycisk, WYCZYŚĆ albo przeciągnięcie na ARCHIWUM. W panelu: ↺ przywróć, 🗑 usuń całkowicie |
| Szukaj | Ctrl+F: zadania, serie, spotkania i archiwum (bez względu na polskie znaki) |
| Cofnij | Ctrl+Z |
| Zaległe | „⟲ ZALEGŁE: n → DZIŚ” (albo automatycznie, w ustawieniach) |
| Eksport | ⋯ → Eksportuj widoczny okres do CSV: dni z oznaczeniami, liczba zadań, zrobione, godziny, spotkania, kategorie i lista zadań |
| Ukryj tablicę | ikona — w prawym górnym rogu albo menu zasobnika. Wraca z menu zasobnika albo skrótem Ctrl+Shift+Spacja |
| Język | Ustawienia → Ogólne → Polski / English (po ponownym uruchomieniu) |
| Wyczyść dane | Ustawienia → Konta → Wyczyść dane: wszystkie zadania, spotkania, alarmy albo oznaczenia dni konta (z potwierdzeniem) |

## Alarmy i spotkania

Alarm to przypomnienie o konkretnej godzinie, osobne od zadań; spotkanie ma godziny od–do i opcjonalne przypomnienie. Widać je w dniu i w karcie Dziś. O czasie w prawym dolnym rogu pojawia się powiadomienie z dźwiękiem: OK albo drzemka +5 min, +15 min, +1 h. Nie zabiera klawiatury, a dźwięk cichnie po minucie (można go wyłączyć w Ustawienia → Ogólne).

Synchronizują się razem z kontem, więc dzwonią na każdym komputerze z TaskWall, dla obu kont. Przegapione przez uśpienie albo wyłączony komputer odezwą się po powrocie, jeśli spóźnienie nie przekracza 12 godzin.

## Konta i synchronizacja

Ustawienia → Konta: nazwa, folder danych (przyciski „Google Drive” i „OneDrive”), kalendarze Google, Notion, kategorie i oznaczenia dni, osobno dla każdego konta. Na każdym komputerze ustaw **ten sam** folder. Google Drive wymaga programu *Google Drive for desktop*.

- Każde zadanie, alarm i oznaczenie dnia ma znacznik czasu. Przy konflikcie wygrywa nowsza wersja **pojedynczego elementu**, a nie całego pliku. Usunięcia nie wracają.
- Kopie konfliktowe tworzone przez klienta chmury są scalane i przenoszone do `backup\`.
- Raz dziennie powstaje kopia w `backup\RRRR-MM-DD.json` (14 ostatnich).
- Gdy folder w chmurze jest chwilowo niedostępny, dane zapisują się lokalnie i są scalane, gdy wróci.

Ustawienia są per komputer (`%APPDATA%\TaskWall\settings.json`). Tajne adresy kalendarzy i token Notion zostają tylko tam.

## Kalendarz Google

Ustawienia → Konta → Kalendarze Google. Własny kalendarz dodajesz przez **tajny adres w formacie iCal** (Kalendarz Google → Ustawienia → kalendarz → Integracja kalendarza). Spotkania są tylko do odczytu (prawy przycisk → „Dodaj jako zadanie [Meeting]”), a ich godziny wliczają się do sumy dnia. Dni ustawowo wolne TaskWall liczy sam.

## Notion: automatyczna synchronizacja

Ustawienia → Konta → Notion. Działa na **osobistym tokenie**, który działa jak Twoje konto: bez admina i bez udostępniania stron integracji. Tylko czyta – nic nie jest zmieniane w Notion.

1. Utwórz token: [notion.so/developers/tokens](https://www.notion.so/developers/tokens) → New token (czy członek workspace'u może, zależy od planu i ustawień firmy).
2. Wklej **link do widoku z filtrami** (kliknij nazwę widoku nad tabelą → *Copy link to view*) i token.
3. „Synchronizuj teraz”, potem automatycznie co 5–60 min.

Z linkiem do widoku TaskWall wczytuje tylko strony pasujące do **filtrów i sortowania tego widoku** (np. „Dla mnie”: przypisane do mnie, wybrane statusy) – „Me” Notion liczy jako właściciela tokenu. Limit bezpieczeństwa (domyślnie 500 stron) blokuje synchronizację, która zalałaby backlog całą bazą zespołu. Nowe strony trafiają do backlogu z linkiem, znane dostają aktualny tytuł, status, priorytet i estymację, a zakończone w Notion mogą odhaczać zadanie.

Token jest zaszyfrowany dla Twojego konta Windows (DPAPI) i zostaje na tym komputerze. Na drugim komputerze wklej ten sam token (Notion pokazuje go tylko raz – zachowaj go w menedżerze haseł) albo utwórz drugi; każdy da się osobno unieważnić.

## Import z Notion (CSV)

1. W Notion: `•••` przy widoku bazy → **Export** → *Markdown & CSV*.
2. W TaskWall: **Import z Notion** w backlogu (albo przeciągnij plik na tablicę), wybierz `.csv` albo cały `.zip`.
3. Zaznacz, co wczytać. Kolumny (nazwa, status, priorytet, estymacja, link) są wykrywane same.

**Linki do stron:** CSV z Notion nie zawiera adresów stron. Dodaj w bazie właściwość typu **Formula**:
```
"https://www.notion.so/" + replaceAll(id(), "-", "")
```
Albo eksportuj z opcją *Include subpages* i wczytaj cały ZIP.

## Dla dewelopera

Przełączniki są w `src/Dev.cs`:

- `--data <folder>`: osobne dane i ustawienia testowe (`.dev\settings.dev.json`), działa obok zainstalowanej kopii, nie rusza autostartu
- `--topmost --shot <dir> [--shot-delay s] --exit`: zrzuty okien
- `--open day|month|year|drawer|archive|calendar|settings|today|add|private|alarm|meeting|repeat-mark|ring|ring-soon|search=…|import=…`
- `--selftest <plik>`: testy warstwy danych (64)

Teksty interfejsu to polskie klucze w `L.T(...)` / `L.F(...)`, angielskie tłumaczenia w `src/Lang/En.*.cs`.

## Licencja

[GPL-3.0](LICENSE)
