using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class MemoryProject
{
    public string ProjectSlug { get; set; } = "";
    public string MemoryDir { get; set; } = "";
    public string IndexFile { get; set; } = "";
    public List<MemoryEntry> Entries { get; set; } = new();
    public List<MemoryIndexItem> IndexItems { get; set; } = new();
}

public static class MemoryIndexer
{
    private const string DisabledMarker = "CCM-DISABLED";
    private static readonly Regex DisabledLineRegex = new(
        $@"<!--\s*{DisabledMarker}:\s*(?<inner>.+?)\s*-->",
        RegexOptions.Compiled);

    private static readonly SignatureCache<List<MemoryProject>> ProjectCache = new();

    /// <summary>
    /// Fingerprint scoped to the <c>memory</c> subdirectories only — deliberately NOT the whole
    /// projects tree. That tree also holds session transcripts, which Claude Code appends to
    /// continuously (measured here: 803 files / 885 MB); fingerprinting those would move the
    /// signature on every turn and the cache would never hit.
    /// </summary>
    private static string MemorySignature()
    {
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return "missing";
        var roots = new List<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
            {
                var m = Path.Combine(d, "memory");
                if (Directory.Exists(m)) roots.Add(m);
            }
        }
        catch { return "error:" + Guid.NewGuid().ToString("N"); }
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots.Count + "|" + FileSignature.OfTrees(roots);
    }

    /// <summary>Memoized; rebuilds only when a memory directory actually changes.
    /// Called on every DASHBOARD, MEMORY and HARNESS activation.</summary>
    public static List<MemoryProject> DiscoverProjects()
        => ProjectCache.Get(MemorySignature, DiscoverProjectsUncached);

    public static List<MemoryProject> DiscoverProjectsUncached()
    {
        var projects = new List<MemoryProject>();
        if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return projects;
        foreach (var projectDir in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
        {
            var memoryDir = Path.Combine(projectDir, "memory");
            if (!Directory.Exists(memoryDir)) continue;
            projects.Add(Load(projectDir, memoryDir));
        }
        return projects;
    }

    public static MemoryProject Load(string projectDir, string memoryDir)
    {
        var slug = Path.GetFileName(projectDir);
        var indexFile = Path.Combine(memoryDir, "MEMORY.md");
        var indexContent = File.Exists(indexFile) ? File.ReadAllText(indexFile) : "";
        var indexItems = MemoryIndexParser.Parse(indexContent);

        var entries = new List<MemoryEntry>();
        // Active .md files
        foreach (var f in Directory.EnumerateFiles(memoryDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(LoadOne(f, disabled: false));
        }
        // Disabled .md.disabled files
        foreach (var f in Directory.EnumerateFiles(memoryDir, "*" + MemoryEntry.DisabledSuffix, SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(f);
            // Only .md.disabled — skip anything else with .disabled ext
            if (!name.EndsWith(".md" + MemoryEntry.DisabledSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(LoadOne(f, disabled: true));
        }

        var indexFileNames = new HashSet<string>(indexItems.Select(i => i.LinkedFile), StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) e.ReferencedInIndex = indexFileNames.Contains(e.DisplayName);
        foreach (var i in indexItems) i.TargetExists = File.Exists(Path.Combine(memoryDir, i.LinkedFile));

        return new MemoryProject
        {
            ProjectSlug = slug,
            MemoryDir = memoryDir,
            IndexFile = indexFile,
            Entries = entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            IndexItems = indexItems
        };
    }

    private static MemoryEntry LoadOne(string filePath, bool disabled)
    {
        var raw = File.ReadAllText(filePath);
        var (fm, body) = FrontmatterParser.Parse(raw);
        var fi = new FileInfo(filePath);
        return new MemoryEntry
        {
            FilePath = filePath,
            Frontmatter = fm,
            Body = body,
            Size = fi.Length,
            ModifiedAt = fi.LastWriteTime,
            LineCount = raw.Count(c => c == '\n') + 1,
            Disabled = disabled
        };
    }

    /// <summary>
    /// Persist a memory entry surgically — one root-level key at a time, then body.
    /// This preserves any frontmatter content the parser doesn't round-trip: nested
    /// `metadata:` blocks that Claude Code's session-summarizer writes, extra keys,
    /// custom fields, etc. A Serialize round-trip would silently delete those.
    /// </summary>
    public static async Task SaveEntryAsync(MemoryEntry entry)
    {
        var fm = entry.Frontmatter;
        await FrontmatterUpdater.SetKeyAsync(entry.FilePath, "name",
            string.IsNullOrWhiteSpace(fm.Name) ? null : fm.Name);
        await FrontmatterUpdater.SetKeyAsync(entry.FilePath, "description",
            string.IsNullOrWhiteSpace(fm.Description) ? null : fm.Description);
        await FrontmatterUpdater.SetKeyAsync(entry.FilePath, "type",
            string.IsNullOrWhiteSpace(fm.Type) ? null : fm.Type);
        await FrontmatterUpdater.ReplaceBodyAsync(entry.FilePath, entry.Body);
    }

    public static async Task SaveIndexAsync(MemoryProject project, string headerBlock, IEnumerable<MemoryIndexItem> items)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerBlock))
        {
            sb.Append(headerBlock.TrimEnd());
            sb.Append("\n\n");
        }
        sb.Append(MemoryIndexParser.Serialize(items));
        await AtomicFileWriter.WriteAsync(project.IndexFile, sb.ToString());
    }

    /// <summary>
    /// Toggle a memory entry between enabled/disabled state.
    /// Renames the file (.md ↔ .md.disabled) and comments/uncomments the corresponding
    /// line in MEMORY.md so Claude Code neither loads the file nor sees the index reference.
    /// </summary>
    public static async Task ToggleEntryAsync(MemoryEntry entry, string indexFilePath)
    {
        if (string.IsNullOrEmpty(entry.FilePath) || !File.Exists(entry.FilePath))
            throw new FileNotFoundException("Memory entry file not found", entry.FilePath);

        var dir = Path.GetDirectoryName(entry.FilePath) ?? "";
        var displayName = entry.DisplayName;

        if (entry.Disabled)
        {
            // ENABLE: rename foo.md.disabled → foo.md
            var newPath = Path.Combine(dir, displayName);
            if (File.Exists(newPath)) throw new IOException($"Target already exists: {newPath}");
            File.Move(entry.FilePath, newPath);
            entry.FilePath = newPath;
            entry.Disabled = false;

            // Uncomment MEMORY.md line if present
            await UncommentIndexLineAsync(indexFilePath, displayName);
        }
        else
        {
            // DISABLE: rename foo.md → foo.md.disabled
            var newPath = entry.FilePath + MemoryEntry.DisabledSuffix;
            if (File.Exists(newPath)) throw new IOException($"Target already exists: {newPath}");
            File.Move(entry.FilePath, newPath);
            entry.FilePath = newPath;
            entry.Disabled = true;

            // Comment out MEMORY.md line for this filename
            await CommentIndexLineAsync(indexFilePath, displayName);
        }
    }

    private static async Task CommentIndexLineAsync(string indexFile, string activeFileName)
    {
        if (!File.Exists(indexFile)) return;
        var text = await File.ReadAllTextAsync(indexFile);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var target = "](" + activeFileName + ")";
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains(target) && !line.TrimStart().StartsWith("<!--"))
            {
                lines[i] = $"<!-- {DisabledMarker}: {line} -->";
                changed = true;
                break;
            }
        }
        if (changed) await AtomicFileWriter.WriteAsync(indexFile, string.Join("\n", lines));
    }

    private static async Task UncommentIndexLineAsync(string indexFile, string activeFileName)
    {
        if (!File.Exists(indexFile)) return;
        var text = await File.ReadAllTextAsync(indexFile);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var target = "](" + activeFileName + ")";
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var m = DisabledLineRegex.Match(lines[i]);
            if (m.Success && m.Groups["inner"].Value.Contains(target))
            {
                lines[i] = m.Groups["inner"].Value;
                changed = true;
                break;
            }
        }
        if (changed) await AtomicFileWriter.WriteAsync(indexFile, string.Join("\n", lines));
    }
}
