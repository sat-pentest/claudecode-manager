using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public static class Linter
{
    public const int MaxIndexLines = 200;
    public const int MaxIndexItemChars = 150;

    public static List<Diagnostic> Run()
    {
        var list = new List<Diagnostic>();
        if (!Directory.Exists(ClaudePaths.ClaudeRoot))
        {
            list.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Category = "paths", Message = "~/.claude/ not found." });
            return list;
        }
        list.AddRange(LintMemory());
        list.AddRange(LintSkills());
        list.AddRange(LintSettings());
        return list;
    }

    private static IEnumerable<Diagnostic> LintMemory()
    {
        foreach (var proj in MemoryIndexer.DiscoverProjects())
        {
            if (File.Exists(proj.IndexFile))
            {
                var lines = File.ReadAllLines(proj.IndexFile);
                if (lines.Length > MaxIndexLines)
                    yield return new Diagnostic { Severity = DiagnosticSeverity.Warning, Category = "memory.index", Message = $"MEMORY.md exceeds {MaxIndexLines} lines ({lines.Length}). Lines after {MaxIndexLines} get truncated.", File = proj.IndexFile, Hint = "Move older entries into subfiles or remove stale ones." };
            }
            foreach (var item in proj.IndexItems)
            {
                if (item.RawLine.Length > MaxIndexItemChars)
                    yield return new Diagnostic { Severity = DiagnosticSeverity.Info, Category = "memory.index", Message = $"Index item exceeds {MaxIndexItemChars} chars: '{item.Title}'.", File = proj.IndexFile, Line = item.LineNumber };
                if (!item.TargetExists)
                    yield return new Diagnostic { Severity = DiagnosticSeverity.Error, Category = "memory.index", Message = $"Broken link in MEMORY.md: {item.LinkedFile}", File = proj.IndexFile, Line = item.LineNumber, Hint = "Restore or remove the index entry." };
            }
            foreach (var entry in proj.Entries.Where(e => !e.ReferencedInIndex && !e.Disabled))
            {
                yield return new Diagnostic { Severity = DiagnosticSeverity.Warning, Category = "memory.orphan", Message = $"Orphan memory file (not in MEMORY.md): {entry.FileName}", File = entry.FilePath, Hint = "Add to MEMORY.md or delete." };
            }
            var dupTitles = proj.IndexItems.GroupBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
            foreach (var g in dupTitles)
                yield return new Diagnostic { Severity = DiagnosticSeverity.Warning, Category = "memory.duplicate", Message = $"Duplicate index title: '{g.Key}' ({g.Count()} entries).", File = proj.IndexFile };
        }
    }

    private static IEnumerable<Diagnostic> LintSkills()
    {
        foreach (var skill in SkillLoader.LoadAll())
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
                yield return new Diagnostic { Severity = DiagnosticSeverity.Error, Category = "skill", Message = "Skill missing 'name' in frontmatter.", File = skill.SkillFilePath };
            if (string.IsNullOrWhiteSpace(skill.Description))
                yield return new Diagnostic { Severity = DiagnosticSeverity.Warning, Category = "skill", Message = $"Skill '{skill.Name}' missing description.", File = skill.SkillFilePath };
        }
    }

    private static IEnumerable<Diagnostic> LintSettings()
    {
        foreach (var path in new[] { ClaudePaths.SettingsJson, ClaudePaths.LocalSettingsJson })
        {
            if (!File.Exists(path)) continue;
            string? err = null;
            try
            {
                var text = File.ReadAllText(path);
                System.Text.Json.JsonDocument.Parse(text);
            }
            catch (Exception ex) { err = ex.Message; }
            if (err is not null)
                yield return new Diagnostic { Severity = DiagnosticSeverity.Error, Category = "settings", Message = $"Invalid JSON: {err}", File = path };
        }
    }
}
