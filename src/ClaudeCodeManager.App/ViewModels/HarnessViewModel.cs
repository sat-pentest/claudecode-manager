using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

// ─── Small display DTOs used by HarnessView ──────────────────────────────

/// <summary>
/// Observable so async probe results (for MCP nodes) can update the dot color live —
/// otherwise HARNESS would only reflect "configured to be enabled" and diverge from
/// the MCP module which reflects "actually reachable right now".
/// </summary>
public partial class HarnessNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "";               // MCP / SKILL / AGENT / MEMORY / SETTINGS
    [ObservableProperty] private string _stateBadge = "";         // ENABLED / DISABLED / PROJECT / OAUTH
    [ObservableProperty] private bool _isActive;                  // drives the pulse dot; true only when configured-enabled AND reachable
    [ObservableProperty] private string _connectionState = "";    // for MCP: ONLINE / OFFLINE / DISABLED / OAUTH / CHECKING (mirrors MCP module semantics)
    [ObservableProperty] private string _subtitle = "";
    public string TargetModuleKey { get; set; } = "";
    public string? NavigateFile { get; set; }
    public McpServerEntry? McpSource { get; set; }                // set for MCP nodes so ProbeAsync can be re-run
}

public sealed class HarnessFlow
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TriggerHint { get; set; } = "";
    public List<HarnessFlowStep> Steps { get; set; } = new();
    public bool AllStepsActive => Steps.All(s => s.IsActive);
    public string StateBadge => AllStepsActive ? "READY" : "PARTIAL";
}

public sealed class HarnessFlowStep
{
    public string Kind { get; set; } = "";     // SKILL / AGENT / MCP / EXTERNAL
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool IsActive { get; set; }
    public string Arrow { get; set; } = "";    // "↓" / "↓×N" / "↔" / "→"
}

// ─── ViewModel ─────────────────────────────────────────────────────────────

public partial class HarnessViewModel : ModuleBase
{
    public override string Key => "HARN";
    public override string Title => "HARNESS";
    // Layered rectangles + connection line
    public override string Glyph => "M3,5 H21 V9 H3 Z M3,15 H21 V19 H3 Z M12,9 V15";

    private readonly MainViewModel _main;

    // Layer 1 (runtime)
    public ObservableCollection<HarnessNode> McpNodes { get; } = new();
    [ObservableProperty] private int _mcpTotal;
    [ObservableProperty] private int _mcpOnline;
    [ObservableProperty] private int _mcpDisabled;
    [ObservableProperty] private string _storageText = "";
    [ObservableProperty] private string _retentionText = "";
    [ObservableProperty] private bool _retentionIsInfinite;
    [ObservableProperty] private int _memoryCount;
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private int _snapshotCount;

    // Layer 2 (task)
    public ObservableCollection<HarnessNode> SkillNodes { get; } = new();
    public ObservableCollection<HarnessNode> AgentNodes { get; } = new();
    [ObservableProperty] private int _skillTotal;
    [ObservableProperty] private int _skillEnabled;
    [ObservableProperty] private int _agentTotal;
    [ObservableProperty] private int _agentEnabled;

    // Layer 3 (workflows) — .mjs scripts in ~/.claude/workflows/
    public ObservableCollection<HarnessNode> WorkflowNodes { get; } = new();
    [ObservableProperty] private int _workflowTotal;
    [ObservableProperty] private int _workflowEnabled;

    // Orchestration flows (predefined based on user setup)
    public ObservableCollection<HarnessFlow> Flows { get; } = new();

    // Collapse state for each section (persisted only in-memory)
    [ObservableProperty] private bool _isLayer1Collapsed;
    [ObservableProperty] private bool _isLayer2Collapsed;
    [ObservableProperty] private bool _isLayer3Collapsed;
    [ObservableProperty] private bool _isFlowsCollapsed;

    [RelayCommand] private void ToggleLayer1() => IsLayer1Collapsed = !IsLayer1Collapsed;
    [RelayCommand] private void ToggleLayer2() => IsLayer2Collapsed = !IsLayer2Collapsed;
    [RelayCommand] private void ToggleLayer3() => IsLayer3Collapsed = !IsLayer3Collapsed;
    [RelayCommand] private void ToggleFlows() => IsFlowsCollapsed = !IsFlowsCollapsed;

    /// <summary>Opens the JSON editor for ~/.claude/harness-flows.json. On successful save, refresh the view.</summary>
    [RelayCommand]
    private void EditFlows()
    {
        var w = new Views.FlowsEditorWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        var saved = w.ShowDialog() == true;
        if (saved) Refresh();
    }

