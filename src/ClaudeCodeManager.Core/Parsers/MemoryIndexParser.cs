using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.Core.Parsers;

public static class MemoryIndexParser
{
    private static readonly Regex ItemRegex = new(
        @"^\s*[-*]\s*\[(?<title>[^\]]+)\]\((?<file>[^)]+)\)\s*(?:[—\-–]\s*(?<hook>.+))?\s*$",
        RegexOptions.Compiled);

    public static List<MemoryIndexItem> Parse(string content)
    {
        var items = new List<MemoryIndexItem>();
        if (string.IsNullOrEmpty(content)) return items;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ItemRegex.Match(lines[i]);
            if (m.Success)
            {
                items.Add(new MemoryIndexItem
                {
                    Title = m.Groups["title"].Value.Trim(),
                    LinkedFile = m.Groups["file"].Value.Trim(),
                    Hook = m.Groups["hook"].Success ? m.Groups["hook"].Value.Trim() : "",
                    RawLine = lines[i],
                    LineNumber = i + 1
                });
            }
        }
        return items;
    }

    public static string Serialize(IEnumerable<MemoryIndexItem> items)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var it in items)
        {
            sb.Append("- [").Append(it.Title).Append("](").Append(it.LinkedFile).Append(")");
            if (!string.IsNullOrEmpty(it.Hook)) sb.Append(" — ").Append(it.Hook);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
