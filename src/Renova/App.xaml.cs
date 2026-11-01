using System;
using System.IO;
using System.Windows;
using Serilog;
using Renova.Services;

namespace Renova;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renova");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        MigrarPastaAntiga();
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

        Log.Information("Renova iniciado. Versão {Version}", typeof(App).Assembly.GetName().Version);

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

    /// <summary>
    /// O produto se chamava Curupira até a v2.3. Quem atualiza não pode perder logs,
    /// backups .reg e reports: a pasta antiga é movida para a nova uma única vez.
    /// </summary>
    private static void MigrarPastaAntiga()
    {
        var antiga = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curupira");
        if (!Directory.Exists(antiga) || Directory.Exists(DataDirectory)) return;
        try { Directory.Move(antiga, DataDirectory); }
        catch (IOException) { /* pasta em uso: segue com a pasta nova vazia */ }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Renova encerrado (código {Code})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
