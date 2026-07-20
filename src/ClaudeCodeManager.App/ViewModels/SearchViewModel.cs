using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class SearchViewModel : ModuleBase
{
    public override string Key => "SRCH";
    public override string Title => "SEARCH";
    public override string Glyph => "M4,11 A7,7 0 1 0 18,11 A7,7 0 1 0 4,11 Z M16,16 L21,21";

    private readonly MainViewModel _main;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private string _stats = "";
    public ObservableCollection<SearchHit> Hits { get; } = new();
    [ObservableProperty] private SearchHit? _selected;

    public SearchViewModel(MainViewModel main) { _main = main; }

    [RelayCommand]
    private void Run()
    {
        Hits.Clear();
        var sw = Stopwatch.StartNew();
        int count = 0;
        foreach (var h in SearchService.Search(Query, IsRegex, CaseSensitive))
        {
            Hits.Add(h);
            count++;
            if (count > 1000) break;
        }
        sw.Stop();
        Stats = $"{Hits.Count} hits in {sw.ElapsedMilliseconds}ms";
        Status = Stats;
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
