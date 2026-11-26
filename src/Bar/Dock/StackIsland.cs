using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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

    /// <summary>The open island came from the pointer resting on its tile: it closes when the pointer leaves the tile and the island
    /// (a click into it, or a click on the tile right after it opened, keeps it open like one opened by a click).</summary>
    static bool _byHover;
    static DateTime _hoverOpenedAt;
    static readonly DispatcherTimer _hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(400) };
    static readonly DispatcherTimer _hoverClose = new() { Interval = TimeSpan.FromMilliseconds(500) };
    static (DockPin Stack, FrameworkElement Anchor, BarWindow Bar, DockView Dock)? _hoverTarget;

    static StackIsland()
    {
        _hoverOpen.Tick += (_, _) =>
        {
            _hoverOpen.Stop();
            if (_hoverTarget is { } t && t.Anchor.IsMouseOver && Mouse.LeftButton != MouseButtonState.Pressed) OpenByHover(t.Stack, t.Anchor, t.Bar, t.Dock);
        };
        _hoverClose.Tick += (_, _) =>
        {
            _hoverClose.Stop();
            if (_byHover && _panel is { IsVisible: true } p && !p.IsMouseOver && _anchor?.IsMouseOver != true && _press == null) p.FadeOut();
        };
    }

    static BarPanel Panel()
    {
        if (_panel != null) return _panel;
        var p = _panel = new BarPanel();
        p.MouseEnter += (_, _) => _hoverClose.Stop();
        p.MouseLeave += (_, _) => LeaveSoon();
        p.Activated += (_, _) => _byHover = false; // clicked into (or opened by a click): it stays until a click elsewhere
        p.IsVisibleChanged += (_, _) => { if (!p.IsVisible) { _byHover = false; _hoverClose.Stop(); _press = null; } };
        // Esc clears the search first, closes the island the next time
        p.Escaping = () => { if (_query.Length == 0 || _searchBox == null) return false; _searchBox.Clear(); return true; };
        p.PreviewTextInput += OnTyping;
        return p;
    }

    /// <summary>The pointer came to a stack's tile: its island opens when it stays there a moment (Ustawienia → Dock).</summary>
    public static void HoverEnter(DockPin stack, FrameworkElement anchor, BarWindow bar, DockView dock)
    {
        _hoverClose.Stop();
        if (!S.StackHover || Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (_panel is { IsVisible: true } && _path.FirstOrDefault() == stack) return; // its island is already open
        _hoverTarget = (stack, anchor, bar, dock);
        _hoverOpen.Stop();
        _hoverOpen.Start();
    }

    /// <summary>The pointer left a dock tile: no opening, and an island opened by hovering closes unless the pointer comes to it.</summary>
    public static void HoverLeave()
    {
        _hoverOpen.Stop();
        LeaveSoon();
    }

    static void LeaveSoon()
    {
        if (!_byHover || _panel is not { IsVisible: true }) return;
        _hoverClose.Stop();
        _hoverClose.Start();
    }

    /// <summary>Opened (or switched to another stack) by hovering: without taking the focus from the window in front. An island
    /// opened by a click only switches to the hovered stack and stays a clicked one.</summary>
    static void OpenByHover(DockPin stack, FrameworkElement anchor, BarWindow bar, DockView dock)
    {
        var p = Panel();
        if (p.IsVisible && _path.FirstOrDefault() == stack) return;
        bool clicked = p.IsVisible && !_byHover;
        _path.Clear();
        _path.Add(stack);
        _query = "";
        _anchor = anchor;
        _bar = bar;
        _dock = dock;
        _byHover = !clicked;
        _hoverOpenedAt = DateTime.Now;
        p.Open(Build(), anchor, bar, activate: clicked);
    }

    /// <summary>Opens the stack's island at its tile – or closes it when it is open (the click that closed it doesn't open it again).</summary>
    public static void Toggle(DockPin stack, FrameworkElement anchor, BarWindow bar, DockView dock)
    {
        var panel = Panel();
        _hoverOpen.Stop();
        _hoverClose.Stop();
        bool same = _path.FirstOrDefault() == stack;
        if (panel.IsVisible)
        {
            if (same)
            {
                // opened by the pointer resting on it just before the click: the click meant to open it, so it stays (as a clicked one)
                if (_byHover && (DateTime.Now - _hoverOpenedAt).TotalMilliseconds < 800) { _byHover = false; panel.Activate(); return; }
                panel.FadeOut();
                return;
            }
            // the click on the dock took the focus: another stack takes the open island over (it glides there); a hovered island
            // never had the focus, so it is taken over at once
            if (!_byHover && !panel.ClosingSoon) { panel.FadeOut(); return; }
            panel.KeepOpen();
        }
        else if (panel.JustClosed && same) return;
        _byHover = false;
        _path.Clear();
        _path.Add(stack);
        _query = "";
        _anchor = anchor;
        _bar = bar;
        _dock = dock;
        panel.Open(Build(), anchor, bar);
    }

    static void Rebuild()
    {
        if (_panel == null || _anchor == null || _bar == null) return;
        _press = null;
        // also back after a dialog took the focus (the island closed meanwhile)
        _panel.Open(Build(), _anchor, _bar);
        if (_query.Length > 0 && _searchBox != null) { _searchBox.Focus(); _searchBox.CaretIndex = _searchBox.Text.Length; }
    }

    /// <summary>Into a stack inside, or back out: a new place, the search empty.</summary>
    static void Go(Action change)
    {
        change();
        _query = "";
        Rebuild();
    }

    static void Changed()
    {
        SettingsStore.Save(S);
        _dock?.Refresh(); // the stack's tile shows its first programs
    }

    const double Tile = 92, IconPx = 54;

    // what the island shows now: its tiles (for the drag), the drop marks over them, the header's way up, the search
    static FrameworkElement? _root;
    static WrapPanel? _grid;
    static Canvas? _overlay;
    static readonly List<(Border Tile, DockPin Item)> _tiles = new();
    static readonly List<FrameworkElement> _upTargets = new();
    static TextBlock? _pathText, _backText;
    static TextBox? _searchBox;
    static string _query = "";

    static FrameworkElement Build()
    {
        var stack = _path[^1];
        stack.Items ??= new();
        var root = new StackPanel { Width = Math.Clamp(stack.Items.Count, 3, 5) * (Tile + 4) + 4 };
        _root = root;
        _upTargets.Clear();
        _backText = null;

        // the header: back, the path, "+"
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 8), LastChildFill = true };
        var add = SmallButton("+", L.T("Dodaj do stosu"));
        add.Click += (_, _) => AddMenu(add, stack);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        if (_path.Count > 1)
        {
            var back = SmallButton("‹", L.T("Wstecz"));
            back.Click += (_, _) => Go(() => _path.RemoveAt(_path.Count - 1));
            DockPanel.SetDock(back, Dock.Left);
            back.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(back);
            _backText = (TextBlock)back.Content;
            _upTargets.Add(back);
        }
        var badge = new Image { Source = Icon(stack), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = L.T("Ikona stosu") };
        badge.MouseLeftButtonUp += (_, _) => { var m = new ContextMenu { PlacementTarget = badge }; m.Items.Add(IconMenu(stack, () => { Changed(); Rebuild(); })); m.IsOpen = true; };
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        _pathText = new TextBlock
        {
            Text = string.Join("  ›  ", _path.Select(p => p.Name.Length > 0 ? p.Name : L.T("Stos"))), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Background = Brushes.Transparent, // the whole line takes a dropped tile (up to the stack outside)
        };
        if (_path.Count > 1) _upTargets.Add(_pathText);
        head.Children.Add(_pathText);
        root.Children.Add(head);

        // the search: typing filters the tiles, programs in the stacks inside too
        var box = _searchBox = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], FontSize = 12, Text = _query, Padding = new Thickness(6, 3, 6, 3) };
        System.Windows.Automation.AutomationProperties.SetName(box, L.T("Szukaj w stosie"));
        var hint = new TextBlock { Text = L.T("Szukaj w stosie"), FontSize = 12, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = _query.Length > 0 ? Visibility.Collapsed : Visibility.Visible };
        box.TextChanged += (_, _) =>
        {
            _query = box.Text.Trim();
            hint.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            Fill();
            _panel?.Reflow();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _found.Count > 0) { e.Handled = true; Run(_found[0]); }
            else if (e.Key == Key.Down && _tiles.Count > 0) { e.Handled = true; _tiles[0].Tile.Focus(); }
        };
        var search = new Grid { Margin = new Thickness(2, 0, 2, 10), Children = { box, hint } };
        root.Children.Add(search);

        _grid = new WrapPanel();
        _overlay = new Canvas { IsHitTestVisible = false };
        root.Children.Add(new Grid { Children = { _grid, _overlay } });
        Fill();
        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Back && _path.Count > 1 && _query.Length == 0 && _searchBox?.IsKeyboardFocused != true) { e.Handled = true; Go(() => _path.RemoveAt(_path.Count - 1)); }
        };
        return root;
    }

    /// <summary>A program or stack the search found: the stack it sits in, and the stacks between the open one and it.</summary>
    sealed record Found(DockPin Item, DockPin Parent, List<DockPin> Between);
    static readonly List<Found> _found = new();

    /// <summary>The tiles: the stack's items, or (while searching) what matches in it and in the stacks inside it, their place in the tip.</summary>
    static void Fill()
    {
        if (_grid == null) return;
        var stack = _path[^1];
        _grid.Children.Clear();
        _overlay?.Children.Clear();
        _tiles.Clear();
        _found.Clear();
        if (_query.Length == 0)
        {
            foreach (var item in stack.Items!.ToList()) _grid.Children.Add(TileFor(stack, item, null));
            if (stack.Items!.Count == 0)
                _grid.Children.Add(new TextBlock { Text = L.T("Pusty stos – dodaj program przyciskiem +."), Foreground = Ui.Res("FgFaint"), FontSize = 12, Margin = new Thickness(4, 6, 4, 10), TextWrapping = TextWrapping.Wrap, Width = 3 * Tile });
            return;
        }
        // the names starting with the text first, then those containing it; in each, in the stacks' order (this stack before those inside)
        var all = Within(stack, new()).Where(f => Name(f.Item).Contains(_query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _found.AddRange(all.Where(f => Name(f.Item).StartsWith(_query, StringComparison.CurrentCultureIgnoreCase)));
        _found.AddRange(all.Except(_found));
        foreach (var f in _found)
        {
            var where = string.Join("  ›  ", _path.Concat(f.Between).Select(p => p.Name.Length > 0 ? p.Name : L.T("Stos")));
            _grid.Children.Add(TileFor(f.Parent, f.Item, f, where + "  ›  " + Name(f.Item)));
        }
        if (_found.Count == 0)
            _grid.Children.Add(new TextBlock { Text = L.F("Nic w tym stosie nie pasuje do „{0}”.", _query), Foreground = Ui.Res("FgFaint"), FontSize = 12, Margin = new Thickness(4, 6, 4, 10), TextWrapping = TextWrapping.Wrap, Width = 3 * Tile });
    }

    /// <summary>Everything in the stack and in the stacks inside it (depth first), each with the stacks between.</summary>
    static IEnumerable<Found> Within(DockPin stack, List<DockPin> between, int depth = 0)
    {
        foreach (var item in stack.Items ?? new())
        {
            yield return new Found(item, stack, between);
            if (item.Items != null && depth < 8)
                foreach (var inner in Within(item, between.Append(item).ToList(), depth + 1)) yield return inner;
        }
    }

    static string Name(DockPin item) => item.Name.Length > 0 ? item.Name : System.IO.Path.GetFileNameWithoutExtension(item.Exe ?? item.Key);

    /// <summary>A typed letter while the island has the focus (not in its search yet): the search starts with it.</summary>
    static void OnTyping(object sender, TextCompositionEventArgs e)
    {
        if (_searchBox == null || _searchBox.IsKeyboardFocused || e.Text.Length == 0 || !char.IsLetterOrDigit(e.Text[0])) return;
        e.Handled = true;
        _searchBox.Focus();
        _searchBox.Text += e.Text;
        _searchBox.CaretIndex = _searchBox.Text.Length;
    }

    /// <summary>A program comes up (or starts) and the island closes; a stack opens in the island.</summary>
    static void Run(Found f)
    {
        if (f.Item.Items != null) { Go(() => { _path.AddRange(f.Between); _path.Add(f.Item); }); return; }
        _panel?.FadeOut();
        _dock?.ActivateOrLaunch(f.Item);
    }

    static Button SmallButton(string text, string tip)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = new TextBlock { Text = text, FontSize = 17, FontFamily = Ui.Font("UiFont"), Foreground = Ui.Res("FgDim") }, ToolTip = tip, Width = 28, Height = 28 };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>One program or nested stack: its icon and name; a click starts / opens it, the right button removes or renames, a drag
    /// moves it (another place in the stack, into a stack inside, up to the stack outside). Found by the search: its place in the tip.</summary>
    static FrameworkElement TileFor(DockPin stack, DockPin item, Found? found, string? where = null)
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
            Background = Brushes.Transparent, Child = new StackPanel { Children = { icon, name } }, ToolTip = where ?? item.Name, Focusable = true,
        };
        Anim.HoverBackground(tile, Colors.Transparent, Th.Ink(0x16));
        System.Windows.Automation.AutomationProperties.SetName(tile, item.Name);
        var run = found ?? new Found(item, stack, new());
        _tiles.Add((tile, item));
        // a press takes the mouse: a click when it comes back up near where it went down, a drag once it moved 6 px (not in the search's results)
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _press = new Press(item, stack, tile, e.GetPosition(_root));
            tile.Focus();
            tile.CaptureMouse();
        };
        tile.MouseMove += (_, e) => DragMove(tile, e);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var p = _press;
            if (p == null || p.Tile != tile) return;
            var target = p.Moving ? TargetAt(p, e) : null;
            _press = null;
            ClearMarks(p);
            tile.ReleaseMouseCapture();
            if (!p.Moving) Run(run);
            else if (target != null) Drop(p, target);
        };
        // the capture taken away (a menu, a dialog, another window): the drag ends there, nothing moved
        tile.LostMouseCapture += (_, _) => { if (_press is { } p && p.Tile == tile) { _press = null; ClearMarks(p); } };
        tile.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Run(run); e.Handled = true; } };
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

    // ---------- dragging a tile inside the island ----------

    sealed class Press(DockPin item, DockPin stack, Border tile, Point start)
    {
        public DockPin Item { get; } = item;
        public DockPin Stack { get; } = stack;
        public Border Tile { get; } = tile;
        public Point Start { get; } = start;
        public bool Moving { get; set; }
    }

    /// <summary>Where a dragged tile goes: before the item at <see cref="Index"/> of its stack, into the stack <see cref="Into"/>, or up to the stack outside.</summary>
    sealed record DropTarget(int Index, DockPin? Into, bool Up, Rect Mark);

    static Press? _press;

    static void DragMove(Border tile, MouseEventArgs e)
    {
        if (_press is not { } p || p.Tile != tile || !tile.IsMouseCaptured || _root == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // the button came up where the island didn't hear it: nothing stays dragged
            _press = null;
            ClearMarks(p);
            tile.ReleaseMouseCapture();
            return;
        }
        if (!p.Moving)
        {
            if (_query.Length > 0 || (e.GetPosition(_root) - p.Start).Length < 6) return;
            p.Moving = true;
            tile.Opacity = 0.4;
            tile.Cursor = Cursors.SizeAll;
        }
        ShowMark(TargetAt(p, e));
    }

    static bool Over(FrameworkElement el, MouseEventArgs e)
    {
        var r = e.GetPosition(el);
        return el.IsVisible && r.X >= 0 && r.Y >= 0 && r.X <= el.ActualWidth && r.Y <= el.ActualHeight;
    }

    /// <summary>The drop place under the pointer: the header's back button or path (up), the middle of a stack's tile (into it), otherwise
    /// the gap nearest the pointer (before or after the nearest tile). Outside the island: nowhere.</summary>
    static DropTarget? TargetAt(Press p, MouseEventArgs e)
    {
        if (_root == null || _grid == null || !Over(_root, e)) return null;
        if (_path.Count > 1 && _upTargets.Any(u => Over(u, e))) return new DropTarget(-1, null, true, Rect.Empty);
        var at = e.GetPosition(_grid);
        (Border Tile, DockPin Item, Rect R)? nearest = null;
        double best = double.MaxValue;
        foreach (var (t, it) in _tiles)
        {
            var r = new Rect(t.TranslatePoint(new Point(0, 0), _grid), new Size(t.ActualWidth, t.ActualHeight));
            if (r.Contains(at) && it.Items != null && it != p.Item && at.X > r.Left + r.Width * 0.2 && at.X < r.Right - r.Width * 0.2)
                return new DropTarget(-1, it, false, r);
            double dx = Math.Max(0, Math.Max(r.Left - at.X, at.X - r.Right)), dy = Math.Max(0, Math.Max(r.Top - at.Y, at.Y - r.Bottom));
            double d = dx * dx + dy * dy * 4; // the row under the pointer counts more than a near tile in another row
            if (d < best) { best = d; nearest = (t, it, r); }
        }
        if (nearest is not { } n) return null;
        int index = p.Stack.Items!.IndexOf(n.Item);
        bool after = at.X >= n.R.Left + n.R.Width / 2;
        double x = after ? n.R.Right + 2 : n.R.Left - 2;
        return new DropTarget(after ? index + 1 : index, null, false, new Rect(x - 1.5, n.R.Top + 6, 3, Math.Max(0, n.R.Height - 12)));
    }

    /// <summary>Shows where the tile goes: an accent line in the gap, a ring round the stack it goes into, the header lit for "up".</summary>
    static void ShowMark(DropTarget? t)
    {
        if (_overlay == null) return;
        _overlay.Children.Clear();
        var accent = Ui.Res("AccentBrush");
        if (_pathText != null) _pathText.Foreground = t?.Up == true ? accent : Ui.Res("Fg");
        if (_backText != null) _backText.Foreground = t?.Up == true ? accent : Ui.Res("FgDim");
        if (t == null || t.Up) return;
        var mark = t.Into != null
            ? new System.Windows.Shapes.Rectangle { Width = t.Mark.Width, Height = t.Mark.Height, RadiusX = Th.Corner(10), RadiusY = Th.Corner(10), Stroke = accent, StrokeThickness = 2, Fill = new SolidColorBrush(Th.Ink(0x10)) }
            : new System.Windows.Shapes.Rectangle { Width = t.Mark.Width, Height = t.Mark.Height, RadiusX = 1.5, RadiusY = 1.5, Fill = accent };
        Canvas.SetLeft(mark, t.Mark.X);
        Canvas.SetTop(mark, t.Mark.Y);
        _overlay.Children.Add(mark);
    }

    static void ClearMarks(Press p)
    {
        p.Tile.Opacity = 1;
        p.Tile.Cursor = Cursors.Hand;
        if (p.Moving) ShowMark(null);
    }

    /// <summary>The dragged item moved to its new place, saved, the dock's tile and the island drawn again.</summary>
    static void Drop(Press p, DropTarget t)
    {
        var items = p.Stack.Items!;
        int from = items.IndexOf(p.Item);
        if (from < 0) return;
        if (t.Up)
        {
            if (_path.Count < 2) return;
            items.RemoveAt(from);
            _path[^2].Items!.Add(p.Item);
        }
        else if (t.Into != null)
        {
            if (t.Into == p.Item) return;
            items.RemoveAt(from);
            t.Into.Items!.Add(p.Item);
        }
        else
        {
            int to = t.Index > from ? t.Index - 1 : t.Index;
            if (to == from || to < 0) return;
            items.RemoveAt(from);
            items.Insert(Math.Min(to, items.Count), p.Item);
        }
        Changed();
        Rebuild();
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
        m.Items.Add(new Separator());
        Item(L.T("Eksportuj stosy…"), () => { ExportStacks(null); Rebuild(); });
        Item(L.T("Importuj stosy…"), () =>
        {
            if (ImportStacks(null))
            {
                // a stack open here may have been replaced by the imported one: the island follows it (by its key), else the first stack
                var top = S.DockPins.FirstOrDefault(p => p.Key == _path[0].Key && p.Items != null) ?? S.DockPins.FirstOrDefault(p => p.Items != null);
                if (top != null && top != _path[0]) { _path.Clear(); _path.Add(top); _query = ""; }
                Changed();
            }
            Rebuild();
        });
        m.IsOpen = true;
    }

    public static DockPin NewStack(string name) => new() { Key = NewKey(), Name = name, Items = new() };

    static string NewKey() => "stack:" + Guid.NewGuid().ToString("N")[..12];

    // ---------- the stacks to a file and back ----------

    /// <summary>The export file: a marker (so another JSON file isn't taken for stacks) and the dock's stacks with everything inside.</summary>
    sealed class StackFile
    {
        public int TaskWallStacks { get; set; } = 1;
        public List<DockPin> Stacks { get; set; } = new();
    }

    static MessageBoxResult Message(Window? owner, string text, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information) =>
        owner != null ? MessageBox.Show(owner, text, "TaskWall", buttons, image) : MessageBox.Show(text, "TaskWall", buttons, image);

    /// <summary>The dock's stacks (nested ones inside them) to a .json file the user chooses.</summary>
    public static void ExportStacks(Window? owner)
    {
        var stacks = S.DockPins.Where(p => p.Items != null).ToList();
        if (stacks.Count == 0) { Message(owner, L.T("W docku nie ma stosów do wyeksportowania.")); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Eksportuj stosy"),
            Filter = L.T("Stosy TaskWall") + " (*.json)|*.json",
            FileName = L.T("Stosy TaskWall") + ".json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (owner != null ? dlg.ShowDialog(owner) != true : dlg.ShowDialog() != true) return;
        try { System.IO.File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(new StackFile { Stacks = stacks }, Json.Options)); }
        catch (Exception ex) { Log.Error("export stacks", ex); Message(owner, L.F("Nie udało się zapisać pliku: {0}", ex.Message), image: MessageBoxImage.Warning); }
    }

    /// <summary>
    /// Stacks from an exported file added to the dock (at its end). A stack the dock already has (the same key – exported from this
    /// computer, or imported before) asks: replace it, add the imported one beside it (with a new key), or skip it. Saved when
    /// anything came in; true then (the caller draws the dock again).
    /// </summary>
    public static bool ImportStacks(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("Importuj stosy"), Filter = L.T("Stosy TaskWall") + " (*.json)|*.json" };
        if (owner != null ? dlg.ShowDialog(owner) != true : dlg.ShowDialog() != true) return false;
        List<DockPin>? stacks;
        try
        {
            var text = System.IO.File.ReadAllText(dlg.FileName);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            stacks = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? System.Text.Json.JsonSerializer.Deserialize<List<DockPin>>(text, Json.Options) // a bare list of stacks
                : System.Text.Json.JsonSerializer.Deserialize<StackFile>(text, Json.Options)?.Stacks;
        }
        catch (Exception ex)
        {
            Log.Error("import stacks", ex);
            Message(owner, L.F("Nie udało się odczytać pliku: {0}", ex.Message), image: MessageBoxImage.Warning);
            return false;
        }
        stacks = stacks?.Where(s => s != null && s.Items != null).ToList();
        if (stacks is not { Count: > 0 }) { Message(owner, L.T("W pliku nie ma stosów TaskWall."), image: MessageBoxImage.Warning); return false; }
        int count = 0;
        foreach (var st in stacks)
        {
            Clean(st, 0);
            if (!st.Key.StartsWith("stack:")) st.Key = NewKey();
            int at = S.DockPins.FindIndex(p => p.Key == st.Key);
            if (at >= 0)
            {
                var answer = Message(owner, L.F("Stos „{0}” już jest w docku.\n\nTak – zastąp go importowanym\nNie – dodaj importowany obok\nAnuluj – pomiń go", Name(st)), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes) { S.DockPins[at] = st; count++; continue; }
                if (answer != MessageBoxResult.No) continue;
                st.Key = NewKey();
            }
            S.DockPins.Add(st);
            count++;
        }
        if (count == 0) return false;
        SettingsStore.Save(S);
        return true;
    }

    /// <summary>A read pin made whole: no null names or keys, nothing nested deeper than a stack can be shown.</summary>
    static void Clean(DockPin p, int depth)
    {
        p.Key ??= "";
        p.Name ??= "";
        if (p.Items == null) return;
        p.Items = depth > 8 ? new() : p.Items.Where(i => i != null).ToList();
        foreach (var i in p.Items) Clean(i, depth + 1);
        foreach (var s in p.Items.Where(i => i.Items != null && !i.Key.StartsWith("stack:"))) s.Key = NewKey();
    }

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
