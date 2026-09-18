using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ClaudeCodeManager.App.Services;
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

    private readonly SmartPollTimer _liveTimer;
    private readonly SmartPollTimer _clockTimer;

    /// <summary>Disk-derived fingerprint of the last scan. Drives the poll backoff and lets an
    /// unchanged scan skip the collection rebuild entirely.</summary>
    private string? _lastLiveSignature;

    public EngagementSafetyViewModel Safety { get; } = new();

    public WorkflowsViewModel(MainViewModel main)
    {
        _main = main;
        // Disk scan. Backs off to 15s while the run set is unchanged and drops to nothing at all
        // while the shell is hidden to tray -- this is the one that touches the filesystem.
        _liveTimer = new SmartPollTimer(System.TimeSpan.FromSeconds(5), RefreshLiveRuns, maxBackoffMultiplier: 3);

        // ACTIVE→RECENT→STALE is a function of elapsed time, so nothing on the entry fires when it
        // changes. Re-raising those members every second keeps the status dot, the left accent and
        // the progress sweep in step with the STATUS text; the 5s scan stays as-is because it is the
        // one that touches disk.
        // Pure UI clock -- no disk, no notion of "new data", so no backoff. It still stands down
        // while hidden: re-raising property notifications for an off-screen window buys nothing.
        _clockTimer = new SmartPollTimer(System.TimeSpan.FromSeconds(1), () =>
        {
            foreach (var r in ActiveRuns) r.NotifyClockDerived();
            var active = ActiveRuns.Count(r => r.Status == "ACTIVE");
            if (HasActiveRun != active > 0) HasActiveRun = active > 0;
            return true;
        }, maxBackoffMultiplier: 1);
    }

    partial void OnActiveTabIndexChanged(int value)
    {
        // Timer is kept running for the whole module lifetime (see OnActivated) so the
        // LIVE tab's ACTIVE indicator can appear even while the user is on INSTALLED.
        // On explicit switch to LIVE, force an immediate refresh so the detail view
        // isn't waiting for the next tick.
        if (value == 1) _liveTimer.PollNow();
    }

    partial void OnIncludeIdleRunsChanged(bool value)
    {
        // The filter changes which runs qualify, so the previous fingerprint no longer describes
        // what should be on screen. Drop it to force a rebuild.
        _lastLiveSignature = null;
        _liveTimer.PollNow();
    }

    /// <returns>True when this scan saw something the previous one did not.</returns>
    private bool RefreshLiveRuns()
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

        // Fingerprint only the fields the scan reads off disk. Status/IdleSeconds are derived from
        // the clock, so including them would report a change on every single tick and defeat the
        // backoff. An identical fingerprint means rebuilding the collection would hand the view an
        // equivalent set of objects, so keep the existing instances -- that alone preserves the
        // selection and the expanded result cards, with no save/restore dance.
        var signature = string.Join("|", runs.Select(r =>
            $"{r.RunId}:{r.AgentsStarted}:{r.AgentsCompleted}:{r.LastActivity.Ticks}:{r.RecentResults.Count}"));
        var changed = signature != _lastLiveSignature;
        _lastLiveSignature = signature;

        if (!changed)
        {
            UpdateLiveStatus();
            return false;
        }

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

        UpdateLiveStatus();
        HasActiveRun = ActiveRuns.Any(r => r.Status == "ACTIVE");
        return true;
    }

    private void UpdateLiveStatus()
    {
        var active = ActiveRuns.Count(r => r.Status == "ACTIVE" || r.Status == "RECENT");
        var cadence = $"{_liveTimer.CurrentInterval.TotalSeconds:0}s";
        if (_liveTimer.IsBackedOff) cadence += " (idle)";
        LiveStatus = $"{ActiveRuns.Count} runs · {active} active · refresh {cadence}";
    }

    [RelayCommand]
    private void RefreshLive() => _liveTimer.PollNow();

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
        _clockTimer.Stop();
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
        _liveTimer.Start();   // no-op while the shell is hidden; resumes on the way back
        _clockTimer.Start();

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
