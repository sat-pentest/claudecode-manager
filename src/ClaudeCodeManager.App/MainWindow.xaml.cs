using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClaudeCodeManager.App.Services;
using ClaudeCodeManager.App.ViewModels;

namespace ClaudeCodeManager.App;

public partial class MainWindow : Window
{
    private bool _reallyClose;

    public MainWindow()
    {
        InitializeComponent();
        // Module pollers stand down while the shell is hidden to tray or minimised.
        AppVisibility.Current.Attach(this);

        // DataContext is assigned in XAML, so it is already in place by the time the constructor
        // body runs and DataContextChanged will never fire for it. Subscribing only to that event
        // left the rail listening to nothing: it was positioned once at load and then sat on the
        // first module for the rest of the session. Hook what is already here, and keep the event
        // for the case where the context is later replaced.
        HookNavRail();
        DataContextChanged += (_, _) => HookNavRail();

        // Deliberately not LayoutUpdated: that event fires for any layout pass anywhere in the
        // window, so with the pipeline sweep running it would call back sixty times a second to
        // recompute a position that has not moved. These three cover every case that actually
        // changes where the rail belongs.
        NavItems.Loaded += (_, _) => PlaceNavRail(animate: false);
        NavItems.SizeChanged += (_, _) => PlaceNavRail(animate: false);
        NavItems.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (NavItems.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                PlaceNavRail(animate: false);
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ─── Selection rail ──────────────────────────────────────────────────
    //
    // One rail travels between rows rather than each row fading its own marker in and out. The
    // difference matters: two independent fades leave the eye to work out what moved where, while a
    // single object in motion is tracked automatically. The rail lives inside the scrolled content,
    // so scrolling needs no handling of its own.

    /// <summary>Last geometry applied, so repeat calls no-op when nothing actually moved.</summary>
    private double _railY = double.NaN, _railHeight = double.NaN;

    private MainViewModel? _hookedVm;

    private void HookNavRail()
    {
        if (Vm is null || ReferenceEquals(Vm, _hookedVm)) return;   // DataContext can be set twice
        if (_hookedVm is not null) _hookedVm.PropertyChanged -= OnVmPropertyChanged;
        _hookedVm = Vm;
        _hookedVm.PropertyChanged += OnVmPropertyChanged;
        PlaceNavRail(animate: false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Current)) return;

        // The row may not be realised yet when the module list is still being built. Retrying once
        // at Loaded priority lets layout finish first; PlaceNavRail is a no-op if it already ran.
        if (!PlaceNavRail(animate: true))
            Dispatcher.BeginInvoke(new Action(() => PlaceNavRail(animate: true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Move the rail onto the current module's row.
    /// </summary>
    /// <param name="animate">
    /// True only for a real selection change. Load, resize and container generation also call this,
    /// and those must snap: animating a layout correction makes the rail drift across the panel
    /// instead of simply being where it belongs.
    /// </param>
    /// <returns>True once the rail sits on a realised row; false while the row does not exist yet.</returns>
    private bool PlaceNavRail(bool animate)
    {
        var current = Vm?.Current;
        if (current is null) return false;

        // Rows now live inside a per-section ItemsControl, so there is no single generator that
        // knows them all; the row is found by walking the realised tree instead.
        var row = FindNavRow(NavItems, current);

        // A row in a folded section is still realised but clipped to nothing. Rather than drop the
        // mark — which leaves "where did my selection go?" unanswered — the rail moves up onto the
        // section header, so a shut section still says the current module is inside it.
        if (row is null || row.ActualHeight <= 0 || !IsRowUnfolded(row))
        {
            var owner = Vm?.Groups.FirstOrDefault(g => g.Contains(current));
            row = owner is null ? null : FindNavRow(NavItems, owner);
            if (row is null || row.ActualHeight <= 0) { HideNavRail(); return false; }
        }

        double y;
        try { y = row.TransformToAncestor(NavItems).Transform(default).Y; }
        catch (InvalidOperationException) { return false; }   // not in the same visual tree yet

        var height = row.ActualHeight;
        if (Math.Abs(y - _railY) < 0.5 && Math.Abs(height - _railHeight) < 0.5) return true;

        var firstPlacement = double.IsNaN(_railY);
        _railY = y;
        _railHeight = height;

        NavRail.Height = height;
        // Clear first: a held fade-out from HideNavRail would otherwise win over the assignment.
        NavRail.BeginAnimation(OpacityProperty, null);
        if (NavRail.Opacity < 1) NavRail.Opacity = 1;

        if (!animate || firstPlacement)
        {
            NavRailOffset.BeginAnimation(TranslateTransform.YProperty, null);
            NavRailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NavRailOffset.Y = y;
            NavRailScale.ScaleY = 1;
            return true;
        }

        var slide = new DoubleAnimation
        {
            To = y,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        // A touch of stretch along the direction of travel, scaled by distance and capped. It reads
        // as momentum on a long jump across the rail and stays invisible on a neighbouring one,
        // which is the point — a fixed stretch on every move looks like a tic.
        var distance = Math.Abs(y - NavRailOffset.Y);
        var stretch = 1 + Math.Min(0.22, distance / (height * 14));

        var squash = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(stretch, KeyTime.FromPercent(0.45),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        squash.Duration = TimeSpan.FromMilliseconds(240);

        NavRailOffset.BeginAnimation(TranslateTransform.YProperty, slide);
        NavRailScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        return true;
    }

    /// <summary>Fade the rail out and forget where it was, so it re-places cleanly on return.</summary>
    private void HideNavRail()
    {
        if (NavRail.Opacity <= 0) return;
        _railY = double.NaN;
        _railHeight = double.NaN;
        NavRail.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    /// <summary>The realised row for a module, wherever in the section tree it ended up.</summary>
    private static FrameworkElement? FindNavRow(DependencyObject root, object item)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            // Rows are Buttons and section headers are ToggleButtons — both derive from ButtonBase.
            if (child is ButtonBase b && ReferenceEquals(b.DataContext, item)) return b;
            var hit = FindNavRow(child, item);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>False once any section border between the row and the rail has folded shut.</summary>
    private static bool IsRowUnfolded(FrameworkElement row)
    {
        DependencyObject? node = row;
        while (node is not null)
        {
            if (node is Border { Name: "" } b && b.ClipToBounds && b.ActualHeight <= 1) return false;
            if (node is FrameworkElement { Name: "NavItems" }) break;
            node = VisualTreeHelper.GetParent(node);
        }
        return true;
    }

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
