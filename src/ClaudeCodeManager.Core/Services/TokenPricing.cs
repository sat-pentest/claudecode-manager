using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>Token counters as Claude Code records them, kept separate because they price differently.</summary>
public sealed class TokenTotals
{
    /// <summary>Uncached input.</summary>
    public long Input { get; set; }
    public long Output { get; set; }
    /// <summary>Cache writes with the 5-minute TTL — billed at 1.25x input.</summary>
    public long CacheWrite5m { get; set; }
    /// <summary>Cache writes with the 1-hour TTL — billed at 2x input.</summary>
    public long CacheWrite1h { get; set; }
    /// <summary>Cache hits — billed at a fraction of input (0.1x on most models).</summary>
    public long CacheRead { get; set; }

    public int Requests { get; set; }

    public long Total => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    public void Add(TokenTotals other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheWrite5m += other.CacheWrite5m;
        CacheWrite1h += other.CacheWrite1h;
        CacheRead += other.CacheRead;
        Requests += other.Requests;
    }
}

public sealed class ModelPrice
{
    [JsonPropertyName("input")] public double InputPerMTok { get; set; }
    [JsonPropertyName("output")] public double OutputPerMTok { get; set; }

    /// <summary>Cache read as a fraction of the input rate. 0.1 on most models.</summary>
    [JsonPropertyName("cacheReadMultiplier")] public double CacheReadMultiplier { get; set; } = 0.1;

    [JsonPropertyName("cacheWrite5mMultiplier")] public double CacheWrite5mMultiplier { get; set; } = 1.25;
    [JsonPropertyName("cacheWrite1hMultiplier")] public double CacheWrite1hMultiplier { get; set; } = 2.0;
}

/// <summary>
/// Per-model rates and the cost arithmetic over <see cref="TokenTotals"/>.
///
/// Rates are first-party Anthropic API list prices per million tokens. They are a moving target and
/// they do not describe a Claude subscription, so this produces a list-price equivalent — useful for
/// comparing sessions against each other and for spotting an expensive habit, not an invoice.
///
/// An unknown model returns null rather than zero. A session priced at $0.00 because the model was
/// unrecognised is worse than one that says it could not be priced, because nothing about the
/// display tells you which of the two happened.
///
/// Rates can be corrected without a rebuild by dropping a <c>token-pricing.json</c> into the
/// manager directory: a map of model id to {input, output, ...} that is merged over the defaults.
/// </summary>
public static class TokenPricing
{
    // Anthropic first-party list prices, USD per million tokens.
    private static readonly Dictionary<string, ModelPrice> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"]  = new() { InputPerMTok = 10.00, OutputPerMTok = 50.00, CacheReadMultiplier = 0.025 },
        ["claude-mythos-5-1"] = new() { InputPerMTok = 10.00, OutputPerMTok = 50.00 },
        ["claude-fable-5"]    = new() { InputPerMTok = 10.00, OutputPerMTok = 50.00 },
        ["claude-opus-5"]     = new() { InputPerMTok = 5.00,  OutputPerMTok = 25.00 },
        ["claude-opus-4-8"]   = new() { InputPerMTok = 5.00,  OutputPerMTok = 25.00 },
        ["claude-opus-4-7"]   = new() { InputPerMTok = 5.00,  OutputPerMTok = 25.00 },
        ["claude-opus-4-6"]   = new() { InputPerMTok = 5.00,  OutputPerMTok = 25.00 },
        ["claude-sonnet-5"]   = new() { InputPerMTok = 2.00,  OutputPerMTok = 10.00 },
        ["claude-sonnet-4-6"] = new() { InputPerMTok = 3.00,  OutputPerMTok = 15.00 },
        ["claude-haiku-4-5"]  = new() { InputPerMTok = 1.00,  OutputPerMTok = 5.00 },
    };

    public static string OverridePath => Path.Combine(ClaudePaths.ManagerRoot, "token-pricing.json");

    private static Dictionary<string, ModelPrice>? _merged;
    private static readonly object Gate = new();

    private static Dictionary<string, ModelPrice> Table
    {
        get
        {
            lock (Gate)
            {
                if (_merged is not null) return _merged;

                var table = new Dictionary<string, ModelPrice>(Defaults, StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(OverridePath))
                    {
                        var custom = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(File.ReadAllText(OverridePath));
                        if (custom is not null)
                            foreach (var (k, v) in custom) table[k] = v;
                    }
                }
                catch { /* malformed override — fall back to defaults rather than pricing nothing */ }

                _merged = table;
                return _merged;
            }
        }
    }

    public static void Reload() { lock (Gate) _merged = null; }

    /// <summary>
    /// Rates for a model id, or null when it is not in the table.
    ///
    /// Falls back to the longest matching prefix so a dated or suffixed variant
    /// (<c>claude-opus-5-20260401</c>) prices as its base model instead of going unpriced.
    /// </summary>
    public static ModelPrice? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (Table.TryGetValue(model, out var exact)) return exact;

        return Table
            .Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    public static bool IsPriced(string? model) => For(model) is not null;

    /// <summary>Cost in USD, or null when the model has no rates.</summary>
    public static double? Cost(string? model, TokenTotals t)
    {
        var p = For(model);
        if (p is null) return null;

        const double M = 1_000_000.0;
        return t.Input / M * p.InputPerMTok
             + t.Output / M * p.OutputPerMTok
             + t.CacheRead / M * p.InputPerMTok * p.CacheReadMultiplier
             + t.CacheWrite5m / M * p.InputPerMTok * p.CacheWrite5mMultiplier
             + t.CacheWrite1h / M * p.InputPerMTok * p.CacheWrite1hMultiplier;
    }

    public static IReadOnlyDictionary<string, ModelPrice> KnownModels => Table;

    public static string FormatUsd(double usd)
        => usd >= 100 ? usd.ToString("0") : usd >= 1 ? usd.ToString("0.00") : usd.ToString("0.000");

    public static string FormatTokens(long n)
        => n >= 1_000_000_000 ? (n / 1_000_000_000.0).ToString("0.0") + "B"
         : n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0") + "M"
         : n >= 1_000 ? (n / 1_000.0).ToString("0.0") + "K"
         : n.ToString();
}
