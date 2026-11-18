using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// "19:43  PONIEDZIAŁEK 26 PAŹDZIERNIK ▾" in one line, for a bar: ticks every second while it runs (<see cref="Pause"/>
/// stops it), a click raises <see cref="Clicked"/> (the bar opens the month calendar).
/// </summary>
public sealed class ClockFace : Border
{
    readonly TextBlock _time = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _date = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 2, 0, 0) };
    readonly TextBlock _chevron = new() { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 2, 0, 0) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    string _shown = "";

    public event Action? Clicked;
    /// <summary>Every ten minutes (the bar's "now" weather gets old).</summary>
    public event Action? TenMinutes;

    public ClockFace()
    {
        _date.Foreground = Ui.Res("FgDim");
        _chevron.Foreground = Ui.Res("FgFaint");
        var clock = new StackPanel { Orientation = Orientation.Horizontal };
        clock.Children.Add(_time);
        clock.Children.Add(_date);
        clock.Children.Add(_chevron);
        Child = clock;
        Padding = new Thickness(12, 0, 10, 0);
        Margin = new Thickness(0, 4, 0, 4);
        CornerRadius = new CornerRadius(9);
        Cursor = Cursors.Hand;
        ToolTip = L.T("Kalendarz");
        Anim.HoverBackground(this, Colors.Transparent, Th.Ink(0x18));
        MouseLeftButtonUp += (_, e) => { e.Handled = true; Clicked?.Invoke(); };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    /// <summary>The chevron points up when the calendar opens above (a bar at the bottom of the screen).</summary>
    public bool OpensUp { set => _chevron.Text = value ? "▴" : "▾"; }

    /// <summary>Stops the second timer (full screen, locked screen); <c>false</c> starts it again and shows the time at once.</summary>
    public void Pause(bool paused)
    {
        if (paused) _timer.Stop();
        else { _timer.Start(); Update(); }
    }

    /// <summary>Shown afresh (settings changed: 12 / 24 h, language).</summary>
    public void Refresh() { _shown = ""; Update(); }

    void Update()
    {
        var now = DateTime.Now;
        var time = App.Settings.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm", CultureInfo.InvariantCulture);
        // PONIEDZIAŁEK 26 PAŹDZIERNIK / MONDAY 26 OCTOBER (month in the nominative, like a wall calendar)
        var date = L.LongDay(now);
        if (time + date == _shown) return;
        bool newMinute = _shown.Length > 0;
        _shown = time + date;
        _time.Text = time;
        _date.Text = App.Settings.Use24h ? date : $"{date} · {now.ToString("tt", CultureInfo.InvariantCulture)}";
        if (newMinute && now.Minute % 10 == 0) TenMinutes?.Invoke();
    }
}
