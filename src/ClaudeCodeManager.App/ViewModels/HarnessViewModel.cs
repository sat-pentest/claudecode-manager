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

public partial class HarnessFlow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _triggerHint = "";
    public List<HarnessFlowStep> Steps { get; set; } = new();

    [ObservableProperty] private bool _allStepsActive;
    [ObservableProperty] private string _stateBadge = "";

    /// <summary>Hops in the chain walk: entry→step0 is hop 0, so there are exactly Steps.Count.</summary>
    public int HopCount => Math.Max(1, Steps.Count);

    /// <summary>The entry connector is hop 0, which the converter addresses as index -1.</summary>
    public int EntryIndex => -1;

    /// <summary>Whether the entry hop completes — i.e. the first step is actually available.</summary>
    [ObservableProperty] private bool _firstStepReachable = true;

    // ── Per-flow animation state ──
    // The walk lives on the flow rather than on the global ticker so only the branch the user
    // picked animates; every other branch sits at rest (progress 0 = nothing lit, ghost rails only).
    [ObservableProperty] private double _chainProgress;
    [ObservableProperty] private double _chainAlpha;

    /// <summary>Which branch a click picked. Survives the lit/animated mode switch.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Brightness, which is not the same thing as selection: in lit mode every branch node
    /// is bright, but only one is still the picked one.</summary>
    [ObservableProperty] private bool _isLit;

    /// <summary>Selected *and* actually walking — drives the RUNNING pill, which would be a lie in
    /// lit mode where nothing moves.</summary>
    [ObservableProperty] private bool _isRunning;

    // ── Tree placement — the spine is drawn per row, so each row needs to know if it caps an end ──
    [ObservableProperty] private bool _isFirstInTree;
    [ObservableProperty] private bool _isLastInTree;

    /// <summary>Recompute AllStepsActive/StateBadge and the per-step chain state from Steps' IsActive.</summary>
    public void Recompute()
    {
        AllStepsActive = Steps.Count > 0 && Steps.All(s => s.IsActive);
        StateBadge = AllStepsActive ? "READY" : "PARTIAL";

        // A step is reachable only if it and everything upstream is active — one dead hop
        // blocks the rest of the chain, which is what the diagram then shows.
        var reachable = true;
        for (var i = 0; i < Steps.Count; i++)
        {
            reachable &= Steps[i].IsActive;
            Steps[i].Owner = this;
            Steps[i].Index = i;
            Steps[i].HopCount = HopCount;
            Steps[i].IsReachable = reachable;
        }
        // Each connector is drawn inside the step it leaves, but its fate belongs to the step it
        // leads to — so carry the next step's reachability back onto this one.
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].NextReachable = i + 1 < Steps.Count ? Steps[i + 1].IsReachable : true;

        FirstStepReachable = Steps.Count == 0 || Steps[0].IsReachable;
        OnPropertyChanged(nameof(HopCount));
    }

    /// <summary>Subscribe to each step's IsActive change so aggregate state (READY/PARTIAL)
    /// stays fresh as async MCP probes resolve. Call after Steps are populated.</summary>
    public void AttachStepListeners()
    {
        foreach (var step in Steps)
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HarnessFlowStep.IsActive))
                    Recompute();
            };
        }
        Recompute();
    }
}

public partial class HarnessFlowStep : ObservableObject
{
    [ObservableProperty] private string _kind = "";     // SKILL / AGENT / MCP / EXTERNAL
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _arrow = "";    // "↓ uses" / "↓ fan-out" / "↔ compare with"

    /// <summary>Concurrent copies of this step. >1 draws the node as a fan-out worker stack.</summary>
    [ObservableProperty] private int _parallel = 1;

    // ── Chain-walk state, filled in by HarnessFlow.Recompute() ──
    /// <summary>Position in the flow. The request arrives here at chain position Index+1.</summary>
    [ObservableProperty] private int _index;
    /// <summary>Total hops in the owning flow, so the converter can scale the shared clock.</summary>
    [ObservableProperty] private int _hopCount = 1;
    /// <summary>This step and everything upstream is active — the request can actually get here.</summary>
    [ObservableProperty] private bool _isReachable = true;
    /// <summary>The step this one's outgoing rail leads to is reachable.</summary>
    [ObservableProperty] private bool _nextReachable = true;

