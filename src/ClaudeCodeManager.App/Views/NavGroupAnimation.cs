using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeCodeManager.App.Views;

/// <summary>
/// Folds a nav section open and shut.
///
/// WPF cannot animate Height to Auto, so the height is measured first and the animation runs
/// between zero and that number; the moment it finishes the height goes back to Auto, otherwise a
/// section would keep a stale fixed height when its contents change. The host border clips, so the
/// rows slide out of view behind the header rather than sitting on top of the next section while
/// the fold runs.
///
/// Opening and closing are deliberately not the same length. A fold that opens as slowly as it
/// closes feels sluggish, because on the way in the reader is waiting for something they asked
/// for, and on the way out they have already moved on.
/// </summary>
public static class NavGroupAnimation
{
    private static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Duration ShutDuration = TimeSpan.FromMilliseconds(160);

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.RegisterAttached(
            "IsOpen", typeof(bool), typeof(NavGroupAnimation),
            new PropertyMetadata(true, OnIsOpenChanged));

    public static void SetIsOpen(DependencyObject o, bool v) => o.SetValue(IsOpenProperty, v);
    public static bool GetIsOpen(DependencyObject o) => (bool)o.GetValue(IsOpenProperty);

    private static void OnIsOpenChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Border border) return;
        var open = (bool)e.NewValue;

        if (!border.IsLoaded)
        {
            // First pass: no animation, just the right resting state. Animating here would play a
            // fold for every section each time the window opens.
            border.Height = open ? double.NaN : 0;
            border.Opacity = open ? 1 : 0;
            border.Loaded += OnFirstLoad;
            return;
        }

        Animate(border, open);
    }

    private static void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        if (sender is not Border b) return;
        b.Loaded -= OnFirstLoad;
        b.Height = GetIsOpen(b) ? double.NaN : 0;
    }

    private static void Animate(Border border, bool open)
    {
        border.BeginAnimation(FrameworkElement.HeightProperty, null);
        border.BeginAnimation(UIElement.OpacityProperty, null);

        var target = open ? MeasureContent(border) : 0;
        if (double.IsNaN(target))
        {
            // Nothing measurable to animate towards — show it rather than animate to nowhere.
            border.Height = double.NaN;
            border.Opacity = 1;
            return;
        }

        var from = double.IsNaN(border.Height) ? border.ActualHeight : border.Height;
        if (open && from <= 0) border.Opacity = 0;

        var height = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = open ? OpenDuration : ShutDuration,
            // Decelerating in both directions. An accelerating close measured barely ten percent
            // folded at the halfway point, which reads as the click not having registered; moving
            // most of the distance up front and settling is what makes a dismissal feel answered.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        if (open)
        {
            // Hand the height back to the layout system once the fold lands, so a section whose
            // contents change later still sizes itself.
            height.Completed += (_, _) =>
            {
                border.BeginAnimation(FrameworkElement.HeightProperty, null);
                border.Height = double.NaN;
            };
        }

        var fade = new DoubleAnimation
        {
            To = open ? 1 : 0,
            // The rows should be legible for most of the opening and gone early on the way out.
            Duration = open ? TimeSpan.FromMilliseconds(170) : TimeSpan.FromMilliseconds(110),
            FillBehavior = FillBehavior.HoldEnd,
        };

        border.BeginAnimation(FrameworkElement.HeightProperty, height);
        border.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Natural height of the rows, measured against the width the border actually has.</summary>
    private static double MeasureContent(Border border)
    {
        if (border.Child is not FrameworkElement child) return 0;
        var width = border.ActualWidth > 0 ? border.ActualWidth : double.PositiveInfinity;
        child.Measure(new Size(width, double.PositiveInfinity));
        var h = child.DesiredSize.Height;
        // A zero measurement means layout has not run yet; falling back to Auto is better than
        // animating to nothing and leaving the section permanently shut.
        return h > 0 ? h : double.NaN;
    }
}