    public HarnessViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Refresh();

    private CancellationTokenSource? _probeCts;

    [RelayCommand]
    private void Refresh()
    {
        // ── LAYER 1: MCP servers ────────────────────────────────────────
        McpNodes.Clear();
        var mcpSummary = McpConfigService.Scan();
        foreach (var s in mcpSummary.Servers)
        {
            string badge;
            if (s.NeedsAuth) badge = "AUTH REQ";
            else if (s.Scope == McpScope.ProjectScoped) badge = "PROJECT";
            else if (s.EnabledInSettings) badge = "ENABLED";
            else badge = "DISABLED";

            var transport = s.Transport switch
            {
                McpTransport.Stdio => "STDIO",
                McpTransport.Sse => "SSE",
                McpTransport.Http => "HTTP",
                _ => "?"
            };
            // Initial ConnectionState. DISABLED and OAUTH resolve immediately (no probe needed).
            // ENABLED and PROJECT start as "CHECKING" and get resolved by the async probe below.
            string initState;
            if (s.NeedsAuth) initState = "OAUTH";
            else if (badge == "DISABLED") initState = "DISABLED";
            else initState = "CHECKING";

            McpNodes.Add(new HarnessNode
            {
                Name = s.Name,
                Kind = "MCP",
                StateBadge = badge,
                IsActive = false,                     // set to true only after probe returns ONLINE
                ConnectionState = initState,
                Subtitle = $"{transport} · {(s.Scope == McpScope.ProjectScoped ? "project" : s.Scope == McpScope.OAuthConnector ? "oauth" : "global")}",
                TargetModuleKey = "MCP",
                McpSource = s
            });
        }
        McpTotal = mcpSummary.TotalCount;
        McpOnline = mcpSummary.EnabledCount;
        McpDisabled = McpTotal - McpOnline - mcpSummary.NeedsAuthCount;

        // Kick off async probes for enabled/project MCPs so the dot reflects
        // actual reachability, matching what the MCP module shows.
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        _ = ProbeMcpNodesAsync(_probeCts.Token);

        // ── LAYER 1: environment stats ──────────────────────────────────
        var storage = SessionStorageService.Scan();
        StorageText = SessionStorageService.FormatBytes(storage.TotalSize);
        SessionCount = storage.TotalSessionFiles;

        try
        {
            var bundle = SettingsService.Load(ClaudePaths.SettingsJson);
            if (bundle.Root["cleanupPeriodDays"] is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<int>(out var days))
            {
                if (days >= 9999) { RetentionText = "∞"; RetentionIsInfinite = true; }
                else               { RetentionText = $"{days}d"; RetentionIsInfinite = false; }
            }
            else { RetentionText = "30d"; RetentionIsInfinite = false; }
        }
        catch { RetentionText = "?"; RetentionIsInfinite = false; }

        MemoryCount = MemoryIndexer.DiscoverProjects().Sum(p => p.Entries.Count);
        SnapshotCount = _main.Snapshots.ListSnapshots(500).Count;

        // ── LAYER 2: Skills ─────────────────────────────────────────────
        SkillNodes.Clear();
        var skills = SkillLoader.LoadAll();
        foreach (var s in skills)
        {
            SkillNodes.Add(new HarnessNode
            {
                Name = s.Name,
                Kind = "SKILL",
                StateBadge = s.Disabled ? "DISABLED" : "ENABLED",
                IsActive = !s.Disabled,
                ConnectionState = s.Disabled ? "DISABLED" : "ONLINE",
                Subtitle = $"{s.Model ?? "(inherit)"} · {s.Tools.Count} tools",
                TargetModuleKey = "SKIL",
                NavigateFile = s.SkillFilePath
            });
        }
        SkillTotal = skills.Count;
        SkillEnabled = skills.Count(s => !s.Disabled);

        // ── LAYER 2: Agents ─────────────────────────────────────────────
        AgentNodes.Clear();
        var agents = AgentLoader.LoadAll();
        foreach (var a in agents)
        {
            AgentNodes.Add(new HarnessNode
            {
                Name = a.Name,
                Kind = "AGENT",
                StateBadge = a.Disabled ? "DISABLED" : "ENABLED",
                IsActive = !a.Disabled,
                ConnectionState = a.Disabled ? "DISABLED" : "ONLINE",
                Subtitle = $"{a.Model ?? "(inherit)"} · {a.Tools.Count} tools",
                TargetModuleKey = "AGNT",
                NavigateFile = a.FilePath
            });
        }
        AgentTotal = agents.Count;
        AgentEnabled = agents.Count(a => !a.Disabled);

        // ── LAYER 3: Workflows ──────────────────────────────────────────
        WorkflowNodes.Clear();
        var workflows = WorkflowLoader.LoadAll();
        foreach (var w in workflows)
        {
            var phasesCount = w.Phases?.Count ?? 0;
            WorkflowNodes.Add(new HarnessNode
            {
                Name = w.Name,
                Kind = "WORKFLOW",
                StateBadge = w.Disabled ? "DISABLED" : "ENABLED",
                IsActive = !w.Disabled,
                ConnectionState = w.Disabled ? "DISABLED" : "ONLINE",
                Subtitle = $"{phasesCount} phase{(phasesCount == 1 ? "" : "s")} · {w.FileName}",
                TargetModuleKey = "WFLW",
                NavigateFile = w.FilePath
            });
        }
        WorkflowTotal = workflows.Count;
        WorkflowEnabled = workflows.Count(w => !w.Disabled);

        // ── FLOWS: predefined orchestration patterns ────────────────────
        BuildFlows(skills, agents, mcpSummary);

        Status = $"L1: {McpTotal} MCPs, {SessionCount} sessions · L2: {SkillTotal} skills, {AgentTotal} agents · L3: {WorkflowTotal} workflows · {Flows.Count} flows";
    }

