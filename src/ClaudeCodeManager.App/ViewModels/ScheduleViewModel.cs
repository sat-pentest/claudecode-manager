using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeManager.App.Views;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>A weekday checkbox in the weekly editor.</summary>
public partial class DayToggle : ObservableObject
{
    public string Name { get; init; } = "";      // PowerShell day name
    public string Label { get; init; } = "";     // 월..일
    [ObservableProperty] private bool _isChecked;
}

public partial class ScheduleViewModel : ModuleBase
{
    public override string Key => "SCHD";
    public override string Title => "SCHEDULE";
    // Clock glyph
    public override string Glyph => "M4,12 A8,8 0 1 0 20,12 A8,8 0 1 0 4,12 Z M12,7 V12 L15.5,14";

    private readonly MainViewModel _main;

    public ObservableCollection<ScheduledRun> Runs { get; } = new();
    [ObservableProperty] private ScheduledRun? _selected;

    // ─── Editor fields ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editPrompt = "";
    [ObservableProperty] private string _editWorkingDir = "";
    [ObservableProperty] private string _editTime = "09:00";
    [ObservableProperty] private int _editEveryHours = 6;
    [ObservableProperty] private int _kindIndex;        // 0 Daily · 1 Weekly · 2 Hourly
    [ObservableProperty] private string _editError = "";
    [ObservableProperty] private string _scriptPreview = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isLogOpen;

    public ObservableCollection<DayToggle> Days { get; } = new();

    public bool IsWeekly => KindIndex == 1;
    public bool IsHourly => KindIndex == 2;
    public bool UsesTimeOfDay => KindIndex != 2;

    public bool HasSelection => Selected is not null;
    public bool HasError => !string.IsNullOrEmpty(EditError);

    /// <summary>False when the CLI is missing — creating a task that cannot run is pure noise.</summary>
    public bool ClaudeAvailable { get; } = ScheduledRunService.IsClaudeAvailable();
    public string ClaudePathText => ScheduledRunService.ResolveClaudeCommand() ?? "claude CLI를 찾을 수 없습니다";

