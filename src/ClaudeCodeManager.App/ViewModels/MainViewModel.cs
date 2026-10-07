using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exception = System.Exception;

namespace ClaudeCodeManager.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Flat list, in nav order. Kept alongside <see cref="Groups"/> because lookups like
    /// <see cref="NavigateToFile"/> want "the skills module", not "the module inside ASSETS".</summary>
    public ObservableCollection<ModuleBase> Modules { get; } = new();

    /// <summary>What the nav rail actually renders.</summary>
    public ObservableCollection<ModuleGroup> Groups { get; } = new();

    /// <summary>Text typed into the rail's filter box.</summary>
    [ObservableProperty] private string _moduleFilter = "";

    /// <summary>True while a filter is narrowing the rail — used to keep the forced folds out of
    /// the saved layout.</summary>
    private bool _filtering;

    partial void OnModuleFilterChanged(string value)
    {
        _filtering = true;
        try { foreach (var g in Groups) g.ApplyFilter(value); }
        finally { _filtering = false; }
    }

    [ObservableProperty] private ModuleBase? _current;

    /// <summary>True while the constructor runs, so the first module does not count as navigation.</summary>
    private bool _starting = true;

    partial void OnCurrentChanged(ModuleBase? value)
    {
        // Navigating into a shut section would leave the selection invisible, so the section opens.
        // Startup is the exception: the first module is not something the user chose, and opening
        // its section then would mean a section holding the startup module could never stay shut.
        foreach (var m in Modules) m.IsCurrent = ReferenceEquals(m, value);

        if (_starting) return;
        foreach (var g in Groups)
            if (g.Contains(value)) { g.EnsureExpanded(); break; }
    }
    [ObservableProperty] private string _claudeRootPath = ClaudePaths.ClaudeRoot;
    [ObservableProperty] private bool _claudeRootExists;
    [ObservableProperty] private string _externalChangeNotice = "";

    public SnapshotManager Snapshots { get; }
    public FileWatcherHub Watcher { get; }

    /// <summary>Config environments found side by side under the user profile (PENTEST · STUDIO · …).</summary>
    public ObservableCollection<ClaudeEnvironment> Environments { get; } = new();

    /// <summary>The selected environment. Switching re-points every module at its config dir.</summary>
    [ObservableProperty] private ClaudeEnvironment? _activeEnvironment;

    public MainViewModel()
    {
        // Restore the environment chosen last run BEFORE anything reads a path, so the watcher and
        // every module below load against the right config dir from the start.
        EnvironmentRegistry.RestorePersisted();

        ClaudeRootExists = ClaudePaths.ClaudeRootExists();
        Snapshots = new SnapshotManager();
        Watcher = new FileWatcherHub();

        BuildGroups();
        LoadEnvironments();

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
            var path = pending;
            pending = null;
            if (path is null) return;
            // Rebuilding costs the user their selection and scroll position, so only the module
            // that actually reads this file gets rebuilt.
            if (Current is null || !Current.DependsOn(path)) return;
            ExternalChangeNotice = $"external change detected · {System.IO.Path.GetFileName(path)}";
            Current.OnActivated();
        };

        _starting = false;

    }

    /// <summary>
    /// Sections are cut by what you are doing, not by what the data is.
    ///
    /// OVERVIEW is where you start a question, ASSETS is what you author and edit, RUNTIME is the
    /// record of what actually ran, SYSTEM is the machinery around all of it. HARNESS sits in
    /// RUNTIME rather than SYSTEM because it answers "what ran and what is kept", which is the same
    /// question SESSIONS and COST answer.
    /// </summary>
    private void BuildGroups()
    {
        var dashboard = new DashboardViewModel(this);
        var search = new SearchViewModel(this);
        var claudeMd = new ClaudeMdViewModel(this);
        var memory = new MemoryViewModel(this);
        var skills = new SkillsViewModel(this);
        var agents = new AgentsViewModel(this);
        var workflows = new WorkflowsViewModel(this);
        var mcp = new McpStatusViewModel(this);
        var harness = new HarnessViewModel(this);
        var sessions = new SessionsViewModel(this);
        var schedule = new ScheduleViewModel(this);
        var cost = new CostViewModel(this);
        var settings = new SettingsViewModel(this);
        var snapshots = new SnapshotsViewModel(this);
        var diagnostics = new DiagnosticsViewModel(this);

        var collapsed = NavGroupStore.LoadCollapsed();

        void Add(string key, string title, string hint, params ModuleBase[] members)
        {
            var g = new ModuleGroup(key, title, hint, members);
            if (collapsed.Contains(key)) g.IsExpanded = false;
            g.Toggled += _ => PersistGroupLayout();
            Groups.Add(g);
            foreach (var m in members) Modules.Add(m);
        }

        Add("overview", "OVERVIEW", "한눈에 보기 · 찾기", dashboard, search);
        Add("assets", "ASSETS", "읽고 고치는 자산", claudeMd, memory, skills, agents, workflows, mcp);
        Add("runtime", "RUNTIME", "무엇이 돌았나", harness, sessions, schedule, cost);
        Add("system", "SYSTEM", "설정 · 복원 · 점검", settings, snapshots, diagnostics);
    }

    private void PersistGroupLayout()
    {
        if (_filtering) return;   // a fold forced open by a search is not a choice
        NavGroupStore.SaveCollapsed(Groups.Where(g => !g.IsExpanded).Select(g => g.Key));
    }

    // ── Environment switching ─────────────────────────────────────────────────────────────────
    //
    // One machine holds several Claude config dirs (~/.claude pentest, ~/.claude-studio, …). The
    // active one is ClaudePaths.ClaudeRoot; switching it re-points every module at once.

    private void LoadEnvironments()
    {
        Environments.Clear();
        foreach (var e in EnvironmentRegistry.Discover()) Environments.Add(e);
        var active = EnvironmentRegistry.Active;
        // Set the backing field directly so wiring up the initial selection is not treated as a switch.
#pragma warning disable MVVMTK0034
        _activeEnvironment = Environments.FirstOrDefault(e =>
                                 string.Equals(e.Path, active.Path, StringComparison.OrdinalIgnoreCase))
                             ?? Environments.FirstOrDefault();
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(ActiveEnvironment));
    }

    partial void OnActiveEnvironmentChanged(ClaudeEnvironment? value)
    {
        if (_starting || value is null) return;
        if (string.Equals(value.Path, ClaudePaths.ClaudeRoot, StringComparison.OrdinalIgnoreCase)) return;

        EnvironmentRegistry.SetActive(value);
        ClaudeRootPath = ClaudePaths.ClaudeRoot;
        ClaudeRootExists = ClaudePaths.ClaudeRootExists();
        Watcher.Repoint();

        // The visible module reloads now; the rest re-read when next navigated to (OnActivated).
        try { Current?.OnActivated(); }
        catch (Exception ex) { ExternalChangeNotice = "env switch err: " + ex.Message; return; }
        ExternalChangeNotice = $"environment · {value.Name}  ·  {value.Path}";
    }

    /// <summary>Where <see cref="LaunchSession"/> opens the session — a terminal or an editor.</summary>
    public ObservableCollection<string> LaunchTargets { get; } = new() { "CLI", "VSCode", "Cursor" };

    [ObservableProperty] private string _launchTarget = "CLI";

    /// <summary>Open a new session with CLAUDE_CONFIG_DIR set to the active environment — so it loads
    /// this env's skills/memory/CLAUDE.md, not the default. CLI opens a terminal running
    /// <c>claude</c>; VSCode opens an editor window (env only reliably applies to a fresh VSCode
    /// instance — an already-running one reuses its own environment).</summary>
    [RelayCommand]
    private void LaunchSession()
    {
        var env = ActiveEnvironment ?? EnvironmentRegistry.Active;
        var work = EnvironmentRegistry.ResolveWorkDir(env); // STUDIO → C:\studio, not home
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            if (LaunchTarget == "CLI")
            {
                // An interactive terminal that stays open is the point here — /k keeps it.
                // No space before '&&': cmd would otherwise fold it into the set value.
                var inner = $"title claude [{env.Name}]&& set CLAUDE_CONFIG_DIR={env.Path}&& claude";
                psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k \"{inner}\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = work
                };
            }
            else
            {
                // Editor launch: the cmd is only a one-shot springboard, so run it hidden and let it
                // exit — no lingering console window. The env var is injected directly (not via `set`),
                // which also sidesteps cmd's trailing-space quirk entirely.
                var folder = $"\"{work}\"";
                var runCmd = LaunchTarget == "Cursor"
                    ? $"cursor --new-window {folder}"
                    : $"code --new-window {folder}";
                psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {runCmd}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = work
                };
                psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = env.Path;
            }

            System.Diagnostics.Process.Start(psi);
            ExternalChangeNotice = $"session launched · {env.Name} · {LaunchTarget}";
        }
        catch (Exception ex) { ExternalChangeNotice = "launch err: " + ex.Message; }
    }


    [RelayCommand]
    private void NavigateToKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var target = Modules.FirstOrDefault(m => m.Key == key);
        if (target is not null) Navigate(target);
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
    private async System.Threading.Tasks.Task QuickSnapshotAsync()
    {
        var snap = await Snapshots.CreateSnapshotAsync($"manual snapshot @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
