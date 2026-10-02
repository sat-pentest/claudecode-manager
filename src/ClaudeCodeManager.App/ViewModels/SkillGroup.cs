using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClaudeCodeManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>
/// One section of the grouped SKILLS list — the skills-module counterpart to the nav rail's
/// <see cref="ModuleGroup"/>, and it reuses the same fold animation and collapse persistence.
///
/// Selection is not held here: each group's inner list binds its selected item to the module's one
/// shared Selected, so clicking a skill in one group clears the highlight in the others without any
/// coordination code.
/// </summary>
public partial class SkillGroup : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<Skill> Skills { get; } = new();
    public int Count => Skills.Count;

    [ObservableProperty] private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => Toggled?.Invoke(this);

    /// <summary>Raised on any fold change so the module can persist the layout.</summary>
    public event Action<SkillGroup>? Toggled;

    public SkillGroup(string key, string title, IEnumerable<Skill> skills)
    {
        Key = key;
        Title = title;
        foreach (var s in skills) Skills.Add(s);
    }

    public bool Contains(Skill? s) => s is not null && Skills.Contains(s);

    public void EnsureExpanded() { if (!IsExpanded) IsExpanded = true; }
}
