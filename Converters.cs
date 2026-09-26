using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UsageNotch.Models;
using UsageNotch.Services;

namespace UsageNotch;

/// <summary>Changes presentation only; stored usage and alert thresholds always remain usage-used.</summary>
public sealed class UsageDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var known = values.Length > 0 && values[0] is double d && double.IsFinite(d);
        var remaining = values.Length > 1 && values[1] is true;
        var fraction = known ? Math.Clamp((double)values[0], 0, 1) : 0;
        var displayed = remaining && known ? 1 - fraction : fraction;
        if (parameter as string == "fraction") return displayed;
        return known ? $"{Math.Round(displayed * 100):0}% {(remaining ? "left" : "used")}" : "—";
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase) ? value is null : value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapses an element when its bound value is null or blank.</summary>
public sealed class PresenceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            bool flag => flag,
            _ => true
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ResetTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Describe(value as DateTimeOffset?);

    public static string Describe(DateTimeOffset? value)
    {
        return ResetClock.Describe(value, DateTimeOffset.UtcNow);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Round(d * 100):0}%" : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LiveResetTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        ResetTextConverter.Describe(values.Length > 0 ? values[0] as DateTimeOffset? : null);
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FractionToRemainingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Max(0, Math.Round((1 - d) * 100)):0}% left" : "no percentage reported";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UsageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Palette.Frozen(value is double d ? Palette.Usage(d) : Palette.Unknown);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UsageTextBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var color = values.Length > 0 && values[0] is double fraction ? Palette.Usage(fraction) : Palette.Unknown;
        var surface = values.Length > 1 && values[1] is System.Windows.Media.Color background ? background : System.Windows.Media.Colors.Black;
        var raised = Palette.Lerp(surface, Palette.IsLight(surface) ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White, .06);
        return Palette.Frozen(Palette.Readable(color, raised));
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Turns an ItemsControl alternation index into a staggered animation delay.</summary>
public sealed class IndexToDelayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var index = value is int i ? i : 0;
        var step = double.TryParse(parameter as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 70;
        return Math.Min(index, 8) * step;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LimitAmountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LimitWindow window) return "";
        if (!string.IsNullOrWhiteSpace(window.Detail)) return window.Detail!;
        if (window.UsedAmount is not { } used) return "";
        var unit = string.IsNullOrWhiteSpace(window.Unit) ? "" : " " + window.Unit;
        return window.LimitAmount is { } limit
            ? $"{Format(used)} of {Format(limit)}{unit}"
            : $"{Format(used)}{unit} used";
    }

    private static string Format(double value) => value >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture)
        : value >= 10 ? value.ToString("0.#", CultureInfo.CurrentCulture)
        : value.ToString("0.##", CultureInfo.CurrentCulture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = values.Length > 0 && values[0] is double d ? Math.Clamp(d, 0, 1) : 0;
        var width = values.Length > 1 && values[1] is double w ? w : 0;
        return fraction * width;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
