using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SysMaintenanceHub.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool bv && bv;
        var invert = string.Equals(parameter?.ToString(), "inv", StringComparison.OrdinalIgnoreCase);
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is Visibility v && v == Visibility.Visible;
        var invert = string.Equals(parameter?.ToString(), "inv", StringComparison.OrdinalIgnoreCase);
        return invert ? !visible : visible;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class MegabytesFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "0 MB";
        double mb = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (mb >= 1024) return $"{mb / 1024:N2} GB";
        return $"{mb:N1} MB";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
