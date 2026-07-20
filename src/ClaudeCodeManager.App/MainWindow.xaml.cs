using System.ComponentModel;
using System.Windows;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App;

public partial class MainWindow : Window
{
    private bool _reallyClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private void Tray_LeftClick(object sender, RoutedEventArgs e)
    {
        if (IsVisible) { Activate(); }
        else { Show(); WindowState = WindowState.Normal; Activate(); }
    }

    private void Tray_ShowClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Tray_SnapshotClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.QuickSnapshotCommand.CanExecute(null) == true) Vm.QuickSnapshotCommand.Execute(null);
    }

    private void Tray_ExitClick(object sender, RoutedEventArgs e)
    {
        _reallyClose = true;
        Vm?.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    private void Caption_Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Caption_MaxRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Caption_Close(object sender, RoutedEventArgs e) => Close();
}
