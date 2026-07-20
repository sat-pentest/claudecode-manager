using System.Collections.Generic;

namespace ClaudeCodeManager.Core.Models;

public sealed class ClaudeMdSection
{
    public int Level { get; set; }
    public string Title { get; set; } = "";
    public string Anchor { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = "";
    public int LineCount => EndLine - StartLine + 1;
    public List<ClaudeMdSection> Children { get; set; } = new();
}
