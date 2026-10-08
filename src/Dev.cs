using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace DeskWall;

/// <summary>
/// Developer switches (not needed for normal use):
///   --data &lt;folder&gt;     use this data folder for this run only
///   --topmost            keep windows above everything (for screenshots)
///   --open settings | --open import=&lt;file&gt;
///   --shot &lt;dir&gt; [--exit] capture every app window to PNG after start-up
/// </summary>
static class Dev
{
    static readonly string[] Args = Environment.GetCommandLineArgs();

    public static string? Arg(string name)
    {
        int i = Array.IndexOf(Args, name);
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    public static bool Has(string name) => Args.Contains(name);

    public static void AfterStartup()
    {
        if (Arg("--ics") is { } ics)
            CalendarService.AddTestEvents(Ics.Parse(File.ReadAllText(ics), "Test", "events", DateTime.Today.AddYears(-1), DateTime.Today.AddYears(1)));

        var open = Arg("--open");
        if (open == "settings") App.Instance.ShowSettings();
        else if (open == "archive") App.Current.Dispatcher.BeginInvoke(() => App.Board?.ShowArchive(), DispatcherPriority.ApplicationIdle);
        else if (open == "private") App.Instance.SwitchLayer("private");
        else if (open == "add") App.Current.Dispatcher.BeginInvoke(() => App.Board?.QuickAdd(), DispatcherPriority.ApplicationIdle);
        else if (open == "today") App.Current.Dispatcher.BeginInvoke(() => App.Instance.ToggleToday(), DispatcherPriority.ApplicationIdle);
        else if (open == "month") App.Current.Dispatcher.BeginInvoke(() => App.Board?.ShowMonth(), DispatcherPriority.ApplicationIdle);
        else if (open?.StartsWith("search=") == true) App.Current.Dispatcher.BeginInvoke(() => App.Board?.ShowSearch(open["search=".Length..]), DispatcherPriority.ApplicationIdle);
        else if (open == "year") App.Board?.ShowYear();
        else if (open == "alarm") App.Current.Dispatcher.BeginInvoke(() => AlarmWindow.Edit(App.Store, null, DateTime.Today.AddDays(1)), DispatcherPriority.ApplicationIdle);
        else if (open == "ring-soon") // a real alarm for the next full minute (goes through the timer)
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                var at = DateTime.Now.AddMinutes(1);
                App.Store.AddAlarm(new Alarm { Text = "Test alarmu", Day = at.ToString("yyyy-MM-dd"), Time = at.ToString("HH:mm") });
                AlarmService.Reschedule();
            }, DispatcherPriority.ApplicationIdle);
        else if (open == "ring")App.Current.Dispatcher.BeginInvoke(() => AlarmToast.Ring(new Alarm { Text = "Telefon do Ani w sprawie budżetu", Day = DateTime.Today.ToString("yyyy-MM-dd"), Time = DateTime.Now.ToString("HH:mm") }, App.Settings.Layer, DateTime.Now.AddMinutes(-7)), DispatcherPriority.ApplicationIdle);
        else if (open == "drawer") App.Current.Dispatcher.BeginInvoke(() => App.Board?.RevealBacklog(), DispatcherPriority.ApplicationIdle);
        else if (open == "calendar") App.Current.Dispatcher.BeginInvoke(() => App.Clock?.Open(), DispatcherPriority.ApplicationIdle);
        else if (open?.StartsWith("import=") == true)
            App.Current.Dispatcher.BeginInvoke(() => App.Instance.ImportNotion(open["import=".Length..]));

