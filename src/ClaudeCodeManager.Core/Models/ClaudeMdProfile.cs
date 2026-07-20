using System;

namespace ClaudeCodeManager.Core.Models;

public sealed class ClaudeMdProfile
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsReservedName { get; set; }
}
