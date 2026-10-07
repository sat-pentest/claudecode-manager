using System;
using System.Collections.Concurrent;
using System.IO;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class FileWatcherHub : IDisposable
{
    private FileSystemWatcher? _watcher;

    /// <summary>Directories whose events are currently the app's own doing, with the time each
    /// mute lapses. Without this every save/toggle bounced back as an "external change" and made
    /// the active module rebuild itself a second time, throwing away the selection.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _muted = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Changed;

    public FileWatcherHub() => Attach();

    /// <summary>
    /// Re-aim the watcher at the current <see cref="ClaudePaths.ClaudeRoot"/>. Called after an
    /// environment switch so file events come from the newly selected config directory, not the old.
    /// </summary>
    public void Repoint()
    {
        _watcher?.Dispose();
        _watcher = null;
        _muted.Clear();
        Attach();
    }

    private void Attach()
    {
        if (!Directory.Exists(ClaudePaths.ClaudeRoot)) return;
        _watcher = new FileSystemWatcher(ClaudePaths.ClaudeRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += Forward;
        _watcher.Created += Forward;
        _watcher.Renamed += (s, e) =>
        {
            // Renames used to forward unconditionally, so every atomic write in the tree — including
            // AtomicFileWriter's own temp-then-rename — announced itself as an external change.
            if (IsInteresting(e.FullPath)) Forward(s, e);
            else if (!string.IsNullOrEmpty(e.OldFullPath) && IsInteresting(e.OldFullPath)) Forward(s, e);
        };
        _watcher.Deleted += Forward;
    }

    /// <summary>
    /// Suppress events under <paramref name="directory"/> for a short window. Call it immediately
    /// before writing files there: the caller already knows what it changed and refreshes itself,
    /// so the watcher echo is pure duplicate work.
    /// </summary>
    public void MuteDirectory(string directory, TimeSpan window)
    {
        if (string.IsNullOrEmpty(directory)) return;
        _muted[Normalize(directory)] = DateTime.UtcNow + window;
    }

    private bool IsMuted(string fullPath)
    {
        if (_muted.IsEmpty) return false;
        var now = DateTime.UtcNow;
        var muted = false;
        foreach (var kv in _muted)
        {
            if (kv.Value <= now) { _muted.TryRemove(kv.Key, out _); continue; }
            if (!muted && fullPath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) muted = true;
        }
        return muted;
    }

    /// <summary>
    /// The file kinds the app actually models. A trailing <c>.disabled</c> is stripped first so a
    /// toggled skill, agent, memory entry or workflow still reports as its own kind.
    /// </summary>
    private static bool IsInteresting(string fullPath)
    {
        var name = fullPath;
        if (name.EndsWith(Models.MemoryEntry.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^Models.MemoryEntry.DisabledSuffix.Length];

        var ext = Path.GetExtension(name);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".mjs", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string dir)
    {
        var full = Path.GetFullPath(dir);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private void Forward(object sender, FileSystemEventArgs e)
    {
        if (!IsInteresting(e.FullPath)) return;
        if (IsMuted(e.FullPath)) return;
        Changed?.Invoke(e.FullPath);
    }

    public void Dispose() => _watcher?.Dispose();
}
