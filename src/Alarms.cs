using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskWall;

/// <summary>
/// Rings alarms of every open account. No polling every second: the timer is set to the next alarm
/// (at most a minute ahead, so alarms added on another PC are picked up too). After sleep / a PC that was off,
/// an alarm missed by less than 12 hours still rings, marked as late.
/// </summary>
static class AlarmService
{
    static readonly DispatcherTimer Timer = new();
    static readonly List<(Alarm Alarm, string Layer, DateTime Due, DateTime At)> Snoozed = new();
    static readonly HashSet<string> RangTasks = new(); // task reminders shown this session
    static bool _started;

    /// <summary>Raised after alarms were added / edited / rang (open views refresh).</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Timer.Tick += (_, _) => Check();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) Application.Current.Dispatcher.BeginInvoke(Check);
        };
        Check();
    }

    /// <summary>After an edit: re-plan the timer and refresh the views.</summary>
    public static void Reschedule()
    {
        if (_started) Check();
        Changed?.Invoke();
    }

    public static IEnumerable<DateTime> SnoozedFor(Alarm a) => Snoozed.Where(s => s.Alarm.Id == a.Id).Select(s => s.Due);

    static void Check()
    {
        Timer.Stop();
        var now = DateTime.Now;
        var next = now.AddMinutes(1);
        bool rang = false;
        foreach (var (layer, store) in App.OpenStores())
            foreach (var a in store.Alarms.ToList())
            {
                if (Due(a, now) is { } at)
                {
                    store.MarkRang(a, at);
                    AlarmToast.Ring(a, layer, at);
                    rang = true;
                }
                if (a.Next(now) is { } n && n < next) next = n;
            }
        // tasks with planned hours today ("14-16"): a reminder some minutes before the start
        if (App.Settings.TaskReminder >= 0)
            foreach (var (layer, store) in App.OpenStores())
                foreach (var t in store.Data.Tasks.Where(t => t.Time != null && !t.Done && !t.Archived && t.Day == now.ToString("yyyy-MM-dd")).ToList())
                {
                    if (!TimeSpan.TryParseExact(t.Time, @"hh\:mm", null, out var tod)) continue;
                    var ringAt = now.Date + tod - TimeSpan.FromMinutes(App.Settings.TaskReminder);
                    var key = $"{t.Id}|{t.Day}|{t.Time}";
                    if (ringAt <= now && now - ringAt < TimeSpan.FromMinutes(3) && RangTasks.Add(key))
                    {
                        AlarmToast.Ring(new Alarm { Id = "task:" + t.Id, Kind = "task", Text = t.Text, Day = t.Day!, Time = t.Time!, End = t.End }, layer, now.Date + tod);
                        rang = true;
                    }
                    else if (ringAt > now && ringAt < next) next = ringAt;
                }
        foreach (var s in Snoozed.Where(s => s.Due <= now).ToList())
        {
            Snoozed.Remove(s);
            AlarmToast.Ring(s.Alarm, s.Layer, s.At, snoozed: true);
        }
        foreach (var s in Snoozed) if (s.Due < next) next = s.Due;

        var wait = next - DateTime.Now;
        if (wait < TimeSpan.FromMilliseconds(300)) wait = TimeSpan.FromMilliseconds(300);
        Timer.Interval = wait + TimeSpan.FromMilliseconds(150); // land just after the full minute
        Timer.Start();
        if (rang) Changed?.Invoke();
    }

    /// <summary>The occurrence that should ring now: not rung yet, at most 12 h late, not before the alarm was (re)set.</summary>
    public static DateTime? Due(Alarm a, DateTime now)
    {
        var armed = a.Armed.AddTicks(-(a.Armed.Ticks % TimeSpan.TicksPerMinute)); // set at 15:30:20 for 15:30 still rings
        for (int i = -1; i <= 1; i++) // tomorrow first: a reminder for a meeting just after midnight rings today
        {
            var d = now.Date.AddDays(-i);
            if (!a.Occurs(d) || a.RingAt(d) is not { } at) continue;
            if (at > now || now - at > TimeSpan.FromHours(12) || at < armed) continue;
            if (string.CompareOrdinal(Alarm.Key(at), a.Rang) <= 0) continue;
            return at;
        }
        return null;
    }

    public static void Snooze(Alarm a, string layer, DateTime at, TimeSpan delay)
    {
        Snoozed.RemoveAll(s => s.Alarm.Id == a.Id);
        Snoozed.Add((a, layer, DateTime.Now + delay, at));
        Check();
    }

    // ---------- parsing ("15:30", "9", "o 7", "za 20 min", "za 2 h"; English: "at 7", "7pm", "in 20 min", "in 2 hours") ----------

    static readonly Regex Relative = new(@"^(?:za|in)\s*(\d+(?:[.,]\d+)?)\s*(m|min|mins|minut|minuty|minutę|minute|minutes|h|hr|hrs|hour|hours|godz\.?|godzin|godziny|godzinę)?$", RegexOptions.IgnoreCase);
    static readonly Regex Clock = new(@"^(?:o\s*|at\s*|godz\.?\s*)?(\d{1,2})(?:[:.\s]?(\d{2}))?\s*(am|pm)?$", RegexOptions.IgnoreCase);

    /// <summary>Either a time of day or a delay from now; both null when the text isn't a time.</summary>
    public static (TimeSpan? Time, TimeSpan? Delay) ParseTime(string input)
    {
        var s = input.Trim();
        var r = Relative.Match(s);
        if (r.Success)
        {
            var n = double.Parse(r.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = r.Groups[2].Value.ToLowerInvariant();
            var delay = unit.StartsWith("h") || unit.StartsWith("godz") ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
            return delay > TimeSpan.Zero && delay < TimeSpan.FromDays(7) ? (null, delay) : (null, null);
        }
        var c = Clock.Match(s);
        if (c.Success)
        {
            int h = int.Parse(c.Groups[1].Value), m = c.Groups[2].Success ? int.Parse(c.Groups[2].Value) : 0;
            if (c.Groups[3].Success) // 7pm / 12am
            {
                if (h is < 1 or > 12) return (null, null);
                bool pm = c.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
                h = h % 12 + (pm ? 12 : 0);
            }
            if (h <= 23 && m <= 59) return (new TimeSpan(h, m, 0), null);
        }
        return (null, null);
    }

    static readonly Regex Range = new(@"(?<![\d:.])(?:(?<!\w)(?:od|from)\s+)?(\d{1,2})(?:[:.](\d{2}))?\s*(?:-|–|—|do|to)\s*(\d{1,2})(?:[:.](\d{2}))?(?![\d:.])", RegexOptions.IgnoreCase);
    static readonly Regex Single = new(@"(?<![\d:.])(?:(?<!\w)(?:o|od|at|from|godz\.?)\s*)?(\d{1,2})[:.](\d{2})(?![\d:.])|(?<!\w)(?:o|od|at|from|godz\.?)\s*(\d{1,2})(?![\d:.\w])", RegexOptions.IgnoreCase);

    /// <summary>
    /// "14-15:30 Sprint", "Sprint 9:30–10", "o 14 Sprint 1,5h", "Sprint from 10 to 11", "Daily at 10": time (and end) of a meeting typed as one line.
    /// <paramref name="rest"/> is the text without the times; <paramref name="duration"/> (e.g. from "1,5h") sets the end
    /// when no range was given.
    /// </summary>
    public static bool ParseMeeting(string text, double? duration, out TimeSpan start, out TimeSpan end, out string rest)
    {
        start = end = default;
        rest = text;
        static TimeSpan T(Group h, Group m) => new(int.Parse(h.Value), m.Success ? int.Parse(m.Value) : 0, 0);
        static bool Ok(Group h, Group m) => int.Parse(h.Value) <= 23 && (!m.Success || int.Parse(m.Value) <= 59);
        var r = Range.Match(text);
        if (r.Success && Ok(r.Groups[1], r.Groups[2]) && Ok(r.Groups[3], r.Groups[4]))
        {
            start = T(r.Groups[1], r.Groups[2]);
            end = T(r.Groups[3], r.Groups[4]);
            if (end <= start) end = start + TimeSpan.FromHours(1);
            rest = text.Remove(r.Index, r.Length);
        }
        else
        {
            var s = Single.Match(text);
            if (!s.Success) return false;
            if (s.Groups[1].Success) { if (!Ok(s.Groups[1], s.Groups[2])) return false; start = T(s.Groups[1], s.Groups[2]); }
            else { if (int.Parse(s.Groups[3].Value) > 23) return false; start = TimeSpan.FromHours(int.Parse(s.Groups[3].Value)); }
            end = start + TimeSpan.FromHours(duration is > 0 and <= 12 ? duration.Value : 1);
            rest = text.Remove(s.Index, s.Length);
        }
        if (end >= TimeSpan.FromDays(1)) end = new TimeSpan(23, 59, 0);
        rest = Regex.Replace(rest, @"\s{2,}", " ").Trim(' ', ',', '-', '–');
        return true;
    }

    /// <summary>Duration for a meeting's "do" field: "1h", "90 min", "+30 min", "45 mins", "2 hours".</summary>
    public static TimeSpan? ParseDuration(string input)
    {
        var m = Regex.Match(input.Trim(), @"^\+?\s*(\d+(?:[.,]\d+)?)\s*(m|min|mins|minut\w*|h|hr|hrs|hour|hours|godz\w*\.?)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value.StartsWith("godz", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
    }

    public static string Until(DateTime at)
    {
        var d = at - DateTime.Now;
        if (d.TotalMinutes < 1) return L.T("za chwilę");
        if (d.TotalHours < 1) return L.F("za {0} min", (int)Math.Ceiling(d.TotalMinutes));
        if (d.TotalHours < 10) return L.F("za {0} h {1} min", (int)d.TotalHours, (int)Math.Ceiling(d.TotalMinutes % 60) % 60);
        if (d.TotalHours < 48) return L.F("za {0} h", (int)Math.Round(d.TotalHours));
        return L.F("za {0} dni", (int)Math.Round(d.TotalDays));
    }
}

/// <summary>Notification card in the bottom-right corner (above other windows, doesn't steal the keyboard) with a looping sound.</summary>
public sealed class AlarmToast : Window
{
    static readonly List<AlarmToast> Open = new();
    static readonly Color Amber = Color.FromRgb(0xF2, 0xB1, 0x4C);
    static readonly DispatcherTimer SoundLimit = new() { Interval = TimeSpan.FromSeconds(60) };

    readonly Alarm _alarm;
    readonly string _layer;
    readonly DateTime _at;

    public static void Ring(Alarm a, string layer, DateTime at, bool snoozed = false)
    {
        // the same occurrence is never shown twice
        foreach (var t in Open.Where(t => t._alarm.Id == a.Id).ToList()) t.Close();
        var toast = new AlarmToast(a, layer, at, snoozed);
        toast.Show();
        Sound(true);
    }

    AlarmToast(Alarm a, string layer, DateTime at, bool snoozed)
    {
        _alarm = a; _layer = layer; _at = at;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        FontSize = 12.5;

        var body = new StackPanel();
        var head = new TextBlock { Foreground = new SolidColorBrush(Amber), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        head.Inlines.Add(new System.Windows.Documents.Run("  ") { FontFamily = Ui.Font("IconFont"), FontSize = 11 });
        var occurrence = a.IsMeeting ? a.At(at.AddMinutes(a.Remind ?? 0).Date) : at;
        head.Inlines.Add(new System.Windows.Documents.Run(a.IsMeeting
            ? $"{L.T("SPOTKANIE")}  {occurrence:HH:mm}–{a.EndAt(occurrence):HH:mm}  ·  {L.Up(App.AccountName(layer))}"
            : a.Kind == "task" ? $"{L.T("ZADANIE")}  {a.Time}{(a.End != null ? "–" + a.End : "")}  ·  {L.Up(App.AccountName(layer))}"
            : $"ALARM  {at:HH:mm}  ·  {L.Up(App.AccountName(layer))}"));
        if (a.IsMeeting && occurrence > DateTime.Now)
            head.Inlines.Add(new System.Windows.Documents.Run("  ·  " + AlarmService.Until(occurrence)) { Foreground = Ui.Res("FgDim") });
        var late = a.IsMeeting ? TimeSpan.Zero : DateTime.Now - at;
        if (snoozed) head.Inlines.Add(new System.Windows.Documents.Run("  ·  " + L.T("drzemka")) { Foreground = Ui.Res("FgDim") });
        else if (late.TotalMinutes >= 2)
            head.Inlines.Add(new System.Windows.Documents.Run("  ·  " + L.F("spóźniony o {0}{1} min", late.TotalHours >= 1 ? $"{(int)late.TotalHours} h " : "", late.Minutes)) { Foreground = Ui.Res("FgDim") });
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(a.Text) ? (a.IsMeeting ? L.T("Spotkanie") : "Alarm") : a.Text,
            FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        var buttons = new DockPanel();
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.Resources["PrimaryButton"], MinWidth = 70 };
        ok.Click += (_, _) => Close();
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(ok);
        var snooze = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 0, 0, 0) };
        foreach (var (label, span) in new[] { ("+5 MIN", TimeSpan.FromMinutes(5)), ("+15 MIN", TimeSpan.FromMinutes(15)), ("+1 H", TimeSpan.FromHours(1)) })
        {
            var b = new Button { Content = label, Style = (Style)Application.Current.Resources["BarButton"], ToolTip = L.T("Drzemka") };
            b.Click += (_, _) => { AlarmService.Snooze(_alarm, _layer, _at, span); Close(); };
            snooze.Children.Add(b);
        }
        buttons.Children.Add(snooze);
        body.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x1A, 0x1D, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, Amber.R, Amber.G, Amber.B)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 14, 12), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
            Child = body,
        };
        Content = card;
        Open.Add(this);
        Loaded += (_, _) => { Restack(); Anim.Enter(card, 40, 0, 320); };
        SizeChanged += (_, _) => Restack();
        Closed += (_, _) =>
        {
            Open.Remove(this);
            Restack();
            if (Open.Count == 0) Sound(false);
        };
    }

    /// <summary>Newest at the bottom, older ones above it.</summary>
    static void Restack()
    {
        var wa = SystemParameters.WorkArea;
        double bottom = wa.Bottom - 4;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            if (t.ActualHeight == 0) continue;
            t.Left = wa.Right - t.ActualWidth - 4;
            t.Top = bottom - t.ActualHeight;
            bottom = t.Top;
        }
    }

    // ---------- sound ----------

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySound(string? sound, IntPtr module, uint flags);
    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_LOOP = 0x8, SND_ALIAS = 0x10000, SND_FILENAME = 0x20000;

    static void Sound(bool on)
    {
        SoundLimit.Stop();
        if (!on || !App.Settings.AlarmSound) { PlaySound(null, IntPtr.Zero, 0); return; }
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
        bool ok = File.Exists(file) && PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_LOOP | SND_NODEFAULT);
        if (!ok) PlaySound("SystemNotification", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
        // the card stays until dismissed; the sound stops after a minute
        SoundLimit.Tick -= StopSound;
        SoundLimit.Tick += StopSound;
        SoundLimit.Start();
    }

    static void StopSound(object? s, EventArgs e) { SoundLimit.Stop(); PlaySound(null, IntPtr.Zero, 0); }
}

