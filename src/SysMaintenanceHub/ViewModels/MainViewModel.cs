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

    // Relatórios de varredura ao vivo (estilo CCleaner)
    public ObservableCollection<ScanModuleReport> ScanReports { get; } = new();
    private readonly Dictionary<string, ScanModuleReport> _reports = new();

    private ScanModuleReport GetReport(string name, string actionHint = "")
    {
        if (_reports.TryGetValue(name, out var r)) return r;
        r = new ScanModuleReport { Name = name, State = ScanState.Pending, ActionHint = actionHint };
        _reports[name] = r;
        RunOnUi(() => ScanReports.Add(r));
        return r;
    }

    private void ReportStart(string name, string activity)
    {
        var r = GetReport(name);
        RunOnUi(() => { r.State = ScanState.Running; r.CurrentActivity = activity; r.Progress = 0; r.FoundCount = 0; });
    }

    private void ReportProgress(string name, double pct, string activity, string lastItem = "")
    {
        var r = GetReport(name);
        RunOnUi(() => {
            r.Progress = pct;
            r.CurrentActivity = activity;
            if (!string.IsNullOrEmpty(lastItem)) r.LastFoundItem = lastItem;
        });
    }

    private void ReportFound(string name, int totalFound, string lastItem = "")
    {
        var r = GetReport(name);
        RunOnUi(() => { r.FoundCount = totalFound; if (!string.IsNullOrEmpty(lastItem)) r.LastFoundItem = lastItem; });
    }

    private void ReportDone(string name, int total, string actionHint)
    {
        var r = GetReport(name);
        RunOnUi(() => {
            r.State = ScanState.Done;
            r.FoundCount = total;
            r.Progress = 100;
            r.ActionHint = actionHint;
            r.CurrentActivity = $"{total} item(ns) encontrado(s)";
        });
    }

    private void ReportFailed(string name, string message)
    {
        var r = GetReport(name);
        RunOnUi(() => { r.State = ScanState.Failed; r.CurrentActivity = message; });
    }

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
            SetProgress(0, "Manutenção completa: iniciando...");
            AppendLog("========================================");
            AppendLog("== INICIO Manutencao completa ==");
            AppendLog($"== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");
            AppendLog("========================================");
            _log.Information("== INICIO Manutencao completa ==");

            // 1. Winget (etapa mais longa e propensa a fechar apps por conta própria)
            await SafeStepAsync("Winget upgrade --all", 15, async () =>
            {
                await _winget.UpgradeAllAsync(AppendLog, _cts.Token);
            });

            // 2. Windows Update
            await SafeStepAsync("Windows Update install", 45, async () =>
            {
                await _wu.InstallAllAsync(AppendLog, _cts.Token);
            });

            // 3. Limpeza direta
            await SafeStepAsync("Limpeza de TEMP e caches", 70, async () =>
            {
                var freed = await _cleanup.CleanAsync(Cleanables.ToList(), AppendLog, _cts.Token);
                AppendLog($"Total liberado: {freed:N1} MB");
            });

            // 4. Disk Cleanup (cleanmgr) — separado por ser processo externo que pode
            //    abrir dialog. Envolvo em try isolado para não abortar o resto.
            await SafeStepAsync("cleanmgr /sagerun:99", 85, async () =>
            {
                await _cleanup.RunDiskCleanupAsync(AppendLog, _cts.Token);
            });

            // 5. Refresh final para atualizar KPIs
            SetProgress(95, "Atualizando snapshot...");
            await RefreshAllAsync();

            SetProgress(100, "Manutenção completa finalizada.");
            AppendLog("== FIM Manutencao completa ==");
            _log.Information("== FIM Manutencao completa ==");
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; AppendLog("== CANCELADO ==");  }
        catch (Exception ex)
        {
            _log.Error(ex, "Falha na manutenção");
            AppendLog($"ERRO FATAL: {ex.Message}");
            StatusText = "Falha — ver logs.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Envolve uma etapa da manutenção com logging estruturado ANTES/DEPOIS
    /// para diagnóstico. Falha de uma etapa NÃO aborta as outras.
    /// </summary>
    private async Task SafeStepAsync(string stepName, double progressPct, Func<Task> action)
    {
        SetProgress(progressPct, $"Executando: {stepName}...");
        var start = DateTime.Now;
        AppendLog($">>> INICIO etapa: {stepName} ({start:HH:mm:ss})");
        _log.Information("Etapa {Step} iniciada", stepName);
        try
        {
            await action();
            var dur = (DateTime.Now - start).TotalSeconds;
            AppendLog($"<<< FIM etapa: {stepName} — {dur:N1}s");
            _log.Information("Etapa {Step} concluida em {Seconds:F1}s", stepName, dur);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var dur = (DateTime.Now - start).TotalSeconds;
            AppendLog($"!!! FALHA na etapa: {stepName} — {ex.Message}");
            _log.Error(ex, "Etapa {Step} falhou apos {Seconds:F1}s", stepName, dur);
            // NÃO relança - deixa as próximas etapas rodarem
        }
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
        const string mod = "Limpeza de disco";
        ReportStart(mod, "Mapeando pastas TEMP, cache de dev e navegador…");
        try
        {
            var items = await Task.Run(() => _cleanup.Enumerate(), ct);
            RunOnUi(() =>
            {
                Cleanables.Clear();
                foreach (var i in items)
                {
                    Cleanables.Add(i);
                    ReportFound(mod, Cleanables.Count, $"{i.Name} — {i.SizeMB:N1} MB");
                }
                TotalCleanableMB = Math.Round(items.Sum(i => i.SizeMB), 1);
            });
            ReportDone(mod, items.Count,
                $"{TotalCleanableMB:N1} MB podem ser liberados — botão 'Executar limpeza'");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
    }

    private async Task ScanStartupAsync(CancellationToken ct)
    {
        const string mod = "Apps de inicialização";
        ReportStart(mod, "Lendo Registry HKCU/HKLM Run + StartupApproved…");
        try
        {
            var apps = await Task.Run(() => _startup.List(), ct);
            RunOnUi(() =>
            {
                StartupApps.Clear();
                foreach (var a in apps)
                {
                    StartupApps.Add(a);
                    ReportFound(mod, StartupApps.Count, a.Name);
                }
                StartupAppCount = apps.Count;
            });
            var disabled = apps.Count(a => !a.Enabled);
            ReportDone(mod, apps.Count,
                $"{apps.Count} apps ({disabled} desabilitado(s)) — aba Startup para alternar");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
    }

    private async Task ScanWindowsUpdatesAsync(CancellationToken ct)
    {
        const string mod = "Windows Update";
        ReportStart(mod, "Consultando PSWindowsUpdate…");
        try
        {
            var list = await _wu.ListPendingAsync(msg =>
            {
                AppendLog(msg);
                ReportProgress(mod, 50, "Baixando lista de KBs pendentes…");
            }, ct);
            RunOnUi(() =>
            {
                WindowsUpdates.Clear();
                foreach (var u in list)
                {
                    WindowsUpdates.Add(u);
                    ReportFound(mod, WindowsUpdates.Count, $"{u.Kb} — {u.Title}");
                }
                PendingWindowsUpdates = list.Count;
                PendingSecurityUpdates = list.Count(u =>
                    u.Category.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
                    u.Category.Contains("Segurança", StringComparison.OrdinalIgnoreCase));
            });
            ReportDone(mod, list.Count,
                list.Count == 0
                    ? "Sistema em dia com o Windows Update"
                    : $"{list.Count} pendente(s), {PendingSecurityUpdates} de segurança — botão 'Instalar todos'");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
    }

    private async Task ScanWingetAsync(CancellationToken ct)
    {
        const string mod = "Aplicativos (winget)";
        ReportStart(mod, "Executando winget upgrade…");
        try
        {
            var list = await _winget.ListUpgradesAsync(AppendLog, ct);
            RunOnUi(() =>
            {
                WingetUpdates.Clear();
                foreach (var u in list)
                {
                    WingetUpdates.Add(u);
                    ReportFound(mod, WingetUpdates.Count, $"{u.Name} {u.CurrentVersion} → {u.AvailableVersion}");
                }
                PendingApps = list.Count;
            });
            ReportDone(mod, list.Count,
                list.Count == 0
                    ? "Todos os apps monitorados pelo winget estão atualizados"
                    : $"{list.Count} apps com update disponível — botão 'Atualizar tudo'");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
    }

    private async Task ScanDrivesAsync(CancellationToken ct)
    {
        const string mod = "Discos (defrag/TRIM)";
        ReportStart(mod, "Analisando fragmentação com Optimize-Volume…");
        try
        {
            var list = await _defrag.AnalyzeAsync(msg =>
            {
                AppendLog(msg);
                ReportProgress(mod, 50, msg);
            }, ct);
            RunOnUi(() =>
            {
                Drives.Clear();
                foreach (var d in list)
                {
                    Drives.Add(d);
                    ReportFound(mod, Drives.Count, $"{d.Drive} — {d.FragmentationPercent:N0}% frag");
                }
                DriveInfoText = string.Join("  ·  ",
                    list.ConvertAll(d => $"{d.Drive} {d.FragmentationPercent:N0}%"));
            });
            var needsDefrag = list.Count(d => d.FragmentationPercent > 10);
            ReportDone(mod, list.Count,
                needsDefrag == 0
                    ? "Todos os drives em boa forma (< 10% frag)"
                    : $"{needsDefrag} drive(s) precisam de desfrag — botão 'Otimizar'");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
    }

    private async Task ScanVulnerabilitiesAsync(int monthsBack, CancellationToken ct)
    {
        const string modMsrc  = "Vulnerabilidades (MSRC)";
        const string modLocal = "Auditoria de segurança local";
        ReportStart(modMsrc,  $"Baixando CVEs dos últimos {monthsBack} meses…");
        ReportStart(modLocal, "12 verificações nativas (Defender, Firewall, SMBv1, RDP…)");

        var os = _catalog.GetCurrentOsBuild();
        var installed = await _catalog.GetInstalledKbsAsync(ct);

        var msrcTask  = _vuln.ScanAsync(os, installed, monthsBack, msg =>
        {
            AppendLog(msg);
            ReportProgress(modMsrc, 50, msg);
        }, ct);
        var localTask = _localAudit.RunAuditAsync(msg =>
        {
            AppendLog(msg);
            ReportProgress(modLocal, 50, msg);
        }, ct);

        var msrcList  = await msrcTask;
        var localList = await localTask;

        var combined = new List<VulnerabilityItem>();
        combined.AddRange(localList);
        combined.AddRange(msrcList);

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

        var critMsrc  = msrcList.Count(v => v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var critLocal = localList.Count(v => v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        ReportDone(modMsrc, msrcList.Count,
            msrcList.Count == 0
                ? "Nenhuma CVE Microsoft pendente"
                : $"{msrcList.Count} CVE(s) — {critMsrc} crítica(s) — aba Vulnerabilidades");
        ReportDone(modLocal, localList.Count,
            localList.Count == 0
                ? "Configuração de segurança OK"
                : $"{localList.Count} misconfig — {critLocal} crítica(s) — clique 'Aplicar patch'");
    }

    private async Task ScanMicrosoftCatalogAsync(CancellationToken ct)
    {
        const string mod = "Catálogo Microsoft (release-health)";
        ReportStart(mod, "Comparando build local vs. última KB publicada…");
        try
        {
            var os = _catalog.GetCurrentOsBuild();
            var installedKbs = await _catalog.GetInstalledKbsAsync(ct);
            var latest = await _catalog.FetchLatestFromMicrosoftAsync(os, msg =>
            {
                AppendLog(msg);
                ReportProgress(mod, 60, msg);
            }, ct);

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
            ReportDone(mod, CatalogPending.Count,
                CatalogHasPending
                    ? $"Build oficial {LatestOfficialBuild} disponível — botão 'Instalar pendentes'"
                    : "Sistema em dia com o catálogo oficial");
        }
        catch (Exception ex) { ReportFailed(mod, ex.Message); throw; }
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
