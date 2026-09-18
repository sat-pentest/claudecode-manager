using System;
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

public partial class SkillsViewModel : ModuleBase
{
    public override string Key => "SKIL";
    public override string Title => "SKILLS";
    public override string Glyph => "M3,7 H17 M3,12 H21 M3,17 H13";

    // Shared model choices for both skills and agents. "(inherit)" clears the frontmatter line.
    public static IReadOnlyList<string> ModelOptions { get; } = new[]
    {
        "(inherit)", "opus", "sonnet", "haiku", "fable"
    };

    private readonly MainViewModel _main;
    public ObservableCollection<Skill> Skills { get; } = new();
    [ObservableProperty] private Skill? _selected;
    [ObservableProperty] private bool _isPreviewMode = true;
    [ObservableProperty] private bool _isDetailCollapsed;
    [ObservableProperty] private bool _isBodyCollapsed;
    [ObservableProperty] private string _modelChoice = "(inherit)";
    private bool _suppressModelSave;

    [RelayCommand]
    private void TogglePreview() => IsPreviewMode = !IsPreviewMode;

    [RelayCommand]
    private void ToggleDetail() => IsDetailCollapsed = !IsDetailCollapsed;

    [RelayCommand]
    private void ToggleBody() => IsBodyCollapsed = !IsBodyCollapsed;

    public SkillsViewModel(MainViewModel main) { _main = main; }

    // ─── SECURITY ────────────────────────────────────────────────────────
    //
    // Twelve static rules over every skill and agent definition, paired with a per-file review
    // baseline. The rules describe capability, not intent, and in a security practitioner's own
    // library a lot of that capability is the subject matter — so the list is driven by what is NEW
    // or CHANGED since the last sign-off, not by the raw hit count. That also makes it answer the
    // question the rules cannot: did a definition change when I did not change it.

    public ObservableCollection<SkillScanResult> SecurityResults { get; } = new();
    [ObservableProperty] private SkillScanResult? _selectedSecurity;
    [ObservableProperty] private bool _isSecurityPanelOpen;
    [ObservableProperty] private string _securityStatus = "";
    [ObservableProperty] private int _needsAttentionCount;

    public bool HasSecurityAttention => NeedsAttentionCount > 0;
    partial void OnNeedsAttentionCountChanged(int value) => OnPropertyChanged(nameof(HasSecurityAttention));

    [RelayCommand]
    private void ToggleSecurityPanel()
    {
        IsSecurityPanelOpen = !IsSecurityPanelOpen;
        if (IsSecurityPanelOpen) ScanSecurity();
    }

    [RelayCommand]
    private void ScanSecurity()
    {
        SecurityResults.Clear();
        var results = SkillSecurityScanner.ScanAll(Skills);
        foreach (var r in results) SecurityResults.Add(r);

        SelectedSecurity = SecurityResults.FirstOrDefault(r => r.NeedsAttention) ?? SecurityResults.FirstOrDefault();
        NeedsAttentionCount = results.Count(r => r.NeedsAttention);

        var changed = results.Count(r => r.ReviewState == SkillReviewState.Changed);
        SecurityStatus =
            $"{results.Count} definitions · {SkillSecurityScanner.RuleCount} rules · "
          + $"{NeedsAttentionCount} need review" + (changed > 0 ? $" ({changed} changed since sign-off)" : "");
    }

    [RelayCommand]
    private void MarkReviewed(SkillScanResult? r)
    {
        if (r is null || string.IsNullOrEmpty(r.ContentHash)) return;
        SkillReviewStore.MarkReviewed(r.Path, r.ContentHash);
        ScanSecurity();
        Status = "signed off · " + r.Name;
    }

    [RelayCommand]
    private void UnreviewSkill(SkillScanResult? r)
    {
        if (r is null) return;
        SkillReviewStore.Forget(r.Path);
        ScanSecurity();
    }

    /// <summary>
    /// Baseline everything at once. Offered because the first run reports every definition as NEW,
    /// and walking 17 of them one by one to establish a starting point is busywork — the value of
    /// the baseline starts at the *next* change, not this one.
    /// </summary>
    [RelayCommand]
    private void MarkAllReviewed()
    {
        if (!ConfirmDialog.Show(System.Windows.Application.Current?.MainWindow,
                "BASELINE ALL",
                $"Sign off all {SecurityResults.Count} definitions at their current content?"
                + Environment.NewLine + Environment.NewLine
                + "Anything edited afterwards comes back as CHANGED.",
                ConfirmKind.Normal))
            return;

        SkillReviewStore.MarkAllReviewed(SecurityResults);
        ScanSecurity();
        Status = "baseline recorded for all definitions";
    }

