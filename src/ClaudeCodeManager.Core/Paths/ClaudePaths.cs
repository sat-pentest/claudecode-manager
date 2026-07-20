using System;
using System.IO;

namespace ClaudeCodeManager.Core.Paths;

public static class ClaudePaths
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ClaudeRoot => Path.Combine(UserProfile, ".claude");
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
