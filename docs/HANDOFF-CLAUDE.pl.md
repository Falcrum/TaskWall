# Przekazanie pracy nad TaskWall do nowej sesji Claude

Plik do wklejenia lub wskazania w nowej sesji Claude Code (konto firmowe), otwartej w folderze `D:\Workfiles\Claude\DeskWall`. Zawiera wszystko, co ustaliliśmy o TaskWall w sesji, która omyłkowo dotyczyła też Shader Studio. Szczegółowy plan jest w [PLAN-DOCK.pl.md](PLAN-DOCK.pl.md).

## 1. Prompt startowy (skopiuj do nowej sesji)

```
Kontynuujemy pracę nad TaskWall (ten folder). Przeczytaj najpierw docs/HANDOFF-CLAUDE.pl.md i docs/PLAN-DOCK.pl.md – są tam ustalenia z poprzedniej sesji i zatwierdzony plan.
Odpowiadaj po polsku. Zacznij od etapów 0 i 1 planu: przygotowanie (komponenty z ClockWindow, projekt testów, opcjonalne rozpoznawanie okien rustyfiles.DesktopCover) oraz pasek jako AppBar z ciągłym tłem na całą szerokość ekranu, pozycją góra/dół, wstrzymaniem przy pełnym ekranie i strażnikiem przywracającym stan po awarii.
Zanim napiszesz kod, pokaż krótko, które pliki zmienisz i jak podzielisz etap na kroki.
```

## 2. Projekt

- **Repozytorium:** `D:\Workfiles\Claude\DeskWall`, zdalne `https://github.com/Falcrum/TaskWall`. Wersja w chwili przekazania: **0.41.2** (`TaskWall.csproj`, `<Version>`).
- **Technologia:** C#, .NET 9, WPF, P/Invoke do Win32 (`src/Native.cs`).
- **Budowanie:** `.\build.ps1` (EXE i instalator `dist\TaskWall-Setup.exe`), `.\build.ps1 -NoSetup` (tylko EXE).
- **Opisy commitów:** zaczynają się od numeru wersji, po nim pełny opis zmian, np. `0.41.2: …`.
- **Pliki ważne dla planu:**
  - `src/GlassWindow.cs`: baza okien ze szkłem. `WndProc` przepisuje `WM_WINDOWPOSCHANGING` na `HWND_BOTTOM`, czyli trzyma tablicę tuż nad pulpitem. Hook `EVENT_SYSTEM_FOREGROUND` obsługuje „pokaż pulpit” (Win+D) i podgląd; za pulpit uznaje tylko okna klasy `WorkerW` i `Progman`.
  - `src/ClockWindows.cs`: `ClockWindow`, czyli dzisiejszy górny pasek wiszący u góry ekranu na szerokość tablicy: pogoda z lewej, zegar i data na środku (klik otwiera `CalendarWindow`), obciążenie z prawej.
  - `src/BoardWindow.xaml(.cs)`: tablica i jej własny pasek z widokami, datami, backlogiem i archiwum. Ten pasek zostaje w tablicy.
  - `src/SysMeters.cs`, `src/Weather.cs`, `src/Tray.cs`, `src/Watchdog.cs`, `src/Wallpaper.cs` (rozmyta tapeta do szkła), `src/Glow.cs`.
  - `src/Storage.cs`, `src/SharedSettings.cs`: synchronizacja przez folder w chmurze.

## 3. Synchronizacja (sprawdzić w biurze)

- **Jak działa:**
  - każdy komputer zapisuje tylko swój plik `devices\board-<NAZWA_KOMPUTERA>.json` oraz `settings-<NAZWA>.json`;
  - wspólny `board.json` jest wynikiem scalenia;
  - nazwa komputera to `Environment.MachineName` (`Storage.cs`, `BoardStore.Machine`);
  - folder konta Praca to `H:\Mój dysk\TaskWall` (Dysk Google). Konto Prywatne jest na OneDrive.
- **Problem:** po zmianie Windows 11 Home → Pro na komputerze w biurze zmiany z biura nie były widoczne w domu.
- **Co ustaliliśmy:**
  - na Dysku plik `board-QUESTLINE-KAMIL.json` (biuro) ostatnio zmienił się 17.11, 16:11;
  - nie było nowego pliku pod inną nazwą komputera;
  - zmiany z biura nie dotarły więc na Dysk Google. Najpewniej klient Dysku Google wylogował się albo wstrzymał synchronizację przy aktualizacji systemu;
  - TaskWall pokazuje „zsynchronizowano”, gdy zapisze plik do lokalnego folderu Dysku, i nie widzi, czy klient wysłał go do chmury.
- **Do sprawdzenia w biurze:**
  - zalogowanie i stan klienta Dysku Google;
  - litera dysku (`H:`) i folder konta w ustawieniach TaskWall;
  - data pliku `devices\board-QUESTLINE-KAMIL.json`;
  - `%APPDATA%\TaskWall\error.log`.
- **Możliwa poprawka w TaskWall:** ostrzeżenie, gdy plik innego komputera nie zmienił się od wielu dni, choć ten komputer ostatnio z nim się synchronizował (np. w podpowiedzi przy synchronizacji: „biuro: ostatni zapis 17.11”).

## 4. Konflikt z Rustyfiles (rozwiązany decyzją)

