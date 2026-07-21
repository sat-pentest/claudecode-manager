using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Loads workflow scripts from ~/.claude/workflows/ and parses their `export const meta = {…}`
/// block into structured metadata. Regex-based — meta is contract-required to be a pure literal,
/// so a simple parser is sufficient (no need to bring in a JS AST).
/// </summary>
public static class WorkflowLoader
{
    public static string WorkflowsDir => Path.Combine(ClaudePaths.ClaudeRoot, "workflows");

    public static List<WorkflowEntry> LoadAll()
    {
        var list = new List<WorkflowEntry>();
        if (!Directory.Exists(WorkflowsDir)) return list;

        foreach (var f in Directory.EnumerateFiles(WorkflowsDir))
        {
            var name = Path.GetFileName(f);
            if (!IsWorkflowFile(name)) continue;

            string text;
            try { text = File.ReadAllText(f); }
            catch { continue; }

            var entry = new WorkflowEntry
            {
                FilePath = f,
                Text = text,
                ModifiedAt = File.GetLastWriteTime(f)
            };
            ParseMeta(text, entry);
            if (string.IsNullOrEmpty(entry.Name))
            {
                // Fall back to filename stem so listing isn't blank
                entry.Name = Path.GetFileNameWithoutExtension(entry.DisplayName);
            }
            list.Add(entry);
        }

        return list.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsWorkflowFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.EndsWith(".mjs") || lower.EndsWith(".mjs.disabled");
    }

    /// <summary>
    /// Extract `name`, `description`, `phases[].title` from the meta object literal.
    /// The Workflow tool contract requires meta to be a pure literal (no expressions),
    /// which makes regex extraction reliable enough for display purposes.
    /// </summary>
    private static void ParseMeta(string text, WorkflowEntry entry)
    {
        // Locate `export const meta = {`
        var head = Regex.Match(text, @"export\s+const\s+meta\s*=\s*\{");
        if (!head.Success) return;

        // Find matching closing brace by tracking depth. Track quoted string bodies so a `{` or `}`
        // inside a string literal doesn't mislead the counter.
        int start = head.Index + head.Length - 1;
        int depth = 0;
        int end = -1;
        char? quote = null;
        bool escape = false;

        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (quote is not null)
            {
                if (c == '\\') { escape = true; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0) return;

        var block = text.Substring(start, end - start + 1);

        // name — single line, one of three quote styles
        var nm = Regex.Match(block, @"name\s*:\s*(['""`])(?<v>(?:\\.|(?!\1).)*)\1", RegexOptions.Singleline);
        if (nm.Success) entry.Name = nm.Groups["v"].Value;

        // description — same quote styles, possibly multi-line for backtick
        var dm = Regex.Match(block, @"description\s*:\s*(['""`])(?<v>(?:\\.|(?!\1).)*)\1", RegexOptions.Singleline);
        if (dm.Success) entry.Description = dm.Groups["v"].Value;

        // phases — array of objects. Uses a balanced-bracket + quote-aware scanner so nested
        // `tools: ['A', 'B']` arrays (which contain `]`) don't prematurely terminate the outer
        // `phases: [...]` block — a naive non-greedy regex broke here.
        var phasesHead = Regex.Match(block, @"phases\s*:\s*\[");
        if (phasesHead.Success)
        {
            var body = ExtractBalanced(block, phasesHead.Index + phasesHead.Length - 1, '[', ']');
            if (body is not null)
            {
                // body includes outer [ and ]; strip them
                var inner = body.Substring(1, body.Length - 2);
                var idx = 1;
                var pos = 0;
                while (pos < inner.Length)
                {
                    // Skip whitespace and commas between objects
                    while (pos < inner.Length && (char.IsWhiteSpace(inner[pos]) || inner[pos] == ',')) pos++;
                    if (pos >= inner.Length) break;
                    if (inner[pos] != '{') { pos++; continue; }
                    var obj = ExtractBalanced(inner, pos, '{', '}');
                    if (obj is null) break;
                    var innerObj = obj.Substring(1, obj.Length - 2);
                    var titleM = Regex.Match(innerObj, @"title\s*:\s*(['""`])(?<v>(?:\\.|(?!\1).)*)\1", RegexOptions.Singleline);
                    if (titleM.Success)
                    {
                        var detailM = Regex.Match(innerObj, @"detail\s*:\s*(['""`])(?<v>(?:\\.|(?!\1).)*)\1", RegexOptions.Singleline);
                        var toolsHead = Regex.Match(innerObj, @"tools\s*:\s*\[");
                        var tools = new List<string>();
                        if (toolsHead.Success)
                        {
                            var toolsBlock = ExtractBalanced(innerObj, toolsHead.Index + toolsHead.Length - 1, '[', ']');
                            if (toolsBlock is not null)
                            {
                                foreach (Match tm in Regex.Matches(toolsBlock, @"(['""`])(?<v>(?:\\.|(?!\1).)*)\1", RegexOptions.Singleline))
                                    tools.Add(tm.Groups["v"].Value);
                            }
                        }
                        entry.Phases.Add(new WorkflowPhase
                        {
                            Index = idx++,
                            Title = titleM.Groups["v"].Value,
                            Detail = detailM.Success ? detailM.Groups["v"].Value : "",
                            Tools = tools
                        });
                    }
                    pos += obj.Length;
                }
                if (entry.Phases.Count > 0)
                    entry.Phases[entry.Phases.Count - 1].IsLast = true;
            }
        }
    }

    /// <summary>
    /// Given a string and a start index pointing at an opening delimiter, walk forward
    /// tracking nested depth and quoted strings, and return the substring from the opener
    /// through its matching closer (inclusive). Returns null if the delimiters are unbalanced.
    /// Handles `'`, `"`, `` ` `` string literals and their backslash escapes.
    /// </summary>
    private static string? ExtractBalanced(string s, int startIdx, char open, char close)
    {
        if (startIdx < 0 || startIdx >= s.Length || s[startIdx] != open) return null;
        int depth = 0;
        char? quote = null;
        bool escape = false;
        for (int i = startIdx; i < s.Length; i++)
        {
            var c = s[i];
            if (escape) { escape = false; continue; }
            if (quote is not null)
            {
                if (c == '\\') { escape = true; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return s.Substring(startIdx, i - startIdx + 1);
            }
        }
        return null;
    }

    /// <summary>Toggle enabled ↔ disabled by renaming with `.disabled` suffix.</summary>
    public static void Toggle(WorkflowEntry w)
    {
        string newPath;
        if (w.Disabled)
        {
            // Strip `.disabled`
            newPath = w.FilePath.Substring(0, w.FilePath.Length - WorkflowEntry.DisabledSuffix.Length);
        }
        else
        {
            newPath = w.FilePath + WorkflowEntry.DisabledSuffix;
        }
        if (File.Exists(newPath)) File.Delete(newPath);
        File.Move(w.FilePath, newPath);
        w.FilePath = newPath;
    }

    public static void Delete(WorkflowEntry w)
    {
        if (File.Exists(w.FilePath)) File.Delete(w.FilePath);
    }
}
