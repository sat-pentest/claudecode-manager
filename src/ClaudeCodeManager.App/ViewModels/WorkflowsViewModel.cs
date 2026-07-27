using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ClaudeCodeManager.App.Views;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class WorkflowsViewModel : ModuleBase
{
    public override string Key => "WFLW";
    public override string Title => "WORKFLOWS";
    // Pipeline glyph — two nodes connected, with a branch
    public override string Glyph => "M4,12 H10 M14,12 H20 M10,12 L14,8 M10,12 L14,16";

    private readonly MainViewModel _main;

    public ObservableCollection<WorkflowEntry> Workflows { get; } = new();
    [ObservableProperty] private WorkflowEntry? _selected;
    [ObservableProperty] private bool _isDetailCollapsed;
    [ObservableProperty] private bool _isPipelineCollapsed;
    [ObservableProperty] private bool _isBodyCollapsed = true;

    /// <summary>PIPELINE ScaleTransform factor. Driven by Ctrl+MouseWheel in the view.</summary>
    [ObservableProperty] private double _pipelineZoom = 1.0;

    /// <summary>Displayed zoom percent for the header badge (recomputes when PipelineZoom changes).</summary>
    public string PipelineZoomText => $"{PipelineZoom * 100:0}%";
    partial void OnPipelineZoomChanged(double value) => OnPropertyChanged(nameof(PipelineZoomText));

    [RelayCommand] private void ResetPipelineZoom() => PipelineZoom = 1.0;

    // LIVE tab state
    public ObservableCollection<WorkflowRunEntry> ActiveRuns { get; } = new();
    [ObservableProperty] private WorkflowRunEntry? _selectedRun;
    [ObservableProperty] private bool _includeIdleRuns;
    [ObservableProperty] private string _liveStatus = "";
    [ObservableProperty] private int _activeTabIndex; // 0 = INSTALLED, 1 = LIVE
    [ObservableProperty] private bool _isLiveHeaderCollapsed;
    [ObservableProperty] private bool _isLiveResultsCollapsed;
    [ObservableProperty] private bool _hasActiveRun;

    [RelayCommand] private void ToggleLiveHeader() => IsLiveHeaderCollapsed = !IsLiveHeaderCollapsed;
    [RelayCommand] private void ToggleLiveResults() => IsLiveResultsCollapsed = !IsLiveResultsCollapsed;

    private readonly DispatcherTimer _liveTimer;

    public EngagementSafetyViewModel Safety { get; } = new();

    public WorkflowsViewModel(MainViewModel main)
    {
        _main = main;
        _liveTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += (_, _) => RefreshLiveRuns();
    }

    partial void OnActiveTabIndexChanged(int value)
    {
        // Timer is kept running for the whole module lifetime (see OnActivated) so the
        // LIVE tab's ACTIVE indicator can appear even while the user is on INSTALLED.
        // On explicit switch to LIVE, force an immediate refresh so the detail view
        // isn't waiting for the next tick.
        if (value == 1) RefreshLiveRuns();
    }

    partial void OnIncludeIdleRunsChanged(bool value) => RefreshLiveRuns();

    private void RefreshLiveRuns()
    {
        var currentId = SelectedRun?.RunId;

        // Preserve expanded state on SelectedRun.RecentResults across refreshes.
        // Without this, the 5s auto-refresh creates new WorkflowResultSummary instances
        // (IsExpanded defaults to false) → the user's clicked-open card silently collapses.
        var expandedAgentIds = SelectedRun is null
            ? new System.Collections.Generic.HashSet<string>()
            : new System.Collections.Generic.HashSet<string>(
                SelectedRun.RecentResults.Where(r => r.IsExpanded).Select(r => r.AgentIdFull));

        var runs = WorkflowLiveService.Scan(IncludeIdleRuns);
        ActiveRuns.Clear();
        foreach (var r in runs) ActiveRuns.Add(r);
        SelectedRun = currentId is null
            ? ActiveRuns.FirstOrDefault()
            : ActiveRuns.FirstOrDefault(r => r.RunId == currentId) ?? ActiveRuns.FirstOrDefault();

        // Restore expanded state on the fresh RecentResults instances.
        if (SelectedRun is not null && expandedAgentIds.Count > 0)
        {
            foreach (var res in SelectedRun.RecentResults)
            {
                if (!string.IsNullOrEmpty(res.AgentIdFull) && expandedAgentIds.Contains(res.AgentIdFull))
                    res.IsExpanded = true;
            }
        }

        var active = ActiveRuns.Count(r => r.Status == "ACTIVE" || r.Status == "RECENT");
        LiveStatus = $"{ActiveRuns.Count} runs · {active} active · refresh 5s";
        HasActiveRun = ActiveRuns.Any(r => r.Status == "ACTIVE");
    }

    [RelayCommand]
    private void RefreshLive() => RefreshLiveRuns();

    [RelayCommand]
    private void OpenRunFolder(WorkflowRunEntry? r)
    {
        if (r is null || !Directory.Exists(r.DirPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r.DirPath}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void CopyResumeCmd(WorkflowRunEntry? r)
    {
        if (r is null) return;
        // Try to infer script path from workflow name pattern (redteam-triage.mjs, bbp-static-recon.mjs, etc.)
        // Fallback: leave placeholder for user to fill in
        var homeWorkflows = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".claude", "workflows");
        var candidates = System.IO.Directory.Exists(homeWorkflows)
            ? System.IO.Directory.GetFiles(homeWorkflows, "*.mjs")
            : System.Array.Empty<string>();
        var scriptHint = candidates.Length == 1
            ? candidates[0].Replace('\\', '/')
            : "<workflow-script-path>.mjs";

        var cmd = $@"Workflow 도구로 아래 워크플로우 재개해줘 (같은 세션에서만 가능):

- scriptPath: {scriptHint}
- resumeFromRunId: {r.RunId}

동작:
1. TaskStop으로 진행 중이던 기존 run 먼저 종료
2. Workflow({{scriptPath, resumeFromRunId}}) 호출
3. 완료된 agent()는 캐시된 결과 즉시 반환, 미완료/실패한 것부터 재실행

대상: {r.Target}
Session: {r.SessionId}";
        try
        {
            System.Windows.Clipboard.SetText(cmd);
            Status = $"resume cmd copied · {r.ShortId} (paste into Claude session {r.ShortSession})";
        }
        catch (System.Exception ex) { Status = "copy failed: " + ex.Message; }
    }

    [RelayCommand]
    private void CopyRunId(WorkflowRunEntry? r)
    {
        if (r is null) return;
        try
        {
            System.Windows.Clipboard.SetText(r.RunId);
            Status = $"run ID copied · {r.RunId}";
        }
        catch (System.Exception ex) { Status = "copy failed: " + ex.Message; }
    }

    public override void OnDeactivated()
    {
        _liveTimer.Stop();
        Safety.StopPolling();
    }

    public override void OnActivated()
    {
        var curPath = Selected?.FilePath;
        Workflows.Clear();
        foreach (var w in WorkflowLoader.LoadAll()) Workflows.Add(w);
        Selected = curPath is null
            ? Workflows.FirstOrDefault()
            : Workflows.FirstOrDefault(w => w.FilePath == curPath) ?? Workflows.FirstOrDefault();
        var disabled = Workflows.Count(w => w.Disabled);
        Status = $"{Workflows.Count} workflows · {disabled} disabled · {WorkflowLoader.WorkflowsDir}";

        // Kick off live-runs scan immediately so the LIVE tab's ACTIVE indicator
        // pulses even before user clicks the LIVE sub-tab.
        RefreshLiveRuns();
        _liveTimer.Start();

        // Safety layer polling — runs while module is active so tab-header dot
        // reflects armed/kill-switch state even before user opens SAFETY tab.
        Safety.StartPolling();
    }

    [RelayCommand]
    private void ToggleDetail() => IsDetailCollapsed = !IsDetailCollapsed;

    [RelayCommand]
    private void TogglePipeline() => IsPipelineCollapsed = !IsPipelineCollapsed;

    [RelayCommand]
    private void ToggleBody() => IsBodyCollapsed = !IsBodyCollapsed;

    [RelayCommand]
    private void Refresh() => OnActivated();

    /// <summary>Select the workflow that owns the given file path (for HARNESS → WORKFLOWS navigation).</summary>
    public void SelectByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Workflows.Count == 0)
        {
            foreach (var w in WorkflowLoader.LoadAll()) Workflows.Add(w);
        }
        var w2 = Workflows.FirstOrDefault(w => string.Equals(w.FilePath, path, System.StringComparison.OrdinalIgnoreCase));
        if (w2 is not null) Selected = w2;
    }

    /// <summary>Enable/disable by renaming with .disabled suffix. Snapshot first for rollback.</summary>
    [RelayCommand]
    private void Toggle(WorkflowEntry? w)
    {
        if (w is null) return;
        _main.Snapshots.CreateSnapshot($"toggle workflow · {w.DisplayName}");
        try
        {
            WorkflowLoader.Toggle(w);
            OnActivated();
            Status = $"{w.DisplayName} → {(w.Disabled ? "disabled" : "enabled")}";
        }
        catch (System.Exception ex)
        {
            Status = "toggle failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenFolder(WorkflowEntry? w)
    {
        var dir = w is null ? WorkflowLoader.WorkflowsDir : Path.GetDirectoryName(w.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            if (w is not null && File.Exists(w.FilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{w.FilePath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void Delete(WorkflowEntry? w)
    {
        if (w is null) return;
        var ok = ConfirmDialog.Show(null,
            "Delete workflow",
            $"{w.FileName} 을(를) 삭제합니다.\n\n스냅샷 자동 생성됨. SNAPSHOTS에서 복구 가능.\n계속?",
            ConfirmKind.Danger);
        if (!ok) return;
        _main.Snapshots.CreateSnapshot($"delete workflow · {w.DisplayName}");
        try
        {
            WorkflowLoader.Delete(w);
            OnActivated();
            Status = $"deleted · {w.FileName}";
        }
        catch (System.Exception ex)
        {
            Status = "delete failed: " + ex.Message;
        }
    }
}
