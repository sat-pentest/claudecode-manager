using System;

namespace ClaudeCodeManager.Core.Models;

public sealed class Snapshot
{
    public string CommitSha { get; set; } = "";
    public string ShortSha => CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Author { get; set; } = "";
    public int FilesChanged { get; set; }
}
