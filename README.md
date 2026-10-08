# TaskWall

A task board that lives on the Windows desktop: day, week, two weeks, month or year, a clock with a calendar and frosted glass made from your blurred wallpaper – in the spirit of [Todowall](https://github.com/Dragonify73/Todowall), written from scratch.

On top of that: two accounts (Work / Private) synced through a cloud folder, a backlog fed from Notion (automatic sync or CSV import), alarms and meetings with reminders, Google Calendar meetings and holidays, day marks (e.g. home office), repeating tasks and marks, checklists, smart add, search, estimates, categories, archive, undo, CSV export. The UI is in Polish or English.

*Polska wersja: [README.pl.md](README.pl.md).* Formerly called DeskWall – the TaskWall installer removes the old version and moves its settings over; data folders stay where they are.

## Install

Download `TaskWall-Setup.exe` from [Releases](https://github.com/Falcrum/TaskWall/releases) and run it. It installs for the current user, no admin rights and no .NET needed:

- the program goes to `%LOCALAPPDATA%\Programs\TaskWall`,
- a Start-menu shortcut and an entry in *Settings → Apps* are added (uninstall from there, from TaskWall's Settings → Info, or with `TaskWall.exe --uninstall`).

The uninstaller asks separately whether to delete this computer's settings and whether to delete all saved data.

## Build

```powershell
.\build.ps1           # dist\TaskWall.exe (needs the .NET 9 Desktop Runtime) + dist\TaskWall-Setup.exe (self-contained)
.\build.ps1 -NoSetup  # dist\TaskWall.exe only
```

TaskWall has no taskbar button. Use the tray icon (left click: the *Today* card, right click: menu), the buttons in the board's top bar, or right-click the top bar / clock for settings.

## Using it

| What | How |
|---|---|
| Accounts | WORK / PRIVATE in the middle of the top bar, or Ctrl+1 / Ctrl+2. Each account has its own folder, calendars, Notion link, categories, day marks, archive and alarms |
| Views | DAY · 1 WEEK · 2 WEEKS · MONTH · YEAR (remembered). ‹ › moves by a day, week, month or year |
| Add a task | "+ add task" under a day. Enter adds and opens the next field, Esc closes. Clicking a category under the field inserts e.g. `[ART]` |
| Smart add | dates at the start or end: `tomorrow`, `fri`, `on fri`, `in 3 days`, `next week`, `14.10` (Polish: `jutro`, `pt`, `za 3 dni`); `2h` / `30 min` = estimate; `#art` = category. A preview shows what was recognised |
| From anywhere | **Ctrl+Shift+Space** brings the board up with a box for today. Left-click the tray icon for the *Today* card with tasks, meetings and alarms |
| Alarm | clock icon in the top bar, right-click a day → Add alarm (on that day), "+ ALARM" in the Today card, or the tray menu. Time: `15:30`, `9`, `in 20 min`. Repeat: daily, workdays, weekly, every 2 weeks, monthly |
| Meeting | click the Meeting category under a new task, or type `[Meeting] 14-15:30 Sprint` (also `#meeting at 10 30 min`). The window has title, from–to, reminder and repeat. Meeting hours count towards the day's total |
| Done | click the circle. The ring colour is the Notion priority |
| Edit | click the text (Notion tasks: right click → Edit text) |
| Checklist | right click → Add checklist. The "☑ 2/5" chip expands it |
| Move | drag to a day, within a list, onto BACKLOG or ARCHIVE. **Ctrl** makes a copy |
| Drag in | a link from the browser or Notion (becomes a linked task; "(9+)" and "\| Notion" are stripped, `[VFX]` is recognised), selected text (one task per line), a Notion CSV/ZIP (import) |
| Estimate | right click → Estimate. The day's corner shows done/total, the hours below it (orange above 8 h) |
| Day marks | right-click a day. Per-account list in Settings → Accounts (e.g. HO/BŚU at work, your own on Private). "Repeat mark…": e.g. home office every Friday or a holiday from–to |
| Repeating tasks | right click → Repeat: quick patterns or "Repeat…": every N days/weeks/months/years, chosen weekdays, a period from–to. ✕ on an occurrence skips just that day |
| Clear | CLEAR in the top bar moves finished tasks of the visible period and the backlog to the archive |
| Archive | ✕ on hover, middle click, CLEAR, or drop on ARCHIVE. In the panel: ↺ restore, 🗑 delete for good |
| Search | Ctrl+F: tasks, series, meetings and archive (accent-insensitive) |
| Undo | Ctrl+Z |
| Overdue | "⟲ OVERDUE: n → TODAY" (or automatically, in settings) |
| Export | ⋯ → Export the visible period to CSV: days with marks, task counts, done, hours, meetings, category summary and the task list |
| Hide the board | the — icon in the top-right corner or the tray menu. It comes back from the tray menu or with Ctrl+Shift+Space |
| Language | Settings → General → Polski / English (after a restart) |
| Clear data | Settings → Accounts → Clear data: all tasks, meetings, alarms or day marks of an account (with confirmation) |

## Alarms and meetings

An alarm is a reminder at a given time, separate from tasks; a meeting has a from–to time and an optional reminder. Both show up in the day and in the Today card. When it's time, a notification with a sound appears in the bottom-right corner: OK or snooze +5 min, +15 min, +1 h. It doesn't steal the keyboard and the sound stops after a minute (it can be turned off in Settings → General).

They sync with the account, so they ring on every computer running TaskWall – for both accounts, whichever one is open. One missed because the PC was asleep or off rings when it's back, if it's less than 12 hours late.

## Accounts and sync

Settings → Accounts: name, data folder ("Google Drive" / "OneDrive" shortcuts), Google calendars, Notion, categories and day marks – separately for each account. Use **the same** folder on every computer. Google Drive needs *Google Drive for desktop*.

- Every task, alarm and day mark carries a timestamp. On a conflict the newer version of **that item** wins, not the whole file. Deletions don't come back.
- Conflict copies made by the cloud client are merged and moved to `backup\`.
- A daily copy is kept in `backup\YYYY-MM-DD.json` (last 14).
- If the cloud folder is temporarily unavailable, data is saved locally and merged back when it returns.

Settings are per computer (`%APPDATA%\TaskWall\settings.json`). Secret calendar addresses and the Notion token stay there only.

## Google Calendar

Settings → Accounts → Google calendars. Add your calendar with its **secret address in iCal format** (Google Calendar → Settings → the calendar → Integrate calendar). Meetings are read-only (right click → "Add as a [Meeting] task") and their hours count towards the day. Polish public holidays are computed locally.

## Notion: automatic sync

Settings → Accounts → Notion. It uses a **personal access token**, which acts as your own account: no workspace admin, no integration to share pages with. It only reads – nothing is ever changed in Notion.

1. Create a token at [notion.so/developers/tokens](https://www.notion.so/developers/tokens) → New token (whether members may create one depends on the workspace plan and settings).
2. Paste the **link to a filtered view** of the database (click the view name above the table → *Copy link to view*) and the token.
3. "Sync now", then automatically every 5–60 min.

With a view link TaskWall loads only the pages matching **that view's filters and sorting** (e.g. "assigned to me, status To do / In progress") – Notion evaluates "Me" as the token's user. A safety limit (default 500 pages) refuses a sync that would flood the backlog with a whole team database. New pages go to the backlog with a link; known ones get the current title, status, priority and estimate; pages finished in Notion can tick their task off.

The token is encrypted for your Windows account (DPAPI) and stays on this computer. On a second computer paste the same token (Notion shows it only once – keep it in a password manager) or create another one; each can be revoked separately.

## Notion import (CSV)

1. In Notion: `•••` on the database view → **Export** → *Markdown & CSV*.
2. In TaskWall: **Import from Notion** in the backlog (or drop the file on the board), pick the `.csv` or the whole `.zip`.
3. Choose what to load. Columns (name, status, priority, estimate, link) are detected automatically.

**Page links:** Notion's CSV has no page addresses. Add a **Formula** property to the database:
```
"https://www.notion.so/" + replaceAll(id(), "-", "")
```
or export with *Include subpages* and load the whole ZIP.

## For developers

Switches are in `src/Dev.cs`:

- `--data <folder>`: separate test data and settings (`.dev\settings.dev.json`); runs next to the installed copy and never touches autostart
- `--topmost --shot <dir> [--shot-delay s] --exit`: window screenshots
- `--open day|month|year|drawer|archive|calendar|settings|today|add|private|alarm|meeting|repeat-mark|ring|ring-soon|search=…|import=…`
- `--selftest <file>`: data-layer tests (64)

UI texts are Polish keys wrapped in `L.T(...)` / `L.F(...)`; English lives in `src/Lang/En.*.cs`.

## License

[GPL-3.0](LICENSE)
