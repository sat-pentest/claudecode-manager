using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;
using LibGit2Sharp;

namespace ClaudeCodeManager.Core.Services;

public sealed class SnapshotManager
{
    private readonly string _mirrorRoot;
    private readonly string _claudeRoot;
    private static readonly Signature _author = new("ClaudeCode Manager", "manager@local", DateTimeOffset.Now);

    public SnapshotManager()
    {
        ClaudePaths.EnsureManagerDirs();
        _mirrorRoot = ClaudePaths.SnapshotsRoot;
        _claudeRoot = ClaudePaths.ClaudeRoot;
        EnsureRepo();
    }

    private void EnsureRepo()
    {
        if (!Directory.Exists(Path.Combine(_mirrorRoot, ".git")))
        {
            Repository.Init(_mirrorRoot);
        }
    }

    public Snapshot CreateSnapshot(string message)
    {
        MirrorClaudeFiles();
        using var repo = new Repository(_mirrorRoot);
        Commands.Stage(repo, "*");
        var status = repo.RetrieveStatus();
        if (!status.IsDirty && repo.Head.Tip is not null)
        {
            var tip = repo.Head.Tip;
            return new Snapshot
            {
                CommitSha = tip.Sha,
                Message = "(no changes since last snapshot) " + message,
                CreatedAt = tip.Author.When,
                Author = tip.Author.Name
            };
        }
        var commit = repo.Commit(message, _author, _author, new CommitOptions { AllowEmptyCommit = true });
        return new Snapshot
        {
            CommitSha = commit.Sha,
            Message = commit.MessageShort,
            CreatedAt = commit.Author.When,
            Author = commit.Author.Name,
            FilesChanged = status.Count()
        };
    }

    public List<Snapshot> ListSnapshots(int limit = 100)
    {
        var result = new List<Snapshot>();
        if (!Directory.Exists(Path.Combine(_mirrorRoot, ".git"))) return result;
        using var repo = new Repository(_mirrorRoot);
        foreach (var c in repo.Commits.Take(limit))
        {
            result.Add(new Snapshot
            {
                CommitSha = c.Sha,
                Message = c.MessageShort,
                CreatedAt = c.Author.When,
                Author = c.Author.Name
            });
        }
        return result;
    }

    public string GetCommitDiff(string sha)
    {
        if (!Directory.Exists(Path.Combine(_mirrorRoot, ".git"))) return "";
        using var repo = new Repository(_mirrorRoot);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return "(commit not found)";
        if (commit.Parents.Count() == 0)
        {
            return $"Initial snapshot — {commit.Tree.Count} files";
        }
        var parent = commit.Parents.First();
        var patch = repo.Diff.Compare<Patch>(parent.Tree, commit.Tree);
        return patch.Content;
    }

    public void RestoreSnapshot(string sha)
    {
        if (!Directory.Exists(Path.Combine(_mirrorRoot, ".git"))) return;
        using var repo = new Repository(_mirrorRoot);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return;
        foreach (var entry in commit.Tree)
        {
            RestoreTreeEntry(entry, "");
        }
    }

    private void RestoreTreeEntry(TreeEntry entry, string subPath)
    {
        var relative = string.IsNullOrEmpty(subPath) ? entry.Name : Path.Combine(subPath, entry.Name);
        if (entry.TargetType == TreeEntryTargetType.Blob)
        {
            var blob = (Blob)entry.Target;
            var dest = Path.Combine(_claudeRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var fs = File.Create(dest);
            using var content = blob.GetContentStream();
            content.CopyTo(fs);
        }
        else if (entry.TargetType == TreeEntryTargetType.Tree)
        {
            var tree = (Tree)entry.Target;
            foreach (var child in tree) RestoreTreeEntry(child, relative);
        }
    }

    public void MirrorClaudeFiles()
    {
        if (!Directory.Exists(_claudeRoot)) return;
        var extensions = new HashSet<string>(new[] { ".md", ".json" }, StringComparer.OrdinalIgnoreCase);
        var skipDirs = new HashSet<string>(new[] { "shell-snapshots", "todos", "statsig", "ide" }, StringComparer.OrdinalIgnoreCase);
        foreach (var src in Directory.EnumerateFiles(_claudeRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_claudeRoot, src);
            var topDir = rel.Split(Path.DirectorySeparatorChar).FirstOrDefault() ?? "";
            if (skipDirs.Contains(topDir)) continue;
            if (!extensions.Contains(Path.GetExtension(src))) continue;
            var dest = Path.Combine(_mirrorRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
    }
}
