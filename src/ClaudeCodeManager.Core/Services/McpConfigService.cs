using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public enum McpTransport { Stdio, Sse, Http, Unknown }
public enum McpScope { UserGlobal, ProjectScoped, OAuthConnector }
public enum McpConnectionStatus { Checking, Online, Offline, OAuth, Disabled, Unknown }

public sealed class McpServerEntry
{
    public string Name { get; set; } = "";
    public McpTransport Transport { get; set; }
    public McpScope Scope { get; set; }
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Url { get; set; }
    public string? Workspace { get; set; }        // project-scoped 인 경우 workspace 경로
    public string? SourceFile { get; set; }
    public bool EnabledInSettings { get; set; }   // settings.json enabledMcpjsonServers 리스트에 있는지
    public bool NeedsAuth { get; set; }
    public string? AuthCacheId { get; set; }
    public DateTime? AuthCacheTimestamp { get; set; }
}

public sealed class McpConfigSummary
{
    public List<McpServerEntry> Servers { get; set; } = new();
    public int StdioCount => Servers.Count(s => s.Transport == McpTransport.Stdio);
    public int RemoteCount => Servers.Count(s => s.Transport == McpTransport.Sse || s.Transport == McpTransport.Http);
    public int NeedsAuthCount => Servers.Count(s => s.NeedsAuth);
    public int TotalCount => Servers.Count;
    // "Enabled/online" = active for the workspace: either explicitly listed in
    // enabledMcpjsonServers (global) OR project-scoped (always active for its project).
    // Auth-required entries are their own bucket, not counted here.
    public int EnabledCount => Servers.Count(s => !s.NeedsAuth && (s.EnabledInSettings || s.Scope == McpScope.ProjectScoped));
    public List<string> EnabledInSettings { get; set; } = new();
}

public static class McpConfigService
{
    private static string GlobalMcpJson => Path.Combine(ClaudePaths.ClaudeRoot, "mcp.json");
    private static string GlobalClaudeJson => Path.Combine(ClaudePaths.UserProfile, ".claude.json");
    private static string AuthCacheJson => Path.Combine(ClaudePaths.ClaudeRoot, "mcp-needs-auth-cache.json");

    /// <summary>
    /// Aggregates MCP server configuration from every known location:
    ///   - ~/.claude/mcp.json                            (legacy global user-scope)
    ///   - ~/.claude.json → mcpServers                   (global)
    ///   - ~/.claude.json → projects.&lt;path&gt;.mcpServers  (project-scoped, per workspace)
    ///   - ~/.claude/settings.json → enabledMcpjsonServers (enable list)
    ///   - ~/.claude/mcp-needs-auth-cache.json           (OAuth-required claude.ai connectors)
    /// </summary>
    private static readonly SignatureCache<McpConfigSummary> ScanCacheSlot = new();

    /// <summary>Memoized over the three config files it reads. Called on every DASHBOARD,
    /// HARNESS and MCP activation, plus by the host security scanner.</summary>
    public static McpConfigSummary Scan()
        => ScanCacheSlot.Get(
            () => FileSignature.Of(GlobalMcpJson, GlobalClaudeJson, ClaudePaths.SettingsJson),
            ScanUncached);

    /// <summary>Drops the memoized scan. Needed because ToggleEnabledAsync writes settings.json
    /// through a path that may land inside the same filesystem timestamp tick as the read that
    /// produced the current signature.</summary>
    public static void InvalidateScanCache() => ScanCacheSlot.Invalidate();

