# DeskWall

Tablica zadań na pulpicie Windows: tydzień, dwa tygodnie, miesiąc albo rok, zegar z kalendarzem i matowe szkło z rozmytej tapety, w stylu [Todowall](https://github.com/Dragonify73/Todowall).
Kod jest napisany od zera. Względem pierwowzoru dochodzą: dwa konta (Praca / Prywatne) z synchronizacją przez folder w chmurze, backlog z importem z Notion, alarmy, spotkania i święta z Kalendarza Google, oznaczenia dni HO/BŚU, zadania cykliczne, checklisty, wyszukiwarka, estymacje, kategorie, archiwum i cofanie.

## Instalacja

`dist\DeskWall-Setup.exe` instaluje program dla bieżącego użytkownika, bez uprawnień administratora:

- program trafia do `%LOCALAPPDATA%\Programs\DeskWall`,
- skrót pojawia się w menu Start, a wpis w *Ustawienia → Aplikacje* (stamtąd też odinstalujesz),
- instalator nie wymaga zainstalowanego .NET.

Odinstalowanie: *Aplikacje i funkcje → DeskWall → Odinstaluj*, Ustawienia → Instalacja albo `DeskWall.exe --uninstall`. Dane w folderach w chmurze zostają nietknięte.

## Budowanie

```powershell
.\build.ps1           # dist\DeskWall.exe (wymaga .NET 9 Desktop Runtime) + dist\DeskWall-Setup.exe
.\build.ps1 -NoSetup  # tylko dist\DeskWall.exe
```

Aplikacja nie ma okna na pasku zadań. Steruje się nią ikoną w zasobniku (lewy przycisk otwiera kartę „Dziś”, prawy menu), przyciskami na górnym pasku tablicy albo prawym przyciskiem na pasku tablicy lub zegarze.

## Obsługa

| Akcja | Jak |
|---|---|
| Konta | PRACA / PRYWATNE na środku górnego paska albo Ctrl+1 / Ctrl+2. Każde konto ma własny folder, kalendarze, kategorie, archiwum i alarmy |
| Widoki | 1 TYDZIEŃ · 2 TYGODNIE · MIESIĄC · ROK (zapamiętywany) |
| Dodaj zadanie | „+ dodaj zadanie” pod dniem. Enter dodaje i otwiera kolejne pole, Esc zamyka. Kliknięcie kategorii pod polem wstawia np. `[ART]` |
| Sprytne dodawanie | `jutro`, `pt`, `w pt`, `za 3 dni`, `14.10` na początku albo na końcu, `2h` / `30 min` (estymacja), `#art` (kategoria). Podgląd pokazuje, co zostało rozpoznane |
| Szybko z dowolnego miejsca | **Ctrl+Shift+Spacja**: tablica wyskakuje z polem na dziś. Lewy klik na ikonie w zasobniku: karta „Dziś” z zadaniami, spotkaniami i alarmami |
| Alarm | ikona zegara na górnym pasku, prawy przycisk na dniu → Dodaj alarm, „+ ALARM” w karcie Dziś albo menu zasobnika. Godzina: `15:30`, `9`, `za 20 min`. Jednorazowo, codziennie, w dni robocze, co tydzień |
| Zrobione | kliknij kółko. Kolor obwódki to priorytet z Notion |
| Edytuj | kliknij tekst (dla zadań z Notion: prawy przycisk → Edytuj tekst) |
| Checklista | prawy przycisk → Dodaj punkt checklisty. Chip „☑ 2/5” rozwija listę |
| Przenieś | przeciągnij na dzień, w inne miejsce listy, na BACKLOG albo ARCHIWUM. **Ctrl** tworzy kopię |
| Przeciągnij z zewnątrz | link z przeglądarki lub Notion (staje się zadaniem z linkiem), zaznaczony tekst (każda linia to zadanie), plik CSV/ZIP z Notion (import) |
| Estymacja | prawy przycisk → Estymacja. W rogu dnia jest licznik zrobionych, pod nim suma godzin (powyżej 8h na pomarańczowo) |
| HO / BŚU | prawy przycisk na pustym miejscu dnia. Liczniki na górnym pasku i przy miesiącach w widoku roku, „EKSPORT CSV” zapisuje zestawienie |
| Kategorie | lista z kolorami w Ustawienia → Konta. Prawy przycisk → Kategoria |
| Zadania cykliczne | prawy przycisk → Powtarzaj. ✕ na wystąpieniu pomija tylko ten dzień |
| Archiwum | ✕ po najechaniu, środkowy przycisk, „WYCZYŚĆ ZROBIONE” albo przeciągnięcie na ARCHIWUM. W panelu: ↺ przywróć, 🗑 usuń całkowicie |
| Szukaj | Ctrl+F: zadania, serie, spotkania i archiwum (bez względu na polskie znaki) |
| Cofnij | Ctrl+Z |
| Zaległe | „⟲ ZALEGŁE: n → DZIŚ” (albo automatycznie, w ustawieniach) |

## Alarmy

Alarm to przypomnienie o konkretnej godzinie, osobne od zadań. Widać go w dniu (pomarańczowy wiersz z godziną) i w karcie Dziś. O czasie w prawym dolnym rogu pojawia się powiadomienie z dźwiękiem: OK albo drzemka +5 min, +15 min, +1 h. Powiadomienie nie zabiera klawiatury, a dźwięk cichnie po minucie (można go wyłączyć w Ustawienia → Ogólne).

Alarmy synchronizują się razem z kontem, więc dzwonią na każdym komputerze z DeskWall. Dzwonią alarmy obu kont, niezależnie od tego, które jest otwarte. Alarm przegapiony przez uśpienie albo wyłączony komputer odezwie się po powrocie, jeśli spóźnienie nie przekracza 12 godzin.

## Konta i synchronizacja

Ustawienia → Konta: nazwa, folder danych (przyciski „Google Drive” i „OneDrive”), kalendarze Google i kategorie, osobno dla każdego konta. Na każdym komputerze ustaw **ten sam** folder. Google Drive wymaga programu *Google Drive for desktop*.

- Każde zadanie, alarm i oznaczenie dnia ma znacznik czasu. Przy konflikcie wygrywa nowsza wersja **pojedynczego elementu**, a nie całego pliku. Usunięcia nie wracają.
- Kopie konfliktowe tworzone przez klienta chmury są scalane i przenoszone do `backup\`.
- Raz dziennie powstaje kopia w `backup\RRRR-MM-DD.json`. Trzymanych jest 14 ostatnich.
- Gdy folder w chmurze jest chwilowo niedostępny, dane zapisują się lokalnie i są scalane, gdy wróci.

Ustawienia są per komputer (`%APPDATA%\DeskWall\settings.json`). Tajne adresy kalendarzy zostają tylko tam.

## Kalendarz Google

Ustawienia → Konta → Kalendarze. Własny kalendarz dodajesz przez **tajny adres w formacie iCal** (Kalendarz Google → Ustawienia → kalendarz → Integracja kalendarza). Spotkania są tylko do odczytu (prawy przycisk → „Dodaj jako zadanie [Meeting]”), a ich godziny wliczają się do sumy dnia. Dni ustawowo wolne DeskWall liczy sam.

## Import z Notion

1. W Notion: `•••` przy widoku bazy → **Export** → *Markdown & CSV*.
2. W DeskWall: **Import z Notion** w backlogu (albo przeciągnij plik na tablicę), wybierz `.csv` albo cały `.zip`.
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
- `--open year|month|drawer|archive|calendar|settings|today|add|private|alarm|ring|ring-soon|search=…|import=…`
- `--selftest <plik>`: testy warstwy danych (48)
