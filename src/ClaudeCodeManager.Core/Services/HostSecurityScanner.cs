using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Workstation hardening probe.
///
/// Four groups, and the last two are the reason this lives in the manager rather than in a generic
/// scanner: HOST and NETWORK cover the machine, CLAUDE covers the agent's own blast radius
/// (permission rules, MCP endpoints, secrets sitting in files the agent reads every session), and
/// ENGAGEMENT covers whether the red-team safety layer is actually holding.
///
/// Everything is read-only. Nothing here changes host state; remediation is handed back as a
/// command for the operator to run, and only the few genuinely contained fixes are marked
/// <see cref="FixSafety.Safe"/>.
/// </summary>
public static class HostSecurityScanner
{
    /// <summary>Probes that need Windows APIs are batched into one PowerShell process. Ten separate
    /// spawns cost seconds of wall clock; one costs a few hundred milliseconds.</summary>
    private const string ProbeScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$o = [ordered]@{}

$mp = Get-MpComputerStatus
if ($mp) { $o['defender'] = [bool]$mp.RealTimeProtectionEnabled } else { $o['defender'] = $null }

$fw = Get-NetFirewallProfile
if ($fw) {
  $off = @()
  foreach ($p in $fw) { if (-not $p.Enabled) { $off += [string]$p.Name } }
  $o['firewallOff'] = $off
  $o['firewallProbed'] = $true
} else { $o['firewallProbed'] = $false; $o['firewallOff'] = @() }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$o['elevated'] = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# Get-BitLockerVolume costs ~7s and then returns nothing at all without elevation, which is most
# of the scan wall clock spent to produce an unknown. Only ask when the answer can come back.
if ($o['elevated']) {
  $bl = Get-BitLockerVolume -MountPoint $env:SystemDrive
  if ($bl) { $o['bitlocker'] = [string]$bl.ProtectionStatus } else { $o['bitlocker'] = $null }
} else { $o['bitlocker'] = $null }

$sys = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
if ($sys -and $sys.PSObject.Properties['EnableLUA']) { $o['uac'] = [int]$sys.EnableLUA } else { $o['uac'] = $null }

$ts = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
if ($ts -and $ts.PSObject.Properties['fDenyTSConnections']) { $o['rdpDenied'] = [int]$ts.fDenyTSConnections } else { $o['rdpDenied'] = $null }

$null = w32tm /query /status 2>$null
$o['timeSync'] = ($LASTEXITCODE -eq 0)

$broad = @()
$credPath = Join-Path $env:USERPROFILE '.claude\.credentials.json'
if (Test-Path $credPath) {
  $o['credExists'] = $true
  $a = Get-Acl $credPath
  if ($a) {
    foreach ($r in $a.Access) {
      $id = [string]$r.IdentityReference
      if ($id -match 'Everyone|BUILTIN\\Users|Authenticated Users|INTERACTIVE') { $broad += $id }
    }
  }
} else { $o['credExists'] = $false }
$o['credBroadAcl'] = $broad

$o | ConvertTo-Json -Compress -Depth 4
";

    public static List<SecurityCheck> Run()
    {
        var checks = new List<SecurityCheck>();
        var probe = RunProbe();

        AddHostChecks(checks, probe);
        AddNetworkChecks(checks, probe);
        AddClaudeChecks(checks, probe);
        AddEngagementChecks(checks);

        AcceptedRiskStore.Apply(checks);
        return checks;
    }

    // -- PowerShell probe -------------------------------------------------

