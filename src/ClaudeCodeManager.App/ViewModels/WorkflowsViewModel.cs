using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
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

    public WorkflowsViewModel(MainViewModel main) { _main = main; }

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
