namespace ClaudeCodeManager.Core.Models;

public sealed class MemoryIndexItem
{
    public string Title { get; set; } = "";
    public string LinkedFile { get; set; } = "";
    public string Hook { get; set; } = "";
    public string RawLine { get; set; } = "";
    public int LineNumber { get; set; }
    public bool TargetExists { get; set; }
}
