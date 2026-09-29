using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeManager.Core.Services;

/// <summary>One session group as the VS Code extension stores it.</summary>
public sealed class VsCodeSessionGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Collapsed { get; init; }
    /// <summary>Session UUIDs — the JSONL file names without their extension.</summary>
    public List<string> SessionIds { get; init; } = new();
    /// <summary>Workspace the group belongs to; groups are stored per workspace path.</summary>
    public string WorkspacePath { get; init; } = "";
}

public sealed class VsCodeSessionLayout
{
    public List<VsCodeSessionGroup> Groups { get; init; } = new();
    /// <summary>Sessions hidden from the VS Code list. Kept separate rather than filtered out —
    /// hiding a session there is a view preference, not a statement that the file is unwanted.</summary>
    public HashSet<string> HiddenSessionIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Available => Groups.Count > 0 || HiddenSessionIds.Count > 0;
    /// <summary>Why nothing was read, for the UI to show instead of an empty list.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Reads the session groups the operator made in the VS Code extension.
///
/// This is the one place the manager looks outside Claude Code's own files: the groups live in
/// VS Code's key-value store, under the extension's own key, in a shape nobody publishes. So it is
/// treated as foreign data throughout — every failure returns an empty layout with a reason rather
/// than throwing, because a session list that refuses to open is far worse than one without groups.
///
/// The database is copied before reading. VS Code keeps it open while running, and a copy removes
/// any question of locking or of a reader interfering with the editor. It is small (~300 KB).
///
/// Nothing here writes. The extension holds this state in memory and flushes it on its own
/// schedule, so an outside write would be silently overwritten — and would take the operator's
/// grouping with it.
/// </summary>
public static class VsCodeSessionGroupService
{
    private const string ExtensionKey = "Anthropic.claude-code";
    private const string GroupsPrefix = "sessionGroups:";
    private const string HiddenKey = "hiddenSessionIds";

    /// <summary>Candidate state stores, newest-installed flavour first.</summary>
    private static IEnumerable<string> CandidateStores()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) yield break;
        foreach (var flavour in new[] { "Code", "Code - Insiders", "VSCodium" })
        {
            var p = Path.Combine(appData, flavour, "User", "globalStorage", "state.vscdb");
            if (File.Exists(p)) yield return p;
        }
    }

    public static VsCodeSessionLayout Load()
    {
        var store = CandidateStores().FirstOrDefault();
        if (store is null)
            return new VsCodeSessionLayout { Problem = "VS Code 상태 저장소를 찾지 못했습니다" };

        string? temp = null;
        try
        {
            temp = CopyForReading(store);
            var json = ReadExtensionBlob(temp);
            if (json is null)
                return new VsCodeSessionLayout { Problem = "확장 데이터가 없습니다 (그룹을 만든 적이 없는 상태)" };

            return Parse(json);
        }
        catch (Exception ex)
        {
            return new VsCodeSessionLayout { Problem = "읽기 실패: " + ex.Message };
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>Copy the store and its sidecars so the read cannot touch the live file.</summary>
    private static string CopyForReading(string store)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccm-vscode-state");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "state.vscdb");
        File.Copy(store, target, overwrite: true);
        // A write-ahead log holds records not yet folded into the main file; without it a recent
        // grouping change would simply be missing from the copy.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = store + suffix;
            if (File.Exists(side)) File.Copy(side, target + suffix, overwrite: true);
        }
        return target;
    }

    private static string? ReadExtensionBlob(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var con = new SqliteConnection(cs);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", ExtensionKey);
        var value = cmd.ExecuteScalar();
        return value switch
        {
            string s => s,
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            _ => null,
        };
    }

    private static VsCodeSessionLayout Parse(string json)
    {
        var groups = new List<VsCodeSessionGroup>();
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new VsCodeSessionLayout { Problem = "예상과 다른 형식입니다" };

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals(HiddenKey) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in prop.Value.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String) hidden.Add(id.GetString()!);
                continue;
            }

            // Groups are keyed per workspace — every workspace is read, because the manager shows
            // every project's sessions, not just the one this window happens to be open on.
            if (!prop.Name.StartsWith(GroupsPrefix, StringComparison.Ordinal)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;

            var workspace = prop.Name[GroupsPrefix.Length..];
            foreach (var g in prop.Value.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var ids = new List<string>();
                if (g.TryGetProperty("sessionIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var id in arr.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);

                groups.Add(new VsCodeSessionGroup
                {
                    Id = g.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString()! : "",
                    Name = g.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString()! : "(이름 없음)",
                    Collapsed = g.TryGetProperty("collapsed", out var cv) && cv.ValueKind == JsonValueKind.True,
                    SessionIds = ids,
                    WorkspacePath = workspace,
                });
            }
        }

        return new VsCodeSessionLayout { Groups = groups, HiddenSessionIds = hidden };
    }

    private static void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* temp file — leave it */ }
        }
    }
}
