using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class SessionFileInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsJsonl { get; set; }
    /// <summary>Claude Code's auto-generated title (from `{"type":"ai-title",...}` line in the JSONL).</summary>
    public string? AiTitle { get; set; }
    /// <summary>First user message text as fallback when AiTitle is absent.</summary>
    public string? FirstUserText { get; set; }
    /// <summary>Best display label — AiTitle if present, else truncated FirstUserText, else the UUID filename.</summary>
    public string DisplayLabel { get; set; } = "";
}

public sealed class SessionProjectInfo
{
    public string Slug { get; set; } = "";
    public string Path { get; set; } = "";
    public long TotalSize { get; set; }
    public int SessionCount { get; set; }
    public int OtherFileCount { get; set; }
    public long MemoryDirSize { get; set; }
    public DateTime? OldestSession { get; set; }
    public DateTime? NewestSession { get; set; }
    public List<SessionFileInfo> Sessions { get; set; } = new();
}

public sealed class SessionStorageSummary
{
    public long TotalSize { get; set; }
    public int TotalSessionFiles { get; set; }
    public int TotalProjects { get; set; }
    public DateTime? OldestSession { get; set; }
    public DateTime? NewestSession { get; set; }
    public List<SessionProjectInfo> Projects { get; set; } = new();
}

public static class SessionStorageService
{
    /// <summary>
    /// Scans ~/.claude/projects/*/ and returns aggregated storage stats. Missing root
    /// returns an empty summary rather than throwing so the caller can render "no data".
    /// </summary>
    public static SessionStorageSummary Scan()
    {
        var summary = new SessionStorageSummary();
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return summary;

        foreach (var projectDir in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
        {
            SessionProjectInfo? p;
            try { p = ScanProject(projectDir); }
            catch { continue; }
            if (p is null) continue;
            summary.Projects.Add(p);
            summary.TotalSize += p.TotalSize;
            summary.TotalSessionFiles += p.SessionCount;
            if (p.OldestSession is { } o && (summary.OldestSession is null || o < summary.OldestSession)) summary.OldestSession = o;
            if (p.NewestSession is { } n && (summary.NewestSession is null || n > summary.NewestSession)) summary.NewestSession = n;
        }

        summary.TotalProjects = summary.Projects.Count;
        summary.Projects = summary.Projects.OrderByDescending(p => p.TotalSize).ToList();
        return summary;
    }

    public static SessionProjectInfo? ScanProject(string projectDir)
    {
        if (!Directory.Exists(projectDir)) return null;
        var info = new SessionProjectInfo
        {
            Slug = Path.GetFileName(projectDir),
            Path = projectDir
        };

        // Session JSONL files live at the project root (~/.claude/projects/<slug>/*.jsonl)
        foreach (var f in Directory.EnumerateFiles(projectDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fi = new FileInfo(f);
            var isJsonl = fi.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
            info.TotalSize += fi.Length;
            if (isJsonl)
            {
                info.SessionCount++;
                if (info.OldestSession is null || fi.LastWriteTime < info.OldestSession) info.OldestSession = fi.LastWriteTime;
                if (info.NewestSession is null || fi.LastWriteTime > info.NewestSession) info.NewestSession = fi.LastWriteTime;
            }
            else
            {
                info.OtherFileCount++;
            }
            var sf = new SessionFileInfo
            {
                Path = f,
                FileName = fi.Name,
                Size = fi.Length,
                ModifiedAt = fi.LastWriteTime,
                IsJsonl = isJsonl
            };
            if (isJsonl)
            {
                var (aiTitle, firstUser) = TryReadJsonlMetadata(f);
                sf.AiTitle = aiTitle;
                sf.FirstUserText = firstUser;
                sf.DisplayLabel = !string.IsNullOrWhiteSpace(aiTitle) ? aiTitle
                                : !string.IsNullOrWhiteSpace(firstUser) ? Truncate(firstUser!, 70)
                                : fi.Name;
            }
            else
            {
                sf.DisplayLabel = fi.Name;
            }
            info.Sessions.Add(sf);
        }

        // Nested memory dir (part of the project — count it toward total size)
        var memoryDir = Path.Combine(projectDir, "memory");
        if (Directory.Exists(memoryDir))
        {
            long mem = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(memoryDir, "*", SearchOption.AllDirectories))
                    mem += new FileInfo(f).Length;
            }
            catch { /* permission — treat as 0 */ }
            info.MemoryDirSize = mem;
            info.TotalSize += mem;
        }

        // Sort session list newest first (most useful for review)
        info.Sessions = info.Sessions.OrderByDescending(s => s.ModifiedAt).ToList();
        return info;
    }

