using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Parsers;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public sealed class ClaudeMdTarget
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists => File.Exists(Path);
}

public partial class ClaudeMdViewModel : ModuleBase
{
    public override string Key => "CLAU";
    public override string Title => "CLAUDE.MD";
    public override string Glyph => "M5,2 H14 L19,7 V22 H5 Z M14,2 V7 H19 M8,12 H16 M8,15 H16 M8,18 H13";

    private readonly MainViewModel _main;
    private bool _suppressLoad;

    public ObservableCollection<ClaudeMdTarget> Targets { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingFilePath))]
    [NotifyPropertyChangedFor(nameof(IsEditingProfile))]
    private ClaudeMdTarget? _selectedTarget;

    [ObservableProperty] private string _editorContent = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _splitSubdir = "claude.d";
    [ObservableProperty] private int _splitLevel = 2;
    [ObservableProperty] private string _splitPreview = "";
    [ObservableProperty] private SplitPlan? _currentPlan;

    public ObservableCollection<ClaudeMdSection> Outline { get; } = new();
    public ObservableCollection<ClaudeMdProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingFilePath))]
    [NotifyPropertyChangedFor(nameof(IsEditingProfile))]
    private ClaudeMdProfile? _selectedProfile;

    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _profileError = "";
    [ObservableProperty] private bool _isOutlineCollapsed = true;
    [ObservableProperty] private bool _isPreviewMode = true;

    [RelayCommand]
    private void ToggleOutline() => IsOutlineCollapsed = !IsOutlineCollapsed;

    [RelayCommand]
    private void TogglePreview() => IsPreviewMode = !IsPreviewMode;

    /// <summary>The file path currently loaded in the editor (profile if selected, else target).</summary>
    public string EditingFilePath =>
        (SelectedProfile is not null && !SelectedProfile.IsActive)
            ? SelectedProfile.FilePath
            : (SelectedTarget?.Path ?? "");

    /// <summary>True when a non-active profile is selected (editor points at CLAUDE.&lt;name&gt;.md, not the active CLAUDE.md).</summary>
    public bool IsEditingProfile => SelectedProfile is not null && !SelectedProfile.IsActive;

    public ClaudeMdViewModel(MainViewModel main) { _main = main; }

    partial void OnSelectedTargetChanged(ClaudeMdTarget? value)
    {
        _suppressLoad = true;
        SelectedProfile = null;
        _suppressLoad = false;
        RefreshProfiles();
        LoadCurrent();
    }

    partial void OnSelectedProfileChanged(ClaudeMdProfile? value)
    {
        if (_suppressLoad) return;
        LoadCurrent();
    }

    partial void OnEditorContentChanged(string value) { IsDirty = true; RefreshOutline(); }

    public override void OnActivated()
    {
        DiscoverTargets();
        if (SelectedTarget is null && Targets.Count > 0) SelectedTarget = Targets[0];
    }

    /// <summary>Only CLAUDE.md and its profile siblings — see <see cref="ModuleBase.DependsOn"/>.</summary>
    public override bool DependsOn(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var fileName = System.IO.Path.GetFileName(path);
        return fileName.StartsWith("CLAUDE", System.StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase);
    }

    public void SelectByPath(string path)
    {
        DiscoverTargets();
        // 1) direct target match (CLAUDE.md of global or per-project)
        var direct = Targets.FirstOrDefault(t => string.Equals(t.Path, path, System.StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            SelectedTarget = direct;
            SelectedProfile = null;
            return;
        }
        // 2) profile file (CLAUDE.<name>.md in the same dir as some target)
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is null) return;
        var containingTarget = Targets.FirstOrDefault(t => string.Equals(System.IO.Path.GetDirectoryName(t.Path), dir, System.StringComparison.OrdinalIgnoreCase));
        if (containingTarget is null) return;
        SelectedTarget = containingTarget;
        RefreshProfiles();
        var fileName = System.IO.Path.GetFileName(path);
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"^CLAUDE\.([A-Za-z0-9_-]+)\.md$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var name = m.Groups[1].Value;
            var profile = Profiles.FirstOrDefault(p => !p.IsActive && string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (profile is not null) SelectedProfile = profile;
        }
    }

    private void DiscoverTargets()
    {
        var current = SelectedTarget?.Path;
        Targets.Clear();
        Targets.Add(new ClaudeMdTarget { Label = "GLOBAL  ·  ~/.claude/CLAUDE.md", Path = ClaudePaths.GlobalClaudeMd });
        if (Directory.Exists(ClaudePaths.ProjectsRoot))
        {
            foreach (var pd in Directory.EnumerateDirectories(ClaudePaths.ProjectsRoot))
            {
                var p = System.IO.Path.Combine(pd, "CLAUDE.md");
                if (File.Exists(p))
                    Targets.Add(new ClaudeMdTarget { Label = $"PROJ    ·  {System.IO.Path.GetFileName(pd)}", Path = p });
            }
        }
        if (current is not null) SelectedTarget = Targets.FirstOrDefault(t => t.Path == current);
    }

    private void LoadCurrent()
    {
        var path = EditingFilePath;
        if (string.IsNullOrEmpty(path)) { EditorContent = ""; return; }
        EditorContent = File.Exists(path) ? File.ReadAllText(path) : "";
        IsDirty = false;
        RefreshOutline();
        var label = IsEditingProfile ? $"profile · CLAUDE.{SelectedProfile!.Name}.md" : "active · CLAUDE.md";
        Status = $"loaded · {label} · {EditorContent.Length} chars";
    }

    private void RefreshProfiles()
    {
        _suppressLoad = true;
        try
        {
            var currentName = SelectedProfile?.Name;
            var wasActive = SelectedProfile?.IsActive ?? false;
            Profiles.Clear();
            if (SelectedTarget is null) return;
            var dir = System.IO.Path.GetDirectoryName(SelectedTarget.Path);
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var p in ClaudeMdProfileManager.List(dir)) Profiles.Add(p);
            if (currentName is not null)
            {
                var match = wasActive
                    ? Profiles.FirstOrDefault(p => p.IsActive)
                    : Profiles.FirstOrDefault(p => !p.IsActive && p.Name == currentName);
                if (match is not null) SelectedProfile = match;
            }
        }
        finally { _suppressLoad = false; }
    }

    private void RefreshOutline()
    {
        Outline.Clear();
        foreach (var s in MarkdownSectionParser.ParseFlat(EditorContent)) Outline.Add(s);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync()
    {
        var path = EditingFilePath;
        if (string.IsNullOrEmpty(path)) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-save · {System.IO.Path.GetFileName(path)}");
        await AtomicFileWriter.WriteAsync(path, EditorContent);
        IsDirty = false;
        Status = $"saved · {path}";
        // When saving a profile we need to refresh the profiles list (size/date updated)
        if (IsEditingProfile) RefreshProfiles();
    }

    [RelayCommand]
    private void Reload() => LoadCurrent();

    [RelayCommand]
    private void PreviewSplit()
    {
        CurrentPlan = ClaudeMdSplitter.Plan(EditorContent, SplitSubdir, SplitLevel);
        if (CurrentPlan.Parts.Count == 0) { SplitPreview = "(no top-level sections found at level " + SplitLevel + ")"; return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- SPLIT PLAN · {CurrentPlan.Parts.Count} files ---\n");
        foreach (var p in CurrentPlan.Parts)
            sb.AppendLine($"  → {p.TargetRelativePath}    ({p.Section.LineCount} lines)");
        sb.AppendLine("\n--- NEW INDEX (overwrites current file) ---\n");
        sb.AppendLine(CurrentPlan.IndexContent);
        SplitPreview = sb.ToString();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ApplySplitAsync()
    {
        if (CurrentPlan is null || CurrentPlan.Parts.Count == 0 || SelectedTarget is null) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-split · {System.IO.Path.GetFileName(SelectedTarget.Path)}");
        await ClaudeMdSplitter.ApplyAsync(CurrentPlan, SelectedTarget.Path, System.IO.Path.GetDirectoryName(SelectedTarget.Path)!);
        LoadCurrent();
        Status = $"split applied · {CurrentPlan.Parts.Count} files written";
        CurrentPlan = null;
        SplitPreview = "";
    }

    // ═══════════════ PROFILES ═══════════════

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsProfileAsync()
    {
        ProfileError = "";
        if (SelectedTarget is null) return;
        var name = (NewProfileName ?? "").Trim();
        if (!ClaudeMdProfileManager.IsValidName(name, out var err)) { ProfileError = err; return; }
        var dir = System.IO.Path.GetDirectoryName(SelectedTarget.Path);
        if (string.IsNullOrEmpty(dir)) { ProfileError = "no target directory"; return; }

        await _main.Snapshots.CreateSnapshotAsync($"pre-profile-save · {name}");
        try
        {
            // Save current editor content directly to new profile file (works whether editing active or another profile)
            var newProfilePath = System.IO.Path.Combine(dir, $"CLAUDE.{name}.md");
            await AtomicFileWriter.WriteAsync(newProfilePath, EditorContent);
            IsDirty = false;
            Status = $"profile saved · CLAUDE.{name}.md";
            NewProfileName = "";
            RefreshProfiles();
        }
        catch (System.Exception ex) { ProfileError = ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ActivateProfileAsync(ClaudeMdProfile? profile)
    {
        ProfileError = "";
        if (profile is null || profile.IsActive || SelectedTarget is null) return;
        var confirm = Views.ConfirmDialog.Show(null,
            "프로파일 활성화",
            $"'{profile.Name}' 프로파일을 활성화할까요?\n\n" +
            "현재 CLAUDE.md는 자동으로 보존됩니다:\n" +
            "  • 내용이 이미 기존 프로파일과 일치하면 별도 백업을 만들지 않습니다.\n" +
            "  • 그 외에는 CLAUDE.autosave-<타임스탬프>.md 로 이름을 바꿔 프로파일로 보관합니다.\n\n" +
            "추가로 git 스냅샷도 남습니다 — 필요 시 SNAPSHOTS 모듈에서 복구 가능.");
        if (!confirm) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-activate · {profile.Name}");
        try
        {
            var result = await ClaudeMdProfileManager.ActivateAsync(profile);
            if (result.PreservedAsProfile is not null)
                Status = $"activated · {profile.Name} · preserved current as '{result.PreservedAsProfile}'";
            else if (result.MatchedExistingProfile is not null)
                Status = $"activated · {profile.Name} · current already saved as '{result.MatchedExistingProfile}'";
            else
                Status = $"activated · {profile.Name}";
            // After activation, switch view back to the active target
            _suppressLoad = true;
            SelectedProfile = null;
            _suppressLoad = false;
            RefreshProfiles();
            LoadCurrent();
        }
        catch (System.Exception ex) { ProfileError = ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RenameProfileAsync(ClaudeMdProfile? profile)
    {
        ProfileError = "";
        if (profile is null || profile.IsActive) return;
        var (ok, newName) = Views.InputDialog.Show(null,
            "프로파일 이름 변경",
            $"'{profile.Name}' 프로파일의 새 이름을 입력하세요.\n영문/숫자/밑줄/하이픈만 허용됩니다.",
            profile.Name,
            v => ClaudeMdProfileManager.IsValidName(v, out var e) ? null : e);
        if (!ok) return;
        if (string.Equals(newName, profile.Name, System.StringComparison.Ordinal)) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-rename-profile · {profile.Name} → {newName}");
        try
        {
            ClaudeMdProfileManager.Rename(profile, newName);
            Status = $"renamed · {profile.Name} → {newName}";
            RefreshProfiles();
        }
        catch (System.Exception ex) { ProfileError = ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteProfileAsync(ClaudeMdProfile? profile)
    {
        ProfileError = "";
        if (profile is null || profile.IsActive) return;
        var confirm = Views.ConfirmDialog.Show(null,
            "프로파일 삭제",
            $"'{profile.Name}' 프로파일을 삭제할까요?\n\n" +
            $"파일: {profile.FilePath}\n\n" +
            "스냅샷으로 복구는 가능하지만 파일 자체는 제거됩니다.",
            Views.ConfirmKind.Danger);
        if (!confirm) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-delete-profile · {profile.Name}");
        try
        {
            ClaudeMdProfileManager.Delete(profile);
            Status = $"deleted · {profile.Name}";
            // If we just deleted the currently displayed profile, revert to active
            if (ReferenceEquals(SelectedProfile, profile))
            {
                _suppressLoad = true;
                SelectedProfile = null;
                _suppressLoad = false;
                LoadCurrent();
            }
            RefreshProfiles();
        }
        catch (System.Exception ex) { ProfileError = ex.Message; }
    }
}
