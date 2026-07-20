using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public static class SkillLoader
{
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
