using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCodeManager.App.Views;

public sealed class ReferenceEqualsMultiConverter : IMultiValueConverter
{
    public static readonly ReferenceEqualsMultiConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => Array.Empty<object>();
}
