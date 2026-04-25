using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

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
