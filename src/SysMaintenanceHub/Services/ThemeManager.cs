using System;
using System.IO;
using System.Windows;

namespace SysMaintenanceHub.Services;

public enum AppTheme { Dark, Light }

public static class ThemeManager
{
    private static readonly string ConfigPath =
        Path.Combine(App.DataDirectory, "theme.cfg");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event EventHandler<AppTheme>? ThemeChanged;

    public static void LoadCurrent()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath).Trim();
                Current = text.Equals("Light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
            }
        }
        catch
        {
            Current = AppTheme.Dark;
        }
        Apply(Current);
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public static void Apply(AppTheme theme)
    {
        var uri = new Uri(
            theme == AppTheme.Light ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml",
            UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;

        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("DarkTheme.xaml") || src.Contains("LightTheme.xaml"))
                merged.RemoveAt(i);
        }
        merged.Insert(0, dict);

        Current = theme;
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(ConfigPath, theme.ToString());
        }
        catch { }

        ThemeChanged?.Invoke(null, theme);
    }
}
