using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The bar's own search (Pasek → Wyszukiwarka → TaskWall): the magnifier in the middle of the bar opens a field right over it.
/// Typing finds programs of the Start menu (shell:AppsFolder – the Store's and the desktop's alike; letters at the starts of
/// words count most, then anywhere, Polish letters with or without their marks), works out arithmetic ("= 12*7"), and offers
/// the web and the files for the words. ↑ ↓ choose, Enter starts, Esc closes. The list of programs is read once in the
/// background and again when the field opens after five minutes.
/// </summary>
sealed class AppSearch
{
    sealed record App(string Name, string Path, string Plain);
    sealed record Result(string Title, string? Sub, ImageSource? Icon, string Glyph, Action Run, DockPin? Pin = null);

    static List<App> _apps = new();
    static DateTime _read = DateTime.MinValue;
    static bool _reading;

    readonly BarPanel _panel = new();
    readonly TextBox _box = new();
    readonly StackPanel _list = new();
    readonly StackPanel _root = new() { Width = 600 };
    List<Result> _results = new();
    readonly List<Border> _rows = new();
    int _sel;
    /// <summary>The bar at the bottom: the field nearest to it, the results over it (the best one next to the field).</summary>
    bool _upward;

    public AppSearch()
    {
        _box.Style = (Style)Application.Current.Resources["FieldBox"];
        _box.FontSize = 15;
        _box.Padding = new Thickness(10, 7, 10, 7);
        _box.TextChanged += (_, _) => Update();
        _box.PreviewKeyDown += OnKey;
        System.Windows.Automation.AutomationProperties.SetName(_box, L.T("Szukaj"));
        _panel.Centred = true; // in the middle of the screen, like the field of a launcher
    }

    /// <summary>The field and the list in the order the bar's side wants (the field always next to the bar).</summary>
    void Arrange(bool upward)
    {
        _upward = upward;
        _root.Children.Clear();
        _list.Margin = upward ? new Thickness(0, 0, 0, 8) : new Thickness(0, 8, 0, 0);
        if (upward) { _root.Children.Add(_list); _root.Children.Add(_box); }
        else { _root.Children.Add(_box); _root.Children.Add(_list); }
    }

    public bool IsOpen => _panel.IsVisible;

