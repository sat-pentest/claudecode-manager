using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public enum ResultKind { Empty, Findings, Verdict, Chain, Inventory, Scope, Recon, Generic }

public sealed class WorkflowResultSummary : INotifyPropertyChanged
{
    public string AgentIdShort { get; set; } = "";
    public string AgentIdFull { get; set; } = "";
    public string AgentLogPath { get; set; } = "";       // absolute path to agent-<id>.jsonl
    public ResultKind Kind { get; set; } = ResultKind.Empty;
    public string Headline { get; set; } = "";           // "findings (4)" · "verdict: REAL" · "chain → RCE"
    public string SeverityBadge { get; set; } = "";      // "1H·2M·1L" or "H" or "" for verdict/chain
    public string SeverityLevel { get; set; } = "";      // "Critical"/"High"/"Medium"/"Low"/"Info" — for color
    public List<string> Tags { get; set; } = new();      // vuln classes or lens types (up to 4)
    public List<string> DetailLines { get; set; } = new(); // top 2-3 finding titles or extra info
    public string FullJson { get; set; } = "";           // pretty-printed full result JSON
    public bool IsEmpty => Kind == ResultKind.Empty;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class WorkflowRunEntry
{
    public string RunId { get; set; } = "";
    public string ShortId => RunId.Length > 12 ? RunId.Substring(0, 12) : RunId;
    public string SessionId { get; set; } = "";
    public string ShortSession => SessionId.Length > 8 ? SessionId.Substring(0, 8) : SessionId;
    public string Target { get; set; } = "(target not identified yet)";
    public int AgentsStarted { get; set; }
    public int AgentsCompleted { get; set; }
    public int AgentsPending => Math.Max(0, AgentsStarted - AgentsCompleted);
    public double ProgressPct => AgentsStarted > 0 ? 100.0 * AgentsCompleted / AgentsStarted : 0;
    public DateTime LastActivity { get; set; }
    public int IdleMinutes => (int)Math.Max(0, (DateTime.Now - LastActivity).TotalMinutes);
    public int IdleSeconds => (int)Math.Max(0, (DateTime.Now - LastActivity).TotalSeconds);
    public string Status =>
        IdleSeconds < 60 ? "ACTIVE" :
        IdleMinutes < 5 ? "RECENT" :
        IdleMinutes < 30 ? "STALE" : "IDLE";
    public string LastActivityText => LastActivity.ToString("HH:mm:ss");
    public string ProgressText => $"{AgentsCompleted}/{AgentsStarted}";
    public string DirPath { get; set; } = "";
    public List<WorkflowResultSummary> RecentResults { get; set; } = new();
}

public static class WorkflowLiveService
{
    public static List<WorkflowRunEntry> Scan(bool includeIdle = false)
    {
        var results = new List<WorkflowRunEntry>();
        var projectsRoot = ClaudePaths.ProjectsRoot;
        if (!Directory.Exists(projectsRoot)) return results;

        foreach (var projectDir in SafeEnumerateDirectories(projectsRoot))
        {
            foreach (var sessionDir in SafeEnumerateDirectories(projectDir))
            {
                var workflowsDir = Path.Combine(sessionDir, "subagents", "workflows");
                if (!Directory.Exists(workflowsDir)) continue;

                foreach (var wfDir in SafeEnumerateDirectories(workflowsDir))
                {
                    var wfName = Path.GetFileName(wfDir);
                    if (!wfName.StartsWith("wf_", StringComparison.OrdinalIgnoreCase)) continue;

                    var journalPath = Path.Combine(wfDir, "journal.jsonl");
                    if (!File.Exists(journalPath)) continue;

                    var entry = ParseWorkflow(wfDir, wfName, sessionDir, journalPath);
                    if (entry is null) continue;

                    if (!includeIdle && entry.IdleMinutes > 30) continue;
                    results.Add(entry);
                }
            }
        }

        return results
            .OrderByDescending(r => r.LastActivity)
            .ToList();
    }

