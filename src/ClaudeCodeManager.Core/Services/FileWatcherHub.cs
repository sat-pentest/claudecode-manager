using System;
using System.IO;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class FileWatcherHub : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    public event Action<string>? Changed;

    public FileWatcherHub()
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
        _watcher.Renamed += (s, e) => Changed?.Invoke(e.FullPath);
        _watcher.Deleted += Forward;
    }

    private void Forward(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath);
        if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            Changed?.Invoke(e.FullPath);
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
