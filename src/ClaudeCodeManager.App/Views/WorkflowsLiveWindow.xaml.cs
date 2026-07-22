using System.Windows;

namespace ClaudeCodeManager.App.Views;

public partial class WorkflowsLiveWindow : Window
{
    public WorkflowsLiveWindow() => InitializeComponent();

    private void Caption_Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Caption_MaxRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Caption_Close(object sender, RoutedEventArgs e) => Close();
}
