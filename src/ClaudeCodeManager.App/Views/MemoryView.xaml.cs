using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.Rendering;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App.Views;

public partial class MemoryView : UserControl
{
    private INotifyPropertyChanged? _boundVm;

    public MemoryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            ApplyIndexCollapsed();
            RefreshBodyPreviewIfActive();
            Find.Target = BodyPreview;
            Find.DocumentResetRequested += RefreshBodyPreviewIfActive;
        };
    }

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Find is null) return;
        if (DataContext is MemoryViewModel vm && !vm.IsPreviewMode) return;
        Find.Toggle();
        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;
        ApplyIndexCollapsed();
        RefreshBodyPreviewIfActive();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryViewModel.IsIndexCollapsed))
            ApplyIndexCollapsed();
        else if (e.PropertyName == nameof(MemoryViewModel.IsPreviewMode)
              || e.PropertyName == nameof(MemoryViewModel.EntryBody))
            RefreshBodyPreviewIfActive();
    }

    private void RefreshBodyPreviewIfActive()
    {
        if (BodyPreview is null) return;
        if (DataContext is not MemoryViewModel vm) return;
        if (!vm.IsPreviewMode) return;
        BodyPreview.Document = MarkdownFlowDocumentRenderer.Render(vm.EntryBody);
    }

    // Popup close via X button — toggles the ToggleButton off so IsOpen goes false.
    private void OnCloseHelpClicked(object sender, RoutedEventArgs e)
    {
        if (HelpToggle is not null) HelpToggle.IsChecked = false;
    }

    private void OnCloseTypeHelpClicked(object sender, RoutedEventArgs e)
    {
        if (TypeHelpToggle is not null) TypeHelpToggle.IsChecked = false;
    }

    private void ApplyIndexCollapsed()
    {
        if (DataContext is not MemoryViewModel vm) return;
        if (vm.IsIndexCollapsed)
        {
            IndexColumn.Width = new GridLength(42);
            IndexSplitterColumn.Width = new GridLength(0);
        }
        else
        {
            IndexColumn.Width = new GridLength(380);
            IndexSplitterColumn.Width = new GridLength(4);
        }
    }
}
