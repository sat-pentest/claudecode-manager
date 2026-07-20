using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class DiagnosticsViewModel : ModuleBase
{
    public override string Key => "DIAG";
    public override string Title => "DIAGNOSTICS";
    public override string Glyph => "M12,3 L22,21 H2 Z M12,10 V15 M12,17.5 V18.5";

    public ObservableCollection<Diagnostic> All { get; } = new();
    public ObservableCollection<Diagnostic> Filtered { get; } = new();

    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private string _filterText = "";

    public DiagnosticsViewModel(MainViewModel main) { }

    public override void OnActivated() => Run();

    partial void OnShowErrorChanged(bool value) => ApplyFilter();
    partial void OnShowWarningChanged(bool value) => ApplyFilter();
    partial void OnShowInfoChanged(bool value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void Run()
    {
        All.Clear();
        foreach (var d in Linter.Run()) All.Add(d);
        ApplyFilter();
        var e = All.Count(d => d.Severity == DiagnosticSeverity.Error);
        var w = All.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var i = All.Count(d => d.Severity == DiagnosticSeverity.Info);
        Status = $"{e} errors · {w} warnings · {i} info";
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var d in All)
        {
            if (d.Severity == DiagnosticSeverity.Error && !ShowError) continue;
            if (d.Severity == DiagnosticSeverity.Warning && !ShowWarning) continue;
            if (d.Severity == DiagnosticSeverity.Info && !ShowInfo) continue;
            if (!string.IsNullOrEmpty(FilterText) && d.Message.IndexOf(FilterText, System.StringComparison.OrdinalIgnoreCase) < 0
                && d.Category.IndexOf(FilterText, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            Filtered.Add(d);
        }
    }

    [RelayCommand]
    private void OpenFile(Diagnostic? d)
    {
        if (d?.File is null || !File.Exists(d.File)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{d.File}\"") { UseShellExecute = true }); } catch { }
    }
}