    /// <summary>The flow this step belongs to. Diagram elements read the running animation off it,
    /// so a step never has to walk the visual tree to find its branch's clock.</summary>
    public HarnessFlow Owner { get; set; } = null!;

    /// <summary>
    /// The arrow text with its glyph stripped. The stored form carries a vertical glyph ("↓ uses")
    /// from when flows were drawn as a top-down list; the diagram runs left→right and paints its own
    /// arrowhead, so only the caption ("uses") belongs on the rail.
    /// </summary>
    public string ArrowLabel => Arrow.TrimStart('↓', '↑', '→', '←', '↔', ' ').Trim();

    public bool IsFanOut => Parallel > 1;

    /// <summary>Worker rows for the fan-out stack, capped at 3 — beyond that the ×N badge carries
    /// the real count and more rows would just make every node card taller.</summary>
    public List<FlowWorker> Instances
    {
        get
        {
            var n = Math.Min(Parallel, 3);
            var list = new List<FlowWorker>(n);
            for (var i = 0; i < n; i++) list.Add(new FlowWorker { Index = i, Label = $"#{i + 1}", Owner = this });
            return list;
        }
    }

    public string ParallelBadge => IsFanOut ? $"×{Parallel}" : "";

    partial void OnArrowChanged(string value) => OnPropertyChanged(nameof(ArrowLabel));

    partial void OnParallelChanged(int value)
    {
        OnPropertyChanged(nameof(IsFanOut));
        OnPropertyChanged(nameof(Instances));
        OnPropertyChanged(nameof(ParallelBadge));
    }
}

