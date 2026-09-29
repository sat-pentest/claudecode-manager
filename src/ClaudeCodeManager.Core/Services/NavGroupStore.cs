using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Remembers which nav groups the operator left collapsed.
///
/// Without this every restart re-expands all four groups, so anyone who keeps a group shut has to
/// shut it again each launch — which makes collapsing not worth doing. Only the collapsed keys are
/// stored: a group added in a later version is then expanded by default rather than hidden by an
/// old file that never heard of it.
/// </summary>
public static class NavGroupStore
{
    public static string FilePath => Path.Combine(ClaudePaths.ManagerRoot, "nav-groups.json");

    public static HashSet<string> LoadCollapsed()
    {
        try
        {
            if (!File.Exists(FilePath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
            return new HashSet<string>(keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A corrupt layout file must never stop the shell from opening.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveCollapsed(IEnumerable<string> keys)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ManagerRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* layout preference — not worth surfacing an error for */ }
    }
}
