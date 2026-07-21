using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App.Views;

public partial class WorkflowsView : UserControl
{
    // Zoom bounds — keep cards legible but let power-users go wide/tight.
    private const double ZoomMin = 0.6;
    private const double ZoomMax = 2.2;
    private const double ZoomStep = 0.1;

    public WorkflowsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ctrl+MouseWheel over the PIPELINE grid → adjust the ScaleTransform.
    /// Without Ctrl the event is left alone so the outer ScrollViewer still scrolls normally.
    /// </summary>
    private void OnPipelineWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        if (DataContext is not WorkflowsViewModel vm) return;
        var step = e.Delta > 0 ? ZoomStep : -ZoomStep;
        vm.PipelineZoom = Math.Clamp(vm.PipelineZoom + step, ZoomMin, ZoomMax);
        e.Handled = true;   // suppress scroll while zooming
    }

    /// <summary>
    /// Double-click in the pipeline area resets zoom to 100%. Grid (Panel) doesn't expose
    /// MouseDoubleClick — that's a Control-only event — so we detect via ClickCount==2 on
    /// the left-button-down handler.
    /// </summary>
    private void OnPipelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (DataContext is not WorkflowsViewModel vm) return;
        vm.PipelineZoom = 1.0;
        e.Handled = true;
    }
}
