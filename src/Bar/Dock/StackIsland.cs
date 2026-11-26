using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskWall;

/// <summary>
/// Stacks on the dock (like StackDock): a pinned tile that opens an island over the bar with its programs and nested stacks.
/// A click on a program brings it up (or starts it) and closes the island; a click on a nested stack goes into it (the arrow
/// back, the path in the header). "+" adds a program from a file, a running one, or a new stack; the right button removes an
/// item or renames a stack. Stacks live in the dock's pins (<see cref="DockPin.Items"/>), so they are kept per computer.
/// </summary>
static class StackIsland
{
    static BarPanel? _panel;
    static readonly List<DockPin> _path = new();
    static FrameworkElement? _anchor;
    static BarWindow? _bar;
    static DockView? _dock;
    static AppSettings S => App.Settings;

    /// <summary>Opens the stack's island at its tile – or closes it when it is open (the click that closed it doesn't open it again).</summary>
    public static void Toggle(DockPin stack, FrameworkElement anchor, BarWindow bar, DockView dock)
    {
        _panel ??= new BarPanel();
        bool same = _path.FirstOrDefault() == stack;
        if (_panel.IsVisible)
        {
            // the click on the dock took the focus: the same stack closes it, another one takes the open island over (it glides there)
            if (same || !_panel.ClosingSoon) { _panel.FadeOut(); return; }
            _panel.KeepOpen();
        }
        else if (_panel.JustClosed && same) return;
        _path.Clear();
        _path.Add(stack);
        _anchor = anchor;
        _bar = bar;
        _dock = dock;
        _panel.Open(Build(), anchor, bar);
    }

    static void Rebuild()
    {
        if (_panel == null || _anchor == null || _bar == null) return;
        if (_panel.IsVisible) { _panel.Open(Build(), _anchor, _bar); return; }
        _panel.Open(Build(), _anchor, _bar); // back after a dialog took the focus
    }

    static void Changed()
    {
        DockView.PinsChanged(); // the stack's tile shows its first programs (on every monitor's dock)
    }

    const double Tile = 92, IconPx = 54;

