using System;
using System.Collections.Generic;

namespace ClaudeCodeManager.Core.Models;

public sealed class Skill
{
    public string FolderPath { get; set; } = "";
    public string SkillFilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Model { get; set; }
    public List<string> Tools { get; set; } = new();
    public List<string> Triggers { get; set; } = new();
    public bool HasTriggers => Triggers.Count > 0;
    public string Body { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTime ModifiedAt { get; set; }
}
