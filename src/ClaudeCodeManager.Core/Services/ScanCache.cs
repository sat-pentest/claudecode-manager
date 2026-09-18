using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Memoization for the scans that run on every module activation.
///
/// Every module rebuilds its whole model in <c>OnActivated()</c>, which <c>MainViewModel.Navigate</c>
/// calls synchronously on the UI thread — so a scan's cost is literally how long the window sits
/// frozen after a menu click. Measured before this existed: a DASHBOARD↔SCHEDULE round trip cost
/// 4.6s, and HARNESS/MCP/SCHEDULE each got *slower* on every revisit.
///
/// Two strategies, because the sources differ in kind:
///   <see cref="SignatureCache{T}"/> — for scans over files. Stat-ing is ~2 orders of magnitude
///       cheaper than reading, so the fingerprint is recomputed every call and the payload is
///       rebuilt only when it moves. Correct by construction: an external edit changes the
///       signature, so no invalidation call is needed anywhere.
///   <see cref="TtlCache{T}"/> — for scans with no file to watch (Task Scheduler, process probes).
///       There is nothing to fingerprint, so freshness is bounded by time and by explicit
///       invalidation from the mutating call sites.
/// </summary>
public sealed class SignatureCache<T> where T : class
{
    private readonly object _gate = new();
    private string? _signature;
    private T? _value;

    /// <summary>
    /// Returns the memoized value, rebuilding only when <paramref name="signature"/> differs from
    /// the one the current value was built under.
    /// </summary>
    public T Get(Func<string> signature, Func<T> build)
    {
        var sig = signature();
        lock (_gate)
        {
            if (_value is not null && _signature == sig) return _value;
        }

        // Build outside the lock: these are file scans, and holding the lock across them would
        // turn two modules activating at once into a serial wait for the slower one.
        var built = build();

        lock (_gate)
        {
            _signature = sig;
            _value = built;
            return built;
        }
    }

    public void Invalidate()
    {
        lock (_gate) { _signature = null; _value = null; }
    }
}

/// <summary>
/// Time-bounded memoization for scans with no file to fingerprint.
/// </summary>
public sealed class TtlCache<T> where T : class
{
    private readonly object _gate = new();
    private readonly TimeSpan _ttl;
    private DateTime _builtAt = DateTime.MinValue;
    private T? _value;

    public TtlCache(TimeSpan ttl) => _ttl = ttl;

    public T Get(Func<T> build)
    {
        lock (_gate)
        {
            if (_value is not null && DateTime.UtcNow - _builtAt < _ttl) return _value;
        }

        var built = build();

        lock (_gate)
        {
            _value = built;
            _builtAt = DateTime.UtcNow;
            return built;
        }
    }

    /// <summary>True when a value is held and still inside its TTL — lets a caller show something
    /// immediately and refresh in the background rather than blocking on a cold build.</summary>
    public bool TryPeek(out T? value)
    {
        lock (_gate)
        {
            value = _value;
            return _value is not null && DateTime.UtcNow - _builtAt < _ttl;
        }
    }

    public void Invalidate()
    {
        lock (_gate) { _value = null; _builtAt = DateTime.MinValue; }
    }
}

/// <summary>
/// Cheap fingerprints of a file set. Mirrors the scheme RankedSearchService already uses: count,
/// last-write and length folded together. Not a hash of contents — a file rewritten within the
/// same filesystem timestamp tick and to the same length reads as unchanged, which is the standard
/// trade every build system makes.
/// </summary>
public static class FileSignature
{
    /// <summary>Fingerprint of specific files. Missing files contribute a fixed marker, so a file
    /// appearing or vanishing moves the signature.</summary>
    public static string Of(params string[] paths)
    {
        long acc = 17;
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                acc = unchecked(acc * 31 + (fi.Exists ? fi.LastWriteTimeUtc.Ticks + fi.Length : -1));
            }
            catch { acc = unchecked(acc * 31 - 2); }
        }
        return acc.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Fingerprint of a directory tree. Enumeration alone is cheap (measured: 803 files
    /// across ~885 MB enumerate in 3 ms) — it is the reading and parsing that costs.</summary>
    public static string OfTree(string root, string pattern = "*", SearchOption option = SearchOption.AllDirectories)
    {
        if (!Directory.Exists(root)) return "missing";
        long acc = 17;
        var n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, pattern, option))
            {
                n++;
                try
                {
                    var fi = new FileInfo(f);
                    acc = unchecked(acc * 31 + fi.LastWriteTimeUtc.Ticks + fi.Length);
                }
                catch { /* vanished mid-scan; the next call picks it up */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable tree must not be cached as if it were stable.
            return "error:" + Guid.NewGuid().ToString("N");
        }
        return n.ToString(CultureInfo.InvariantCulture) + ":" + acc.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Fingerprint of several trees.</summary>
    public static string OfTrees(IEnumerable<string> roots, string pattern = "*")
    {
        var parts = new List<string>();
        foreach (var r in roots) parts.Add(OfTree(r, pattern));
        return string.Join("|", parts);
    }
}