/// <summary>
/// Add / edit an alarm (time: "15:30", "9", "za 20 min") or a meeting (from–to: "14-15:30", reminder). The day is the
/// cell it was opened from (today from the tray / top bar); repeating entries start on that day.
/// </summary>
public sealed class AlarmWindow : DarkWindow
{
    readonly BoardStore _store;
    readonly Alarm? _existing;
    readonly bool _meeting, _fixedDay;
    readonly DateTime _day;
    readonly TextBox _text = Field(0);
    readonly TextBox _time = Field(120);
    readonly TextBox _end = Field(120);
    readonly ComboBox _remind = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _repeat = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _dayText = Label("", 15, "Fg", FontWeights.Bold);
    readonly TextBlock _preview = Label("", 12, "FgDim");
    readonly Button _save;

    static (int? Minutes, string Label)[] Reminders => new (int?, string)[]
        { (null, L.T("Bez przypomnienia")), (0, L.T("W chwili rozpoczęcia")), (5, L.F("{0} min przed", 5)), (10, L.F("{0} min przed", 10)), (15, L.F("{0} min przed", 15)), (30, L.F("{0} min przed", 30)), (60, L.T("1 h przed")) };

    static TextBox Field(double width)
    {
        var t = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"] };
        if (width > 0) { t.Width = width; t.HorizontalAlignment = HorizontalAlignment.Left; }
        return t;
    }

