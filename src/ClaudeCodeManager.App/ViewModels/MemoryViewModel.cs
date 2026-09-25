using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class MemoryViewModel : ModuleBase
{
    public override string Key => "MEMO";
    public override string Title => "MEMORY";
    public override string Glyph => "M5,5 H19 V19 H5 Z M9,9 H15 V15 H9 Z M9,2 V5 M12,2 V5 M15,2 V5 M9,19 V22 M12,19 V22 M15,19 V22 M2,9 H5 M2,12 H5 M2,15 H5 M19,9 H22 M19,12 H22 M19,15 H22";

    private readonly MainViewModel _main;
    private readonly List<MemoryEntry> _allEntries = new();

    public ObservableCollection<MemoryProject> Projects { get; } = new();
    [ObservableProperty] private MemoryProject? _selectedProject;
    public ObservableCollection<MemoryEntry> Entries { get; } = new();
    [ObservableProperty] private MemoryEntry? _selectedEntry;
    [ObservableProperty] private string _fmName = "";
    [ObservableProperty] private string _fmDescription = "";
    [ObservableProperty] private string _fmType = "";
    [ObservableProperty] private string _entryBody = "";
    [ObservableProperty] private string _indexRaw = "";
    [ObservableProperty] private string _indexWarning = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStats = "";
    [ObservableProperty] private bool _isIndexCollapsed = true;
    [ObservableProperty] private bool _isPreviewMode = true;

    // ── GRAPH 뷰 ──────────────────────────────────────────────────────────
    // 메모리 포맷은 이미 [[링크]]로 그래프를 이루고 있는데, 목록은 그 구조를 못 보여준다.
    // 링크가 끊긴 곳(오타 / 아직 안 쓴 것)과 아무와도 연결 안 된 고아 항목은 특히 그렇다.
    [ObservableProperty] private bool _isGraphMode;
    [ObservableProperty] private MemoryGraph? _graph;
    [ObservableProperty] private string _graphStats = "";
    /// <summary>캔버스 ↔ 목록 선택 동기화용 슬러그(파일명에서 .md 제거).</summary>
    [ObservableProperty] private string? _selectedSlug;
    /// <summary>그래프에 비활성(.md.disabled) 항목을 포함할지. 이 디렉터리는 3분의 1이 비활성이라
    /// "지금 Claude Code 가 실제로 읽는 것"만 보려면 꺼야 한다.</summary>
    [ObservableProperty] private bool _showDisabledInGraph = true;

    // Memory count statistics + threshold-based load level.
    // Thresholds are pragmatic estimates of how many active memories start affecting Claude Code:
    //   0..80   = HEALTHY   (green)   no measurable impact
    //   80..150 = CAUTION   (warn)    MEMORY.md 200-line truncation risk begins
    //   150..300= WARN      (ember)   semantic search noise · perceptible latency
    //   300+    = CRITICAL  (danger)  definite degradation, prune required
    [ObservableProperty] private int _totalEntries;
    [ObservableProperty] private int _activeEntries;
    [ObservableProperty] private int _disabledEntries;
    [ObservableProperty] private string _loadLevel = "HEALTHY";       // HEALTHY / CAUTION / WARN / CRITICAL

    // Entry filters (composable with SearchText)
    public static IReadOnlyList<string> TypeFilterOptions { get; } = new[]
    {
        "ALL", "user", "feedback", "project", "reference", "untyped"
    };
    [ObservableProperty] private string _typeFilter = "ALL";
    [ObservableProperty] private bool _overLengthOnly;                // filter entries whose MEMORY.md index line > 150 chars

    // Enabled/disabled filter — a disabled entry is the .md.disabled form (or its
    // MEMORY.md line is HTML-commented). Composable with TYPE + search + >150ch.
    public static IReadOnlyList<string> StatusFilterOptions { get; } = new[]
    {
        "ALL", "ACTIVE", "DISABLED"
    };
    [ObservableProperty] private string _statusFilter = "ALL";

    [RelayCommand]
    private void ToggleIndex() => IsIndexCollapsed = !IsIndexCollapsed;

    [RelayCommand]
    private void TogglePreview() => IsPreviewMode = !IsPreviewMode;

    public MemoryViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated()
    {
        var curPath = SelectedProject?.MemoryDir;
        // Rebuilding the project list re-creates every MemoryEntry, so the ListBox loses its
        // selection. Remember it by display name — the one identifier that survives a toggle,
        // which renames the file underneath.
        var curEntry = SelectedEntry?.DisplayName;

        Projects.Clear();
        foreach (var p in MemoryIndexer.DiscoverProjects()) Projects.Add(p);
        SelectedProject = curPath is null ? Projects.FirstOrDefault() : Projects.FirstOrDefault(p => p.MemoryDir == curPath) ?? Projects.FirstOrDefault();

        if (curEntry is not null && SelectedEntry is null)
        {
            var restored = _allEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, curEntry, StringComparison.OrdinalIgnoreCase));
            if (restored is not null && Entries.Contains(restored)) SelectedEntry = restored;
        }
    }

    /// <summary>
    /// Only memory files matter here. The watcher spans all of ~/.claude, and before this the
    /// module rebuilt itself — dropping the selection — whenever anything else in the tree was
    /// written, which during an active Claude Code session is constantly.
    /// </summary>
    public override bool DependsOn(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is null) return false;
        if (Projects.Any(p => string.Equals(p.MemoryDir, dir, StringComparison.OrdinalIgnoreCase))) return true;
        // Not yet loaded, or a project that appeared since: fall back to the directory name.
        return string.Equals(System.IO.Path.GetFileName(dir), "memory", StringComparison.OrdinalIgnoreCase);
    }

    public void SelectByPath(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is null) return;
        // Ensure projects loaded
        if (Projects.Count == 0)
        {
            foreach (var p in MemoryIndexer.DiscoverProjects()) Projects.Add(p);
        }
        var project = Projects.FirstOrDefault(p => string.Equals(p.MemoryDir, dir, System.StringComparison.OrdinalIgnoreCase));
        if (project is null) return;
        SelectedProject = project;
        var fileName = System.IO.Path.GetFileName(path);
        if (string.Equals(fileName, "MEMORY.md", System.StringComparison.OrdinalIgnoreCase))
        {
            // Open MEMORY.md — just switch project and expand index panel
            IsIndexCollapsed = false;
            SelectedEntry = null;
            return;
        }
        var entry = _allEntries.FirstOrDefault(e => string.Equals(e.FilePath, path, System.StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // Might be disabled variant or filename difference — try DisplayName match
            entry = _allEntries.FirstOrDefault(e => string.Equals(e.DisplayName, fileName, System.StringComparison.OrdinalIgnoreCase));
        }
        if (entry is not null)
        {
            // If entry is filtered out by current search, clear the filter so it appears
            if (!Entries.Contains(entry)) SearchText = "";
            SelectedEntry = entry;
        }
    }

    partial void OnSelectedProjectChanged(MemoryProject? value)
    {
        _allEntries.Clear();
        if (value is not null) _allEntries.AddRange(value.Entries);
        ApplyEntryFilter();
        // Loading is not editing — without this guard every reload left the module looking dirty.
        _loadingIndex = true;
        try { IndexRaw = value is not null && File.Exists(value.IndexFile) ? File.ReadAllText(value.IndexFile) : ""; }
        finally { _loadingIndex = false; }
        UpdateIndexWarning();
        UpdateStats();
        RebuildGraph();
    }

    private void RebuildGraph()
    {
        if (_allEntries.Count == 0) { Graph = null; GraphStats = ""; return; }
        var g = MemoryGraphBuilder.Build(_allEntries);
        Graph = g;
        var orphans = g.Orphans.Count();
        GraphStats = $"{g.Nodes.Count(n => !n.IsGhost)} 노드 · {g.Links.Count} 링크 · " +
                     $"오타 {g.MisspelledCount} · 미작성 {g.UnwrittenCount} · 고립 {orphans}";
    }

    /// <summary>
    /// 그래프에서 선택이 넘어왔을 때만 발생한다. 목록에서 직접 고른 경우는 사용자가 이미 어느 행인지
    /// 알고 있으니 강조가 불필요하고, 넣으면 클릭할 때마다 깜빡이는 소음이 된다.
    /// </summary>
    public event Action<MemoryEntry>? GraphSelectionArrived;

    /// <summary>목록 → 그래프 동기화 중임을 표시. 이게 없으면 목록 클릭이 그래프 클릭처럼 보여
    /// 강조가 잘못 터진다.</summary>
    private bool _syncingFromList;

    /// <summary>슬러그로 항목을 고른다 — 그래프에서 노드를 클릭했을 때.</summary>
    partial void OnSelectedSlugChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var hit = _allEntries.FirstOrDefault(e =>
            string.Equals(SlugOf(e), value, StringComparison.OrdinalIgnoreCase));
        // 고스트 노드(아직 없는 파일)는 고를 항목이 없다 — 선택을 그대로 둔다.
        if (hit is null) return;
        if (!ReferenceEquals(hit, SelectedEntry)) SelectedEntry = hit;
        if (!_syncingFromList) GraphSelectionArrived?.Invoke(hit);
    }

    private static string SlugOf(MemoryEntry e)
    {
        var n = e.DisplayName;
        return n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? n[..^3] : n;
    }

    [RelayCommand]
    private void ToggleGraphMode() => IsGraphMode = !IsGraphMode;

    private void UpdateStats()
    {
        TotalEntries = _allEntries.Count;
        ActiveEntries = _allEntries.Count(e => !e.Disabled);
        DisabledEntries = _allEntries.Count(e => e.Disabled);
        // Load level is based on ACTIVE count — disabled entries don't consume context
        LoadLevel = ActiveEntries switch
        {
            < 80 => "HEALTHY",
            < 150 => "CAUTION",
            < 300 => "WARN",
            _ => "CRITICAL"
        };
    }

    partial void OnSearchTextChanged(string value) => ApplyEntryFilter();
    partial void OnTypeFilterChanged(string value) => ApplyEntryFilter();
    partial void OnOverLengthOnlyChanged(bool value) => ApplyEntryFilter();
    partial void OnStatusFilterChanged(string value) => ApplyEntryFilter();

    private static readonly HashSet<string> StandardTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "feedback", "project", "reference"
    };

    private void ApplyEntryFilter()
    {
        var q = (SearchText ?? "").Trim();
        var typeFilter = TypeFilter ?? "ALL";
        var statusFilter = StatusFilter ?? "ALL";
        Entries.Clear();

        IEnumerable<MemoryEntry> src = _allEntries;

        if (!string.IsNullOrEmpty(q))
            src = src.Where(e => Matches(e, q));

        if (typeFilter != "ALL")
            src = src.Where(e => MatchesType(e, typeFilter));

        if (statusFilter == "ACTIVE")
            src = src.Where(e => !e.Disabled);
        else if (statusFilter == "DISABLED")
            src = src.Where(e => e.Disabled);

        if (OverLengthOnly)
            src = src.Where(IsIndexLineOverLength);

        int hitCount = 0;
        foreach (var e in src) { Entries.Add(e); hitCount++; }

        var hasFilter = !string.IsNullOrEmpty(q) || typeFilter != "ALL" || statusFilter != "ALL" || OverLengthOnly;
        SearchStats = hasFilter ? $"{hitCount} / {_allEntries.Count} matches" : "";
    }

    private static bool MatchesType(MemoryEntry e, string typeFilter)
    {
        var t = e.Frontmatter?.Type ?? "";
        return typeFilter switch
        {
            "untyped" => string.IsNullOrWhiteSpace(t),
            _ => string.Equals(t, typeFilter, StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Checks whether this entry's line in MEMORY.md exceeds Linter.MaxIndexItemChars (150).
    /// Scans lines for `](DisplayName)` marker. Returns false if entry isn't in MEMORY.md.
    /// </summary>
    private bool IsIndexLineOverLength(MemoryEntry e)
    {
        if (string.IsNullOrEmpty(IndexRaw) || string.IsNullOrEmpty(e.DisplayName)) return false;
        var marker = "](" + e.DisplayName + ")";
        foreach (var line in IndexRaw.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return line.Length > Linter.MaxIndexItemChars;
        }
        return false;
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = "";
        TypeFilter = "ALL";
        StatusFilter = "ALL";
        OverLengthOnly = false;
    }

    /// <summary>
    /// Bulk-assign the `type:` frontmatter field to every untyped entry based on its filename
    /// prefix. Only handles entries where type is empty AND the filename begins with one of the
    /// four standard prefixes (feedback_, project_, reference_, user_). Everything else is skipped.
    /// Creates a single snapshot before the batch so all changes are rollback-able as one unit.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task AutoClassifyByFilenameAsync()
    {
        if (SelectedProject is null) return;

        // Prepare the candidate list first so we can show the count in confirm.
        var candidates = _allEntries
            .Where(e => string.IsNullOrWhiteSpace(e.Frontmatter?.Type))
            .Select(e => (Entry: e, Inferred: InferTypeFromFilename(e.DisplayName)))
            .Where(t => t.Inferred is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            Status = "auto-classify: nothing to do (no untyped entries with a standard prefix)";
            return;
        }

        var confirm = ClaudeCodeManager.App.Views.ConfirmDialog.Show(null,
            "Auto-classify by filename",
            $"파일명 prefix 기준으로 {candidates.Count}개 untyped 엔트리에 type을 자동 할당합니다.\n\n" +
            $"규칙: feedback_* → feedback, project_* → project, reference_* → reference, user_* → user\n\n" +
            $"실행 전 스냅샷 자동 생성. 결과가 마음에 안 들면 SNAPSHOTS에서 롤백 가능합니다.\n\n계속할까요?",
            ClaudeCodeManager.App.Views.ConfirmKind.Normal);
        if (!confirm) return;

        await _main.Snapshots.CreateSnapshotAsync($"auto-classify · {candidates.Count} entries");
        _main.Watcher.MuteDirectory(SelectedProject.MemoryDir, TimeSpan.FromSeconds(10));

        int applied = 0;
        int failed = 0;
        foreach (var (entry, inferred) in candidates)
        {
            try
            {
                await FrontmatterUpdater.SetKeyAsync(entry.FilePath, "type", inferred);
                entry.Frontmatter.Type = inferred;
                applied++;
            }
            catch { failed++; }
        }

        // Also refresh currently-selected entry's UI Fm* fields if it was affected
        if (SelectedEntry is not null)
        {
            _suppressTypeAutoSave = true;
            try { FmType = SelectedEntry.Frontmatter.Type ?? ""; }
            finally { _suppressTypeAutoSave = false; }
        }

        ApplyEntryFilter();
        UpdateStats();

        int totalUntyped = _allEntries.Count(e => string.IsNullOrWhiteSpace(e.Frontmatter?.Type));
        Status = failed == 0
            ? $"auto-classify · {applied} classified · {totalUntyped} still untyped (no standard prefix)"
            : $"auto-classify · {applied} classified · {failed} failed · {totalUntyped} still untyped";
    }

    private static string? InferTypeFromFilename(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;
        var underscoreIdx = displayName.IndexOf('_');
        if (underscoreIdx <= 0) return null;
        var prefix = displayName[..underscoreIdx].ToLowerInvariant();
        return StandardTypes.Contains(prefix) ? prefix : null;
    }

    private static bool Matches(MemoryEntry entry, string query)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (entry.FileName.IndexOf(query, cmp) >= 0) return true;
        if (!string.IsNullOrEmpty(entry.Frontmatter.Name) && entry.Frontmatter.Name.IndexOf(query, cmp) >= 0) return true;
        if (!string.IsNullOrEmpty(entry.Frontmatter.Description) && entry.Frontmatter.Description.IndexOf(query, cmp) >= 0) return true;
        if (!string.IsNullOrEmpty(entry.Body) && entry.Body.IndexOf(query, cmp) >= 0) return true;
        return false;
    }

    // Guard: when SelectedEntry changes we push its values into FmName/FmDescription/FmType.
    // Those setters would otherwise trigger the "user changed the type" auto-save path.
    private bool _suppressTypeAutoSave;

    partial void OnSelectedEntryChanged(MemoryEntry? value)
    {
        _suppressTypeAutoSave = true;
        try
        {
            if (value is null) { FmName = FmDescription = FmType = EntryBody = ""; return; }
            FmName = value.Frontmatter.Name ?? "";
            FmDescription = value.Frontmatter.Description ?? "";
            FmType = value.Frontmatter.Type ?? "";
            EntryBody = value.Body;
            IsDirty = false;
            // 목록에서 고른 항목을 그래프 선택에도 반영 — 두 뷰가 같은 항목을 가리키게.
            var slug = SlugOf(value);
            if (!string.Equals(SelectedSlug, slug, StringComparison.OrdinalIgnoreCase))
            {
                _syncingFromList = true;
                try { SelectedSlug = slug; }
                finally { _syncingFromList = false; }
            }
        }
        finally { _suppressTypeAutoSave = false; }
    }

    partial void OnFmNameChanged(string value) => IsDirty = true;
    partial void OnFmDescriptionChanged(string value) => IsDirty = true;
    partial void OnFmTypeChanged(string value)
    {
        IsDirty = true;
        if (_suppressTypeAutoSave) return;
        // Auto-save type only — leaves name/description/body dirty until user hits SAVE ENTRY.
        _ = SaveTypeOnlyAsync(value);
    }
    partial void OnEntryBodyChanged(string value) => IsDirty = true;

    private async System.Threading.Tasks.Task SaveTypeOnlyAsync(string newType)
    {
        var target = SelectedEntry;
        if (target is null) return;
        var cur = target.Frontmatter.Type ?? "";
        var next = (newType ?? "").Trim();
        if (string.Equals(cur, next, StringComparison.Ordinal)) return;

        await _main.Snapshots.CreateSnapshotAsync($"change type · {target.FileName}");
        _main.Watcher.MuteDirectory(System.IO.Path.GetDirectoryName(target.FilePath) ?? "", TimeSpan.FromSeconds(3));
        try
        {
            // Surgical single-line edit — leaves description/name/body untouched.
            var newVal = string.IsNullOrWhiteSpace(next) ? null : next;
            await FrontmatterUpdater.SetKeyAsync(target.FilePath, "type", newVal);
            target.Frontmatter.Type = next;
            Status = $"type → {(string.IsNullOrEmpty(next) ? "(cleared)" : next)}  ·  {target.FileName}";
            // Refresh stats since type distribution changed
            UpdateStats();
        }
        catch (Exception ex)
        {
            Status = "type save failed: " + ex.Message;
        }
    }
    private bool _loadingIndex;
    partial void OnIndexRawChanged(string value)
    {
        if (!_loadingIndex) IsDirty = true;
        UpdateIndexWarning();
    }

    private void UpdateIndexWarning()
    {
        var lines = (IndexRaw ?? "").Replace("\r\n", "\n").Split('\n').Length;
        var msgs = new System.Collections.Generic.List<string>();
        if (lines > Linter.MaxIndexLines) msgs.Add($"⚠ {lines} lines > {Linter.MaxIndexLines} (overflow truncated)");
        var long_items = (IndexRaw ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Length > Linter.MaxIndexItemChars).Count();
        if (long_items > 0) msgs.Add($"⚠ {long_items} items > {Linter.MaxIndexItemChars} chars");
        IndexWarning = string.Join("   ", msgs);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveEntryAsync()
    {
        if (SelectedEntry is null) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-save memory · {SelectedEntry.FileName}");
        _main.Watcher.MuteDirectory(System.IO.Path.GetDirectoryName(SelectedEntry.FilePath) ?? "", TimeSpan.FromSeconds(3));
        SelectedEntry.Frontmatter.Name = FmName;
        SelectedEntry.Frontmatter.Description = FmDescription;
        SelectedEntry.Frontmatter.Type = FmType;
        SelectedEntry.Body = EntryBody;
        await MemoryIndexer.SaveEntryAsync(SelectedEntry);
        IsDirty = false;
        Status = $"saved · {SelectedEntry.FileName}";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveIndexAsync()
    {
        if (SelectedProject is null) return;
        await _main.Snapshots.CreateSnapshotAsync("pre-save MEMORY.md");
        _main.Watcher.MuteDirectory(SelectedProject.MemoryDir, TimeSpan.FromSeconds(3));
        await AtomicFileWriter.WriteAsync(SelectedProject.IndexFile, IndexRaw);
        IsDirty = false;
        Status = $"saved · MEMORY.md";
    }

    [RelayCommand]
    private void ReloadProject() => OnActivated();

    [RelayCommand]
    private void SearchClear() => SearchText = "";

    [RelayCommand]
    private async System.Threading.Tasks.Task ToggleEntryAsync(MemoryEntry? entry)
    {
        if (entry is null || SelectedProject is null) return;
        if (_toggleBusy) return;                       // 클릭 연타로 스냅샷이 겹치지 않게
        _toggleBusy = true;
        try
        {
            Status = (entry.Disabled ? "enabling" : "disabling") + " · " + entry.DisplayName + " …";

            // 스냅샷은 파일을 건드리기 전에 찍혀야 되돌릴 수 있다 — 그래서 await 한다.
            // 대신 UI 스레드 밖에서 돌리므로 창이 멈추지 않는다.
            await _main.Snapshots.CreateSnapshotAsync($"pre-toggle · {entry.FileName}");

            // 이어지는 rename/MEMORY.md 쓰기는 우리 것이다. 워처가 이걸 외부 변경으로 되돌려
            // 보내면 아래 재로드가 한 번 더 돌면서 선택이 날아간다.
            _main.Watcher.MuteDirectory(SelectedProject.MemoryDir, TimeSpan.FromSeconds(3));

            try
            {
                await MemoryIndexer.ToggleEntryAsync(entry, SelectedProject.IndexFile);
                Status = entry.Disabled
                    ? $"disabled · {entry.DisplayName}"
                    : $"enabled · {entry.DisplayName}";
            }
            catch (System.Exception ex)
            {
                Status = "toggle error: " + ex.Message;
                return;
            }

            // 상태 반영을 위해 프로젝트 재로드 (SelectedEntry는 새 인스턴스로 유지)
            var displayName = entry.DisplayName;
            OnActivated();
            SelectedEntry = _allEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _toggleBusy = false; }
    }

    private bool _toggleBusy;

    /// <summary>
    /// Delete a memory entry file after confirmation. Also removes any matching
    /// line in MEMORY.md index. A pre-delete snapshot is created so it's recoverable.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteEntryAsync(MemoryEntry? entry)
    {
        if (entry is null || SelectedProject is null) return;

        var result = System.Windows.MessageBox.Show(
            $"이 memory entry를 삭제하시겠습니까?\n\n{entry.FileName}\n\n" +
            "· 원본 .md 파일 삭제\n" +
            "· MEMORY.md 인덱스에서 매칭 라인 자동 제거\n" +
            "· pre-delete 스냅샷 자동 생성 (복구 가능)",
            "MEMORY entry 삭제",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);
        if (result != System.Windows.MessageBoxResult.OK) return;

        await _main.Snapshots.CreateSnapshotAsync($"pre-delete · {entry.FileName}");
        _main.Watcher.MuteDirectory(SelectedProject.MemoryDir, TimeSpan.FromSeconds(3));

        var filePath = entry.FilePath;
        try
        {
            // 1) Try to remove matching index line first (best-effort)
            var indexFile = SelectedProject.IndexFile;
            if (System.IO.File.Exists(indexFile))
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(entry.FileName);
                var lines = System.IO.File.ReadAllLines(indexFile);
                var kept = lines.Where(l => !l.Contains(entry.FileName, System.StringComparison.OrdinalIgnoreCase)
                                            && !(l.TrimStart().StartsWith("<!--") && l.Contains(entry.FileName, System.StringComparison.OrdinalIgnoreCase)))
                                .ToArray();
                if (kept.Length != lines.Length)
                    System.IO.File.WriteAllLines(indexFile, kept);
            }

            // 2) Delete the .md file
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);

            Status = $"deleted · {entry.DisplayName}";
        }
        catch (System.Exception ex)
        {
            Status = "delete error: " + ex.Message;
            return;
        }

        OnActivated();
        SelectedEntry = null;
    }
}
