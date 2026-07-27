using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Manages the redteam-safety engagement (arm/disarm/kill-switch), the mitmproxy
/// L1 enforcement process, and reads the hook audit log for the SAFETY UI.
///
/// Backing store is the same active-engagement.json / STOP file that arm.ps1 and
/// hook_scope_guard.ps1 use — CCM's UI and CLI can be used interchangeably.
/// </summary>
public sealed class EngagementSafetyService
{
    public static readonly string SafetyRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".claude", "redteam-safety");
    public static string ManifestPath => Path.Combine(SafetyRoot, "active-engagement.json");
    public static string StopFile     => Path.Combine(SafetyRoot, "STOP");
    public static string AuditLog     => Path.Combine(SafetyRoot, "logs", "hook-audit.log");
    public static string MitmProxyAddon => Path.Combine(SafetyRoot, "scope_guard.py");
    public static string PresetsDir => Path.Combine(SafetyRoot, "presets");
    public const int MitmProxyPort = 8888;

    public bool SafetyLayerInstalled => Directory.Exists(SafetyRoot) && File.Exists(ManifestPath);

    // ─── Manifest ─────────────────────────────────────────────────────────

    public EngagementManifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            var raw = File.ReadAllText(ManifestPath);
            var node = JsonNode.Parse(raw);
            if (node is not JsonObject obj) return null;
            return new EngagementManifest
            {
                Raw = obj,
                RawText = raw,
                Armed = obj["armed"]?.GetValue<bool>() ?? false,
                EngagementId = obj["engagementId"]?.GetValue<string>() ?? "",
                Target = obj["target"]?.GetValue<string>() ?? "",
                Environment = obj["environment"]?.GetValue<string>() ?? "",
                EnforceMode = obj["enforceMode"]?.GetValue<string>() ?? "strict",
                Aggression = obj["aggression"]?.GetValue<string>() ?? "",
                ScopeInclude = ReadStringList(obj, "scopeInclude"),
                ScopeExclude = ReadStringList(obj, "scopeExclude"),
                InfraAllow   = ReadStringList(obj, "infraAllow"),
                ForbiddenReadGlobs = ReadStringList(obj, "forbiddenReadGlobs"),
                PerHostRps = ReadDouble(obj, "rate", "perHostRps"),
                GlobalConcurrency = ReadInt(obj, "rate", "globalConcurrency"),
                CwdRoot = obj["cwdRoot"]?.GetValue<string>() ?? "",
                KillSwitchFile = obj["killSwitchFile"]?.GetValue<string>() ?? StopFile,
                LogFile = obj["logFile"]?.GetValue<string>() ?? AuditLog
            };
        }
        catch { return null; }
    }

    private static List<string> ReadStringList(JsonObject obj, string key)
    {
        var arr = obj[key] as JsonArray;
        if (arr is null) return new List<string>();
        return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
    }
    private static double ReadDouble(JsonObject obj, string parent, string key)
    {
        if (obj[parent] is JsonObject p && p[key] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return 0;
    }
    private static int ReadInt(JsonObject obj, string parent, string key)
    {
        if (obj[parent] is JsonObject p && p[key] is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        return 0;
    }

    /// <summary>Apply a partial edit to the manifest and persist. Preserves unknown keys.</summary>
    public void UpdateManifest(Action<JsonObject> mutator)
    {
        if (!File.Exists(ManifestPath))
            throw new FileNotFoundException("active-engagement.json not found. Install redteam-safety layer first.");
        var raw = File.ReadAllText(ManifestPath);
        var node = JsonNode.Parse(raw);
        if (node is not JsonObject obj)
            throw new InvalidDataException("active-engagement.json malformed");
        mutator(obj);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ManifestPath, obj.ToJsonString(options));
    }

    // ─── ARM / DISARM ─────────────────────────────────────────────────────

    public void Arm() => UpdateManifest(o => o["armed"] = true);
    public void Disarm() => UpdateManifest(o => o["armed"] = false);

    // ─── KILL SWITCH ──────────────────────────────────────────────────────

    public bool IsKillSwitchActive() => File.Exists(StopFile);

    public void CreateKillSwitch()
    {
        Directory.CreateDirectory(SafetyRoot);
        File.WriteAllText(StopFile, $"created by CCM at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
    }

    public void RemoveKillSwitch()
    {
        if (File.Exists(StopFile)) File.Delete(StopFile);
    }

    // ─── MITMPROXY PROCESS ────────────────────────────────────────────────

    public bool IsMitmproxyRunning() => GetMitmproxyProcess() is not null;

    public Process? GetMitmproxyProcess()
    {
        try
        {
            foreach (var name in new[] { "mitmdump", "mitmproxy", "mitmweb" })
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited) return p;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Start mitmproxy with our scope_guard addon. Returns started process (or null if failed).</summary>
    public (Process? proc, string? error) StartMitmproxy()
    {
        if (!File.Exists(MitmProxyAddon))
            return (null, "scope_guard.py not found at " + MitmProxyAddon);
        var existing = GetMitmproxyProcess();
        if (existing is not null) return (existing, null);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "mitmdump",
                Arguments = $"-s \"{MitmProxyAddon}\" --listen-port {MitmProxyPort}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = SafetyRoot
            };
            var p = Process.Start(psi);
            return (p, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public void StopMitmproxy()
    {
        try
        {
            var p = GetMitmproxyProcess();
            if (p is null) return;
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(5000); } catch { }
        }
        catch { }
    }

    // ─── HTTP_PROXY ENVIRONMENT (user scope) ──────────────────────────────

    private const string ProxyUrl = "http://127.0.0.1:8888";

    public bool IsProxyEnvSet()
    {
        var h  = Environment.GetEnvironmentVariable("HTTP_PROXY",  EnvironmentVariableTarget.User);
        var hs = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User);
        return string.Equals(h,  ProxyUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hs, ProxyUrl, StringComparison.OrdinalIgnoreCase);
    }

    public void SetProxyEnv()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY",  ProxyUrl, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", ProxyUrl, EnvironmentVariableTarget.User);
    }

    public void ClearProxyEnv()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY",  null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.User);
    }

    // ─── HOOK AUDIT LOG ───────────────────────────────────────────────────

    /// <summary>
    /// Archive current audit log to logs/archive/hook-audit-YYYYMMDD-HHmmss.log
    /// and create a fresh empty log. Called when starting a new engagement to
    /// prevent cross-engagement event contamination in the LIVE view.
    /// </summary>
    public (bool archived, string? archivePath, string? error) ArchiveAuditLog()
    {
        try
        {
            if (!File.Exists(AuditLog)) return (false, null, "no log to archive");
            var logsDir = Path.GetDirectoryName(AuditLog) ?? SafetyRoot;
            var archiveDir = Path.Combine(logsDir, "archive");
            Directory.CreateDirectory(archiveDir);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var archivePath = Path.Combine(archiveDir, $"hook-audit-{ts}.log");
            // Ensure unique name if collision
            int c = 1;
            while (File.Exists(archivePath))
            {
                archivePath = Path.Combine(archiveDir, $"hook-audit-{ts}-{c}.log");
                c++;
            }
            File.Move(AuditLog, archivePath);
            // Create fresh empty log so hook can continue writing
            using (File.Create(AuditLog)) { }
            return (true, archivePath, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public List<HookAuditEntry> ReadRecentBlocks(int max = 30)
    {
        var result = new List<HookAuditEntry>();
        if (!File.Exists(AuditLog)) return result;
        try
        {
            // Read tail; expected format per hook is JSON per line OR key=val text.
            // We handle both defensively.
            using var stream = new FileStream(AuditLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            foreach (var l in lines.Skip(Math.Max(0, lines.Count - max)))
            {
                var e = ParseHookEntry(l);
                if (e is not null) result.Add(e);
            }
        }
        catch { }
        return result;
    }

    private static HookAuditEntry? ParseHookEntry(string line)
    {
        // Try JSON first
        try
        {
            var node = JsonNode.Parse(line);
            if (node is JsonObject obj)
            {
                return new HookAuditEntry
                {
                    Timestamp = obj["ts"]?.GetValue<string>()
                                ?? obj["time"]?.GetValue<string>()
                                ?? "",
                    Decision  = obj["decision"]?.GetValue<string>()
                                ?? obj["action"]?.GetValue<string>()
                                ?? "",
                    Reason    = obj["reason"]?.GetValue<string>()
                                ?? obj["rule"]?.GetValue<string>()
                                ?? "",
                    Command   = obj["command"]?.GetValue<string>()
                                ?? obj["cmd"]?.GetValue<string>()
                                ?? "",
                    Raw = line
                };
            }
        }
        catch { }
        // Fallback: plain text
        return new HookAuditEntry { Raw = line, Timestamp = "", Decision = "", Reason = "", Command = line };
    }

    // ─── PRESETS ──────────────────────────────────────────────────────────

    public List<PresetInfo> ListPresets()
    {
        var result = new List<PresetInfo>();
        if (!Directory.Exists(PresetsDir)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var info = new PresetInfo { Name = name, FilePath = file };
                try
                {
                    var raw = File.ReadAllText(file);
                    var node = JsonNode.Parse(raw);
                    if (node is JsonObject obj)
                    {
                        info.EngagementId = obj["engagementId"]?.GetValue<string>() ?? "";
                        info.Target = obj["target"]?.GetValue<string>() ?? "";
                        info.Environment = obj["environment"]?.GetValue<string>() ?? "";
                        info.ScopeIncludeCount = (obj["scopeInclude"] as JsonArray)?.Count ?? 0;
                        info.ScopeExcludeCount = (obj["scopeExclude"] as JsonArray)?.Count ?? 0;
                    }
                }
                catch { }
                result.Add(info);
            }
        }
        catch { }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Load preset into active-engagement.json. Preserves current 'armed' state
    /// so loading a preset doesn't accidentally arm/disarm.
    /// </summary>
    public void LoadPreset(string name)
    {
        var path = Path.Combine(PresetsDir, name + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Preset not found: " + name, path);

        // Read preset
        var presetRaw = File.ReadAllText(path);
        var presetNode = JsonNode.Parse(presetRaw);
        if (presetNode is not JsonObject presetObj)
            throw new InvalidDataException("Preset malformed: " + name);

        // Preserve current armed state
        bool currentArmed = false;
        if (File.Exists(ManifestPath))
        {
            try
            {
                var curr = JsonNode.Parse(File.ReadAllText(ManifestPath));
                if (curr is JsonObject co && co["armed"] is JsonValue av && av.TryGetValue<bool>(out var b))
                    currentArmed = b;
            }
            catch { }
        }
        presetObj["armed"] = currentArmed;

        Directory.CreateDirectory(SafetyRoot);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ManifestPath, presetObj.ToJsonString(options));
    }

    /// <summary>
    /// Save current manifest as a preset. armed=false is forced in saved preset
    /// (presets are scope templates, not armed state snapshots).
    /// </summary>
    public void SaveAsPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Preset name required");
        if (!File.Exists(ManifestPath))
            throw new FileNotFoundException("No active manifest to save");

        var raw = File.ReadAllText(ManifestPath);
        var node = JsonNode.Parse(raw);
        if (node is not JsonObject obj)
            throw new InvalidDataException("Active manifest malformed");
        // Strip armed state — presets are templates
        obj["armed"] = false;

        Directory.CreateDirectory(PresetsDir);
        // Sanitize filename
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        if (string.IsNullOrEmpty(safe)) throw new ArgumentException("Invalid preset name");
        var path = Path.Combine(PresetsDir, safe + ".json");

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, obj.ToJsonString(options));
    }

    public void DeletePreset(string name)
    {
        var path = Path.Combine(PresetsDir, name + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Detect if any preset matches current manifest scope (by scopeInclude equality).</summary>
    public string? DetectActivePreset()
    {
        if (!File.Exists(ManifestPath) || !Directory.Exists(PresetsDir)) return null;
        try
        {
            var currInc = ReadStringList((JsonNode.Parse(File.ReadAllText(ManifestPath)) as JsonObject) ?? new JsonObject(), "scopeInclude");
            var currSet = new HashSet<string>(currInc, StringComparer.OrdinalIgnoreCase);
            foreach (var p in ListPresets())
            {
                try
                {
                    var pObj = JsonNode.Parse(File.ReadAllText(p.FilePath)) as JsonObject;
                    if (pObj is null) continue;
                    var pInc = ReadStringList(pObj, "scopeInclude");
                    if (pInc.Count == currSet.Count && pInc.All(x => currSet.Contains(x))) return p.Name;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ─── SUMMARY ──────────────────────────────────────────────────────────

    public SafetyStatus GetStatus()
    {
        var m = ReadManifest();
        return new SafetyStatus
        {
            Installed = SafetyLayerInstalled,
            Armed = m?.Armed ?? false,
            KillSwitchActive = IsKillSwitchActive(),
            MitmProxyRunning = IsMitmproxyRunning(),
            ProxyEnvSet = IsProxyEnvSet(),
            EngagementId = m?.EngagementId ?? "",
            Target = m?.Target ?? "",
            Environment = m?.Environment ?? "",
            EnforceMode = m?.EnforceMode ?? ""
        };
    }
}

public sealed class EngagementManifest
{
    public JsonObject Raw { get; init; } = new();
    public string RawText { get; init; } = "";
    public bool Armed { get; set; }
    public string EngagementId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Environment { get; set; } = "";
    public string EnforceMode { get; set; } = "";
    public string Aggression { get; set; } = "";
    public List<string> ScopeInclude { get; set; } = new();
    public List<string> ScopeExclude { get; set; } = new();
    public List<string> InfraAllow { get; set; } = new();
    public List<string> ForbiddenReadGlobs { get; set; } = new();
    public double PerHostRps { get; set; }
    public int GlobalConcurrency { get; set; }
    public string CwdRoot { get; set; } = "";
    public string KillSwitchFile { get; set; } = "";
    public string LogFile { get; set; } = "";
}

public sealed class SafetyStatus
{
    public bool Installed { get; set; }
    public bool Armed { get; set; }
    public bool KillSwitchActive { get; set; }
    public bool MitmProxyRunning { get; set; }
    public bool ProxyEnvSet { get; set; }
    public string EngagementId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Environment { get; set; } = "";
    public string EnforceMode { get; set; } = "";
}

public sealed class PresetInfo
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string EngagementId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Environment { get; set; } = "";
    public int ScopeIncludeCount { get; set; }
    public int ScopeExcludeCount { get; set; }
    public string DisplayLabel => string.IsNullOrEmpty(EngagementId) ? Name : $"{Name}  ·  {EngagementId}";
}

public sealed class HookAuditEntry
{
    public string Timestamp { get; set; } = "";
    public string Decision { get; set; } = "";     // DENY / ALLOW / …
    public string Reason { get; set; } = "";
    public string Command { get; set; } = "";
    public string Raw { get; set; } = "";
    public bool IsDeny => Decision.Equals("DENY", StringComparison.OrdinalIgnoreCase)
                       || Decision.Equals("BLOCK", StringComparison.OrdinalIgnoreCase);
}
