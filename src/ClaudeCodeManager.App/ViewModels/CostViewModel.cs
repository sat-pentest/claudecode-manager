using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>One row in the by-model or by-day breakdown, with a bar width relative to the largest row.</summary>
public sealed class CostBar
{
    public string Label { get; init; } = "";
    public double Cost { get; init; }
    public long Tokens { get; init; }
    /// <summary>0..100 against the largest row in the same list, so the existing
    /// PercentToWidthConverter can size the bar without a new converter.</summary>
    public double Percent { get; init; }
    public bool IsUnpriced { get; init; }

    public string CostText => "$" + TokenPricing.FormatUsd(Cost);
    public string TokensText => TokenPricing.FormatTokens(Tokens);
}

public partial class CostViewModel : ModuleBase
{
    public override string Key => "COST";
    public override string Title => "COST";
    // Coin glyph — circle with a vertical stroke
    public override string Glyph => "M4,12 A8,8 0 1 0 20,12 A8,8 0 1 0 4,12 Z M12,7 V17 M9.5,9.5 H14.5 M9.5,14.5 H14.5";

    private readonly MainViewModel _main;

    public ObservableCollection<SessionCost> Sessions { get; } = new();
    public ObservableCollection<CostBar> ByModel { get; } = new();
    public ObservableCollection<CostBar> ByDay { get; } = new();

    [ObservableProperty] private SessionCost? _selected;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanProgress = "";

    [ObservableProperty] private string _totalCostText = "—";
    [ObservableProperty] private string _totalTokensText = "—";
    [ObservableProperty] private string _requestsText = "—";
    [ObservableProperty] private string _cacheShareText = "";
    [ObservableProperty] private string _unpricedWarning = "";

    /// <summary>Days shown in the trend strip.</summary>
    [ObservableProperty] private int _trendDays = 14;

    public bool HasData => Sessions.Count > 0;
    public bool HasUnpriced => !string.IsNullOrEmpty(UnpricedWarning);

    partial void OnUnpricedWarningChanged(string value) => OnPropertyChanged(nameof(HasUnpriced));

    public CostViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated()
    {
        if (Sessions.Count == 0 && !IsScanning) _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanProgress = "scanning transcripts…";

        try
        {
            var progress = new Progress<(int done, int total)>(p =>
                ScanProgress = $"{p.done}/{p.total} transcripts");

            var summary = await Task.Run(() => SessionCostService.Scan(progress));
            Apply(summary);
        }
        catch (Exception ex)
        {
            Status = "cost scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            ScanProgress = "";
        }
    }

    /// <summary>
    /// Re-read every transcript from scratch. Only needed if the per-file cache is suspected wrong,
    /// since the normal scan already re-reads anything whose size or write time changed.
    /// </summary>
    [RelayCommand]
    private async Task RescanFromScratchAsync()
    {
        SessionCostService.ClearCache();
        TokenPricing.Reload();
        await ScanAsync();
    }

    private void Apply(CostSummary s)
    {
        Sessions.Clear();
        foreach (var x in s.Sessions.Where(x => x.Totals.Total > 0)) Sessions.Add(x);

        var totals = s.Totals;
        TotalCostText = "$" + TokenPricing.FormatUsd(s.TotalCost);
        TotalTokensText = TokenPricing.FormatTokens(totals.Total);
        RequestsText = $"{totals.Requests:N0} requests · {s.Sessions.Count} sessions";

        // Cache reads dominate agent workloads and are billed at a tenth of input, so the headline
        // token count is misleading on its own. Showing the share makes the cheap bulk legible.
        var cacheShare = totals.Total == 0 ? 0 : 100.0 * totals.CacheRead / totals.Total;
        CacheShareText =
            $"cache reads {TokenPricing.FormatTokens(totals.CacheRead)} ({cacheShare:0}% of tokens, billed at 0.1x) · "
          + $"output {TokenPricing.FormatTokens(totals.Output)}";

        UnpricedWarning = s.UnpricedModels.Count == 0
            ? ""
            : "No rates for: " + string.Join(", ", s.UnpricedModels)
              + ". Their tokens are counted but not priced — add them to token-pricing.json.";

        BuildBars(ByModel, s.ByModel.Select(kv => (
            label: kv.Key,
            cost: TokenPricing.Cost(kv.Key, kv.Value) ?? 0,
            tokens: kv.Value.Total,
            unpriced: !TokenPricing.IsPriced(kv.Key))));

        var recent = s.CostByDay.Keys.OrderByDescending(d => d, StringComparer.Ordinal).Take(TrendDays).ToHashSet(StringComparer.Ordinal);
        BuildBars(ByDay, s.CostByDay
            .Where(kv => recent.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (
                label: kv.Key,
                cost: kv.Value,
                tokens: s.ByDay.TryGetValue(kv.Key, out var t) ? t.Total : 0L,
                unpriced: false)),
            sortByCost: false);

        Status = $"{s.FilesScanned} transcripts ({s.FilesFromCache} cached) in {s.ElapsedMs} ms";
        OnPropertyChanged(nameof(HasData));
    }

    private static void BuildBars(ObservableCollection<CostBar> target,
        IEnumerable<(string label, double cost, long tokens, bool unpriced)> rows,
        bool sortByCost = true)
    {
        var list = rows.ToList();
        if (sortByCost) list = list.OrderByDescending(r => r.cost).ToList();
        var max = list.Count == 0 ? 0 : list.Max(r => r.cost);

        target.Clear();
        foreach (var r in list)
        {
            target.Add(new CostBar
            {
                Label = r.label,
                Cost = r.cost,
                Tokens = r.tokens,
                Percent = max <= 0 ? 0 : 100.0 * r.cost / max,
                IsUnpriced = r.unpriced,
            });
        }
    }

    [RelayCommand]
    private void OpenTranscript(SessionCost? s)
    {
        if (s is null) return;
        var target = File.Exists(s.FilePath) ? $"/select,\"{s.FilePath}\"" : $"\"{s.FilePath}\"";
        try { Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void CopyBreakdown(SessionCost? s)
    {
        if (s is null) return;
        var lines = new List<string>
        {
            $"session  {s.DisplayLabel}",
            $"id       {s.SessionId}",
            $"project  {s.ProjectSlug}",
            $"range    {s.RangeText}",
            $"cost     {s.CostText}   ({s.Requests:N0} requests, {s.SubagentFiles} subagent transcripts)",
            "",
        };
        foreach (var (model, t) in s.ByModel.OrderByDescending(kv => kv.Value.Total))
        {
            var c = TokenPricing.Cost(model, t);
            lines.Add($"{model}");
            lines.Add($"   in {t.Input:N0} · out {t.Output:N0} · cacheRead {t.CacheRead:N0} · write5m {t.CacheWrite5m:N0} · write1h {t.CacheWrite1h:N0}");
            lines.Add($"   {(c is null ? "unpriced" : "$" + TokenPricing.FormatUsd(c.Value))}");
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Status = "breakdown copied · " + s.SessionId;
        }
        catch (Exception ex) { Status = "copy failed: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenPricingFile()
    {
        try
        {
            if (!File.Exists(TokenPricing.OverridePath))
            {
                // Seed a template from the live table so the file is editable rather than empty.
                var sample = System.Text.Json.JsonSerializer.Serialize(
                    TokenPricing.KnownModels,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                AtomicFileWriter.Write(TokenPricing.OverridePath, sample);
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{TokenPricing.OverridePath}\"") { UseShellExecute = true });
            Status = "pricing overrides: " + TokenPricing.OverridePath;
        }
        catch (Exception ex) { Status = "could not open pricing file: " + ex.Message; }
    }
}
