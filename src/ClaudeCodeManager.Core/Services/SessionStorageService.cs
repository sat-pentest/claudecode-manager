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
    /// <summary>The session's current name: the operator's own title when one was set, otherwise
    /// the one Claude Code generated. Null when the session has neither.</summary>
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
                var (title, firstUser) = TryReadJsonlMetadata(f);
                sf.AiTitle = title;
                sf.FirstUserText = firstUser;
                // Titles are free text and some carry newlines, which would break the row layout;
                // they go through the same flattening as the fallback rather than straight in.
                sf.DisplayLabel = !string.IsNullOrWhiteSpace(title) ? Truncate(title!, 80)
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
    /// Send a single session file to the Recycle Bin. Returns bytes freed, or -1 on failure.
    /// </summary>
    public static long DeleteSessionFile(string path)
    {
        var r = RecycleBin.Send(path);
        return r.Deleted > 0 ? r.Freed : -1;
    }

    /// <summary>Send several session files to the Recycle Bin in one shell operation.</summary>
    public static RecycleBin.Outcome DeleteSessionFiles(IEnumerable<string> paths)
        => RecycleBin.Send(paths);

    /// <summary>
    /// Send every JSONL session under a project older than the cutoff to the Recycle Bin.
    ///
    /// Failures are counted, not swallowed. A locked file used to be skipped silently, so pruning
    /// forty files while three were open reported "37 pruned" and never mentioned the rest — the
    /// operator had no way to know the space was not actually reclaimed.
    /// </summary>
    public static RecycleBin.Outcome DeleteOlderThan(string projectDir, DateTime cutoff)
    {
        if (!Directory.Exists(projectDir)) return new RecycleBin.Outcome(0, 0, 0);

        var doomed = new List<string>();
        int unreadable = 0;
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (new FileInfo(f).LastWriteTime >= cutoff) continue;
                doomed.Add(f);
            }
            catch { unreadable++; }   // could not even be inspected — still a failure to report
        }

        var r = RecycleBin.Send(doomed);
        return unreadable == 0 ? r : r with { Failed = r.Failed + unreadable };
    }

    /// <summary>
    /// Delete all JSONL session files across every project older than the cutoff.
    /// </summary>
    public static RecycleBin.Outcome DeleteAllOlderThan(DateTime cutoff)
    {
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return new RecycleBin.Outcome(0, 0, 0);
        int count = 0, failed = 0;
        long freed = 0;
        foreach (var projectDir in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
        {
            var r = DeleteOlderThan(projectDir, cutoff);
            count += r.Deleted;
            freed += r.Freed;
            failed += r.Failed;
        }
        return new RecycleBin.Outcome(count, freed, failed);
    }

    /// <summary>
    /// Scans the top of a Claude Code session JSONL to extract the friendly
    /// title Claude Code auto-generates (line: {"type":"ai-title","aiTitle":"..."}).
    /// Also captures the first user message text as fallback. Bounded to the first
    /// ~200 lines so large sessions don't get slow-scanned.
    /// </summary>
    /// <summary>
    /// Title and opening text for one session file.
    ///
    /// A session carries two kinds of title and they are not interchangeable:
    /// <c>{"type":"custom-title","customTitle":…}</c> is the name the operator typed, and
    /// <c>{"type":"ai-title","aiTitle":…}</c> is the one Claude Code generated. The typed name wins
    /// whenever it exists — it is what the session is called everywhere else.
    ///
    /// Both records are appended, never edited, so a renamed session holds every name it has ever
    /// had and only the last one is current. They are also written continuously as the session
    /// runs, which puts the current pair at the very end of the file: measured across twenty live
    /// sessions the last record sat at most 27 KB from EOF, so the tail is read rather than the
    /// whole file, which for a 95 MB session is the difference between a list that opens and one
    /// that does not.
    /// </summary>
    public static (string? title, string? firstUserText) TryReadJsonlMetadata(string jsonlPath)
    {
        return (ReadTitleFromTail(jsonlPath), ReadFirstUserText(jsonlPath));
    }

    /// <summary>How much of the end of the file to search for the current title.</summary>
    private const int TitleTailBytes = 256 * 1024;

    private static string? ReadTitleFromTail(string jsonlPath)
    {
        string? custom = null, ai = null;
        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - TitleTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            // The first line of a mid-file chunk is usually cut in half; it simply fails to parse.
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 40) continue;
                if (line.Contains("-title\"", StringComparison.Ordinal)) lines.Add(line);
            }

            // Walk backwards: the last of each kind is the one in force.
            for (var i = lines.Count - 1; i >= 0 && (custom is null || ai is null); i--)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;

                    switch (t.GetString())
                    {
                        case "custom-title" when custom is null
                            && doc.RootElement.TryGetProperty("customTitle", out var ct)
                            && ct.ValueKind == JsonValueKind.String:
                            custom = ct.GetString();
                            break;
                        case "ai-title" when ai is null
                            && doc.RootElement.TryGetProperty("aiTitle", out var at)
                            && at.ValueKind == JsonValueKind.String:
                            ai = at.GetString();
                            break;
                    }
                }
                catch { /* truncated or malformed line — skip */ }
            }
        }
        catch { /* file locked or permissions — best-effort */ }

        return !string.IsNullOrWhiteSpace(custom) ? custom
             : !string.IsNullOrWhiteSpace(ai) ? ai
             : null;
    }

    private static string? ReadFirstUserText(string jsonlPath)
    {
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
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (doc.RootElement.TryGetProperty("type", out var tu) && tu.ValueKind == JsonValueKind.String && tu.GetString() == "user"
                        && doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        var text = ExtractFirstUserText(msg);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                catch { /* malformed line — skip */ }
            }
        }
        catch { /* file locked or permissions — best-effort */ }
        return null;
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
