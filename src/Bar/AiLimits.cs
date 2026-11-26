using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// The usage limits of Claude and ChatGPT (Settings → Pasek → Limity AI, off by default): how much of the 5-hour window and of
/// the week is used, for the bar's two-ring meters. Read with the login the user's own tools keep on this computer – Claude Code
/// (%USERPROFILE%\.claude\.credentials.json) and the Codex CLI (%USERPROFILE%\.codex\auth.json) – and sent only to the service
/// that issued it (api.anthropic.com, chatgpt.com), every 3 minutes while the bar shows the rings. These are the endpoints the
/// tools' own /usage and /status use, not a public API: when one fails or the login has expired the ring says so in its tip.
/// The tokens are never logged or stored by TaskWall.
/// </summary>
static class AiLimits
{
    public sealed record Usage(double? FiveHour, double? Week, DateTimeOffset? FiveReset, DateTimeOffset? WeekReset, string? Problem);

    public static readonly Dictionary<string, Usage> Now = new();
    public static event Action? Changed;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static DispatcherTimer? _timer;
    static int _users;

    public static void Start()
    {
        if (_users++ > 0) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += (_, _) => _ = Read();
        _timer.Start();
        _ = Read();
    }

    public static void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        _timer?.Stop();
        _timer = null;
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static async Task Read()
    {
        var kinds = App.Settings.AiMeters.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var k in kinds)
        {
            Usage u;
            try { u = k == "claude" ? await Claude() : k == "chatgpt" ? await ChatGpt() : new Usage(null, null, null, null, "?"); }
            catch (Exception ex) { u = new Usage(null, null, null, null, ex.Message); Log.Error("ai limits " + k + ": " + ex.Message); }
            Now[k] = u;
        }
        Changed?.Invoke();
    }

    /// <summary>Claude (Pro / Max): GET api.anthropic.com/api/oauth/usage with Claude Code's login → five_hour / seven_day utilization.</summary>
    static async Task<Usage> Claude()
    {
        var file = Path.Combine(Home, ".claude", ".credentials.json");
        if (!File.Exists(file)) return new Usage(null, null, null, null, L.T("Zaloguj się w Claude Code na tym komputerze."));
        using var cred = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var oauth = cred.RootElement.GetProperty("claudeAiOauth");
        var token = oauth.GetProperty("accessToken").GetString();
        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow)
            return new Usage(null, null, null, null, L.T("Logowanie Claude Code wygasło – uruchom Claude Code, aby je odświeżyć."));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Add("Authorization", "Bearer " + token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return new Usage(null, null, null, null, L.F("Claude odpowiedział {0}.", (int)res.StatusCode));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        (double?, DateTimeOffset?) Window(string name) =>
            doc.RootElement.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
                ? (w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null,
                   w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var at) ? at : null)
                : (null, null);
        var (five, fiveAt) = Window("five_hour");
        var (week, weekAt) = Window("seven_day");
        return new Usage(five, week, fiveAt, weekAt, null);
    }

    /// <summary>ChatGPT (Plus / Pro, Codex): GET chatgpt.com/backend-api/wham/usage with the Codex CLI's login → the primary (5 h) and secondary (week) windows.</summary>
    static async Task<Usage> ChatGpt()
    {
        var file = Path.Combine(Home, ".codex", "auth.json");
        if (!File.Exists(file)) return new Usage(null, null, null, null, L.T("Zaloguj się w Codex CLI (codex login) na tym komputerze."));
        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        if (!auth.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return new Usage(null, null, null, null, L.T("Codex CLI nie jest zalogowany kontem ChatGPT."));
        var token = tokens.GetProperty("access_token").GetString();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Add("Authorization", "Bearer " + token);
        if (tokens.TryGetProperty("account_id", out var acc) && acc.GetString() is { Length: > 0 } id) req.Headers.Add("ChatGPT-Account-Id", id);
        req.Headers.Add("User-Agent", "codex_cli_rs");
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return new Usage(null, null, null, null, L.F("ChatGPT odpowiedział {0}.", (int)res.StatusCode));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object) return new Usage(null, null, null, null, L.T("Brak limitów w odpowiedzi."));
        (double?, DateTimeOffset?) Window(string name) =>
            rl.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
                ? (w.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null,
                   w.TryGetProperty("reset_after_seconds", out var r) && r.ValueKind == JsonValueKind.Number ? DateTimeOffset.Now.AddSeconds(r.GetDouble()) : null)
                : (null, null);
        var (five, fiveAt) = Window("primary_window");
        var (week, weekAt) = Window("secondary_window");
        return new Usage(five, week, fiveAt, weekAt, null);
    }

    /// <summary>The tip: "Claude · 5 h: 42 % (do 14:30) · tydzień: 18 % (do pon 09:00)".</summary>
    public static string Describe(string name, Usage? u)
    {
        if (u == null) return name + " – " + L.T("wczytuję…");
        if (u.Problem != null) return name + " – " + u.Problem;
        static string At(DateTimeOffset? t) => t is { } v ? " (" + L.F("do {0}", v.ToLocalTime().ToString(v.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", L.Culture)) + ")" : "";
        return $"{name}\n" + L.F("5 godzin: {0}", u.FiveHour is { } f ? $"{f:0}%" : "—") + At(u.FiveReset)
            + "\n" + L.F("tydzień: {0}", u.Week is { } w ? $"{w:0}%" : "—") + At(u.WeekReset);
    }
}
