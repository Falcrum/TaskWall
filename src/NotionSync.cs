using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeskWall;

/// <summary>
/// Reads one Notion database with the user's personal access token (the token acts as the user: no admin, no
/// integration to share pages with) and brings its pages into the account's backlog. Only reads – nothing is ever
/// written to Notion. Matching by page id: new pages are added, known ones get their title / status / priority /
/// estimate updated, pages done in Notion can tick their task off.
/// </summary>
static class NotionSync
{
    const string Api = "https://api.notion.com/v1/";
    const string Version = "2025-09-03"; // databases → data sources
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly HashSet<string> Running = new();

    public sealed record Result(int Added, int Updated, int Total, string? Error);

    /// <summary>A parsed page (title, status …), independent of the API's JSON.</summary>
    public sealed record Page(string Id, string Title, string? Status, string? Priority, double? Estimate, string Url, List<string> People);

    // ---------- token storage (DPAPI, current Windows user) ----------

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    static byte[]? Crypt(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect
                ? CryptProtectData(ref input, "DeskWall Notion", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    public static string? Protect(string token) => Crypt(Encoding.UTF8.GetBytes(token.Trim()), true) is { } b ? Convert.ToBase64String(b) : null;

    static string? Token(NotionLink link)
    {
        if (link.TokenProtected == null) return null;
        try { return Crypt(Convert.FromBase64String(link.TokenProtected), false) is { } b ? Encoding.UTF8.GetString(b) : null; }
        catch { return null; }
    }

    // ---------- API ----------

    static async Task<JsonDocument> Call(string token, HttpMethod method, string path, object? body = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, Api + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Notion-Version", Version);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 429 && attempt < 3) // rate limited: wait as told
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new NotionError(res.StatusCode, Message(text));
            return JsonDocument.Parse(text);
        }
    }

    static string Message(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? json : json; }
        catch { return json.Length > 200 ? json[..200] : json; }
    }

    sealed class NotionError(HttpStatusCode code, string message) : Exception(message)
    {
        public HttpStatusCode Code { get; } = code;
    }

    /// <summary>Reads the whole database (all pages, up to 5 000).</summary>
    public static async Task<(string Title, List<Page> Pages, string? MeId)> Read(NotionLink link)
    {
        var token = Token(link) ?? throw new InvalidOperationException(L.T("Brak tokenu (albo został zapisany na innym koncie Windows) – wklej go ponownie."));
        var id = NotionImport.ExtractId(link.Database) ?? throw new InvalidOperationException(L.T("Nie rozpoznaję linku do bazy. Skopiuj link do widoku bazy (••• → Copy link to view)."));

        string? me = null;
        try
        {
            using var u = await Call(token, HttpMethod.Get, "users/me");
            var r = u.RootElement;
            me = r.GetProperty("type").GetString() == "person" ? r.GetProperty("id").GetString()
                : r.TryGetProperty("bot", out var bot) && bot.TryGetProperty("owner", out var owner) && owner.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var uid) ? uid.GetString() : null;
        }
        catch (NotionError e) when (e.Code == HttpStatusCode.Unauthorized) { throw new InvalidOperationException(L.T("Notion nie przyjął tokenu (wygasł albo jest błędny).")); }
        catch (NotionError) { /* "me" is only needed for "only mine" */ }

        string title = "";
        string sourceId;
        try
        {
            using var db = await Call(token, HttpMethod.Get, "databases/" + id);
            title = PlainText(db.RootElement, "title");
            sourceId = db.RootElement.TryGetProperty("data_sources", out var ds) && ds.GetArrayLength() > 0
                ? ds[0].GetProperty("id").GetString()! : id;
        }
        catch (NotionError e) when (e.Code == HttpStatusCode.NotFound)
        {
            // maybe the link points at a data source itself
            sourceId = id;
        }

