using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public sealed class SettingsBundle
{
    public string Path { get; set; } = "";
    public JsonObject Root { get; set; } = new();
    public bool Exists { get; set; }
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static SettingsBundle Load(string path)
    {
        var bundle = new SettingsBundle { Path = path, Exists = File.Exists(path) };
        if (!bundle.Exists)
        {
            bundle.Root = new JsonObject();
            return bundle;
        }
        var text = File.ReadAllText(path);
        try
        {
            var node = JsonNode.Parse(text);
            bundle.Root = node as JsonObject ?? new JsonObject();
        }
        catch
        {
            bundle.Root = new JsonObject();
        }
        return bundle;
    }

    public static async Task SaveAsync(SettingsBundle bundle)
    {
        var text = bundle.Root.ToJsonString(Opts);
        await AtomicFileWriter.WriteAsync(bundle.Path, text);
    }

    public static IEnumerable<string> ListPermissionAllow(SettingsBundle b)
    {
        if (b.Root["permissions"] is JsonObject perms && perms["allow"] is JsonArray arr)
        {
            foreach (var n in arr) if (n is JsonValue v && v.TryGetValue<string>(out var s)) yield return s;
        }
    }

    public static IEnumerable<string> ListPermissionDeny(SettingsBundle b)
    {
        if (b.Root["permissions"] is JsonObject perms && perms["deny"] is JsonArray arr)
        {
            foreach (var n in arr) if (n is JsonValue v && v.TryGetValue<string>(out var s)) yield return s;
        }
    }

    public static IEnumerable<(string Event, string Matcher, string Command)> ListHooks(SettingsBundle b)
    {
        if (b.Root["hooks"] is not JsonObject hooks) yield break;
        foreach (var ev in hooks)
        {
            if (ev.Value is JsonArray groups)
            {
                foreach (var g in groups)
                {
                    if (g is JsonObject go)
                    {
                        var matcher = go["matcher"]?.GetValue<string>() ?? "";
                        if (go["hooks"] is JsonArray inner)
                        {
                            foreach (var h in inner)
                            {
                                if (h is JsonObject ho)
                                {
                                    var cmd = ho["command"]?.GetValue<string>() ?? "";
                                    yield return (ev.Key, matcher, cmd);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
