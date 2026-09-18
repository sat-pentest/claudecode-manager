using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeManager.App.Views;
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

    /// <summary>0 = LINT, 1 = HOST SECURITY.</summary>
    [ObservableProperty] private int _activeTabIndex;

    private readonly MainViewModel _main;

    public DiagnosticsViewModel(MainViewModel main) { _main = main; }

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

    /// <summary>
    /// Double-click target: jump to the module that owns this file and focus the entry
    /// (MEMORY / SKILLS / WORKFLOWS / CLAUDE.MD / SETTINGS). Falls back to Explorer
    /// for paths no module claims — MainViewModel.NavigateToFile handles that branch.
    /// </summary>
    [RelayCommand]
    private void RevealInModule(Diagnostic? d)
    {
        if (d?.File is null) return;
        _main.NavigateToFile(d.File);
    }

    // ─── HOST SECURITY ───────────────────────────────────────────────────
    //
    // The lint tab answers "is my config well-formed". This one answers "what can reach this
    // workstation, and how far does the agent reach from it" — machine controls, the agent's own
    // permission surface, and whether the engagement safety layer is actually holding.

    /// <summary>Every check from the last scan, accepted ones included. The filtered collection is
    /// rebuilt from this rather than rescanned, so accepting a risk costs no probe.</summary>
    private List<SecurityCheck> _allSecurity = new();

    public ObservableCollection<SecurityCheck> SecurityFiltered { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _securityStatus = "not scanned yet";

    /// <summary>Passing and accepted checks are hidden by default: the list is for what is still
    /// outstanding. Toggle to audit the full set.</summary>
    [ObservableProperty] private bool _showResolvedChecks;

    partial void OnShowResolvedChecksChanged(bool value) => ApplySecurityFilter();

    public bool HasSecurityResults => _allSecurity.Count > 0;

    [RelayCommand]
    private async Task RunSecurityScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        SecurityStatus = "scanning…";
        try
        {
            // The probe spawns PowerShell and walks the memory tree; neither belongs on the
            // dispatcher thread.
            var results = await Task.Run(HostSecurityScanner.Run);
            _allSecurity = results;
            ApplySecurityFilter();
        }
        catch (System.Exception ex)
        {
            SecurityStatus = "scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplySecurityFilter()
    {
        SecurityFiltered.Clear();

        // Outstanding first, worst first, so the top of the list is the work queue.
        var ordered = _allSecurity
            .OrderByDescending(c => c.IsOutstanding)
            .ThenByDescending(c => (int)c.Severity)
            .ThenBy(c => c.Category, System.StringComparer.Ordinal)
            .ThenBy(c => c.Name, System.StringComparer.Ordinal);

        foreach (var c in ordered)
        {
            if (!ShowResolvedChecks && !c.IsOutstanding) continue;
            SecurityFiltered.Add(c);
        }

        var fail = _allSecurity.Count(c => c.Status == CheckStatus.Fail && !c.AcceptedRisk);
        var warn = _allSecurity.Count(c => c.Status == CheckStatus.Warn && !c.AcceptedRisk);
        var accepted = _allSecurity.Count(c => c.AcceptedRisk);
        var skipped = _allSecurity.Count(c => c.Status == CheckStatus.Skip && !c.AcceptedRisk);

        SecurityStatus =
            $"{_allSecurity.Count} checks · {fail} fail · {warn} warn · {accepted} accepted · {skipped} not determined";

        OnPropertyChanged(nameof(HasSecurityResults));
    }

    [RelayCommand]
    private void CopyFix(SecurityCheck? c)
    {
        if (c is null || !c.HasFix) return;
        try
        {
            System.Windows.Clipboard.SetText(c.Fix);
            SecurityStatus = $"fix copied · {c.Id}";
        }
        catch (System.Exception ex) { SecurityStatus = "copy failed: " + ex.Message; }
    }

    /// <summary>
    /// Record that this finding is the way it is on purpose. Requires a reason — an accepted risk
    /// with no rationale is indistinguishable from one that was clicked away, and the whole point
    /// of the mark is that the next scan can tell the difference.
    /// </summary>
    [RelayCommand]
    private void AcceptRisk(SecurityCheck? c)
    {
        if (c is null || c.AcceptedRisk) return;

        var (ok, note) = InputDialog.Show(
            System.Windows.Application.Current?.MainWindow,
            "ACCEPT RISK",
            $"{c.Name}\n{c.Detail}\n\nWhy is this state intended?",
            "",
            v => string.IsNullOrWhiteSpace(v) ? "A reason is required." : null);

        if (!ok) return;

        AcceptedRiskStore.Accept(c.Id, note);
        c.AcceptedRisk = true;
        c.AcceptedNote = $"{note.Trim()}  (accepted {System.DateTime.Now:yyyy-MM-dd})";
        ApplySecurityFilter();
    }

    [RelayCommand]
    private void UnacceptRisk(SecurityCheck? c)
    {
        if (c is null || !c.AcceptedRisk) return;
        AcceptedRiskStore.Unaccept(c.Id);
        c.AcceptedRisk = false;
        c.AcceptedNote = "";
        ApplySecurityFilter();
    }
}