    private static JsonElement? RunProbe()
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ProbeScript));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                // EncodedCommand sidesteps every layer of quoting between here and the shell; the
                // script carries both quote styles and backslash registry paths.
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.Clone();
        }
        catch
        {
            // No PowerShell, blocked by policy, or malformed output. Every consumer degrades to
            // Skip rather than reporting a control as absent on no evidence.
            return null;
        }
    }

    private static bool TryGet(JsonElement? probe, string name, out JsonElement value)
    {
        value = default;
        if (probe is null) return false;
        if (!probe.Value.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Null) return false;
        value = v;
        return true;
    }

    private static void Add(List<SecurityCheck> list, string id, string category, string name,
        CheckStatus status, CheckSeverity severity, string detail, string fix = "",
        FixSafety safety = FixSafety.ManualOnly)
        => list.Add(new SecurityCheck
        {
            Id = id,
            Category = category,
            Name = name,
            Status = status,
            Severity = severity,
            Detail = detail,
            Fix = fix,
            FixSafety = safety,
        });

    private static void Skip(List<SecurityCheck> list, string id, string category, string name, string why)
        => Add(list, id, category, name, CheckStatus.Skip, CheckSeverity.Low, why);

    // -- HOST -------------------------------------------------------------

    private const string CatHost = "HOST";

    private static void AddHostChecks(List<SecurityCheck> c, JsonElement? p)
    {
        if (TryGet(p, "defender", out var def))
        {
            var on = def.ValueKind == JsonValueKind.True;
            Add(c, "win_defender", CatHost, "Defender real-time protection",
                on ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.High,
                on ? "Real-time protection is enabled" : "Real-time protection is OFF",
                on ? "" : "Set-MpPreference -DisableRealtimeMonitoring $false   (admin)");
        }
        else Skip(c, "win_defender", CatHost, "Defender real-time protection", "Get-MpComputerStatus returned nothing");

        if (TryGet(p, "firewallProbed", out var fwp) && fwp.ValueKind == JsonValueKind.True)
        {
            var off = TryGet(p, "firewallOff", out var fo) && fo.ValueKind == JsonValueKind.Array
                ? fo.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();

            Add(c, "win_firewall", CatHost, "Windows Firewall profiles",
                off.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.High,
                off.Count == 0 ? "All profiles enabled" : "Disabled profiles: " + string.Join(", ", off),
                off.Count == 0 ? "" : $"Set-NetFirewallProfile -Profile {string.Join(",", off)} -Enabled True   (admin)");
        }
        else Skip(c, "win_firewall", CatHost, "Windows Firewall profiles", "Get-NetFirewallProfile unavailable");

        if (TryGet(p, "bitlocker", out var bl))
        {
            var s = bl.GetString() ?? "";
            var on = s.Equals("On", StringComparison.OrdinalIgnoreCase) || s == "1";
            Add(c, "win_bitlocker", CatHost, "System drive encryption",
                on ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.High,
                on ? "BitLocker protection is On" : $"BitLocker protection status: {(s.Length == 0 ? "unknown" : s)}",
                on ? "" : "manage-bde -on %SystemDrive%   (admin; back up the recovery key first)");
        }
        else
        {
            var elevated = TryGet(p, "elevated", out var el) && el.ValueKind == JsonValueKind.True;
            Skip(c, "win_bitlocker", CatHost, "System drive encryption",
                elevated
                    ? "Get-BitLockerVolume returned nothing for the system drive"
                    : "Needs elevation — run the manager as administrator to probe BitLocker");
        }

        if (TryGet(p, "uac", out var uac))
        {
            var on = uac.GetInt32() == 1;
            Add(c, "win_uac", CatHost, "User Account Control",
                on ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.High,
                on ? "EnableLUA=1" : "EnableLUA=0 - UAC is disabled",
                on ? "" : @"Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' EnableLUA 1   (admin, reboot)");
        }
        else Skip(c, "win_uac", CatHost, "User Account Control", "Policy key not readable");

        if (TryGet(p, "timeSync", out var ts))
        {
            var on = ts.ValueKind == JsonValueKind.True;
            Add(c, "ntp_sync", CatHost, "System clock synchronisation",
                on ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.Low,
                on ? "w32time is reporting status" : "w32tm /query /status failed - clock may be unsynchronised",
                on ? "" : "w32tm /resync   (finding timestamps in reports depend on this)");
        }
    }

    // -- NETWORK ----------------------------------------------------------

    private const string CatNet = "NETWORK";

    private static void AddNetworkChecks(List<SecurityCheck> c, JsonElement? p)
    {
        if (TryGet(p, "rdpDenied", out var rdp))
        {
            var denied = rdp.GetInt32() == 1;
            Add(c, "win_rdp_disabled", CatNet, "Inbound RDP",
                denied ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.Medium,
                denied ? "fDenyTSConnections=1 - RDP refused" : "fDenyTSConnections=0 - RDP is accepting connections",
                denied
                    ? ""
                    : @"Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections 1   (admin)"
                      + "\nIf inbound RDP is deliberate (VPN jump host), mark this as accepted risk instead of changing it.");
        }
        else Skip(c, "win_rdp_disabled", CatNet, "Inbound RDP", "Terminal Server key not readable");

        // Listeners bound to a routable address rather than loopback. On an assessment workstation
        // these are the ports a target network could reach back to.
        try
        {
            var exposed = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(e => !IPAddress.IsLoopback(e.Address))
                .Select(e => e.Port)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            Add(c, "open_ports", CatNet, "Non-loopback TCP listeners",
                exposed.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.Medium,
                exposed.Count == 0
                    ? "All TCP listeners are bound to loopback"
                    : $"{exposed.Count} port(s) reachable off-host: {string.Join(", ", exposed.Take(15))}"
                      + (exposed.Count > 15 ? " ..." : ""),
                exposed.Count == 0
                    ? ""
                    : "Get-NetTCPConnection -State Listen | Where-Object { $_.LocalAddress -notin '127.0.0.1','::1' }\nBind local tooling to 127.0.0.1 where it offers the choice.");
        }
        catch
        {
            Skip(c, "open_ports", CatNet, "Non-loopback TCP listeners", "TCP table unavailable");
        }
    }

    // -- CLAUDE (the agent's own blast radius) ----------------------------

    private const string CatClaude = "CLAUDE";

    private static void AddClaudeChecks(List<SecurityCheck> c, JsonElement? p)
    {
        AddPermissionChecks(c);
        AddCredentialChecks(c, p);
        AddMcpChecks(c);
        AddSecretSweep(c);
    }

    /// <summary>Allow rules carrying no constraint at all - these hand the agent the whole verb.</summary>
    private static readonly Regex UnboundedRule = new(
        @"^\s*(Bash|Read|Write|Edit|WebFetch|WebSearch)\s*(\(\s*\*+\s*\)|\(\s*\*\s*:\s*\*\s*\))?\s*$",
        RegexOptions.Compiled);

    private static void AddPermissionChecks(List<SecurityCheck> c)
    {
        var path = ClaudePaths.SettingsJson;
        if (!File.Exists(path))
        {
            Skip(c, "claude_permissions_broad", CatClaude, "Permission rule breadth", "settings.json not found");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var allow = new List<string>();

            if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object)
            {
                if (perms.TryGetProperty("allow", out var a) && a.ValueKind == JsonValueKind.Array)
                    allow.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? ""));

                var mode = perms.TryGetProperty("defaultMode", out var dm) ? dm.GetString() ?? "" : "";
                var bypass = mode.Equals("bypassPermissions", StringComparison.OrdinalIgnoreCase);
                Add(c, "claude_default_mode", CatClaude, "Default permission mode",
                    bypass ? CheckStatus.Fail : CheckStatus.Pass, CheckSeverity.Critical,
                    bypass
                        ? "defaultMode=bypassPermissions - every tool call is pre-approved"
                        : $"defaultMode={(mode.Length == 0 ? "(unset - prompts normally)" : mode)}",
                    bypass ? "Remove permissions.defaultMode, or set it to \"default\", in settings.json" : "");
            }

            var flagged = allow
                .Where(r => UnboundedRule.IsMatch(r) || r.Contains("//**") || r.Contains("(**)"))
                .Distinct()
                .ToList();

            Add(c, "claude_permissions_broad", CatClaude, "Permission rule breadth",
                flagged.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.High,
                flagged.Count == 0
                    ? $"{allow.Count} allow rules, all constrained"
                    : $"{flagged.Count} of {allow.Count} allow rules are unconstrained: {string.Join(", ", flagged.Take(6))}"
                      + (flagged.Count > 6 ? " ..." : ""),
                flagged.Count == 0 ? "" : "Narrow these to specific commands or paths under settings.json > permissions.allow");
        }
        catch (Exception ex)
        {
            Skip(c, "claude_permissions_broad", CatClaude, "Permission rule breadth", "settings.json unreadable: " + ex.Message);
        }
    }

    private static void AddCredentialChecks(List<SecurityCheck> c, JsonElement? p)
    {
        var cred = Path.Combine(ClaudePaths.ClaudeRoot, ".credentials.json");

        if (!TryGet(p, "credExists", out var exists) || exists.ValueKind != JsonValueKind.True)
        {
            Skip(c, "claude_credentials_acl", CatClaude, "Credential file exposure",
                File.Exists(cred) ? "ACL probe unavailable" : ".credentials.json not present");
            return;
        }

        var broad = TryGet(p, "credBroadAcl", out var acl) && acl.ValueKind == JsonValueKind.Array
            ? acl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).Distinct().ToList()
            : new List<string>();

        Add(c, "claude_credentials_acl", CatClaude, "Credential file exposure",
            broad.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.High,
            broad.Count == 0
                ? ".credentials.json is not granted to broad principals"
                : "Readable by " + string.Join(", ", broad),
            broad.Count == 0 ? "" : $"icacls \"{cred}\" /inheritance:r /grant:r \"%USERNAME%\":F");
    }

    private static void AddMcpChecks(List<SecurityCheck> c)
    {
        try
        {
            var summary = McpConfigService.Scan();
            var remote = summary.Servers
                .Where(s => s.Transport == McpTransport.Sse || s.Transport == McpTransport.Http)
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .ToList();
            var offHost = remote.Where(s => !IsLoopbackUrl(s.Url!)).ToList();

            Add(c, "mcp_remote_endpoints", CatClaude, "MCP transport endpoints",
                offHost.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn, CheckSeverity.Medium,
                offHost.Count == 0
                    ? $"{summary.TotalCount} servers, {remote.Count} remote, all loopback-bound"
                    : $"{offHost.Count} MCP server(s) point off-host: {string.Join(", ", offHost.Select(s => s.Name + " -> " + s.Url))}",
                offHost.Count == 0
                    ? ""
                    : "Prompts and tool results cross these endpoints. Confirm each one is infrastructure you control.");
        }
        catch (Exception ex)
        {
            Skip(c, "mcp_remote_endpoints", CatClaude, "MCP transport endpoints", "MCP config unreadable: " + ex.Message);
        }
    }

    private static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Secrets sitting in files Claude loads every session. Memory notes are the sharp case: they
    /// are auto-loaded into context, so a token pasted into one is read by the model on every
    /// single start, and it is the kind of file nobody thinks of as a credential store.
    /// </summary>
    private static void AddSecretSweep(List<SecurityCheck> c)
    {
        var roots = new List<string>();

        if (Directory.Exists(ClaudePaths.ProjectsRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
            {
                var mem = Path.Combine(d, "memory");
                if (Directory.Exists(mem)) roots.Add(mem);
            }
        }
        if (Directory.Exists(ClaudePaths.SkillsRoot)) roots.Add(ClaudePaths.SkillsRoot);
        if (Directory.Exists(ClaudePaths.AgentsRoot)) roots.Add(ClaudePaths.AgentsRoot);

        if (roots.Count == 0)
        {
            Skip(c, "claude_secrets_in_context", CatClaude, "Secrets in auto-loaded context", "No memory or skill roots found");
            return;
        }

        var findings = new List<string>();
        var scanned = 0;

        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var f in files)
            {
                string text;
                try
                {
                    if (new FileInfo(f).Length > 512 * 1024) continue;
                    text = File.ReadAllText(f);
                }
                catch { continue; }

                scanned++;
                var kinds = SecretPatterns.Scan(text)
                    .Where(h => h.Severity == CredentialSeverity.Critical)
                    .Select(h => h.Type)
                    .Distinct()
                    .ToList();
                if (kinds.Count == 0) continue;

                findings.Add($"{Path.GetFileName(f)}: {string.Join(", ", kinds)}");
            }
        }

        Add(c, "claude_secrets_in_context", CatClaude, "Secrets in auto-loaded context",
            findings.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Critical,
            findings.Count == 0
                ? $"{scanned} markdown files scanned, no live credential patterns"
                : $"{findings.Count} file(s) carry credential material: {string.Join(" | ", findings.Take(5))}"
                  + (findings.Count > 5 ? " ..." : ""),
            findings.Count == 0
                ? ""
                : "Revoke the exposed credential first, then strip it from the file. These are read into context on every session start.");
    }

    // -- ENGAGEMENT (red-team safety layer) -------------------------------

    private const string CatEng = "ENGAGEMENT";

    private static void AddEngagementChecks(List<SecurityCheck> c)
    {
        var svc = new EngagementSafetyService();
        if (!svc.SafetyLayerInstalled)
        {
            Skip(c, "engagement_layer", CatEng, "Safety layer installed", "redteam-safety is not installed");
            return;
        }

        Add(c, "engagement_layer", CatEng, "Safety layer installed", CheckStatus.Pass, CheckSeverity.Low,
            "redteam-safety present at " + EngagementSafetyService.SafetyRoot);

        if (svc.IsKillSwitchActive())
        {
            Add(c, "engagement_killswitch", CatEng, "Kill switch", CheckStatus.Warn, CheckSeverity.High,
                "STOP file present - all scoped tooling is halted",
                "Clear the kill switch from WORKFLOWS > SAFETY when the engagement resumes.");
            return;
        }

        var m = svc.ReadManifest();
        if (m?.Armed != true)
        {
            Add(c, "engagement_enforcement", CatEng, "Scope enforcement", CheckStatus.Skip, CheckSeverity.Low,
                "Not armed - no engagement in progress");
            return;
        }

        // Armed means work is authorised to run, which is exactly when a missing L1 matters: the L2
        // hook only inspects command strings, so without mitmproxy nothing is checking real traffic
        // against the scope.
        var missing = new List<string>();
        if (!svc.IsMitmproxyRunning()) missing.Add("mitmproxy (L1) not running");
        if (!svc.IsProxyEnvSet()) missing.Add("HTTP_PROXY not set");

        Add(c, "engagement_enforcement", CatEng, "Scope enforcement",
            missing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.Critical,
            missing.Count == 0
                ? $"Armed on '{m.Target}' with L1 proxy and proxy env in place"
                : $"Armed on '{m.Target}' but {string.Join("; ", missing)}",
            missing.Count == 0 ? "" : "Open WORKFLOWS > SAFETY and use ENFORCE ON to bring the proxy and env up together.");

        var include = m.ScopeInclude?.Count ?? 0;
        Add(c, "engagement_scope_defined", CatEng, "Scope definition",
            include > 0 ? CheckStatus.Pass : CheckStatus.Fail, CheckSeverity.High,
            include > 0
                ? $"{include} include pattern(s), {m.ScopeExclude?.Count ?? 0} exclude"
                : "Armed with an empty include list - nothing is in scope, or everything is",
            include > 0 ? "" : "Define scope include patterns in the SAFETY tab before running tooling.");
    }
}
