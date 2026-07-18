using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysMaintenanceHub.Models;
using SysMaintenanceHub.Services;

namespace SysMaintenanceHub.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly WindowsUpdateService _wu;
    private readonly WingetService _winget;
    private readonly CleanupService _cleanup;
    private readonly DefragService _defrag;
    private readonly StartupAppsService _startup;
    private readonly ILogger _log = Log.ForContext<MainViewModel>();

    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        var ps = new PowerShellRunner();
        _wu = new WindowsUpdateService(ps);
        _winget = new WingetService(ps);
        _cleanup = new CleanupService(ps);
        _defrag = new DefragService(ps);
        _startup = new StartupAppsService();

        AppTheme = ThemeManager.Current == Services.AppTheme.Dark ? "Dark" : "Light";
        LastCleanupText = FormatLastCleanup(_cleanup.LastCleanup());
        AppendLog("Aplicativo iniciado.");
        AppendLog($"Executando como administrador: {AdminGuard.IsAdministrator()}");
    }

    // --- Observable state ---

    [ObservableProperty] private string _appTheme = "Dark";

    [ObservableProperty] private string _statusText = "Pronto.";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private int _pendingWindowsUpdates;
    [ObservableProperty] private int _pendingSecurityUpdates;
    [ObservableProperty] private int _pendingApps;
    [ObservableProperty] private int _startupAppCount;
    [ObservableProperty] private double _totalCleanableMB;
    [ObservableProperty] private string _lastCleanupText = "—";
    [ObservableProperty] private string _driveInfoText = "—";

    public ObservableCollection<WindowsUpdateItem> WindowsUpdates { get; } = new();
    public ObservableCollection<WingetUpdateItem> WingetUpdates { get; } = new();
    public ObservableCollection<StartupApp> StartupApps { get; } = new();
    public ObservableCollection<DriveDefragInfo> Drives { get; } = new();
    public ObservableCollection<CleanableItem> Cleanables { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // --- Commands ---

    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeManager.Toggle();
        AppTheme = ThemeManager.Current == Services.AppTheme.Dark ? "Dark" : "Light";
        AppendLog($"Tema alterado para {AppTheme}.");
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsIndeterminate = false;
            Progress = 0;
            StatusText = "Coletando informações...";
            AppendLog("== Refresh iniciado ==");

            await ScanCleanablesAsync(_cts.Token);
            SetProgress(20, "Enumerando apps de inicialização...");
            await ScanStartupAsync(_cts.Token);
            SetProgress(40, "Consultando updates do Windows...");
            await ScanWindowsUpdatesAsync(_cts.Token);
            SetProgress(70, "Consultando atualizações de aplicativos (winget)...");
            await ScanWingetAsync(_cts.Token);
            SetProgress(90, "Analisando fragmentação...");
            await ScanDrivesAsync(_cts.Token);

            SetProgress(100, "Refresh concluído.");
            AppendLog("== Refresh finalizado ==");
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex)
        {
            _log.Error(ex, "Falha no refresh");
            AppendLog($"ERRO: {ex.Message}");
            StatusText = "Falha no refresh — ver logs.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunFullMaintenanceAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            SetProgress(0, "Instalando atualizações de apps...");
            AppendLog("== Manutenção completa iniciada ==");

            await _winget.UpgradeAllAsync(AppendLog, _cts.Token);
            SetProgress(30, "Atualizando Windows...");
            await _wu.InstallAllAsync(AppendLog, _cts.Token);
            SetProgress(60, "Limpando arquivos temporários...");
            var freed = await _cleanup.CleanAsync(Cleanables.ToList(), AppendLog, _cts.Token);
            AppendLog($"Total liberado (varredura direta): {freed:N1} MB");
            SetProgress(80, "Executando Disk Cleanup do Windows...");
            await _cleanup.RunDiskCleanupAsync(AppendLog, _cts.Token);
            SetProgress(95, "Atualizando snapshot...");
            await RefreshAllAsync();
            SetProgress(100, "Manutenção concluída.");
            AppendLog("== Manutenção completa finalizada ==");
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex)
        {
            _log.Error(ex, "Falha na manutenção");
            AppendLog($"ERRO: {ex.Message}");
            StatusText = "Falha — ver logs.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallWindowsUpdatesAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Instalando atualizações do Windows...");
            AppendLog("Instalando updates do Windows (sem confirmação)...");
            await _wu.InstallAllAsync(AppendLog, _cts.Token);
            await ScanWindowsUpdatesAsync(_cts.Token);
            StatusText = "Updates do Windows aplicados.";
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task UpgradeAppsAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Atualizando aplicativos (winget)...");
            await _winget.UpgradeAllAsync(AppendLog, _cts.Token);
            await ScanWingetAsync(_cts.Token);
            StatusText = "Aplicativos atualizados.";
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunCleanupAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Limpando...");
            var freed = await _cleanup.CleanAsync(Cleanables.ToList(), AppendLog, _cts.Token);
            AppendLog($"Total liberado: {freed:N1} MB");
            await _cleanup.RunDiskCleanupAsync(AppendLog, _cts.Token);
            await ScanCleanablesAsync(_cts.Token);
            LastCleanupText = FormatLastCleanup(_cleanup.LastCleanup());
            StatusText = "Limpeza concluída.";
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task DefragAllAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Otimizando drives...");
            foreach (var d in Drives.ToList())
            {
                AppendLog($"Otimizando {d.Drive}...");
                await _defrag.OptimizeAsync(d.Drive, AppendLog, _cts.Token);
            }
            await ScanDrivesAsync(_cts.Token);
            StatusText = "Otimização concluída.";
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private void ToggleStartup(StartupApp? app)
    {
        if (app is null) return;
        _startup.SetEnabled(app, !app.Enabled);
        var idx = StartupApps.IndexOf(app);
        if (idx >= 0) StartupApps[idx] = app with { Enabled = !app.Enabled };
        AppendLog($"Startup: {app.Name} agora {(!app.Enabled ? "habilitado" : "desabilitado")}");
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", App.LogDirectory); }
        catch (Exception ex) { AppendLog($"Não foi possível abrir a pasta de logs: {ex.Message}"); }
    }

    // --- Scans ---

    private async Task ScanCleanablesAsync(CancellationToken ct)
    {
        StatusText = "Mapeando alvos de limpeza...";
        var items = await Task.Run(() => _cleanup.Enumerate(), ct);
        RunOnUi(() =>
        {
            Cleanables.Clear();
            foreach (var i in items) Cleanables.Add(i);
            TotalCleanableMB = Math.Round(items.Sum(i => i.SizeMB), 1);
        });
    }

    private async Task ScanStartupAsync(CancellationToken ct)
    {
        var apps = await Task.Run(() => _startup.List(), ct);
        RunOnUi(() =>
        {
            StartupApps.Clear();
            foreach (var a in apps) StartupApps.Add(a);
            StartupAppCount = apps.Count;
        });
    }

    private async Task ScanWindowsUpdatesAsync(CancellationToken ct)
    {
        var list = await _wu.ListPendingAsync(AppendLog, ct);
        RunOnUi(() =>
        {
            WindowsUpdates.Clear();
            foreach (var u in list) WindowsUpdates.Add(u);
            PendingWindowsUpdates = list.Count;
            PendingSecurityUpdates = list.Count(u =>
                u.Category.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                u.Category.Contains("Segurança", StringComparison.OrdinalIgnoreCase));
        });
    }

    private async Task ScanWingetAsync(CancellationToken ct)
    {
        var list = await _winget.ListUpgradesAsync(AppendLog, ct);
        RunOnUi(() =>
        {
            WingetUpdates.Clear();
            foreach (var u in list) WingetUpdates.Add(u);
            PendingApps = list.Count;
        });
    }

    private async Task ScanDrivesAsync(CancellationToken ct)
    {
        var list = await _defrag.AnalyzeAsync(AppendLog, ct);
        RunOnUi(() =>
        {
            Drives.Clear();
            foreach (var d in list) Drives.Add(d);
            DriveInfoText = string.Join("  ·  ",
                list.ConvertAll(d => $"{d.Drive} {d.FragmentationPercent:N0}%"));
        });
    }

    // --- Helpers ---

    private void SetProgress(double value, string status)
    {
        RunOnUi(() =>
        {
            Progress = value;
            IsIndeterminate = false;
            StatusText = status;
        });
    }

    public void AppendLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        RunOnUi(() =>
        {
            LogLines.Add(stamped);
            while (LogLines.Count > 2000) LogLines.RemoveAt(0);
        });
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true) action();
        else Application.Current?.Dispatcher.Invoke(action);
    }

    private static string FormatLastCleanup(DateTime? dt) =>
        dt is null ? "Nunca" : $"{dt.Value:dd/MM/yyyy HH:mm}";
}