        var pages = new List<Page>();
        string? cursor = null;
        do
        {
            var body = new Dictionary<string, object> { ["page_size"] = 100 };
            if (cursor != null) body["start_cursor"] = cursor;
            JsonDocument q;
            try { q = await Call(token, HttpMethod.Post, $"data_sources/{sourceId}/query", body); }
            catch (NotionError e) when (e.Code is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException(L.T("Nie widzę tej bazy. Sprawdź link i to, czy masz do niej dostęp w Notion na koncie, z którego jest token.") + " (" + e.Message + ")");
            }
            using (q)
            {
                foreach (var p in q.RootElement.GetProperty("results").EnumerateArray())
                    if (ParsePage(p) is { } page) pages.Add(page);
                cursor = q.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean() && q.RootElement.TryGetProperty("next_cursor", out var nc) ? nc.GetString() : null;
            }
        } while (cursor != null && pages.Count < 5000);
        return (title, pages, me);
    }

    static string PlainText(JsonElement owner, string prop)
    {
        if (!owner.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(arr.EnumerateArray().Select(x => x.TryGetProperty("plain_text", out var t) ? t.GetString() : ""));
    }

    /// <summary>A page → title, status, priority, estimate, people (property names in Polish or English).</summary>
    public static Page? ParsePage(JsonElement p)
    {
        if (p.TryGetProperty("object", out var o) && o.GetString() != "page") return null;
        if (p.TryGetProperty("in_trash", out var tr) && tr.ValueKind == JsonValueKind.True) return null;
        var id = p.GetProperty("id").GetString()!.Replace("-", "");
        var url = p.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } s ? s : NotionImport.PageUrl(id);
        string title = "";
        string? status = null, priority = null;
        double? estimate = null;
        var people = new List<string>();
        if (!p.TryGetProperty("properties", out var props)) return null;
        foreach (var prop in props.EnumerateObject())
        {
            var name = prop.Name.Trim().ToLowerInvariant();
            var v = prop.Value;
            var type = v.GetProperty("type").GetString();
            switch (type)
            {
                case "title": title = PlainText(v, "title"); break;
                case "status":
                    if (v.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object) status ??= st.GetProperty("name").GetString();
                    break;
                case "select":
                    if (v.TryGetProperty("select", out var se) && se.ValueKind == JsonValueKind.Object)
                    {
                        var val = se.GetProperty("name").GetString();
                        if (name.Contains("prior")) priority = val;
                        else if (name.Contains("status") || name.Contains("stan")) status ??= val;
                    }
                    break;
                case "people":
                    foreach (var person in v.GetProperty("people").EnumerateArray())
                        if (person.TryGetProperty("id", out var pid)) people.Add(pid.GetString()!);
                    break;
                case "number":
                    if (IsEstimate(name) && v.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number) estimate = n.GetDouble();
                    break;
                case "formula":
                    if (IsEstimate(name) && v.TryGetProperty("formula", out var f))
                    {
                        if (f.TryGetProperty("number", out var fn) && fn.ValueKind == JsonValueKind.Number) estimate = fn.GetDouble();
                        else if (f.TryGetProperty("string", out var fs) && fs.ValueKind == JsonValueKind.String) estimate = NotionImport.ParseHours(fs.GetString());
                    }
                    break;
                case "rich_text":
                    if (IsEstimate(name)) estimate = NotionImport.ParseHours(PlainText(v, "rich_text"));
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (estimate is <= 0 or > 1000) estimate = null;
        return new Page(id, title.Trim(), status, priority, estimate is { } e ? Math.Round(e, 2) : null, url, people);
    }

    static bool IsEstimate(string name) =>
        name.Contains("estym") || name.Contains("estimat") || name.Contains("godzin") || name.Contains("hours") || name == "h"
        || (name.Contains("czas") && !name.Contains("utworz") && !name.Contains("edycj") && !name.Contains("modyf"));

    // ---------- applying to the board ----------

    /// <summary>Brings the pages into the store (no network – also used by the self-test).</summary>
    public static (int Added, int Updated) Apply(BoardStore store, NotionLink link, List<Page> pages, string? me)
    {
        int added = 0, updated = 0;
        var skip = (link.SkipStatuses ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backlog = store.Data.Tasks.Where(t => t.IsBacklog && !t.Archived).ToList();
        double order = backlog.Count > 0 ? backlog.Max(t => t.Order) + 1 : 0;
        foreach (var page in pages)
        {
            if (link.OnlyMine && me != null && !page.People.Contains(me)) continue;
            var title = ExternalDrop.KnownTags(page.Title); // as shown on the board: "[VFX] Woda"
            var t = store.Data.Tasks.FirstOrDefault(x => x.NotionId == page.Id);
            if (t == null)
            {
                if (page.Status != null && (skip.Contains(page.Status) || (link.SkipStatuses == null && NotionImport.LooksDone(page.Status)))) continue;
                store.Add(new TaskItem
                {
                    Text = title, NotionTitle = title, NotionId = page.Id, Url = page.Url,
                    Status = page.Status, Priority = page.Priority, Estimate = page.Estimate, Order = order++,
                });
                added++;
                continue;
            }
            bool changed = false;
            // renamed in Notion: follow, unless the task was renamed on the board
            if (title != t.NotionTitle)
            {
                if (t.NotionTitle == null || t.Text.EndsWith(t.NotionTitle, StringComparison.Ordinal))
                {
                    var prefix = t.NotionTitle != null ? t.Text[..^t.NotionTitle.Length] : "";
                    var text = t.NotionTitle != null ? prefix + title : t.Text;
                    if (text != t.Text) t.Text = text;
                }
                t.NotionTitle = title;
                changed = true;
            }
            if (page.Status != t.Status) { t.Status = page.Status; changed = true; }
            if (page.Priority != t.Priority) { t.Priority = page.Priority; changed = true; }
            if (page.Estimate != null && page.Estimate != t.Estimate) { t.Estimate = page.Estimate; changed = true; }
            if (link.SyncDone && !t.Done && NotionImport.LooksDone(page.Status)) { t.Done = true; changed = true; }
            if (page.Url != t.Url) { t.Url = page.Url; changed = true; }
            if (changed) { store.Changed(t); updated++; }
        }
        return (added, updated);
    }

    /// <summary>Reads and applies one account's database; the result also lands in <see cref="NotionLink.LastResult"/>.</summary>
    public static async Task<Result> Run(string layer)
    {
        var link = App.Settings.AccountFor(layer).Notion;
        if (!link.Configured) return new Result(0, 0, 0, L.T("Notion nie jest skonfigurowany."));
        if (!Running.Add(layer)) return new Result(0, 0, 0, L.T("Synchronizacja już trwa."));
        try
        {
            var (_, pages, me) = await Read(link);
            var store = App.StoreFor(layer);
            var (added, updated) = Apply(store, link, pages, me);
            link.KnownStatuses = pages.Select(p => p.Status).Where(s => s != null).Select(s => s!).Distinct().OrderBy(s => s).ToList();
            link.LastSync = DateTime.Now;
            link.LastResult = $"{DateTime.Now:HH:mm}: " + L.F("stron {0}, nowe {1}, zmienione {2}", pages.Count, added, updated) + (link.OnlyMine && me == null ? " " + L.T("(nie udało się ustalić „moich”)") : "");
            SettingsStore.Save(App.Settings);
            if (added + updated > 0 && ReferenceEquals(store, App.Store)) App.Board?.OnExternalChange(quiet: true);
            return new Result(added, updated, pages.Count, null);
        }
        catch (Exception ex)
        {
            Log.Error("notion sync " + layer, ex);
            var msg = ex is HttpRequestException or TaskCanceledException ? L.T("Brak połączenia z Notion.") : ex.Message;
            link.LastResult = $"{DateTime.Now:HH:mm}: " + L.F("błąd – {0}", msg);
            SettingsStore.Save(App.Settings);
            return new Result(0, 0, 0, msg);
        }
        finally { Running.Remove(layer); }
    }

    /// <summary>Accounts with automatic sync whose interval has passed (called by the app's timer).</summary>
    public static async void Tick()
    {
        foreach (var acc in App.Settings.Accounts)
        {
            var l = acc.Notion;
            if (!l.Configured || !l.Auto) continue;
            if (l.LastSync is { } last && DateTime.Now - last < TimeSpan.FromMinutes(Math.Max(2, l.Minutes))) continue;
            await Run(acc.Id);
        }
    }
}
