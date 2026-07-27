using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class EngagementSafetyViewModel : ObservableObject
{
    private readonly EngagementSafetyService _svc = new();
    private readonly DispatcherTimer _timer;

    // ─── State (poll-refreshed) ──────────────────────────────────────────
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private bool _isKillSwitchActive;
    [ObservableProperty] private bool _isMitmproxyRunning;
    [ObservableProperty] private bool _isProxyEnvSet;
    [ObservableProperty] private string _engagementId = "";
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _environment = "";
    [ObservableProperty] private string _enforceMode = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _actionLog = ""; // last operation feedback

    // ─── Scope editor state ──────────────────────────────────────────────
    public ObservableCollection<string> ScopeInclude { get; } = new();
    public ObservableCollection<string> ScopeExclude { get; } = new();
    public ObservableCollection<string> InfraAllow { get; } = new();
    public ObservableCollection<string> ForbiddenReadGlobs { get; } = new();
    [ObservableProperty] private string _newScopeInclude = "";
    [ObservableProperty] private string _newScopeExclude = "";
    [ObservableProperty] private string _newInfraAllow = "";

    [ObservableProperty] private double _perHostRps;
    [ObservableProperty] private int _globalConcurrency;
    [ObservableProperty] private string _cwdRoot = "";

    // ─── Collapse state per section ──────────────────────────────────────
    [ObservableProperty] private bool _isL1Collapsed;
    [ObservableProperty] private bool _isScopeCollapsed;
    [ObservableProperty] private bool _isRecentCollapsed;

    [RelayCommand] private void ToggleL1() => IsL1Collapsed = !IsL1Collapsed;
    [RelayCommand] private void ToggleScope() => IsScopeCollapsed = !IsScopeCollapsed;
    [RelayCommand] private void ToggleRecent() => IsRecentCollapsed = !IsRecentCollapsed;

    // ─── Recent hook blocks ──────────────────────────────────────────────
    public ObservableCollection<HookAuditEntry> RecentBlocks { get; } = new();

    // ─── Presets ─────────────────────────────────────────────────────────
    public ObservableCollection<PresetInfo> Presets { get; } = new();
    [ObservableProperty] private PresetInfo? _selectedPreset;
    [ObservableProperty] private string _activePresetName = "";  // name of preset matching current scope (or empty)
    public bool HasActivePreset => !string.IsNullOrEmpty(ActivePresetName);
    partial void OnActivePresetNameChanged(string value) => OnPropertyChanged(nameof(HasActivePreset));

    // Guards ComboBox → auto-load re-entry from Refresh's internal SelectedPreset assignment.
    private bool _suppressAutoLoad;

    /// <summary>
    /// When user picks a different preset in the ComboBox, auto-apply it to active manifest.
    /// (Removes the need for a separate LOAD button.) Skipped during Refresh() and when the
    /// selection already matches the active preset.
    /// </summary>
    partial void OnSelectedPresetChanged(PresetInfo? value)
    {
        if (_suppressAutoLoad || value is null) return;
        // If already active, no-op — avoids redundant load on user re-select
        if (string.Equals(value.Name, ActivePresetName, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var archived = AutoArchiveIfEngagementChanged(value);
            _svc.LoadPreset(value.Name);
            var suffix = archived is not null ? $" · log auto-archived → {archived}" : "";
            ActionLog = $"preset auto-loaded: {value.Name} (enforce state preserved){suffix}";
            _suppressAutoLoad = true;
            try { Refresh(); }
            finally { _suppressAutoLoad = false; }
        }
        catch (Exception ex) { ActionLog = "auto-load failed: " + ex.Message; }
    }

    // ─── Convenience computed ────────────────────────────────────────────
    /// <summary>Any active protective state (armed OR kill-switch OR proxy) — used by tab-header dot.</summary>
    public bool AnySafetyActive => IsArmed || IsKillSwitchActive || IsMitmproxyRunning;

    /// <summary>Human-readable overall state label (UI-facing).</summary>
    public string OverallState =>
        IsKillSwitchActive ? "KILL-SWITCH"
        : IsArmed          ? "ENFORCED"
        :                    "OFF";

    public string OverallStateColor =>
        IsKillSwitchActive ? "Danger"
        : IsArmed          ? "Success"
        :                    "TextDim";

    partial void OnIsArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(OverallState));
        OnPropertyChanged(nameof(OverallStateColor));
        OnPropertyChanged(nameof(AnySafetyActive));
    }
    partial void OnIsKillSwitchActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(OverallState));
        OnPropertyChanged(nameof(OverallStateColor));
        OnPropertyChanged(nameof(AnySafetyActive));
    }
    partial void OnIsMitmproxyRunningChanged(bool value) => OnPropertyChanged(nameof(AnySafetyActive));

    public EngagementSafetyViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void StartPolling()
    {
        Refresh();
        _timer.Start();
    }

    public void StopPolling() => _timer.Stop();

    // ─── Auto-elevate: when workflow auto-arms (armed=true) but L1 not yet running,
    // spawn mitmproxy + set env automatically. Guard rail so external arm (from workflow
    // Phase 1 auto-arm-target.ps1) still gets full L1+L2 protection without user click.
    [ObservableProperty] private bool _autoElevateEnabled = true;
    private string _lastArmedState = "unknown"; // tracks armed transitions for edge-triggered elevation

    // ─── Refresh from disk ───────────────────────────────────────────────

    [RelayCommand]
    public void Refresh()
    {
        var st = _svc.GetStatus();
        Installed = st.Installed;
        var prevArmed = IsArmed;
        IsArmed = st.Armed;
        IsKillSwitchActive = st.KillSwitchActive;
        IsMitmproxyRunning = st.MitmProxyRunning;
        IsProxyEnvSet = st.ProxyEnvSet;
        EngagementId = st.EngagementId;
        Target = st.Target;
        Environment = st.Environment;
        EnforceMode = st.EnforceMode;

        // Auto-elevate: armed just went true (external arm) but L1 not running → spawn
        if (AutoElevateEnabled
            && IsArmed && !prevArmed && !IsMitmproxyRunning
            && _lastArmedState != "enforced-elevated")
        {
            _lastArmedState = "enforced-elevated";
            try
            {
                var (proc, err) = _svc.StartMitmproxy();
                _svc.SetProxyEnv();
                IsMitmproxyRunning = _svc.IsMitmproxyRunning();
                IsProxyEnvSet = _svc.IsProxyEnvSet();
                ActionLog = err is not null
                    ? $"auto-elevated (enforcement detected) · mitmproxy start FAILED: {err}"
                    : $"auto-elevated (enforcement detected) · mitmproxy pid {proc?.Id} · proxy env set";
            }
            catch (Exception ex) { ActionLog = "auto-elevate failed: " + ex.Message; }
        }
        else if (!IsArmed)
        {
            _lastArmedState = "off";
        }

        StatusText = $"{OverallState} · mitmproxy {(IsMitmproxyRunning ? "on" : "off")} · env-proxy {(IsProxyEnvSet ? "set" : "unset")}";

        var m = _svc.ReadManifest();
        SyncList(ScopeInclude, m?.ScopeInclude);
        SyncList(ScopeExclude, m?.ScopeExclude);
        SyncList(InfraAllow, m?.InfraAllow);
        SyncList(ForbiddenReadGlobs, m?.ForbiddenReadGlobs);
        PerHostRps = m?.PerHostRps ?? 0;
        GlobalConcurrency = m?.GlobalConcurrency ?? 0;
        CwdRoot = m?.CwdRoot ?? "";

        // Refresh recent blocks (30 latest)
        var blocks = _svc.ReadRecentBlocks(30);
        RecentBlocks.Clear();
        foreach (var b in blocks.AsEnumerable().Reverse()) RecentBlocks.Add(b);

        // Refresh presets + detect active — suppress auto-load re-entry during this block
        var wasSuppressed = _suppressAutoLoad;
        _suppressAutoLoad = true;
        try
        {
            var presetList = _svc.ListPresets();
            var prevSelected = SelectedPreset?.Name;
            Presets.Clear();
            foreach (var p in presetList) Presets.Add(p);
            var detected = _svc.DetectActivePreset() ?? "";
            ActivePresetName = detected;
            // Prefer detected active preset for the ComboBox selection so the UI reflects reality.
            var choose = !string.IsNullOrEmpty(detected)
                ? Presets.FirstOrDefault(p => p.Name == detected)
                : (string.IsNullOrEmpty(prevSelected)
                    ? null
                    : Presets.FirstOrDefault(p => p.Name == prevSelected));
            SelectedPreset = choose;
        }
        finally { _suppressAutoLoad = wasSuppressed; }
    }

    private static void SyncList(ObservableCollection<string> target, System.Collections.Generic.List<string>? source)
    {
        target.Clear();
        if (source is null) return;
        foreach (var s in source) target.Add(s);
    }

    // ─── ARM / DISARM / KILL ─────────────────────────────────────────────

    [RelayCommand]
    private void ArmOn()
    {
        try
        {
            _svc.Arm();
            // Auto-start mitmproxy + set proxy env
            var (proc, err) = _svc.StartMitmproxy();
            if (err is not null)
                ActionLog = $"enforced · mitmproxy start FAILED: {err} (manual: mitmdump -s scope_guard.py --listen-port 8888)";
            else
                ActionLog = $"enforced · mitmproxy pid {proc?.Id} · proxy env set";
            _svc.SetProxyEnv();
            Refresh();
        }
        catch (Exception ex) { ActionLog = "enforce on failed: " + ex.Message; }
    }

    [RelayCommand]
    private void ArmOff()
    {
        try
        {
            _svc.Disarm();
            _svc.StopMitmproxy();
            _svc.ClearProxyEnv();
            ActionLog = "enforcement off · mitmproxy stopped · proxy env cleared";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "enforce off failed: " + ex.Message; }
    }

    [RelayCommand]
    private void KillSwitchOn()
    {
        try
        {
            _svc.CreateKillSwitch();
            ActionLog = "KILL-SWITCH engaged · all enforced tool calls will be blocked";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "kill-switch failed: " + ex.Message; }
    }

    [RelayCommand]
    private void KillSwitchOff()
    {
        try
        {
            _svc.RemoveKillSwitch();
            ActionLog = "kill-switch cleared";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "clear failed: " + ex.Message; }
    }

    [RelayCommand]
    private void StartMitmOnly()
    {
        var (proc, err) = _svc.StartMitmproxy();
        ActionLog = err is not null ? "mitm start failed: " + err : $"mitmproxy started pid {proc?.Id}";
        Refresh();
    }

    [RelayCommand]
    private void StopMitmOnly()
    {
        _svc.StopMitmproxy();
        ActionLog = "mitmproxy stopped";
        Refresh();
    }

    [RelayCommand]
    private void SetProxyEnv()
    {
        _svc.SetProxyEnv();
        ActionLog = "HTTP_PROXY / HTTPS_PROXY set (User) — new terminals will pick this up";
        Refresh();
    }

    [RelayCommand]
    private void ClearProxyEnv()
    {
        _svc.ClearProxyEnv();
        ActionLog = "HTTP_PROXY / HTTPS_PROXY cleared";
        Refresh();
    }

    // ─── Open helpers ────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenSafetyFolder()
    {
        if (!Directory.Exists(EngagementSafetyService.SafetyRoot)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{EngagementSafetyService.SafetyRoot}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenAuditLog()
    {
        var path = EngagementSafetyService.AuditLog;
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenManifest()
    {
        var path = EngagementSafetyService.ManifestPath;
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    // ─── Scope editor commands ───────────────────────────────────────────

    [RelayCommand]
    private void AddScopeInclude()
    {
        var v = NewScopeInclude?.Trim() ?? "";
        if (string.IsNullOrEmpty(v)) return;
        UpdateArrayField("scopeInclude", list => { if (!list.Contains(v)) list.Add(v); });
        NewScopeInclude = "";
        Refresh();
    }

    [RelayCommand]
    private void RemoveScopeInclude(string? item)
    {
        if (string.IsNullOrEmpty(item)) return;
        UpdateArrayField("scopeInclude", list => list.RemoveAll(x => x == item));
        Refresh();
    }

    [RelayCommand]
    private void AddScopeExclude()
    {
        var v = NewScopeExclude?.Trim() ?? "";
        if (string.IsNullOrEmpty(v)) return;
        UpdateArrayField("scopeExclude", list => { if (!list.Contains(v)) list.Add(v); });
        NewScopeExclude = "";
        Refresh();
    }

    [RelayCommand]
    private void RemoveScopeExclude(string? item)
    {
        if (string.IsNullOrEmpty(item)) return;
        UpdateArrayField("scopeExclude", list => list.RemoveAll(x => x == item));
        Refresh();
    }

    [RelayCommand]
    private void AddInfraAllow()
    {
        var v = NewInfraAllow?.Trim() ?? "";
        if (string.IsNullOrEmpty(v)) return;
        UpdateArrayField("infraAllow", list => { if (!list.Contains(v)) list.Add(v); });
        NewInfraAllow = "";
        Refresh();
    }

    [RelayCommand]
    private void RemoveInfraAllow(string? item)
    {
        if (string.IsNullOrEmpty(item)) return;
        UpdateArrayField("infraAllow", list => list.RemoveAll(x => x == item));
        Refresh();
    }

    private void UpdateArrayField(string field, Action<System.Collections.Generic.List<string>> mutator)
    {
        try
        {
            _svc.UpdateManifest(obj =>
            {
                var current = new System.Collections.Generic.List<string>();
                if (obj[field] is System.Text.Json.Nodes.JsonArray arr)
                {
                    foreach (var n in arr) if (n is not null) current.Add(n.GetValue<string>());
                }
                mutator(current);
                var newArr = new System.Text.Json.Nodes.JsonArray();
                foreach (var s in current) newArr.Add(s);
                obj[field] = newArr;
            });
        }
        catch (Exception ex) { ActionLog = "scope update failed: " + ex.Message; }
    }

    [RelayCommand]
    private void ClearRecentBlocks() => RecentBlocks.Clear();

    /// <summary>Archive current audit log to logs/archive/ and start fresh (destructive on the live file).</summary>
    [RelayCommand]
    private void ArchiveAuditLog()
    {
        var ok = Views.ConfirmDialog.Show(null,
            "Archive Hook Audit Log",
            "현재 hook-audit.log를 archive/ 폴더로 이동시키고 빈 로그로 새로 시작합니다.\n\n" +
            "이전 이벤트는 파일로 보관되지만 이 뷰에서는 즉시 사라집니다.\n\n계속?",
            Views.ConfirmKind.Danger);
        if (!ok) return;
        var (archived, path, err) = _svc.ArchiveAuditLog();
        if (archived)
            ActionLog = $"log archived → {System.IO.Path.GetFileName(path ?? "")}";
        else
            ActionLog = "archive failed: " + (err ?? "unknown");
        RecentBlocks.Clear();
        Refresh();
    }

    // ─── Preset commands ─────────────────────────────────────────────────

    /// <summary>
    /// Auto-archive audit log if the target preset's engagementId differs from current.
    /// Prevents cross-engagement event contamination in the LIVE view without user action.
    /// Returns the archived filename (for status log) or null if no archive occurred.
    /// </summary>
    private string? AutoArchiveIfEngagementChanged(PresetInfo target)
    {
        var currentEid = EngagementId ?? "";
        var targetEid = target.EngagementId ?? "";
        // Skip if same engagement or empty (nothing to distinguish)
        if (string.Equals(currentEid, targetEid, StringComparison.OrdinalIgnoreCase)) return null;
        // Only archive if log has meaningful content
        if (!System.IO.File.Exists(EngagementSafetyService.AuditLog)) return null;
        try
        {
            var fi = new System.IO.FileInfo(EngagementSafetyService.AuditLog);
            if (fi.Length < 10) return null;  // essentially empty
        }
        catch { }
        var (archived, path, _) = _svc.ArchiveAuditLog();
        if (archived && path is not null)
        {
            RecentBlocks.Clear();
            return System.IO.Path.GetFileName(path);
        }
        return null;
    }

    [RelayCommand]
    private void LoadPreset(PresetInfo? preset)
    {
        var p = preset ?? SelectedPreset;
        if (p is null) { ActionLog = "no preset selected"; return; }
        try
        {
            var archived = AutoArchiveIfEngagementChanged(p);
            _svc.LoadPreset(p.Name);
            var suffix = archived is not null ? $" · log auto-archived → {archived}" : "";
            ActionLog = $"preset loaded: {p.Name} (enforce state preserved){suffix}";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "load preset failed: " + ex.Message; }
    }

    /// <summary>Load preset + auto ENFORCE ON in one operation (typical engagement start).</summary>
    [RelayCommand]
    private void ArmWithPreset(PresetInfo? preset)
    {
        var p = preset ?? SelectedPreset;
        if (p is null) { ActionLog = "no preset selected"; return; }
        try
        {
            var archived = AutoArchiveIfEngagementChanged(p);
            _svc.LoadPreset(p.Name);
            _svc.Arm();
            var (proc, err) = _svc.StartMitmproxy();
            _svc.SetProxyEnv();
            var archiveSuffix = archived is not null ? $" · log auto-archived → {archived}" : "";
            if (err is not null)
                ActionLog = $"preset {p.Name} enforced · mitmproxy start FAILED: {err}{archiveSuffix}";
            else
                ActionLog = $"preset {p.Name} enforced · mitmproxy pid {proc?.Id} · proxy env set · restart Claude Code to inherit env{archiveSuffix}";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "enforce-with-preset failed: " + ex.Message; }
    }

    [RelayCommand]
    private void SaveAsPreset()
    {
        var (ok, name) = Views.InputDialog.Show(null,
            "Save Preset",
            "프리셋 이름 (영문·숫자·`-_.` 허용):",
            EngagementId);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _svc.SaveAsPreset(name.Trim());
            ActionLog = $"preset saved: {name.Trim()}";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "save preset failed: " + ex.Message; }
    }

    [RelayCommand]
    private void DeletePreset(PresetInfo? preset)
    {
        var p = preset ?? SelectedPreset;
        if (p is null) return;
        var ok = Views.ConfirmDialog.Show(null,
            "Delete Preset",
            $"프리셋 '{p.Name}' 을(를) 삭제합니다.\n계속?",
            Views.ConfirmKind.Danger);
        if (!ok) return;
        try
        {
            _svc.DeletePreset(p.Name);
            ActionLog = $"preset deleted: {p.Name}";
            Refresh();
        }
        catch (Exception ex) { ActionLog = "delete preset failed: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenPresetsFolder()
    {
        var dir = EngagementSafetyService.PresetsDir;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }
}
