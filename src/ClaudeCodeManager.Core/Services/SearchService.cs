using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public enum SearchHitStatus { Active, Inactive, Disabled }

public sealed class SearchHit
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Preview { get; set; } = "";
    public SearchHitStatus Status { get; set; } = SearchHitStatus.Active;
}

public static class SearchService
{
    private static readonly string[] FileSuffixes = { ".md", ".json", ".md.disabled" };
    private static readonly string[] SkipDirs = { "shell-snapshots", "todos", "statsig", "ide", ".git" };

    // CLAUDE.<name>.md (with any name that has no dots inside) — inactive profile
    private static readonly Regex ClaudeProfileRegex = new(
        @"^CLAUDE\.[^.]+\.md$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static SearchHitStatus DetermineStatus(string filePath)
    {
        var name = Path.GetFileName(filePath);
        // .md.disabled or SKILL.md.disabled — disabled
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            return SearchHitStatus.Disabled;
        // CLAUDE.<name>.md profile (not the active CLAUDE.md)
        if (ClaudeProfileRegex.IsMatch(name) && !name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase))
            return SearchHitStatus.Inactive;
        // Check parent for skills whose folder contains a disabled marker
        var parent = Path.GetDirectoryName(filePath);
        if (parent is not null)
        {
            // Skill inside a folder where SKILL.md.disabled exists → disabled skill body
            if (File.Exists(Path.Combine(parent, "SKILL.md.disabled")) &&
                name.Equals("SKILL.md.disabled", StringComparison.OrdinalIgnoreCase))
                return SearchHitStatus.Disabled;
        }
        return SearchHitStatus.Active;
    }

    public static IEnumerable<SearchHit> Search(string query, bool isRegex, bool caseSensitive)
    {
        if (!Directory.Exists(ClaudePaths.ClaudeRoot) || string.IsNullOrEmpty(query)) yield break;
        Regex? rx = null;
        if (isRegex)
        {
            try { rx = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
            catch { yield break; }
        }
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var file in EnumerateFiles())
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            var status = DetermineStatus(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                bool match = rx is not null ? rx.IsMatch(line) : line.IndexOf(query, cmp) >= 0;
                if (match)
                {
                    yield return new SearchHit
                    {
                        File = file,
                        Line = i + 1,
                        Preview = line.Length > 240 ? line[..240] : line,
                        Status = status
                    };
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles()
    {
        var stack = new Stack<string>();
        stack.Push(ClaudePaths.ClaudeRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { continue; }
            foreach (var d in subs)
            {
                var name = Path.GetFileName(d);
                if (Array.Exists(SkipDirs, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
                stack.Push(d);
            }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (Array.Exists(FileSuffixes, s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    yield return f;
            }
        }
    }
}
