using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The weather now, for a bar: the symbol and the temperature, then the sky in words, the feel, today's max / min,
/// the rain and the wind (the wide form) or only the sky (<see cref="Narrow"/>). Empty when the bar's weather is off.
/// </summary>
public sealed class WeatherBadge : StackPanel
{
    public WeatherBadge()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        WeatherService.Changed += Update;
    }

    /// <summary>Only the sky in words, no feel / max-min / rain / wind (a narrow bar).</summary>
    public bool Narrow { get; set; }

    /// <summary>Only the symbol and the temperature (a bar the dock fills): the rest in the tool tip.</summary>
    public bool Compact { get; set; }

    /// <summary>The temperature's size (the full-width bar's size).</summary>
    public double TextSize { get; set; } = 17;

    public void Update()
    {
        Children.Clear();
        if (!App.Settings.BarWeather || WeatherService.Current is not { } w) return;
        var (symbol, desc) = WeatherService.Describe(w.Code);
        if (!w.Day && w.Code <= 2) symbol = "☾"; // clear or a few clouds at night
        Children.Add(new TextBlock { Text = symbol, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = TextSize, Foreground = Ui.Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        Children.Add(new TextBlock { Text = $"{Math.Round(w.Temp):0}°", FontSize = TextSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var line = new TextBlock { FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines.Add(new Run(desc));
        if (!Narrow)
        {
            if (w.Feels is { } f && Math.Abs(f - w.Temp) >= 1) line.Inlines.Add(new Run("  ·  " + L.F("odczuwalna {0}°", Math.Round(f))) { Foreground = Ui.Res("FgFaint") });
            var today = WeatherService.On(DateTime.Today);
            // like the day headers: today's max / min, the chance of rain and how much, the wind now
            if (today != null)
                line.Inlines.Add(new Run($"  ·  {Math.Round(today.Max):0}°/{Math.Round(today.Min):0}°"
                    + (today.Rain is { } r && r > 0 ? $"  ☂ {r}%" + (today.Mm is >= 0.1 and var mm ? " · " + mm.ToString(mm < 10 ? "0.#" : "0", L.Culture) + " mm" : "") : "")) { Foreground = Ui.Res("FgFaint") });
            if (w.Wind is { } wn)
            {
                line.Inlines.Add(new Run("  ·  ") { Foreground = Ui.Res("FgFaint") });
                line.Inlines.Add(new InlineUIContainer(Icons.Wind(Ui.Res("FgFaint"), 12)) { BaselineAlignment = BaselineAlignment.Center });
                line.Inlines.Add(new Run($" {Math.Round(wn):0} km/h") { Foreground = Ui.Res("FgFaint") });
            }
        }
        if (!Compact) Children.Add(line);
        var day = WeatherService.On(DateTime.Today);
        ToolTip = $"{App.Settings.Weather.City}\n{desc}, {Math.Round(w.Temp):0}°"
            + (w.Feels is { } ff ? "\n" + L.F("odczuwalna {0}°", Math.Round(ff)) : "")
            + (w.Wind is { } wi ? "\n" + L.F("wiatr {0} km/h", Math.Round(wi)) : "")
            + (day?.Rain is { } dr ? "\n" + L.F("szansa opadów {0}%", dr) : "")
            + (day?.Mm is { } dmm ? "\n" + L.F("opady: {0} mm", dmm.ToString("0.#", L.Culture)) : "")
            + (w.Humidity is { } h ? "\n" + L.F("wilgotność {0}%", h) : "")
            + "\n" + L.F("pomiar z {0}", w.At.ToString("HH:mm"));
    }
}
