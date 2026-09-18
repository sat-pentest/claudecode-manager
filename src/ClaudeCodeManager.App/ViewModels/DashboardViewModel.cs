using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class DashboardViewModel : ModuleBase
{
    public override string Key => "DASH";
    public override string Title => "DASHBOARD";
    public override string Glyph => "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z";

    private readonly MainViewModel _main;

    [ObservableProperty] private int _totalMemoryEntries;
    [ObservableProperty] private int _activeMemoryEntries;
    [ObservableProperty] private int _disabledMemoryEntries;
    [ObservableProperty] private int _totalProjects;
    [ObservableProperty] private int _totalSkills;
    [ObservableProperty] private int _activeSkills;
    [ObservableProperty] private int _disabledSkills;
    [ObservableProperty] private int _totalAgents;
    [ObservableProperty] private int _activeAgents;
    [ObservableProperty] private int _disabledAgents;
    [ObservableProperty] private int _totalWorkflows;
    [ObservableProperty] private int _activeWorkflows;
    [ObservableProperty] private int _disabledWorkflows;
    [ObservableProperty] private int _totalMcp;
    [ObservableProperty] private int _onlineMcp;
    [ObservableProperty] private int _disabledMcp;
    [ObservableProperty] private int _authRequiredMcp;
    [ObservableProperty] private int _snapshotCount;
    [ObservableProperty] private int _diagErrors;
    [ObservableProperty] private int _diagWarnings;
    [ObservableProperty] private int _diagInfo;
    [ObservableProperty] private string _lastSnapshot = "—";
    [ObservableProperty] private string _claudeRoot = ClaudePaths.ClaudeRoot;
    [ObservableProperty] private string _snapshotRoot = ClaudePaths.SnapshotsRoot;
    [ObservableProperty] private string _settingsExists = "—";
    [ObservableProperty] private string _localSettingsExists = "—";
    [ObservableProperty] private string _claudeMdStatus = "—";
    [ObservableProperty] private int _claudeMdLines;

    // Session storage stats (~/.claude/projects/)
    [ObservableProperty] private string _sessionsTotalSize = "0 B";
    [ObservableProperty] private long _sessionsTotalBytes;
    [ObservableProperty] private int _sessionsFileCount;
    [ObservableProperty] private string _sessionsOldest = "—";
    [ObservableProperty] private string _sessionsNewest = "—";
    [ObservableProperty] private string _cleanupPeriod = "—";
    [ObservableProperty] private string _sessionsTopProject = "—";
    [ObservableProperty] private string _sessionsTopProjectSize = "—";
    [ObservableProperty] private double _sessionsTopProjectPct;
    [ObservableProperty] private string _sessionsWarningLevel = "OK"; // OK / WARN / DANGER
    [ObservableProperty] private bool _cleanupPeriodInfinite;
    [ObservableProperty] private bool _showRelativeBars;

    public ObservableCollection<Diagnostic> TopDiagnostics { get; } = new();
    public ObservableCollection<SessionProjectRow> TopProjects { get; } = new();

    public DashboardViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var projects = MemoryIndexer.DiscoverProjects();
        TotalProjects = projects.Count;
        TotalMemoryEntries = projects.Sum(p => p.Entries.Count);
        // Memory has the same enabled/disabled notion the other assets do: a disabled entry is
        // renamed .md.disabled and its MEMORY.md line commented out, so Claude Code loads neither.
        DisabledMemoryEntries = projects.Sum(p => p.Entries.Count(e => e.Disabled));
        ActiveMemoryEntries = TotalMemoryEntries - DisabledMemoryEntries;

        var skills = SkillLoader.LoadAll();
        TotalSkills = skills.Count;
        DisabledSkills = skills.Count(s => s.Disabled);
        ActiveSkills = TotalSkills - DisabledSkills;

        var agents = AgentLoader.LoadAll();
        TotalAgents = agents.Count;
        DisabledAgents = agents.Count(a => a.Disabled);
        ActiveAgents = TotalAgents - DisabledAgents;

        var workflows = WorkflowLoader.LoadAll();
        TotalWorkflows = workflows.Count;
        DisabledWorkflows = workflows.Count(w => w.Disabled);
        ActiveWorkflows = TotalWorkflows - DisabledWorkflows;

        var mcp = McpConfigService.Scan();
        TotalMcp = mcp.TotalCount;
        OnlineMcp = mcp.EnabledCount;
        AuthRequiredMcp = mcp.NeedsAuthCount;
        DisabledMcp = TotalMcp - OnlineMcp - AuthRequiredMcp;

        var snaps = _main.Snapshots.ListSnapshots(500);
        SnapshotCount = snaps.Count;
        LastSnapshot = snaps.FirstOrDefault() is { } s ? $"{s.ShortSha} · {s.CreatedAt:yyyy-MM-dd HH:mm}" : "(none)";

        var diags = Linter.Run();
        DiagErrors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
        DiagWarnings = diags.Count(d => d.Severity == DiagnosticSeverity.Warning);
        DiagInfo = diags.Count(d => d.Severity == DiagnosticSeverity.Info);

        TopDiagnostics.Clear();
        foreach (var d in diags.OrderByDescending(d => d.Severity).Take(8)) TopDiagnostics.Add(d);

        SettingsExists = File.Exists(ClaudePaths.SettingsJson) ? "OK" : "missing";
        LocalSettingsExists = File.Exists(ClaudePaths.LocalSettingsJson) ? "OK" : "missing";
        if (File.Exists(ClaudePaths.GlobalClaudeMd))
        {
            try
            {
                var text = File.ReadAllText(ClaudePaths.GlobalClaudeMd);
                ClaudeMdLines = text.Replace("\r\n", "\n").Split('\n').Length;
                ClaudeMdStatus = "OK";
            }
            catch { ClaudeMdStatus = "read err"; ClaudeMdLines = 0; }
        }
        else { ClaudeMdStatus = "missing"; ClaudeMdLines = 0; }

        // Session storage scan
        var storage = SessionStorageService.Scan();
        SessionsTotalBytes = storage.TotalSize;
        SessionsTotalSize = SessionStorageService.FormatBytes(storage.TotalSize);
        SessionsFileCount = storage.TotalSessionFiles;
        SessionsOldest = storage.OldestSession is { } o ? o.ToString("yyyy-MM-dd") : "—";
        SessionsNewest = storage.NewestSession is { } n ? n.ToString("yyyy-MM-dd HH:mm") : "—";

        // Read cleanupPeriodDays from settings.json
        try
        {
            var bundle = SettingsService.Load(ClaudePaths.SettingsJson);
            if (bundle.Root["cleanupPeriodDays"] is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<int>(out var days))
            {
                if (days >= 9999)
                {
                    CleanupPeriod = "FOREVER";
                    CleanupPeriodInfinite = true;
                }
                else
                {
                    CleanupPeriod = $"{days} days";
                    CleanupPeriodInfinite = false;
                }
            }
            else
            {
                CleanupPeriod = "30 days (default)";
                CleanupPeriodInfinite = false;
            }
        }
        catch { CleanupPeriod = "?"; CleanupPeriodInfinite = false; }

        // Top project
        var top = storage.Projects.FirstOrDefault();
        if (top is not null && storage.TotalSize > 0)
        {
            SessionsTopProject = top.Slug;
            SessionsTopProjectSize = SessionStorageService.FormatBytes(top.TotalSize);
            SessionsTopProjectPct = 100.0 * top.TotalSize / storage.TotalSize;
        }
        else
        {
            SessionsTopProject = "—";
            SessionsTopProjectSize = "—";
            SessionsTopProjectPct = 0;
        }

        // Fill top 5 projects list for dashboard preview
        TopProjects.Clear();
        long maxSize = storage.Projects.Count > 0 ? storage.Projects.Max(p => p.TotalSize) : 0;
        foreach (var p in storage.Projects.Take(5))
        {
            TopProjects.Add(new SessionProjectRow
            {
                Slug = p.Slug,
                SessionCount = p.SessionCount,
                SizeText = SessionStorageService.FormatBytes(p.TotalSize),
                Bytes = p.TotalSize,
                BarPct = maxSize > 0 ? 100.0 * p.TotalSize / maxSize : 0
            });
        }
        // Bar is a relative comparison across projects — meaningless with only 1
        ShowRelativeBars = storage.Projects.Count >= 2;

        // Warning level: >2GB = DANGER, >500MB = WARN, else OK
        SessionsWarningLevel = storage.TotalSize switch
        {
            > 2L * 1024 * 1024 * 1024 => "DANGER",
            > 500L * 1024 * 1024 => "WARN",
            _ => "OK"
        };

        Status = $"refreshed · {projects.Count} projects · {skills.Count} skills · {agents.Count} agents · {workflows.Count} workflows · {SessionsTotalSize} sessions";
    }

    /// <summary>
    /// Navigate to a module by its Key. Wired to the dashboard stat cards so users can
    /// click any tile to jump into that module.
    /// </summary>
    [RelayCommand]
    private void NavigateTo(string? moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey)) return;
        var target = _main.Modules.FirstOrDefault(m => m.Key == moduleKey);
        if (target is null) return;
        _main.NavigateCommand.Execute(target);
    }
}

public sealed class SessionProjectRow
{
    public string Slug { get; set; } = "";
    public int SessionCount { get; set; }
    public string SizeText { get; set; } = "";
    public long Bytes { get; set; }
    public double BarPct { get; set; }
}