    AlarmWindow(BoardStore store, Alarm? existing, DateTime? day, bool meeting, string? title)
    {
        _store = store;
        _existing = existing;
        _meeting = existing?.IsMeeting ?? meeting;
        _fixedDay = day != null || existing != null;
        _day = (day ?? (existing != null && DateTime.TryParse(existing.Day, out var d) ? d : DateTime.Today)).Date;
        Title = _meeting ? (existing == null ? L.T("Nowe spotkanie") : L.T("Spotkanie")) : (existing == null ? L.T("Nowy alarm") : "Alarm");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var p = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        p.Children.Add(Label((_meeting ? L.T("SPOTKANIE") : "ALARM") + "  ·  " + L.Up(App.AccountName(store.Layer)), 11.5, "FgDim", FontWeights.Bold));
        _dayText.Margin = new Thickness(0, 2, 0, 0);
        _dayText.Foreground = Ui.Res("AccentBrush");
        p.Children.Add(_dayText);

        p.Children.Add(Caption(_meeting ? L.T("TYTUŁ") : L.T("OPIS")));
        p.Children.Add(_text);

        if (_meeting)
        {
            p.Children.Add(Caption(L.T("OD – DO")));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_time);
            row.Children.Add(new TextBlock { Text = "–", Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
            row.Children.Add(_end);
            row.Children.Add(Hint(L.T("np. 14-15:30 · do: 1h")));
            p.Children.Add(row);
            p.Children.Add(Caption(L.T("PRZYPOMNIENIE")));
            foreach (var (m, label) in Reminders) _remind.Items.Add(new ComboBoxItem { Content = label, Tag = m });
            p.Children.Add(_remind);
        }
        else
        {
            p.Children.Add(Caption(L.T("GODZINA")));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_time);
            row.Children.Add(Hint(L.T("np. 15:30 · 9 · za 20 min · za 2 h")));
            p.Children.Add(row);
        }

        p.Children.Add(Caption(L.T("POWTARZAJ")));
        foreach (var (code, label) in Alarm.Repeats) _repeat.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        p.Children.Add(_repeat);

        _preview.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(_preview);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (existing != null)
        {
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            if (!existing.Once && day != null)
            {
                var skip = Btn(L.T("Usuń ten dzień"), "SecondaryButton", (_, _) =>
                {
                    (existing.Skips ??= new()).Add(BoardWindow.DayKey(_day));
                    _store.Changed(existing);
                    Done();
                });
                skip.Margin = new Thickness(0, 0, 8, 0);
                left.Children.Add(skip);
            }
            var del = Btn(existing.Once ? L.T("Usuń") : L.T("Usuń serię"), "SecondaryButton", (_, _) => { _store.DeleteAlarm(existing); Done(); });
            del.Margin = new Thickness(0);
            left.Children.Add(del);
            DockPanel.SetDock(left, Dock.Left);
            buttons.Children.Add(left);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Btn(L.T("Anuluj"), "SecondaryButton", (_, _) => Close()));
        _save = Btn(L.T("Zapisz"), "PrimaryButton", (_, _) => Save());
        right.Children.Add(_save);
        buttons.Children.Add(right);
        p.Children.Add(buttons);
        Content = p;

        // initial values
        if (existing != null)
        {
            _text.Text = existing.Text;
            _time.Text = existing.Time;
            _end.Text = existing.End ?? "";
            _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(Alarm.Repeats, r => r.Code == (existing.Repeat ?? "")));
            _remind.SelectedIndex = Math.Max(0, Array.FindIndex(Reminders, r => r.Minutes == existing.Remind));
        }
        else
        {
            _text.Text = title ?? "";
            var next = DateTime.Now.AddHours(1);
            _time.Text = $"{next.Hour:00}:00";
            if (_meeting) _end.Text = $"{(next.Hour + 1) % 24:00}:00";
            _repeat.SelectedIndex = 0;
            _remind.SelectedIndex = Array.FindIndex(Reminders, r => r.Minutes == 5);
        }

