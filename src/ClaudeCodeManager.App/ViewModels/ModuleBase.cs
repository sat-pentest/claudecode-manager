using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.ViewModels;

public abstract partial class ModuleBase : ObservableObject
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }

    /// <summary>
    /// Whether a file change should make this module rebuild. The watcher covers all of ~/.claude,
    /// so without this an unrelated write anywhere in the tree rebuilt whatever module was open —
    /// and a rebuild drops the selection. Default stays permissive; modules narrow it themselves.
    /// </summary>
    public virtual bool DependsOn(string path) => true;

    [ObservableProperty] private string _status = "";
}
