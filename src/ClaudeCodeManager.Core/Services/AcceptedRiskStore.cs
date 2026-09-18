using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Remembers which host-security findings the operator has deliberately accepted.
///
/// This exists so the security list can keep unresolved items distinct from accepted risk. Without
/// it, a workstation that is configured on purpose -- inbound RDP for a VPN jump, a broad Bash
/// allow rule -- reports the same red count forever, and a permanently red list is one nobody
/// reads. An accepted mark records the reason and the date alongside the id, and survives rescans.
///
/// Accepting never changes host state and never suppresses the underlying check: the finding is
/// still scanned, still shown, and can be un-accepted at any time.
/// </summary>
public static class AcceptedRiskStore
{
    private sealed class Entry
    {
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("acceptedAt")] public string AcceptedAt { get; set; } = "";
    }

    public static string FilePath => Path.Combine(ClaudePaths.ManagerRoot, "accepted-risks.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, Entry>(StringComparer.Ordinal);
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(json)
                   ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt store must not take the scan down with it -- worst case the operator
            // re-accepts. Never rewrite the file here; that would destroy recoverable content.
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private static void Save(Dictionary<string, Entry> map)
        => AtomicFileWriter.Write(FilePath, JsonSerializer.Serialize(map, Options));

    /// <summary>Stamp accepted marks onto a freshly scanned set.</summary>
    public static void Apply(IEnumerable<SecurityCheck> checks)
    {
        var map = Load();
        foreach (var c in checks)
        {
            if (!map.TryGetValue(c.Id, out var e)) continue;
            c.AcceptedRisk = true;
            c.AcceptedNote = string.IsNullOrWhiteSpace(e.AcceptedAt)
                ? e.Note
                : $"{e.Note}  (accepted {e.AcceptedAt})";
        }
    }

    public static void Accept(string id, string note)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var map = Load();
        map[id] = new Entry { Note = note.Trim(), AcceptedAt = DateTime.Now.ToString("yyyy-MM-dd") };
        Save(map);
    }

    public static void Unaccept(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var map = Load();
        if (map.Remove(id)) Save(map);
    }
}