    /// <summary>Opens over the magnifier (empty, the caret in it) – or closes when open.</summary>
    public void Toggle(FrameworkElement anchor, BarWindow bar)
    {
        if (_panel.IsVisible) { _panel.Hide(); return; }
        if (_panel.JustClosed) return;
        if (DateTime.Now - _read > TimeSpan.FromMinutes(5)) ReadApps();
        Arrange(!bar.AtTop);
        _box.Text = "";
        Update();
        _panel.Open(_root, anchor, bar);
        _panel.Dispatcher.BeginInvoke(() => { _box.Focus(); Keyboard.Focus(_box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The Start menu's programs, on a thread of their own (the shell's folder is a COM object that wants STA).</summary>
    static void ReadApps()
    {
        if (_reading) return;
        _reading = true;
        var ui = Application.Current.Dispatcher;
        var t = new Thread(() =>
        {
            var list = new List<App>();
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(type!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    list.Add(new App(name, path, Plain(name)));
                }
            }
            catch (Exception ex) { Log.Error("search apps", ex); }
            ui.BeginInvoke(() =>
            {
                _reading = false;
                if (list.Count > 0) { _apps = list.GroupBy(a => a.Name).Select(g => g.First()).ToList(); _read = DateTime.Now; }
            });
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Lower case without Polish marks ("Wiadomości" → "wiadomosci"): typing without them finds them too.</summary>
    static string Plain(string s)
    {
        var d = s.ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>How well a program's name fits: its start, a word's start, anywhere, the letters in order (0: not at all).</summary>
    static int Score(string plain, string q)
    {
        if (plain.StartsWith(q)) return 100 - Math.Min(plain.Length, 40) / 4;
        int at = plain.IndexOf(q, StringComparison.Ordinal);
        if (at > 0 && !char.IsLetterOrDigit(plain[at - 1])) return 80;
        if (at > 0) return 60;
        // the first letters of the words ("vsc" → Visual Studio Code); not letters strewn anywhere ("paint" ≠ Captain of Industry)
        var initials = string.Concat(plain.Split(' ', '-', '.', '_').Where(w => w.Length > 0).Select(w => w[0]));
        return q.Length >= 2 && initials.StartsWith(q) ? 40 : 0;
    }

    void Update()
    {
        var q = _box.Text.Trim();
        var results = new List<Result>();
        if (q.Length == 0)
        {
            Render(results, _apps.Count == 0 && _reading ? L.T("Wczytuję programy…") : L.T("Wpisz nazwę programu, działanie (= 2*3) albo hasło do internetu."));
            return;
        }
        // arithmetic: "12*7", "= (3+4)/2"
        var expr = q.TrimStart('=').Trim().Replace(',', '.');
        if (expr.Length > 0 && expr.Any(char.IsDigit) && expr.All(c => char.IsDigit(c) || "+-*/(). %".Contains(c)) && expr.Any(c => "+-*/%".Contains(c)))
        {
            try
            {
                var v = Convert.ToDouble(new System.Data.DataTable().Compute(expr, ""), CultureInfo.InvariantCulture);
                if (!double.IsNaN(v) && !double.IsInfinity(v))
                {
                    var text = Math.Round(v, 10).ToString("0.##########", L.Culture);
                    results.Add(new Result("= " + text, L.T("Enter kopiuje wynik"), null, "=", () => Clipboard.SetText(text)));
                }
            }
            catch { }
        }
        var pq = Plain(q);
        foreach (var a in _apps.Select(a => (a, s: Score(a.Plain, pq))).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.a.Name.Length).Take(7).Select(x => x.a))
        {
            var app = a;
            results.Add(new Result(app.Name, L.T("Program"), ShellIcons.Icon("apps:" + app.Path, null, app.Path, IntPtr.Zero), "",
                () => Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.Path) { UseShellExecute = true }), PinFor(app)));
        }
        if (Uri.TryCreate(q.Contains("://") ? q : "https://" + q, UriKind.Absolute, out var uri) && q.Contains('.') && !q.Contains(' '))
            results.Add(new Result(q, L.T("Otwórz stronę"), null, "↗", () => Open(uri.AbsoluteUri)));
        results.Add(new Result(L.F("Szukaj w internecie: „{0}”", q), null, null, "⌕", () => Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))));
        results.Add(new Result(L.F("Szukaj plików: „{0}”", q), L.T("Eksplorator Windows"), null, "🗀",
            () => Open("search-ms:query=" + Uri.EscapeDataString(q) + "&crumb=location:" + Uri.EscapeDataString(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))));
        Render(results, null);
    }

    /// <summary>A Start menu program as a dock pin: by its file when Windows names one ("{known folder}\app.exe"), else by its app id.</summary>
    static DockPin PinFor(App app)
    {
        var exe = app.Path.StartsWith('{') ? AppBadges.KnownPath(app.Path) : System.IO.File.Exists(app.Path) ? app.Path : null;
        return exe != null && System.IO.File.Exists(exe)
            ? new DockPin { Key = exe.ToLowerInvariant(), Exe = exe, AppId = app.Path, Name = app.Name }
            : new DockPin { Key = app.Path.ToLowerInvariant(), AppId = app.Path, Name = app.Name };
    }

    /// <summary>The right button on a program: pin it to the dock, or put it into a stack.</summary>
    void PinMenu(Border row, DockPin pin)
    {
        var m = new ContextMenu { PlacementTarget = row };
        var s = TaskWall.App.Settings;
        var pinItem = new MenuItem { Header = L.T("Przypnij do docka"), IsEnabled = !s.DockPins.Any(p => string.Equals(p.Key, pin.Key, StringComparison.OrdinalIgnoreCase)) };
        pinItem.Click += (_, _) => { s.DockPins.Add(pin); TaskWall.App.Instance.ApplySettings(); };
        m.Items.Add(pinItem);
        var stacks = StackIsland.AllStacks(s.DockPins).ToList();
        var into = new MenuItem { Header = L.T("Dodaj do stosu"), IsEnabled = stacks.Count > 0 };
        foreach (var (stack, path) in stacks)
        {
            var si = new MenuItem { Header = path };
            si.Click += (_, _) => { stack.Items!.Add(new DockPin { Key = pin.Key, Exe = pin.Exe, AppId = pin.AppId, Name = pin.Name }); TaskWall.App.Instance.ApplySettings(); };
            into.Items.Add(si);
        }
        m.Items.Add(into);
        if (stacks.Count == 0) m.Items.Add(new MenuItem { Header = L.T("Stos dodasz w Ustawieniach → Pasek i dock → Dock"), IsEnabled = false });
        m.IsOpen = true;
    }

    static void Open(string what)
    {
        try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("search open", ex); }
    }

