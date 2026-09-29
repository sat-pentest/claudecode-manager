using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>
/// One section of the nav rail.
///
/// Fifteen flat rows give the eye no landmarks — finding COST means reading the whole list every
/// time. Grouping by what the module is *for* turns that into two steps: pick the section, then the
/// row. The sections are deliberately few and wide rather than many and precise; a group of one or
/// two is a header that earns no space.
/// </summary>
public partial class ModuleGroup : ObservableObject
{
    public string Key { get; }
    public string Title { get; }

    /// <summary>One line on what the section is for. Shown while the group is collapsed, so a shut
    /// group still says what is inside it instead of becoming an opaque label.</summary>
    public string Hint { get; }

    public ObservableCollection<ModuleBase> Modules { get; } = new();

    /// <summary>What the rail draws — every module, or only those matching the filter.</summary>
    public ObservableCollection<ModuleBase> Visible { get; } = new();

    /// <summary>False hides the whole section, header and all, while a filter is running.</summary>
    [ObservableProperty] private bool _hasMatches = true;

    /// <summary>The fold the operator chose, parked while a filter forces sections open.</summary>
    private bool? _foldBeforeFilter;

    /// <summary>
    /// Narrow the section to modules whose name contains <paramref name="query"/>.
    ///
    /// A filtered section always opens: leaving a match hidden inside a folded group would make
    /// the search look broken. The fold the operator set is remembered and restored the moment the
    /// box is cleared, so filtering never quietly rearranges the rail.
    /// </summary>
    public void ApplyFilter(string? query)
    {
        Visible.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var m in Modules) Visible.Add(m);
            HasMatches = true;
            if (_foldBeforeFilter is { } restored)
            {
                IsExpanded = restored;
                _foldBeforeFilter = null;
            }
            return;
        }

        foreach (var m in Modules)
            if (m.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                Visible.Add(m);

        HasMatches = Visible.Count > 0;
        if (HasMatches)
        {
            _foldBeforeFilter ??= IsExpanded;
            IsExpanded = true;
        }
    }

    /// <summary>
    /// Single source of truth for the fold, two-way bound to the header's IsChecked.
    ///
    /// The header used to drive a command while its checked state read from here one-way. That
    /// works for a mouse click and breaks for everything else: a keyboard or accessibility toggle
    /// flips the button's own state without running the command, and the header then disagrees
    /// with the section it controls.
    /// </summary>
    [ObservableProperty] private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => Toggled?.Invoke(this);

    /// <summary>Raised whenever the fold changes, so the shell can persist the layout.</summary>
    public event Action<ModuleGroup>? Toggled;

    public ModuleGroup(string key, string title, string hint, IEnumerable<ModuleBase> modules)
    {
        Key = key;
        Title = title;
        Hint = hint;
        foreach (var m in modules) { Modules.Add(m); Visible.Add(m); }
    }

    public bool Contains(ModuleBase? module) => module is not null && Modules.Contains(module);

    /// <summary>Open a shut section — used when navigation lands inside one, where hiding the row
    /// that just became current would be a lie.</summary>
    public void EnsureExpanded()
    {
        if (!IsExpanded) IsExpanded = true;
    }
}
