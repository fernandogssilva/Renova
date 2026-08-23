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

/// <summary>Compara o indice da secao ativa com o parametro do botao de navegacao.</summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null &&
            int.TryParse(parameter.ToString(), out var i))
            return i;
        return Binding.DoNothing;
    }
}

/// <summary>String hex (#RRGGBB) -> Brush. Usado nos badges de estado.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var hex = value?.ToString();
            if (string.IsNullOrWhiteSpace(hex)) return System.Windows.Media.Brushes.Transparent;
            return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;
        }
        catch { return System.Windows.Media.Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Zero -> Collapsed, qualquer outro numero -> Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        var invert = string.Equals(parameter?.ToString(), "inv", StringComparison.OrdinalIgnoreCase);
        var visible = n > 0;
        if (invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
