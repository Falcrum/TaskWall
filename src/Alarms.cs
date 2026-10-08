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

namespace DeskWall;

/// <summary>
/// Rings alarms of every open account. No polling every second: the timer is set to the next alarm
/// (at most a minute ahead, so alarms added on another PC are picked up too). After sleep / a PC that was off,
/// an alarm missed by less than 12 hours still rings, marked as late.
/// </summary>
static class AlarmService
{
    static readonly DispatcherTimer Timer = new();
    static readonly List<(Alarm Alarm, string Layer, DateTime Due, DateTime At)> Snoozed = new();
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
        for (int i = 0; i <= 1; i++)
        {
            var d = now.Date.AddDays(-i);
            if (!a.Occurs(d)) continue;
            var at = a.At(d);
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

    // ---------- parsing ("15:30", "9", "o 7", "za 20 min", "za 2 h") ----------

    static readonly Regex Relative = new(@"^za\s*(\d+(?:[.,]\d+)?)\s*(m|min|minut|minuty|minutę|h|godz\.?|godzin|godziny|godzinę)?$", RegexOptions.IgnoreCase);
    static readonly Regex Clock = new(@"^(?:o\s*|godz\.?\s*)?(\d{1,2})(?:[:.\s]?(\d{2}))?$", RegexOptions.IgnoreCase);

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
            if (h <= 23 && m <= 59) return (new TimeSpan(h, m, 0), null);
        }
        return (null, null);
    }

    public static string Until(DateTime at)
    {
        var d = at - DateTime.Now;
        if (d.TotalMinutes < 1) return "za chwilę";
        if (d.TotalHours < 1) return $"za {(int)Math.Ceiling(d.TotalMinutes)} min";
        if (d.TotalHours < 10) return $"za {(int)d.TotalHours} h {(int)Math.Ceiling(d.TotalMinutes % 60) % 60} min";
        if (d.TotalHours < 48) return $"za {(int)Math.Round(d.TotalHours)} h";
        return $"za {(int)Math.Round(d.TotalDays)} dni";
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
        head.Inlines.Add(new System.Windows.Documents.Run($"ALARM  {at:HH:mm}  ·  {App.AccountName(layer).ToUpper(BoardWindow.Pl)}"));
        var late = DateTime.Now - at;
        if (snoozed) head.Inlines.Add(new System.Windows.Documents.Run("  ·  drzemka") { Foreground = Ui.Res("FgDim") });
        else if (late.TotalMinutes >= 2)
            head.Inlines.Add(new System.Windows.Documents.Run($"  ·  spóźniony o {(late.TotalHours >= 1 ? $"{(int)late.TotalHours} h " : "")}{late.Minutes} min") { Foreground = Ui.Res("FgDim") });
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(a.Text) ? "Alarm" : a.Text,
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
            var b = new Button { Content = label, Style = (Style)Application.Current.Resources["BarButton"], ToolTip = "Drzemka" };
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

/// <summary>Add / edit an alarm: time ("15:30", "za 20 min"), day ("jutro", "pt", "14.10"), repeat.</summary>
public sealed class AlarmWindow : DarkWindow
{
    readonly BoardStore _store;
    readonly Alarm? _existing;
    readonly TextBox _text = new() { Style = (Style)Application.Current.Resources["FieldBox"] };
    readonly TextBox _time = new() { Style = (Style)Application.Current.Resources["FieldBox"], Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _day = new() { Style = (Style)Application.Current.Resources["FieldBox"], Width = 150 };
    readonly ComboBox _repeat = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _preview = Label("", 12, "FgDim");
    readonly Button _save;
    bool _dayTouched;

    public AlarmWindow(BoardStore store, Alarm? existing = null, DateTime? day = null)
    {
        _store = store;
        _existing = existing;
        Title = existing == null ? "Nowy alarm" : "Alarm";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var p = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        p.Children.Add(Label("ALARM  ·  " + App.AccountName(store.Layer).ToUpper(BoardWindow.Pl), 12, "FgDim", FontWeights.Bold));
        p.Children.Add(Label("Przypomnienie o konkretnej godzinie, niezależne od zadań. Dzwoni na każdym komputerze z DeskWall.", 11.5, "FgFaint"));

        p.Children.Add(Caption("OPIS"));
        p.Children.Add(_text);

        p.Children.Add(Caption("GODZINA"));
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(_time);
        timeRow.Children.Add(new TextBlock { Text = "np. 15:30 · 9 · za 20 min · za 2 h", Foreground = Ui.Res("FgFaint"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        p.Children.Add(timeRow);

        p.Children.Add(Caption("DZIEŃ"));
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal };
        dayRow.Children.Add(_day);
        foreach (var (label, offset) in new[] { ("DZIŚ", 0), ("JUTRO", 1), ("POJUTRZE", 2) })
        {
            var b = new Button { Content = label, Style = (Style)Application.Current.Resources["BarButton"], Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => { _day.Text = DayText(DateTime.Today.AddDays(offset)); _dayTouched = true; _time.Focus(); };
            dayRow.Children.Add(b);
        }
        p.Children.Add(dayRow);

        p.Children.Add(Caption("POWTARZAJ"));
        foreach (var (code, label) in Alarm.Repeats) _repeat.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        p.Children.Add(_repeat);

        _preview.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(_preview);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (existing != null)
        {
            var del = Btn("Usuń alarm", "SecondaryButton", (_, _) => { _store.DeleteAlarm(_existing!); Done(); });
            del.Margin = new Thickness(0);
            DockPanel.SetDock(del, Dock.Left);
            buttons.Children.Add(del);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Btn("Anuluj", "SecondaryButton", (_, _) => Close()));
        _save = Btn("Zapisz", "PrimaryButton", (_, _) => Save());
        right.Children.Add(_save);
        buttons.Children.Add(right);
        p.Children.Add(buttons);
        Content = p;

        // initial values
        if (existing != null)
        {
            _text.Text = existing.Text;
            _time.Text = existing.Time;
            _day.Text = DateTime.TryParse(existing.Day, out var d) ? DayText(d) : DayText(DateTime.Today);
            _dayTouched = true;
            _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(Alarm.Repeats, r => r.Code == (existing.Repeat ?? "")));
        }
        else
        {
            var start = day ?? DateTime.Today;
            _day.Text = DayText(start);
            _dayTouched = day != null && day != DateTime.Today;
            // a sensible default: the next full hour
            var next = DateTime.Now.AddHours(1);
            _time.Text = $"{next.Hour:00}:00";
            _repeat.SelectedIndex = 0;
        }

        _text.TextChanged += (_, _) => Update();
        _time.TextChanged += (_, _) => Update();
        _day.TextChanged += (_, _) => { if (_day.IsKeyboardFocused) _dayTouched = true; Update(); };
        _repeat.SelectionChanged += (_, _) => Update();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter && _save.IsEnabled) { e.Handled = true; Save(); }
        };
        Loaded += (_, _) => { Activate(); _text.Focus(); };
        Update();
    }

    static TextBlock Caption(string text)
    {
        var t = Label(text, 11, "FgDim", FontWeights.SemiBold);
        t.Margin = new Thickness(0, 12, 0, 4);
        return t;
    }

    static string DayText(DateTime d) =>
        d == DateTime.Today ? "dziś" : d == DateTime.Today.AddDays(1) ? "jutro" : d.ToString("dd.MM.yyyy");

    string RepeatCode => (_repeat.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>The resulting first occurrence (null = can't read the time / day).</summary>
    DateTime? Resolve()
    {
        var (time, delay) = AlarmService.ParseTime(_time.Text);
        if (delay is { } dl)
        {
            var t = DateTime.Now + dl;
            return t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));
        }
        if (time is not { } tod) return null;
        var day = SmartAdd.ParseDay(_day.Text.Trim()) ?? (DateTime.TryParseExact(_day.Text.Trim(), "dd.MM.yyyy", null, DateTimeStyles.None, out var d) ? d : (DateTime?)null);
        if (day is not { } dd) return null;
        var at = dd.Date + tod;
        // "8:00" typed in the afternoon without picking a day = tomorrow morning
        if (!_dayTouched && at <= DateTime.Now && dd.Date == DateTime.Today) at = at.AddDays(1);
        return at;
    }

    void Update()
    {
        var at = Resolve();
        _save.IsEnabled = at != null;
        if (at is not { } a) { _preview.Text = "Nie rozumiem godziny albo dnia."; _preview.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A)); return; }
        _preview.Foreground = Ui.Res("FgDim");
        var repeat = RepeatCode;
        var when = a.ToString("dddd, d MMMM, HH:mm", BoardWindow.Pl);
        _preview.Text = repeat.Length == 0
            ? (a <= DateTime.Now ? $"→ {when}  (już minęło)" : $"→ {when}  ({AlarmService.Until(a)})")
            : $"→ {Alarm.Repeats.First(r => r.Code == repeat).Label.ToLower(BoardWindow.Pl)} o {a:HH:mm}, od {a.ToString("d MMMM", BoardWindow.Pl)}";
    }

    void Save()
    {
        if (Resolve() is not { } at) return;
        var x = _existing ?? new Alarm();
        x.Text = _text.Text.Trim();
        x.Day = at.ToString("yyyy-MM-dd");
        x.Time = at.ToString("HH:mm");
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
    public static void Edit(BoardStore store, Alarm? alarm = null, DateTime? day = null)
    {
        GlassWindow.EndOverlays();
        new AlarmWindow(store, alarm, day).Show();
    }
}
