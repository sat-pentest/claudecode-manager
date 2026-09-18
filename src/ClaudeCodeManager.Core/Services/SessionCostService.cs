using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class SessionCost
{
    public string SessionId { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>Per-model totals. A session can switch models mid-run, and they price differently.</summary>
    public Dictionary<string, TokenTotals> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }

    /// <summary>How many subagent / workflow-agent transcripts rolled into this session.</summary>
    public int SubagentFiles { get; set; }
    public string SubagentText => SubagentFiles > 0 ? $"+{SubagentFiles} agents" : "";

    public TokenTotals Totals
    {
        get
        {
            var t = new TokenTotals();
            foreach (var v in ByModel.Values) t.Add(v);
            return t;
        }
    }

    /// <summary>Models in this session that the price table does not cover.</summary>
    public List<string> UnpricedModels =>
        ByModel.Keys.Where(m => !TokenPricing.IsPriced(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();

    public bool FullyPriced => UnpricedModels.Count == 0;

    /// <summary>USD across every priced model. Unpriced models contribute nothing and are flagged
    /// separately rather than silently counted as free.</summary>
    public double Cost => ByModel.Sum(kv => TokenPricing.Cost(kv.Key, kv.Value) ?? 0);

    public string PrimaryModel =>
        ByModel.OrderByDescending(kv => kv.Value.Total).Select(kv => kv.Key).FirstOrDefault() ?? "";

    public int Requests => ByModel.Values.Sum(v => v.Requests);

    // Display helpers, so the view does not carry formatting logic.
    public string CostText => FullyPriced ? "$" + TokenPricing.FormatUsd(Cost) : "$" + TokenPricing.FormatUsd(Cost) + "+";
    public string TotalTokensText => TokenPricing.FormatTokens(Totals.Total);
    public string CacheReadText => TokenPricing.FormatTokens(Totals.CacheRead);
    public string OutputText => TokenPricing.FormatTokens(Totals.Output);
    public string RangeText => FirstAt == default ? "" : $"{FirstAt:MM-dd HH:mm} → {LastAt:MM-dd HH:mm}";

    /// <summary>What the list shows: the session's own title, or its id when it never got one.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? SessionId : Label;

    /// <summary>Short id, shown beside the title so a transcript is still identifiable on disk.</summary>
    public string ShortId => SessionId.Length > 8 ? SessionId[..8] : SessionId;
}

public sealed class CostSummary
{
    public List<SessionCost> Sessions { get; set; } = new();
    public Dictionary<string, TokenTotals> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TokenTotals> ByProject { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Local calendar day (yyyy-MM-dd) to totals, for the trend strip.</summary>
    public Dictionary<string, TokenTotals> ByDay { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> CostByDay { get; set; } = new(StringComparer.Ordinal);

    public int FilesScanned { get; set; }
    public int FilesFromCache { get; set; }
    public long ElapsedMs { get; set; }

    public double TotalCost => Sessions.Sum(s => s.Cost);
    public TokenTotals Totals
    {
        get { var t = new TokenTotals(); foreach (var v in ByModel.Values) t.Add(v); return t; }
    }
    public List<string> UnpricedModels =>
        ByModel.Keys.Where(m => !TokenPricing.IsPriced(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Token spend reconstructed from Claude Code's own session transcripts.
///
/// Two things make this non-trivial, and getting either wrong produces a plausible but wrong number:
///
/// 1. <b>Duplicate lines.</b> Claude Code writes one assistant line per content block, and every one
///    of them repeats the same <c>message.usage</c> for the request. Summing lines therefore
///    double-counts — measured at roughly 2.1x on a real session here. Usage is keyed by
///    <c>requestId</c> and counted once.
/// 2. <b>Cache tiers.</b> <c>input_tokens</c> excludes cached tokens; reads, 5-minute writes and
///    1-hour writes each carry a different multiplier. Collapsing them into one "input" number
///    misprices a cache-heavy session badly, and agent sessions are extremely cache-heavy.
///
/// The transcript corpus runs to hundreds of megabytes, so per-file results are cached on disk and
/// keyed by length plus write time; only the session being written to is re-read on a rescan.
/// </summary>
public static class SessionCostService
{
    private sealed class CachedFile
    {
        [JsonPropertyName("len")] public long Length { get; set; }
        [JsonPropertyName("mtime")] public long MTimeTicks { get; set; }
        [JsonPropertyName("first")] public long FirstTicks { get; set; }
        [JsonPropertyName("last")] public long LastTicks { get; set; }
        /// <summary>Session title, cached so the warm path never re-reads the transcript head.</summary>
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("models")] public Dictionary<string, TokenTotals> ByModel { get; set; } = new();
        /// <summary>Day (yyyy-MM-dd) to per-model totals, so the trend survives caching.</summary>
        [JsonPropertyName("days")] public Dictionary<string, Dictionary<string, TokenTotals>> ByDay { get; set; } = new();
    }

    private static string CachePath => Path.Combine(ClaudePaths.ManagerRoot, "cost-cache.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static Dictionary<string, CachedFile> LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, CachedFile>>(File.ReadAllText(CachePath))
                   ?? new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveCache(Dictionary<string, CachedFile> cache)
    {
        try { AtomicFileWriter.Write(CachePath, JsonSerializer.Serialize(cache, JsonOpts)); }
        catch { /* cache is an optimisation; losing it only costs time */ }
    }

    public static void ClearCache()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }

    /// <param name="progress">Reports (done, total) as files are processed.</param>
    public static CostSummary Scan(IProgress<(int done, int total)>? progress = null,
                                   CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = new CostSummary();
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return summary;

        var cache = LoadCache();

        // Recursive, because a session's subagent and workflow-agent transcripts live under
        // <project>/<session-uuid>/subagents/... and carry their own API usage. On a workflow-heavy
        // setup they outnumber the top-level transcripts several to one, and leaving them out
        // understates what a session actually cost.
        var files = new List<(string path, string slug, string sessionId, bool isSub)>();
        foreach (var projectDir in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
        {
            var slug = Path.GetFileName(projectDir);
            try
            {
                foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var (sessionId, isSub) = OwningSession(projectDir, f);
                    files.Add((f, slug, sessionId, isSub));
                }
            }
            catch { /* unreadable project */ }
        }

        var done = 0;
        var cacheDirty = false;
        var bySession = new Dictionary<string, SessionCost>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, slug, sessionId, isSub) in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (done % 5 == 0) progress?.Report((done, files.Count));

            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { continue; }

            CachedFile? entry = null;
            if (cache.TryGetValue(path, out var hit)
                && hit.Length == fi.Length
                && hit.MTimeTicks == fi.LastWriteTimeUtc.Ticks)
            {
                entry = hit;
                summary.FilesFromCache++;
            }

            if (entry is null)
            {
                entry = ParseFile(path);
                if (entry is null) continue;
                entry.Length = fi.Length;
                entry.MTimeTicks = fi.LastWriteTimeUtc.Ticks;

                // Same title the SESSIONS module shows: Claude Code's own ai-title line, falling
                // back to the opening user message. A cost list keyed by raw UUID is unreadable --
                // the operator knows "하나의 리포트 작성", not 4fc5c17a-c05f-48fa.
                // Subagent transcripts have no title of their own; they inherit the parent row.
                if (!isSub)
                {
                    var (aiTitle, firstUser) = SessionStorageService.TryReadJsonlMetadata(path);
                    entry.Title = !string.IsNullOrWhiteSpace(aiTitle) ? aiTitle!
                                : !string.IsNullOrWhiteSpace(firstUser) ? Shorten(firstUser!)
                                : "";
                }
                cache[path] = entry;
                cacheDirty = true;
            }

            summary.FilesScanned++;
            if (entry.ByModel.Count == 0) continue;

            // Subagent transcripts roll into the session that spawned them: they are that session's
            // spend, and a list of orphan agent-*.jsonl rows would be unreadable.
            if (!bySession.TryGetValue(sessionId, out var sc))
            {
                bySession[sessionId] = sc = new SessionCost
                {
                    SessionId = sessionId,
                    ProjectSlug = slug,
                    FilePath = isSub ? Path.Combine(Path.GetDirectoryName(path) ?? "", "") : path,
                };
                summary.Sessions.Add(sc);
            }
            if (isSub) sc.SubagentFiles++;
            else
            {
                sc.FilePath = path;
                if (!string.IsNullOrWhiteSpace(entry.Title)) sc.Label = entry.Title;
            }

            foreach (var (model, totals) in entry.ByModel)
            {
                if (!sc.ByModel.TryGetValue(model, out var agg)) sc.ByModel[model] = agg = new TokenTotals();
                agg.Add(totals);
            }

            var first = entry.FirstTicks == 0 ? default : new DateTime(entry.FirstTicks);
            var last = entry.LastTicks == 0 ? default : new DateTime(entry.LastTicks);
            if (first != default && (sc.FirstAt == default || first < sc.FirstAt)) sc.FirstAt = first;
            if (last > sc.LastAt) sc.LastAt = last;

            foreach (var (model, totals) in entry.ByModel)
            {
                if (!summary.ByModel.TryGetValue(model, out var m)) summary.ByModel[model] = m = new TokenTotals();
                m.Add(totals);
            }

            if (!summary.ByProject.TryGetValue(slug, out var proj)) summary.ByProject[slug] = proj = new TokenTotals();
            foreach (var totals in entry.ByModel.Values) proj.Add(totals);

            foreach (var (day, byModel) in entry.ByDay)
            {
                if (!summary.ByDay.TryGetValue(day, out var d)) summary.ByDay[day] = d = new TokenTotals();
                foreach (var (model, totals) in byModel)
                {
                    d.Add(totals);
                    summary.CostByDay.TryGetValue(day, out var c);
                    summary.CostByDay[day] = c + (TokenPricing.Cost(model, totals) ?? 0);
                }
            }
        }

        if (cacheDirty)
        {
            // Drop entries for transcripts that no longer exist, so pruning sessions also prunes
            // the cache instead of growing it forever.
            var live = new HashSet<string>(files.Select(f => f.path), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in cache.Keys.Where(k => !live.Contains(k)).ToList()) cache.Remove(stale);
            SaveCache(cache);
        }

        summary.Sessions = summary.Sessions.OrderByDescending(s => s.Cost).ToList();
        progress?.Report((files.Count, files.Count));
        sw.Stop();
        summary.ElapsedMs = sw.ElapsedMilliseconds;
        return summary;
    }

    /// <summary>
    /// Read one transcript, counting each request's usage exactly once.
    /// </summary>
    private static CachedFile? ParseFile(string path)
    {
        var result = new CachedFile();
        // requestIds already counted. Duplicate lines for one request repeat identical usage
        // (verified across every multi-line request in a real transcript), so first-wins is safe.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anonymous = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Cheap reject first: most transcript lines are user turns or tool results and carry
                // no usage at all. Parsing every line of an 90 MB file would dominate the scan.
                if (line.Length < 40 || !line.Contains("\"usage\"", StringComparison.Ordinal)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                    var requestId = root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String
                        ? rid.GetString() ?? ""
                        : "";
                    if (requestId.Length == 0) requestId = "anon:" + anonymous++;   // count, but never dedupe away
                    if (!seen.Add(requestId)) continue;

                    var model = msg.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String
                        ? mo.GetString() ?? "unknown"
                        : "unknown";

                    var t = new TokenTotals
                    {
                        Input = ReadLong(usage, "input_tokens"),
                        Output = ReadLong(usage, "output_tokens"),
                        CacheRead = ReadLong(usage, "cache_read_input_tokens"),
                        Requests = 1,
                    };

                    // Prefer the per-TTL breakdown; fall back to the flat total as a 5-minute write,
                    // which is the default TTL, when the breakdown is absent on older transcripts.
                    if (usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                    {
                        t.CacheWrite5m = ReadLong(cc, "ephemeral_5m_input_tokens");
                        t.CacheWrite1h = ReadLong(cc, "ephemeral_1h_input_tokens");
                        if (t.CacheWrite5m == 0 && t.CacheWrite1h == 0)
                            t.CacheWrite5m = ReadLong(usage, "cache_creation_input_tokens");
                    }
                    else
                    {
                        t.CacheWrite5m = ReadLong(usage, "cache_creation_input_tokens");
                    }

                    // Claude Code emits <synthetic> assistant turns for local operations with every
                    // counter at zero. They are not API calls; recording them only adds a model row
                    // that can never be priced and has nothing to price.
                    if (t.Total == 0) continue;

                    if (!result.ByModel.TryGetValue(model, out var agg))
                        result.ByModel[model] = agg = new TokenTotals();
                    agg.Add(t);

                    if (root.TryGetProperty("timestamp", out var tsEl)
                        && tsEl.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(tsEl.GetString(), out var ts))
                    {
                        ts = ts.ToLocalTime();
                        if (result.FirstTicks == 0 || ts.Ticks < result.FirstTicks) result.FirstTicks = ts.Ticks;
                        if (ts.Ticks > result.LastTicks) result.LastTicks = ts.Ticks;

                        var day = ts.ToString("yyyy-MM-dd");
                        if (!result.ByDay.TryGetValue(day, out var dayMap))
                            result.ByDay[day] = dayMap = new Dictionary<string, TokenTotals>(StringComparer.OrdinalIgnoreCase);
                        if (!dayMap.TryGetValue(model, out var dayAgg))
                            dayMap[model] = dayAgg = new TokenTotals();
                        dayAgg.Add(t);
                    }
                }
                catch { /* malformed line — skip it, keep the file */ }
            }
        }
        catch { return null; }

        return result;
    }

    /// <summary>
    /// Which session a transcript belongs to. A file sitting directly in the project directory is
    /// its own session; anything deeper is a subagent of the session directory it lives under.
    /// </summary>
    private static (string sessionId, bool isSubagent) OwningSession(string projectDir, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar),
                          projectDir.TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return (Path.GetFileNameWithoutExtension(filePath), false);

        var rel = Path.GetRelativePath(projectDir, filePath);
        var firstSegment = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return (firstSegment, true);
    }

    private static string Shorten(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length <= 70 ? s : s[..70] + "…";
    }

    private static long ReadLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