        var shotDir = Arg("--shot");
        if (shotDir == null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(double.TryParse(Arg("--shot-delay"), out var sd) ? sd : 2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Directory.CreateDirectory(shotDir);
            int n = 0;
            foreach (Window w in Application.Current.Windows)
            {
                if (!w.IsVisible) continue;
                var hwnd = new WindowInteropHelper(w).Handle;
                if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 2) continue;
                SaveScreen(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Path.Combine(shotDir, $"{++n}-{w.GetType().Name}.png"));
            }
            if (Has("--exit")) Application.Current.Shutdown();
        };
        timer.Start();
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    static void SaveScreen(int x, int y, int w, int h, string file)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */);
        SelectObject(mem, old);
        var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using (var fs = File.Create(file)) enc.Save(fs);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>--selftest &lt;file&gt;: exercises the data layer in a temp folder and writes PASS/FAIL lines.</summary>
    public static void SelfTest(string outFile)
    {
        var log = new System.Collections.Generic.List<string>();
        void Check(string name, bool ok) => log.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
        var dir = Path.Combine(Path.GetTempPath(), "deskwall-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // day marks: newer wins
            var a = new BoardData(); a.Days["2026-10-06"] = new DayInfo { Mark = "HO", Modified = new DateTime(2026, 1, 1) };
            var b = new BoardData(); b.Days["2026-10-06"] = new DayInfo { Mark = "BŚU", Modified = new DateTime(2026, 1, 2) };
            Check("day marks merge (newer wins)", BoardStore.Merge(a, b).Days["2026-10-06"].Mark == "BŚU" && BoardStore.Merge(b, a).Days["2026-10-06"].Mark == "BŚU");

            var store = new BoardStore();
            store.Open(dir);
            var t = new TaskItem { Text = "test", Day = "2026-10-07" };
            store.Add(t);
            var remoteOld = BoardStore.Merge(new BoardData(), store.Data); // other PC still has the task

            // archive + restore
            store.Archive(t);
            Check("archived task stays stored but hidden", store.Data.Tasks.Count == 1 && store.Data.Tasks[0].Archived);
            store.Unarchive(t);
            Check("unarchive brings task back", !store.Data.Tasks[0].Archived);
            Check("other PC still holding the task keeps one copy", BoardStore.Merge(store.Data, remoteOld).Tasks.Count == 1);

            // legacy trash (v1-v3) becomes archived tasks
            var legacy = new BoardData { Trash = new() { new TrashEntry { Task = new TaskItem { Id = "old1", Text = "z kosza" }, Deleted = DateTime.UtcNow.AddDays(-1) } } };
            legacy.Deleted["old1"] = DateTime.UtcNow.AddDays(-1);
            File.WriteAllText(Path.Combine(dir, "board-legacy.json"), System.Text.Json.JsonSerializer.Serialize(legacy, Json.Options));
            var legacyStore = new BoardStore();
            legacyStore.Open(dir); // conflict-copy merge path reads + migrates it
            Check("legacy trash migrated to archive", legacyStore.Data.Tasks.Any(x => x.Id == "old1" && x.Archived));
            legacyStore.Data.Tasks.RemoveAll(x => x.Id == "old1");

            // recurring series
            var rule = new RecurringRule { Text = "Planowanie sprintu", Pattern = "weekly", Start = "2026-10-05" };
            store.AddRule(rule);
            Check("weekly rule occurs on Mondays only", rule.Occurs(new DateTime(2026, 10, 12)) && !rule.Occurs(new DateTime(2026, 10, 13)) && !rule.Occurs(new DateTime(2026, 9, 28)));
            var v = store.VirtualTasks(new DateTime(2026, 10, 12)).ToList();
            Check("virtual occurrence generated", v.Count == 1 && v[0].IsVirtual && v[0].Text == "Planowanie sprintu");
            var real = store.Materialize(v[0]);
            real.Done = true;
            Check("materialized occurrence replaces the virtual one", !store.VirtualTasks(new DateTime(2026, 10, 12)).Any() && store.Data.Tasks.Contains(real));
            real.Day = "2026-10-13"; // moved to Tuesday: Monday must not get a second copy
            Check("moved occurrence does not reappear", !store.VirtualTasks(new DateTime(2026, 10, 12)).Any());
            rule.Skips.Add("2026-10-19");
            Check("skipped occurrence hidden", !store.VirtualTasks(new DateTime(2026, 10, 19)).Any() && store.VirtualTasks(new DateTime(2026, 10, 26)).Any());
            var wd = new RecurringRule { Pattern = "weekdays", Start = "2026-10-01" };
            var mo = new RecurringRule { Pattern = "monthly", Start = "2026-01-31" };
            var bi = new RecurringRule { Pattern = "biweekly", Start = "2026-10-05" };
            Check("weekdays / monthly (31 → 28 Feb) / biweekly",
                wd.Occurs(new DateTime(2026, 10, 9)) && !wd.Occurs(new DateTime(2026, 10, 10))
                && mo.Occurs(new DateTime(2026, 2, 28)) && mo.Occurs(new DateTime(2026, 3, 31))
                && bi.Occurs(new DateTime(2026, 10, 19)) && !bi.Occurs(new DateTime(2026, 10, 12)));
            var rA = new BoardData { Rules = { new RecurringRule { Id = "r", Text = "A", Modified = new DateTime(2026, 1, 1) } } };
            var rB = new BoardData { Rules = { new RecurringRule { Id = "r", Text = "B", Modified = new DateTime(2026, 1, 2) } } };
            Check("rules merge (newer wins)", BoardStore.Merge(rA, rB).Rules.Single().Text == "B");
            store.Data.Rules.Clear();
            store.Data.Tasks.RemoveAll(x => x.RuleId != null);

            // review fixes
            var live = new TaskItem { Id = "L", Text = "lokalne", Modified = new DateTime(2026, 1, 1) };
            var mine = new BoardData { Tasks = { live } };
            var theirs = new BoardData { Tasks = { new TaskItem { Id = "L", Text = "zdalne", Modified = new DateTime(2026, 1, 2) } } };
            var mergedLive = BoardStore.Merge(mine, theirs);
            Check("merge keeps the local instance (UI references stay valid)", ReferenceEquals(mergedLive.Tasks.Single(), live) && live.Text == "zdalne");
            var occRule = new RecurringRule { Text = "Standup", Pattern = "daily", Start = "2026-10-01" };
            store.AddRule(occRule);
            var occ = store.Materialize(store.VirtualTasks(new DateTime(2026, 10, 2)).Single());
            Check("materialized id is deterministic", occ.Id == $"r:{occRule.Id}:2026-10-02");
            store.Remove(occ);
            Check("deleting an occurrence does not bring the virtual one back", !store.VirtualTasks(new DateTime(2026, 10, 2)).Any());
            store.Data.Rules.Clear();
            File.WriteAllText(Path.Combine(dir, "board — kopia.json"), "{\"Tasks\":[{\"Id\":\"zzz\",\"Text\":\"kopia\"}]}");
            var copyStore = new BoardStore();
            copyStore.Open(dir);
            Check("a user's own 'board — kopia.json' is not treated as a conflict copy", File.Exists(Path.Combine(dir, "board — kopia.json")) && !copyStore.Data.Tasks.Any(x => x.Id == "zzz"));
            var stamp = DateTime.UtcNow.AddMinutes(5); // another PC's clock is ahead
            var skewed = new TaskItem { Modified = stamp };
            skewed.Touch();
            Check("timestamps never go backwards (clock skew)", skewed.Modified > stamp);
            var lastWorkday = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nDTSTART:20261030T090000Z\r\nDTEND:20261030T093000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3\r\n"
                            + "SUMMARY:Raport\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nSUMMARY:alarm\r\nDURATION:PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var lw = Ics.Parse(lastWorkday, "t", "events", new DateTime(2026, 1, 1), new DateTime(2027, 12, 31)).OrderBy(e => e.Start).ToList();
            Check("iCal: BYSETPOS=-1 (last working day) + VALARM ignored",
                lw.Count == 3 && lw.All(e => e.Title == "Raport") && lw[1].Start.Date == new DateTime(2026, 11, 30) && lw[2].Start.Date == new DateTime(2026, 12, 31));
            Check("hours: 1:30", NotionImport.ParseHours("1:30") == 1.5);

            // smart add (Wednesday 7 Oct 2026 as "today")
            var wed = new DateTime(2026, 10, 7);
            var cats = Category.DefaultsFor("work");
            var sa = SmartAdd.Parse("jutro Review enviro #art 2h", cats, wed);
            Check("smart add: jutro + #art + 2h", sa.Day == wed.AddDays(1) && sa.Text == "[ART] Review enviro" && sa.Estimate == 2);
            var sb = SmartAdd.Parse("Build na piątek pt", cats, wed);
            Check("smart add: weekday at the end, not in the middle", sb.Day == new DateTime(2026, 10, 9) && sb.Text == "Build na piątek");
            var sc = SmartAdd.Parse("12.10 Sprint planning 1,5h #meeting", cats, wed);
            Check("smart add: date dd.MM + 1,5h + existing category spelling", sc.Day == new DateTime(2026, 10, 12) && sc.Estimate == 1.5 && sc.Text == "[Meeting] Sprint planning");
            var sd = SmartAdd.Parse("za 3 dni oddać raport 30 min", cats, wed);
            Check("smart add: za 3 dni + 30 min", sd.Day == wed.AddDays(3) && sd.Estimate == 0.5 && sd.Text == "oddać raport");
            var se = SmartAdd.Parse("jutro", cats, wed);
            Check("smart add: a lone date word stays a task", se.Day == null && se.Text == "jutro");

            // alarms
            var noon = new DateTime(2026, 10, 7, 12, 0, 30); // Wednesday
            var once = new Alarm { Text = "telefon", Day = "2026-10-07", Time = "12:00", Armed = noon.AddHours(-2) };
            Check("alarm: rings at its minute, then not again", AlarmService.Due(once, noon) == noon.Date.AddHours(12)
                && AlarmService.Due(new Alarm { Day = "2026-10-07", Time = "12:00", Armed = noon.AddHours(-2), Rang = "2026-10-07 12:00" }, noon) == null);
            Check("alarm: set after its time doesn't ring as missed, set during the same minute does",
                AlarmService.Due(new Alarm { Day = "2026-10-07", Time = "11:00", Armed = noon.AddMinutes(-30) }, noon) == null
                && AlarmService.Due(new Alarm { Day = "2026-10-07", Time = "12:00", Armed = noon }, noon) != null);
            Check("alarm: missed by > 12 h is dropped", AlarmService.Due(once, noon.AddHours(13)) == null && AlarmService.Due(once, noon.AddHours(11)) != null);
            var weekdays = new Alarm { Day = "2026-10-01", Time = "08:30", Repeat = "weekdays", Armed = new DateTime(2026, 10, 1) };
            Check("alarm: weekdays skip the weekend", weekdays.Next(new DateTime(2026, 10, 9, 9, 0, 0)) == new DateTime(2026, 10, 12, 8, 30, 0));
            Check("alarm: time parsing", AlarmService.ParseTime("15:30").Time == new TimeSpan(15, 30, 0) && AlarmService.ParseTime("930").Time == new TimeSpan(9, 30, 0)
                && AlarmService.ParseTime("o 7").Time == new TimeSpan(7, 0, 0) && AlarmService.ParseTime("za 20 min").Delay == TimeSpan.FromMinutes(20)
                && AlarmService.ParseTime("za 2 h").Delay == TimeSpan.FromHours(2) && AlarmService.ParseTime("25:00").Time == null);
            var alA = new Alarm { Id = "al", Text = "A", Day = "2026-10-07", Time = "12:00", Modified = new DateTime(2026, 10, 1), Rang = "2026-10-07 12:00" };
            var alB = new Alarm { Id = "al", Text = "B", Day = "2026-10-07", Time = "12:00", Modified = new DateTime(2026, 10, 2) };
            var alMerged = BoardStore.Merge(new BoardData { Alarms = { alA } }, new BoardData { Alarms = { alB } }).Alarms.Single();
            Check("alarm: merge keeps the newer edit and 'already rang'", alMerged.Text == "B" && alMerged.Rang == "2026-10-07 12:00");
            Check("smart day: jutro / 14.10", SmartAdd.ParseDay("jutro", wed) == wed.AddDays(1) && SmartAdd.ParseDay("14.10", wed) == new DateTime(2026, 10, 14) && SmartAdd.ParseDay("xyz", wed) == null);

            // checklist copies are independent; Notion link title from the slug
            var withList = new TaskItem { Text = "x", Checklist = new() { new CheckItem { Text = "a" } } };
            var cloned = withList.Clone();
            cloned.Checklist![0].Done = true;
            Check("checklist is deep-copied", !withList.Checklist[0].Done);
            var link = ExternalDrop.FromLink("https://www.notion.so/awaken/Nowa-Woda-do-Tuathan-3333444455556666777788889999aaaa?pvs=4", null);
            Check("dropped Notion link → linked task with title", link.Text == "Nowa Woda do Tuathan" && link.NotionId == "3333444455556666777788889999aaaa");

            // categories
            var defaults = new BoardData { Categories = Category.DefaultsFor("work"), CategoriesModified = DateTime.MinValue.AddTicks(1) };
            var edited = new BoardData { Categories = new() { new Category { Name = "Moja", Color = "#123456" } }, CategoriesModified = DateTime.UtcNow };
            Check("edited categories beat defaults from a fresh PC (both directions)",
                BoardStore.Merge(defaults, edited).Categories!.Single().Name == "Moja" && BoardStore.Merge(edited, defaults).Categories!.Single().Name == "Moja");
            Check("toggle category prefix",
                BoardWindow.ToggleCategory("[ART] Model", "ART") == "Model"
                && BoardWindow.ToggleCategory("Model", "Meeting") == "[Meeting] Model"
                && BoardWindow.ToggleCategory("[ART][Meeting] Sync", "meeting") == "[ART] Sync");

            // holidays + iCal
            Check("Polish days off (Easter Monday 2026 = 6 Apr, Corpus Christi = 4 Jun, Wigilia 2026)",
                PolishHolidays.DayOff(new DateTime(2026, 4, 6)) != null && PolishHolidays.DayOff(new DateTime(2026, 6, 4)) != null
                && PolishHolidays.DayOff(new DateTime(2026, 12, 24)) != null && PolishHolidays.DayOff(new DateTime(2026, 10, 7)) == null);
            var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=Europe/Warsaw:20261005T100000\r\nDTEND;TZID=Europe/Warsaw:20261005T101500\r\n"
                    + "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6\r\nEXDATE;TZID=Europe/Warsaw:20261007T100000\r\nSUMMARY:Daily\\, standup\r\nEND:VEVENT\r\n"
                    + "BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID;TZID=Europe/Warsaw:20261012T100000\r\nDTSTART;TZID=Europe/Warsaw:20261012T120000\r\n"
                    + "DTEND;TZID=Europe/Warsaw:20261012T121500\r\nSUMMARY:Daily (przesunięty)\r\nEND:VEVENT\r\n"
                    + "BEGIN:VEVENT\r\nUID:b\r\nDTSTART;VALUE=DATE:20261111\r\nDTEND;VALUE=DATE:20261112\r\nSUMMARY:Urlop\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var evs = Ics.Parse(ics, "test", "events", new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
            var daily = evs.Where(e => e.Title.StartsWith("Daily")).OrderBy(e => e.Start).ToList();
            Check("iCal: weekly BYDAY + COUNT + EXDATE + moved occurrence",
                daily.Count == 5 && daily[0].Start == new DateTime(2026, 10, 5, 10, 0, 0) && daily[0].Title == "Daily, standup"
                && !daily.Any(e => e.Start.Date == new DateTime(2026, 10, 7))
                && daily.Any(e => e.Start == new DateTime(2026, 10, 12, 12, 0, 0) && e.Title == "Daily (przesunięty)")
                && !daily.Any(e => e.Start == new DateTime(2026, 10, 12, 10, 0, 0)));
            Check("iCal: all-day event", evs.Any(e => e.AllDay && e.Title == "Urlop" && e.Start == new DateTime(2026, 11, 11) && e.End == new DateTime(2026, 11, 12)));

            // undo: edit, delete, add, day mark
            var cur = store.Data.Tasks[0];
            store.Checkpoint(); cur.Text = "zmienione"; store.Changed(cur);
            store.Undo();
            Check("undo text edit", store.Data.Tasks[0].Text == "test");
            store.Checkpoint(); store.Remove(store.Data.Tasks[0]);
            store.Undo();
            Check("undo delete", store.Data.Tasks.Count == 1 && store.Data.Tasks[0].Text == "test");
            store.Checkpoint(); store.Archive(store.Data.Tasks[0]);
            store.Undo();
            Check("undo archive", !store.Data.Tasks[0].Archived);
            store.Checkpoint(); store.AddRule(new RecurringRule { Text = "x", Start = "2026-10-05" });
            store.Undo();
            Check("undo new series", store.Data.Rules.All(r => r.Deleted));
            store.Checkpoint(); store.Add(new TaskItem { Text = "nowe" });
            store.Undo();
            Check("undo add", store.Data.Tasks.Count == 1);
            store.Checkpoint(); store.SetDayMark("2026-10-08", "HO");
            store.Undo();
            Check("undo day mark", store.DayMark("2026-10-08") == null);

            // persistence + daily backup
            store.SaveNow();
            var reread = new BoardStore();
            reread.Open(dir);
            Check("save + reload keeps tasks", reread.Data.Tasks.Count == 1 && reread.Data.Tasks[0].Text == "test");
            Check("daily backup written", File.Exists(Path.Combine(dir, "backup", DateTime.Today.ToString("yyyy-MM-dd") + ".json")));

            // parsing
            Check("hours: 2 / 1,5 / 90 min / 1h 30m / 2h",
                NotionImport.ParseHours("2") == 2 && NotionImport.ParseHours("1,5") == 1.5 && NotionImport.ParseHours("90 min") == 1.5
                && NotionImport.ParseHours("1h 30m") == 1.5 && NotionImport.ParseHours("2h") == 2 && NotionImport.ParseHours("") == null);
            var tags = TaskItem.Tags("[ART][Ogrywki] Covert Camp - test", out var rest);
            Check("tags parsed", tags.Count == 2 && tags[0] == "ART" && tags[1] == "OGRYWKI" && rest == "Covert Camp - test");
        }
        catch (Exception ex) { log.Add("FAIL  exception: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch { } }
        File.WriteAllLines(outFile, log);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
}
