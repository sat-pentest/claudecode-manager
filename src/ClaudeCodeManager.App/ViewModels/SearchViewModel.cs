using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class SearchFilterChip : ObservableObject
{
    public SearchHitCategory Key { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
}

public partial class SearchViewModel : ModuleBase
{
    public override string Key => "SRCH";
    public override string Title => "SEARCH";
    public override string Glyph => "M4,11 A7,7 0 1 0 18,11 A7,7 0 1 0 4,11 Z M16,16 L21,21";

    private readonly MainViewModel _main;
    private readonly List<SearchHit> _allHits = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private bool _caseSensitive;

    /// <summary>
    /// Rank whole documents by relevance instead of listing every matching line.
    ///
    /// Default on: for "where did I write about X" the literal list buries the answer among
    /// incidental mentions, because every line scores the same. Literal mode stays one click away
    /// and is still the right tool when the exact string is what matters.
    /// </summary>
    [ObservableProperty] private bool _isRanked = true;

    /// <summary>regex and case only mean anything to the literal scan.</summary>
    public bool LiteralOptionsEnabled => !IsRanked;
    partial void OnIsRankedChanged(bool value) => OnPropertyChanged(nameof(LiteralOptionsEnabled));
    [ObservableProperty] private string _stats = "";
    public ObservableCollection<SearchHit> Hits { get; } = new();
    [ObservableProperty] private SearchHit? _selected;

    public ObservableCollection<SearchFilterChip> Filters { get; } = new();
    [ObservableProperty] private SearchHitCategory _activeFilter = SearchHitCategory.All;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.All, Label = "ALL", IsActive = true });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.ClaudeMd, Label = "CLAUDE.MD" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Memory, Label = "MEMORY" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Skills, Label = "SKILLS" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Agents, Label = "AGENTS" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Workflows, Label = "WORKFLOWS" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Settings, Label = "SETTINGS" });
        Filters.Add(new SearchFilterChip { Key = SearchHitCategory.Other, Label = "OTHER" });
    }

    [RelayCommand]
    private void Run()
    {
        _allHits.Clear();
        var sw = Stopwatch.StartNew();

        if (IsRanked)
        {
            _allHits.AddRange(RankedSearchService.Search(Query, 300));
        }
        else
        {
            int count = 0;
            foreach (var h in SearchService.Search(Query, IsRegex, CaseSensitive))
            {
                _allHits.Add(h);
                count++;
                if (count > 2000) break;
            }
        }

        sw.Stop();
        RecomputeCounts();
        ApplyFilter();
        Stats = IsRanked
            ? $"{_allHits.Count} docs ranked in {sw.ElapsedMilliseconds}ms"
            : $"{_allHits.Count} hits in {sw.ElapsedMilliseconds}ms";
        Status = Stats;
    }

    private void RecomputeCounts()
    {
        foreach (var f in Filters)
        {
            f.Count = f.Key == SearchHitCategory.All
                ? _allHits.Count
                : _allHits.Count(h => h.Category == f.Key);
        }
    }

    private void ApplyFilter()
    {
        Hits.Clear();
        IEnumerable<SearchHit> src = ActiveFilter == SearchHitCategory.All
            ? _allHits
            : _allHits.Where(h => h.Category == ActiveFilter);
        // _allHits is already in score order for ranked runs and file order otherwise; filtering
        // preserves both, so nothing re-sorts here.
        int shown = 0;
        foreach (var h in src)
        {
            Hits.Add(h);
            shown++;
            if (shown > 1000) break;
        }
    }

    [RelayCommand]
    private void SelectFilter(SearchFilterChip? chip)
    {
        if (chip is null) return;
        ActiveFilter = chip.Key;
        foreach (var f in Filters) f.IsActive = f.Key == chip.Key;
        ApplyFilter();
    }

    [RelayCommand]
    private void OpenInExplorer(SearchHit? h)
    {
        if (h is null || !File.Exists(h.File)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{h.File}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Navigate to the module that owns this file and select it there.</summary>
    [RelayCommand]
    private void Navigate(SearchHit? h)
    {
        if (h is null || !File.Exists(h.File)) return;
        _main.NavigateToFile(h.File);
    }
}
