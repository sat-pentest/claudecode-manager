using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.Core.Services;

namespace ClaudeCodeManager.App.Views;

public partial class WorkflowsLiveView : UserControl
{
    public WorkflowsLiveView() => InitializeComponent();

    /// <summary>Toggle IsExpanded on the clicked result card.</summary>
    private void OnResultCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is WorkflowResultSummary item)
        {
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }

    /// <summary>Open the agent's raw jsonl log in Explorer (select the file).</summary>
    private void OnOpenAgentLog(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkflowResultSummary item) return;
        if (string.IsNullOrEmpty(item.AgentLogPath) || !File.Exists(item.AgentLogPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.AgentLogPath}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