    public static McpConfigSummary ScanUncached()
    {
        var summary = new McpConfigSummary();
        var seen = new Dictionary<string, McpServerEntry>(StringComparer.OrdinalIgnoreCase);

        // 1) Legacy ~/.claude/mcp.json
        LoadFromMcpJson(GlobalMcpJson, McpScope.UserGlobal, workspace: null, seen);

        // 2) ~/.claude.json — global mcpServers + per-project mcpServers
        LoadFromClaudeJson(GlobalClaudeJson, seen);

        // 3) enabledMcpjsonServers list
        try
        {
            var bundle = SettingsService.Load(ClaudePaths.SettingsJson);
            if (bundle.Root["enabledMcpjsonServers"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    {
                        summary.EnabledInSettings.Add(s);
                        if (seen.TryGetValue(s, out var entry))
                            entry.EnabledInSettings = true;
                    }
                }
            }
        }
        catch { /* settings read failure — not fatal */ }

        // 4) OAuth-needs-auth cache (claude.ai 1st-party connectors) — INTENTIONALLY SKIPPED.
        //    These are managed by claude.ai / Claude Desktop's Connectors UI (OAuth flow),
        //    and cannot be enabled/disabled or otherwise acted on from this app. Showing them
        //    only clutters the MCP list. If you ever need to inspect them, read
        //    ~/.claude/mcp-needs-auth-cache.json directly.

        summary.Servers = seen.Values
            .OrderBy(s => s.NeedsAuth ? 1 : 0)              // real installs first
            .ThenBy(s => (int)s.Scope)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return summary;
    }

    private static void LoadFromMcpJson(string path, McpScope scope, string? workspace, Dictionary<string, McpServerEntry> seen)
    {
        try
        {
            if (!File.Exists(path)) return;
            var root = JsonNode.Parse(File.ReadAllText(path));
            var servers = root?["mcpServers"] as JsonObject;
            if (servers is null) return;
            foreach (var kv in servers)
            {
                if (kv.Value is not JsonObject cfg) continue;
                var entry = BuildEntry(kv.Key, cfg, scope, workspace, path);
                // merge — later reads override earlier (project-scoped wins over global if same name in same workspace)
                seen[MergeKey(entry)] = entry;
            }
        }
        catch { /* non-fatal */ }
    }

    private static void LoadFromClaudeJson(string path, Dictionary<string, McpServerEntry> seen)
    {
        try
        {
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var root = JsonNode.Parse(text);
            if (root is not JsonObject obj) return;

            // Global mcpServers
            if (obj["mcpServers"] is JsonObject globalServers)
            {
                foreach (var kv in globalServers)
                {
                    if (kv.Value is not JsonObject cfg) continue;
                    var entry = BuildEntry(kv.Key, cfg, McpScope.UserGlobal, workspace: null, path);
                    seen[MergeKey(entry)] = entry;
                }
            }

            // Per-project mcpServers
            if (obj["projects"] is JsonObject projects)
            {
                foreach (var pkv in projects)
                {
                    var workspacePath = pkv.Key;
                    if (pkv.Value is not JsonObject pobj) continue;
                    if (pobj["mcpServers"] is not JsonObject projectServers) continue;
                    foreach (var kv in projectServers)
                    {
                        if (kv.Value is not JsonObject cfg) continue;
                        var entry = BuildEntry(kv.Key, cfg, McpScope.ProjectScoped, workspacePath, path);
                        seen[MergeKey(entry)] = entry;
                    }
                }
            }
        }
        catch { /* non-fatal */ }
    }

    private static string MergeKey(McpServerEntry e) =>
        e.Scope == McpScope.ProjectScoped
            ? $"{e.Workspace}::{e.Name}"
            : e.Name;

