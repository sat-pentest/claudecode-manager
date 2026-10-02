using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Remembers which skill groups the operator left collapsed, and the grouping axis they last chose.
///
/// Keys are stored as <c>AXIS:groupname</c> so the collapse state for CATEGORY and STATUS do not
/// tread on each other. Like the nav rail's store, only the collapsed keys are kept — a group that
/// appears in a later build is expanded by default rather than hidden by a stale file.
/// </summary>
public static class SkillGroupStore
{
    private sealed class State
    {
        public string Axis { get; set; } = "CATEGORY";
        public List<string> Collapsed { get; set; } = new();
    }

    public static string FilePath => Path.Combine(ClaudePaths.ManagerRoot, "skill-groups.json");

    public static (string axis, HashSet<string> collapsed) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return ("CATEGORY", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var st = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State();
            return (string.IsNullOrWhiteSpace(st.Axis) ? "CATEGORY" : st.Axis,
                    new HashSet<string>(st.Collapsed ?? new List<string>(), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return ("CATEGORY", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public static void Save(string axis, IEnumerable<string> collapsed)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ManagerRoot);
            var st = new State { Axis = axis, Collapsed = new List<string>(collapsed) };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* layout preference — not worth surfacing */ }
    }

    public static string Key(string axis, string group) => axis + ":" + group;
}
