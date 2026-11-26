using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// A small panel that opens from the full-width bar under (or over) one of its buttons: the volume's slider, the network's
/// details. It closes when it loses the focus, like the calendar; a click on its button while it is open only closes it.
/// </summary>
sealed class BarPanel : GlassWindow
{
    readonly Border _content = new() { Padding = new Thickness(16, 12, 16, 14) };
    DateTime _closedAt;
    /// <summary>Lost the focus a moment ago: it closes in a quarter of a second unless the click was meant for it (another stack: the panel moves there instead).</summary>
    readonly System.Windows.Threading.DispatcherTimer _closeSoon = new() { Interval = TimeSpan.FromMilliseconds(260) };
    bool _fading;

    public BarPanel()
    {
        Radius = 12;
        StaysOnTop = true;
        ShowActivated = true;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        // the same frosted glass as the bar (painted when placed), not a flat panel
        Body = _content;
        Deactivated += (_, _) => { if (IsVisible) { _closedAt = DateTime.Now; _closeSoon.Stop(); _closeSoon.Start(); } };
        _closeSoon.Tick += (_, _) => { _closeSoon.Stop(); FadeOut(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Escaping?.Invoke() != true) FadeOut(); };
        FrameElement.RenderTransformOrigin = new Point(0.5, 1);
    }

    /// <summary>Esc pressed: true when the content used it itself (a search cleared) and the panel stays open.</summary>
    public Func<bool>? Escaping { get; set; }

    /// <summary>About to close (the focus went elsewhere a moment ago).</summary>
    public bool ClosingSoon => _closeSoon.IsEnabled;

    /// <summary>Stays open after all (the click that took the focus was for this panel: another stack in it).</summary>
    public void KeepOpen() => _closeSoon.Stop();

    /// <summary>Closed softly: fading and sinking a little towards the bar.</summary>
    public void FadeOut()
    {
        _closeSoon.Stop();
        if (!IsVisible || _fading) return;
        if (!Anim.On) { Hide(); return; }
        _fading = true;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            _fading = false;
            Hide();
            FrameElement.BeginAnimation(OpacityProperty, null);
            FrameElement.Opacity = 1;
        };
        FrameElement.BeginAnimation(OpacityProperty, fade);
        var scale = new ScaleTransform(1, 1);
        FrameElement.RenderTransform = scale;
        var shrink = new System.Windows.Media.Animation.DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(140));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Just closed by the click that would open it again.</summary>
    public bool JustClosed => (DateTime.Now - _closedAt).TotalMilliseconds < 400;

    /// <summary>Opens with <paramref name="content"/> at <paramref name="anchor"/> (a button of <paramref name="bar"/>), inside the bar's width.</summary>
    /// <summary>In the middle of the screen (the bar's glass) instead of at its button: the search's field.</summary>
    public bool Centred { get; set; }

    /// <param name="activate">false: shown without taking the focus from the window in front (a stack opened by hovering its tile)</param>
    public void Open(FrameworkElement content, FrameworkElement anchor, BarWindow bar, bool activate = true)
    {
        _closeSoon.Stop();
        bool moving = IsVisible && !_fading;
        _content.Child = content;
        _anchor = anchor;
        _bar = bar;
        FrameElement.RenderTransformOrigin = new Point(0.5, bar.AtTop ? 0 : 1);
        if (moving)
        {
            // already open (another stack, a stack inside): glide to the new place and size, the new content fading in
            Glide();
            Anim.Enter(content, 0, 0, 180);
            if (activate) Activate();
            return;
        }
        _fading = false;
        FrameElement.BeginAnimation(OpacityProperty, null);
        Place();
        App.Instance.PaintGlass(this);
        ShowActivated = activate;
        Show();
        ShowActivated = true;
        if (activate) Activate();
        if (!Anim.On) return;
        // opening: growing a little out of the bar and fading in
        var scale = new ScaleTransform(0.94, 0.94);
        FrameElement.RenderTransform = scale;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var grow = new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        FrameElement.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>From where it is to where its new content wants it, over a few frames (eased), the glass painted again at the end.</summary>
    void Glide()
    {
        var from = PhysicalRect;
        var to = Target();
        if (!Anim.On || from.Width <= 0) { PlacePhysical(to.X, to.Y, to.Width, to.Height, to.Scale); App.Instance.PaintGlass(this); return; }
        var start = DateTime.Now;
        EventHandler? step = null;
        step = (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 180);
            double k = 1 - Math.Pow(1 - t, 3);
            int L(int a, int b) => (int)Math.Round(a + (b - a) * k);
            PlacePhysical(L(from.X, to.X), L(from.Y, to.Y), L(from.Width, to.Width), L(from.Height, to.Height), to.Scale);
            if (t >= 1) { CompositionTarget.Rendering -= step; App.Instance.PaintGlass(this); }
        };
        CompositionTarget.Rendering += step;
    }

    FrameworkElement? _anchor;
    BarWindow? _bar;

    /// <summary>The content changed its size (the search's results, another stack): placed again at its button, no animation.</summary>
    public void Reflow()
    {
        if (_anchor == null || _bar == null || !IsVisible) return;
        Place();
        App.Instance.PaintGlass(this);
    }

    void Place()
    {
        var t = Target();
        PlacePhysical(t.X, t.Y, t.Width, t.Height, t.Scale);
    }

    /// <summary>Where the panel goes for its content: at its button (or the screen's middle), next to the bar's glass.</summary>
    (int X, int Y, int Width, int Height, double Scale) Target()
    {
        var anchor = _anchor!;
        var bar = _bar!;
        var dpi = VisualTreeHelper.GetDpi(bar);
        double ui = App.Settings.UiScale;
        ApplyScale(ui);
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(_content.DesiredSize.Width * ui * dpi.DpiScaleX), h = (int)Math.Ceiling(_content.DesiredSize.Height * ui * dpi.DpiScaleY);
        var frame = bar.FrameRect;
        var a = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        int gap = (int)Math.Round(6 * dpi.DpiScaleY);
        int x = Centred ? frame.X + (frame.Width - w) / 2 : Math.Clamp((int)Math.Round(a.X - w / 2.0), frame.X, Math.Max(frame.X, frame.Right - w));
        int y = bar.AtTop ? frame.Bottom + gap : frame.Y - h - gap;
        return (x, y, w, h, dpi.DpiScaleX);
    }

    public static TextBlock Text(string text, double size = 12, string brush = "Fg", FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = Ui.Res(brush), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Link(string text, string uri)
    {
        var t = Text(text, 12, "AccentBrush");
        t.Cursor = Cursors.Hand;
        t.Margin = new Thickness(0, 10, 0, 0);
        t.MouseLeftButtonUp += (_, _) => { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("open " + uri, ex); } };
        return t;
    }
}