    public ScheduleViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var (n, l) in new[]
                 {
                     ("Monday", "월"), ("Tuesday", "화"), ("Wednesday", "수"), ("Thursday", "목"),
                     ("Friday", "금"), ("Saturday", "토"), ("Sunday", "일"),
                 })
            Days.Add(new DayToggle { Name = n, Label = l });
    }

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IsHourly));
        OnPropertyChanged(nameof(UsesTimeOfDay));
        RefreshPreview();
    }

    partial void OnEditErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnEditNameChanged(string value) => RefreshPreview();
    partial void OnEditWorkingDirChanged(string value) => RefreshPreview();

    partial void OnSelectedChanged(ScheduledRun? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        IsLogOpen = false;
        if (value is not null && !IsEditing) LoadIntoEditor(value, editing: false);
    }

    // Activation takes the cached path; the Refresh button forces a real Task Scheduler query.
    // Keeping them the same method would have made the cache pointless, since activation is what
    // calls it most.
    public override void OnActivated() => Load();

    [RelayCommand]
    private void Refresh()
    {
        ScheduledRunService.InvalidateListCache();
        Load();
    }

    private void Load()
    {
        var keep = Selected?.Name;
        Runs.Clear();
        foreach (var r in ScheduledRunService.List()) Runs.Add(r);

        Selected = keep is null
            ? Runs.FirstOrDefault()
            : Runs.FirstOrDefault(r => r.Name == keep) ?? Runs.FirstOrDefault();

        var enabled = Runs.Count(r => r.IsEnabled);
        var failed = Runs.Count(r => r.LastRunFailed);
        Status = Runs.Count == 0
            ? "등록된 예약 실행 없음"
            : $"{Runs.Count} runs · {enabled} enabled" + (failed > 0 ? $" · {failed} 실패" : "");
    }

    private void LoadIntoEditor(ScheduledRun run, bool editing)
    {
        var s = run.Spec;
        EditName = s.Name;
        EditPrompt = s.Prompt;
        EditWorkingDir = s.WorkingDir;
        EditTime = string.IsNullOrWhiteSpace(s.Time) ? "09:00" : s.Time;
        EditEveryHours = s.EveryHours <= 0 ? 6 : s.EveryHours;
        KindIndex = s.Kind switch { ScheduleKind.Weekly => 1, ScheduleKind.Hourly => 2, _ => 0 };
        foreach (var d in Days) d.IsChecked = s.DaysOfWeek.Contains(d.Name, StringComparer.OrdinalIgnoreCase);
        IsEditing = editing;
        IsNew = false;
        EditError = "";
        RefreshPreview();
    }

    [RelayCommand]
    private void New()
    {
        EditName = "";
        EditPrompt = "";
        EditWorkingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        EditTime = "09:00";
        EditEveryHours = 6;
        KindIndex = 0;
        foreach (var d in Days) d.IsChecked = d.Name == "Monday";
        IsEditing = true;
        IsNew = true;
        EditError = "";
        RefreshPreview();
    }

    [RelayCommand]
    private void Edit()
    {
        if (Selected is null) return;
        LoadIntoEditor(Selected, editing: true);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditError = "";
        if (Selected is not null) LoadIntoEditor(Selected, editing: false);
    }

    private ScheduledRunSpec BuildSpec() => new()
    {
        Name = EditName.Trim(),
        Prompt = EditPrompt,
        WorkingDir = EditWorkingDir.Trim(),
        Kind = KindIndex switch { 1 => ScheduleKind.Weekly, 2 => ScheduleKind.Hourly, _ => ScheduleKind.Daily },
        Time = EditTime.Trim(),
        EveryHours = EditEveryHours,
        DaysOfWeek = Days.Where(d => d.IsChecked).Select(d => d.Name).ToList(),
    };

    /// <summary>
    /// Keep the generated launcher on screen while the operator types. This is the whole command
    /// that will run unattended, and showing it is the only honest way to ask someone to approve it.
    /// </summary>
    private void RefreshPreview()
    {
        if (string.IsNullOrWhiteSpace(EditName)) { ScriptPreview = ""; return; }
        try { ScriptPreview = ScheduledRunService.BuildScript(BuildSpec()); }
        catch { ScriptPreview = ""; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync()
    {
        EditError = "";
        var spec = BuildSpec();

        if (spec.Kind == ScheduleKind.Weekly && spec.DaysOfWeek.Count == 0)
        {
            EditError = "요일을 하나 이상 선택하세요.";
            return;
        }
        if (spec.Kind != ScheduleKind.Hourly && !TimeSpan.TryParse(spec.Time, out _))
        {
            EditError = "시각을 HH:mm 형식으로 입력하세요.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(spec.WorkingDir) && !Directory.Exists(spec.WorkingDir))
        {
            EditError = "작업 디렉터리가 존재하지 않습니다.";
            return;
        }

        if (IsNew && Runs.Any(r => string.Equals(r.Name, ScheduledRunService.Sanitize(spec.Name), StringComparison.OrdinalIgnoreCase)))
        {
            EditError = "같은 이름의 예약이 이미 있습니다.";
            return;
        }

        // Unattended execution is the whole point of the feature and also its only real hazard:
        // nobody is watching to stop a prompt that wanders outside the engagement scope.
        var confirmed = ConfirmDialog.Show(
            System.Windows.Application.Current?.MainWindow,
            "예약 실행 등록",
            $"'{spec.Name}' 을(를) {spec.ScheduleText} 에 무인 실행합니다." + Environment.NewLine + Environment.NewLine
            + "이 프롬프트는 사람이 지켜보지 않는 상태로 돌아갑니다. 스코프 밖 자산을 지시하지 않는지 확인하세요."
            + Environment.NewLine + Environment.NewLine
            + "작업 디렉터리: " + (string.IsNullOrWhiteSpace(spec.WorkingDir) ? "(사용자 홈)" : spec.WorkingDir),
            ConfirmKind.Normal);
        if (!confirmed) return;

        await _main.Snapshots.CreateSnapshotAsync($"schedule · save {spec.Name}");

        var err = ScheduledRunService.Save(spec);
        if (err is not null) { EditError = err; return; }

        IsEditing = false;
        IsNew = false;
        Refresh();
        Selected = Runs.FirstOrDefault(r => r.Name == ScheduledRunService.Sanitize(spec.Name)) ?? Selected;
        Status = "등록됨 · " + spec.Name;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        var name = Selected.Name;

        if (!ConfirmDialog.Show(System.Windows.Application.Current?.MainWindow,
                "예약 삭제",
                $"'{name}' 예약을 삭제합니다." + Environment.NewLine + Environment.NewLine
                + "작업·프롬프트·런처가 제거됩니다. 실행 로그는 기록으로 남깁니다.",
                ConfirmKind.Danger))
            return;

        var err = ScheduledRunService.Delete(name);
        Status = err is null ? "삭제됨 · " + name : "삭제 실패: " + err;
        Refresh();
    }

    [RelayCommand]
    private void ToggleEnabled()
    {
        if (Selected is null) return;
        var err = ScheduledRunService.SetEnabled(Selected.Name, !Selected.IsEnabled);
        Status = err is null
            ? (Selected.IsEnabled ? "비활성화됨 · " : "활성화됨 · ") + Selected.Name
            : "변경 실패: " + err;
        Refresh();
    }

    [RelayCommand]
    private void RunNow()
    {
        if (Selected is null) return;
        if (!ConfirmDialog.Show(System.Windows.Application.Current?.MainWindow,
                "지금 실행",
                $"'{Selected.Name}' 을(를) 즉시 실행합니다." + Environment.NewLine + Environment.NewLine
                + "백그라운드에서 Claude가 실행되며, 결과는 로그에 기록됩니다.",
                ConfirmKind.Normal))
            return;

        var err = ScheduledRunService.RunNow(Selected.Name);
        Status = err is null ? "실행 시작됨 · " + Selected.Name : "실행 실패: " + err;
    }

    [RelayCommand]
    private void ToggleLog()
    {
        IsLogOpen = !IsLogOpen;
        if (IsLogOpen && Selected is not null) LogText = ScheduledRunService.ReadLogTail(Selected.Name);
    }

    [RelayCommand]
    private void RefreshLog()
    {
        if (Selected is not null) LogText = ScheduledRunService.ReadLogTail(Selected.Name);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ScheduledRunService.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScheduledRunService.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Status = "폴더 열기 실패: " + ex.Message; }
    }

    /// <summary>Hand the operator the Windows console for anything this editor does not cover.</summary>
    [RelayCommand]
    private void OpenTaskScheduler()
    {
        try { Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "작업 스케줄러 열기 실패: " + ex.Message; }
    }
}
