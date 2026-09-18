using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// App-wide "is the UI actually on screen" signal.
///
/// The shell hides to the tray on close and minimises to the taskbar, and in both states every
/// module timer keeps hitting disk on its normal cadence for data nobody can see. Pollers
/// subscribe here and stand down while hidden, then catch up on the way back.
///
/// Visible means the window is shown AND not minimised. Occlusion by another window does not
/// count — WPF cannot observe it cheaply, and a half-covered window is still being watched.
/// </summary>
public sealed partial class AppVisibility : ObservableObject
{
    public static AppVisibility Current { get; } = new();

    /// <summary>True while the shell is on screen. Starts optimistic so pollers created before
    /// <see cref="Attach"/> runs are not stalled waiting for a window that already exists.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Raised on the hidden -> visible edge, after <see cref="IsVisible"/> is updated.</summary>
    public event EventHandler? Shown;

    private AppVisibility() { }

    /// <summary>Bind to the shell window. Call once, from the window constructor.</summary>
    public void Attach(Window window)
    {
        window.IsVisibleChanged += (_, _) => Update(window);
        window.StateChanged += (_, _) => Update(window);
        Update(window);
    }

    private void Update(Window window)
    {
        var visible = window.IsVisible && window.WindowState != WindowState.Minimized;
        if (visible == IsVisible) return;
        IsVisible = visible;
        if (visible) Shown?.Invoke(this, EventArgs.Empty);
    }
}
