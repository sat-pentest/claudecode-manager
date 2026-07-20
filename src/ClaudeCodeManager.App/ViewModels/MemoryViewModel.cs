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

    [RelayCommand]
    private void ToggleIndex() => IsIndexCollapsed = !IsIndexCollapsed;

    [RelayCommand]
    private void TogglePreview() => IsPreviewMode = !IsPreviewMode;

    public MemoryViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated()
    {
        var curPath = SelectedProject?.MemoryDir;
        Projects.Clear();
        foreach (var p in MemoryIndexer.DiscoverProjects()) Projects.Add(p);
        SelectedProject = curPath is null ? Projects.FirstOrDefault() : Projects.FirstOrDefault(p => p.MemoryDir == curPath) ?? Projects.FirstOrDefault();
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
        IndexRaw = value is not null && File.Exists(value.IndexFile) ? File.ReadAllText(value.IndexFile) : "";
        UpdateIndexWarning();
        UpdateStats();
    }

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

    private static readonly HashSet<string> StandardTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "feedback", "project", "reference"
    };

    private void ApplyEntryFilter()
    {
        var q = (SearchText ?? "").Trim();
        var typeFilter = TypeFilter ?? "ALL";
        Entries.Clear();

        IEnumerable<MemoryEntry> src = _allEntries;

        if (!string.IsNullOrEmpty(q))
            src = src.Where(e => Matches(e, q));

        if (typeFilter != "ALL")
            src = src.Where(e => MatchesType(e, typeFilter));

        if (OverLengthOnly)
            src = src.Where(IsIndexLineOverLength);

        int hitCount = 0;
        foreach (var e in src) { Entries.Add(e); hitCount++; }

        var hasFilter = !string.IsNullOrEmpty(q) || typeFilter != "ALL" || OverLengthOnly;
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

        _main.Snapshots.CreateSnapshot($"auto-classify · {candidates.Count} entries");

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

        _main.Snapshots.CreateSnapshot($"change type · {target.FileName}");
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
    partial void OnIndexRawChanged(string value) { IsDirty = true; UpdateIndexWarning(); }

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
        _main.Snapshots.CreateSnapshot($"pre-save memory · {SelectedEntry.FileName}");
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
        _main.Snapshots.CreateSnapshot("pre-save MEMORY.md");
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
        _main.Snapshots.CreateSnapshot($"pre-toggle · {entry.FileName}");
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
        var curFile = entry.FilePath;
        OnActivated();
        SelectedEntry = _allEntries.FirstOrDefault(e => e.FilePath == curFile);
    }
}
