namespace ClaudeCodeManager.Core.Models;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? File { get; set; }
    public int? Line { get; set; }
    public string? Hint { get; set; }
}