    [RelayCommand]
    private void OpenSecurityTarget(SkillScanResult? r)
    {
        if (r is null || !System.IO.File.Exists(r.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{r.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public override void OnActivated()
    {
        var curPath = Selected?.SkillFilePath;
        Skills.Clear();
        foreach (var s in SkillLoader.LoadAll()) Skills.Add(s);
        Selected = curPath is null ? Skills.FirstOrDefault() : Skills.FirstOrDefault(s => s.SkillFilePath == curPath || s.FolderPath == System.IO.Path.GetDirectoryName(curPath));
        Status = $"{Skills.Count} skills · {Skills.Count(s => s.Disabled)} disabled";

        // Cheap enough (a few dozen small files) to keep the header badge honest on every entry.
        ScanSecurity();
    }

    partial void OnSelectedChanged(Skill? value)
    {
        // Refresh the model choice combo when selection changes — but suppress auto-save
        _suppressModelSave = true;
        ModelChoice = string.IsNullOrWhiteSpace(value?.Model) ? "(inherit)" : value.Model!;
        _suppressModelSave = false;
    }

    partial void OnModelChoiceChanged(string value)
    {
        if (_suppressModelSave) return;
        _ = SaveModelAsync(value);
    }

    private async Task SaveModelAsync(string choice)
    {
        if (Selected is null) return;
        string? newModel = choice == "(inherit)" ? null : choice;
        // No-op if unchanged (Selected.Model may be null for inherit)
        var cur = string.IsNullOrWhiteSpace(Selected.Model) ? null : Selected.Model;
        if (cur == newModel) return;

        _main.Snapshots.CreateSnapshot($"change model · skill {Selected.Name}");
        try
        {
            var changed = await FrontmatterUpdater.SetModelAsync(Selected.SkillFilePath, newModel);
            if (changed)
            {
                Selected.Model = newModel;
                Status = $"{Selected.Name} model → {choice}";
                OnActivated();   // reload from disk (in case parser normalizes anything)
            }
        }
        catch (System.Exception ex)
        {
            Status = "model save failed: " + ex.Message;
        }
    }

    public void SelectByPath(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (folder is null) return;
        if (Skills.Count == 0)
        {
            foreach (var s in SkillLoader.LoadAll()) Skills.Add(s);
        }
        var skill = Skills.FirstOrDefault(s => string.Equals(s.FolderPath, folder, System.StringComparison.OrdinalIgnoreCase));
        if (skill is not null) Selected = skill;
    }

    [RelayCommand]
    private void Toggle(Skill? s)
    {
        if (s is null) return;
        _main.Snapshots.CreateSnapshot($"toggle skill · {s.Name}");
        SkillLoader.Toggle(s);
        OnActivated();
        Status = $"{s.Name} → {(s.Disabled ? "disabled" : "enabled")}";
    }

    [RelayCommand]
    private void Delete(Skill? s)
    {
        if (s is null) return;
        var ok = Views.ConfirmDialog.Show(null,
            "Delete skill",
            $"{s.Name} 스킬 폴더 전체를 삭제합니다.\n{s.FolderPath}\n\n" +
            "SKILL.md 외 번들 스크립트·레퍼런스도 함께 제거됩니다.\n" +
            "스냅샷 자동 생성됨. SNAPSHOTS에서 복구 가능.\n계속?",
            Views.ConfirmKind.Danger);
        if (!ok) return;
        _main.Snapshots.CreateSnapshot($"delete skill · {s.Name}");
        try
        {
            SkillLoader.Delete(s);
            OnActivated();
            Status = $"deleted · {s.Name}";
        }
        catch (System.Exception ex)
        {
            Status = "delete failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenFolder(Skill? s)
    {
        if (s is null || !Directory.Exists(s.FolderPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.FolderPath}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void Refresh() => OnActivated();

    [RelayCommand]
    private async Task AddTrigger(Skill? skill)
    {
        if (skill is null) return;
        var (ok, phrase) = Views.InputDialog.Show(null,
            "Add TRIGGER",
            "새 트리거 문구 입력 (한국어/영어):",
            "");
        if (!ok || string.IsNullOrWhiteSpace(phrase)) return;
        phrase = phrase.Trim().Trim('"').Trim('`');
        if (string.IsNullOrEmpty(phrase)) return;
        if (skill.Triggers.Contains(phrase, System.StringComparer.OrdinalIgnoreCase))
        {
            Status = "trigger already exists: " + phrase;
            return;
        }

        var desc = (skill.Description ?? "").TrimEnd();
        var addition = string.IsNullOrEmpty(desc) ? $"\"{phrase}\"" : $"{desc} · \"{phrase}\"";
        _main.Snapshots.CreateSnapshot($"add trigger · skill {skill.Name}");
        try
        {
            var changed = await FrontmatterUpdater.SetKeyAsync(skill.SkillFilePath, "description", addition);
            if (changed)
            {
                var curPath = skill.SkillFilePath;
                OnActivated();
                Selected = Skills.FirstOrDefault(s => s.SkillFilePath == curPath);
                Status = $"{skill.Name} trigger added: \"{phrase}\"";
            }
        }
        catch (System.Exception ex) { Status = "add trigger failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveTrigger(string? phrase)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(phrase)) return;
        var skill = Selected;
        var desc = skill.Description ?? "";
        var quoted = "\"" + phrase + "\"";
        var backtick = "`" + phrase + "`";

        string newDesc = desc;
        foreach (var form in new[] {
            " · " + quoted, "·" + quoted, ", " + quoted, "," + quoted, quoted,
            " · " + backtick, "·" + backtick, ", " + backtick, "," + backtick, backtick
        })
        {
            newDesc = newDesc.Replace(form, "");
        }
        while (newDesc.Contains("  ")) newDesc = newDesc.Replace("  ", " ");
        newDesc = newDesc.Trim(' ', ',', '·');

        _main.Snapshots.CreateSnapshot($"remove trigger · skill {skill.Name}");
        try
        {
            var changed = await FrontmatterUpdater.SetKeyAsync(skill.SkillFilePath, "description", newDesc);
            if (changed)
            {
                var curPath = skill.SkillFilePath;
                OnActivated();
                Selected = Skills.FirstOrDefault(s => s.SkillFilePath == curPath);
                Status = $"{skill.Name} trigger removed: \"{phrase}\"";
            }
        }
        catch (System.Exception ex) { Status = "remove trigger failed: " + ex.Message; }
    }
}