    static FrameworkElement Build()
    {
        var stack = _path[^1];
        stack.Items ??= new();
        var root = new StackPanel { Width = Math.Clamp(stack.Items.Count, 3, 5) * (Tile + 4) + 4 };

        // the header: back, the path, "+"
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 10), LastChildFill = true };
        var add = SmallButton("+", L.T("Dodaj do stosu"));
        add.Click += (_, _) => AddMenu(add, stack);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        if (_path.Count > 1)
        {
            var back = SmallButton("‹", L.T("Wstecz"));
            back.Click += (_, _) => { _path.RemoveAt(_path.Count - 1); Rebuild(); };
            DockPanel.SetDock(back, Dock.Left);
            back.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(back);
        }
        var badge = new Image { Source = Icon(stack), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = L.T("Ikona stosu") };
        badge.MouseLeftButtonUp += (_, _) => { var m = new ContextMenu { PlacementTarget = badge }; m.Items.Add(IconMenu(stack, () => { Changed(); Rebuild(); })); m.IsOpen = true; };
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(new TextBlock
        {
            Text = string.Join("  ›  ", _path.Select(p => p.Name.Length > 0 ? p.Name : L.T("Stos"))), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        root.Children.Add(head);

        var grid = new WrapPanel();
        foreach (var item in stack.Items.ToList()) grid.Children.Add(TileFor(stack, item));
        if (stack.Items.Count == 0)
            grid.Children.Add(new TextBlock { Text = L.T("Pusty stos – dodaj program przyciskiem +."), Foreground = Ui.Res("FgFaint"), FontSize = 12, Margin = new Thickness(4, 6, 4, 10), TextWrapping = TextWrapping.Wrap, Width = 3 * Tile });
        root.Children.Add(grid);
        root.PreviewKeyDown += (_, e) => { if (e.Key == Key.Back && _path.Count > 1) { _path.RemoveAt(_path.Count - 1); Rebuild(); e.Handled = true; } };
        return root;
    }

    static Button SmallButton(string text, string tip)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = new TextBlock { Text = text, FontSize = 17, FontFamily = Ui.Font("UiFont"), Foreground = Ui.Res("FgDim") }, ToolTip = tip, Width = 28, Height = 28 };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>One program or nested stack: its icon and name; a click starts / opens it, the right button removes or renames.</summary>
    static FrameworkElement TileFor(DockPin stack, DockPin item)
    {
        bool isStack = item.Items != null;
        var icon = new Image { Width = IconPx, Height = IconPx, Source = isStack ? Icon(item) : ProgramIcon(item), Margin = new Thickness(0, 2, 0, 6) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = item.Name.Length > 0 ? item.Name : System.IO.Path.GetFileNameWithoutExtension(item.Exe ?? item.Key), FontSize = 11, Foreground = Ui.Res(isStack ? "Fg" : "FgDim"),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Tile - 8, FontWeight = isStack ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var tile = new Border
        {
            Width = Tile, Padding = new Thickness(4, 6, 4, 8), Margin = new Thickness(2), CornerRadius = new CornerRadius(Th.Corner(10)), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, Child = new StackPanel { Children = { icon, name } }, ToolTip = item.Name, Focusable = true,
        };
        Anim.HoverBackground(tile, Colors.Transparent, Th.Ink(0x16));
        System.Windows.Automation.AutomationProperties.SetName(tile, item.Name);
        void Run()
        {
            if (isStack) { _path.Add(item); Rebuild(); return; }
            _panel?.FadeOut();
            _dock?.ActivateOrLaunch(item);
        }
        tile.MouseLeftButtonUp += (_, _) => Run();
        tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Run(); e.Handled = true; } };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu { PlacementTarget = tile };
            void Item(string text, Action a) { var i = new MenuItem { Header = text }; i.Click += (_, _) => a(); m.Items.Add(i); }
            if (isStack)
            {
                Item(L.T("Zmień nazwę stosu…"), () => { if (AskWindow.Ask(L.T("Stos"), L.T("Nazwa stosu:"), item.Name) is { Length: > 0 } n) { item.Name = n.Trim(); Changed(); } Rebuild(); });
                m.Items.Add(IconMenu(item, () => { Changed(); Rebuild(); }));
            }
            int at = stack.Items!.IndexOf(item);
            if (at > 0) Item(L.T("Przesuń w lewo"), () => { stack.Items.RemoveAt(at); stack.Items.Insert(at - 1, item); Changed(); Rebuild(); });
            if (at < stack.Items.Count - 1) Item(L.T("Przesuń w prawo"), () => { stack.Items.RemoveAt(at); stack.Items.Insert(at + 1, item); Changed(); Rebuild(); });
            Item(isStack ? L.F("Usuń stos ({0})", item.Items!.Count) : L.T("Usuń ze stosu"), () => { stack.Items.Remove(item); Changed(); Rebuild(); });
            m.IsOpen = true;
        };
        return tile;
    }

    static void AddMenu(Button anchor, DockPin stack)
    {
        var m = new ContextMenu { PlacementTarget = anchor };
        void Item(string text, Action a) { var i = new MenuItem { Header = text }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Item(L.T("Program z pliku…"), () => { if (PickProgram(null) is { } pin) { stack.Items!.Add(pin); Changed(); } Rebuild(); });
        var running = _dock?.RunningPrograms().ToList() ?? new();
        if (running.Count > 0)
        {
            var sub = new MenuItem { Header = L.T("Uruchomiony program") };
            foreach (var r in running)
            {
                var ri = new MenuItem { Header = r.Name, Icon = new Image { Source = ShellIcons.Icon(r.Key, r.Exe, r.AppId, IntPtr.Zero), Width = 16, Height = 16 } };
                ri.Click += (_, _) => { stack.Items!.Add(r); Changed(); Rebuild(); };
                sub.Items.Add(ri);
            }
            m.Items.Add(sub);
        }
        m.Items.Add(new Separator());
        Item(L.T("Nowy stos w tym stosie…"), () =>
        {
            if (AskWindow.Ask(L.T("Nowy stos"), L.T("Nazwa stosu:"), L.T("Stos")) is { Length: > 0 } n)
            {
                stack.Items!.Add(NewStack(n.Trim()));
                Changed();
            }
            Rebuild();
        });
        m.IsOpen = true;
    }

    public static DockPin NewStack(string name) => new() { Key = "stack:" + Guid.NewGuid().ToString("N")[..12], Name = name, Items = new() };

    /// <summary>Every stack in the pins (nested too), with its path ("Gry › Strategie") for menus.</summary>
    public static IEnumerable<(DockPin Stack, string Path)> AllStacks(IEnumerable<DockPin> pins, string prefix = "")
    {
        foreach (var p in pins.Where(p => p.Items != null))
        {
            var path = prefix.Length > 0 ? prefix + "  ›  " + p.Name : p.Name;
            yield return (p, path);
            foreach (var inner in AllStacks(p.Items!, path)) yield return inner;
        }
    }

    /// <summary>A program file or a shortcut chosen by hand (a shortcut followed to its program, so its windows group under it).</summary>
    public static DockPin? PickProgram(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Wybierz program"),
            Filter = L.T("Programy i skróty") + " (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) is { Length: > 0 } sm ? System.IO.Path.Combine(sm, "Programs") : "",
            DereferenceLinks = false,
        };
        if (owner != null ? dlg.ShowDialog(owner) != true : dlg.ShowDialog() != true) return null;
        string exe = dlg.FileName, args = "";
        if (exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var shell = Type.GetTypeFromProgID("WScript.Shell");
                dynamic ws = Activator.CreateInstance(shell!)!;
                dynamic lnk = ws.CreateShortcut(exe);
                if (((string)lnk.TargetPath) is { Length: > 0 } target) { exe = target; args = (string)lnk.Arguments ?? ""; }
            }
            catch (Exception ex) { Log.Error("pin shortcut", ex); }
        }
        string name;
        try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription ?? ""; } catch { name = ""; }
        if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        return new DockPin { Key = exe.ToLowerInvariant(), Exe = exe, Args = args.Length > 0 ? args : null, Name = name.Trim() };
    }

    /// <summary>A program's icon (its path written with \ – the shell doesn't read C:/…).</summary>
    static ImageSource? ProgramIcon(DockPin p) => ShellIcons.Icon(p.Key, p.Exe?.Replace('/', '\\'), p.AppId, IntPtr.Zero);

    /// <summary>The symbols a stack can wear (Segoe Fluent Icons), with their names in the menu.</summary>
    static readonly (string Hex, string Name)[] Glyphs =
    {
        ("E7FC", "Gry"), ("E821", "Praca"), ("E943", "Kod"), ("E790", "Grafika"), ("E8D6", "Muzyka"), ("E714", "Wideo"),
        ("E722", "Zdjęcia"), ("E8BD", "Rozmowy"), ("E774", "Internet"), ("E90F", "Narzędzia"), ("E8B7", "Folder"), ("E734", "Ulubione"),
        ("E7BE", "Nauka"), ("E80F", "Dom"), ("E8F1", "Biblioteka"), ("E945", "Szybkie"),
    };
    static readonly string[] Palette = { "#5B8CFF", "#8B6CFF", "#E26D9A", "#E5484D", "#F0A04B", "#4CC38A", "#3BB8C9", "#7A808C" };
    static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>"Ikona stosu ▸": its programs in a grid, one of its programs, or a symbol – and the symbol's colour.</summary>
    public static MenuItem IconMenu(DockPin stack, Action changed)
    {
        var root = new MenuItem { Header = L.T("Ikona stosu") };
        MenuItem Add(ItemsControl parent, string text, Action a, ImageSource? icon = null, bool check = false)
        {
            var i = new MenuItem { Header = text, IsCheckable = false, IsChecked = check };
            if (icon != null) i.Icon = new Image { Source = icon, Width = 18, Height = 18 };
            i.Click += (_, _) => a();
            parent.Items.Add(i);
            return i;
        }
        Add(root, L.T("Siatka programów"), () => { stack.Icon = null; changed(); }, null, stack.Icon == null);
        if (stack.Items is { Count: > 0 } items)
        {
            var programs = new MenuItem { Header = L.T("Ikona programu ze stosu") };
            foreach (var it in items)
            {
                var key = it.Key;
                Add(programs, it.Name, () => { stack.Icon = "item:" + key; changed(); }, it.Items != null ? Icon(it, 2) : ProgramIcon(it), stack.Icon == "item:" + key);
            }
            root.Items.Add(programs);
        }
        var symbols = new MenuItem { Header = L.T("Symbol") };
        foreach (var (hex, name) in Glyphs)
        {
            var h = hex;
            Add(symbols, L.T(name), () => { stack.Icon = "glyph:" + h; stack.IconColor ??= Palette[0]; changed(); }, GlyphIcon(h, stack.IconColor ?? Palette[0]), stack.Icon == "glyph:" + h);
        }
        root.Items.Add(symbols);
        var colours = new MenuItem { Header = L.T("Kolor symbolu") };
        foreach (var c in Palette)
        {
            var col = c;
            var sw = new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col)) };
            var mi = new MenuItem { Header = col, Icon = sw, IsChecked = string.Equals(stack.IconColor, col, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (_, _) => { stack.IconColor = col; if (stack.Icon?.StartsWith("glyph:") != true) stack.Icon = "glyph:" + Glyphs[0].Hex; changed(); };
            colours.Items.Add(mi);
        }
        root.Items.Add(colours);
        return root;
    }

    /// <summary>A symbol in white on a rounded square of the colour (lighter at the top), 128 px.</summary>
    static ImageSource GlyphIcon(string hex, string colour)
    {
        const int Px = 128;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(colour); } catch { c = Color.FromRgb(0x5B, 0x8C, 0xFF); }
        var top = Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new LinearGradientBrush(top, c, 90), null, new Rect(1, 1, Px - 2, Px - 2), 30, 30);
            var glyph = ((char)Convert.ToInt32(hex, 16)).ToString();
            var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 72, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((Px - ft.Width) / 2, (Px - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(Px, Px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    static ImageSource? _search;

    /// <summary>The dock's magnifier: the search symbol on a quiet round tile, like an icon among the others.</summary>
    public static ImageSource SearchIcon()
    {
        if (_search != null) return _search;
        const int Px = 128;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Th.Ink(0x16)), new Pen(new SolidColorBrush(Th.Ink(0x30)), 3), new Point(Px / 2, Px / 2), Px / 2 - 10, Px / 2 - 10);
            var ft = new FormattedText("\uE721", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 52, Ui.Res("Fg"), 1.0);
            dc.DrawText(ft, new Point((Px - ft.Width) / 2, (Px - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(Px, Px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return _search = bmp;
    }

    /// <summary>A stack's tile: its chosen icon, or a rounded square with its first four programs (a nested stack drawn small inside), 128 px.</summary>
    public static ImageSource Icon(DockPin stack, int level = 0)
    {
        if (stack.Icon is { } chosen)
        {
            if (chosen.StartsWith("glyph:")) return GlyphIcon(chosen[6..], stack.IconColor ?? Palette[0]);
            if (chosen.StartsWith("item:") && stack.Items?.FirstOrDefault(i => i.Key == chosen[5..]) is { } it
                && (it.Items != null ? (level < 2 ? Icon(it, level + 1) : null) : ProgramIcon(it)) is { } img) return img;
        }
        const int Px = 128;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new SolidColorBrush(Th.Light ? Color.FromArgb(0x50, 0x60, 0x60, 0x70) : Color.FromArgb(0x48, 0xB0, 0xB4, 0xC0));
            dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(Th.Ink(0x50)), 2), new Rect(1, 1, Px - 2, Px - 2), 30, 30);
            var kids = (stack.Items ?? new()).Take(4).ToList();
            const double pad = 10, gap = 5;
            double cell = (Px - 2 * pad - gap) / 2;
            for (int i = 0; i < 4; i++)
            {
                var r = new Rect(pad + i % 2 * (cell + gap), pad + i / 2 * (cell + gap), cell, cell);
                if (i < kids.Count)
                {
                    var k = kids[i];
                    var img = k.Items != null ? (level < 2 ? Icon(k, level + 1) : null) : ProgramIcon(k);
                    if (img != null) dc.DrawImage(img, r);
                }
                else dc.DrawRoundedRectangle(new SolidColorBrush(Th.Ink(0x14)), null, r, 9, 9); // an empty place
            }
        }
        var bmp = new RenderTargetBitmap(Px, Px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
