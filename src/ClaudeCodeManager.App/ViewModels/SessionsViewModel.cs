using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeManager.App.Views;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class SessionsProjectVm : ObservableObject
{
    [ObservableProperty] private string _slug = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private string _oldest = "—";
    [ObservableProperty] private string _newest = "—";
    [ObservableProperty] private double _barPct;
    [ObservableProperty] private long _memoryDirBytes;
    [ObservableProperty] private string _memoryDirText = "";
    public ObservableCollection<SessionsFileVm> Sessions { get; } = new();
}

public partial class SessionsFileVm : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _displayLabel = "";
    [ObservableProperty] private string _aiTitle = "";
    [ObservableProperty] private bool _hasAiTitle;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private DateTime _modifiedAt;
    [ObservableProperty] private bool _isJsonl;
    [ObservableProperty] private int _ageDays;

    /// <summary>Name of the VS Code group this session was filed under, empty when ungrouped.</summary>
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private bool _hasGroup;
}

public partial class SessionsViewModel : ModuleBase
{
    public override string Key => "SESSIONS";
    public override string Title => "SESSIONS";
    // Stacked disks + trash icon
    public override string Glyph => "M4,5 C4,4 8,3 12,3 C16,3 20,4 20,5 C20,6 16,7 12,7 C8,7 4,6 4,5 Z M4,5 V10 C4,11 8,12 12,12 C16,12 20,11 20,10 V5 M4,10 V15 C4,16 8,17 12,17 C16,17 20,16 20,15 V10 M4,15 V19 C4,20 8,21 12,21 C16,21 20,20 20,19 V15";

    private readonly MainViewModel _main;

    [ObservableProperty] private string _totalSize = "0 B";
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private int _totalSessionFiles;
    [ObservableProperty] private int _totalProjects;
    [ObservableProperty] private string _oldestSession = "—";
    [ObservableProperty] private string _newestSession = "—";
    [ObservableProperty] private string _cleanupPeriodText = "—";
    [ObservableProperty] private string _retentionValue = "—";
    [ObservableProperty] private string _retentionLabel = "";
    [ObservableProperty] private bool _retentionInfinite;
    [ObservableProperty] private int _pruneDays = 30;
    [ObservableProperty] private SessionsProjectVm? _selectedProject;
    [ObservableProperty] private SessionsFileVm? _selectedSession;
    [ObservableProperty] private string _warningLevel = "OK";
    [ObservableProperty] private string _diskFreeText = "";
    [ObservableProperty] private string _diskDriveLabel = "";
    [ObservableProperty] private bool _showRelativeBars;

    public ObservableCollection<SessionsProjectVm> Projects { get; } = new();

    /// <summary>The same sessions bucketed by the groups made in the VS Code extension.</summary>
    public ObservableCollection<SessionsProjectVm> Groups { get; } = new();

    /// <summary>What the left list actually shows. Both modes produce the same row shape, so the
    /// detail pane on the right needs no knowledge of which one is active.</summary>
    public ObservableCollection<SessionsProjectVm> Buckets { get; } = new();

    [ObservableProperty] private bool _groupMode;
    [ObservableProperty] private string _bucketHeader = "◢ PROJECTS BY SIZE";
    [ObservableProperty] private string _bucketSubhead = "sorted largest first";
    /// <summary>Set when the VS Code groups could not be read, shown in place of an empty list.</summary>
    [ObservableProperty] private string _groupProblem = "";
    [ObservableProperty] private bool _hasGroupProblem;
    /// <summary>Real groups only — the catch-all bucket is counted separately so the subhead does
    /// not claim a group the operator never made.</summary>
    private int _realGroupCount;
    private int _ungroupedCount;

    /// <summary>Mirror of <see cref="GroupMode"/> so the two radio buttons can each bind to a
    /// property instead of pulling in a converter for one of them.</summary>
    public bool ProjectMode
    {
        get => !GroupMode;
        set { if (value) GroupMode = false; }
    }

