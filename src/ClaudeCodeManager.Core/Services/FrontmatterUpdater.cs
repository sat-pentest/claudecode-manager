using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Surgical edits to a markdown file's YAML frontmatter — only the target key line is
/// added/replaced/removed, leaving every other line, spacing, and body content exactly as-is.
/// Preferred over Frontmatter round-trip (Serialize) when only one field must change and we
/// don't want to reformat description, tool list, quoting style, etc.
/// </summary>
public static class FrontmatterUpdater
{
    private const string Delimiter = "---";

    /// <summary>
    /// Set (or clear) the `model:` line in the frontmatter of the given file.
    /// Passing null or empty removes the line entirely (agent/skill inherits parent).
    /// Returns true if the file was modified, false if the requested state already matched.
    /// </summary>
    public static Task<bool> SetModelAsync(string filePath, string? newModel)
        => SetKeyAsync(filePath, "model", string.IsNullOrWhiteSpace(newModel) ? null : newModel.Trim());

    /// <summary>Generic single-key surgical setter.</summary>
    public static async Task<bool> SetKeyAsync(string filePath, string key, string? newValue)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Frontmatter file not found", filePath);
        var raw = await File.ReadAllTextAsync(filePath);
        var (updated, changed) = ReplaceKey(raw, key, newValue);
        if (!changed) return false;
        await AtomicFileWriter.WriteAsync(filePath, updated);
        return true;
    }

    /// <summary>Testable pure function — takes the raw file text and returns modified text.</summary>
    public static (string content, bool changed) ReplaceKey(string raw, string key, string? newValue)
    {
        if (string.IsNullOrEmpty(raw)) return (raw ?? "", false);

        // Preserve original line endings — detect and re-apply
        var useCrlf = raw.Contains("\r\n");
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        // Locate frontmatter block
        if (lines.Length < 2 || lines[0].TrimEnd() != Delimiter) return (raw, false);
        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == Delimiter) { endIdx = i; break; }
        }
        if (endIdx < 0) return (raw, false);

        // Find existing key line inside frontmatter block.
        // Anchor at column 0 (no leading whitespace) so nested keys like
        // `metadata.type` under a mapping block don't accidentally match — otherwise
        // SetKeyAsync would think the value is "already set" and silently no-op.
        var keyPattern = new Regex($@"^{Regex.Escape(key)}\s*:\s*(.*)$", RegexOptions.IgnoreCase);
        int existingIdx = -1;
        string existingRaw = "";
        for (int i = 1; i < endIdx; i++)
        {
            var m = keyPattern.Match(lines[i]);
            if (m.Success) { existingIdx = i; existingRaw = m.Groups[1].Value.Trim(); break; }
        }

        // Decide the write action
        bool changed;
        if (newValue is null)
        {
            if (existingIdx < 0) return (raw, false);   // already absent
            // remove the line
            var listRm = new System.Collections.Generic.List<string>(lines);
            listRm.RemoveAt(existingIdx);
            lines = listRm.ToArray();
            changed = true;
        }
        else
        {
            var formatted = $"{key}: {YamlScalarEncode(newValue)}";
            if (existingIdx >= 0)
            {
                if (existingRaw == YamlScalarEncode(newValue).Trim()) return (raw, false);   // already same
                lines[existingIdx] = formatted;
                changed = true;
            }
            else
            {
                // insert before the closing delimiter
                var listAdd = new System.Collections.Generic.List<string>(lines);
                listAdd.Insert(endIdx, formatted);
                lines = listAdd.ToArray();
                changed = true;
            }
        }

        if (!changed) return (raw, false);
        var joined = string.Join("\n", lines);
        if (useCrlf) joined = joined.Replace("\n", "\r\n");
        return (joined, true);
    }

    /// <summary>
    /// Replace only the body of a markdown file (everything after the closing '---'),
    /// leaving the entire frontmatter block byte-for-byte intact. Preserves unknown or
    /// nested keys (like the `metadata:` block Claude Code auto-adds) that a Serialize
    /// round-trip through Frontmatter would otherwise drop.
    /// </summary>
    public static async Task<bool> ReplaceBodyAsync(string filePath, string newBody)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Body target file not found", filePath);
        var raw = await File.ReadAllTextAsync(filePath);
        var (updated, changed) = ReplaceBody(raw, newBody);
        if (!changed) return false;
        await AtomicFileWriter.WriteAsync(filePath, updated);
        return true;
    }

    /// <summary>Testable pure function — takes raw text + new body, returns modified text.</summary>
    public static (string content, bool changed) ReplaceBody(string raw, string? newBody)
    {
        if (raw is null) return ("", false);

        var useCrlf = raw.Contains("\r\n");
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        // No frontmatter block → treat entire file as body
        if (lines.Length < 2 || lines[0].TrimEnd() != Delimiter)
        {
            var justBody = (newBody ?? "").Replace("\r\n", "\n");
            if (useCrlf) justBody = justBody.Replace("\n", "\r\n");
            return (justBody, !string.Equals(justBody, raw, StringComparison.Ordinal));
        }

        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == Delimiter) { endIdx = i; break; }
        }
        if (endIdx < 0) return (raw, false);   // malformed frontmatter — refuse to touch

        // Keep everything through the closing '---' verbatim, then blank line + newBody.
        var sb = new StringBuilder();
        for (int i = 0; i <= endIdx; i++)
        {
            sb.Append(lines[i]);
            sb.Append('\n');
        }
        sb.Append('\n');
        sb.Append((newBody ?? "").Replace("\r\n", "\n"));

        var result = sb.ToString();
        if (useCrlf) result = result.Replace("\n", "\r\n");
        return (result, !string.Equals(result, raw, StringComparison.Ordinal));
    }

    private static string YamlScalarEncode(string v)
    {
        // Simple scalars only — model names are always safe (alphanumeric + hyphen + brackets)
        if (v.Contains(':') || v.Contains('#') || v.Contains('"') || v.Contains('\n')
            || v.StartsWith(' ') || v.EndsWith(' '))
        {
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        }
        return v;
    }
}
