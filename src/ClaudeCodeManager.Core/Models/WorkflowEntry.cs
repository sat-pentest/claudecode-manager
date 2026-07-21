using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCodeManager.Core.Models;

/// <summary>
/// One phase entry extracted from a workflow's `meta.phases: [{title, detail?}, ...]`.
/// Index/IsLast are computed at load time so the pipeline view can render arrow connectors
/// between all-but-last steps.
/// </summary>
public sealed class WorkflowPhase
{
    public int Index { get; set; }               // 1-based, for the badge display
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Tools { get; set; } = new();  // optional: pills shown under detail
    public bool IsLast { get; set; }             // suppresses the arrow after the final phase
}

/// <summary>
/// A workflow script file in ~/.claude/workflows/. Each file is a JavaScript module
/// whose `export const meta = {...}` block declares name/description/phases.
/// Disable pattern: rename `<name>.mjs` → `<name>.mjs.disabled` (Workflow tool ignores it).
/// </summary>
public sealed class WorkflowEntry
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Filename without trailing `.disabled` suffix if present.</summary>
    public string DisplayName => FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
        ? FileName.Substring(0, FileName.Length - DisabledSuffix.Length)
        : FileName;

    public bool Disabled => FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<WorkflowPhase> Phases { get; set; } = new();

    /// <summary>Raw script text (for the detail viewer).</summary>
    public string Text { get; set; } = "";

    public DateTime ModifiedAt { get; set; }

    public const string DisabledSuffix = ".disabled";
}
