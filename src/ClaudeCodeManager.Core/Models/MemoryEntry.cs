using System;

namespace ClaudeCodeManager.Core.Models;

public enum MemoryType { User, Feedback, Project, Reference, Unknown }

public sealed class MemoryEntry
{
    public const string DisabledSuffix = ".disabled";

    public string FilePath { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(FilePath);

    /// <summary>Filename without trailing .disabled suffix if present.</summary>
    public string DisplayName => FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
        ? FileName.Substring(0, FileName.Length - DisabledSuffix.Length)
        : FileName;

    /// <summary>True if file ends with .md.disabled (Claude Code will not load it).</summary>
    public bool Disabled { get; set; }

    public Frontmatter Frontmatter { get; set; } = new();
    public string Body { get; set; } = "";
    public int LineCount { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool ReferencedInIndex { get; set; }
    public MemoryType Type => Frontmatter.Type?.ToLowerInvariant() switch
    {
        "user" => MemoryType.User,
        "feedback" => MemoryType.Feedback,
        "project" => MemoryType.Project,
        "reference" => MemoryType.Reference,
        _ => MemoryType.Unknown
    };
}
