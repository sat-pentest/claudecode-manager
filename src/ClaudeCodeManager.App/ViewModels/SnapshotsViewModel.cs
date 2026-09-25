using System.Collections.ObjectModel;
using ClaudeCodeManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public partial class SnapshotsViewModel : ModuleBase
{
    public override string Key => "SNAP";
    public override string Title => "SNAPSHOTS";
    public override string Glyph => "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M12,7 V12 L16,14";

    private readonly MainViewModel _main;
    public ObservableCollection<Snapshot> Items { get; } = new();
    [ObservableProperty] private Snapshot? _selected;
    [ObservableProperty] private string _diff = "";
    [ObservableProperty] private string _newMessage = "";

    public SnapshotsViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Items.Clear();
        foreach (var s in _main.Snapshots.ListSnapshots(200)) Items.Add(s);
        Status = $"{Items.Count} snapshots";
    }

    partial void OnSelectedChanged(Snapshot? value)
    {
        Diff = value is null ? "" : _main.Snapshots.GetCommitDiff(value.CommitSha);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateAsync()
    {
        var msg = string.IsNullOrWhiteSpace(NewMessage) ? $"manual @ {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}" : NewMessage;
        var s = await _main.Snapshots.CreateSnapshotAsync(msg);
        Status = $"created · {s.ShortSha}";
        NewMessage = "";
        Refresh();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RestoreAsync()
    {
        if (Selected is null) return;
        var confirm = Views.ConfirmDialog.Show(null,
            "스냅샷 복구",
            $"스냅샷 {Selected.ShortSha} 로 복구할까요?\n\n" +
            $"메시지: {Selected.Message}\n\n" +
            "현재 ~/.claude/ 하위 파일이 이 스냅샷 내용으로 덮어씌워집니다.\n" +
            "복구 직전 현재 상태의 스냅샷이 자동으로 하나 더 남습니다.",
            Views.ConfirmKind.Danger);
        if (!confirm) return;
        await _main.Snapshots.CreateSnapshotAsync($"pre-restore (was at {Selected.ShortSha})");
        // A restore rewrites a large part of ~/.claude; without muting, every file lands as an
        // "external change" and whichever module is open rebuilds itself mid-restore.
        _main.Watcher.MuteDirectory(ClaudeCodeManager.Core.Paths.ClaudePaths.ClaudeRoot, System.TimeSpan.FromSeconds(20));
        _main.Snapshots.RestoreSnapshot(Selected.CommitSha);
        Status = $"restored · {Selected.ShortSha}";
        Refresh();
    }
}
