using System.Windows;
using System.Windows.Data;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// Attached property that pulses the target element's Opacity in lockstep with the app-wide
/// <see cref="PulseTicker"/> when the attached bool is true; otherwise resets Opacity to 1.0.
/// Use for any "active/live/green" indicator so the pulse cadence stays synchronized across
/// modules (individual DataTriggers with per-element Storyboards drift out of phase).
///
/// Usage:  &lt;Ellipse svc:PulseBehavior.IsActive="{Binding IsOnline}" Fill="{StaticResource B.Success}"/&gt;
/// </summary>
public static class PulseBehavior
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(PulseBehavior),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static void SetIsActive(DependencyObject d, bool v) => d.SetValue(IsActiveProperty, v);
    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            var binding = new Binding(nameof(PulseTicker.Opacity)) { Source = PulseTicker.Current };
            BindingOperations.SetBinding(fe, UIElement.OpacityProperty, binding);
        }
        else
        {
            BindingOperations.ClearBinding(fe, UIElement.OpacityProperty);
            fe.SetCurrentValue(UIElement.OpacityProperty, 1.0);
        }
    }
}
