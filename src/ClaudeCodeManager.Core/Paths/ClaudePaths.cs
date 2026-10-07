using System;
using System.IO;

namespace ClaudeCodeManager.Core.Paths;

public static class ClaudePaths
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The default pentest environment — <c>~/.claude</c>.</summary>
    public static string DefaultClaudeRoot => Path.Combine(UserProfile, ".claude");

    private static string? _activeRoot;

    /// <summary>
    /// The active config directory every other path derives from. Switchable at runtime
    /// (environment switcher) — a studio env points this at <c>~/.claude-studio</c>, for
    /// example. All the properties below recompute off this, so a switch re-points the
    /// whole app at once. Changing it raises <see cref="ActiveEnvironmentChanged"/>.
    /// </summary>
    public static string ClaudeRoot
    {
        get => _activeRoot ??= DefaultClaudeRoot;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? DefaultClaudeRoot : value;
            if (string.Equals(next, _activeRoot, StringComparison.OrdinalIgnoreCase)) return;
            _activeRoot = next;
            ActiveEnvironmentChanged?.Invoke(next);
        }
    }

    /// <summary>Raised after <see cref="ClaudeRoot"/> switches to a new environment.</summary>
    public static event Action<string>? ActiveEnvironmentChanged;

    /// <summary>True when the active environment is the default pentest one.</summary>
    public static bool IsDefaultEnvironment =>
        string.Equals(ClaudeRoot, DefaultClaudeRoot, StringComparison.OrdinalIgnoreCase);
    public static string GlobalClaudeMd => Path.Combine(ClaudeRoot, "CLAUDE.md");
    public static string SettingsJson => Path.Combine(ClaudeRoot, "settings.json");
    public static string LocalSettingsJson => Path.Combine(ClaudeRoot, "settings.local.json");
    public static string KeybindingsJson => Path.Combine(ClaudeRoot, "keybindings.json");
    public static string SkillsRoot => Path.Combine(ClaudeRoot, "skills");
    public static string AgentsRoot => Path.Combine(ClaudeRoot, "agents");
    public static string ProjectsRoot => Path.Combine(ClaudeRoot, "projects");

    public static string ManagerRoot => Path.Combine(UserProfile, ".claude-control");
    public static string SnapshotsRoot => Path.Combine(ManagerRoot, "snapshots");
    public static string LogsRoot => Path.Combine(ManagerRoot, "logs");
    public static string ConfigFile => Path.Combine(ManagerRoot, "config.json");

    public static void EnsureManagerDirs()
    {
        Directory.CreateDirectory(SnapshotsRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public static bool ClaudeRootExists() => Directory.Exists(ClaudeRoot);
}