/// <summary>
/// The speakers on the bar: a speaker with waves for the volume (crossed when muted). The wheel turns it up and down, the middle
/// button mutes, a click opens a slider with the device's name. It follows changes made anywhere (the keyboard's volume keys).
/// </summary>
sealed class VolumeButton : Button
{
    readonly BarWindow _bar;
    readonly BarPanel _panel = new();
    Slider? _slider;
    TextBlock? _percent;
    Button? _mute;
    bool _setting;

    public VolumeButton(BarWindow bar)
    {
        _bar = bar;
        Style = (Style)Application.Current.Resources["BarIcon"];
        MinWidth = MinHeight = 32;
        VerticalAlignment = VerticalAlignment.Center;
        Volume.Changed += OnVolume;
        Volume.DeviceChanged += OnDevice;
        Click += (_, _) => { if (_panel.IsVisible || _panel.JustClosed) { _panel.Hide(); return; } OpenPanel(); };
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { Volume.Muted = !Volume.Muted; e.Handled = true; } };
        PreviewMouseWheel += (_, e) => { if (Volume.Level is { } l) Volume.Level = l + Math.Sign(e.Delta) * 0.02; e.Handled = true; };
        Show();
    }

    /// <summary>The icon, the tool tip and an open slider after any change.</summary>
    void OnVolume(bool own) => Dispatcher.BeginInvoke(() => Show(own));
    void OnDevice() => Dispatcher.BeginInvoke(() => { Volume.Reopen(); Show(); FillOutputs(); if (_deviceName != null) _deviceName.Text = Volume.DeviceName; });

    /// <summary>The bar closed: off the static events (they would keep this button and its bar alive), the panel closed.</summary>
    public void Detach()
    {
        Volume.Changed -= OnVolume;
        Volume.DeviceChanged -= OnDevice;
        _panel.Close();
    }

    /// <param name="own">the change came from this slider: the icon and the number follow, the slider stays under the hand</param>
    public void Show(bool own = false)
    {
        var level = Volume.Level;
        bool muted = Volume.Muted;
        int pct = (int)Math.Round((level ?? 0) * 100);
        int waves = level is not { } l || l <= 0.001 ? 0 : l < 0.34 ? 1 : l < 0.67 ? 2 : 3;
        Content = Icons.Speaker(waves, muted || level == null);
        System.Windows.Automation.AutomationProperties.SetName(this, level == null ? L.T("Brak głośników") : L.F("Głośność {0}%", pct));
        ToolTip = level == null ? L.T("Brak głośników") : (Volume.DeviceName.Length > 0 ? Volume.DeviceName + "\n" : "") + (muted ? L.F("Wyciszone ({0}%)", pct) : L.F("Głośność {0}%", pct)) + "\n" + L.T("Kółko: głośniej / ciszej · środkowy przycisk: wycisz");
        if (_slider != null && !_setting && !own) _slider.Value = pct;
        if (_percent != null) _percent.Text = muted ? L.T("wyciszone") : $"{pct}%";
        if (_mute != null) _mute.Content = Icons.Speaker(waves, muted);
    }

    StackPanel? _outputs;
    TextBlock? _deviceName;

    void FillOutputs()
    {
        if (_outputs == null) return;
        _outputs.Children.Clear();
        var (devices, current) = Volume.Outputs();
        foreach (var d in devices)
        {
            bool on = d.Id == current;
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Background = on ? Ui.Res("AccentBrush") : Brushes.Transparent };
            var name = new TextBlock { Text = d.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Ui.Res(on ? "Fg" : "FgDim"), ToolTip = d.Name };
            var line = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            line.Children.Add(dot);
            line.Children.Add(name);
            var item = new Border { Child = line, Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(6), Cursor = on ? null : Cursors.Hand };
            Anim.HoverBackground(item, Colors.Transparent, Th.Ink(0x14));
            var id = d.Id;
            if (!on) item.MouseLeftButtonUp += (_, _) => Volume.SetDefault(id); // DeviceChanged follows: the icon, the slider and this list
            _outputs.Children.Add(item);
        }
        if (devices.Count == 0) _outputs.Children.Add(BarPanel.Text(L.T("Brak urządzeń wyjściowych"), 12, "FgFaint"));
    }

    void OpenPanel()
    {
        var root = new StackPanel { Width = 300 };
        _deviceName = BarPanel.Text(Volume.DeviceName.Length > 0 ? Volume.DeviceName : L.T("Głośniki"), 11.5, "FgDim");
        root.Children.Add(_deviceName);
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _mute = new Button { Style = (Style)Application.Current.Resources["BarIcon"], ToolTip = L.T("Wycisz / włącz dźwięk"), VerticalAlignment = VerticalAlignment.Center };
        _mute.Click += (_, _) => Volume.Muted = !Volume.Muted;
        DockPanel.SetDock(_mute, Dock.Left);
        row.Children.Add(_mute);
        _percent = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(_percent, Dock.Right);
        row.Children.Add(_percent);
        _slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        _slider.ValueChanged += (_, e) => { if (!_slider.IsLoaded) return; _setting = true; Volume.Level = e.NewValue / 100; _setting = false; };
        row.Children.Add(_slider);
        root.Children.Add(row);
        // the output devices, the default one marked – a click makes another the default (like Windows' own volume panel)
        root.Children.Add(BarPanel.Text(L.T("Urządzenie wyjściowe"), 11.5, "FgDim"));
        ((FrameworkElement)root.Children[^1]).Margin = new Thickness(0, 12, 0, 4);
        _outputs = new StackPanel();
        FillOutputs();
        root.Children.Add(_outputs);
        root.Children.Add(BarPanel.Link(L.T("Ustawienia dźwięku…"), "ms-settings:sound"));
        Show();
        _panel.Open(root, this, _bar);
    }
}

