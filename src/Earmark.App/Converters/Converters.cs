using Earmark.Core.WaveLink;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Earmark.App.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && bool.TryParse(s, out var paramFlag))
        {
            flag ^= paramFlag;
        }
        else if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class NullableToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SatisfiedGlyphConverter : IValueConverter
{
    // Segoe MDL2 Assets: CheckMark (E73E), Cancel (E711).
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SatisfiedBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is bool b && b ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b2)
        {
            return b2;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SatisfiedTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "Condition satisfied" : "Condition not satisfied";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class WaveLinkStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is WaveLinkConnectionState s ? s switch
        {
            WaveLinkConnectionState.Connected => "SystemFillColorSuccessBrush",
            WaveLinkConnectionState.Unavailable => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        } : "TextFillColorTertiaryBrush";

        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b)
        {
            return b;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NegatedBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;
}

public sealed class NegatedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class WaveLinkStateGlyphConverter : IValueConverter
{
    // Segoe Fluent Icons: CheckMark E73E, Warning E7BA, StatusCircleBlock F140.
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is WaveLinkConnectionState s ? s switch
        {
            WaveLinkConnectionState.Connected => "",
            WaveLinkConnectionState.Unavailable => "",
            _ => "",
        } : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PeakLevelToBrushConverter : IValueConverter
{
    // Thresholds picked to mirror conventional pro-audio meters:
    //   < 0.25 (~ -12 dBFS): below the speech / vocal sweet spot - keep it green.
    //   0.25 - 0.5 (-12 to -6 dBFS): the speech / vocal sweet spot - amber.
    //   >= 0.5 (~ -6 dBFS and up): hot, approaching 0 dBFS clip - red.
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is float f ? f : 0f;
        var key = level switch
        {
            >= 0.5f => "SystemFillColorCriticalBrush",
            >= 0.25f => "SystemFillColorCautionBrush",
            _ => "SystemFillColorSuccessBrush",
        };
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class MuteLockedBackgroundConverter : IValueConverter
{
    // When the mute is rule-locked, the icon button shouldn't read as "clickable" - drop its
    // chip background to transparent. Unlocked state uses the normal subtle fill.
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var locked = value is bool b && b;
        if (locked)
        {
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brush) && brush is Brush b2)
        {
            return b2;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class MutedBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is bool muted && muted
            ? "SystemFillColorCriticalBrush"
            : "AccentTextFillColorPrimaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class VolumeFloatToPercentConverter : IValueConverter
{
    // Slider values are double; the rule action stores Volume as float in [0,1].
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is float f ? Math.Round(f * 100.0, 0) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double d ? (float)Math.Clamp(d / 100.0, 0.0, 1.0) : 0f;
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(value is Color c ? c : Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is SolidColorBrush b ? b.Color : Microsoft.UI.Colors.Transparent;
}

public sealed class EnumToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string s && targetType.IsEnum)
        {
            return Enum.Parse(targetType, s);
        }

        return value;
    }
}
