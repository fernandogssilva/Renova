using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Curupira.Services;

/// <summary>
/// Verifica se a máquina tem tudo que o Curupira precisa para funcionar, e
/// instala o que for instalável.
///
/// Nem tudo é instalável: cmdlets de BitLocker não existem no Windows Home,
/// e o Defender pode ter sido removido por outro antivírus. Nesses casos o
/// serviço informa claramente que o módulo correspondente ficará indisponível,
/// em vez de deixar a função falhar em silêncio depois.
/// </summary>
public sealed class PrerequisiteService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<PrerequisiteService>();

    public PrerequisiteService(PowerShellRunner ps) => _ps = ps;

    /// <summary>Versão do PSWindowsUpdate auditada e fixada.</summary>
    public const string PinnedPSWindowsUpdate = "2.2.1.5";

    public async Task<List<Prerequisite>> CheckAsync(Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Verificando pré-requisitos da máquina...");

        const string script = @"
function Test-Cmd([string]$n) { if (Get-Command $n -ErrorAction SilentlyContinue) { 'OK' } else { 'MISSING' } }

Write-Output ""PSVERSION=$($PSVersionTable.PSVersion.ToString())""

$nuget = Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue
Write-Output ""NUGET=$(if ($nuget) { $nuget.Version.ToString() } else { 'MISSING' })""

$gallery = Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
Write-Output ""PSGALLERY=$(if ($gallery) { $gallery.InstallationPolicy } else { 'MISSING' })""

$pswu = Get-Module -ListAvailable -Name PSWindowsUpdate -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending | Select-Object -First 1
Write-Output ""PSWINDOWSUPDATE=$(if ($pswu) { $pswu.Version.ToString() } else { 'MISSING' })""

Write-Output ""WINGET=$(Test-Cmd 'winget')""
Write-Output ""DEFENDER=$(Test-Cmd 'Get-MpComputerStatus')""
Write-Output ""FIREWALL=$(Test-Cmd 'Get-NetFirewallProfile')""
Write-Output ""BITLOCKER=$(Test-Cmd 'Get-BitLockerVolume')""
Write-Output ""STORAGE=$(Test-Cmd 'Optimize-Volume')""
Write-Output ""TPM=$(Test-Cmd 'Get-Tpm')""
Write-Output ""DISM=$(Test-Cmd 'Get-WindowsOptionalFeature')""
Write-Output ""NETTCP=$(Test-Cmd 'Get-NetTCPConnection')""
Write-Output ""HOTFIX=$(Test-Cmd 'Get-HotFix')""
Write-Output ""SECUREBOOT=$(Test-Cmd 'Confirm-SecureBootUEFI')""
";

        var result = await _ps.RunAsync(script, null, ct);
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq > 0) kv[line[..eq]] = line[(eq + 1)..].Trim();
        }

        string V(string k) => kv.TryGetValue(k, out var v) ? v : "MISSING";
        bool Ok(string k) => !V(k).Equals("MISSING", StringComparison.OrdinalIgnoreCase);

        var list = new List<Prerequisite>
        {
            new()
            {
                Name = "PowerShell 5.1 ou superior",
                Detail = $"Versão detectada: {V("PSVERSION")}",
                Status = V("PSVERSION").StartsWith("MISSING") ? PrereqStatus.Missing : PrereqStatus.Ok,
                UsedBy = "Base de toda a automação do app",
                Installable = false,
                FixHint = "Vem com o Windows. Se faltou, a instalação do sistema está corrompida."
            },
            new()
            {
                Name = "Provedor NuGet",
                Detail = Ok("NUGET") ? $"Versão {V("NUGET")}" : "Não instalado",
                Status = Ok("NUGET") ? PrereqStatus.Ok : PrereqStatus.Missing,
                UsedBy = "Necessário para instalar o PSWindowsUpdate",
                Installable = true,
                FixHint = "Install-PackageProvider -Name NuGet"
            },
            new()
            {
                Name = "Repositório PSGallery",
                Detail = Ok("PSGALLERY") ? $"Política: {V("PSGALLERY")}" : "Não registrado",
                Status = !Ok("PSGALLERY") ? PrereqStatus.Missing
                       : V("PSGALLERY").Equals("Trusted", StringComparison.OrdinalIgnoreCase)
                            ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Origem oficial do módulo PSWindowsUpdate",
                Installable = true,
                FixHint = "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted"
            },
            new()
            {
                Name = $"Módulo PSWindowsUpdate {PinnedPSWindowsUpdate}",
                Detail = Ok("PSWINDOWSUPDATE") ? $"Versão instalada: {V("PSWINDOWSUPDATE")}" : "Não instalado",
                Status = !Ok("PSWINDOWSUPDATE") ? PrereqStatus.Missing
                       : V("PSWINDOWSUPDATE") == PinnedPSWindowsUpdate ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Windows Update e aplicação de patches por KB",
                Installable = true,
                FixHint = $"Install-Module PSWindowsUpdate -RequiredVersion {PinnedPSWindowsUpdate}"
            },
            new()
            {
                Name = "Winget (App Installer)",
                Detail = Ok("WINGET") ? "Disponível" : "Não encontrado no PATH",
                Status = Ok("WINGET") ? PrereqStatus.Ok : PrereqStatus.Missing,
                UsedBy = "Atualização de aplicativos instalados",
                Installable = false,
                FixHint = "Instale o App Installer pela Microsoft Store"
            },
            new()
            {
                Name = "Cmdlets do Windows Defender",
                Detail = Ok("DEFENDER") ? "Get-MpComputerStatus disponível" : "Indisponível",
                Status = Ok("DEFENDER") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Auditoria de antivírus, proteção em tempo real e assinaturas",
                Installable = false,
                FixHint = "Normalmente ausente quando outro antivírus substituiu o Defender."
            },
            new()
            {
                Name = "Cmdlets de Firewall (NetSecurity)",
                Detail = Ok("FIREWALL") ? "Get-NetFirewallProfile disponível" : "Indisponível",
                Status = Ok("FIREWALL") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Auditoria dos perfis Domínio, Privado e Público",
                Installable = false,
                FixHint = "Componente nativo do Windows."
            },
            new()
            {
                Name = "Cmdlets de BitLocker",
                Detail = Ok("BITLOCKER") ? "Get-BitLockerVolume disponível" : "Indisponível nesta edição",
                Status = Ok("BITLOCKER") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Verificação de criptografia do disco",
                Installable = false,
                FixHint = "BitLocker não existe no Windows Home. A verificação será pulada."
            },
            new()
            {
                Name = "Cmdlets de armazenamento",
                Detail = Ok("STORAGE") ? "Optimize-Volume disponível" : "Indisponível",
                Status = Ok("STORAGE") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Análise de fragmentação, desfragmentação e TRIM",
                Installable = false,
                FixHint = "Componente nativo do Windows."
            },
            new()
            {
                Name = "Cmdlets de TPM",
                Detail = Ok("TPM") ? "Get-Tpm disponível" : "Indisponível",
                Status = Ok("TPM") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Verificação de módulo de segurança do hardware",
                Installable = false,
                FixHint = "Componente nativo. Se ausente, a checagem de TPM é pulada."
            },
            new()
            {
                Name = "Cmdlets DISM",
                Detail = Ok("DISM") ? "Get-WindowsOptionalFeature disponível" : "Indisponível",
                Status = Ok("DISM") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Detecção de SMBv1 e PowerShell v2 legado",
                Installable = false,
                FixHint = "Componente nativo do Windows."
            },
            new()
            {
                Name = "Cmdlets de rede (NetTCPIP)",
                Detail = Ok("NETTCP") ? "Get-NetTCPConnection disponível" : "Indisponível",
                Status = Ok("NETTCP") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Detecção de portas sensíveis expostas",
                Installable = false,
                FixHint = "Componente nativo do Windows."
            },
            new()
            {
                Name = "Secure Boot",
                Detail = Ok("SECUREBOOT") ? "Confirm-SecureBootUEFI disponível" : "Indisponível (máquina BIOS legado)",
                Status = Ok("SECUREBOOT") ? PrereqStatus.Ok : PrereqStatus.Warning,
                UsedBy = "Verificação de inicialização segura",
                Installable = false,
                FixHint = "Só existe em máquinas UEFI."
            },
        };

        var missing = list.Count(p => p.Status == PrereqStatus.Missing);
        onLine?.Invoke($"Pré-requisitos: {list.Count(p => p.Status == PrereqStatus.Ok)} OK · " +
                       $"{list.Count(p => p.Status == PrereqStatus.Warning)} limitado(s) · {missing} faltando");
        _log.Information("Prereqs: {Missing} faltando de {Total}", missing, list.Count);
        return list;
    }

    /// <summary>Instala o que for instalável: NuGet, PSGallery confiável e PSWindowsUpdate fixado.</summary>
    public async Task<bool> InstallMissingAsync(Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Instalando componentes ausentes — isso pode levar alguns minutos...");

        var script = $@"
$ErrorActionPreference = 'Continue'
$ok = $true

# --- Provedor NuGet ---
if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {{
    Write-Output 'Instalando provedor NuGet...'
    try {{
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers -ErrorAction Stop | Out-Null
        Write-Output '  NuGet instalado.'
    }} catch {{ Write-Output ""  FALHA NuGet: $($_.Exception.Message)""; $ok = $false }}
}} else {{ Write-Output 'Provedor NuGet ja presente.' }}

# --- PSGallery confiavel ---
try {{
    $g = Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
    if (-not $g) {{
        Write-Output 'Registrando PSGallery...'
        Register-PSRepository -Default -ErrorAction Stop
    }}
    if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {{
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction Stop
        Write-Output 'PSGallery marcado como confiavel.'
    }} else {{ Write-Output 'PSGallery ja confiavel.' }}
}} catch {{ Write-Output ""  FALHA PSGallery: $($_.Exception.Message)""; $ok = $false }}

# --- PSWindowsUpdate na versao fixada ---
$req = '{PinnedPSWindowsUpdate}'
$have = Get-Module -ListAvailable -Name PSWindowsUpdate | Where-Object {{ $_.Version -eq [Version]$req }}
if (-not $have) {{
    Write-Output ""Instalando PSWindowsUpdate $req...""
    try {{
        Install-Module -Name PSWindowsUpdate -RequiredVersion $req -Force -Scope AllUsers `
            -AllowClobber -Confirm:$false -ErrorAction Stop | Out-Null
        Write-Output '  PSWindowsUpdate instalado.'
    }} catch {{ Write-Output ""  FALHA PSWindowsUpdate: $($_.Exception.Message)""; $ok = $false }}
}} else {{ Write-Output ""PSWindowsUpdate $req ja presente."" }}

# --- Winget: nao instalavel por script, so orienta ---
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {{
    Write-Output 'AVISO: winget ausente. Instale o App Installer pela Microsoft Store.'
}}

Write-Output ""RESULTADO=$(if ($ok) {{ 'OK' }} else {{ 'PARCIAL' }})""
";

        var result = await _ps.RunAsync(script, onLine, ct);
        var success = result.StdOut.Contains("RESULTADO=OK");
        _log.Information("Instalacao de prereqs: {Result}", success ? "OK" : "PARCIAL");
        return success;
    }
}

public sealed class Prerequisite : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>Qual função do Curupira depende disso.</summary>
    public string UsedBy { get; init; } = "";

    /// <summary>Comando ou orientação para resolver manualmente.</summary>
    public string FixHint { get; init; } = "";

    /// <summary>O Curupira consegue instalar sozinho?</summary>
    public bool Installable { get; init; }

    private PrereqStatus _status;
    public PrereqStatus Status
    {
        get => _status;
        set { SetProperty(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string StatusText => Status switch
    {
        PrereqStatus.Ok      => "Disponível",
        PrereqStatus.Warning => "Limitado",
        PrereqStatus.Missing => "Faltando",
        _ => ""
    };

    public string StatusColor => Status switch
    {
        PrereqStatus.Ok      => "#4ADE80",
        PrereqStatus.Warning => "#FBBF24",
        PrereqStatus.Missing => "#EF4444",
        _ => "#5F8271"
    };
}

public enum PrereqStatus { Ok, Warning, Missing }
