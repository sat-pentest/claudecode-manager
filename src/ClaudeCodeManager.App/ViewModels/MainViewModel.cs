using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exception = System.Exception;

namespace ClaudeCodeManager.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<ModuleBase> Modules { get; } = new();
    [ObservableProperty] private ModuleBase? _current;
    [ObservableProperty] private string _claudeRootPath = ClaudePaths.ClaudeRoot;
    [ObservableProperty] private bool _claudeRootExists;
    [ObservableProperty] private string _externalChangeNotice = "";

    public SnapshotManager Snapshots { get; }
    public FileWatcherHub Watcher { get; }

    public MainViewModel()
    {
        ClaudeRootExists = ClaudePaths.ClaudeRootExists();
        Snapshots = new SnapshotManager();
        Watcher = new FileWatcherHub();

        Modules.Add(new DashboardViewModel(this));
        Modules.Add(new HarnessViewModel(this));
        Modules.Add(new ClaudeMdViewModel(this));
        Modules.Add(new MemoryViewModel(this));
        Modules.Add(new SkillsViewModel(this));
        Modules.Add(new AgentsViewModel(this));
        Modules.Add(new WorkflowsViewModel(this));
        Modules.Add(new McpStatusViewModel(this));
        Modules.Add(new SessionsViewModel(this));
        Modules.Add(new SettingsViewModel(this));
        Modules.Add(new SearchViewModel(this));
        Modules.Add(new SnapshotsViewModel(this));
        Modules.Add(new DiagnosticsViewModel(this));

        Current = Modules.First();
        Current?.OnActivated();

        var debouncer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        string? pending = null;
        Watcher.Changed += p =>
        {
            pending = p;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                debouncer.Stop();
                debouncer.Start();
            });
        };
        debouncer.Tick += (s, e) =>
        {
            debouncer.Stop();
            if (pending is null) return;
            ExternalChangeNotice = $"external change detected · {System.IO.Path.GetFileName(pending)}";
            Current?.OnActivated();
        };
    }

    [RelayCommand]
    private void Navigate(ModuleBase target)
    {
        if (target is null || ReferenceEquals(target, Current)) return;
        try { Current?.OnDeactivated(); } catch (Exception ex) { ExternalChangeNotice = "deactivate err: " + ex.Message; }
        Current = target;
        try { Current.OnActivated(); } catch (Exception ex) { ExternalChangeNotice = "activate err: " + ex.Message; }
        if (!ExternalChangeNotice.StartsWith("activate") && !ExternalChangeNotice.StartsWith("deactivate")) ExternalChangeNotice = "";
    }

    [RelayCommand]
    private void QuickSnapshot()
    {
        var snap = Snapshots.CreateSnapshot($"manual snapshot @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        ExternalChangeNotice = $"snapshot · {snap.ShortSha}";
    }

    [RelayCommand]
    private void OpenClaudeRoot()
    {
        try
        {
            var path = ClaudePaths.ClaudeRoot;
            if (!System.IO.Directory.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* ignore — non-critical UI action */ }
    }

    /// <summary>
    /// Navigate to the module that owns the given file and select it there.
    /// Falls back to opening Explorer if no module matches.
    /// </summary>
    public void NavigateToFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var normalized = filePath.Replace('/', '\\');
        var claudeRoot = ClaudePaths.ClaudeRoot.Replace('/', '\\').TrimEnd('\\');

        var skillsRoot = System.IO.Path.Combine(claudeRoot, "skills");
        var workflowsRoot = System.IO.Path.Combine(claudeRoot, "workflows");
        var projectsRoot = System.IO.Path.Combine(claudeRoot, "projects");
        var fileName = System.IO.Path.GetFileName(normalized);

        ModuleBase? target = null;

        if (normalized.StartsWith(skillsRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            target = Modules.FirstOrDefault(m => m is SkillsViewModel);
        }
        else if (normalized.StartsWith(workflowsRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            target = Modules.FirstOrDefault(m => m is WorkflowsViewModel);
        }
        else if (normalized.StartsWith(projectsRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(fileName, "CLAUDE.md", System.StringComparison.OrdinalIgnoreCase))
                target = Modules.FirstOrDefault(m => m is ClaudeMdViewModel);
            else
                target = Modules.FirstOrDefault(m => m is MemoryViewModel);
        }
        else if (normalized.StartsWith(claudeRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            if (fileName.StartsWith("CLAUDE", System.StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase))
                target = Modules.FirstOrDefault(m => m is ClaudeMdViewModel);
            else if (fileName.Equals("settings.json", System.StringComparison.OrdinalIgnoreCase)
                  || fileName.Equals("settings.local.json", System.StringComparison.OrdinalIgnoreCase)
                  || fileName.Equals("keybindings.json", System.StringComparison.OrdinalIgnoreCase))
                target = Modules.FirstOrDefault(m => m is SettingsViewModel);
        }

        if (target is null)
        {
            // Unknown location — fall back to Explorer
            try
            {
                var dir = System.IO.Path.GetDirectoryName(normalized);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{normalized}\"") { UseShellExecute = true });
            }
            catch { }
            return;
        }

        // Switch module
        Current?.OnDeactivated();
        Current = target;
        try { target.OnActivated(); } catch { }

        // Ask module to select the specific item
        switch (target)
        {
            case ClaudeMdViewModel cvm: cvm.SelectByPath(normalized); break;
            case MemoryViewModel mvm: mvm.SelectByPath(normalized); break;
            case SkillsViewModel svm: svm.SelectByPath(normalized); break;
            case WorkflowsViewModel wvm: wvm.SelectByPath(normalized); break;
            case SettingsViewModel setvm: setvm.SelectByPath(normalized); break;
        }
        ExternalChangeNotice = "navigated · " + System.IO.Path.GetFileName(normalized);
    }

    public void Dispose() => Watcher.Dispose();
}