    private static McpServerEntry BuildEntry(string name, JsonObject cfg, McpScope scope, string? workspace, string sourceFile)
    {
        var entry = new McpServerEntry
        {
            Name = name,
            Scope = scope,
            Workspace = workspace,
            SourceFile = sourceFile,
        };

        // Transport type
        var typeStr = cfg["type"]?.GetValue<string>()?.ToLowerInvariant();
        entry.Transport = typeStr switch
        {
            "stdio" => McpTransport.Stdio,
            "sse" => McpTransport.Sse,
            "http" or "streamable-http" => McpTransport.Http,
            _ => cfg["command"] is not null ? McpTransport.Stdio
               : cfg["url"] is not null ? McpTransport.Sse
               : McpTransport.Unknown
        };

        if (cfg["command"] is JsonValue cmdv && cmdv.TryGetValue<string>(out var cmd)) entry.Command = cmd;
        if (cfg["url"] is JsonValue urlv && urlv.TryGetValue<string>(out var url)) entry.Url = url;

        if (cfg["args"] is JsonArray argsArr)
        {
            foreach (var n in argsArr)
                if (n is JsonValue av && av.TryGetValue<string>(out var s)) entry.Args.Add(s);
        }

        if (cfg["env"] is JsonObject envObj)
        {
            foreach (var kv in envObj)
            {
                if (kv.Value is JsonValue vv && vv.TryGetValue<string>(out var s))
                    entry.Env[kv.Key] = s;
            }
        }

        return entry;
    }

    /// <summary>
    /// Probe whether the MCP server is reachable/available. Best-effort — for stdio this
    /// only verifies the referenced script/command exists (MCP stdio spawns on demand);
    /// for SSE/HTTP it does a TCP connect with a short timeout; for OAuth connectors it
    /// returns OAuth (real reachability requires the claude.ai token flow).
    /// </summary>
    /// <summary>
    /// Probe results, keyed by what the probe actually depends on. A probe costs a `where.exe`
    /// spawn per stdio server (800 ms cap) or a TCP connect per SSE/HTTP server (1500 ms cap) —
    /// and an endpoint that is *down* always pays the full timeout. HARNESS re-probed everything
    /// on every activation, so bouncing in and out of that menu piled up work and made each visit
    /// slower than the last (measured: 668 ms -> 3722 ms -> 4077 ms).
    ///
    /// The TTL is short on purpose: this is liveness, and a stale "ONLINE" dot is worse than a
    /// slightly delayed one. 15 s covers menu bouncing without hiding a server that just died.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, McpConnectionStatus Status)> ProbeResults = new();
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(15);

    private static string ProbeKey(McpServerEntry e)
        => string.Join("", e.Name, e.Transport, e.Url ?? "", e.Command ?? "",
                       string.Join(" ", e.Args), e.Scope, e.EnabledInSettings, e.NeedsAuth);

    /// <summary>Forget cached liveness so the next probe really goes out to the network.</summary>
    public static void InvalidateProbeCache() => ProbeResults.Clear();

    public static async Task<McpConnectionStatus> ProbeAsync(McpServerEntry entry, CancellationToken ct = default)
    {
        var key = ProbeKey(entry);
        if (ProbeResults.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ProbeTtl)
            return hit.Status;

        var status = await ProbeUncachedAsync(entry, ct);
        // Never cache a cancelled probe: it says nothing about the server, only that the user
        // navigated away mid-flight.
        if (!ct.IsCancellationRequested)
            ProbeResults[key] = (DateTime.UtcNow, status);
        return status;
    }

