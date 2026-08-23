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

/// <summary>
/// Converte uma pontuacao 0-100 em um arco de circulo (PathGeometry).
/// ConverterParameter: "cx,cy,raio" — ex.: "70,70,60".
/// </summary>
public sealed class ScoreToArcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double score = 0;
        try { score = System.Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { }
        score = Math.Max(0, Math.Min(100, score));

        double cx = 70, cy = 70, r = 60;
        var parts = parameter?.ToString()?.Split(',');
        if (parts is { Length: 3 })
        {
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out cx);
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cy);
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
        }

        double sweep = score / 100.0 * 360.0;
        if (sweep <= 0.01) sweep = 0.01;
        if (sweep >= 359.99) sweep = 359.99;

        const double startAngle = -90; // comeca no topo
        var start = OnCircle(cx, cy, r, startAngle);
        var end   = OnCircle(cx, cy, r, startAngle + sweep);

        var figure = new System.Windows.Media.PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new System.Windows.Media.ArcSegment(
            end,
            new System.Windows.Size(r, r),
            0,
            sweep > 180,
            System.Windows.Media.SweepDirection.Clockwise,
            true));

        var geo = new System.Windows.Media.PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    private static System.Windows.Point OnCircle(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new System.Windows.Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
