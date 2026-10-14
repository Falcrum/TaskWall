using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>Weather layer settings (per computer).</summary>
public sealed class WeatherSettings
{
    public bool Enabled { get; set; }
    public string City { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Show the precipitation chance next to the temperatures.</summary>
    public bool ShowRain { get; set; } = true;
    /// <summary>Show the day's top wind speed (km/h).</summary>
    public bool ShowWind { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public bool Ready => Enabled && (Latitude != 0 || Longitude != 0);
}

/// <summary>One day of the forecast.</summary>
public sealed record WeatherDay(DateTime Date, int Code, double Max, double Min, int? Rain, double? Wind = null)
{
    /// <summary>Expected rain / snow of the day in mm.</summary>
    public double? Mm { get; init; }
}

/// <summary>
/// The weather layer: a 16-day forecast from Open-Meteo (free, no key, no account), shown in the day headers.
/// Read on start and every 3 hours, kept on disk for offline starts. Only the city's coordinates are sent.
/// </summary>
static class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string CacheFile = Path.Combine(SettingsStore.Dir, "weather-cache.json");
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromHours(3) };
    static Dictionary<DateTime, WeatherDay> _days = new();
    public static DateTime? Fetched { get; private set; }
    public static string? Error { get; private set; }
    public static event Action? Changed;

    public static WeatherDay? On(DateTime date) => App.Settings.Weather.Ready && _days.TryGetValue(date.Date, out var d) ? d : null;

    public static void Start()
    {
        LoadCache();
        Timer.Tick += (_, _) => Refresh();
        Timer.Start();
        if (Fetched == null || DateTime.Now - Fetched > TimeSpan.FromHours(3)) Refresh();
    }

    public static async void Refresh()
    {
        var w = App.Settings.Weather;
        if (!w.Ready) return;
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={w.Latitude:0.####}&longitude={w.Longitude:0.####}&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max&timezone=auto&forecast_days=16");
            var json = await Http.GetStringAsync(url);
            _days = Parse(json);
            Fetched = DateTime.Now;
            Error = null;
            try { File.WriteAllText(CacheFile, json); } catch { }
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException or TaskCanceledException ? L.T("Brak połączenia z serwerem pogody.") : ex.Message;
            Log.Error("weather", ex);
        }
        Changed?.Invoke();
    }

    public static Dictionary<DateTime, WeatherDay> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = doc.RootElement.GetProperty("daily");
        var time = d.GetProperty("time").EnumerateArray().Select(x => DateTime.ParseExact(x.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        double Num(JsonElement arr, int i) => arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : double.NaN;
        var code = d.GetProperty("weather_code");
        var max = d.GetProperty("temperature_2m_max");
        var min = d.GetProperty("temperature_2m_min");
        d.TryGetProperty("precipitation_probability_max", out var rain);
        d.TryGetProperty("wind_speed_10m_max", out var wind);
        d.TryGetProperty("precipitation_sum", out var sum);
        var result = new Dictionary<DateTime, WeatherDay>();
        for (int i = 0; i < time.Count; i++)
        {
            if (double.IsNaN(Num(max, i))) continue;
            int? r = rain.ValueKind == JsonValueKind.Array && rain[i].ValueKind == JsonValueKind.Number ? (int)rain[i].GetDouble() : null;
            double? wnd = wind.ValueKind == JsonValueKind.Array && wind[i].ValueKind == JsonValueKind.Number ? wind[i].GetDouble() : null;
            double? mm = sum.ValueKind == JsonValueKind.Array && sum[i].ValueKind == JsonValueKind.Number ? sum[i].GetDouble() : null;
            result[time[i]] = new WeatherDay(time[i], code[i].ValueKind == JsonValueKind.Number ? code[i].GetInt32() : 0, Num(max, i), Num(min, i), r, wnd) { Mm = mm };
        }
        return result;
    }

    static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            _days = Parse(File.ReadAllText(CacheFile));
            // a cache from before the wind / rain amount were read: fetch again right away
            Fetched = _days.Values.Any(d => d.Wind != null && d.Mm != null) ? File.GetLastWriteTime(CacheFile) : null;
        }
        catch (Exception ex) { Log.Error("weather cache", ex); }
    }

    /// <summary>City search (Open-Meteo geocoding): name, region, country, coordinates.</summary>
    public static async Task<List<(string Label, double Lat, double Lon)>> Find(string name)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language={(L.En ? "en" : "pl")}&format=json";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, double, double)>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            string S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var label = string.Join(", ", new[] { S("name"), S("admin1"), S("country") }.Where(x => x.Length > 0).Distinct());
            list.Add((label, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }

    /// <summary>WMO weather code → a symbol (Segoe UI Symbol, monochrome) and a description.</summary>
    public static (string Symbol, string Text) Describe(int code) => code switch
    {
        0 => ("☀", L.T("bezchmurnie")),
        1 or 2 => ("⛅", L.T("częściowe zachmurzenie")),
        3 => ("☁", L.T("pochmurno")),
        45 or 48 => ("≋", L.T("mgła")),
        >= 51 and <= 57 => ("🌧", L.T("mżawka")),
        >= 61 and <= 67 => ("🌧", L.T("deszcz")),
        >= 71 and <= 77 => ("❄", L.T("śnieg")),
        >= 80 and <= 82 => ("🌧", L.T("przelotne opady")),
        85 or 86 => ("❄", L.T("przelotny śnieg")),
        >= 95 => ("⚡", L.T("burza")),
        _ => ("·", ""),
    };
}
