using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.Rendering;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App.Views;

public partial class AgentsView : UserControl
{
    private INotifyPropertyChanged? _boundVm;

    public AgentsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            RefreshPreviewIfActive();
            Find.Target = AgentPreview;
            Find.DocumentResetRequested += RefreshPreviewIfActive;
        };
    }

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Find is null) return;
        if (DataContext is AgentsViewModel vm && !vm.IsPreviewMode) return;
        Find.Toggle();
        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;
        RefreshPreviewIfActive();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentsViewModel.IsPreviewMode)
         || e.PropertyName == nameof(AgentsViewModel.Selected))
            RefreshPreviewIfActive();
    }

    private void RefreshPreviewIfActive()
    {
        if (AgentPreview is null) return;
        if (DataContext is not AgentsViewModel vm) return;
        if (!vm.IsPreviewMode) return;
        AgentPreview.Document = MarkdownFlowDocumentRenderer.Render(vm.Selected?.Body ?? "");
    }
}