    partial void OnGroupModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ProjectMode));
        ApplyBuckets();
        PruneProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProjectChanged(SessionsProjectVm? value)
        => PruneProjectCommand.NotifyCanExecuteChanged();

    private void ApplyBuckets()
    {
        var keep = SelectedProject?.Slug;
        Buckets.Clear();
        foreach (var b in (GroupMode ? Groups : Projects)) Buckets.Add(b);

        BucketHeader = GroupMode ? "◢ VSCODE GROUPS" : "◢ PROJECTS BY SIZE";
        BucketSubhead = GroupMode
            ? (HasGroupProblem ? GroupProblem
               : _ungroupedCount > 0
                   ? $"VS Code 그룹 {_realGroupCount}개 · 미분류 {_ungroupedCount}개"
                   : $"VS Code 그룹 {_realGroupCount}개")
            : "sorted largest first";

        SelectedProject = Buckets.FirstOrDefault(b => b.Slug == keep) ?? Buckets.FirstOrDefault();
        ShowRelativeBars = Buckets.Count >= 2;
    }

    public SessionsViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var previouslySelectedSlug = SelectedProject?.Slug;

        var summary = SessionStorageService.Scan();
        TotalBytes = summary.TotalSize;
        TotalSize = SessionStorageService.FormatBytes(summary.TotalSize);
        TotalSessionFiles = summary.TotalSessionFiles;
        TotalProjects = summary.TotalProjects;
        OldestSession = summary.OldestSession is { } o ? o.ToString("yyyy-MM-dd") : "—";
        NewestSession = summary.NewestSession is { } n ? n.ToString("yyyy-MM-dd HH:mm") : "—";

        try
        {
            var bundle = SettingsService.Load(ClaudePaths.SettingsJson);
            if (bundle.Root["cleanupPeriodDays"] is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<int>(out var days))
            {
                if (days >= 9999)
                {
                    CleanupPeriodText = "∞ (kept forever)";
                    RetentionValue = "∞";
                    RetentionLabel = "kept forever";
                    RetentionInfinite = true;
                }
                else
                {
                    CleanupPeriodText = $"{days} days";
                    RetentionValue = days.ToString();
                    RetentionLabel = "days";
                    RetentionInfinite = false;
                }
            }
            else
            {
                CleanupPeriodText = "30 days (default)";
                RetentionValue = "30";
                RetentionLabel = "days (default)";
                RetentionInfinite = false;
            }
        }
        catch
        {
            CleanupPeriodText = "?";
            RetentionValue = "?";
            RetentionLabel = "";
            RetentionInfinite = false;
        }

        WarningLevel = summary.TotalSize switch
        {
            > 2L * 1024 * 1024 * 1024 => "DANGER",
            > 500L * 1024 * 1024 => "WARN",
            _ => "OK"
        };

        Projects.Clear();
        long maxSize = summary.Projects.Count > 0 ? summary.Projects.Max(p => p.TotalSize) : 0;
        foreach (var p in summary.Projects)
        {
            var vm = new SessionsProjectVm
            {
                Slug = p.Slug,
                Path = p.Path,
                Bytes = p.TotalSize,
                SizeText = SessionStorageService.FormatBytes(p.TotalSize),
                SessionCount = p.SessionCount,
                Oldest = p.OldestSession is { } po ? po.ToString("yyyy-MM-dd") : "—",
                Newest = p.NewestSession is { } pn ? pn.ToString("yyyy-MM-dd HH:mm") : "—",
                BarPct = maxSize > 0 ? 100.0 * p.TotalSize / maxSize : 0,
                MemoryDirBytes = p.MemoryDirSize,
                MemoryDirText = p.MemoryDirSize > 0 ? SessionStorageService.FormatBytes(p.MemoryDirSize) : ""
            };
            foreach (var s in p.Sessions)
            {
                vm.Sessions.Add(new SessionsFileVm
                {
                    Path = s.Path,
                    FileName = s.FileName,
                    DisplayLabel = string.IsNullOrWhiteSpace(s.DisplayLabel) ? s.FileName : s.DisplayLabel,
                    AiTitle = s.AiTitle ?? "",
                    HasAiTitle = !string.IsNullOrWhiteSpace(s.AiTitle),
                    Bytes = s.Size,
                    SizeText = SessionStorageService.FormatBytes(s.Size),
                    ModifiedAt = s.ModifiedAt,
                    IsJsonl = s.IsJsonl,
                    AgeDays = Math.Max(0, (int)(DateTime.Now - s.ModifiedAt).TotalDays)
                });
            }
            Projects.Add(vm);
        }

        // Disk free space for the drive holding ~/.claude/projects/
        try
        {
            var driveRoot = Path.GetPathRoot(ClaudePaths.ProjectsRoot);
            if (!string.IsNullOrEmpty(driveRoot))
            {
                var di = new DriveInfo(driveRoot);
                if (di.IsReady)
                {
                    DiskDriveLabel = di.Name.TrimEnd('\\');
                    DiskFreeText = $"{SessionStorageService.FormatBytes(di.AvailableFreeSpace)} free / {SessionStorageService.FormatBytes(di.TotalSize)}";
                }
            }
        }
        catch { DiskFreeText = "—"; }

        BuildGroups();
        ApplyBuckets();

        Status = $"{TotalProjects} projects · {TotalSessionFiles} sessions · {TotalSize}";
    }

    /// <summary>
    /// Fold the flat session list into the operator's VS Code groups.
    ///
    /// A group can span projects, so the sessions are indexed by their UUID first and then
    /// gathered — matching by file name, which is what the extension stores. Sessions in no group
    /// are collected into one bucket rather than dropped, otherwise switching to this view would
    /// silently hide most of the list.
    /// </summary>
    private void BuildGroups()
    {
        Groups.Clear();
        GroupProblem = "";
        HasGroupProblem = false;
        _realGroupCount = 0;
        _ungroupedCount = 0;

        var layout = VsCodeSessionGroupService.Load();
        if (layout.Problem is { Length: > 0 } problem)
        {
            GroupProblem = problem;
            HasGroupProblem = true;
            return;
        }

        var byId = new Dictionary<string, SessionsFileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Projects)
            foreach (var s in p.Sessions)
            {
                // Cleared first: a session moved out of a group in VS Code must lose its badge.
                s.GroupName = "";
                s.HasGroup = false;
                byId[Path.GetFileNameWithoutExtension(s.FileName)] = s;
            }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var built = new List<SessionsProjectVm>();

        foreach (var g in layout.Groups)
        {
            var vm = new SessionsProjectVm { Slug = g.Name, Path = "" };
            foreach (var id in g.SessionIds)
            {
                if (!byId.TryGetValue(id, out var s)) continue;   // grouped elsewhere, or pruned
                s.GroupName = g.Name;
                s.HasGroup = true;
                vm.Sessions.Add(s);
                claimed.Add(id);
            }
            if (vm.Sessions.Count == 0) continue;
            built.Add(Summarise(vm));
        }

        var loose = new SessionsProjectVm { Slug = "(그룹 없음)", Path = "" };
        foreach (var kv in byId)
            if (!claimed.Contains(kv.Key)) loose.Sessions.Add(kv.Value);
        if (loose.Sessions.Count > 0) built.Add(Summarise(loose));

        _ungroupedCount = loose.Sessions.Count;
        _realGroupCount = built.Count - (loose.Sessions.Count > 0 ? 1 : 0);

        var max = built.Count > 0 ? built.Max(b => b.Bytes) : 0;
        foreach (var b in built.OrderByDescending(b => b.Bytes))
        {
            b.BarPct = max > 0 ? 100.0 * b.Bytes / max : 0;
            Groups.Add(b);
        }
    }

    private static SessionsProjectVm Summarise(SessionsProjectVm vm)
    {
        var ordered = vm.Sessions.OrderByDescending(s => s.ModifiedAt).ToList();
        vm.Sessions.Clear();
        foreach (var s in ordered) vm.Sessions.Add(s);
        vm.Bytes = vm.Sessions.Sum(s => s.Bytes);
        vm.SizeText = SessionStorageService.FormatBytes(vm.Bytes);
        vm.SessionCount = vm.Sessions.Count;
        vm.Oldest = vm.Sessions.Count > 0 ? vm.Sessions.Min(s => s.ModifiedAt).ToString("yyyy-MM-dd") : "—";
        vm.Newest = vm.Sessions.Count > 0 ? vm.Sessions.Max(s => s.ModifiedAt).ToString("yyyy-MM-dd HH:mm") : "—";
        return vm;
    }

    [RelayCommand]
    private void OpenProjectInExplorer()
    {
        // A group bucket has no folder of its own — its sessions live across several projects.
        if (SelectedProject is null || string.IsNullOrEmpty(SelectedProject.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedProject.Path}\"") { UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }

    [RelayCommand]
    private void OpenProjectsRoot()
    {
        try
        {
            if (!Directory.Exists(ClaudePaths.ProjectsRoot)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ClaudePaths.ProjectsRoot}\"") { UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }

    [RelayCommand]
    private void DeleteSelectedSessions(IList? selectedItems)
    {
        // Prefer the multi-selection passed from the ListView; fall back to the single Selected
        var files = (selectedItems?.OfType<SessionsFileVm>() ?? Enumerable.Empty<SessionsFileVm>()).ToList();
        if (files.Count == 0 && SelectedSession is not null) files.Add(SelectedSession);
        if (files.Count == 0 || SelectedProject is null) return;

        long totalBytes = files.Sum(f => f.Bytes);
        string msg;
        if (files.Count == 1)
        {
            var s = files[0];
            var label = string.IsNullOrWhiteSpace(s.AiTitle) ? s.FileName : $"{s.AiTitle}\n({s.FileName})";
            msg = $"{label}\n크기: {s.SizeText}\n\n이 대화 로그를 영구 삭제합니다.\n스냅샷은 이 파일을 백업하지 않으므로 복구할 수 없습니다.";
        }
        else
        {
            var preview = string.Join("\n", files.Take(5).Select(f =>
                $"  · {(string.IsNullOrWhiteSpace(f.AiTitle) ? f.FileName : f.AiTitle)}  ({f.SizeText})"));
            var more = files.Count > 5 ? $"\n  … 외 {files.Count - 5}개" : "";
            msg = $"{files.Count}개 세션 · 총 {SessionStorageService.FormatBytes(totalBytes)}\n\n{preview}{more}\n\n선택된 대화 로그들을 영구 삭제합니다.\n스냅샷은 이 파일들을 백업하지 않으므로 복구할 수 없습니다.";
        }

        var confirm = ConfirmDialog.Show(null,
            files.Count > 1 ? $"Delete {files.Count} session files" : "Delete session file",
            msg, ConfirmKind.Danger);
        if (!confirm) return;

        var r = SessionStorageService.DeleteSessionFiles(files.Select(f => f.Path));
        Status = r.Failed == 0
            ? $"휴지통으로 {r.Deleted}개 이동 · {SessionStorageService.FormatBytes(r.Freed)} 확보"
            : $"휴지통으로 {r.Deleted}개 이동, {r.Failed}개 실패 · {SessionStorageService.FormatBytes(r.Freed)} 확보";
        Refresh();
    }

    /// <summary>
    /// Pruning by age needs one folder to walk, and a group bucket has none — its sessions are
    /// spread across projects. Rather than let the button open a danger dialog and then quietly
    /// delete nothing, it is simply unavailable while the list is bucketed by group.
    /// </summary>
    private bool CanPruneProject()
        => !GroupMode && SelectedProject is not null && !string.IsNullOrEmpty(SelectedProject.Path);

    [RelayCommand(CanExecute = nameof(CanPruneProject))]
    private void PruneProject()
    {
        if (SelectedProject is null || string.IsNullOrEmpty(SelectedProject.Path)) return;
        var days = PruneDays;
        if (days <= 0) { Status = "prune days must be > 0"; return; }
        var cutoff = DateTime.Now.AddDays(-days);
        var confirm = ConfirmDialog.Show(null,
            "Prune old sessions",
            $"'{SelectedProject.Slug}' 프로젝트에서 {days}일 이상 지난 세션(.jsonl) 파일을 휴지통으로 보냅니다.\n기준일: {cutoff:yyyy-MM-dd HH:mm}\n\n계속할까요?",
            ConfirmKind.Danger);
        if (!confirm) return;

        var r = SessionStorageService.DeleteOlderThan(SelectedProject.Path, cutoff);
        Status = PruneStatus(r);
        Refresh();
    }

    /// <summary>Failures are always named. A prune that silently skipped locked files reported
    /// space it never freed.</summary>
    private static string PruneStatus(RecycleBin.Outcome r)
        => r.Failed == 0
            ? $"휴지통으로 {r.Deleted}개 이동 · {SessionStorageService.FormatBytes(r.Freed)} 확보"
            : $"휴지통으로 {r.Deleted}개 이동, {r.Failed}개 실패(사용 중이거나 접근 불가) · {SessionStorageService.FormatBytes(r.Freed)} 확보";

    [RelayCommand]
    private void PruneAll()
    {
        var days = PruneDays;
        if (days <= 0) { Status = "prune days must be > 0"; return; }
        var cutoff = DateTime.Now.AddDays(-days);
        var confirm = ConfirmDialog.Show(null,
            "Prune ALL projects",
            $"모든 프로젝트에서 {days}일 이상 지난 세션(.jsonl) 파일을 휴지통으로 보냅니다.\n기준일: {cutoff:yyyy-MM-dd HH:mm}\n\n이 작업은 되돌릴 수 없습니다.",
            ConfirmKind.Danger);
        if (!confirm) return;

        var r = SessionStorageService.DeleteAllOlderThan(cutoff);
        Status = PruneStatus(r);
        Refresh();
    }
}
