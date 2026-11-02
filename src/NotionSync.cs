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

namespace TaskWall;

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
    public static bool AnyRunning => Running.Count > 0;
    /// <summary>A sync started or ended (the spinning icon).</summary>
    public static event Action? StateChanged;

    public sealed record Result(int Added, int Updated, int Total, string? Error);

    /// <summary>A parsed page (title, status …), independent of the API's JSON.</summary>
    public sealed record Page(string Id, string Title, string? Status, string? Priority, double? Estimate, string Url, List<string> People)
    {
        /// <summary>The main task of a Notion sub-item (page id without dashes).</summary>
        public string? ParentId { get; init; }
        /// <summary>Text values of the page's properties (selects, statuses, people names, text) – for the extra conditions ("Dział != PM").</summary>
        public Dictionary<string, List<string>> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

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
                ? CryptProtectData(ref input, "TaskWall Notion", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, ref output)
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

    public static string? Protect(string token) => Clean(token) is { Length: > 0 } t && Crypt(Encoding.UTF8.GetBytes(t), true) is { } b ? Convert.ToBase64String(b) : null;

    /// <summary>
    /// The token out of what was pasted: without the field's mask "****" (pasting next to it instead of over it kept
    /// both), spaces, line breaks or quotes around it. Notion tokens start with "ntn_" or "secret_".
    /// </summary>
    public static string Clean(string pasted)
    {
        var s = pasted.Replace("*", "").Trim().Trim('"', '\'', '`').Trim();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(ntn_|secret_)[A-Za-z0-9_\-]+");
        return m.Success ? m.Value : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");
    }

    static string? Token(NotionLink link)
    {
        if (link.TokenProtected == null) return null;
        try { return Crypt(Convert.FromBase64String(link.TokenProtected), false) is { } b ? Clean(Encoding.UTF8.GetString(b)) : null; }
        catch { return null; }
    }

    /// <summary>Asks Notion who the token belongs to: null = it works, else what is wrong (for the settings).</summary>
    public static async Task<string?> Check(NotionLink link)
    {
        if (Token(link) is not { } token) return L.T("Brak tokenu (albo został zapisany na innym koncie Windows) – wklej go ponownie.");
        try { using var _ = await Call(token, HttpMethod.Get, "users/me"); return null; }
        catch (NotionError e) when (e.Code == HttpStatusCode.Unauthorized) { return L.T("Notion nie przyjął tokenu (wygasł albo jest błędny)."); }
        catch (Exception e) { return e.Message; }
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

    /// <summary>"…?v=1a2b…" → the view id from a "Copy link to view" link, or null.</summary>
    public static string? ViewId(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(link, @"[?&]v=([0-9a-fA-F-]{8,36})");
        return m.Success ? m.Groups[1].Value.Replace("-", "").ToLowerInvariant() : null;
    }

    static string Dashed(string id) => id.Length == 32 ? $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}" : id;

    /// <summary>
    /// Reads the database – through the filter of the view from the link (e.g. "Dla mnie": assigned to me, chosen
    /// statuses), so only those pages come in. Stops at <see cref="NotionLink.MaxPages"/> instead of flooding the backlog.
    /// </summary>
    /// <summary>The database's statuses as Notion has them now: names in order, ids (id → name), groups (name → 0 to-do, 1 in progress, 2 complete).</summary>
    public sealed record Statuses(List<string> Names, Dictionary<string, string>? Ids, Dictionary<string, int>? Groups);

    /// <summary>Notion refused the query over an option the filter names but the database no longer has.</summary>
    sealed class MissingOption(string option, string message) : InvalidOperationException(message)
    {
        public string Option { get; } = option;
    }

    public static async Task<(string Title, List<Page> Pages, string? MeId, string? ViewName, bool Capped, bool ViewFiltered, Statuses? Statuses, List<string> Dropped)> Read(NotionLink link, NotionLink? tokenFrom = null)
    {
        var token = Token(tokenFrom ?? link) ?? throw new InvalidOperationException(L.T("Brak tokenu (albo został zapisany na innym koncie Windows) – wklej go ponownie."));
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
        string sourceId = id;
        try
        {
            using var db = await Call(token, HttpMethod.Get, "databases/" + id);
            title = PlainText(db.RootElement, "title");
            if (db.RootElement.TryGetProperty("data_sources", out var ds) && ds.GetArrayLength() > 0) sourceId = ds[0].GetProperty("id").GetString()!;
        }
        catch (NotionError e) when (e.Code == HttpStatusCode.NotFound) { /* maybe the link points at a data source itself */ }

        // the view's saved filter (and sorts) – Notion evaluates "me" as the token's user
        JsonElement? filter = null, sorts = null;
        string? viewName = null;
        if (link.UseViewFilter && ViewId(link.Database) is { } viewId)
        {
            var full = viewId.Length == 32 ? viewId : await FindView(token, id, viewId);
            if (full == null) throw new InvalidOperationException(L.T("Nie znalazłem widoku z linku. Skopiuj link jeszcze raz (nazwa widoku → Copy link to view)."));
            try
            {
                using var v = await Call(token, HttpMethod.Get, "views/" + Dashed(full));
                var r = v.RootElement;
                viewName = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (r.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.Object) filter = f.Clone();
                if (r.TryGetProperty("sorts", out var s) && s.ValueKind == JsonValueKind.Array) sorts = s.Clone();
                if (r.TryGetProperty("data_source_id", out var vds) && vds.ValueKind == JsonValueKind.String) sourceId = vds.GetString()!;
            }
            catch (NotionError e) when (e.Code is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException(L.T("Nie mogę odczytać filtrów widoku z linku.") + " (" + e.Message + ")");
            }
        }

        int max = Math.Max(50, link.MaxPages);
        async Task<(List<Page> Pages, bool Capped)> Query(JsonElement? flt)
        {
            var found = new List<Page>();
            string? cursor = null;
            do
            {
                var body = new Dictionary<string, object> { ["page_size"] = 100 };
                if (flt is { } fl) body["filter"] = fl;
                if (sorts is { } so) body["sorts"] = so;
                if (cursor != null) body["start_cursor"] = cursor;
                JsonDocument q;
                try { q = await Call(token, HttpMethod.Post, $"data_sources/{sourceId}/query", body); }
                // 'status option "Zaakceptowany" not found for property "status"': the view's filter names a status renamed / removed in Notion
                catch (NotionError e) when (e.Code == HttpStatusCode.BadRequest && System.Text.RegularExpressions.Regex.Match(e.Message, "option \"(.+?)\" not found") is { Success: true } m)
                {
                    throw new MissingOption(m.Groups[1].Value, L.T("Notion odrzucił filtr widoku:") + " " + e.Message);
                }
                catch (NotionError e) when (e.Code is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    throw new InvalidOperationException(L.T("Nie widzę tej bazy. Sprawdź link i to, czy masz do niej dostęp w Notion na koncie, z którego jest token.") + " (" + e.Message + ")");
                }
                using (q)
                {
                    foreach (var p in q.RootElement.GetProperty("results").EnumerateArray())
                        if (ParsePage(p) is { } page) found.Add(page);
                    cursor = q.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean() && q.RootElement.TryGetProperty("next_cursor", out var nc) ? nc.GetString() : null;
                }
                if (cursor != null && found.Count >= max) return (found, true);
            } while (cursor != null);
            return (found, false);
        }

        // the database's columns: all its statuses (for the settings, also those no page has yet) and the people columns
        var peopleProps = new List<string>();
        List<string>? schemaStatuses = null;
        Dictionary<string, string>? statusIds = null;
        Dictionary<string, int>? statusGroups = null;
        // the options of every status / select column, by its name and its id (a view's filter may use either)
        var options = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var columns = new List<string>();
        try
        {
            using var src = await Call(token, HttpMethod.Get, "data_sources/" + sourceId);
            if (src.RootElement.TryGetProperty("properties", out var props))
            {
                bool mainFound = false;
                foreach (var pr in props.EnumerateObject())
                {
                    var type = pr.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != null) columns.Add(pr.Name + ": " + type);
                    if (type == "people") peopleProps.Add(pr.Name);
                    if (type is "status" or "select" or "multi_select" && pr.Value.TryGetProperty(type, out var od) && od.TryGetProperty("options", out var all))
                    {
                        var names = all.EnumerateArray().Select(o => o.TryGetProperty("name", out var on) ? on.GetString() ?? "" : "").Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
                        options[pr.Name] = names;
                        if (pr.Value.TryGetProperty("id", out var pid) && pid.GetString() is { Length: > 0 } propId) options[propId] = names;
                    }
                    if (type != "status" || mainFound) continue;
                    bool main = IsMainStatus(pr.Name.Trim().ToLowerInvariant());
                    if (schemaStatuses != null && !main) continue;
                    if (pr.Value.TryGetProperty("status", out var sd) && sd.TryGetProperty("options", out var opts))
                    {
                        schemaStatuses = opts.EnumerateArray().Select(o => o.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
                        statusIds = new();
                        foreach (var o in opts.EnumerateArray())
                            if (o.TryGetProperty("id", out var oid) && oid.GetString() is { Length: > 0 } idv && o.TryGetProperty("name", out var on) && on.GetString() is { Length: > 0 } nm) statusIds[idv] = nm;
                        // Notion's three groups, always in this order: To-do, In progress, Complete
                        statusGroups = null;
                        if (sd.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                        {
                            statusGroups = new();
                            int gi = 0;
                            foreach (var g in gs.EnumerateArray())
                            {
                                if (g.TryGetProperty("option_ids", out var oids) && oids.ValueKind == JsonValueKind.Array)
                                    foreach (var x in oids.EnumerateArray())
                                        if (x.GetString() is { } xs && statusIds.TryGetValue(xs, out var gn)) statusGroups[gn] = Math.Min(gi, 2);
                                gi++;
                            }
                        }
                    }
                    mainFound = main;
                }
            }
        }
        catch (NotionError) { /* only for the settings and "mine" */ }

        // "only mine" on Notion's side (people columns contain me), so a big team database never hits the limit
        JsonElement? MineFilterNow()
        {
            if (me == null || peopleProps.Count == 0) return null;
            // the assignee column when there is one (not "Watchers", "Odbiera" …), like Notion's own "Assignee contains me"
            var assignee = peopleProps.Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"assign|przypis|wykonaw|owner|odpowiedzial|osoba", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            var parts = (assignee.Count > 0 ? assignee : peopleProps).Select(n => new Dictionary<string, object> { ["property"] = n, ["people"] = new Dictionary<string, object> { ["contains"] = me } }).ToList();
            object f = parts.Count == 1 ? parts[0] : new Dictionary<string, object> { ["or"] = parts };
            return JsonSerializer.SerializeToElement(f);
        }
        Task<JsonElement?> MineFilter() => Task.FromResult(MineFilterNow());

        bool viewFiltered = filter != null; // the view's own filter (the finished statuses are its business then)
        // statuses renamed or removed in Notion that the view's filter still names: taken out of the filter (Notion would
        // refuse the whole query), also the ones Notion itself names in its refusal when the columns couldn't be read
        var viewFilter = filter;
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        JsonElement? Pruned()
        {
            if (viewFilter is not { } vf) return null;
            dropped.Clear();
            var (node, constant) = Prune(System.Text.Json.Nodes.JsonNode.Parse(vf.GetRawText())!,
                (prop, option) => !missing.Contains(option) && (!options.TryGetValue(prop, out var set) || set.Contains(option)), dropped);
            if (constant == false)
                throw new InvalidOperationException(L.F("Filtr widoku w Notion wybiera tylko statusy, których w bazie już nie ma ({0}) – popraw filtr widoku w Notion.", string.Join(", ", dropped.Distinct())));
            return constant == true ? null : JsonSerializer.SerializeToElement(node);
        }
        filter = Pruned();
        if (filter == null && link.OnlyMine && await MineFilter() is { } mine) filter = mine;
        List<Page> pages;
        bool capped;
        for (int attempt = 0; ; attempt++)
        {
            try { (pages, capped) = await Query(filter); break; }
            catch (MissingOption mo) when (attempt < 8 && viewFilter != null && missing.Add(mo.Option))
            {
                filter = Pruned();
                if (filter == null && link.OnlyMine && await MineFilter() is { } mine2) filter = mine2;
            }
        }
        // a view whose filter the API doesn't give out (e.g. Notion's own "My tasks" views): with too many pages,
        // read the ones assigned to me instead of nothing
        if (capped && filter == null && await MineFilter() is { } mineOnly)
        {
            (pages, capped) = await Query(mineOnly);
            viewName = (viewName ?? L.T("widok")) + " · " + L.T("przypisane do mnie");
        }
        if (columns.Count > 0) link.KnownColumns = columns;
        return (title, pages, me, viewName, capped, viewFiltered, schemaStatuses == null ? null : new Statuses(schemaStatuses, statusIds, statusGroups), dropped.Distinct().ToList());
    }

    /// <summary>
    /// A view's filter without its conditions on options the database no longer has, read as Notion would read them:
    /// "is X" matches nothing, "is not X" everything; an "and" with a part that matches nothing matches nothing, an "or"
    /// keeps its other parts. Returns the filter left, or a constant (true: no filter at all, false: nothing matches).
    /// </summary>
    internal static (System.Text.Json.Nodes.JsonNode? Node, bool? Constant) Prune(System.Text.Json.Nodes.JsonNode node, Func<string, string, bool> exists, List<string> dropped)
    {
        if (node is not System.Text.Json.Nodes.JsonObject o) return (node, null);
        foreach (var op in new[] { "and", "or" })
        {
            if (o[op] is not System.Text.Json.Nodes.JsonArray parts) continue;
            bool and = op == "and";
            var kept = new System.Text.Json.Nodes.JsonArray();
            foreach (var part in parts)
            {
                if (part == null) continue;
                var (n, c) = Prune(part, exists, dropped);
                if (c == !and) return (null, !and); // and: a part matching nothing; or: a part matching everything
                if (c == null) kept.Add(n!.DeepClone());
            }
            if (kept.Count == 0) return (null, and);
            if (kept.Count == 1) return (kept[0]!.DeepClone(), null);
            return (new System.Text.Json.Nodes.JsonObject { [op] = kept }, null);
        }
        if (o["property"] is System.Text.Json.Nodes.JsonValue pv && pv.TryGetValue<string>(out var prop))
            foreach (var type in new[] { "status", "select", "multi_select" })
                if (o[type] is System.Text.Json.Nodes.JsonObject cond)
                    foreach (var (key, negative) in new[] { ("equals", false), ("does_not_equal", true), ("contains", false), ("does_not_contain", true) })
                        if (cond[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var option) && !exists(prop, option))
                        {
                            dropped.Add(option);
                            return (null, negative);
                        }
        return (node, null);
    }

    /// <summary>
    /// The database's statuses changed in Notion: the chosen ones follow. A renamed status (the same Notion id) keeps
    /// its place under the new name, a new one is read unless Notion counts it as complete (or its name says finished),
    /// a removed one is let go – and a choice left empty goes back to the default (all but the finished ones) rather
    /// than reading nothing. Returns what changed, for the sync's result line.
    /// </summary>
    internal static List<string> Adapt(NotionLink link, Statuses? now)
    {
        var notes = new List<string>();
        if (now == null || now.Names.Count == 0) return notes;
        var exists = now.Names.ToHashSet(StringComparer.Ordinal);
        var before = (link.KnownStatuses ?? new()).ToHashSet(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (link.StatusIds != null && now.Ids != null)
            foreach (var (id, name) in now.Ids)
                if (link.StatusIds.TryGetValue(id, out var old) && old != name) renamed[old] = name;
        bool Known(string s) => before.Contains(s) || renamed.ContainsValue(s)
            || (link.StatusIds != null && now.Ids != null && now.Ids.Any(kv => kv.Value == s && link.StatusIds.ContainsKey(kv.Key)));
        var added = before.Count == 0 ? new List<string>() : now.Names.Where(s => !Known(s)).ToList();
        bool Finished(string s) => now.Groups != null && now.Groups.TryGetValue(s, out var g) ? g == 2 : NotionImport.LooksDone(s);

        List<string>? Follow(List<string>? list, bool read)
        {
            if (list == null) return null;
            var result = new List<string>();
            foreach (var s in list)
            {
                var n = renamed.TryGetValue(s, out var r) ? r : s;
                if (exists.Contains(n) && !result.Contains(n)) result.Add(n);
            }
            if (read) result.AddRange(added.Where(s => !Finished(s) && !result.Contains(s)));
            return result;
        }
        foreach (var (old, name) in renamed) notes.Add(L.F("status „{0}” nazywa się teraz „{1}”", old, name));
        if (link.ReadStatuses != null)
            foreach (var s in added) notes.Add(Finished(s) ? L.F("nowy status „{0}” (zakończony – pomijany)", s) : L.F("nowy status „{0}” – wczytywany", s));
        foreach (var s in (link.ReadStatuses ?? new()).Concat(link.SkipStatuses ?? new()).Distinct().Where(s => !renamed.ContainsKey(s) && !exists.Contains(s)))
            notes.Add(L.F("statusu „{0}” nie ma już w bazie", s));
        var read = Follow(link.ReadStatuses, true);
        if (read is { Count: 0 } && link.ReadStatuses is { Count: > 0 })
        {
            read = null; // none of the chosen ones is left: the default, not an empty board
            notes.Add(L.T("żaden z wybranych statusów już nie istnieje – wczytywane są wszystkie poza zakończonymi (wybierz je ponownie w ustawieniach)"));
        }
        if (!SameList(read, link.ReadStatuses)) link.ReadStatuses = read;
        var skip = Follow(link.SkipStatuses, false);
        if (!SameList(skip, link.SkipStatuses)) link.SkipStatuses = skip;
        if (now.Ids is { Count: > 0 }) link.StatusIds = now.Ids;
        link.StatusGroups = now.Groups;
        return notes;
    }

    static bool SameList(List<string>? a, List<string>? b) => a == null ? b == null : b != null && a.SequenceEqual(b);

    /// <summary>Notion's group of a status: 0 to-do, 1 in progress, 2 complete, -1 not known.</summary>
    static int Group(NotionLink link, string? status) => status != null && link.StatusGroups != null && link.StatusGroups.TryGetValue(status, out var g) ? g : -1;

    /// <summary>Finished in Notion: its name says so, or Notion has it in the "Complete" group (whatever it is called).</summary>
    static bool IsDone(NotionLink link, string? status) => NotionImport.LooksDone(status) || Group(link, status) == 2;

    /// <summary>Being worked on: its name says so, or Notion has it in the "In progress" group and nothing more specific fits.</summary>
    static bool IsInProgress(NotionLink link, string? status) => LooksInProgress(status)
        || (Group(link, status) == 1 && !LooksOnHold(status) && !LooksInReview(status) && !LooksInTests(status));

    /// <summary>A short "?v=" id → the full view id (views of the database, matched by prefix).</summary>
    static async Task<string?> FindView(string token, string databaseId, string prefix)
    {
        try
        {
            using var list = await Call(token, HttpMethod.Get, "views?database_id=" + Dashed(databaseId));
            foreach (var v in list.RootElement.GetProperty("results").EnumerateArray())
            {
                var vid = v.GetProperty("id").GetString()!.Replace("-", "");
                if (vid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return vid;
            }
        }
        catch (NotionError) { }
        return null;
    }

    static string PlainText(JsonElement owner, string prop)
    {
        if (!owner.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(arr.EnumerateArray().Select(x => x.TryGetProperty("plain_text", out var t) ? t.GetString() : ""));
    }

    /// <summary>A page → title, status, priority, estimate, people (property names in Polish or English).</summary>
    /// <summary>The task's own status property: "Status" / "Stan" (not "QA Status", "Status testów" …).</summary>
    static bool IsMainStatus(string lowerName) => lowerName is "status" or "stan" or "state" or "status zadania";

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
        string? parent = null;
        string? mainStatus = null, otherStatus = null; // "Status" itself wins over e.g. "QA Status"
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Value(string prop, string? v) { if (string.IsNullOrEmpty(v)) return; if (!values.TryGetValue(prop, out var l)) values[prop] = l = new(); l.Add(v); }
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
                    if (v.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
                    {
                        var sv = st.GetProperty("name").GetString();
                        Value(prop.Name, sv);
                        if (IsMainStatus(name)) mainStatus ??= sv; else otherStatus ??= sv;
                    }
                    break;
                case "select":
                    if (v.TryGetProperty("select", out var se) && se.ValueKind == JsonValueKind.Object)
                    {
                        var val = se.GetProperty("name").GetString();
                        Value(prop.Name, val);
                        if (name.Contains("prior")) priority = val;
                        else if (IsMainStatus(name)) mainStatus ??= val;
                        else if (name.Contains("status") || name.Contains("stan")) otherStatus ??= val;
                    }
                    break;
                case "multi_select":
                    if (v.TryGetProperty("multi_select", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray()) if (m.TryGetProperty("name", out var mn)) Value(prop.Name, mn.GetString());
                    break;
                case "relation":
                    // Notion sub-items: "Parent item" / "Element nadrzędny" points at the main task
                    if (parent == null && (name.Contains("parent") || name.Contains("nadrz") || name.Contains("rodzic") || name.Contains("główn")) && v.TryGetProperty("relation", out var rel) && rel.ValueKind == JsonValueKind.Array)
                        foreach (var r in rel.EnumerateArray())
                            if (r.TryGetProperty("id", out var rid)) { parent = rid.GetString()!.Replace("-", ""); break; }
                    break;
                case "people":
                    foreach (var person in v.GetProperty("people").EnumerateArray())
                    {
                        if (person.TryGetProperty("id", out var pid)) people.Add(pid.GetString()!);
                        if (person.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String) Value(prop.Name, pn.GetString());
                    }
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
                    else Value(prop.Name, PlainText(v, "rich_text"));
                    break;
            }
        }
        status = mainStatus ?? otherStatus;
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (estimate is <= 0 or > 1000) estimate = null;
        return new Page(id, title.Trim(), status, priority, estimate is { } e ? Math.Round(e, 2) : null, url, people) { ParentId = parent, Values = values };
    }

    static bool IsEstimate(string name) =>
        name.Contains("estym") || name.Contains("estimat") || name.Contains("godzin") || name.Contains("hours") || name == "h"
        || (name.Contains("czas") && !name.Contains("utworz") && !name.Contains("edycj") && !name.Contains("modyf"));

    // ---------- applying to the board ----------

    /// <summary>Brings the pages into the store (no network – also used by the self-test).</summary>
    public static (int Added, int Updated) Apply(BoardStore store, NotionLink link, List<Page> pages, string? me, bool viewFiltered = false, bool complete = false)
    {
        int added = 0, updated = 0;
        var skip = (link.SkipStatuses ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backlog = store.Data.Tasks.Where(t => t.IsBacklog && !t.Archived).ToList();
        double order = backlog.Count > 0 ? backlog.Max(t => t.Order) + 1 : 0;
        var read = link.ReadStatuses?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conditions = (link.Conditions ?? new()).Select(Condition.Parse).Where(c => c != null).Select(c => c!).ToList();
        // not read: a status outside the chosen ones (or a skipped / finished one) and pages failing the extra conditions
        bool Skipped(string? status) => status != null && (read != null ? !read.Contains(status)
            : skip.Contains(status) || (link.SkipStatuses == null && !viewFiltered && IsDone(link, status)));
        bool Excluded(Page page) => Skipped(page.Status) || conditions.Any(c => !c.Matches(page));
        foreach (var page in pages)
        {
            if (link.OnlyMine && me != null && !page.People.Contains(me)) continue;
            var title = ExternalDrop.KnownTags(page.Title); // as shown on the board: "[VFX] Woda"
            var t = store.Data.Tasks.FirstOrDefault(x => x.NotionId == page.Id);
            // deleted on the board (in the bin): not brought back while it is there
            // deleted by hand here (in the bin, also after emptying it – the entry stays marked): it stays deleted
            if (t == null && (store.BinItems.Any(e => e.Task.NotionId == page.Id) || store.Data.Bin?.Any(e => e.Task.NotionId == page.Id) == true)) continue;
            if (t == null)
            {
                if (Excluded(page)) continue;
                store.Add(new TaskItem
                {
                    Id = "notion-" + page.Id.Replace("-", ""), // the same id on every computer: two PCs importing the same page merge into one task
                    Text = title, NotionTitle = title, NotionId = page.Id, Url = page.Url,
                    Status = page.Status, Priority = page.Priority, Estimate = page.Estimate, Order = order++, ParentNotionId = page.ParentId,
                    Started = IsInProgress(link, page.Status) || LooksInReview(page.Status) || LooksInTests(page.Status) ? true : null,
                    Review = LooksInReview(page.Status) ? true : null,
                    Testing = LooksInTests(page.Status) ? true : null,
                    Hold = LooksOnHold(page.Status) ? true : null,
                    NotionSource = link.Name.Length > 0 ? link.Name : null,
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
            if (page.Status != t.Status)
            {
                t.Status = page.Status;
                changed = true;
                // the status moved in Notion: the bar follows it (forward and back); a status that means none of these
                // leaves what was set on the board
                if (!t.Done)
                {
                    // on hold in Notion ("On hold", "Blocked", "Wstrzymane"): held here too, the state under it stays
                    if (LooksOnHold(page.Status)) t.Hold = true;
                    else if (LooksInTests(page.Status)) { t.Hold = null; t.Started = true; t.Review = null; t.Testing = true; }
                    else if (LooksInReview(page.Status)) { t.Hold = null; t.Started = true; t.Review = true; t.Testing = null; }
                    else if (IsInProgress(link, page.Status)) { t.Hold = null; t.Started = true; t.Review = null; t.Testing = null; }
                    else t.Hold = null; // another status: no longer held
                    t.NoteState(Days.Key(DateTime.Today)); // the day keeps the state Notion gave it
                }
            }
            if (page.Priority != t.Priority) { t.Priority = page.Priority; changed = true; }
            if (page.Estimate != null && page.Estimate != t.Estimate) { t.Estimate = page.Estimate; changed = true; }
            if (link.SyncDone && !t.Done && IsDone(link, page.Status)) { t.Done = true; changed = true; }
            if (page.Url != t.Url) { t.Url = page.Url; changed = true; }
            if (page.ParentId != t.ParentNotionId) { t.ParentNotionId = page.ParentId; changed = true; }
            var source = link.Name.Length > 0 ? link.Name : null;
            if (source != t.NotionSource) { t.NotionSource = source; changed = true; }
            // a status that isn't read (any more): an untouched task still in the backlog leaves it again
            if (Excluded(page) && t.IsBacklog && !t.Archived && t.Checklist == null && t.Text == t.NotionTitle)
            {
                store.Remove(t);
                updated++;
                continue;
            }
            if (changed) { store.Changed(t); updated++; }
        }
        // the whole result came in: an untouched backlog task of this database that is no longer in it (left the view,
        // re-assigned …) leaves the backlog too
        if (complete && link.Name.Length > 0)
        {
            var ids = pages.Select(p => p.Id).ToHashSet();
            foreach (var t in store.Data.Tasks.Where(t => t.NotionId != null && t.NotionSource == link.Name && !ids.Contains(t.NotionId) && t.IsBacklog && !t.Archived && t.Checklist == null && t.Text == t.NotionTitle).ToList())
            {
                store.Remove(t);
                updated++;
            }
        }
        return (added, updated);
    }

    /// <summary>A Notion status meaning the work has begun ("W trakcie", "In progress", "Doing").</summary>
    /// <summary>A Notion status that means "waiting for review / acceptance" (Review, Merge Request, Do odebrania, QA …).</summary>
    public static bool LooksInReview(string? status) => status != null &&
        !LooksInTests(status) && System.Text.RegularExpressions.Regex.IsMatch(status, @"review|merge request|do odebrania|do sprawdzenia|weryfikacj|akceptacj", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A Notion status that means "blocked, waiting" (On hold, Blocked, Wstrzymane, Zablokowane, Czeka …).</summary>
    public static bool LooksOnHold(string? status) => status != null &&
        System.Text.RegularExpressions.Regex.IsMatch(status, @"\bhold\b|on.?hold|block|wstrzym|zablok|czeka|waiting|paused|pauz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A Notion status that means "being tested" (Test, Testy, QA, Testing …).</summary>
    public static bool LooksInTests(string? status) => status != null &&
        System.Text.RegularExpressions.Regex.IsMatch(status, @"\btest|\bqa\b|testowan", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool LooksInProgress(string? status) => status != null &&
        System.Text.RegularExpressions.Regex.IsMatch(status, @"w trakcie|w toku|w pracy|in progress|in work|working|ongoing|doing|started|rozpocz|realizacj|wip", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads and applies all Notion databases of one account (one token, the options of the first one). Each database
    /// keeps its own result in <see cref="NotionLink.LastResult"/>; the first one also gets the summary of all.
    /// </summary>
    public static async Task<Result> Run(string layer)
    {
        var acc = App.Settings.AccountFor(layer);
        var main = acc.Notion;
        if (!main.Configured) return new Result(0, 0, 0, L.T("Notion nie jest skonfigurowany."));
        if (!Running.Add(layer)) return new Result(0, 0, 0, L.T("Synchronizacja już trwa."));
        StateChanged?.Invoke();
        int added = 0, updated = 0, total = 0;
        string? error = null;
        var parts = new List<string>();
        var sources = acc.NotionSources.ToList();
        try
        {
            var store = App.StoreFor(layer);
            foreach (var link in sources)
            {
                // the options are the account's (set once, on the first database)
                if (!ReferenceEquals(link, main))
                {
                    link.UseViewFilter = main.UseViewFilter; link.MaxPages = main.MaxPages; link.OnlyMine = main.OnlyMine;
                    link.SyncDone = main.SyncDone; link.Auto = main.Auto; link.Minutes = main.Minutes;
                }
                var label = link.Name.Length > 0 ? link.Name + ": " : sources.Count > 1 ? L.T("baza") + " " + (sources.IndexOf(link) + 1) + ": " : "";
                try
                {
                    var (_, pages, me, viewName, capped, filtered, statuses, dropped) = await Read(link, main);
                    if (capped)
                        throw new InvalidOperationException(L.F("Ten widok ma ponad {0} zadań – nic nie wczytałem. Użyj widoku z filtrami (np. przypisane do mnie) albo podnieś limit.", link.MaxPages));
                    // statuses renamed / added / removed in Notion: the chosen ones follow before the pages are applied
                    var notes = Adapt(link, statuses);
                    if (dropped.Count > 0) notes.Insert(0, L.F("filtr widoku w Notion wskazuje statusy, których już nie ma ({0}) – pominięte, popraw filtr w Notion", string.Join(", ", dropped)));
                    foreach (var n in notes) Log.Error($"notion sync {layer} {link.Name}: {n}");
                    var (a, u) = Apply(store, link, pages, me, viewFiltered: filtered, complete: !capped); // without the view's filter the finished statuses stay out
                    added += a; updated += u; total += pages.Count;
                    // all statuses of the database in Notion's order (and any a page has that the list lacks)
                    link.KnownStatuses = (statuses?.Names ?? new()).Concat(pages.Select(p => p.Status).Where(s => s != null).Select(s => s!).OrderBy(s => s)).Distinct().ToList();
                    link.LastSync = DateTime.Now;
                    link.ViewName = viewName;
                    link.LastResult = $"{DateTime.Now:HH:mm}: " + (viewName != null ? L.F("widok „{0}”", viewName) + " · " : link.UseViewFilter && ViewId(link.Database) == null ? L.T("cała baza (link bez widoku)") + " · " : "")
                        + L.F("stron {0}, nowe {1}, zmienione {2}", pages.Count, a, u) + (link.OnlyMine && me == null ? " " + L.T("(nie udało się ustalić „moich”)") : "")
                        + (notes.Count > 0 ? "\n" + L.T("Zmiany statusów w Notion:") + " " + string.Join("; ", notes) : "");
                    parts.Add(label + L.F("stron {0}, nowe {1}, zmienione {2}", pages.Count, a, u));
                }
                catch (Exception ex)
                {
                    Log.Error("notion sync " + layer + " " + link.Name, ex);
                    var msg = ex is HttpRequestException or TaskCanceledException ? L.T("Brak połączenia z Notion.") : ex.Message;
                    error ??= msg;
                    link.LastResult = $"{DateTime.Now:HH:mm}: " + L.F("błąd – {0}", msg);
                    parts.Add(label + L.F("błąd – {0}", msg));
                }
            }
            main.LastSync = DateTime.Now; // the account's interval counts from here, whatever happened to each database
            if (sources.Count > 1) main.LastResult = $"{DateTime.Now:HH:mm}: " + string.Join("  ·  ", parts);
            SettingsStore.Save(App.Settings);
            if (added + updated > 0 && ReferenceEquals(store, App.Store)) App.Board?.OnExternalChange(quiet: true);
            return new Result(added, updated, total, error);
        }
        finally { Running.Remove(layer); StateChanged?.Invoke(); }
    }

    /// <summary>Accounts with automatic sync whose interval has passed (called by the app's timer).</summary>
    public static async void Tick(bool startup = false)
    {
        foreach (var acc in App.Settings.Accounts)
        {
            var l = acc.Notion;
            if (!l.Configured || !l.Auto) continue;
            if (!startup && l.LastSync is { } last && DateTime.Now - last < TimeSpan.FromMinutes(Math.Max(2, l.Minutes))) continue;
            await Run(acc.Id);
        }
    }
}

/// <summary>
/// An extra condition of a Notion database in the settings: "Dział != PM" (none of the column's values is PM),
/// "Projekt = Mnich DLC" (one of them is). Names and values are compared without case.
/// </summary>
sealed record Condition(string Property, bool Equals_, string Value)
{
    public static Condition? Parse(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line ?? "", @"^\s*(.+?)\s*(!=|≠|=)\s*(.+?)\s*$");
        return m.Success ? new Condition(m.Groups[1].Value, m.Groups[2].Value == "=", m.Groups[3].Value) : null;
    }

    public bool Matches(NotionSync.Page page)
    {
        var has = page.Values.TryGetValue(Property, out var values) && values.Any(v => string.Equals(v.Trim(), Value, StringComparison.OrdinalIgnoreCase));
        return Equals_ ? has : !has;
    }
}