    private static WorkflowRunEntry? ParseWorkflow(string wfDir, string wfName, string sessionDir, string journalPath)
    {
        var entry = new WorkflowRunEntry
        {
            RunId = wfName,
            DirPath = wfDir,
            SessionId = Path.GetFileName(sessionDir)
        };

        // Compute last activity as newest mtime among agent-*.jsonl + journal.jsonl
        DateTime latest = File.GetLastWriteTime(journalPath);
        try
        {
            foreach (var agentFile in Directory.EnumerateFiles(wfDir, "agent-*.jsonl"))
            {
                var m = File.GetLastWriteTime(agentFile);
                if (m > latest) latest = m;
            }
        }
        catch { }
        entry.LastActivity = latest;

        // Parse journal.jsonl
        int started = 0, completed = 0;
        var recentResults = new List<WorkflowResultSummary>();
        string? firstTarget = null;

        try
        {
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    if (type == "started") started++;
                    else if (type == "result")
                    {
                        completed++;
                        if (root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
                        {
                            // First-seen target extraction
                            if (firstTarget is null)
                            {
                                foreach (var key in new[] { "subsidiary", "programMatched", "target", "domain", "apex" })
                                {
                                    if (resultEl.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                                    {
                                        var s = v.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) { firstTarget = s; break; }
                                    }
                                }
                                if (firstTarget is null && resultEl.TryGetProperty("scopeInclude", out var siEl)
                                    && siEl.ValueKind == JsonValueKind.Array && siEl.GetArrayLength() > 0)
                                {
                                    var first = siEl[0];
                                    if (first.ValueKind == JsonValueKind.String) firstTarget = first.GetString();
                                }
                            }
                            var agentId = root.TryGetProperty("agentId", out var aidEl) ? aidEl.GetString() ?? "" : "";
                            var summary = BuildResultSummary(resultEl);
                            summary.AgentIdShort = agentId.Length > 12 ? agentId.Substring(0, 12) : agentId;
                            summary.AgentIdFull = agentId;
                            summary.AgentLogPath = string.IsNullOrEmpty(agentId)
                                ? "" : Path.Combine(wfDir, $"agent-{agentId}.jsonl");
                            try
                            {
                                summary.FullJson = JsonSerializer.Serialize(resultEl,
                                    new JsonSerializerOptions { WriteIndented = true });
                            }
                            catch { summary.FullJson = resultEl.GetRawText(); }
                            recentResults.Add(summary);
                        }
                    }
                }
                catch { }
            }
        }
        catch { return null; }

        entry.AgentsStarted = started;
        entry.AgentsCompleted = completed;
        if (firstTarget is not null) entry.Target = Trunc(firstTarget, 90);
        // Keep only last 5 results
        entry.RecentResults = recentResults.Skip(Math.Max(0, recentResults.Count - 5)).ToList();

