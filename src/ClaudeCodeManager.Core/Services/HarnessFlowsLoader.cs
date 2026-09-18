using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// One flow step as declared in ~/.claude/harness-flows.json.
/// Wire format is intentionally identical to the runtime shape so JSON edits show up
/// in HARNESS on next Refresh, no rebuild required.
/// </summary>
public sealed class HarnessFlowStepDef
{
    public string Kind { get; set; } = "";       // SKILL | AGENT | MCP | EXTERNAL
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Arrow { get; set; } = "";
    /// <summary>How many copies of this step run concurrently. >1 renders the node as a
    /// fan-out stack (worker #1..#N) in HARNESS instead of a single box. Default 1.</summary>
    public int Parallel { get; set; } = 1;
}

public sealed class HarnessFlowDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TriggerHint { get; set; } = "";
    public List<HarnessFlowStepDef> Steps { get; set; } = new();
}

/// <summary>
/// Loads orchestration flow definitions from ~/.claude/harness-flows.json.
/// Missing file or parse error → returns null so the caller can fall back to built-in defaults.
/// Edit the JSON to add/remove/reorder flows without rebuilding the app.
/// </summary>
public static class HarnessFlowsLoader
{
    public static string FlowsFilePath => Path.Combine(ClaudePaths.ClaudeRoot, "harness-flows.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Read the raw JSON text (empty string if file missing) so it can be shown in an editor.</summary>
    public static string ReadRawText()
    {
        try { return File.Exists(FlowsFilePath) ? File.ReadAllText(FlowsFilePath) : ""; }
        catch { return ""; }
    }

    /// <summary>Write the raw JSON text atomically. Caller is responsible for validating first.</summary>
    public static async System.Threading.Tasks.Task WriteRawTextAsync(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FlowsFilePath)!);
        await AtomicFileWriter.WriteAsync(FlowsFilePath, content);
    }

    /// <summary>Validate a candidate JSON string. Returns (ok, error, flowCount).</summary>
    public static (bool ok, string error, int flowCount) Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "empty content", 0);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, "root must be a JSON object", 0);
            if (!doc.RootElement.TryGetProperty("flows", out var flows) || flows.ValueKind != JsonValueKind.Array)
                return (false, "root must have a 'flows' array", 0);
            int i = 0;
            foreach (var f in flows.EnumerateArray())
            {
                i++;
                if (f.ValueKind != JsonValueKind.Object) return (false, $"flows[{i - 1}] is not an object", 0);
                if (!f.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString()))
                    return (false, $"flows[{i - 1}].name is missing or not a non-empty string", 0);
                if (!f.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                    return (false, $"flows[{i - 1}].steps must be an array", 0);
            }
            return (true, "", i);
        }
        catch (JsonException ex)
        {
            return (false, ex.Message, 0);
        }
    }

    public static List<HarnessFlowDef>? TryLoad()
    {
        try
        {
            if (!File.Exists(FlowsFilePath)) return null;
            var text = File.ReadAllText(FlowsFilePath);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (!doc.RootElement.TryGetProperty("flows", out var flowsEl)) return null;
            var list = new List<HarnessFlowDef>();
            foreach (var f in flowsEl.EnumerateArray())
            {
                var def = new HarnessFlowDef
                {
                    Name = f.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                    Description = f.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "",
                    TriggerHint = f.TryGetProperty("triggerHint", out var t) ? (t.GetString() ?? "") : "",
                };
                if (f.TryGetProperty("steps", out var stepsEl))
                {
                    foreach (var s in stepsEl.EnumerateArray())
                    {
                        def.Steps.Add(new HarnessFlowStepDef
                        {
                            Kind = s.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "",
                            Name = s.TryGetProperty("name", out var sn) ? (sn.GetString() ?? "") : "",
                            Detail = s.TryGetProperty("detail", out var sd) ? (sd.GetString() ?? "") : "",
                            Arrow = s.TryGetProperty("arrow", out var sa) ? (sa.GetString() ?? "") : "",
                            // Absent / non-numeric / <1 all collapse to a single node.
                            Parallel = s.TryGetProperty("parallel", out var sp) && sp.TryGetInt32(out var pn) && pn > 1 ? pn : 1,
                        });
                    }
                }
                list.Add(def);
            }
            return list;
        }
        catch { return null; }
    }
}
