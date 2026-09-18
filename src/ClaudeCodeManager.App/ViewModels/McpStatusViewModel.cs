using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class McpServerVm : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _transportText = "";       // "STDIO" / "SSE" / "HTTP"
    [ObservableProperty] private string _scopeText = "";           // "global" / "project" / "oauth"
    [ObservableProperty] private string _endpointText = "";        // command+args or url
    [ObservableProperty] private string _workspaceText = "";
    [ObservableProperty] private string _sourceFile = "";
    [ObservableProperty] private bool _enabledInSettings;
    [ObservableProperty] private bool _needsAuth;
    [ObservableProperty] private string _statusBadge = "";         // "OK" / "AUTH REQ" / "DISABLED"
    [ObservableProperty] private string _authCacheTs = "";
    [ObservableProperty] private string _connectionState = "CHECKING"; // CHECKING / ONLINE / OFFLINE / OAUTH / DISABLED / UNKNOWN
    [ObservableProperty] private bool _canToggle;                       // false for OAuth and project-scoped
    [ObservableProperty] private string _descriptionText = "";
    public McpServerEntry Source { get; set; } = new();
    public List<string> ArgsList { get; set; } = new();
    public List<KeyValuePair<string, string>> EnvList { get; set; } = new();
}

public partial class McpStatusViewModel : ModuleBase
{
    public override string Key => "MCP";
    public override string Title => "MCP";
    // Nested squares icon (bridges/connections)
    public override string Glyph => "M3,4 H10 V11 H3 Z M14,4 H21 V11 H14 Z M3,15 H10 V22 H3 Z M14,15 H21 V22 H14 Z M10,7.5 H14 M7.5,11 V15 M14,18.5 H10";

    private readonly MainViewModel _main;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _stdioCount;
    [ObservableProperty] private int _remoteCount;
    [ObservableProperty] private int _needsAuthCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private bool _isDescriptionExpanded;   // (legacy) still bound in case anything else uses it
    [ObservableProperty] private bool _isDetailCollapsed;         // AGENTS-style DETAIL section fold. default expanded.

    [RelayCommand] private void ToggleDetail() => IsDetailCollapsed = !IsDetailCollapsed;

