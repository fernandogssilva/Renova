using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace SysMaintenanceHub;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Auto-scroll do painel de logs
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
