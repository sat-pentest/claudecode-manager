using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App.Views;

public partial class SessionsView : UserControl
{
    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private INotifyPropertyChanged? _boundVm;

    public SessionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionsViewModel.SelectedProject))
            Dispatcher.BeginInvoke(new Action(ApplySort));
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (header.Column is null) return;

        var text = StripIndicator(header.Column.Header?.ToString() ?? "");
        var prop = MapHeader(text);
        if (prop is null) return;

        if (_sortProperty == prop)
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        else
        {
            _sortProperty = prop;
            // First click on new column: Size/Modified/Age default to descending (biggest/newest first),
            // Session title defaults to ascending (A→Z).
            _sortDirection = prop == nameof(SessionsFileVm.DisplayLabel)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
        }

        UpdateHeaderIndicators();
        ApplySort();
    }

    private void ApplySort()
    {
        if (FilesList?.ItemsSource is null || _sortProperty is null) return;
        var view = CollectionViewSource.GetDefaultView(FilesList.ItemsSource);
        if (view is null) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
        view.Refresh();
        UpdateHeaderIndicators();
    }

    private void UpdateHeaderIndicators()
    {
        if (FilesList?.View is not GridView gv) return;
        foreach (var col in gv.Columns)
        {
            var text = StripIndicator(col.Header?.ToString() ?? "");
            var mapped = MapHeader(text);
            col.Header = (mapped == _sortProperty && _sortProperty is not null)
                ? text + (_sortDirection == ListSortDirection.Ascending ? "  ▲" : "  ▼")
                : text;
        }
    }

    private static string StripIndicator(string s)
        => s.Replace("  ▲", "").Replace("  ▼", "").Trim();

    private static string? MapHeader(string text) => text switch
    {
        "Session" or "File" => nameof(SessionsFileVm.DisplayLabel),
        "Size" => nameof(SessionsFileVm.Bytes),
        "Modified" => nameof(SessionsFileVm.ModifiedAt),
        "Age" => nameof(SessionsFileVm.AgeDays),
        _ => null
    };
}
