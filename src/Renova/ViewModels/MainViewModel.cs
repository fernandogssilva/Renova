using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Renova.Models;
using Renova.Services;

namespace Renova.ViewModels;

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
    private readonly MemoryService _memory;
    private readonly Winapp2Service _winapp2;
    private readonly RegistryAuditService _registry;
    private readonly ReportService _report;
    private readonly PrerequisiteService _prereq;
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
        _memory = new MemoryService();
        _winapp2 = new Winapp2Service();
        _registry = new RegistryAuditService();
        _report = new ReportService(ps);
        _prereq = new PrerequisiteService(ps);
        Winapp2DatabaseStatus = _winapp2.DatabaseExists
            ? $"Base carregada ({_winapp2.DatabaseDate:dd/MM/yyyy})"
            : "Base ainda não baixada";

        AppTheme = ThemeManager.Current == Services.AppTheme.Dark ? "Dark" : "Light";
        LastCleanupText = FormatLastCleanup(_cleanup.LastCleanup());
        RefreshMemorySnapshot();
        AppendLog("Renova iniciado.");
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

    // --- Navegação lateral (menu à direita) ---
    [ObservableProperty] private int _selectedSection;

    // --- Memória RAM ---
    [ObservableProperty] private double _memoryTotalMB;
    [ObservableProperty] private double _memoryAvailableMB;
    [ObservableProperty] private int _memoryLoadPercent;
    [ObservableProperty] private string _memoryStatusText = "—";
    [ObservableProperty] private double _lastMemoryFreedMB;

    // --- Estado de aplicação de correções (dirige a animação) ---
    [ObservableProperty] private bool _isApplyingFixes;
    [ObservableProperty] private string _currentFixTarget = "";
    [ObservableProperty] private int _fixesTotal;
    [ObservableProperty] private int _fixesDone;
    [ObservableProperty] private int _selectedVulnCount;
    [ObservableProperty] private int _vulnLikelyPatchedCount;
    [ObservableProperty] private int _sevCritical;
    [ObservableProperty] private int _sevImportant;
    [ObservableProperty] private int _sevModerate;
    [ObservableProperty] private int _sevLow;
    [ObservableProperty] private int _sevInformational;
    [ObservableProperty] private int _vulnNoPatchCount;

    /// <summary>
    /// Por padrao a lista mostra so o que esta realmente pendente. As CVEs
    /// cobertas pela cumulativa continuam disponiveis - marcar isto revela.
    /// </summary>
    [ObservableProperty] private bool _showLikelyPatchedVulns;

    public ObservableCollection<WindowsUpdateItem> WindowsUpdates { get; } = new();
    public ObservableCollection<WingetUpdateItem> WingetUpdates { get; } = new();
    public ObservableCollection<StartupApp> StartupApps { get; } = new();
    public ObservableCollection<DriveDefragInfo> Drives { get; } = new();
    public ObservableCollection<CleanableItem> Cleanables { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<KbPendingItem> CatalogPending { get; } = new();
    public ObservableCollection<VulnRow> Vulnerabilities { get; } = new();

    // Relatórios de varredura ao vivo (estilo CCleaner)
    public ObservableCollection<ScanModuleReport> ScanReports { get; } = new();
    private readonly Dictionary<string, ScanModuleReport> _reports = new();

    // ================= PAINEL PRINCIPAL =================

    public ObservableCollection<DashboardItem> DashboardItems { get; } = new();

    /// <summary>Top processos por consumo de memoria (agrupados por nome).</summary>
    public ObservableCollection<ProcessMemoryInfo> TopProcesses { get; } = new();
    [ObservableProperty] private double _topProcessesTotalMB;

    // ================= WINAPP2 (regras da comunidade) =================
    public ObservableCollection<Winapp2Result> Winapp2Results { get; } = new();
    [ObservableProperty] private string _winapp2DatabaseStatus = "Base ainda não baixada";
    [ObservableProperty] private double _winapp2TotalMB;
    [ObservableProperty] private int _winapp2SelectedCount;
    [ObservableProperty] private string _winapp2Status = "Nenhuma varredura executada";

    // ================= REGISTRO =================
    public ObservableCollection<RegistryFinding> RegistryFindings { get; } = new();
    [ObservableProperty] private int _registrySelectedCount;
    [ObservableProperty] private string _registryStatus = "Nenhuma análise executada";
    [ObservableProperty] private string _lastRegistryBackup = "";

    /// <summary>Versao exibida na tela, lida do assembly (fonte unica: Renova.csproj).</summary>
    public string VersaoApp { get; } = typeof(App).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
    public string RodapeVersao => $"Renova {VersaoApp} · CyberThrust";

    // ================= REPORT DE FALHA =================
    public ObservableCollection<ReportAttachment> ReportAttachments { get; } = new();

    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private string _reportDescription = "";
    [ObservableProperty] private string _reportSteps = "";
    [ObservableProperty] private string _reportModule = "Nao sei dizer";
    [ObservableProperty] private string _reportSeverity = "Media";
    [ObservableProperty] private bool _reportReproducible;
    [ObservableProperty] private string _reportStatus = "";
    [ObservableProperty] private string _lastReportPath = "";
    [ObservableProperty] private bool _reportOpenIssue = true;

    public List<string> ReportModules { get; } = new()
    {
        "Nao sei dizer", "Painel", "Vulnerabilidades", "Windows Update",
        "Catalogo Microsoft", "Limpeza de disco", "Regras da comunidade",
        "Registro do Windows", "Memoria RAM", "Discos", "Inicializacao",
        "Aplicativos", "Pre-requisitos", "Instalacao / atualizacao"
    };

    public List<string> ReportSeverities { get; } = new()
    { "Baixa", "Media", "Alta", "Critica - trava o app" };

    /// <summary>
    /// O envio so libera com imagem anexada. Report de falha sem evidencia
    /// visual gera ida e volta e raramente vira correcao.
    /// </summary>
    public bool CanSubmitReport =>
        ReportAttachments.Count > 0 &&
        !string.IsNullOrWhiteSpace(ReportDescription) &&
        ReportDescription.Trim().Length >= 20;

    public string ReportBlockReason
    {
        get
        {
            if (ReportAttachments.Count == 0)
                return "Anexe ao menos uma imagem mostrando o problema.";
            if (string.IsNullOrWhiteSpace(ReportDescription) || ReportDescription.Trim().Length < 20)
                return "Descreva o problema com pelo menos 20 caracteres.";
            return "";
        }
    }

    private void RefreshReportValidation()
    {
        OnPropertyChanged(nameof(CanSubmitReport));
        OnPropertyChanged(nameof(ReportBlockReason));
        SubmitReportCommand.NotifyCanExecuteChanged();
    }

    partial void OnReportDescriptionChanged(string value) => RefreshReportValidation();

    partial void OnShowLikelyPatchedVulnsChanged(bool value) => ApplyVulnFilter();

    /// <summary>Reaplica o filtro sobre a lista completa ja carregada.</summary>
    private void ApplyVulnFilter()
    {
        var fonte = ShowLikelyPatchedVulns
            ? _allVulnerabilities
            : _allVulnerabilities.Where(v => !v.LikelyPatched).ToList();

        RunOnUi(() =>
        {
            foreach (var old in Vulnerabilities) old.PropertyChanged -= OnVulnRowChanged;
            Vulnerabilities.Clear();
            foreach (var v in fonte)
            {
                var row = new VulnRow(v);
                row.PropertyChanged += OnVulnRowChanged;
                Vulnerabilities.Add(row);
            }
            SelectedVulnCount = 0;
        });
    }

    private List<VulnerabilityItem> _allVulnerabilities = new();

    // ================= PRE-REQUISITOS =================
    public ObservableCollection<Prerequisite> Prerequisites { get; } = new();
    [ObservableProperty] private string _prereqSummary = "Ainda não verificado";
    [ObservableProperty] private int _prereqMissingCount;
    [ObservableProperty] private int _prereqWarningCount;
    [ObservableProperty] private bool _prereqHasInstallable;

    [ObservableProperty] private int _healthScore = 100;
    [ObservableProperty] private string _healthLabel = "Aguardando varredura";
    [ObservableProperty] private string _healthColor = "#5F8271";
    [ObservableProperty] private string _healthSummary = "Execute uma varredura para avaliar o sistema.";
    [ObservableProperty] private int _areasNeedingAction;

    /// <summary>
    /// Monta os cards do painel a partir do resultado das varreduras e
    /// calcula uma pontuação de saúde 0-100. Cada card explica o que a área
    /// significa e o que muda na máquina ao resolver.
    /// </summary>
    private void BuildDashboard()
    {
        double score = 100;
        var items = new List<DashboardItem>();

        // ---------- 1. Vulnerabilidades ----------
        var otherVulns = Math.Max(0, TotalVulnCount - CriticalVulnCount);
        score -= Math.Min(35, CriticalVulnCount * 8);
        score -= Math.Min(10, otherVulns * 1.0);
        items.Add(new DashboardItem
        {
            Area = "Vulnerabilidades",
            Icon = "⚠",
            TargetSection = 1,
            ActionLabel = "Corrigir agora",
            Value = TotalVulnCount.ToString(),
            ValueCaption = CriticalVulnCount > 0 ? $"{CriticalVulnCount} crítica(s)" : "nenhuma crítica",
            WhatItIs = "Falhas conhecidas (CVEs publicadas pela Microsoft) e configurações inseguras do próprio Windows, como SMBv1 ligado, RDP sem NLA ou proteção do LSA desativada.",
            WhatItImproves = "Fecha as portas de entrada mais usadas por ransomware e por roubo de credenciais. É o item de maior impacto real em segurança.",
            Level = CriticalVulnCount > 0 ? HealthLevel.Critical
                  : TotalVulnCount > 0 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = TotalVulnCount > 0
        });

        // ---------- 2. Windows Update ----------
        score -= Math.Min(20, PendingSecurityUpdates * 5);
        score -= Math.Min(8, Math.Max(0, PendingWindowsUpdates - PendingSecurityUpdates) * 2);
        items.Add(new DashboardItem
        {
            Area = "Windows Update",
            Icon = "⭳",
            TargetSection = 2,
            ActionLabel = "Instalar todas",
            Value = PendingWindowsUpdates.ToString(),
            ValueCaption = PendingSecurityUpdates > 0 ? $"{PendingSecurityUpdates} de segurança" : "nenhuma de segurança",
            WhatItIs = "Pacotes de correção que a Microsoft publica toda segunda terça-feira do mês, além de atualizações de definição do Defender.",
            WhatItImproves = "Aplica correções de falhas que já estão sendo exploradas na internet e resolve bugs de estabilidade e compatibilidade de driver.",
            Level = PendingSecurityUpdates > 0 ? HealthLevel.Critical
                  : PendingWindowsUpdates > 0 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = PendingWindowsUpdates > 0
        });

        // ---------- 3. Aplicativos ----------
        score -= Math.Min(8, PendingApps * 0.5);
        items.Add(new DashboardItem
        {
            Area = "Aplicativos",
            Icon = "▣",
            TargetSection = 8,
            ActionLabel = "Atualizar tudo",
            Value = PendingApps.ToString(),
            ValueCaption = "com versão nova",
            WhatItIs = "Programas instalados na máquina que têm versão mais recente disponível no repositório do winget.",
            WhatItImproves = "Navegador, leitor de PDF e runtimes desatualizados são o vetor de invasão mais comum em máquinas domésticas. Atualizar fecha esse caminho.",
            Level = PendingApps > 15 ? HealthLevel.Attention : PendingApps > 0 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = PendingApps > 0
        });

        // ---------- 4. Limpeza de disco ----------
        double gb = TotalCleanableMB / 1024.0;
        score -= gb > 10 ? 10 : gb > 5 ? 6 : gb > 2 ? 3 : 0;
        items.Add(new DashboardItem
        {
            Area = "Limpeza de disco",
            Icon = "✧",
            TargetSection = 4,
            ActionLabel = "Limpar agora",
            Value = gb >= 1 ? $"{gb:N1} GB" : $"{TotalCleanableMB:N0} MB",
            ValueCaption = "recuperáveis",
            WhatItIs = "Arquivos temporários, caches de navegador e de ferramentas de desenvolvimento, resíduos do Windows Update e a lixeira.",
            WhatItImproves = "O Windows precisa de espaço livre para arquivo de paginação, atualizações e cache. Com disco cheio o sistema fica visivelmente mais lento.",
            Level = gb > 10 ? HealthLevel.Attention : gb > 2 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = gb > 0.5
        });

        // ---------- 5. Memória RAM ----------
        score -= MemoryLoadPercent > 90 ? 10 : MemoryLoadPercent > 80 ? 6 : 0;
        items.Add(new DashboardItem
        {
            Area = "Memória RAM",
            Icon = "▤",
            TargetSection = 5,
            ActionLabel = "Liberar memória",
            Value = $"{MemoryLoadPercent}%",
            ValueCaption = MemoryStatusText,
            WhatItIs = "Working set retido por processos que já não precisam dele, mais a standby list — cache de arquivos que o Windows guarda mas não é memória livre.",
            WhatItImproves = "Devolve RAM utilizável para abrir aplicativos pesados sem que o Windows precise usar disco como memória, o que é ordens de grandeza mais lento.",
            Level = MemoryLoadPercent > 90 ? HealthLevel.Critical
                  : MemoryLoadPercent > 80 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = MemoryLoadPercent > 70
        });

        // ---------- 6. Inicialização ----------
        var enabledStartup = StartupApps.Count(a => a.Enabled);
        score -= enabledStartup > 20 ? 6 : enabledStartup > 12 ? 3 : 0;
        items.Add(new DashboardItem
        {
            Area = "Inicialização",
            Icon = "⏻",
            TargetSection = 7,
            ActionLabel = "Revisar lista",
            Value = enabledStartup.ToString(),
            ValueCaption = $"ativos de {StartupAppCount}",
            WhatItIs = "Programas registrados para subir junto com o Windows, vindos das chaves Run do registro e da pasta de inicialização.",
            WhatItImproves = "Cada programa adiciona segundos ao tempo de boot e continua consumindo memória e CPU o dia inteiro, mesmo sem você usar.",
            Level = enabledStartup > 20 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = enabledStartup > 8
        });

        // ---------- 7. Discos ----------
        var fragDrives = Drives.Count(d => d.FragmentationPercent > 10);
        score -= fragDrives > 0 ? (fragDrives > 1 ? 6 : 3) : 0;
        items.Add(new DashboardItem
        {
            Area = "Discos",
            Icon = "⛁",
            TargetSection = 6,
            ActionLabel = "Otimizar",
            Value = fragDrives.ToString(),
            ValueCaption = fragDrives == 0 ? "todos otimizados" : "precisam otimizar",
            WhatItIs = "Nível de fragmentação por unidade. Em HDD os arquivos ficam espalhados; em SSD o que importa é o TRIM manter os blocos livres marcados.",
            WhatItImproves = "No HDD reduz o tempo de leitura porque a cabeça percorre menos. No SSD o TRIM preserva a velocidade de escrita ao longo do tempo.",
            Level = fragDrives > 1 ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = fragDrives > 0
        });

        // ---------- 8. Catálogo Microsoft ----------
        score -= CatalogHasPending ? 8 : 0;
        items.Add(new DashboardItem
        {
            Area = "Build do Windows",
            Icon = "◎",
            TargetSection = 3,
            ActionLabel = "Ver catálogo",
            Value = CatalogHasPending ? "Desatualizada" : "Atual",
            ValueCaption = string.IsNullOrWhiteSpace(LatestOfficialBuild) ? "—" : $"oficial: {LatestOfficialBuild}",
            WhatItIs = "Comparação entre a build instalada nesta máquina e a última publicada no release-health oficial da Microsoft.",
            WhatItImproves = "Confirma que você está de fato na versão mais recente. O Windows Update às vezes atrasa a entrega por semanas em algumas máquinas.",
            Level = CatalogHasPending ? HealthLevel.Attention : HealthLevel.Ok,
            NeedsAction = CatalogHasPending
        });

        var final = (int)Math.Round(Math.Max(0, Math.Min(100, score)));

        RunOnUi(() =>
        {
            DashboardItems.Clear();
            foreach (var i in items) DashboardItems.Add(i);

            HealthScore = final;
            AreasNeedingAction = items.Count(i => i.NeedsAction);

            (HealthLabel, HealthColor) = final switch
            {
                >= 90 => ("Excelente", "#4ADE80"),
                >= 75 => ("Bom", "#34D399"),
                >= 55 => ("Requer atenção", "#FBBF24"),
                _     => ("Crítico", "#EF4444")
            };

            HealthSummary = AreasNeedingAction == 0
                ? "Nenhuma ação pendente. O sistema está em dia."
                : CriticalVulnCount > 0
                    ? $"{CriticalVulnCount} vulnerabilidade(s) crítica(s) exigem correção imediata. {AreasNeedingAction} área(s) no total precisam de atenção."
                    : $"{AreasNeedingAction} área(s) podem ser melhoradas. Nenhuma falha crítica detectada.";
        });
    }

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
            RefreshMemorySnapshot();
            _ = CheckPrerequisitesQuietAsync(_cts.Token);
            await Task.WhenAll(tasks);
            RefreshMemorySnapshot();
            RefreshProcesses();
            BuildDashboard();

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
            await SafeStepAsync("cleanmgr /sagerun:99", 80, async () =>
            {
                await _cleanup.RunDiskCleanupAsync(AppendLog, _cts.Token);
            });

            // 5. Liberação de RAM (por último — depois que tudo já rodou e sujou memória)
            await SafeStepAsync("Liberação de memória RAM", 90, async () =>
            {
                var r = await _memory.CleanAsync(AppendLog, _cts.Token);
                RunOnUi(() =>
                {
                    LastMemoryFreedMB = r.FreedMB;
                    MemoryAvailableMB = r.After.AvailableMB;
                    MemoryLoadPercent = r.After.LoadPercent;
                    MemoryStatusText = $"{r.After.AvailableMB / 1024.0:N1} GB livres de {r.After.TotalMB / 1024.0:N1} GB";
                });
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
            BuildDashboard();
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
            BuildDashboard();
            StatusText = VulnScanStatus;
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    private void OnVulnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VulnRow.IsSelected))
            RunOnUi(() => SelectedVulnCount = Vulnerabilities.Count(v => v.IsSelected));
    }

    [RelayCommand]
    private void SelectAllVulns()
    {
        foreach (var v in Vulnerabilities) v.IsSelected = true;
        SelectedVulnCount = Vulnerabilities.Count;
    }

    [RelayCommand]
    private void SelectCriticalVulns()
    {
        foreach (var v in Vulnerabilities)
            v.IsSelected = v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase);
        SelectedVulnCount = Vulnerabilities.Count(v => v.IsSelected);
    }

    [RelayCommand]
    private void SelectAutoFixableVulns()
    {
        foreach (var v in Vulnerabilities) v.IsSelected = v.IsAutoFixable;
        SelectedVulnCount = Vulnerabilities.Count(v => v.IsSelected);
    }

    [RelayCommand]
    private void ClearVulnSelection()
    {
        foreach (var v in Vulnerabilities) v.IsSelected = false;
        SelectedVulnCount = 0;
    }

    /// <summary>Aplica UMA correção (botão da linha).</summary>
    [RelayCommand]
    private async Task ApplyPatchAsync(VulnRow? row)
    {
        if (row is null || IsBusy) return;
        ClearVulnSelection();
        row.IsSelected = true;
        SelectedVulnCount = 1;
        await ApplySelectedPatchesAsync();
    }

    /// <summary>
    /// Aplica TODAS as correções selecionadas, em sequência, com estado
    /// visual por linha (Na fila → Aplicando → Corrigido/Falhou).
    /// </summary>
    [RelayCommand]
    private async Task ApplySelectedPatchesAsync()
    {
        if (IsBusy) return;
        var queue = Vulnerabilities.Where(v => v.IsSelected).ToList();
        if (queue.Count == 0)
        {
            AppendLog("Nenhuma vulnerabilidade selecionada.");
            StatusText = "Selecione ao menos uma vulnerabilidade.";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            IsBusy = true;
            IsApplyingFixes = true;
            IsIndeterminate = false;
            FixesTotal = queue.Count;
            FixesDone = 0;

            AppendLog("========================================");
            AppendLog($"== CORRECAO EM LOTE: {queue.Count} item(ns) ==");
            AppendLog("========================================");

            RunOnUi(() => { foreach (var r in queue) r.State = FixState.Queued; });

            int ok = 0, failed = 0, manual = 0;

            foreach (var row in queue)
            {
                ct.ThrowIfCancellationRequested();

                RunOnUi(() =>
                {
                    row.State = FixState.Applying;
                    CurrentFixTarget = $"{row.Cve} · {row.Kb}";
                    Progress = FixesTotal == 0 ? 0 : (FixesDone * 100.0 / FixesTotal);
                    StatusText = $"Corrigindo {FixesDone + 1}/{FixesTotal}: {row.Cve}";
                });

                if (!row.IsAutoFixable)
                {
                    // Findings locais trazem cmdlet/registry — exigem ação manual.
                    AppendLog($"[MANUAL] {row.Cve}: aplicar manualmente -> {row.Kb}");
                    RunOnUi(() => row.State = FixState.Skipped);
                    manual++;
                }
                else
                {
                    AppendLog($">>> {row.Cve} — instalando {row.Kb} (severidade {row.Severity})");
                    var start = DateTime.Now;
                    try
                    {
                        var success = await _wu.InstallByKbAsync(row.Kb, AppendLog, ct);
                        var dur = (DateTime.Now - start).TotalSeconds;
                        if (success)
                        {
                            AppendLog($"<<< {row.Kb} aplicada em {dur:N1}s");
                            RunOnUi(() => row.State = FixState.Fixed);
                            ok++;
                        }
                        else
                        {
                            AppendLog($"!!! {row.Kb} falhou apos {dur:N1}s");
                            RunOnUi(() => row.State = FixState.Failed);
                            failed++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppendLog($"!!! {row.Kb} erro: {ex.Message}");
                        RunOnUi(() => row.State = FixState.Failed);
                        failed++;
                    }
                }

                RunOnUi(() =>
                {
                    FixesDone++;
                    Progress = FixesTotal == 0 ? 100 : (FixesDone * 100.0 / FixesTotal);
                });
            }

            AppendLog($"== FIM: {ok} corrigida(s), {failed} falha(s), {manual} manual(is) ==");
            StatusText = $"{ok} corrigida(s) · {failed} falha(s) · {manual} manual(is)";
            SetProgress(100, StatusText);

            if (ok > 0)
                AppendLog("Reinicie o Windows para concluir a aplicacao das atualizacoes.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Correção cancelada.";
            AppendLog("== CORRECAO CANCELADA ==");
            RunOnUi(() =>
            {
                foreach (var r in Vulnerabilities.Where(x => x.State is FixState.Queued or FixState.Applying))
                    r.State = FixState.Pending;
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Falha na correcao em lote");
            AppendLog($"ERRO FATAL: {ex.Message}");
        }
        finally
        {
            IsApplyingFixes = false;
            CurrentFixTarget = "";
            IsBusy = false;
        }
    }

    // ================= MEMORIA RAM =================

    private void RefreshMemorySnapshot()
    {
        var s = _memory.GetSnapshot();
        RunOnUi(() =>
        {
            MemoryTotalMB = s.TotalMB;
            MemoryAvailableMB = s.AvailableMB;
            MemoryLoadPercent = s.LoadPercent;
            MemoryStatusText = s.TotalMB <= 0
                ? "—"
                : $"{s.AvailableMB / 1024.0:N1} GB livres de {s.TotalMB / 1024.0:N1} GB";
        });
    }

    [RelayCommand]
    private async Task CleanMemoryAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        const string mod = "Memória RAM";
        try
        {
            IsBusy = true; IsIndeterminate = true;
            ReportStart(mod, "Compactando working sets e descartando standby list…");
            SetProgress(0, "Liberando memória RAM...");
            AppendLog("== Liberacao de RAM iniciada ==");

            var result = await _memory.CleanAsync(AppendLog, _cts.Token);
            RefreshProcesses();

            RunOnUi(() =>
            {
                LastMemoryFreedMB = result.FreedMB;
                MemoryTotalMB = result.After.TotalMB;
                MemoryAvailableMB = result.After.AvailableMB;
                MemoryLoadPercent = result.After.LoadPercent;
                MemoryStatusText = $"{result.After.AvailableMB / 1024.0:N1} GB livres de {result.After.TotalMB / 1024.0:N1} GB";
                StatusText = $"RAM: {result.FreedMB:N0} MB liberados";
            });
            BuildDashboard();

            ReportDone(mod, (int)result.FreedMB,
                result.FreedMB > 0
                    ? $"{result.FreedMB:N0} MB liberados · {result.ProcessesTrimmed} processos compactados"
                    : "Memória já estava otimizada — nada a liberar");
            SetProgress(100, StatusText);
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex)
        {
            ReportFailed(mod, ex.Message);
            AppendLog($"ERRO: {ex.Message}");
        }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private void RefreshMemory() => RefreshMemorySnapshot();

    // ================= PROCESSOS NA MEMORIA =================

    [RelayCommand]
    private void RefreshProcesses()
    {
        var list = _memory.GetTopProcesses(25);
        RunOnUi(() =>
        {
            TopProcesses.Clear();
            foreach (var p in list) TopProcesses.Add(p);
            TopProcessesTotalMB = Math.Round(list.Sum(p => p.MemoryMB), 1);
        });
    }

    /// <summary>Encerra todas as instancias do aplicativo escolhido.</summary>
    [RelayCommand]
    private async Task CloseProcessAsync(ProcessMemoryInfo? proc)
    {
        if (proc is null || IsBusy) return;

        if (proc.IsProtected)
        {
            AppendLog($"[BLOQUEADO] {proc.Name} e componente do Windows e nao pode ser encerrado.");
            StatusText = $"{proc.Name} e protegido pelo sistema.";
            return;
        }

        var aviso = proc.HasWindow
            ? $"Encerrar {proc.Name}?\n\n{proc.Display}\n\nO aplicativo sera fechado. Trabalho nao salvo pode ser perdido."
            : $"Encerrar {proc.Name}?\n\n{proc.InstanceCount} processo(s) usando {proc.MemoryMB:N0} MB.";

        var r = MessageBox.Show(aviso, "Renova - Encerrar aplicativo",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        try
        {
            IsBusy = true;
            AppendLog($"Encerrando {proc.Name}...");
            var result = await Task.Run(() => _memory.CloseByName(proc.Name, AppendLog));
            StatusText = result.Success
                ? $"{proc.Name} encerrado - {result.FreedMB:N0} MB devolvidos"
                : $"Nao foi possivel encerrar {proc.Name}";
            await Task.Delay(600);
            RefreshProcesses();
            RefreshMemorySnapshot();
            RefreshProcesses();
            BuildDashboard();
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    /// <summary>Compacta a memoria do app sem fecha-lo.</summary>
    [RelayCommand]
    private async Task TrimProcessAsync(ProcessMemoryInfo? proc)
    {
        if (proc is null || IsBusy) return;
        try
        {
            IsBusy = true;
            var result = await Task.Run(() => _memory.TrimByName(proc.Name, AppendLog));
            StatusText = $"{proc.Name}: {result.FreedMB:N0} MB compactados";
            await Task.Delay(400);
            RefreshProcesses();
            RefreshMemorySnapshot();
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    // ================= WINAPP2 =================

    [RelayCommand]
    private async Task DownloadWinapp2Async()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Baixando base de regras da comunidade...");
            var ok = await _winapp2.DownloadDatabaseAsync(AppendLog, _cts.Token);
            Winapp2DatabaseStatus = ok
                ? $"Base carregada ({_winapp2.DatabaseDate:dd/MM/yyyy})"
                : "Falha ao baixar a base";
            if (ok) await ScanWinapp2Async();
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task ScanWinapp2Async()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        const string mod = "Regras da comunidade (winapp2)";
        try
        {
            IsBusy = true; IsIndeterminate = false;
            ReportStart(mod, "Detectando aplicativos instalados e medindo espaco...");
            var prog = new Progress<int>(v => RunOnUi(() => Progress = v));
            var results = await _winapp2.ScanAsync(AppendLog, prog, _cts.Token);

            RunOnUi(() =>
            {
                foreach (var old in Winapp2Results) old.PropertyChanged -= OnWinapp2Changed;
                Winapp2Results.Clear();
                foreach (var r in results) { r.PropertyChanged += OnWinapp2Changed; Winapp2Results.Add(r); }
                Winapp2TotalMB = Math.Round(results.Sum(r => r.SizeMB), 1);
                Winapp2SelectedCount = results.Count(r => r.IsSelected);
                Winapp2Status = results.Count == 0
                    ? "Nenhum resíduo encontrado pelas regras da comunidade."
                    : $"{results.Count} alvos - {Winapp2TotalMB:N1} MB recuperáveis";
            });
            ReportDone(mod, results.Count, $"{Winapp2TotalMB:N1} MB recuperáveis por regras da comunidade");
            SetProgress(100, Winapp2Status);
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { ReportFailed(mod, ex.Message); AppendLog($"ERRO: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void OnWinapp2Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Winapp2Result.IsSelected))
            RunOnUi(() => Winapp2SelectedCount = Winapp2Results.Count(r => r.IsSelected));
    }

    [RelayCommand]
    private void SelectAllWinapp2()
    { foreach (var r in Winapp2Results) r.IsSelected = true; Winapp2SelectedCount = Winapp2Results.Count; }

    [RelayCommand]
    private void ClearWinapp2Selection()
    { foreach (var r in Winapp2Results) r.IsSelected = false; Winapp2SelectedCount = 0; }

    [RelayCommand]
    private async Task CleanWinapp2Async()
    {
        if (IsBusy) return;
        var sel = Winapp2Results.Where(r => r.IsSelected).ToList();
        if (sel.Count == 0) { StatusText = "Selecione ao menos um alvo."; return; }

        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = false;
            SetProgress(0, $"Limpando {sel.Count} alvo(s)...");
            var prog = new Progress<int>(v => RunOnUi(() => Progress = v));
            var summary = await _winapp2.CleanAsync(sel, AppendLog, prog, _cts.Token);
            StatusText = $"{summary.FreedMB:N1} MB liberados - {summary.FilesDeleted} arquivos removidos";
            await ScanWinapp2Async();
            BuildDashboard();
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    // ================= REGISTRO =================

    [RelayCommand]
    private async Task ScanRegistryAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        const string mod = "Registro do Windows";
        try
        {
            IsBusy = true; IsIndeterminate = false;
            ReportStart(mod, "Analisando categorias seguras...");
            var prog = new Progress<int>(v => RunOnUi(() => Progress = v));
            var findings = await _registry.ScanAsync(AppendLog, prog, _cts.Token);

            RunOnUi(() =>
            {
                foreach (var old in RegistryFindings) old.PropertyChanged -= OnRegistryChanged;
                RegistryFindings.Clear();
                foreach (var f in findings) { f.PropertyChanged += OnRegistryChanged; RegistryFindings.Add(f); }
                RegistrySelectedCount = findings.Count(f => f.IsSelected);
                var sec = findings.Count(f => f.Severity == 3);
                RegistryStatus = findings.Count == 0
                    ? "Nenhuma entrada órfã encontrada."
                    : $"{findings.Count} entrada(s) órfã(s)" + (sec > 0 ? $" - {sec} relevante(s) para segurança" : "");
            });
            ReportDone(mod, findings.Count, RegistryStatus);
            SetProgress(100, RegistryStatus);
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { ReportFailed(mod, ex.Message); AppendLog($"ERRO: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void OnRegistryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegistryFinding.IsSelected))
            RunOnUi(() => RegistrySelectedCount = RegistryFindings.Count(f => f.IsSelected));
    }

    [RelayCommand]
    private void SelectAllRegistry()
    { foreach (var f in RegistryFindings) f.IsSelected = true; RegistrySelectedCount = RegistryFindings.Count; }

    [RelayCommand]
    private void SelectSecurityRegistry()
    { foreach (var f in RegistryFindings) f.IsSelected = f.Severity >= 2; RegistrySelectedCount = RegistryFindings.Count(f => f.IsSelected); }

    [RelayCommand]
    private void ClearRegistrySelection()
    { foreach (var f in RegistryFindings) f.IsSelected = false; RegistrySelectedCount = 0; }

    [RelayCommand]
    private async Task CleanRegistryAsync()
    {
        if (IsBusy) return;
        var sel = RegistryFindings.Where(f => f.IsSelected).ToList();
        if (sel.Count == 0) { StatusText = "Selecione ao menos uma entrada."; return; }

        var confirm = MessageBox.Show(
            $"Remover {sel.Count} entrada(s) do registro?\n\n" +
            "Um backup .reg sera criado ANTES da remocao. Se algo quebrar, " +
            "de duplo clique no arquivo de backup para restaurar tudo." + "\n\n" +
            "Lembrete: limpar registro nao acelera o Windows. O ganho real e de " +
            "higiene e, no caso das regras de firewall orfas, de seguranca.",
            "Renova - Limpeza de registro",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Criando backup e removendo...");
            var result = await _registry.CleanAsync(sel, AppendLog, _cts.Token);

            if (!string.IsNullOrEmpty(result.Error))
            {
                StatusText = result.Error;
            }
            else
            {
                LastRegistryBackup = result.BackupFile;
                StatusText = $"{result.Removed} entrada(s) removida(s). Backup salvo.";
                await ScanRegistryAsync();
            }
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private void OpenRegistryBackups()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_registry.BackupDirectory);
            System.Diagnostics.Process.Start("explorer.exe", _registry.BackupDirectory);
        }
        catch (Exception ex) { AppendLog($"Nao foi possivel abrir a pasta: {ex.Message}"); }
    }

    /// <summary>Verificacao silenciosa no refresh, sem ocupar a barra de progresso.</summary>
    private async Task CheckPrerequisitesQuietAsync(CancellationToken ct)
    {
        try
        {
            var list = await _prereq.CheckAsync(null, ct);
            RunOnUi(() =>
            {
                Prerequisites.Clear();
                foreach (var i in list) Prerequisites.Add(i);
                PrereqMissingCount = list.Count(i => i.Status == PrereqStatus.Missing);
                PrereqWarningCount = list.Count(i => i.Status == PrereqStatus.Warning);
                PrereqHasInstallable = list.Any(i => i.Status != PrereqStatus.Ok && i.Installable);
                PrereqSummary = PrereqMissingCount == 0 && PrereqWarningCount == 0
                    ? "Tudo pronto — todos os componentes disponíveis."
                    : $"{PrereqMissingCount} faltando · {PrereqWarningCount} com limitação";
            });
        }
        catch { }
    }

    // ================= PRE-REQUISITOS =================

    [RelayCommand]
    private async Task CheckPrerequisitesAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        const string mod = "Pré-requisitos";
        try
        {
            IsBusy = true; IsIndeterminate = true;
            ReportStart(mod, "Verificando cmdlets e módulos necessários...");
            var list = await _prereq.CheckAsync(AppendLog, _cts.Token);

            RunOnUi(() =>
            {
                Prerequisites.Clear();
                foreach (var i in list) Prerequisites.Add(i);
                PrereqMissingCount = list.Count(i => i.Status == PrereqStatus.Missing);
                PrereqWarningCount = list.Count(i => i.Status == PrereqStatus.Warning);
                PrereqHasInstallable = list.Any(i => i.Status != PrereqStatus.Ok && i.Installable);
                PrereqSummary = PrereqMissingCount == 0 && PrereqWarningCount == 0
                    ? "Tudo pronto — todos os componentes disponíveis."
                    : $"{PrereqMissingCount} faltando · {PrereqWarningCount} com limitação";
            });
            ReportDone(mod, PrereqMissingCount, PrereqSummary);
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { ReportFailed(mod, ex.Message); AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallPrerequisitesAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Instalando componentes ausentes...");
            AppendLog("== Instalacao de pre-requisitos ==");
            var ok = await _prereq.InstallMissingAsync(AppendLog, _cts.Token);
            StatusText = ok
                ? "Componentes instalados com sucesso."
                : "Instalação parcial — veja os logs para detalhes.";
            await CheckPrerequisitesAsync();
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    // ================= REPORT DE FALHA =================

    [RelayCommand]
    private void AddReportImages()
    {
        var paths = ReportService.PickImages();
        foreach (var path in paths) AttachImage(path);
    }

    [RelayCommand]
    private void PasteReportImage()
    {
        var path = _report.SaveClipboardImage();
        if (path is null)
        {
            ReportStatus = "Nao ha imagem na area de transferencia. Use Win+Shift+S para recortar a tela e tente de novo.";
            return;
        }
        AttachImage(path);
        ReportStatus = "Imagem colada da area de transferencia.";
    }

    private void AttachImage(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            if (!ReportService.IsImage(path))
            {
                ReportStatus = $"{System.IO.Path.GetFileName(path)} nao e uma imagem.";
                return;
            }
            if (ReportAttachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            var info = new System.IO.FileInfo(path);

            // Miniatura decodificada em largura reduzida e congelada, para a
            // lista nao segurar o arquivo nem carregar a imagem inteira.
            System.Windows.Media.ImageSource? thumb = null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 220;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                thumb = bmp;
            }
            catch { }

            ReportAttachments.Add(new ReportAttachment
            {
                Path = path,
                SizeKB = Math.Round(info.Length / 1024.0, 0),
                Thumbnail = thumb
            });
            ReportStatus = $"{ReportAttachments.Count} imagem(ns) anexada(s).";
            RefreshReportValidation();
        }
        catch (Exception ex)
        {
            ReportStatus = $"Falha ao anexar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveReportImage(ReportAttachment? att)
    {
        if (att is null) return;
        ReportAttachments.Remove(att);
        ReportStatus = ReportAttachments.Count == 0
            ? "Nenhuma imagem anexada."
            : $"{ReportAttachments.Count} imagem(ns) anexada(s).";
        RefreshReportValidation();
    }

    [RelayCommand(CanExecute = nameof(CanSubmitReport))]
    private async Task SubmitReportAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        try
        {
            IsBusy = true; IsIndeterminate = true;
            SetProgress(0, "Montando pacote de report...");
            AppendLog("== Gerando report de falha ==");

            var input = new ReportInput
            {
                Title = string.IsNullOrWhiteSpace(ReportTitle) ? "(sem titulo)" : ReportTitle,
                Description = ReportDescription,
                StepsToReproduce = ReportSteps,
                Module = ReportModule,
                Severity = ReportSeverity,
                Reproducible = ReportReproducible,
                Attachments = ReportAttachments.ToList()
            };

            var result = await _report.BuildAsync(input, AppendLog, _cts.Token);

            if (!result.Success)
            {
                ReportStatus = result.Error;
                StatusText = result.Error;
                return;
            }

            LastReportPath = string.IsNullOrEmpty(result.ZipPath) ? result.Folder : result.ZipPath;
            ReportStatus = "Report gerado. Nada foi enviado — o arquivo esta no seu disco e voce decide se compartilha.";
            StatusText = "Report de falha gerado.";
            AppendLog($"Report em: {LastReportPath}");

            if (ReportOpenIssue)
            {
                var issue = _report.OpenGitHubIssue(input, result, AppendLog);
                if (issue.Success)
                {
                    ReportStatus = "Issue aberto no GitHub. Arraste o .zip que abrimos no Explorer para o issue, " +
                                   "revise as imagens (o issue e publico) e clique em Submit.";
                }
                else
                {
                    ReportStatus = issue.Error;
                    _report.OpenFolder(result.Folder);
                }
            }
            else
            {
                _report.OpenFolder(result.Folder);
            }
        }
        catch (OperationCanceledException) { StatusText = "Cancelado."; }
        catch (Exception ex)
        {
            ReportStatus = $"Falha ao gerar o report: {ex.Message}";
            AppendLog($"ERRO: {ex.Message}");
        }
        finally { IsIndeterminate = false; IsBusy = false; }
    }

    [RelayCommand]
    private void ClearReport()
    {
        ReportTitle = "";
        ReportDescription = "";
        ReportSteps = "";
        ReportModule = "Nao sei dizer";
        ReportSeverity = "Media";
        ReportReproducible = false;
        ReportAttachments.Clear();
        LastReportPath = "";
        ReportStatus = "Formulario limpo.";
        RefreshReportValidation();
    }

    /// <summary>Reabre o issue no GitHub para o ultimo pacote gerado.</summary>
    [RelayCommand]
    private void IssueLastReport()
    {
        if (string.IsNullOrEmpty(LastReportPath))
        {
            ReportStatus = "Gere um pacote antes de enviar.";
            return;
        }
        try
        {
            var input = new ReportInput
            {
                Title = string.IsNullOrWhiteSpace(ReportTitle) ? "(sem titulo)" : ReportTitle,
                Description = ReportDescription,
                StepsToReproduce = ReportSteps,
                Module = ReportModule,
                Severity = ReportSeverity,
                Reproducible = ReportReproducible,
                Attachments = ReportAttachments.ToList()
            };
            var zip = LastReportPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? LastReportPath : "";
            var issue = _report.OpenGitHubIssue(input, new ReportResult(true, "", zip, ""), AppendLog);
            ReportStatus = issue.Success
                ? "Issue reaberto no GitHub. Anexe o .zip e revise as imagens antes de publicar."
                : issue.Error;
        }
        catch (Exception ex) { ReportStatus = $"Falha: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_report.ReportsDirectory);
            _report.OpenFolder(_report.ReportsDirectory);
        }
        catch (Exception ex) { AppendLog($"Nao foi possivel abrir a pasta: {ex.Message}"); }
    }

    [RelayCommand]
    // Aceita object porque os cards do painel passam TargetSection (int) e os
    // botões do XAML passam literais de string. Um RelayCommand<string> lança
    // ao receber int, derrubando a UI no meio do render.
    private void Navigate(object? index)
    {
        switch (index)
        {
            case int i:
                SelectedSection = i;
                break;
            case string s when int.TryParse(s, out var parsed):
                SelectedSection = parsed;
                break;
        }
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

        var latestPatch = await _catalog.GetLatestPatchDateAsync(ct);
        if (latestPatch is { } lp)
            AppendLog($"Patch mais recente instalado: {lp:dd/MM/yyyy} — CVEs anteriores a essa data ja estao cobertas pela cumulativa.");

        var msrcTask  = _vuln.ScanAsync(os, installed, monthsBack, latestPatch, msg =>
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
        _allVulnerabilities = combined;

        RunOnUi(() =>
        {
            foreach (var old in Vulnerabilities) old.PropertyChanged -= OnVulnRowChanged;
            Vulnerabilities.Clear();
            var visiveis = ShowLikelyPatchedVulns
                ? combined
                : combined.Where(v => !v.LikelyPatched).ToList();
            foreach (var v in visiveis)
            {
                var row = new VulnRow(v);
                row.PropertyChanged += OnVulnRowChanged;
                Vulnerabilities.Add(row);
            }
            VulnLikelyPatchedCount = combined.Count(v => v.LikelyPatched);

            // Distribuicao por severidade, contando so o que esta pendente
            var pend = combined.Where(v => !v.LikelyPatched).ToList();
            int PorSev(string s) => pend.Count(v => string.Equals(v.Severity, s, StringComparison.OrdinalIgnoreCase));
            SevCritical      = PorSev("Critical");
            SevImportant     = PorSev("Important");
            SevModerate      = PorSev("Moderate");
            SevLow           = PorSev("Low");
            SevInformational = pend.Count - SevCritical - SevImportant - SevModerate - SevLow;
            VulnNoPatchCount = pend.Count(v =>
                !System.Text.RegularExpressions.Regex.IsMatch(v.Kb ?? "", @"^KB\d{5,8}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !v.Cve.StartsWith("SEC-", StringComparison.OrdinalIgnoreCase));
            TotalVulnCount = combined.Count(v => !v.LikelyPatched);
            CriticalVulnCount = combined.Count(v =>
                !v.LikelyPatched && v.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            SelectedVulnCount = 0;
            VulnCheckedAt = DateTime.Now;
            VulnScanStatus = combined.Count == 0
                ? "Nenhuma vulnerabilidade detectada."
                : $"{CriticalVulnCount} crítica(s) · {TotalVulnCount} pendente(s)" +
                  (VulnLikelyPatchedCount > 0
                      ? $" · {VulnLikelyPatchedCount} provavelmente já corrigida(s) pela cumulativa (ocultas)"
                      : "");
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
