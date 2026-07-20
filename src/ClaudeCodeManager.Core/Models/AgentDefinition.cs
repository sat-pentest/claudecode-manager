using System;
using System.Collections.Generic;

namespace ClaudeCodeManager.Core.Models;

/// <summary>
/// A Claude Code custom subagent — a markdown file under ~/.claude/agents/
/// or &lt;project&gt;/.claude/agents/ with YAML frontmatter and a system prompt body.
/// Analogous to a Skill but spawns an isolated Claude instance with its own context.
/// </summary>
public sealed class AgentDefinition
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Model { get; set; }
    public List<string> Tools { get; set; } = new();
    public string Body { get; set; } = "";
    public bool Disabled { get; set; }
    public bool IsProjectScoped { get; set; }
    public string Scope { get; set; } = "user";     // "user" | "project"
    public string? ProjectRoot { get; set; }         // only for project-scoped
    public DateTime ModifiedAt { get; set; }
}
