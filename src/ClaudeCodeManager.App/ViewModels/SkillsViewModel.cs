using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    public override void OnActivated()
    {
        var curPath = Selected?.SkillFilePath;
        Skills.Clear();
        foreach (var s in SkillLoader.LoadAll()) Skills.Add(s);
        Selected = curPath is null ? Skills.FirstOrDefault() : Skills.FirstOrDefault(s => s.SkillFilePath == curPath || s.FolderPath == System.IO.Path.GetDirectoryName(curPath));
        Status = $"{Skills.Count} skills · {Skills.Count(s => s.Disabled)} disabled";
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