        _text.TextChanged += (_, _) => Update();
        _time.TextChanged += (_, _) =>
        {
            // "14-15:30" typed into "od" fills both fields
            if (_meeting && Regex.IsMatch(_time.Text, "[-–]") && AlarmService.ParseMeeting(_time.Text, null, out var s, out var e, out var rest) && rest.Length == 0)
            {
                _time.Text = s.ToString("hh\\:mm");
                _end.Text = e.ToString("hh\\:mm");
                _end.Focus();
                _end.CaretIndex = _end.Text.Length;
            }
            Update();
        };
        _end.TextChanged += (_, _) => Update();
        _repeat.SelectionChanged += (_, _) => Update();
        _remind.SelectionChanged += (_, _) => Update();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter && _save.IsEnabled) { e.Handled = true; Save(); }
        };
        Loaded += (_, _) =>
        {
            Activate();
            var focus = _text.Text.Length == 0 ? _text : _time;
            focus.Focus();
            focus.SelectAll();
        };
        Update();
    }

    static TextBlock Caption(string text)
    {
        var t = Label(text, 11, "FgDim", FontWeights.SemiBold);
        t.Margin = new Thickness(0, 12, 0, 4);
        return t;
    }

    static TextBlock Hint(string text) => new() { Text = text, Foreground = Ui.Res("FgFaint"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    /// <summary>CZWARTEK 8 PAŹDZIERNIK (with "DZIŚ" / "JUTRO" in front).</summary>
    public static string DayTitle(DateTime d)
    {
        var text = L.LongDay(d);
        if (d.Year != DateTime.Today.Year) text += $" {d.Year}";
        var rel = d == DateTime.Today ? L.T("DZIŚ") : d == DateTime.Today.AddDays(1) ? L.T("JUTRO") : null;
        return rel != null ? $"{rel}  ·  {text}" : text;
    }

    string RepeatCode => (_repeat.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    int? RemindMinutes => (_remind.SelectedItem as ComboBoxItem)?.Tag as int?;

    /// <summary>Start (and meeting end) of the first occurrence; null = can't read the time.</summary>
    (DateTime Start, DateTime? End)? Resolve()
    {
        var (time, delay) = AlarmService.ParseTime(_time.Text);
        DateTime start;
        if (delay is { } dl && !_meeting)
        {
            var t = DateTime.Now + dl;
            start = t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));
        }
        else if (time is { } tod)
        {
            start = _day + tod;
            // "8:00" typed in the afternoon from the tray / top bar = tomorrow morning
            if (!_fixedDay && start <= DateTime.Now) start = start.AddDays(1);
        }
        else return null;
        if (!_meeting) return (start, null);

        DateTime end;
        var endText = _end.Text.Trim();
        if (endText.Length == 0) end = start.AddHours(1);
        else if (AlarmService.ParseDuration(endText) is { } dur) end = start + dur;
        else if (AlarmService.ParseTime(endText).Time is { } et) end = start.Date + et;
        else return null;
        if (end <= start) return null;
        return (start, end);
    }

    void Update()
    {
        var r = Resolve();
        _save.IsEnabled = r != null;
        _dayText.Text = DayTitle(r?.Start.Date ?? _day);
        if (r is not { } v)
        {
            _preview.Text = _meeting ? L.T("Nie rozumiem godzin (koniec musi być po początku).") : L.T("Nie rozumiem godziny.");
            _preview.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
            return;
        }
        _preview.Foreground = Ui.Res("FgDim");
        var repeat = RepeatCode;
        var when = _meeting ? $"{v.Start:HH:mm}–{v.End:HH:mm}  ({BoardWindow.Hours((v.End!.Value - v.Start).TotalHours)})" : $"{v.Start:HH:mm}";
        string text = repeat.Length == 0
            ? (v.Start <= DateTime.Now ? $"→ {when}  ·  {L.T("już minęło")}" : $"→ {when}  ·  {AlarmService.Until(v.Start)}")
            : $"→ {Alarm.Repeats.First(x => x.Code == repeat).Label.ToLower(BoardWindow.Pl)}, {when}";
        if (_meeting && RemindMinutes is { } m) text += "  ·  " + (m == 0 ? L.T("przypomnienie o czasie") : L.F("przypomnienie {0} min wcześniej", m));
        _preview.Text = text;
    }

    void Save()
    {
        if (Resolve() is not { } r) return;
        var x = _existing ?? new Alarm { Kind = _meeting ? "meeting" : "alarm" };
        x.Text = _text.Text.Trim();
        // a repeating series keeps its first day when edited from a later occurrence
        if (_existing == null || _existing.Once || RepeatCode.Length == 0) x.Day = r.Start.ToString("yyyy-MM-dd");
        x.Time = r.Start.ToString("HH:mm");
        x.End = r.End?.ToString("HH:mm");
        x.Remind = _meeting ? RemindMinutes : null;
        x.Repeat = RepeatCode;
        x.Armed = DateTime.Now;
        x.Deleted = false;
        if (_existing == null) _store.AddAlarm(x); else _store.Changed(x);
        Done();
    }

    void Done()
    {
        AlarmService.Reschedule(); // also refreshes the board and the "Dziś" card
        Close();
    }

    /// <summary>Opens the dialog above everything (the board may be behind other windows).</summary>
    public static void Edit(BoardStore store, Alarm? alarm = null, DateTime? day = null, bool meeting = false, string? title = null)
    {
        GlassWindow.EndOverlays();
        new AlarmWindow(store, alarm, day, meeting, title).Show();
    }
}
