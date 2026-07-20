using System;
using System.Collections;
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

        // Restore selection
        if (previouslySelectedSlug is not null)
            SelectedProject = Projects.FirstOrDefault(p => p.Slug == previouslySelectedSlug) ?? Projects.FirstOrDefault();
        else
            SelectedProject = Projects.FirstOrDefault();

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

        // Show relative comparison bars only when there's something to compare (2+ projects)
        ShowRelativeBars = Projects.Count >= 2;

        Status = $"{TotalProjects} projects · {TotalSessionFiles} sessions · {TotalSize}";
    }

    [RelayCommand]
    private void OpenProjectInExplorer()
    {
        if (SelectedProject is null) return;
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

        int okCount = 0;
        int failCount = 0;
        long freed = 0;
        foreach (var f in files)
        {
            var b = SessionStorageService.DeleteSessionFile(f.Path);
            if (b >= 0) { okCount++; freed += b; }
            else failCount++;
        }

        Status = failCount == 0
            ? $"deleted {okCount} file(s) · freed {SessionStorageService.FormatBytes(freed)}"
            : $"deleted {okCount}, failed {failCount} · freed {SessionStorageService.FormatBytes(freed)}";
        Refresh();
    }

    [RelayCommand]
    private void PruneProject()
    {
        if (SelectedProject is null) return;
        var days = PruneDays;
        if (days <= 0) { Status = "prune days must be > 0"; return; }
        var cutoff = DateTime.Now.AddDays(-days);
        var confirm = ConfirmDialog.Show(null,
            "Prune old sessions",
            $"'{SelectedProject.Slug}' 프로젝트에서 {days}일 이상 지난 세션(.jsonl) 파일을 모두 삭제합니다.\n기준일: {cutoff:yyyy-MM-dd HH:mm}\n\n계속할까요?",
            ConfirmKind.Danger);
        if (!confirm) return;

        var (count, freed) = SessionStorageService.DeleteOlderThan(SelectedProject.Path, cutoff);
        Status = $"pruned {count} files · freed {SessionStorageService.FormatBytes(freed)}";
        Refresh();
    }

    [RelayCommand]
    private void PruneAll()
    {
        var days = PruneDays;
        if (days <= 0) { Status = "prune days must be > 0"; return; }
        var cutoff = DateTime.Now.AddDays(-days);
        var confirm = ConfirmDialog.Show(null,
            "Prune ALL projects",
            $"모든 프로젝트에서 {days}일 이상 지난 세션(.jsonl) 파일을 삭제합니다.\n기준일: {cutoff:yyyy-MM-dd HH:mm}\n\n이 작업은 되돌릴 수 없습니다.",
            ConfirmKind.Danger);
        if (!confirm) return;

        var (count, freed) = SessionStorageService.DeleteAllOlderThan(cutoff);
        Status = $"pruned {count} files · freed {SessionStorageService.FormatBytes(freed)}";
        Refresh();
    }
}
