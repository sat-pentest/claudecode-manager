using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.Core.Parsers;

public static class MarkdownSectionParser
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);

    public static List<ClaudeMdSection> ParseFlat(string content)
    {
        var result = new List<ClaudeMdSection>();
        if (string.IsNullOrEmpty(content)) return result;
        var lines = content.Replace("\r\n", "\n").Split('\n');

        ClaudeMdSection? cur = null;
        bool inCodeFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```")) inCodeFence = !inCodeFence;
            if (!inCodeFence)
            {
                var m = HeadingRegex.Match(line);
                if (m.Success)
                {
                    if (cur is not null)
                    {
                        cur.EndLine = i - 1;
                        cur.Content = JoinLines(lines, cur.StartLine, cur.EndLine);
                        result.Add(cur);
                    }
                    cur = new ClaudeMdSection
                    {
                        Level = m.Groups[1].Value.Length,
                        Title = m.Groups[2].Value.Trim(),
                        StartLine = i,
                        Anchor = Slugify(m.Groups[2].Value.Trim())
                    };
                    continue;
                }
            }
        }
        if (cur is not null)
        {
            cur.EndLine = lines.Length - 1;
            cur.Content = JoinLines(lines, cur.StartLine, cur.EndLine);
            result.Add(cur);
        }
        return result;
    }

    private static string JoinLines(string[] lines, int from, int to)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = from; i <= to && i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < to) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }
}
