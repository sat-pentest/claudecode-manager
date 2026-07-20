using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.ViewModels;
using ClaudeCodeManager.Core.Services;

namespace ClaudeCodeManager.App.Views;

public partial class SearchView : UserControl
{
    public SearchView() { InitializeComponent(); }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SearchHit hit } && DataContext is SearchViewModel vm)
        {
            vm.NavigateCommand.Execute(hit);
        }
    }
}