        return entry;
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>
    /// Build a structured summary of a workflow agent's result.
    /// Priority: rich vuln/chain info > counts+samples > key fallback.
    /// </summary>
    private static WorkflowResultSummary BuildResultSummary(JsonElement r)
    {
        var s = new WorkflowResultSummary();

        // 1) Chain Exploration result
        if (r.TryGetProperty("finalImpact", out var impact) && impact.ValueKind == JsonValueKind.String)
        {
            s.Kind = ResultKind.Chain;
            s.Headline = $"chain → {Trunc(impact.GetString(), 45)}";
            if (r.TryGetProperty("stoppedReason", out var sr) && sr.ValueKind == JsonValueKind.String)
                s.Tags.Add(sr.GetString() ?? "");
            s.SeverityLevel = "Critical";
            s.SeverityBadge = "CHAIN";
            return s;
        }

        // 2) Adversarial verdict
        if (r.TryGetProperty("isReal", out var isReal))
        {
            var real = isReal.ValueKind == JsonValueKind.True;
            s.Kind = ResultKind.Verdict;
            var conf = r.TryGetProperty("confidence", out var c) ? c.ToString() : "?";
            s.Headline = real ? $"verdict: REAL · conf {conf}" : $"verdict: FALSE POSITIVE · conf {conf}";
            s.SeverityBadge = real ? "REAL" : "FALSE";
            s.SeverityLevel = real ? "High" : "Info";
            if (r.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String)
                s.DetailLines.Add(Trunc(rs.GetString(), 120));
            return s;
        }

        // 3) Single-finding schema (has vulnClass at top level)
        if (r.TryGetProperty("vulnClass", out var vc) && vc.ValueKind == JsonValueKind.String)
        {
            s.Kind = ResultKind.Findings;
            var sev = r.TryGetProperty("severity", out var sev1) && sev1.ValueKind == JsonValueKind.String ? sev1.GetString() ?? "?" : "?";
            s.Headline = $"{sev} · {vc.GetString()}";
            s.SeverityBadge = SevAbbrev(sev);
            s.SeverityLevel = NormalizeSeverity(sev);
            s.Tags.Add(vc.GetString() ?? "");
            if (r.TryGetProperty("endpoint", out var e) && e.ValueKind == JsonValueKind.String)
                s.DetailLines.Add(Trunc(e.GetString(), 90));
            return s;
        }

        // 4) Findings array — count + severity breakdown + top vuln classes + top titles
        if (r.TryGetProperty("findings", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            var n = fs.GetArrayLength();
            if (n == 0)
            {
                s.Kind = ResultKind.Empty;
                s.Headline = "no findings";
                s.SeverityBadge = "—";
                s.SeverityLevel = "Empty";
                return s;
            }

            int crit = 0, high = 0, med = 0, low = 0, info = 0;
            var classes = new List<string>();
            var titles = new List<string>();
            string topSev = "Low";
            foreach (var f in fs.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                if (f.TryGetProperty("severity", out var sv) && sv.ValueKind == JsonValueKind.String)
                {
                    var lv = sv.GetString()?.ToUpperInvariant() ?? "";
                    if (lv.Contains("CRIT")) { crit++; topSev = "Critical"; }
                    else if (lv.Contains("HIGH")) { high++; if (topSev != "Critical") topSev = "High"; }
                    else if (lv.Contains("MED")) { med++; if (topSev != "Critical" && topSev != "High") topSev = "Medium"; }
                    else if (lv.Contains("LOW")) { low++; }
                    else if (lv.Contains("INFO")) info++;
                }
                if (f.TryGetProperty("vulnClass", out var cls) && cls.ValueKind == JsonValueKind.String)
                {
                    var cs = cls.GetString();
                    if (!string.IsNullOrEmpty(cs) && !classes.Contains(cs)) classes.Add(cs);
                }
                // Try to extract a title/description for detail lines
                if (titles.Count < 3)
                {
                    string? title = null;
                    if (f.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                    else if (f.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) title = d.GetString();
                    else if (f.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String) title = sm.GetString();
                    if (!string.IsNullOrWhiteSpace(title)) titles.Add(Trunc(title, 95));
                }
            }

            s.Kind = ResultKind.Findings;
            s.Headline = $"findings ({n})";
            var sevParts = new List<string>();
            if (crit > 0) sevParts.Add($"{crit}C");
            if (high > 0) sevParts.Add($"{high}H");
            if (med > 0) sevParts.Add($"{med}M");
            if (low > 0) sevParts.Add($"{low}L");
            if (info > 0) sevParts.Add($"{info}I");
            s.SeverityBadge = sevParts.Count > 0 ? string.Join("·", sevParts) : "?";
            s.SeverityLevel = topSev;
            foreach (var cls in classes.Take(4)) s.Tags.Add(cls);
            foreach (var title in titles.Take(3)) s.DetailLines.Add(title);
            return s;
        }

        // 5) Inventory schema (bbp-static-recon)
        if (r.TryGetProperty("endpointCount", out var ec) && ec.ValueKind == JsonValueKind.Number)
        {
            s.Kind = ResultKind.Inventory;
            s.Headline = $"inventory · endpoints {ec}";
            s.SeverityBadge = "INV";
            s.SeverityLevel = "Info";
            if (r.TryGetProperty("bundleCount", out var b)) s.Tags.Add($"bundles {b}");
            return s;
        }
        if (r.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            s.Kind = ResultKind.Inventory;
            s.Headline = $"endpoints ({eps.GetArrayLength()})";
            s.SeverityBadge = "INV";
            s.SeverityLevel = "Info";
            return s;
        }

        // 6) Scope check result
        if (r.TryGetProperty("isLotteAsset", out var isL))
        {
            s.Kind = ResultKind.Scope;
            var sub = r.TryGetProperty("subsidiary", out var sb) && sb.ValueKind == JsonValueKind.String
                      ? Trunc(sb.GetString(), 55) : "";
            s.Headline = isL.ValueKind == JsonValueKind.True ? $"scope: LOTTE" : "scope: NOT LOTTE";
            s.SeverityBadge = "SCOPE";
            s.SeverityLevel = "Info";
            if (!string.IsNullOrEmpty(sub)) s.DetailLines.Add(sub);
            return s;
        }
        if (r.TryGetProperty("programMatched", out var pm) && pm.ValueKind == JsonValueKind.String)
        {
            s.Kind = ResultKind.Scope;
            s.Headline = $"scope: {Trunc(pm.GetString(), 40)}";
            s.SeverityBadge = "SCOPE";
            s.SeverityLevel = "Info";
            if (r.TryGetProperty("engagementMode", out var em) && em.ValueKind == JsonValueKind.String)
                s.Tags.Add(em.GetString() ?? "");
            return s;
        }

        // 7) Surface Map / recon
        if (r.TryGetProperty("subdomains", out var sd) && sd.ValueKind == JsonValueKind.Array)
        {
            s.Kind = ResultKind.Recon;
            s.Headline = $"recon · subdomains {sd.GetArrayLength()}";
            s.SeverityBadge = "RECON";
            s.SeverityLevel = "Info";
            if (r.TryGetProperty("ips", out var ip) && ip.ValueKind == JsonValueKind.Array)
                s.Tags.Add($"ips {ip.GetArrayLength()}");
            return s;
        }

        // 8) Simple category with count
        foreach (var key in new[] { "category", "label", "count", "summary" })
        {
            if (r.TryGetProperty(key, out var v))
            {
                s.Kind = ResultKind.Generic;
                s.SeverityBadge = "•";
                s.SeverityLevel = "Info";
                if (v.ValueKind == JsonValueKind.String) { s.Headline = $"{key}: {Trunc(v.GetString(), 70)}"; return s; }
                if (v.ValueKind == JsonValueKind.Number) { s.Headline = $"{key}: {v}"; return s; }
            }
        }

        // 9) Fallback: list keys
        var keys = new List<string>();
        foreach (var prop in r.EnumerateObject())
        {
            keys.Add(prop.Name);
            if (keys.Count >= 4) break;
        }
        s.Kind = ResultKind.Generic;
        s.SeverityBadge = "?";
        s.SeverityLevel = "Info";
        s.Headline = keys.Count > 0 ? "result · " + string.Join(", ", keys) : "(empty result)";
        return s;
    }

    private static string SevAbbrev(string sev)
    {
        var u = sev.ToUpperInvariant();
        if (u.Contains("CRIT")) return "C";
        if (u.Contains("HIGH")) return "H";
        if (u.Contains("MED")) return "M";
        if (u.Contains("LOW")) return "L";
        if (u.Contains("INFO")) return "I";
        return "?";
    }

    private static string NormalizeSeverity(string sev)
    {
        var u = sev.ToUpperInvariant();
        if (u.Contains("CRIT")) return "Critical";
        if (u.Contains("HIGH")) return "High";
        if (u.Contains("MED")) return "Medium";
        if (u.Contains("LOW")) return "Low";
        if (u.Contains("INFO")) return "Info";
        return "Info";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }
}
