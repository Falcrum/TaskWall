# Plan: pasek TaskWall jako AppBar, dock, Start i zasobnik

Plan przed wdrożeniem. W kodzie nic jeszcze nie jest zmienione.

## 1. Cel i zasady

TaskWall dostaje jeden pasek z ciągłym tłem na całą szerokość ekranu, który może zastąpić pasek zadań Windows. Dziś pogoda, zegar i obciążenie wiszą u góry ekranu w `ClockWindow`. Nowy pasek ma trzy sektory:

- **Sektor lewy:** Start, wyszukiwarka, pogoda.
- **Sektor środkowy:** dock z przypiętymi i uruchomionymi aplikacjami, z powiększaniem ikon jak w Rustyfiles.
- **Sektor prawy:** obciążenie komputera, sieć i głośność, zegar z kalendarzem, alarmy, zasobnik.

Pasek stoi u góry albo u dołu ekranu (ustawienie) i rezerwuje swój pas jako AppBar. Tablica układa się w pozostałej części ekranu.

Zasady, których trzymamy się w każdym etapie:

1. **Explorer zostaje powłoką.** TaskWall nie podmienia pulpitu ani `Shell_TrayWnd`. Najwyżej ukrywa pasek Windows i przechwytuje wybrane skróty.
2. **Wszystko, co zmienia zachowanie Windows, jest opcjonalne i domyślnie wyłączone:** ukrycie paska zadań, klawisz Win, własny zasobnik.
3. **Awaria TaskWall nie może zostawić komputera bez paska zadań.** Pilnuje tego strażnik (etap 1) i reguła „fail open” dla hooków klawiatury.
4. **Logika bez okien:** sprężyna, krzywa powiększania, układ sektorów, reguły pełnego ekranu i kolejność okien to czyste funkcje z testami. Okna tylko wykonują ich decyzje. To wzorzec z Rustyfiles (`zorder.rs`, `magnify.rs`, `spring.rs`).

## 2. Co z paskiem zadań Windows, menu Start i zasobnikiem

Pasek Windows obok paska TaskWall nie ma sensu. Docelowo, gdy dock jest włączony, pasek Windows jest ukryty. Trzy elementy, które on daje, przejmujemy tak:

| Element Windows | Co robi TaskWall | Etap |
|---|---|---|
| Lista okien i przypięte | dock w sektorze środkowym | 2 |
| Menu Start | własny panel Start; przycisk Start w docku, a klawisz Win opcjonalnie przez hook | 4 |
| Wyszukiwanie | wyszukiwarka TaskWall: aplikacje, pliki (Windows Search), ustawienia i zadania z tablicy | 3 |
| Zegar i kalendarz | już są w TaskWall | — |
| Sieć, głośność, bateria | własne wskaźniki; klik otwiera Szybkie ustawienia Windows (`ms-actioncenter:` lub Win+A) | 3 |
| Powiadomienia | dzwonek z alarmami TaskWall; klik otwiera centrum powiadomień Windows (Win+N) | 3 |
| Zasobnik (ikony programów) | **etap A:** przycisk „ikony ukryte” otwiera zasobnik Windows; **etap B:** własny zasobnik, jak `trayhost` w Rustyfiles | 5 |
| Ukrycie paska Windows | `Shell_TrayWnd` i `Shell_SecondaryTrayWnd` ukryte, obszar roboczy przeliczony, przywrócenie przy wyjściu i awarii | 6 |

**Dlaczego zasobnik jest na końcu.** Programy wysyłają ikony komunikatem `WM_COPYDATA` do okna `Shell_TrayWnd`. Własny zasobnik musi stanąć przed oknem Explorera, czytać te komunikaty i przekazywać je dalej bez zmian. Rustyfiles robi to w `trayhost/` i sam opisuje format danych jako „niezmierzony”, czyli niesprawdzony na prawdziwym Windows. To najbardziej kruchy element całego planu. Do tego czasu zasobnik Windows zostaje dostępny jednym kliknięciem.

**Kolejność ukrywania paska Windows:** dopiero gdy dock (etap 2), Start i wyszukiwarka (etapy 3–4) działają stabilnie, i zawsze razem ze strażnikiem.

## 3. Wygląd: jedno ciągłe tło, trzy sektory

