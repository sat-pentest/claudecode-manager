using System;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeCodeManager.Core.Models;
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

    private MemoryViewModel? _pingVm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;

        if (_pingVm is not null) _pingVm.GraphSelectionArrived -= OnGraphSelectionArrived;
        _pingVm = e.NewValue as MemoryViewModel;
        if (_pingVm is not null) _pingVm.GraphSelectionArrived += OnGraphSelectionArrived;
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

    private void OnGraphRelayout(object sender, RoutedEventArgs e) => GraphCanvas?.Relayout();

    /// <summary>
    /// 그래프에서 노드를 고르면 좌측 목록의 해당 행으로 스크롤하고 한 번 강조한다.
    ///
    /// 선택 자체는 이미 바인딩으로 넘어가지만, 목록이 101행이라 고른 항목이 화면 밖이면
    /// 아무 일도 일어나지 않은 것처럼 보였다. 스크롤만 하면 이번엔 "어느 행이 바뀐 건지"를
    /// 눈으로 찾아야 하므로, 도착 지점을 한 번 짚어주는 신호까지 같이 준다.
    /// </summary>
    private void OnGraphSelectionArrived(MemoryEntry entry)
    {
        // 목록이 아직 레이아웃되지 않았거나(그래프만 보이는 중) 컨테이너가 가상화로 없을 수 있으니
        // 렌더 직전 우선순위로 미뤄서 실행한다.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                EntriesList.ScrollIntoView(entry);
                EntriesList.UpdateLayout();
                if (EntriesList.ItemContainerGenerator.ContainerFromItem(entry) is not ListBoxItem row) return;
                PlayRowPing(row);
            }
            catch { /* 필터가 바뀌어 항목이 목록에 없으면 강조할 대상도 없다 */ }
        }));
    }

    private static void PlayRowPing(DependencyObject row)
    {
        var wash = FindChild<Border>(row, "PingWash");
        var bar = FindChild<Border>(row, "PingBar");
        if (wash is null || bar is null) return;

        // 엠버 한 번 훑고 감쇠 — CCM 의 다른 강조(HARNESS 체인, PIPELINE 스윕)와 같은 어휘.
        // 지속 하이라이트가 아니라 "여기다" 하고 한 번 짚는 신호라 1초 안에 끝난다.
        var sb = new Storyboard();

        var washFade = new DoubleAnimationUsingKeyFrames();
        washFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        washFade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        washFade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        washFade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Storyboard.SetTarget(washFade, wash);
        Storyboard.SetTargetProperty(washFade, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(washFade);

        var barFade = washFade.Clone();
        Storyboard.SetTarget(barFade, bar);
        Storyboard.SetTargetProperty(barFade, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(barFade);

        // 좌측 바가 위아래로 펼쳐지며 들어온다 — 밝기만 변하면 눈이 잘 못 잡는다(면적 > 밝기).
        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } });
        Storyboard.SetTarget(grow, bar);
        Storyboard.SetTargetProperty(grow,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        sb.Children.Add(grow);

        sb.Begin();
    }

    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && t.Name == name) return t;
            var deep = FindChild<T>(c, name);
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>그래프에서 노드를 더블클릭 — 그 항목을 열고 편집기로 돌아간다.</summary>
    private void OnGraphNodeActivated(object? sender, string slug)
    {
        if (DataContext is not MemoryViewModel vm) return;
        vm.SelectedSlug = slug;
        vm.IsGraphMode = false;
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