- **Objaw:** od instalacji Rustyfiles tablica znikała i wracała przy każdej próbie zmiany czegoś na niej.
- **Przyczyna:**
  - eksperymentalny pulpit Rustyfiles to własne pełnoekranowe okna z właściwością `rustyfiles.DesktopCover`, które muszą stać tuż nad pulpitem Explorera (`crates/rf-core/src/desktop/cover/zorder.rs`, `crates/rf-win/src/desktop/cover.rs`, funkcja `reinsert_if_needed`);
  - TaskWall chce tego samego miejsca (`HWND_BOTTOM` w `GlassWindow.WndProc`);
  - kliknięcie w tablicę zmienia jej położenie w kolejności okien, TaskWall spycha ją na dół pod okno Rustyfiles i tablica znika; przy następnej zmianie aktywnego okna Rustyfiles przestawia swoje okna i tablica wraca.
- **Decyzja użytkownika:** Rustyfiles zostaje jako eksplorator plików, a jego eksperymentalny pulpit i Dock zostają wyłączone. Konflikt wtedy nie występuje.
- **Opcjonalna poprawka w TaskWall** (etap 0 planu):
  - rozpoznawać okna z właściwością `rustyfiles.DesktopCover` (`GetProp`);
  - w `WndProc` stawać tuż nad najwyższym z nich zamiast `HWND_BOTTOM`;
  - w hooku traktować je jak pulpit.

## 5. Rustyfiles jako źródło wzorców

- **Źródło:** `https://github.com/gelenmedya/rustyfiles` (licencja MIT). Przeglądany commit: `243f22c` z 2026-11-18. Kod jest w Ruście, więc przenosimy pomysły i matematykę, nie kod.
- **Do przeniesienia** (ścieżki w `crates/rf-win/src/`, chyba że podano inaczej):
  - `dock/magnify.rs`, `dock/spring.rs`: powiększanie ikon i sprężyna (parametry w PLAN-DOCK, punkt 4);
  - `dock/layout.rs`: wymiary;
  - `dock/thin.rs`: tryb cienki;
  - `dock/grow.rs`: panele wyrastające z paska, otwieranie 220 ms i zamykanie 160 ms;
  - `dock/fullscreen.rs`: wykrywanie pełnego ekranu;
  - `shellhost/workarea.rs`: rezerwacja obszaru roboczego;
  - `shellhost/shellhook.rs`: zdarzenia okien bez odpytywania;
  - `tasklist.rs`: zasady listy okien jak w pasku Explorera (DWM cloaked, `WS_EX_APPWINDOW`, grupowanie po AUMID);
  - `glass/watch.rs`, `glass/rest.rs`: szkło przerysowywane tylko po zdarzeniu;
  - `toast_post.rs`: natywne powiadomienia Windows, np. dla alarmów;
  - `jumplist.rs`: jump lista TaskWall („Dodaj zadanie”, „Dzisiaj”);
  - `crates/rf-shellguard`: strażnik i wykrywanie pętli awarii;
  - `crates/rf-app/src/shell/keylayer.rs` i `winkey`: hook klawisza Win z biciem serca i zasadą „fail open”;
  - `trayhost/`: własny zasobnik. Autorzy opisują format komunikatów jako niezmierzony, więc to ostatni i najbardziej ryzykowny etap.
- **Wzorzec inżynierski:** decyzje (kolejność okien, animacja, układ) jako czyste funkcje z testami, a okna tylko je wykonują.

## 6. Ustalenia do planu

- **Pasek:**
  - jedno ciągłe tło na całą szerokość ekranu (nie trzy wyspy jak w Rustyfiles);
  - trzy sektory treści: lewy (Start, szukaj, pogoda), środkowy (dock), prawy (obciążenie, sieć, głośność, alarmy, zasobnik, zegar z kalendarzem);
  - pozycja góra albo dół ekranu w ustawieniach;
  - rejestracja jako AppBar, czyli rezerwacja pasa ekranu.
- **Dock:** przypięte i uruchomione aplikacje, animacje przy najechaniu dokładnie jak w Rustyfiles. Powiększone ikony wystają nad tło paska.
- **Pełny ekran:** wstrzymanie pogody, liczników, animacji i hooków.
- **Pasek zadań Windows:** docelowo ukryty. Najpierw własny dock, wyszukiwarka i Start, potem zasobnik (najpierw przycisk do zasobnika Windows, własny dopiero po pomiarze formatu), na końcu ukrycie paska. Zawsze ze strażnikiem przywracającym stan po awarii.
- **Następny krok:** wdrożenie etapów 0 i 1.

## 7. Notatka do pamięci nowej sesji

Do zapisania w pamięci Claude na koncie firmowym (np. poleceniem „zapamiętaj to”):

```
TaskWall (D:\Workfiles\Claude\DeskWall, github.com/Falcrum/TaskWall): pulpitowa tablica zadań w C#/.NET 9 WPF. Użytkownik pisze po polsku.
Zatwierdzony plan: docs/PLAN-DOCK.pl.md – pasek AppBar z ciągłym tłem na całą szerokość (góra/dół), trzy sektory, dock z animacjami jak w Rustyfiles, wstrzymanie przy pełnym ekranie, strażnik; pasek zadań Windows ukrywany na końcu.
Rustyfiles: używany jako eksplorator plików, eksperymentalny pulpit i Dock wyłączone (konflikt kolejności okien z TaskWall). Kontekst i ustalenia: docs/HANDOFF-CLAUDE.pl.md.
```