Decyzja: w odróżnieniu od Rustyfiles, które ma trzy osobne wyspy, pasek TaskWall ma **jedno ciągłe tło na całą szerokość ekranu**. Na nim leżą trzy sektory treści. Tło jest jednym oknem z matowego szkła: rozmyta tapeta, odcień i poświata akcentu jak w tablicy, bez przerw między sektorami.

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ◉  🔍 Szukaj…   ☀ 7°   │        📁 🌐 🎮 ✉  ┆ 🅤 🎨 ▪ ▪        │   CPU▁▂ RAM▃   📶 🔊   🔔2  ⌃   18:42 śr 18 │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────┘
  sektor lewy                          sektor środkowy (dock)                        sektor prawy
```

- **Tło:** jedno okno na całą szerokość monitora. Wysokość 48 px, a w trybie z dockiem domyślnie 56 px (do ustawienia). Krawędź przy ekranie jest prosta, krawędź od strony tablicy ma cienką linię i delikatny cień. Opcjonalnie lekko zaokrąglone dolne (u góry: górne) narożniki.
- **Sektory:** treść, nie osobne tła.
  - Lewy i prawy są przyklejone do boków.
  - Środkowy jest wyśrodkowany na ekranie (opcja: dosunięty w lewo jak w Windows 10).
  - Między sektorami są cienkie, półprzezroczyste separatory. Pod sektorem aktywnym przy najechaniu pojawia się lekkie rozjaśnienie tła w obrębie sektora, bez osobnej kapsuły.
- **Powiększanie ikon wychodzi poza tło.** Ikony w spoczynku mieszczą się w pasku. Powiększone (do 1,55×) wystają w stronę tablicy, nad krawędź tła, jak w docku macOS. Okno paska ma więc zapas przezroczystej wysokości (`HEADROOM`), który nie jest częścią AppBara i przepuszcza kliknięcia (`WS_EX_TRANSPARENT` poza obszarem ikon albo osobne okno nakładki na czas powiększenia). Rezerwowana jest tylko wysokość tła.
- **Wymiary ikon jak w Rustyfiles (`dock/layout.rs`):** ikony w spoczynku 44 px, przy pasku 56 px (przy 48 px ikony 36 px), odstęp 10 px, margines wewnętrzny sektora 12 px.
- **Wskaźniki pod ikoną:** kropka dla uruchomionego programu, dłuższa kreska dla aktywnego, pulsowanie, gdy program prosi o uwagę (`HSHELL_FLASH`).
- **Separator** między przypiętymi a uruchomionymi, które nie są przypięte.
- **Tryb cienki** (`dock/thin.rs`): gdy jakieś okno jest zmaksymalizowane, ikony sektora środkowego zmniejszają się do 30 px. Środki ikon zostają w miejscu, więc nic obok się nie przesuwa. Pełny rozmiar wraca, gdy kursor zbliży się do paska. Tło paska nie zmienia wysokości, więc obszar roboczy się nie przelicza.
- **Pasek u dołu:** ikony rosną w górę od dolnej linii. **Pasek u góry:** wszystko jest odbite, ikony rosną w dół od górnej linii, a panele otwierają się w dół.
- **Pasek tablicy** (widoki, nawigacja dat, backlog, archiwum) zostaje w tablicy. Przenosimy tylko to, co dziś jest w `ClockWindow`: pogodę, zegar i kalendarz, obciążenie.

### Panele wyrastające z paska (`dock/grow.rs`)

Kliknięcie w zegar, Start, wyszukiwarkę albo zasobnik nie otwiera osobnego okna. Panel wyrasta z tła paska nad danym sektorem, jak wysunięty fragment tego samego szkła:
- róg panelu przy pasku stoi w miejscu (lewy dla Startu i wyszukiwarki, prawy dla zegara i zasobnika);
- otwieranie trwa 220 ms, zamykanie 160 ms, po tej samej krzywej (sprężyna bez odbicia) w obie strony;
- zawartość panelu pojawia się w drugiej połowie ruchu;
- treść paska (ikona, pole szukania, zegar) nie zmienia położenia, a panel dorasta do niej od strony tablicy;
- odwrócenie w połowie ruchu po prostu zmienia kierunek, bez skoku.

## 4. Animacja powiększenia, dokładnie jak w Rustyfiles

Źródło: `crates/rf-win/src/dock/magnify.rs` i `spring.rs` (licencja MIT). Przenosimy liczby i matematykę 1:1 do C#:

- **Dzwon:** każda ikona rośnie zależnie od poziomej odległości `d` między kursorem a środkiem ikony w spoczynku. Kształt to podniesiony cosinus `0.5·(1 + cos(π·min(|d|/R, 1)))`, gdzie `R = 2.5 × (44 + 10) = 135 px`. Wielkość zmienia się więc płynnie przy przesuwaniu kursora i nigdy skokowo.
- **Siła:** ikona pod kursorem urasta do 1,55× (ustawienie „pełne”) albo 1,25× („subtelne”). Można też wyłączyć powiększanie.
- **Układ:**
  - ikony stoją na wspólnej linii i rozpychają sąsiadów; sektor środkowy rozszerza się symetrycznie wokół środka (tło paska jest i tak na całą szerokość, więc nic poza sektorem się nie zmienia);
  - gdy brak miejsca, najpierw kurczą się odstępy (do 2 px), potem wszystkie ikony razem.
- **Wejście i wyjście kursora:** wysokość dzwonu (0→1→0) prowadzi sprężyna bez odbicia, czas 160 ms × 1,5. Dzwon gaśnie w miejscu, gdzie kursor był ostatnio.
- **Sprężyna:** liczona wzorem zamkniętym (tłumiony oscylator, parametry `duration` i `bounce` jak w SwiftUI). Animacja jest więc identyczna przy 60 i 240 Hz i po zacięciu, a zawrócenie w połowie zachowuje prędkość.
- **Zmiana kolejności przypiętych przeciąganiem:** pozostałe ikony rozsuwają się tą samą sprężyną.

**Wykonanie w WPF:**
- **Pętla klatek:** `CompositionTarget.Rendering` jest podpięty tylko na czas ruchu i odpinany, gdy sprężyna dojdzie do celu, więc w spoczynku dock nie zużywa CPU.
- **Pozycje:** ikony leżą w `Canvas` z pozycją i rozmiarem liczonym w każdej klatce (nie `ScaleTransform`, bo sąsiedzi muszą się rozsunąć).
- **Ikony:** `IShellItemImageFactory` w 256 px, buforowane w rozmiarze `44 × 1,55 × DPI`, żeby przy powiększeniu nie były rozmyte.

## 5. Dock: przypięte i uruchomione

- **Lista okien** według zasad paska Explorera (`tasklist.rs`):
  - okno widoczne i niezasłonięte przez DWM (`DWMWA_CLOAKED`), co pomija okna z innych pulpitów wirtualnych;
  - bez właściciela i nie okno pomocnicze (tool window), chyba że ma `WS_EX_APPWINDOW`.
- **Grupowanie** po AppUserModelID (`SHGetPropertyStoreForWindow`), a bez niego po pliku programu.
- **Zmiany okien:** dochodzą przez `RegisterShellHookWindow` (utworzenie, zamknięcie, aktywacja, prośba o uwagę), bez odpytywania co chwilę. Po restarcie Explorera komunikat `TaskbarCreated` wymaga ponownej rejestracji.
- **Klik:** aktywuje okno, a drugi klik je minimalizuje. Grupa z kilkoma oknami pokazuje listę miniatur. Środkowy klik otwiera nową instancję.
- **Najechanie:** podgląd na żywo przez `DwmRegisterThumbnail` w panelu wyrastającym z paska nad ikoną.
- **Prawy klik:** przypnij lub odepnij, nowe okno, zamknij wszystkie. Odczyt jump list innych programów jest odłożony na później, bo to trudniejsza część.
- **Przypięte:** lista w ustawieniach TaskWall. Każde przypięcie to ścieżka, argumenty albo AUMID dla aplikacji ze Sklepu.
- **Ograniczenie:** okna uruchomione jako administrator nie dadzą się minimalizować ani podglądać z TaskWall bez uprawnień administratora. Dock je pokazuje, a klik uruchamia `SwitchToThisWindow`.

## 6. Wstrzymywanie przy pełnym ekranie

- **AppBar dostaje od Windows `ABN_FULLSCREENAPP`,** gdy na monitorze pojawia się program pełnoekranowy. Uzupełnienie: `SHQueryUserNotificationState` (`QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_BUSY`, `QUNS_PRESENTATION_MODE`) oraz reguła z `dock/fullscreen.rs`: okno na pierwszym planie pokrywa cały monitor i nie jest pulpitem ani częścią powłoki.
- **Na czas pełnego ekranu** TaskWall:
  - zatrzymuje timery pogody, liczników i kalendarza, animacje i przerysowanie szkła;
  - zdejmuje hook klawiatury;
  - chowa pasek.

  Wznowienie następuje po wyjściu z gry, z jednym odświeżeniem danych.
- **To samo przy zablokowanym ekranie i uśpieniu** (`WTSRegisterSessionNotification`, `PBT_APMRESUMEAUTOMATIC`).

## 7. Pasek na górze albo na dole

- Ustawienie w Settings: pozycja paska (Góra / Dół) i monitor (główny, wszystkie, wybrany). Przełączenie działa od razu: `ABM_SETPOS` z `ABE_TOP` albo `ABE_BOTTOM`.
- Tablica bierze obszar roboczy po odjęciu paska, więc nic się nie nakłada. Gdy pasek jest u góry, a Windows ma swój pasek u dołu, oba AppBary rezerwują swoje pasy do czasu ukrycia paska Windows (etap 6).
- Na drugim monitorze opcjonalnie pasek tylko z sektorem środkowym i oknami z tego monitora, jak w Windows 11.

## 8. Etapy

Każdy etap kończy się działającą wersją. Etapy można wydawać po kolei.

**Etap 0: przygotowanie**
- Przenieść logikę `ClockWindow` (pogoda, zegar, liczniki) do komponentów, które da się umieścić w sektorach paska.
- Projekt testów jednostkowych dla czystej logiki (TaskWall ma dziś tylko self-test).
- Wyłączyć eksperymentalny pulpit i Dock w Rustyfiles. Opcjonalnie: TaskWall rozpoznaje okna `rustyfiles.DesktopCover` i staje nad nimi.

**Etap 1: pasek jako AppBar**
- Jedno okno paska z ciągłym tłem na całą szerokość i trzema sektorami (na razie bez docka): lewy ze Startem tylko jako przycisk i pogodą, prawy z licznikami, zegarem i kalendarzem.
- Pozycja góra lub dół, rezerwacja pasa, reakcja na zmianę DPI i monitorów.
- Wstrzymanie przy pełnym ekranie (punkt 6).
- **Strażnik:**
  - osobny proces `TaskWall.exe --guard`, który pilnuje głównego;
  - po awarii przywraca obszar roboczy, a w etapie 6 także pasek Windows;
  - trzy awarie w 10 minut sprawiają, że TaskWall startuje bez funkcji paska (pomysł z `rf-shellguard`).
- **Testy:** układ sektorów, reguły pełnego ekranu, przeliczanie obszaru roboczego dla góry i dołu.

**Etap 2: dock**
- Sektor środkowy: przypięte i uruchomione, wskaźniki, prośba o uwagę.
- Powiększenie i sprężyna 1:1 (punkt 4), przeciąganie przypiętych, tryb cienki.
- Podglądy okien na żywo, menu pod prawym klikiem.
- **Testy:** dzwon, układ z rozpychaniem, sprężyna (zgodność wyników z Rustyfiles dla tych samych danych), zasady listy okien.

**Etap 3: wyszukiwarka i wskaźniki systemowe**
- Pole szukania w lewym sektorze i panel wyników. Szuka w:
  - aplikacjach (`shell:AppsFolder` razem ze Sklepem);
  - plikach (Windows Search przez OLE DB, bez własnego indeksu);
  - stronach ustawień (`ms-settings:`);
  - zadaniach z tablicy.
- Kalkulator w polu szukania.
- Wskaźniki sieci, głośności (Core Audio) i baterii; klik otwiera Szybkie ustawienia Windows.
- Dzwonek z alarmami i dniem dzisiejszym.

**Etap 4: Start**
- Panel Start wyrastający z lewego sektora:
  - przypięte (siatka);
  - wszystkie aplikacje A–Z;
  - ostatnie pliki;
  - użytkownik oraz zasilanie: blokada, uśpienie, restart, wyłączenie.
- Opcja „klawisz Win otwiera Start TaskWall”: hook niskiego poziomu z biciem serca co 250 ms. Gdy wątek nie odpowiada, hook przepuszcza wszystkie klawisze (fail open, jak `keylayer.rs`). Przy pełnym ekranie hook jest zdjęty.
- Bez przejmowania Alt+Tab w tym planie.

**Etap 5: zasobnik**
- **A (od etapu 1):** przycisk otwierający zasobnik Windows.
- **B:** własny zasobnik:
  - okno klasy `Shell_TrayWnd` stoi przed oknem Explorera;
  - każdy komunikat jest przekazywany do Explorera bez zmian, a TaskWall zapamiętuje ikony;
  - klik w ikonę wysyła wiadomość do programu, do którego należy;
  - kolejność i ukryte ikony da się ustawić.
- Przed wdrożeniem trzeba zmierzyć format komunikatów na prawdziwym Windows 11 (narzędzie podsłuchowe). Bez tego etap nie startuje.

**Etap 6: ukrycie paska zadań Windows**
- Opcja „Ukryj pasek zadań Windows”: ukrycie `Shell_TrayWnd` i pasków dodatkowych.
- Ponowne ukrycie po restarcie Explorera (`TaskbarCreated`) i przywrócenie przy wyjściu.
- Na czas działania TaskWall ustawić autoukrywanie paska Windows, żeby oddał pas ekranu; poprzedni stan zapisać i przywrócić.
- Strażnik z etapu 1 przywraca wszystko po awarii.

## 9. Ustawienia (nowa zakładka „Pasek”)

| Ustawienie | Wartości | Domyślnie |
|---|---|---|
| Pasek | wyłączony / włączony | włączony (jak dziś górny pasek) |
| Pozycja | góra / dół | góra |
| Monitory | główny / wszystkie / wybrany | główny |
| Dock | wyłączony / włączony | wyłączony |
| Powiększanie ikon | wyłączone / subtelne / pełne | pełne |
| Tryb cienki przy zmaksymalizowanym oknie | tak / nie | tak |
| Klawisz Win otwiera Start TaskWall | tak / nie | nie |
| Zasobnik | Windows / własny | Windows |
| Ukryj pasek zadań Windows | tak / nie | nie |
| Animacje | jak w Ustawieniach (istniejące „Animations”) | — |

## 10. Testy przed każdym wydaniem

- **Monitory i DPI:** jeden i dwa monitory, 100% i 150%, odłączenie monitora w trakcie pracy.
- **Pełny ekran:** gra w trybie wyłącznym i bez ramki (borderless), wideo w przeglądarce, prezentacja.
- **Okna uruchomione jako administrator:** dock pokazuje je bez błędów.
- **Środowisko:** pulpity wirtualne, restart Explorera (`taskkill /f /im explorer.exe`), uśpienie i wznowienie, blokada ekranu, pulpit zdalny.
- **Awaria:** zabicie procesu TaskWall przywraca pasek Windows i obszar roboczy w ciągu 2 s.
- **Rustyfiles z wyłączonym pulpitem:** brak konfliktów, a Alt+Space i Ctrl+Shift+Space nie kolidują.
- **Wydajność:** w spoczynku 0% CPU i brak timerów animacji; przy pełnym ekranie zatrzymane timery.

## 11. Ryzyka

| Ryzyko | Co z tym robimy |
|---|---|
| Aktualizacja Windows zmienia pasek zadań lub zasobnik | zasobnik i ukrycie paska są opcjonalne i na końcu; strażnik przywraca stan |
| Gry z anti-cheatem nie lubią hooków klawiatury | hook tylko po włączeniu opcji, zdejmowany przy pełnym ekranie |
| Okna uruchomione jako administrator | brak sterowania; dock tylko je pokazuje |
| Wydajność WPF przy animacji | klatki tylko w czasie ruchu, ikony buforowane, bez efektów rozmycia na żywo w czasie animacji |
| Zawieszenie TaskWall z ukrytym paskiem Windows | strażnik i watchdog z wykrywaniem zawieszeń |
