using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.Core.Services;

public static class ClaudeMdProfileManager
{
    // Matches CLAUDE.<name>.md — captures name group
    private static readonly Regex ProfileRegex = new(@"^CLAUDE\.(?<name>[A-Za-z0-9_-]+)\.md$", RegexOptions.Compiled);
    private const string ActiveFileName = "CLAUDE.md";
    // Names Claude Code treats specially — reserved from user-defined profile names
    private static readonly HashSet<string> ReservedNames =
        new(new[] { "local" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// List all CLAUDE.md profiles in the given directory (including the active CLAUDE.md if present).
    /// The active file is returned first with IsActive=true.
    /// </summary>
    public static List<ClaudeMdProfile> List(string dir)
    {
        var result = new List<ClaudeMdProfile>();
        if (!Directory.Exists(dir)) return result;

        var activePath = Path.Combine(dir, ActiveFileName);
        if (File.Exists(activePath))
        {
            var fi = new FileInfo(activePath);
            result.Add(new ClaudeMdProfile
            {
                FilePath = activePath,
                Name = "(active)",
                IsActive = true,
                Size = fi.Length,
                ModifiedAt = fi.LastWriteTime,
                IsReservedName = false
            });
        }

        foreach (var f in Directory.EnumerateFiles(dir, "CLAUDE.*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(f);
            var m = ProfileRegex.Match(name);
            if (!m.Success) continue;
            var profileName = m.Groups["name"].Value;
            var fi = new FileInfo(f);
            result.Add(new ClaudeMdProfile
            {
                FilePath = f,
                Name = profileName,
                IsActive = false,
                Size = fi.Length,
                ModifiedAt = fi.LastWriteTime,
                IsReservedName = ReservedNames.Contains(profileName)
            });
        }

        return result
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsValidName(string? name, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(name)) { error = "profile name is empty"; return false; }
        var trimmed = name.Trim();
        if (!Regex.IsMatch(trimmed, "^[A-Za-z0-9_-]+$")) { error = "only letters/digits/underscore/hyphen allowed"; return false; }
        if (ReservedNames.Contains(trimmed)) { error = $"'{trimmed}' is reserved (Claude Code special file)"; return false; }
        return true;
    }

    /// <summary>Copy current active CLAUDE.md content to CLAUDE.&lt;name&gt;.md. Overwrites if exists.</summary>
    public static async Task SaveAsProfileAsync(string dir, string name)
    {
        if (!IsValidName(name, out var err)) throw new ArgumentException(err, nameof(name));
        var active = Path.Combine(dir, ActiveFileName);
        if (!File.Exists(active)) throw new FileNotFoundException("No active CLAUDE.md to snapshot", active);
        var target = Path.Combine(dir, $"CLAUDE.{name}.md");
        var content = await File.ReadAllTextAsync(active);
        await AtomicFileWriter.WriteAsync(target, content);
    }

    public sealed class ActivateResult
    {
        public string? PreservedAsProfile { get; set; }   // set if current active was renamed to auto profile
        public string? MatchedExistingProfile { get; set; } // set if current active content matched an existing profile (no backup needed)
    }

    /// <summary>
    /// Activate a profile: preserve current active CLAUDE.md as an auto-named profile (unless
    /// its content already matches an existing profile), then swap the target profile into
    /// the active slot as a copy.
    /// </summary>
    public static async Task<ActivateResult> ActivateAsync(ClaudeMdProfile profile)
    {
        if (profile.IsActive) return new ActivateResult();
        var dir = Path.GetDirectoryName(profile.FilePath) ?? throw new InvalidOperationException("profile has no directory");
        var active = Path.Combine(dir, ActiveFileName);
        var result = new ActivateResult();

        // 1) Preserve current active
        if (File.Exists(active))
        {
            var currentContent = await File.ReadAllTextAsync(active);
            var currentSize = new FileInfo(active).Length;

            // Cheap match: same size profile whose full content is byte-equal
            var existing = List(dir).Where(p => !p.IsActive && p.Size == currentSize);
            string? matchedName = null;
            foreach (var p in existing)
            {
                var content = await File.ReadAllTextAsync(p.FilePath);
                if (content == currentContent) { matchedName = p.Name; break; }
            }

            if (matchedName is not null)
            {
                result.MatchedExistingProfile = matchedName;
                // Content already preserved elsewhere — just remove active before overwrite
                File.Delete(active);
            }
            else
            {
                // Rename active to autosave-<timestamp> to preserve content
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var autoName = $"autosave-{stamp}";
                var backupPath = Path.Combine(dir, $"CLAUDE.{autoName}.md");
                int suffix = 1;
                while (File.Exists(backupPath))
                {
                    backupPath = Path.Combine(dir, $"CLAUDE.{autoName}-{suffix}.md");
                    suffix++;
                }
                File.Move(active, backupPath);
                result.PreservedAsProfile = Path.GetFileNameWithoutExtension(backupPath).Substring("CLAUDE.".Length);
            }
        }

        // 2) Copy target profile to active (profile file remains intact)
        var profileContent = await File.ReadAllTextAsync(profile.FilePath);
        await AtomicFileWriter.WriteAsync(active, profileContent);
        return result;
    }

    public static void Delete(ClaudeMdProfile profile)
    {
        if (profile.IsActive) throw new InvalidOperationException("cannot delete active CLAUDE.md via profile manager");
        if (File.Exists(profile.FilePath)) File.Delete(profile.FilePath);
    }

    /// <summary>Rename profile file from CLAUDE.&lt;old&gt;.md to CLAUDE.&lt;new&gt;.md.</summary>
    public static void Rename(ClaudeMdProfile profile, string newName)
    {
        if (profile.IsActive) throw new InvalidOperationException("cannot rename the active CLAUDE.md");
        if (!IsValidName(newName, out var err)) throw new ArgumentException(err, nameof(newName));
        var dir = Path.GetDirectoryName(profile.FilePath);
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("profile has no directory");
        if (string.Equals(profile.Name, newName, StringComparison.Ordinal)) return;
        var newPath = Path.Combine(dir, $"CLAUDE.{newName}.md");
        if (File.Exists(newPath)) throw new IOException($"Target already exists: CLAUDE.{newName}.md");
        File.Move(profile.FilePath, newPath);
        profile.FilePath = newPath;
        profile.Name = newName;
    }
}
