using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>One Claude Code config environment — a <c>~/.claude*</c> directory.</summary>
public sealed record ClaudeEnvironment(string Name, string Path, bool IsDefault)
{
    /// <summary>Env var value a launched session uses to load this environment.</summary>
    public string ConfigDirValue => Path;
}

/// <summary>
/// Discovers and switches between Claude config environments living side by side under the
/// user profile: the default pentest <c>~/.claude</c> plus any <c>~/.claude-&lt;name&gt;</c>
/// (e.g. <c>~/.claude-studio</c>). CCM's own <c>~/.claude-control</c> is never listed.
///
/// Switching sets <see cref="ClaudePaths.ClaudeRoot"/>, which re-points every derived path, so
/// all modules reload against the selected environment. The choice persists across restarts.
/// </summary>
public static class EnvironmentRegistry
{
    private static string ActiveMarkerFile => System.IO.Path.Combine(ClaudePaths.ManagerRoot, "active-environment.txt");

    /// <summary>All environments found under the user profile, default first.</summary>
    public static IReadOnlyList<ClaudeEnvironment> Discover()
    {
        var home = ClaudePaths.UserProfile;
        var list = new List<ClaudeEnvironment>();

        var def = ClaudePaths.DefaultClaudeRoot;
        if (Directory.Exists(def))
            list.Add(new ClaudeEnvironment("PENTEST", def, IsDefault: true));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in SafeEnumerate(home, ".claude-*"))
        {
            // TrimEnd tolerates a stray trailing space in the folder name — a bogus ".claude-studio "
            // could otherwise show up as a second "STUDIO".
            var leaf = System.IO.Path.GetFileName(dir).TrimEnd();
            if (leaf.Equals(".claude-control", StringComparison.OrdinalIgnoreCase)) continue; // CCM's own state
            var suffix = leaf.Substring(".claude-".Length).Trim();
            if (suffix.Length == 0) continue;
            var name = suffix.ToUpperInvariant();
            if (!seen.Add(name)) continue; // dedup (e.g. the trailing-space twin of an existing env)
            list.Add(new ClaudeEnvironment(name, dir, IsDefault: false));
        }

        if (list.Count == 0) // nothing on disk yet — still surface the default
            list.Add(new ClaudeEnvironment("PENTEST", def, IsDefault: true));

        return list;
    }

    /// <summary>The currently active environment (matches <see cref="ClaudePaths.ClaudeRoot"/>).</summary>
    public static ClaudeEnvironment Active
    {
        get
        {
            var root = ClaudePaths.ClaudeRoot;
            return Discover().FirstOrDefault(e => string.Equals(e.Path, root, StringComparison.OrdinalIgnoreCase))
                   ?? new ClaudeEnvironment("PENTEST", ClaudePaths.DefaultClaudeRoot, IsDefault: true);
        }
    }

    /// <summary>
    /// The folder a launched session opens in for <paramref name="env"/>. Resolution order:
    ///   1. a <c>workdir.txt</c> in the env's config dir (explicit override),
    ///   2. by convention <c>&lt;systemdrive&gt;\&lt;name&gt;</c> for a non-default env (STUDIO → C:\studio),
    ///   3. the user profile as a last resort.
    /// Without this a studio session opened in <c>~</c> (home), which is cluttered and carries the
    /// pentest project's files.
    /// </summary>
    public static string ResolveWorkDir(ClaudeEnvironment env)
    {
        try
        {
            var marker = System.IO.Path.Combine(env.Path, "workdir.txt");
            if (File.Exists(marker))
            {
                var p = File.ReadAllText(marker).Trim();
                if (p.Length > 0 && Directory.Exists(p)) return p;
            }
            if (!env.IsDefault)
            {
                var drive = System.IO.Path.GetPathRoot(ClaudePaths.UserProfile) ?? "C:\\";
                var conv = System.IO.Path.Combine(drive, env.Name.ToLowerInvariant());
                if (Directory.Exists(conv)) return conv;
            }
        }
        catch { /* fall through to default */ }
        return ClaudePaths.UserProfile;
    }

    /// <summary>Switch the active environment. Re-points <see cref="ClaudePaths.ClaudeRoot"/> and persists.</summary>
    public static void SetActive(ClaudeEnvironment env)
    {
        if (env is null) return;
        ClaudePaths.ClaudeRoot = env.Path;
        Persist(env.Path);
    }

    /// <summary>Restore the environment chosen last run (called once at startup). Default if none/missing.</summary>
    public static void RestorePersisted()
    {
        try
        {
            if (!File.Exists(ActiveMarkerFile)) return;
            var saved = File.ReadAllText(ActiveMarkerFile).Trim();
            if (saved.Length > 0 && Directory.Exists(saved))
                ClaudePaths.ClaudeRoot = saved;
        }
        catch { /* best-effort — fall back to default */ }
    }

    private static void Persist(string path)
    {
        try
        {
            ClaudePaths.EnsureManagerDirs();
            Directory.CreateDirectory(ClaudePaths.ManagerRoot);
            File.WriteAllText(ActiveMarkerFile, path);
        }
        catch { /* non-fatal */ }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern); }
        catch { return Enumerable.Empty<string>(); }
    }
}