    private void BuildFlows(List<Skill> skills, List<AgentDefinition> agents, McpConfigSummary mcps)
    {
        Flows.Clear();
        bool SkillActive(string name) => skills.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !s.Disabled);
        bool AgentActive(string name) => agents.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !a.Disabled);
        bool McpEnabled(string name) => mcps.Servers.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (m.EnabledInSettings || m.Scope == McpScope.ProjectScoped));

        // External flow definitions (from ~/.claude/harness-flows.json) take precedence.
        // Fall back to hardcoded defaults only when the file is missing/unparseable.
        var external = HarnessFlowsLoader.TryLoad();
        if (external is { Count: > 0 })
        {
            foreach (var def in external)
            {
                var flow = new HarnessFlow
                {
                    Name = def.Name,
                    Description = def.Description,
                    TriggerHint = def.TriggerHint,
                    Steps = new List<HarnessFlowStep>()
                };
                foreach (var s in def.Steps)
                {
                    bool active = s.Kind switch
                    {
                        "SKILL" => SkillActive(s.Name),
                        "AGENT" => AgentActive(s.Name),
                        "MCP"   => McpEnabled(s.Name),
                        _       => true    // EXTERNAL is always assumed present
                    };
                    flow.Steps.Add(new HarnessFlowStep
                    {
                        Kind = s.Kind,
                        Name = s.Name,
                        Detail = s.Detail,
                        Arrow = s.Arrow,
                        IsActive = active
                    });
                }
                Flows.Add(flow);
            }
            return;   // JSON loaded — skip hardcoded fallback
        }

        Flows.Add(new HarnessFlow
        {
            Name = "Web Static Recon",
            Description = "웹 apex + 하위 디렉터리 정적 정찰 · 엔드포인트 인벤토리",
            TriggerHint = "\"정적 분석\", \"소스 훑어봐\", \"static analysis\"",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "SKILL", Name = "code-static-recon", Detail = "orchestrator, 5-카테고리 baseline", IsActive = SkillActive("code-static-recon"), Arrow = "↓ uses" },
                new() { Kind = "MCP",   Name = "playwright",        Detail = "SPA 렌더 후 DOM 접근 시",         IsActive = McpEnabled("playwright"),         Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "Mobile Static Recon",
            Description = "APK/IPA 언팩 · MASVS 8-카테고리 · Native 병렬 워커",
            TriggerHint = "\"APK 분석\", \"jadx로 뜯어봐\", \"MASVS\"",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "SKILL", Name = "mobile-static-recon", Detail = "orchestrator, Manifest·소스·리소스·SDK 5-패스", IsActive = SkillActive("mobile-static-recon"), Arrow = "↓ fan-out" },
                new() { Kind = "AGENT", Name = "apk-native-analyzer", Detail = "per-.so 격리 워커, JSON 원자재 반환 (×N 병렬)", IsActive = AgentActive("apk-native-analyzer"), Arrow = "↓ then" },
                new() { Kind = "SKILL", Name = "frida-bypass",         Detail = "동적 우회 릴레이 (필요 시)",                       IsActive = SkillActive("frida-bypass"),         Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "Dynamic Bypass",
            Description = "Frida 후킹 · SSL pinning · anti-tamper 우회",
            TriggerHint = "\"Frida 우회\", \"SSL 피닝\", \"AppIron 우회\"",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "SKILL", Name = "frida-bypass",   Detail = "7 우회 모듈, 한국 벤더 특이",   IsActive = SkillActive("frida-bypass"),   Arrow = "↓ traffic via" },
                new() { Kind = "MCP",   Name = "burp-official",  Detail = "SSE, 트래픽 캡처·재요청",       IsActive = McpEnabled("burp-official"),   Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "Active Scanning (optional)",
            Description = "능동 스캔 · 인가된 프로그램에서만 활성화",
            TriggerHint = "명시 요청 · CVD·VDP 능동 승인",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "MCP",   Name = "nuclei",           Detail = "template 기반 취약점 스캔",  IsActive = McpEnabled("nuclei"),           Arrow = "↔ compare with" },
                new() { Kind = "SKILL", Name = "code-static-recon", Detail = "정적 인벤토리와 대조",       IsActive = SkillActive("code-static-recon"), Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "FSI Report",
            Description = "금융권 표준 5단 제출 양식",
            TriggerHint = "\"FSI 리포트\", \"금융권 리포트\"",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "SKILL", Name = "fsi-report", Detail = "제목공식·요약·분류·발생정보·상세5단", IsActive = SkillActive("fsi-report"), Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "FTG (Findthegap) Report",
            Description = "파인더갭 9단 표준 양식, API 단위 분할",
            TriggerHint = "\"FTG 리포트\", \"파인더갭\"",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "SKILL", Name = "ftg-report", Detail = "9단, 최민우 문구 패턴", IsActive = SkillActive("ftg-report"), Arrow = "" }
            }
        });

        Flows.Add(new HarnessFlow
        {
            Name = "Cross-Session Memory",
            Description = "프로젝트 컨텍스트 자동 로딩 · 새 세션에도 유지",
            TriggerHint = "자동 (세션 시작 시)",
            Steps = new List<HarnessFlowStep>
            {
                new() { Kind = "EXTERNAL", Name = "MEMORY.md",             Detail = $"{MemoryCount} 엔트리 인덱스", IsActive = MemoryCount > 0, Arrow = "↓ activates" },
                new() { Kind = "EXTERNAL", Name = "CLAUDE.md",             Detail = "전역 pentest ROE 지시문",     IsActive = File.Exists(ClaudePaths.GlobalClaudeMd), Arrow = "" }
            }
        });
    }

    /// <summary>
    /// Fires ProbeAsync for each MCP node that isn't already resolved (DISABLED/OAUTH short-circuit
    /// in Refresh). Updates ConnectionState + IsActive on the UI thread as each probe returns —
    /// so the dot goes from CHECKING → ONLINE (green pulse) or OFFLINE (red static), mirroring MCP module.
    /// </summary>
    private async Task ProbeMcpNodesAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var vm in McpNodes)
        {
            if (vm.McpSource is null) continue;
            if (vm.ConnectionState is "DISABLED" or "OAUTH") continue;   // already resolved
            tasks.Add(ProbeOneAsync(vm, ct));
        }
        await Task.WhenAll(tasks);
    }

    private static async Task ProbeOneAsync(HarnessNode vm, CancellationToken ct)
    {
        var entry = vm.McpSource!;
        var result = await McpConfigService.ProbeAsync(entry, ct);
        var state = result switch
        {
            McpConnectionStatus.Online => "ONLINE",
            McpConnectionStatus.Offline => "OFFLINE",
            McpConnectionStatus.OAuth => "OAUTH",
            McpConnectionStatus.Disabled => "DISABLED",
            _ => "UNKNOWN"
        };
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            vm.ConnectionState = state;
            vm.IsActive = state == "ONLINE";
        });
    }

    [RelayCommand]
    private void NavigateNode(HarnessNode? node)
    {
        if (node is null) return;
        var target = _main.Modules.FirstOrDefault(m => m.Key == node.TargetModuleKey);
        if (target is null) return;
        _main.NavigateCommand.Execute(target);
        // If a specific file navigation is requested, delegate to MainViewModel
        if (!string.IsNullOrEmpty(node.NavigateFile))
            _main.NavigateToFile(node.NavigateFile);
    }
}
