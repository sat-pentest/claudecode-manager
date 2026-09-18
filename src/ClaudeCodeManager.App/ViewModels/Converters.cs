using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace ClaudeCodeManager.App.ViewModels;

public sealed class UpperCaseConverter : IValueConverter
{
    public static readonly UpperCaseConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s.ToUpperInvariant() : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToOnOffConverter : IValueConverter
{
    public static readonly BoolToOnOffConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? "ON" : "OFF";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseIntPositiveToVisibility : IValueConverter
{
    public static readonly InverseIntPositiveToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int n = value switch { int i => i, long l => (int)Math.Min(l, int.MaxValue), _ => 0 };
        return n > 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StringToGeometry : IValueConverter
{
    public static readonly StringToGeometry Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return Geometry.Parse(s); } catch { return Geometry.Empty; }
        }
        return Geometry.Empty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibility : IValueConverter
{
    public static readonly BoolToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public static readonly InverseBoolToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the string is non-empty, Collapsed otherwise. Used to hide labels/descriptions that would render as blank rows.</summary>
public sealed class StringToVisibility : IValueConverter
{
    public static readonly StringToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when a collection has at least one item; Collapsed for null or empty. Used to hide empty tool-pill rows.</summary>
public sealed class ListToVisibility : IValueConverter
{
    public static readonly ListToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is System.Collections.ICollection c) return c.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StringToDocumentConverter : IValueConverter
{
    public static readonly StringToDocumentConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        return new TextDocument(s);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TextDocument doc) return doc.Text;
        return "";
    }
}

public sealed class SeverityToBrush : IValueConverter
{
    public static readonly SeverityToBrush Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Error" => "B.Danger",
            "Warning" => "B.Warn",
            "Info" => "B.TextMuted",
            _ => "B.Text"
        };
        return Application.Current.Resources[key] ?? System.Windows.Media.Brushes.White;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibility : IValueConverter
{
    public static readonly NullToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SubtractDoubleConverter : IValueConverter
{
    public static readonly SubtractDoubleConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch
        {
            double d => d,
            _ => double.NaN
        };
        if (double.IsNaN(v)) return DependencyProperty.UnsetValue;
        double sub = 0;
        if (parameter is string s && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
            sub = p;
        var result = v - sub;
        return result < 0 ? 0 : result;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// MultiBinding [0]=ConnectionState string, [1]=shared PulseOpacity double.
/// ONLINE → follows the shared pulse (so every online dot pulses in perfect sync).
/// OFFLINE/OAUTH/CHECKING → static opacity.
/// </summary>
public sealed class McpDotOpacityConverter : IMultiValueConverter
{
    public static readonly McpDotOpacityConverter Instance = new();
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length == 0) return 1.0;
        var state = values[0] as string ?? "";
        double pulse = values.Length > 1 && values[1] is double d ? d : 1.0;
        return state switch
        {
            "ONLINE" => pulse,
            "OFFLINE" => 0.85,
            "OAUTH" => 0.5,
            "DISABLED" => 0.35,
            "CHECKING" => 0.5,
            _ => 0.5
        };
    }
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

public sealed class StringNotEmptyToVisibility : IValueConverter
{
    public static readonly StringNotEmptyToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is string s && !string.IsNullOrWhiteSpace(s)) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class IntPositiveToVisibility : IValueConverter
{
    public static readonly IntPositiveToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int n = value switch
        {
            int i => i,
            long l => (int)Math.Min(l, int.MaxValue),
            _ => 0
        };
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PercentToWidthConverter : IValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value switch { double d => d, _ => 0 };
        double maxWidth = 200;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var mw)) maxWidth = mw;
        double w = maxWidth * (pct / 100.0);
        if (w < 0) w = 0;
        if (w > maxWidth) w = maxWidth;
        return w;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Width of the filled portion of a progress bar: [0]=percent 0..100, [1]=full track width.
/// The sweep is clipped to this so it dies at the progress edge instead of running on over
/// track that has not been earned yet.
/// </summary>
public sealed class FilledWidthConverter : IMultiValueConverter
{
    public static readonly FilledWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = values is { Length: > 0 } && values[0] is double p ? p : 0;
        var w = values is { Length: > 1 } && values[1] is double tw ? tw : 0;
        if (w <= 0 || pct <= 0) return 0d;
        var filled = w * Math.Clamp(pct, 0, 100) / 100.0;
        return Math.Max(0, filled);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>
/// Maps FlowTicker.ChainProgress (0..1) to the X offset of a progress-bar sweep, starting off the
/// left edge and ending as it clears the right edge of whatever it is travelling across.
/// Values are [0]=progress, [1]=travel width (the filled portion, not the whole track).
/// </summary>
public sealed class SweepPositionConverter : IMultiValueConverter
{
    public static readonly SweepPositionConverter Instance = new();
    private const double BandWidth = 70;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = values is { Length: > 0 } && values[0] is double d ? d : 0;
        var w = values is { Length: > 1 } && values[1] is double tw && tw > 0 ? tw : 0;
        if (w <= 0) return -BandWidth;
        return -BandWidth + p * (w + BandWidth);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

public sealed class SearchHitStatusConverter : IValueConverter
{
    public static readonly SearchHitStatusConverter InactiveVisibility = new() { Match = ClaudeCodeManager.Core.Services.SearchHitStatus.Inactive };
    public static readonly SearchHitStatusConverter DisabledVisibility = new() { Match = ClaudeCodeManager.Core.Services.SearchHitStatus.Disabled };

    public ClaudeCodeManager.Core.Services.SearchHitStatus Match { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ClaudeCodeManager.Core.Services.SearchHitStatus s && s == Match)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Colour for a host-security verdict. Bound to EffectiveStatus rather than Status, so an accepted
/// risk reads as settled (muted) instead of shouting red at every scan.
/// </summary>
public sealed class CheckStatusToBrush : IValueConverter
{
    public static readonly CheckStatusToBrush Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Fail" => "B.Danger",
            "Warn" => "B.Warn",
            "Pass" => "B.Success",
            "Skip" => "B.TextMuted",
            _ => "B.Text"
        };
        return Application.Current.Resources[key] ?? System.Windows.Media.Brushes.White;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