    /// <summary>
    /// Curated descriptions for known MCP servers. For unknown/custom servers, falls back to
    /// a generic placeholder. Update this table when adding new MCPs to give users context
    /// without cracking open the source repo.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playwright"] =
            "Microsoft 공식 브라우저 자동화 MCP (@playwright/mcp). SPA 렌더링·DOM 접근·스냅샷·폼 입력·클릭·네트워크 감시 지원. " +
            "NexacroN·WebSquare·React 같은 JS-heavy 사이트에서 chunk 로드 후 실제 렌더된 상태를 접근할 때 사용. WebFetch로는 안 되는 정적 정찰 보완용.",
        ["nuclei"] =
            "ProjectDiscovery Nuclei 스캐너 커스텀 Python 래퍼 (crazyMarky/mcp_nuclei_server). " +
            "template 기반 취약점 탐지 · severity 필터 · tag 필터 지원. **능동 스캔 성격**이므로 CVD·VDP 시범사업처럼 " +
            "명시 인가 있는 프로그램에서만 활성화. 평상시엔 DISABLED 유지 권장.",
        ["burp-official"] =
            "PortSwigger 공식 Burp Suite MCP (SSE 127.0.0.1:9876). Proxy history 조회·요청 리플레이·사이트맵·스캐너 이슈 조회·리피터·인트루더 통합 지원. " +
            "사용자 메모리 정책 [[feedback_burp_mcp_routing]]에 따라 유일 허용 Burp MCP.",
        ["burp-proxy"] =
            "Burp REST API를 커스텀 도구로 감싼 Python MCP (mcp_server.py). burp-official과 기능 중복 · Custom AI Agent 성격이라 사용자 정책상 미사용.",
        ["burp-ai-agent"] =
            "supergateway를 통한 stdio↔SSE 브리지 (burp-mcp-bridge.ps1). Claude Desktop 등 SSE 미지원 클라이언트용 어댑터. Claude Code 전용 환경에선 burp-official 직접 SSE가 더 나음.",
    };

    private static string GetDescriptionFor(string serverName)
    {
        if (KnownDescriptions.TryGetValue(serverName, out var d)) return d;
        return "커스텀 MCP 서버. 자세한 설명은 등록된 소스 파일 · 저장소 README 참조.";
    }
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private McpServerVm? _selected;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _enabledListText = "";
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _offlineCount;

    private CancellationTokenSource? _probeCts;

    // Global pulse clock is at Services.PulseTicker.Current — bind Opacity there in XAML.

    public ObservableCollection<McpServerVm> Servers { get; } = new();
    public ObservableCollection<McpServerVm> Filtered { get; } = new();

    public McpStatusViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Load();

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void Refresh()
    {
        McpConfigService.InvalidateProbeCache();
        Load();
    }

    private void Load()
    {
        var summary = McpConfigService.Scan();
        Servers.Clear();
        foreach (var s in summary.Servers)
        {
            var endpoint = s.Transport switch
            {
                McpTransport.Stdio => (s.Command ?? "") + (s.Args.Count > 0 ? " " + string.Join(" ", s.Args) : ""),
                McpTransport.Sse or McpTransport.Http => s.Url ?? "",
                _ => ""
            };
            var scope = s.Scope switch
            {
                McpScope.UserGlobal => "global",
                McpScope.ProjectScoped => "project",
                McpScope.OAuthConnector => "oauth",
                _ => ""
            };
            var transport = s.Transport switch
            {
                McpTransport.Stdio => "STDIO",
                McpTransport.Sse => "SSE",
                McpTransport.Http => "HTTP",
                _ => "?"
            };
            string badge;
            if (s.NeedsAuth) badge = "AUTH REQ";
            else if (s.Scope == McpScope.ProjectScoped) badge = "PROJECT";
            else if (s.EnabledInSettings) badge = "ENABLED";
            else badge = "DISABLED";
            bool canToggle = !s.NeedsAuth && s.Scope == McpScope.UserGlobal;

            Servers.Add(new McpServerVm
            {
                Name = s.Name,
                TransportText = transport,
                ScopeText = scope,
                EndpointText = endpoint,
                WorkspaceText = s.Workspace ?? "",
                SourceFile = s.SourceFile ?? "",
                EnabledInSettings = s.EnabledInSettings,
                NeedsAuth = s.NeedsAuth,
                StatusBadge = badge,
                CanToggle = canToggle,
                AuthCacheTs = s.AuthCacheTimestamp is { } ts ? ts.ToString("yyyy-MM-dd HH:mm") : "",
                ConnectionState = s.NeedsAuth ? "OAUTH"
                                : (canToggle && !s.EnabledInSettings) ? "DISABLED"
                                : "CHECKING",
                Source = s,
                ArgsList = s.Args.ToList(),
                EnvList = s.Env.Select(kv => new KeyValuePair<string, string>(kv.Key, McpConfigService.RedactSensitive(kv.Key, kv.Value))).ToList(),
                DescriptionText = GetDescriptionFor(s.Name)
            });
        }

        TotalCount = summary.TotalCount;
        StdioCount = summary.StdioCount;
        RemoteCount = summary.RemoteCount;
        NeedsAuthCount = summary.NeedsAuthCount;
        DisabledCount = Servers.Count(s => s.StatusBadge == "DISABLED");
        EnabledCount = summary.EnabledCount;
        EnabledListText = summary.EnabledInSettings.Count > 0
            ? string.Join(", ", summary.EnabledInSettings)
            : "(none listed in settings.json)";

        ApplyFilter();
        Status = $"{TotalCount} servers · probing…";

        // Kick off async probes (fire-and-forget, updates VMs as each completes)
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        _ = ProbeAllAsync(_probeCts.Token);
    }

    private async Task ProbeAllAsync(CancellationToken ct)
    {
        var tasks = Servers.Select(vm => ProbeOneAsync(vm, ct)).ToList();
        while (tasks.Count > 0 && !ct.IsCancellationRequested)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            RecalcOnlineCounts();
        }
        if (!ct.IsCancellationRequested)
            Status = $"{TotalCount} servers · {OnlineCount} online · {OfflineCount} offline · {DisabledCount} disabled";
    }

    private static async Task ProbeOneAsync(McpServerVm vm, CancellationToken ct)
    {
        var result = await McpConfigService.ProbeAsync(vm.Source, ct);
        // Marshal back to UI thread — property change fires from thread pool otherwise
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            vm.ConnectionState = result switch
            {
                McpConnectionStatus.Online => "ONLINE",
                McpConnectionStatus.Offline => "OFFLINE",
                McpConnectionStatus.OAuth => "OAUTH",
                McpConnectionStatus.Disabled => "DISABLED",
                McpConnectionStatus.Checking => "CHECKING",
                _ => "UNKNOWN"
            };
        });
    }

    private void RecalcOnlineCounts()
    {
        OnlineCount = Servers.Count(s => s.ConnectionState == "ONLINE");
        OfflineCount = Servers.Count(s => s.ConnectionState == "OFFLINE");
    }

    [RelayCommand]
    private async Task ToggleEnabled(McpServerVm? vm)
    {
        if (vm is null || !vm.CanToggle) return;
        try
        {
            var newState = await McpConfigService.ToggleEnabledAsync(vm.Name);
            Status = $"{vm.Name} → {(newState ? "ENABLED" : "DISABLED")} (restart Claude Code to apply)";
            Refresh();
        }
        catch (Exception ex)
        {
            Status = "toggle failed: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        var q = SearchQuery?.Trim() ?? "";
        foreach (var s in Servers)
        {
            if (q.Length == 0
                || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.EndpointText.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.ScopeText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                Filtered.Add(s);
            }
        }
        Selected ??= Filtered.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenSourceFile()
    {
        if (Selected is null || string.IsNullOrEmpty(Selected.SourceFile)) return;
        try
        {
            if (!File.Exists(Selected.SourceFile)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Selected.SourceFile}\"") { UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }

    [RelayCommand]
    private void OpenSettingsInModule()
    {
        // Navigate to SETTINGS module so user can edit enabledMcpjsonServers
        _main.NavigateToFile(ClaudePaths.SettingsJson);
    }

    [RelayCommand]
    private void OpenClaudeJson()
    {
        try
        {
            var path = Path.Combine(ClaudePaths.UserProfile, ".claude.json");
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }
}
