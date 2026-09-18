using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Loads Claude Code custom subagents from:
///   - ~/.claude/agents/*.md              (user-scope, global)
///   - ~/.claude/projects/*/agents/*.md   (project-scope for project-scoped agents when applicable)
///   - &lt;cwd&gt;/.claude/agents/*.md   (repo-level, not scanned here — those load with the workspace)
/// Enable/disable via .md ↔ .md.disabled rename (same convention as skills/memory).
/// </summary>
public static class AgentLoader
{
    public static List<AgentDefinition> LoadAll()
    {
        var agents = new List<AgentDefinition>();
        if (Directory.Exists(ClaudePaths.AgentsRoot))
        {
            foreach (var f in Directory.EnumerateFiles(ClaudePaths.AgentsRoot, "*.md", SearchOption.TopDirectoryOnly))
                agents.Add(LoadOne(f, disabled: false, scope: "user"));
            foreach (var f in Directory.EnumerateFiles(ClaudePaths.AgentsRoot, "*.md.disabled", SearchOption.TopDirectoryOnly))
                agents.Add(LoadOne(f, disabled: true, scope: "user"));
        }
        return agents.OrderBy(a => a.Disabled ? 1 : 0)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    private static AgentDefinition LoadOne(string filePath, bool disabled, string scope)
    {
        var raw = File.ReadAllText(filePath);
        var (fm, body) = FrontmatterParser.Parse(raw);
        var fi = new FileInfo(filePath);
        var fname = Path.GetFileNameWithoutExtension(filePath);
        if (disabled) fname = Path.GetFileNameWithoutExtension(fname);   // strip .md from .md.disabled

        return new AgentDefinition
        {
            FilePath = filePath,
            Name = !string.IsNullOrEmpty(fm.Name) ? fm.Name! : fname,
            Description = fm.Description,
            Model = fm.Model,
            Tools = fm.Tools,
            Body = body,
            Disabled = disabled,
            IsProjectScoped = scope == "project",
            Scope = scope,
            ModifiedAt = fi.LastWriteTime
        };
    }

    public static void Toggle(AgentDefinition agent)
    {
        var path = agent.FilePath;
        if (agent.Disabled && path.EndsWith(".md.disabled", StringComparison.OrdinalIgnoreCase))
        {
            var enabled = path[..^".disabled".Length];
            File.Move(path, enabled);
            agent.FilePath = enabled;
            agent.Disabled = false;
        }
        else if (!agent.Disabled && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var disabled = path + ".disabled";
            File.Move(path, disabled);
            agent.FilePath = disabled;
            agent.Disabled = true;
        }
    }

    /// <summary>Delete the agent definition file (.md or .md.disabled).</summary>
    public static void Delete(AgentDefinition agent)
    {
        if (File.Exists(agent.FilePath)) File.Delete(agent.FilePath);
    }
}
