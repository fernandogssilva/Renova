using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Curupira.Services;

namespace Curupira;

public partial class MainWindow : Window
{
    // ---- DWM: pinta a barra de título com as cores da marca ----
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR            = 34;
    private const int DWMWA_CAPTION_COLOR           = 35;
    private const int DWMWA_TEXT_COLOR              = 36;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        ThemeManager.ThemeChanged += (_, _) => Dispatcher.Invoke(ApplyTitleBarTheme);
    }

    /// <summary>
    /// Aplica cor de legenda, texto e borda da janela conforme o tema ativo.
    /// Requer Windows 11 build 22000+; em versões anteriores as chamadas
    /// simplesmente retornam erro e a barra fica no padrão do sistema.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        bool dark = ThemeManager.Current == AppTheme.Dark;

        // Bg1 de cada tema — a barra passa a ser continuação do cabeçalho
        var caption = dark ? Color.FromRgb(0x11, 0x1F, 0x19) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var text    = dark ? Color.FromRgb(0xF0, 0xFD, 0xF4) : Color.FromRgb(0x08, 0x20, 0x16);
        var border  = dark ? Color.FromRgb(0x1B, 0x2E, 0x25) : Color.FromRgb(0xC9, 0xE3, 0xD5);

        int darkMode = dark ? 1 : 0;
        SetAttr(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode);

        int cap = ToColorRef(caption);
        int txt = ToColorRef(text);
        int brd = ToColorRef(border);
        SetAttr(hwnd, DWMWA_CAPTION_COLOR, ref cap);
        SetAttr(hwnd, DWMWA_TEXT_COLOR,    ref txt);
        SetAttr(hwnd, DWMWA_BORDER_COLOR,  ref brd);
    }

    private static void SetAttr(IntPtr hwnd, int attr, ref int value)
    {
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch { }
    }

    /// <summary>DWM espera COLORREF: 0x00BBGGRR (ordem invertida do RGB).</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBarTheme();

        if (DataContext is ViewModels.MainViewModel vm)
        {
            ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
            {
                if (FindName("LogScroll") is ScrollViewer sv)
                    sv.ScrollToBottom();
            };
            await vm.RefreshAllCommand.ExecuteAsync(null);
        }
    }
}
