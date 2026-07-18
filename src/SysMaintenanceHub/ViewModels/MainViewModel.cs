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
    private readonly MicrosoftCatalogService _catalog;
    private readonly VulnerabilityService _vuln;
    private readonly LocalSecurityAuditService _localAudit;
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
        _catalog = new MicrosoftCatalogService(ps);
        _vuln = new VulnerabilityService(_catalog);
        _localAudit = new LocalSecurityAuditService(ps);

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

    [ObservableProperty] private string _currentOsBuild = "—";
    [ObservableProperty] private string _latestOfficialKb = "—";
    [ObservableProperty] private string _latestOfficialBuild = "—";
    [ObservableProperty] private string _catalogStatus = "Aguardando consulta";
    [ObservableProperty] private bool _catalogHasPending;
    [ObservableProperty] private DateTime? _catalogCheckedAt;

    [ObservableProperty] private int _criticalVulnCount;
    [ObservableProperty] private int _totalVulnCount;
    [ObservableProperty] private string _vulnScanStatus = "Nenhuma varredura executada";
    [ObservableProperty] private DateTime? _vulnCheckedAt;

    public ObservableCollection<WindowsUpdateItem> WindowsUpdates { get; } = new();
    public ObservableCollection<WingetUpdateItem> WingetUpdates { get; } = new();
    public ObservableCollection<StartupApp> StartupApps { get; } = new();
    public ObservableCollection<DriveDefragInfo> Drives { get; } = new();
    public ObservableCollection<CleanableItem> Cleanables { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<KbPendingItem> CatalogPending { get; } = new();
    public ObservableCollection<VulnerabilityItem> Vulnerabilities { get; } = new();

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
            StatusText = "Coletando informações em paralelo...";
            AppendLog("== Refresh iniciado (execução paralela) ==");

            // ARQUITETURA: scans independentes rodam em paralelo. O tempo total
            // agora é o do scan mais lento (Vulnerabilidades ~15s), não a soma
            // sequencial que passava de 45s.
            var progressReporter = new Progress<int>(p => RunOnUi(() => Progress = p));
            var progressLock = new object();
            int completed = 0;
            const int totalTasks = 7;

            async Task RunStep(string label, Func<Task> action)
            {
                try
                {
                    RunOnUi(() => StatusText = label);
                    await action();
                }
                finally
                {
                    int c;
                    lock (progressLock) { c = ++completed; }
                    ((IProgress<int>)progressReporter).Report((int)(c * 100.0 / totalTasks));
                }
            }

            var tasks = new[]
            {
                RunStep("Mapeando alvos de limpeza...",           () => ScanCleanablesAsync(_cts.Token)),
                RunStep("Enumerando apps de inicialização...",     () => ScanStartupAsync(_cts.Token)),
                RunStep("Consultando updates do Windows...",       () => ScanWindowsUpdatesAsync(_cts.Token)),
                RunStep("Consultando updates de apps (winget)...", () => ScanWingetAsync(_cts.Token)),
                RunStep("Analisando fragmentação dos discos...",   () => ScanDrivesAsync(_cts.Token)),
                RunStep("Consultando catálogo Microsoft...",       () => ScanMicrosoftCatalogAsync(_cts.Token)),
                RunStep("Varrendo vulnerabilidades (MSRC)...",     () => ScanVulnerabilitiesAsync(3, _cts.Token)),
            };
            await Task.WhenAll(tasks);

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
    private async Task CheckMicrosoftCatalogAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Consultando catálogo oficial da Microsoft...");
            await ScanMicrosoftCatalogAsync(_cts.Token);
            StatusText = CatalogStatus;
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunVulnScanAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Varrendo MSRC — últimos 3 meses...");
            await ScanVulnerabilitiesAsync(3, _cts.Token);
            StatusText = VulnScanStatus;
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyPatchAsync(VulnerabilityItem? item)
    {
        if (item is null || IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, $"Aplicando patch {item.Kb} para {item.Cve}...");
            AppendLog($"== Aplicando patch {item.Kb} ({item.Cve}, severidade {item.Severity}) ==");
            var ok = await _wu.InstallByKbAsync(item.Kb, AppendLog, _cts.Token);
            AppendLog(ok
                ? $"Patch {item.Kb} solicitado. Um reboot pode ser necessário."
                : $"Falha ao instalar {item.Kb} — ver logs.");
            await ScanVulnerabilitiesAsync(3, _cts.Token);
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private void OpenCatalogUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"Não foi possível abrir {url}: {ex.Message}"); }
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

    private async Task ScanVulnerabilitiesAsync(int monthsBack, CancellationToken ct)
    {
        var os = _catalog.GetCurrentOsBuild();
        var installed = await _catalog.GetInstalledKbsAsync(ct);

        // Roda MSRC (CVEs remotas) e Local Audit em paralelo — combina no fim.
        var msrcTask  = _vuln.ScanAsync(os, installed, monthsBack, AppendLog, ct);
        var localTask = _localAudit.RunAuditAsync(AppendLog, ct);
        await Task.WhenAll(msrcTask, localTask);
        var combined = new List<VulnerabilityItem>();
        combined.AddRange(await localTask); // local audit primeiro (mais acionável)
        combined.AddRange(await msrcTask);

        RunOnUi(() =>
        {
            Vulnerabilities.Clear();
            foreach (var v in combined) Vulnerabilities.Add(v);
            TotalVulnCount = combined.Count;
            CriticalVulnCount = combined.Count(v =>
                v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            VulnCheckedAt = DateTime.Now;
            VulnScanStatus = combined.Count == 0
                ? "Nenhuma vulnerabilidade detectada."
                : $"{CriticalVulnCount} crítica(s) · {TotalVulnCount} total (MSRC + Auditoria local)";
        });
    }

    private async Task ScanMicrosoftCatalogAsync(CancellationToken ct)
    {
        var os = _catalog.GetCurrentOsBuild();
        var installedKbs = await _catalog.GetInstalledKbsAsync(ct);
        var latest = await _catalog.FetchLatestFromMicrosoftAsync(os, AppendLog, ct);

        RunOnUi(() =>
        {
            CurrentOsBuild = $"{os.ProductName} {os.DisplayVersion} · Build {os.Build}.{os.UBR}";
            CatalogCheckedAt = DateTime.Now;
            CatalogPending.Clear();

            if (latest is null)
            {
                LatestOfficialKb = "n/d";
                LatestOfficialBuild = "n/d";
                CatalogStatus = "Não foi possível ler a página oficial (ver logs).";
                CatalogHasPending = false;
                return;
            }

            LatestOfficialKb = latest.Kb;
            LatestOfficialBuild = latest.BuildString;

            bool alreadyInstalled = installedKbs.Contains(latest.Kb, StringComparer.OrdinalIgnoreCase);
            bool buildIsCurrent = os.UBR >= latest.BuildMinor && os.Build >= latest.BuildMajor;

            if (alreadyInstalled || buildIsCurrent)
            {
                CatalogHasPending = false;
                CatalogStatus = $"Sistema em dia — {latest.Kb} (build {latest.BuildString}) já aplicada.";
            }
            else
            {
                CatalogHasPending = true;
                CatalogStatus = $"PENDENTE: {latest.Kb} — sua build {os.Build}.{os.UBR} < oficial {latest.BuildString}";
                CatalogPending.Add(new KbPendingItem(
                    Kb: latest.Kb,
                    BuildString: latest.BuildString,
                    PublishedAt: latest.PublishedAt,
                    CatalogUrl: latest.CatalogUrl,
                    IsCritical: true));
            }
            AppendLog(CatalogStatus);
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
