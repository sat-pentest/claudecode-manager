using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;

namespace ClaudeCodeManager.Core.Services;

public sealed class SplitPlan
{
    public List<SplitPart> Parts { get; set; } = new();
    public string IndexContent { get; set; } = "";
}

public sealed class SplitPart
{
    public string TargetRelativePath { get; set; } = "";
    public string Content { get; set; } = "";
    public ClaudeMdSection Section { get; set; } = new();
}

public static class ClaudeMdSplitter
{
    public static SplitPlan Plan(string fullContent, string targetSubdir, int splitAtLevel = 2)
    {
        var sections = MarkdownSectionParser.ParseFlat(fullContent);
        var top = sections.Where(s => s.Level == splitAtLevel).ToList();
        var plan = new SplitPlan();
        if (top.Count == 0) return plan;

        var preamble = sections.FirstOrDefault()?.Level < splitAtLevel
            ? sections.First().Content
            : "";

        var indexSb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(preamble))
        {
            indexSb.Append(preamble.TrimEnd());
            indexSb.Append("\n\n");
        }

        foreach (var s in top)
        {
            var fileName = SafeFileName(s.Title) + ".md";
            plan.Parts.Add(new SplitPart
            {
                TargetRelativePath = $"{targetSubdir}/{fileName}",
                Content = s.Content,
                Section = s
            });
            indexSb.Append($"@{targetSubdir}/{fileName}\n");
        }

        plan.IndexContent = indexSb.ToString();
        return plan;
    }

    public static async Task ApplyAsync(SplitPlan plan, string indexPath, string baseDir)
    {
        foreach (var part in plan.Parts)
        {
            var abs = Path.Combine(baseDir, part.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await AtomicFileWriter.WriteAsync(abs, part.Content);
        }
        await AtomicFileWriter.WriteAsync(indexPath, plan.IndexContent);
    }

    private static string SafeFileName(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) || c == '-' || c == '_') sb.Append('-');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "section" : s;
    }
}
