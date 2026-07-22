using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public static class SkillLoader
{
    // Match "..." or `...` phrases (1-80 chars, no newline inside)
    private static readonly Regex TriggerPhraseRegex = new(
        "(?:\"([^\"\\r\\n]{1,80})\"|`([^`\\r\\n]{1,60})`)",
        RegexOptions.Compiled);

    public static List<string> ExtractTriggers(string? description)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(description)) return result;
        foreach (Match m in TriggerPhraseRegex.Matches(description))
        {
            var v = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (v.Length == 0) continue;
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase))
                result.Add(v);
        }
        return result;
    }

    public static List<Skill> LoadAll()
    {
        var skills = new List<Skill>();
        if (!Directory.Exists(ClaudePaths.SkillsRoot)) return skills;
        foreach (var dir in Directory.EnumerateDirectories(ClaudePaths.SkillsRoot))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            var disabled = false;
            if (!File.Exists(skillFile))
            {
                var disabledFile = Path.Combine(dir, "SKILL.md.disabled");
                if (File.Exists(disabledFile)) { skillFile = disabledFile; disabled = true; }
                else continue;
            }
            var raw = File.ReadAllText(skillFile);
            var (fm, body) = FrontmatterParser.Parse(raw);
            var fi = new FileInfo(skillFile);
            skills.Add(new Skill
            {
                FolderPath = dir,
                SkillFilePath = skillFile,
                Name = !string.IsNullOrEmpty(fm.Name) ? fm.Name! : Path.GetFileName(dir),
                Description = fm.Description,
                Model = fm.Model,
                Tools = fm.Tools,
                Triggers = ExtractTriggers(fm.Description),
                Body = body,
                Disabled = disabled,
                ModifiedAt = fi.LastWriteTime
            });
        }
        return skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Toggle(Skill skill)
    {
        var dir = skill.FolderPath;
        var enabled = Path.Combine(dir, "SKILL.md");
        var disabled = Path.Combine(dir, "SKILL.md.disabled");
        if (skill.Disabled && File.Exists(disabled))
        {
            File.Move(disabled, enabled);
            skill.Disabled = false;
            skill.SkillFilePath = enabled;
        }
        else if (!skill.Disabled && File.Exists(enabled))
        {
            File.Move(enabled, disabled);
            skill.Disabled = true;
            skill.SkillFilePath = disabled;
        }
    }
}
