using System.Collections.Generic;

namespace ClaudeCodeManager.Core.Models;

public sealed class Frontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Model { get; set; }
    public List<string> Tools { get; set; } = new();
    public Dictionary<string, object?> Extra { get; set; } = new();
    public bool IsEmpty => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Description) && string.IsNullOrEmpty(Type) && string.IsNullOrEmpty(Model) && Tools.Count == 0 && Extra.Count == 0;
}