    /// <summary>
    /// Delete a single session file. Returns bytes freed, or -1 on failure.
    /// </summary>
    public static long DeleteSessionFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            var size = new FileInfo(path).Length;
            File.Delete(path);
            return size;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Delete all JSONL session files under a project older than the cutoff. Returns (deletedCount, bytesFreed).
    /// </summary>
    public static (int deleted, long freed) DeleteOlderThan(string projectDir, DateTime cutoff)
    {
        if (!Directory.Exists(projectDir)) return (0, 0);
        int count = 0;
        long freed = 0;
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTime >= cutoff) continue;
                var size = fi.Length;
                File.Delete(f);
                count++;
                freed += size;
            }
            catch { /* skip locked/inaccessible */ }
        }
        return (count, freed);
    }

    /// <summary>
    /// Delete all JSONL session files across every project older than the cutoff.
    /// </summary>
    public static (int deleted, long freed) DeleteAllOlderThan(DateTime cutoff)
    {
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return (0, 0);
        int count = 0;
        long freed = 0;
        foreach (var projectDir in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
        {
            var (c, f) = DeleteOlderThan(projectDir, cutoff);
            count += c;
            freed += f;
        }
        return (count, freed);
    }

    /// <summary>
    /// Scans the top of a Claude Code session JSONL to extract the friendly
    /// title Claude Code auto-generates (line: {"type":"ai-title","aiTitle":"..."}).
    /// Also captures the first user message text as fallback. Bounded to the first
    /// ~200 lines so large sessions don't get slow-scanned.
    /// </summary>
    public static (string? aiTitle, string? firstUserText) TryReadJsonlMetadata(string jsonlPath)
    {
        string? aiTitle = null;
        string? firstUser = null;
        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            int lineCount = 0;
            string? line;
            while ((line = reader.ReadLine()) != null && lineCount < 200)
            {
                lineCount++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Fast-path early filter — most lines don't contain either marker
                bool maybeTitle = aiTitle is null && line.Contains("\"ai-title\"", StringComparison.Ordinal);
                bool maybeUser = firstUser is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
                if (!maybeTitle && !maybeUser) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                    if (maybeTitle
                        && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "ai-title"
                        && doc.RootElement.TryGetProperty("aiTitle", out var at) && at.ValueKind == JsonValueKind.String)
                    {
                        aiTitle = at.GetString();
                    }
                    else if (maybeUser
                        && doc.RootElement.TryGetProperty("type", out var tu) && tu.ValueKind == JsonValueKind.String && tu.GetString() == "user"
                        && doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        firstUser = ExtractFirstUserText(msg);
                    }
                }
                catch { /* malformed line — skip */ }

                if (aiTitle is not null && firstUser is not null) break;
            }
        }
        catch { /* file locked or permissions — best-effort */ }
        return (aiTitle, firstUser);
    }

    private static string? ExtractFirstUserText(JsonElement message)
    {
        // Claude Code stores user content as either a plain string or an array of parts
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString();
                    }
                }
            }
        }
        return null;
    }

    private static string Truncate(string s, int maxChars)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length <= maxChars) return s;
        return s[..maxChars] + "…";
    }

    /// <summary>
    /// Human-friendly byte size (KB/MB/GB with one decimal).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int idx = 0;
        while (v >= 1024 && idx < units.Length - 1) { v /= 1024; idx++; }
        return v.ToString(idx <= 1 ? "0" : "0.##") + " " + units[idx];
    }
}