/// <summary>
/// The connection on the bar: Wi-Fi arcs for the signal, a screen for a cable, a crossed globe without a network (or without the
/// internet: the icon faded). A click opens the details – the network's name, the signal, the address, the speed – with a way
/// to Windows' network settings. Windows tells about changes; the Wi-Fi signal is read again every half minute while on Wi-Fi.
/// </summary>
sealed class NetworkButton : Button
{
    readonly BarWindow _bar;
    readonly BarPanel _panel = new();
    readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly DispatcherTimer _signal = new() { Interval = TimeSpan.FromSeconds(30) };
    Network.State _state = new(Network.Kind.None, "", 0, false, "", 0, "");
    bool _paused;

    public NetworkButton(BarWindow bar)
    {
        _bar = bar;
        Style = (Style)Application.Current.Resources["BarIcon"];
        MinWidth = MinHeight = 32;
        VerticalAlignment = VerticalAlignment.Center;
        _soon.Tick += (_, _) => { _soon.Stop(); Refresh(); };
        _signal.Tick += (_, _) => Refresh();
        NetworkChange.NetworkAddressChanged += OnAddress;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        Click += (_, _) => { if (_panel.IsVisible || _panel.JustClosed) { _panel.Hide(); return; } Refresh(); OpenPanel(); };
        Refresh();
    }