/// <summary>
/// One row of a fan-out node's worker stack. Holds a back-reference to its step so the diagram's
/// chain converter can read the step's timing off the worker itself, instead of every worker row
/// having to walk the visual tree to find its parent.
/// </summary>
public sealed class FlowWorker
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public HarnessFlowStep Owner { get; init; } = null!;
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

    /// <summary>The branch whose animation is running. Everything else rests.</summary>
    [ObservableProperty] private HarnessFlow? _selectedFlow;

    /// <summary>
    /// Static-highlight mode: every branch is drawn fully lit and nothing moves. Useful for reading
    /// the whole harness at once, or for a screenshot, where the walk just gets in the way.
    /// Toggling back hands the diagram to the animation again.
    /// </summary>
    [ObservableProperty] private bool _isStaticHighlight;

    partial void OnIsStaticHighlightChanged(bool value) => ApplyHighlightMode();

    /// <summary>
    /// Push the current mode onto the flows. Lit mode parks every branch at the end of its walk
    /// (progress 1) — which the diagram already renders as "all hops drawn, all nodes lit, no packet
    /// in flight", so no separate static styling is needed. Animated mode empties every branch and
    /// lets the ticker refill the selected one.
    /// </summary>
    private void ApplyHighlightMode()
    {
        foreach (var f in Flows)
        {
            if (IsStaticHighlight)
            {
                f.ChainProgress = 1;
                f.ChainAlpha = 1;
            }
            else
            {
                var live = ReferenceEquals(f, SelectedFlow);
                f.ChainProgress = live ? f.ChainProgress : 0;
                f.ChainAlpha = live ? f.ChainAlpha : 0;
            }
        }
        RefreshFlowVisualState();
        if (!IsStaticHighlight) Services.FlowTicker.Current.Restart();
    }

    /// <summary>
    /// Split brightness from selection. The step nodes already take their brightness from the
    /// chain state, but a branch node has no chain position of its own, so it needs telling —
    /// otherwise lit mode brightens every box in a row except the first one.
    /// </summary>
    private void RefreshFlowVisualState()
    {
        foreach (var f in Flows)
        {
            f.IsLit = IsStaticHighlight || f.IsSelected;
            f.IsRunning = f.IsSelected && !IsStaticHighlight;
        }
    }

    public HarnessViewModel(MainViewModel main)
    {
        _main = main;

        // One clock, but its output is routed to a single flow — clicking a branch is what decides
        // which one. Flows left unselected keep progress 0, which renders as the resting diagram.
        Services.FlowTicker.Current.PropertyChanged += (_, _) =>
        {
            if (IsStaticHighlight) return;   // lit mode holds its own values; the clock must not overwrite them
            var f = SelectedFlow;
            if (f is null) return;
            f.ChainProgress = Services.FlowTicker.Current.ChainProgress;
            f.ChainAlpha = Services.FlowTicker.Current.ChainAlpha;
        };
    }

    /// <summary>Pick a branch of the tree: it starts running from its entry node, the previous one
    /// falls back to rest.</summary>
    [RelayCommand]
    private void SelectFlow(HarnessFlow? flow)
    {
        if (flow is null) return;
        foreach (var f in Flows) f.IsSelected = ReferenceEquals(f, flow);
        SelectedFlow = flow;
        RefreshFlowVisualState();

        // In lit mode a click only moves the marker — everything stays bright, and the pick takes
        // effect when the animation is switched back on.
        if (IsStaticHighlight) return;

        foreach (var f in Flows)
        {
            if (f.IsSelected) continue;
            f.ChainProgress = 0;
            f.ChainAlpha = 0;
        }
        Services.FlowTicker.Current.Restart();   // answer the click with a run from the top
    }

    public override void OnActivated() => Load();

    /// <summary>
    /// Stop probing the moment the user leaves. The probes are fire-and-forget, so without this
    /// they outlive the module: leaving HARNESS left a `where.exe` per stdio server and a TCP
    /// connect per SSE server running, and those contended with whatever module was activated
    /// next. Measured before this: activating MEMORY right after HARNESS cost 2972 ms, against
    /// ~2 ms for the same module in isolation.
    /// </summary>
    public override void OnDeactivated()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
    }

    private CancellationTokenSource? _probeCts;

    /// <summary>Refresh button: re-reads config AND re-tests every MCP for real. Activation alone
    /// reuses probe results for their TTL, so bouncing through this menu no longer re-spawns a
    /// `where.exe` per server or re-waits the full timeout on a server that is down.</summary>
    [RelayCommand]
    private void Refresh()
    {
        McpConfigService.InvalidateProbeCache();
        Load();
    }

    private void Load()
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
        _probeCts?.Dispose();
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
                Subtitle = $"{ModelAlias.ToChoice(s.Model)} · {s.Tools.Count} tools",
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
                Subtitle = $"{ModelAlias.ToChoice(a.Model)} · {a.Tools.Count} tools",
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
        var previouslySelected = SelectedFlow?.Name;
        BuildFlows(skills, agents, mcpSummary);
        // Wire step→flow listeners so async MCP probes update aggregate state live
        foreach (var f in Flows) f.AttachStepListeners();

        // Tree placement: the spine is drawn per row, so the ends have to be marked.
        for (var i = 0; i < Flows.Count; i++)
        {
            Flows[i].IsFirstInTree = i == 0;
            Flows[i].IsLastInTree = i == Flows.Count - 1;
        }
        // Keep the user's branch across a refresh; otherwise start on the first one so the
        // diagram is alive on arrival rather than sitting inert until something is clicked.
        var restore = Flows.FirstOrDefault(f => f.Name == previouslySelected) ?? Flows.FirstOrDefault();
        SelectFlow(restore);
        ApplyHighlightMode();   // flows were rebuilt, so re-assert lit mode if it is engaged

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
                        Parallel = s.Parallel,
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
                new() { Kind = "AGENT", Name = "apk-native-analyzer", Detail = "per-.so 격리 워커, JSON 원자재 반환", Parallel = 3, IsActive = AgentActive("apk-native-analyzer"), Arrow = "↓ then" },
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

    private async Task ProbeOneAsync(HarnessNode vm, CancellationToken ct)
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
        var isOnline = state == "ONLINE";
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            vm.ConnectionState = state;
            vm.IsActive = isOnline;

            // Propagate to flow steps referring to this MCP so that flows go
            // READY→PARTIAL (or vice versa) as reachability changes. Each step's
            // IsActive change fires the flow's Recompute() via AttachStepListeners.
            foreach (var flow in Flows)
            {
                foreach (var step in flow.Steps)
                {
                    if (step.Kind == "MCP" && step.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                        step.IsActive = isOnline;
                }
            }
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
