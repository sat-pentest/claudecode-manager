using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.ViewModels;

public abstract partial class ModuleBase : ObservableObject
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }

    [ObservableProperty] private string _status = "";
}
