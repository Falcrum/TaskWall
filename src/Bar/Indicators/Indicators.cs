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

    public BarPanel()
    {
        Radius = 12;
        StaysOnTop = true;
        ShowActivated = true;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        _content.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        Body = _content;
        Deactivated += (_, _) => { if (IsVisible) { Hide(); _closedAt = DateTime.Now; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    /// <summary>Just closed by the click that would open it again.</summary>
    public bool JustClosed => (DateTime.Now - _closedAt).TotalMilliseconds < 400;

    /// <summary>Opens with <paramref name="content"/> at <paramref name="anchor"/> (a button of <paramref name="bar"/>), inside the bar's width.</summary>
    public void Open(FrameworkElement content, FrameworkElement anchor, BarWindow bar)
    {
        _content.Child = content;
        var dpi = VisualTreeHelper.GetDpi(bar);
        double ui = App.Settings.UiScale;
        ApplyScale(ui);
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(_content.DesiredSize.Width * ui * dpi.DpiScaleX), h = (int)Math.Ceiling(_content.DesiredSize.Height * ui * dpi.DpiScaleY);
        var frame = bar.FrameRect;
        var a = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        int gap = (int)Math.Round(6 * dpi.DpiScaleY);
        int x = Math.Clamp((int)Math.Round(a.X - w / 2.0), frame.X, Math.Max(frame.X, frame.Right - w));
        int y = bar.Top ? frame.Bottom + gap : frame.Y - h - gap;
        PlacePhysical(x, y, w, h, dpi.DpiScaleX);
        Show();
        Activate();
        Anim.Enter(FrameElement, 0, bar.Top ? -8 : 8, 220);
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
        VerticalAlignment = VerticalAlignment.Center;
        Volume.Changed += () => Dispatcher.BeginInvoke(Show);
        Volume.DeviceChanged += () => Dispatcher.BeginInvoke(() => { Volume.Reopen(); Show(); });
        Click += (_, _) => { if (_panel.IsVisible || _panel.JustClosed) { _panel.Hide(); return; } OpenPanel(); };
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { Volume.Muted = !Volume.Muted; e.Handled = true; } };
        PreviewMouseWheel += (_, e) => { if (Volume.Level is { } l) Volume.Level = l + Math.Sign(e.Delta) * 0.02; e.Handled = true; };
        Show();
    }

    /// <summary>The icon, the tool tip and an open slider after any change.</summary>
    public void Show()
    {
        var level = Volume.Level;
        bool muted = Volume.Muted;
        int pct = (int)Math.Round((level ?? 0) * 100);
        int waves = level is not { } l || l <= 0.001 ? 0 : l < 0.34 ? 1 : l < 0.67 ? 2 : 3;
        Content = Icons.Speaker(waves, muted || level == null);
        ToolTip = level == null ? L.T("Brak głośników") : (Volume.DeviceName.Length > 0 ? Volume.DeviceName + "\n" : "") + (muted ? L.F("Wyciszone ({0}%)", pct) : L.F("Głośność {0}%", pct)) + "\n" + L.T("Kółko: głośniej / ciszej · środkowy przycisk: wycisz");
        if (_slider != null && !_setting) _slider.Value = pct;
        if (_percent != null) _percent.Text = muted ? L.T("wyciszone") : $"{pct}%";
        if (_mute != null) _mute.Content = Icons.Speaker(waves, muted);
    }

    void OpenPanel()
    {
        var root = new StackPanel { Width = 280 };
        root.Children.Add(BarPanel.Text(Volume.DeviceName.Length > 0 ? Volume.DeviceName : L.T("Głośniki"), 11.5, "FgDim"));
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
        VerticalAlignment = VerticalAlignment.Center;
        _soon.Tick += (_, _) => { _soon.Stop(); Refresh(); };
        _signal.Tick += (_, _) => Refresh();
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(Soon);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(Soon);
        Click += (_, _) => { if (_panel.IsVisible || _panel.JustClosed) { _panel.Hide(); return; } Refresh(); OpenPanel(); };
        Refresh();
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
