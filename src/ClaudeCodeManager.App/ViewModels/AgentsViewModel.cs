using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class AgentsViewModel : ModuleBase
{
    public override string Key => "AGNT";
    public override string Title => "AGENTS";
    // Two overlapping node icons — represents a delegated agent
    public override string Glyph => "M6,7 A3,3 0 1 0 12,7 A3,3 0 1 0 6,7 M14,15 A3,3 0 1 0 20,15 A3,3 0 1 0 14,15 M9,10 L15,14 M12,3 V4 M12,20 V21";

    public static IReadOnlyList<string> ModelOptions => SkillsViewModel.ModelOptions;

    private readonly MainViewModel _main;
    public ObservableCollection<AgentDefinition> Agents { get; } = new();

    [ObservableProperty] private AgentDefinition? _selected;
    [ObservableProperty] private bool _isPreviewMode = true;
    [ObservableProperty] private bool _isDetailCollapsed;
    [ObservableProperty] private bool _isBodyCollapsed;
    [ObservableProperty] private string _modelChoice = "(inherit)";
    private bool _suppressModelSave;

    public AgentsViewModel(MainViewModel main) { _main = main; }

    [RelayCommand] private void TogglePreview() => IsPreviewMode = !IsPreviewMode;
    [RelayCommand] private void ToggleDetail() => IsDetailCollapsed = !IsDetailCollapsed;
    [RelayCommand] private void ToggleBody() => IsBodyCollapsed = !IsBodyCollapsed;

    public override void OnActivated()
    {
        var curPath = Selected?.FilePath;
        Agents.Clear();
        foreach (var a in AgentLoader.LoadAll()) Agents.Add(a);
        Selected = curPath is null
            ? Agents.FirstOrDefault()
            : (Agents.FirstOrDefault(a => a.FilePath == curPath) ?? Agents.FirstOrDefault());
        Status = $"{Agents.Count} agents · {Agents.Count(a => a.Disabled)} disabled";
    }

    partial void OnSelectedChanged(AgentDefinition? value)
    {
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
        var cur = string.IsNullOrWhiteSpace(Selected.Model) ? null : Selected.Model;
        if (cur == newModel) return;

        _main.Snapshots.CreateSnapshot($"change model · agent {Selected.Name}");
        try
        {
            var changed = await FrontmatterUpdater.SetModelAsync(Selected.FilePath, newModel);
            if (changed)
            {
                Selected.Model = newModel;
                Status = $"{Selected.Name} model → {choice}";
                OnActivated();
            }
        }
        catch (Exception ex)
        {
            Status = "model save failed: " + ex.Message;
        }
    }

    public void SelectByPath(string path)
    {
        if (Agents.Count == 0)
            foreach (var a in AgentLoader.LoadAll()) Agents.Add(a);
        var agent = Agents.FirstOrDefault(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (agent is not null) Selected = agent;
    }

    [RelayCommand]
    private void Toggle(AgentDefinition? a)
    {
        if (a is null) return;
        _main.Snapshots.CreateSnapshot($"toggle agent · {a.Name}");
        AgentLoader.Toggle(a);
        OnActivated();
        Status = $"{a.Name} → {(a.Disabled ? "disabled" : "enabled")}";
    }

    [RelayCommand]
    private void OpenFolder(AgentDefinition? a)
    {
        if (a is null) return;
        var dir = Path.GetDirectoryName(a.FilePath);
        if (dir is null || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{a.FilePath}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenAgentsRoot()
    {
        try
        {
            if (!Directory.Exists(ClaudePaths.AgentsRoot)) Directory.CreateDirectory(ClaudePaths.AgentsRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ClaudePaths.AgentsRoot}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void Refresh() => OnActivated();
}
