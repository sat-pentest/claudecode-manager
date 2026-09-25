using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;
using LibGit2Sharp;

namespace ClaudeCodeManager.Core.Services;

public sealed class SnapshotManager
{
    private readonly string _mirrorRoot;
    private readonly string _claudeRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Signature is built per commit — a static one froze every snapshot's timestamp at
    /// app start, so the SNAPSHOTS list showed the same time for a whole session.</summary>
    private static Signature Author() => new("ClaudeCode Manager", "manager@local", DateTimeOffset.Now);

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

    /// <summary>
    /// Snapshot the mirror. Serialized: every mutating command in the app takes a pre-action
    /// snapshot, and two of those running at once would stage each other's half-written mirror.
    ///
    /// Cost model (measured on this machine, 927 mirrored files / 1347 tracked):
    ///   old — unconditional copy of every file, then <c>Stage("*")</c> + <c>RetrieveStatus()</c>:
    ///         ~11 s, and *the same 11 s when nothing had changed*, because rewriting every file
    ///         moved its mtime and git had to re-hash the entire working tree.
    ///   new — stat-compare the mirror and copy only what moved, then stage only those paths and
    ///         skip the working-directory status scan entirely: ~130 ms idle, ~300 ms for a
    ///         one-file change.
    /// </summary>
    public Snapshot CreateSnapshot(string message)
    {
        _gate.Wait();
        try { return CreateSnapshotCore(message); }
        finally { _gate.Release(); }
    }

    /// <summary>Off the UI thread. Callers that snapshot *before* mutating must await this —
    /// fire-and-forget would race the mirror against the change it is supposed to precede.</summary>
    public Task<Snapshot> CreateSnapshotAsync(string message) => Task.Run(() => CreateSnapshot(message));

    private Snapshot CreateSnapshotCore(string message)
    {
        var changed = MirrorClaudeFiles();
        using var repo = new Repository(_mirrorRoot);
        var tip = repo.Head.Tip;

        if (changed.Count == 0 && tip is not null)
        {
            return new Snapshot
            {
                CommitSha = tip.Sha,
                Message = "(no changes since last snapshot) " + message,
                CreatedAt = tip.Author.When,
                Author = tip.Author.Name
            };
        }

        // Stage by path rather than "*": the glob walks and hashes the whole working tree, which is
        // the bulk of the old cost. We already know exactly which paths moved.
        if (changed.Count > 0) Commands.Stage(repo, changed);

        var author = Author();
        var commit = repo.Commit(message, author, author, new CommitOptions { AllowEmptyCommit = true });
        return new Snapshot
        {
            CommitSha = commit.Sha,
            Message = commit.MessageShort,
            CreatedAt = commit.Author.When,
            Author = commit.Author.Name,
            FilesChanged = changed.Count
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

    /// <summary><c>.mjs</c> is here because workflow scripts are .mjs: without it the WORKFLOWS
    /// delete dialog promised "스냅샷에서 복구 가능" while the file was never mirrored at all.</summary>
    private static readonly HashSet<string> MirrorExtensions =
        new(new[] { ".md", ".json", ".mjs" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Top-level directories under ~/.claude that hold no user configuration. <c>telemetry</c> and
    /// <c>cache</c> are Claude Code's own churn (a 1.1 MB failed-events dump, a 500 KB changelog)
    /// and were rewriting themselves into every snapshot.
    /// </summary>
    private static readonly HashSet<string> MirrorSkipDirs =
        new(new[] { "shell-snapshots", "todos", "statsig", "ide", "telemetry", "cache" },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bring the mirror in line with ~/.claude and report which paths moved, git-relative.
    ///
    /// Copies only files whose length or write time differs — the previous unconditional
    /// <c>File.Copy</c> touched all ~900 mirrored files on every snapshot, which invalidated git's
    /// index stat cache and forced a full re-hash downstream. Files that vanished from the source
    /// are now removed from the mirror too, so a snapshot records deletions instead of keeping
    /// every file ever seen (a disabled memory entry, for one, leaves the mirrored set).
    /// </summary>
    public IReadOnlyList<string> MirrorClaudeFiles()
    {
        var changed = new List<string>();
        if (!Directory.Exists(_claudeRoot)) return changed;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in Directory.EnumerateFiles(_claudeRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_claudeRoot, src);
            var topDir = rel.Split(Path.DirectorySeparatorChar).FirstOrDefault() ?? "";
            if (MirrorSkipDirs.Contains(topDir)) continue;
            if (!MirrorExtensions.Contains(Path.GetExtension(src))) continue;
            seen.Add(rel);

            try
            {
                var s = new FileInfo(src);
                var dest = Path.Combine(_mirrorRoot, rel);
                var d = new FileInfo(dest);
                if (d.Exists && d.Length == s.Length && d.LastWriteTimeUtc == s.LastWriteTimeUtc) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
                // Explicit, so the next run's stat comparison is against the source's own time
                // rather than whatever the copy happened to stamp.
                File.SetLastWriteTimeUtc(dest, s.LastWriteTimeUtc);
                changed.Add(ToGitPath(rel));
            }
            catch (IOException) { /* written mid-scan; the next snapshot picks it up */ }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var dst in Directory.EnumerateFiles(_mirrorRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_mirrorRoot, dst);
            var topDir = rel.Split(Path.DirectorySeparatorChar).FirstOrDefault() ?? "";
            if (string.Equals(topDir, ".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Contains(rel)) continue;
            try
            {
                File.Delete(dst);
                changed.Add(ToGitPath(rel));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return changed;
    }

    private static string ToGitPath(string relative) => relative.Replace('\\', '/');
}
