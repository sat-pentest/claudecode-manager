using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using YamlDotNet.RepresentationModel;

namespace ClaudeCodeManager.Core.Parsers;

public static class FrontmatterParser
{
    private const string Delimiter = "---";

    public static (Frontmatter fm, string body) Parse(string content)
    {
        var fm = new Frontmatter();
        if (string.IsNullOrEmpty(content)) return (fm, content ?? "");

        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != Delimiter)
            return (fm, content);

        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == Delimiter) { endIdx = i; break; }
        }
        if (endIdx < 0) return (fm, content);

        var yamlText = string.Join("\n", lines.Skip(1).Take(endIdx - 1));
        try
        {
            using var sr = new StringReader(yamlText);
            var ys = new YamlStream();
            ys.Load(sr);
            if (ys.Documents.Count > 0 && ys.Documents[0].RootNode is YamlMappingNode map)
            {
                foreach (var kv in map.Children)
                {
                    var key = ((YamlScalarNode)kv.Key).Value ?? "";
                    switch (key.ToLowerInvariant())
                    {
                        case "name": fm.Name = ScalarValue(kv.Value); break;
                        case "description": fm.Description = ScalarValue(kv.Value); break;
                        case "type": fm.Type = ScalarValue(kv.Value); break;
                        case "model": fm.Model = ScalarValue(kv.Value); break;
                        case "category": fm.Category = ScalarValue(kv.Value); break;
                        case "tools":
                            if (kv.Value is YamlSequenceNode seq)
                                fm.Tools = seq.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                            else if (kv.Value is YamlScalarNode sc && !string.IsNullOrWhiteSpace(sc.Value))
                                fm.Tools = sc.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                            break;
                        default:
                            fm.Extra[key] = ScalarValue(kv.Value);
                            break;
                    }
                }

                // Fallback: Claude Code session-summarizer writes `type` NESTED under `metadata:`
                // rather than at the root. If root-level type wasn't found, look inside metadata.
                // This is how ~half the memory files in the wild are structured.
                if (string.IsNullOrEmpty(fm.Type))
                {
                    foreach (var kv in map.Children)
                    {
                        var key = ((YamlScalarNode)kv.Key).Value ?? "";
                        if (!key.Equals("metadata", StringComparison.OrdinalIgnoreCase)) continue;
                        if (kv.Value is not YamlMappingNode metaMap) continue;
                        foreach (var mkv in metaMap.Children)
                        {
                            var mkey = ((YamlScalarNode)mkv.Key).Value ?? "";
                            if (mkey.Equals("type", StringComparison.OrdinalIgnoreCase))
                            {
                                fm.Type = ScalarValue(mkv.Value);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // fall through with empty frontmatter
        }

        var body = string.Join("\n", lines.Skip(endIdx + 1)).TrimStart('\n');
        return (fm, body);
    }

    public static string Serialize(Frontmatter fm, string body)
    {
        if (fm.IsEmpty) return body ?? "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Delimiter);
        if (!string.IsNullOrEmpty(fm.Name)) sb.AppendLine($"name: {YamlEscape(fm.Name)}");
        if (!string.IsNullOrEmpty(fm.Description)) sb.AppendLine($"description: {YamlEscape(fm.Description!)}");
        if (!string.IsNullOrEmpty(fm.Type)) sb.AppendLine($"type: {fm.Type}");
        if (!string.IsNullOrEmpty(fm.Model)) sb.AppendLine($"model: {fm.Model}");
        if (fm.Tools.Count > 0) sb.AppendLine($"tools: [{string.Join(", ", fm.Tools)}]");
        foreach (var kv in fm.Extra)
        {
            if (kv.Value is not null)
                sb.AppendLine($"{kv.Key}: {YamlEscape(kv.Value.ToString() ?? "")}");
        }
        sb.AppendLine(Delimiter);
        sb.AppendLine();
        sb.Append(body ?? "");
        return sb.ToString();
    }

    private static string? ScalarValue(YamlNode? n) => (n as YamlScalarNode)?.Value;

    private static string YamlEscape(string v)
    {
        if (v.Contains(':') || v.Contains('#') || v.Contains('"') || v.Contains('\n') || v.StartsWith(' ') || v.EndsWith(' '))
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        return v;
    }
}
