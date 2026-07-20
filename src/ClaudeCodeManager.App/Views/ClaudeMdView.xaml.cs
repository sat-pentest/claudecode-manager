using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeManager.App.Rendering;
using ClaudeCodeManager.App.ViewModels;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.App.Views;

public partial class ClaudeMdView : UserControl
{
    private INotifyPropertyChanged? _boundVm;

    public ClaudeMdView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            ApplyOutlineCollapsed();
            RefreshPreviewIfActive();
            Find.Target = Preview;
            Find.DocumentResetRequested += RefreshPreviewIfActive;
        };
    }

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Find is null) return;
        if (DataContext is ClaudeMdViewModel vm && !vm.IsPreviewMode)
        {
            // In editor mode, let AvalonEdit handle Ctrl+F if it wants (its own search panel)
            return;
        }
        Find.Toggle();
        e.Handled = true;
    }

    private void OnProfileNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ClaudeCodeManager.Core.Models.ClaudeMdProfile profile) return;
        if (profile.IsActive) return;
        if (DataContext is not ClaudeMdViewModel vm) return;
        vm.RenameProfileCommand.Execute(profile);
        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;
        ApplyOutlineCollapsed();
        RefreshPreviewIfActive();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeMdViewModel.IsOutlineCollapsed))
            ApplyOutlineCollapsed();
        else if (e.PropertyName == nameof(ClaudeMdViewModel.IsPreviewMode)
              || e.PropertyName == nameof(ClaudeMdViewModel.EditorContent))
            RefreshPreviewIfActive();
    }

    private void RefreshPreviewIfActive()
    {
        if (Preview is null) return;
        if (DataContext is not ClaudeMdViewModel vm) return;
        if (!vm.IsPreviewMode) return;
        Preview.Document = MarkdownFlowDocumentRenderer.Render(vm.EditorContent);
    }

    private void ApplyOutlineCollapsed()
    {
        if (DataContext is not ClaudeMdViewModel vm) return;
        if (vm.IsOutlineCollapsed)
        {
            OutlineColumn.Width = new GridLength(42);
            OutlineSplitterColumn.Width = new GridLength(0);
        }
        else
        {
            OutlineColumn.Width = new GridLength(320);
            OutlineSplitterColumn.Width = new GridLength(4);
        }
    }

    private void OnOutlineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not ClaudeMdSection section) return;
        if (DataContext is not ClaudeMdViewModel vm) return;

        // Ensure EDITOR tab is active (not SPLIT PLAN)
        if (EditorTabs is not null && EditorTabs.SelectedIndex != 0) EditorTabs.SelectedIndex = 0;

        if (vm.IsPreviewMode)
        {
            JumpInPreview(section);
        }
        else
        {
            JumpInEditor(section);
        }
    }

    private void JumpInEditor(ClaudeMdSection section)
    {
        if (Editor is null || Editor.Document is null) return;
        var lineNumber = section.StartLine + 1;   // ClaudeMdSection is 0-based, AvalonEdit is 1-based
        if (lineNumber < 1) lineNumber = 1;
        if (lineNumber > Editor.Document.LineCount) lineNumber = Editor.Document.LineCount;

        var docLine = Editor.Document.GetLineByNumber(lineNumber);
        Editor.ScrollToLine(lineNumber);
        Editor.CaretOffset = docLine.Offset;
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
    }

    private void JumpInPreview(ClaudeMdSection section)
    {
        if (Preview?.Document is null) return;
        // Find the paragraph tagged with matching source line
        foreach (var block in Preview.Document.Blocks)
        {
            if (block is System.Windows.Documents.Paragraph p && p.Tag is int line && line == section.StartLine)
            {
                p.BringIntoView();
                return;
            }
        }
        // Fallback: find the closest tagged heading at or before StartLine (in case line numbers drift)
        System.Windows.Documents.Paragraph? best = null;
        int bestLine = -1;
        foreach (var block in Preview.Document.Blocks)
        {
            if (block is System.Windows.Documents.Paragraph p && p.Tag is int line)
            {
                if (line <= section.StartLine && line > bestLine)
                {
                    best = p;
                    bestLine = line;
                }
            }
        }
        best?.BringIntoView();
    }
}