    void OnAddress(object? s, EventArgs e) => Dispatcher.BeginInvoke(Soon);
    void OnAvailability(object? s, NetworkAvailabilityEventArgs e) => Dispatcher.BeginInvoke(Soon);

    /// <summary>The bar closed: off Windows' network events, the timers stopped, the panel closed.</summary>
    public void Detach()
    {
        NetworkChange.NetworkAddressChanged -= OnAddress;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
        _soon.Stop();
        _signal.Stop();
        _panel.Close();
    }

    // a change comes in bursts (an address, then the gateway, then the internet check): read once it settles
    void Soon() { if (_paused) return; _soon.Stop(); _soon.Start(); }

    /// <summary>Full screen, locked: nothing read; back with a fresh look.</summary>
    public void Pause(bool paused)
    {
        _paused = paused;
        if (paused) { _signal.Stop(); _soon.Stop(); }
        else Refresh();
    }

    void Refresh()
    {
        _state = Network.Read();
        var s = _state;
        int arcs = s.Signal >= 70 ? 3 : s.Signal >= 40 ? 2 : s.Signal > 0 ? 1 : 0;
        FrameworkElement icon = s.Kind switch { Network.Kind.Wifi => Icons.Wifi(arcs), Network.Kind.None => Icons.NoNetwork(), _ => Icons.Cable() };
        icon.Opacity = s.Kind != Network.Kind.None && !s.Internet ? 0.45 : 1; // connected, but no internet: faded
        Content = icon;
        System.Windows.Automation.AutomationProperties.SetName(this, s.Kind == Network.Kind.None ? L.T("Brak połączenia z siecią") : s.Name);
        ToolTip = s.Kind switch
        {
            Network.Kind.None => L.T("Brak połączenia z siecią"),
            Network.Kind.Wifi => $"{s.Name}  ·  Wi-Fi {s.Signal}%" + (s.Internet ? "" : "\n" + L.T("Bez dostępu do internetu")),
            _ => s.Name + (s.Internet ? "" : "\n" + L.T("Bez dostępu do internetu")),
        };
        if (s.Kind == Network.Kind.Wifi && !_paused) { if (!_signal.IsEnabled) _signal.Start(); } else _signal.Stop();
    }

    void OpenPanel()
    {
        var s = _state;
        var root = new StackPanel { Width = 280 };
        root.Children.Add(BarPanel.Text(s.Kind switch { Network.Kind.Wifi => "Wi-Fi", Network.Kind.Cable => L.T("Połączenie kablowe"), Network.Kind.None => L.T("Brak połączenia"), _ => L.T("Połączenie") }, 11.5, "FgDim"));
        if (s.Kind != Network.Kind.None)
        {
            root.Children.Add(BarPanel.Text(s.Name, 15, "Fg", FontWeights.SemiBold));
            void Row(string label, string value)
            {
                var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                t.Inlines.Add(new Run(label + "  ") { Foreground = Ui.Res("FgFaint") });
                t.Inlines.Add(new Run(value) { Foreground = Ui.Res("Fg") });
                root.Children.Add(t);
            }
            Row(L.T("Internet"), s.Internet ? L.T("jest") : L.T("brak"));
            if (s.Kind == Network.Kind.Wifi) Row(L.T("Sygnał"), $"{s.Signal}%");
            if (s.SpeedBps > 0) Row(L.T("Prędkość łącza"), s.SpeedBps >= 1_000_000_000 ? $"{s.SpeedBps / 1e9:0.#} Gb/s" : $"{s.SpeedBps / 1e6:0} Mb/s");
            if (s.Address.Length > 0) Row(L.T("Adres"), s.Address);
            if (s.Adapter.Length > 0) Row(L.T("Karta"), s.Adapter);
        }
        root.Children.Add(BarPanel.Link(L.T("Ustawienia sieci…"), "ms-settings:network"));
        if (s.Kind is Network.Kind.Wifi or Network.Kind.None) root.Children.Add(BarPanel.Link(L.T("Sieci Wi-Fi…"), "ms-settings:network-wifi"));
        _panel.Open(root, this, _bar);
    }
}