    void Render(List<Result> results, string? hint)
    {
        _results = results;
        _sel = 0;
        _list.Children.Clear();
        _rows.Clear();
        if (hint != null) _list.Children.Add(new TextBlock { Text = hint, Foreground = Ui.Res("FgFaint"), FontSize = 12, Margin = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            int index = i;
            var row = new DockPanel();
            FrameworkElement icon = r.Icon != null
                ? new Image { Source = r.Icon, Width = 24, Height = 24 }
                : new TextBlock { Text = r.Glyph, FontSize = 15, Width = 24, TextAlignment = TextAlignment.Center, Foreground = Ui.Res("FgDim"), FontFamily = new FontFamily("Segoe UI Symbol"), VerticalAlignment = VerticalAlignment.Center };
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            if (r.Sub != null)
            {
                var sub = new TextBlock { Text = r.Sub, FontSize = 11, Foreground = Ui.Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(sub, Dock.Right);
                row.Children.Add(sub);
            }
            row.Children.Add(new TextBlock { Text = r.Title, FontSize = 13, Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(Th.Corner(8)), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            b.MouseEnter += (_, _) => Select(index);
            b.MouseLeftButtonUp += (_, _) => RunAt(index);
            if (r.Pin is { } pin) b.MouseRightButtonUp += (_, e) => { e.Handled = true; Select(index); PinMenu(b, pin); };
            _rows.Add(b);
            // the bar at the bottom: the best result next to the field (the list grows away from it)
            if (_upward) _list.Children.Insert(hint != null ? 1 : 0, b); else _list.Children.Add(b);
        }
        Select(0);
        _panel.Reflow();
    }

    void Select(int i)
    {
        if (_results.Count == 0) return;
        _sel = Math.Clamp(i, 0, _results.Count - 1);
        for (int k = 0; k < _rows.Count; k++) _rows[k].Background = k == _sel ? Ui.Res("AccentSoft") : Brushes.Transparent;
    }

    void RunAt(int i)
    {
        if (i < 0 || i >= _results.Count) return;
        _panel.Hide();
        try { _results[i].Run(); } catch (Exception ex) { Log.Error("search run", ex); }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // the arrows move away from the field and back, whichever side the list is on
            case Key.Down: Select(_upward ? _sel - 1 : _sel + 1); e.Handled = true; break;
            case Key.Up: Select(_upward ? _sel + 1 : _sel - 1); e.Handled = true; break;
            case Key.Enter: RunAt(_sel); e.Handled = true; break;
            case Key.Escape: _panel.Hide(); e.Handled = true; break;
        }
    }

    public void DevType(string words) => _box.Text = words;

    /// <summary>Reads the programs early (at the bar's start), so the first search already finds them.</summary>
    public static void Warm() { if (_apps.Count == 0) ReadApps(); }
}
