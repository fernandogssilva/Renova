using System;
using System.IO;
using System.Windows;
using Serilog;
using SysMaintenanceHub.Services;

namespace SysMaintenanceHub;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysMaintenanceHub");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext} :: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console()
            .CreateLogger();

        Log.Information("SysMaintenanceHub iniciado. Versão {Version}", typeof(App).Assembly.GetName().Version);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "Exceção não tratada no AppDomain");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Exceção no dispatcher WPF");
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        ThemeManager.LoadCurrent();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("SysMaintenanceHub encerrado (código {Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
