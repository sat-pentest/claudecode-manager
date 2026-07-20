using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.ViewModels;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() { InitializeComponent(); }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Diagnostic d } && DataContext is DiagnosticsViewModel vm)
        {
            vm.OpenFileCommand.Execute(d);
        }
    }
}
