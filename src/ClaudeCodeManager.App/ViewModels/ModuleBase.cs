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

    /// <summary>
    /// Whether this is the module on screen.
    ///
    /// The nav row used to be a ToggleButton whose checked state came from a one-way comparison
    /// against the shell's Current. That reads correctly until something toggles the button
    /// itself — a keyboard Space, or an accessibility tool — because a local value replaces the
    /// binding and the row then stops tracking the selection for the rest of the session. Holding
    /// the answer here means the row only ever displays it.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    [ObservableProperty] private string _status = "";
}