    private static async Task<McpConnectionStatus> ProbeUncachedAsync(McpServerEntry entry, CancellationToken ct = default)
    {
        if (entry.NeedsAuth) return McpConnectionStatus.OAuth;

        // Enable-state gate: file-scoped globals (from ~/.claude/mcp.json) need to be listed in
        // enabledMcpjsonServers. Project-scoped ones (~/.claude.json → projects.<path>.mcpServers)
        // are always active for their project. Show unlisted globals as DISABLED regardless of
        // whether the underlying executable/URL is reachable — "reachable but off" is misleading.
        if (entry.Scope == McpScope.UserGlobal && !entry.EnabledInSettings)
            return McpConnectionStatus.Disabled;

        if (entry.Transport == McpTransport.Sse || entry.Transport == McpTransport.Http)
        {
            if (string.IsNullOrWhiteSpace(entry.Url)) return McpConnectionStatus.Offline;
            try
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)) return McpConnectionStatus.Offline;
                var host = uri.Host;
                var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                using var tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(host, port, cts.Token);
                    return tcp.Connected ? McpConnectionStatus.Online : McpConnectionStatus.Offline;
                }
                catch { return McpConnectionStatus.Offline; }
            }
            catch { return McpConnectionStatus.Offline; }
        }

        if (entry.Transport == McpTransport.Stdio)
        {
            // 1) command itself — absolute path (File.Exists) or bare name (PATH lookup)
            if (!string.IsNullOrWhiteSpace(entry.Command))
            {
                var cmd = entry.Command.Trim().Trim('"');
                bool cmdIsPath = cmd.Contains('\\') || cmd.Contains('/')
                              || (cmd.Length > 1 && cmd[1] == ':');
                if (cmdIsPath)
                {
                    try
                    {
                        if (File.Exists(cmd)) return McpConnectionStatus.Online;
                    }
                    catch { /* skip */ }
                }
                else
                {
                    if (await IsCommandAvailableAsync(cmd, ct)) return McpConnectionStatus.Online;
                }
            }
            // 2) any arg looking like a file path — script the command spawns
            foreach (var arg in entry.Args)
            {
                var trimmed = arg.Trim('"');
                if (trimmed.Length < 3) continue;
                if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains('/') || (trimmed.Length > 1 && trimmed[1] == ':'))
                {
                    try
                    {
                        if (File.Exists(trimmed)) return McpConnectionStatus.Online;
                    }
                    catch { /* skip */ }
                }
            }
            return McpConnectionStatus.Offline;
        }

        return McpConnectionStatus.Unknown;
    }

    private static async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct)
    {
        // Strip any extension / arg suffix accidentally left on `command`
        var c = command.Trim();
        try
        {
            var psi = new ProcessStartInfo("where", c)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));
            // Kill the whole tree, not just `where` itself: the old form left the child alive when
            // the outer token tripped, so navigating away mid-probe leaked a process per server.
            try { await proc.WaitForExitAsync(cts.Token); }
            catch { try { proc.Kill(entireProcessTree: true); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Add or remove a server name from settings.json → permissions.enabledMcpjsonServers.
    /// Returns the new enable state (true=enabled after this call).
    /// Only meaningful for global (file-scoped) MCPs — project-scoped ones are governed
    /// by their .claude.json project section and are ignored here.
    /// </summary>
    public static async Task<bool> ToggleEnabledAsync(string serverName)
    {
        // settings.json is about to change; drop both caches so the next Scan/Probe is truthful
        // even if the write lands inside the same filesystem timestamp tick as the last read.
        ScanCacheSlot.Invalidate();
        ProbeResults.Clear();

        var bundle = SettingsService.Load(ClaudePaths.SettingsJson);
        var arr = bundle.Root["enabledMcpjsonServers"] as JsonArray;
        if (arr is null)
        {
            arr = new JsonArray();
            bundle.Root["enabledMcpjsonServers"] = arr;
        }

        int existingIndex = -1;
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)
                && string.Equals(s, serverName, StringComparison.Ordinal))
            {
                existingIndex = i;
                break;
            }
        }

        bool newState;
        if (existingIndex >= 0)
        {
            arr.RemoveAt(existingIndex);
            newState = false;
        }
        else
        {
            arr.Add(serverName);
            newState = true;
        }

        await SettingsService.SaveAsync(bundle);
        return newState;
    }

    /// <summary>
    /// Redact secret-shaped env values for display (Bearer tokens, API keys, JWTs).
    /// </summary>
    public static string RedactSensitive(string envKey, string value)
    {
        var kLower = envKey.ToLowerInvariant();
        bool sensitive = kLower.Contains("key") || kLower.Contains("token") || kLower.Contains("secret")
                      || kLower.Contains("password") || kLower.Contains("passwd") || kLower.Contains("credential");
        if (!sensitive || string.IsNullOrEmpty(value)) return value;
        if (value.Length <= 8) return "****";
        return value[..4] + new string('*', Math.Min(8, value.Length - 8)) + value[^4..];
    }
}
