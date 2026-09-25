using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ClaudeCodeManager.Core.Paths;
using ClaudeCodeManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCodeManager.App.ViewModels;

public sealed class HookRow
{
    public string Event { get; set; } = "";
    public string Matcher { get; set; } = "";
    public string Command { get; set; } = "";
}

public partial class SettingsViewModel : ModuleBase
{
    public override string Key => "SETT";
    public override string Title => "SETTINGS";
    public override string Glyph => "M12,1 V5 M12,19 V23 M4.22,4.22 L7.05,7.05 M16.95,16.95 L19.78,19.78 M1,12 H5 M19,12 H23 M4.22,19.78 L7.05,16.95 M16.95,7.05 L19.78,4.22 M8,12 A4,4 0 1 1 16,12 A4,4 0 1 1 8,12 Z";

    private readonly MainViewModel _main;

    [ObservableProperty] private string _activeFile = "settings.json";
    public ObservableCollection<string> AllowList { get; } = new();
    public ObservableCollection<string> DenyList { get; } = new();
    public ObservableCollection<HookRow> Hooks { get; } = new();
    [ObservableProperty] private string _rawJson = "";
    [ObservableProperty] private string _newAllowEntry = "";
    [ObservableProperty] private string _newDenyEntry = "";
    [ObservableProperty] private string _jsonValidity = "";

    private SettingsBundle? _bundle;

    public SettingsViewModel(MainViewModel main) { _main = main; }

    public override void OnActivated() => Load();

    /// <summary>Only the three files this module edits — see <see cref="ModuleBase.DependsOn"/>.</summary>
    public override bool DependsOn(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var fileName = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return fileName is "settings.json" or "settings.local.json" or "keybindings.json";
    }

    public void SelectByPath(string path)
    {
        var fileName = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (fileName == "settings.json" || fileName == "settings.local.json" || fileName == "keybindings.json")
            ActiveFile = fileName;
    }

    partial void OnActiveFileChanged(string value) => Load();

    private void Load()
    {
        var path = ActiveFile == "settings.local.json" ? ClaudePaths.LocalSettingsJson :
                   ActiveFile == "keybindings.json" ? ClaudePaths.KeybindingsJson :
                   ClaudePaths.SettingsJson;
        _bundle = SettingsService.Load(path);
        AllowList.Clear();
        DenyList.Clear();
        Hooks.Clear();
        foreach (var a in SettingsService.ListPermissionAllow(_bundle)) AllowList.Add(a);
        foreach (var d in SettingsService.ListPermissionDeny(_bundle)) DenyList.Add(d);
        foreach (var h in SettingsService.ListHooks(_bundle)) Hooks.Add(new HookRow { Event = h.Event, Matcher = h.Matcher, Command = h.Command });
        RawJson = _bundle.Root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        JsonValidity = _bundle.Exists ? "valid · " + new FileInfo(path).Length + " bytes" : "(file does not exist yet)";
        Status = $"loaded · {path}";
    }

    [RelayCommand]
    private void AddAllow()
    {
        if (string.IsNullOrWhiteSpace(NewAllowEntry) || _bundle is null) return;
        var perms = _bundle.Root["permissions"] as JsonObject ?? new JsonObject();
        var arr = perms["allow"] as JsonArray ?? new JsonArray();
        arr.Add(NewAllowEntry.Trim());
        perms["allow"] = arr;
        _bundle.Root["permissions"] = perms;
        AllowList.Add(NewAllowEntry.Trim());
        NewAllowEntry = "";
        RawJson = _bundle.Root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    private void RemoveAllow(string? entry)
    {
        if (entry is null || _bundle is null) return;
        if (_bundle.Root["permissions"] is JsonObject perms && perms["allow"] is JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == entry) arr.RemoveAt(i);
            }
        }
        AllowList.Remove(entry);
        RawJson = _bundle.Root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    private void AddDeny()
    {
        if (string.IsNullOrWhiteSpace(NewDenyEntry) || _bundle is null) return;
        var perms = _bundle.Root["permissions"] as JsonObject ?? new JsonObject();
        var arr = perms["deny"] as JsonArray ?? new JsonArray();
        arr.Add(NewDenyEntry.Trim());
        perms["deny"] = arr;
        _bundle.Root["permissions"] = perms;
        DenyList.Add(NewDenyEntry.Trim());
        NewDenyEntry = "";
        RawJson = _bundle.Root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    private void RemoveDeny(string? entry)
    {
        if (entry is null || _bundle is null) return;
        if (_bundle.Root["permissions"] is JsonObject perms && perms["deny"] is JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == entry) arr.RemoveAt(i);
            }
        }
        DenyList.Remove(entry);
        RawJson = _bundle.Root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (_bundle is null) return;
        try
        {
            var parsed = JsonNode.Parse(RawJson) as JsonObject;
            if (parsed is null) { JsonValidity = "ERROR: not a JSON object"; return; }
            _bundle.Root = parsed;
        }
        catch (System.Exception ex)
        {
            JsonValidity = "ERROR: " + ex.Message;
            return;
        }
        await _main.Snapshots.CreateSnapshotAsync($"pre-save · {System.IO.Path.GetFileName(_bundle.Path)}");
        _main.Watcher.MuteDirectory(System.IO.Path.GetDirectoryName(_bundle.Path) ?? "", System.TimeSpan.FromSeconds(3));
        await SettingsService.SaveAsync(_bundle);
        Status = $"saved · {_bundle.Path}";
        Load();
    }

    [RelayCommand]
    private void ValidateJson()
    {
        try
        {
            var p = JsonNode.Parse(RawJson);
            JsonValidity = p is JsonObject ? "valid JSON object" : "valid JSON but not an object";
        }
        catch (System.Exception ex) { JsonValidity = "ERROR: " + ex.Message; }
    }
}
