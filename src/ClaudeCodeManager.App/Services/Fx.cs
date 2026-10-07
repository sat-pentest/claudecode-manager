using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// Lightweight motion helpers, attached in XAML. All of them run once per Loaded, respect
/// the system "no animation" preference, and never change layout — only Opacity and a
/// RenderTransform, so measured sizes stay exactly what the view declared.
///
///   svc:Fx.FadeIn="True"          element rises 8px and fades in (260ms)
///   svc:Fx.StaggerChildren="True" each child of a Panel rises in turn (45ms apart)
///   svc:Fx.GrowX="True"           element scales from 0 → full width (bars, gauges)
/// </summary>
public static class Fx
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static bool Enabled => !SystemParameters.ClientAreaAnimation ? false : true;

    // ── FadeIn ────────────────────────────────────────────────────────────
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnFadeInChanged));
    public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject d, bool v) => d.SetValue(FadeInProperty, v);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.Loaded += (_, _) => Rise(fe, TimeSpan.Zero);
    }

    // ── StaggerChildren ───────────────────────────────────────────────────
    public static readonly DependencyProperty StaggerChildrenProperty = DependencyProperty.RegisterAttached(
        "StaggerChildren", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnStaggerChanged));
    public static bool GetStaggerChildren(DependencyObject d) => (bool)d.GetValue(StaggerChildrenProperty);
    public static void SetStaggerChildren(DependencyObject d, bool v) => d.SetValue(StaggerChildrenProperty, v);

    private static void OnStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel || !(bool)e.NewValue) return;
        panel.Loaded += (_, _) =>
        {
            int i = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                    Rise(fe, TimeSpan.FromMilliseconds(Math.Min(i++, 14) * 45));
            }
        };
    }

    // ── GrowX ─────────────────────────────────────────────────────────────
    public static readonly DependencyProperty GrowXProperty = DependencyProperty.RegisterAttached(
        "GrowX", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnGrowXChanged));
    public static bool GetGrowX(DependencyObject d) => (bool)d.GetValue(GrowXProperty);
    public static void SetGrowX(DependencyObject d, bool v) => d.SetValue(GrowXProperty, v);

    private static void OnGrowXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.Loaded += (_, _) =>
        {
            if (!Enabled) return;
            var st = new ScaleTransform(0, 1);
            fe.RenderTransformOrigin = new Point(0, 0.5);
            fe.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(620)) { EasingFunction = Ease, BeginTime = TimeSpan.FromMilliseconds(80) });
        };
    }

    // ── shared ────────────────────────────────────────────────────────────
    /// <summary>Opacity 0→1 and a short upward drift. Any existing transform is preserved by
    /// wrapping it in a group, so elements that already use RenderTransform keep working.</summary>
    public static void Rise(FrameworkElement fe, TimeSpan delay, double distance = 8, int ms = 260)
    {
        if (!Enabled) return;
        var tt = new TranslateTransform(0, distance);
        fe.RenderTransform = fe.RenderTransform is null or MatrixTransform { Matrix.IsIdentity: true }
            ? tt
            : new TransformGroup { Children = { fe.RenderTransform, tt } };

        fe.Opacity = 0;
        fe.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = delay, EasingFunction = Ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(ms + 60)) { BeginTime = delay, EasingFunction = Ease });
    }
}
