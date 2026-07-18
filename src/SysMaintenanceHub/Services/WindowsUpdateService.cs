using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Consulta e instala atualizações do Windows via módulo PSWindowsUpdate.
/// Instala o módulo silenciosamente se não estiver presente.
/// </summary>
public sealed class WindowsUpdateService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<WindowsUpdateService>();

    public WindowsUpdateService(PowerShellRunner ps) => _ps = ps;

    // Pinned version (auditada) — evita supply-chain attack via 'Force' pegando última versão da PSGallery.
    private const string PinnedPSWindowsUpdateVersion = "2.2.1.5";

    public async Task EnsureModuleAsync(Action<string>? onLine, CancellationToken ct)
    {
        var script = $@"
$required = '{PinnedPSWindowsUpdateVersion}'
$existing = Get-Module -ListAvailable -Name PSWindowsUpdate | Where-Object {{ $_.Version -eq [Version]$required }}
if (-not $existing) {{
    Write-Output ""Instalando PSWindowsUpdate versao pinada $required...""
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
    Install-Module -Name PSWindowsUpdate -RequiredVersion $required -Force -Scope AllUsers -AllowClobber -Confirm:$false | Out-Null
    Write-Output 'PSWindowsUpdate instalado.'
}} else {{
    Write-Output ""PSWindowsUpdate $required ja presente.""
}}
Import-Module PSWindowsUpdate -RequiredVersion $required -ErrorAction Stop
";
        var result = await _ps.RunAsync(script, onLine, ct);
        if (!result.Success)
            _log.Warning("EnsureModule terminou com código {Code}: {Err}", result.ExitCode, result.StdErr);
    }

    public async Task<List<WindowsUpdateItem>> ListPendingAsync(Action<string>? onLine, CancellationToken ct)
    {
        await EnsureModuleAsync(onLine, ct);
        const string script = @"
Import-Module PSWindowsUpdate -ErrorAction SilentlyContinue
$u = Get-WindowsUpdate -MicrosoftUpdate -ErrorAction SilentlyContinue
if (-not $u) { return }
foreach ($x in $u) {
    $kb   = ($x.KB | Select-Object -First 1)
    $cat  = ($x.Categories -join '; ')
    $size = if ($x.Size) { [math]::Round($x.Size/1MB,2) } else { 0 }
    ""KB={0}|TITLE={1}|CAT={2}|SIZE={3}"" -f $kb, ($x.Title -replace '\|','/'), $cat, $size
}
";
        var result = await _ps.RunAsync(script, onLine, ct);
        var list = new List<WindowsUpdateItem>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("KB=")) continue;
            var parts = new Dictionary<string, string>();
            foreach (var seg in line.Split('|'))
            {
                var eq = seg.IndexOf('=');
                if (eq <= 0) continue;
                parts[seg[..eq]] = seg[(eq + 1)..];
            }
            list.Add(new WindowsUpdateItem(
                parts.GetValueOrDefault("KB", ""),
                parts.GetValueOrDefault("TITLE", ""),
                parts.GetValueOrDefault("CAT", ""),
                double.TryParse(parts.GetValueOrDefault("SIZE", "0"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0));
        }
        _log.Information("{Count} atualizações do Windows pendentes", list.Count);
        return list;
    }

    /// <summary>
    /// Instala uma KB específica sem prompt.
    /// </summary>
    public async Task<bool> InstallByKbAsync(string kb, Action<string>? onLine, CancellationToken ct)
    {
        // SEGURANÇA: valida estritamente antes de interpolar em script PS elevado
        var normalized = (kb ?? "").Trim().Replace("KB", "", StringComparison.OrdinalIgnoreCase);
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d{5,8}$"))
        {
            onLine?.Invoke($"[BLOQUEADO] KB inválida: '{kb}' — apenas dígitos (5–8) são permitidos.");
            _log.Warning("Tentativa de instalar KB com formato inválido: {Kb}", kb);
            return false;
        }
        await EnsureModuleAsync(onLine, ct);
        var script = $@"
Import-Module PSWindowsUpdate -ErrorAction Stop
Write-Output 'Instalando KB{normalized}...'
Get-WindowsUpdate -MicrosoftUpdate -KBArticleID {normalized} -Install -AcceptAll -IgnoreReboot -Confirm:$false -Verbose 2>&1 |
    ForEach-Object {{ Write-Output $_ }}
";
        var result = await _ps.RunAsync(script, onLine, ct);
        return result.Success;
    }

    /// <summary>
    /// Instala TODAS as atualizações pendentes sem confirmar.
    /// </summary>
    public async Task<bool> InstallAllAsync(Action<string>? onLine, CancellationToken ct)
    {
        await EnsureModuleAsync(onLine, ct);
        const string script = @"
Import-Module PSWindowsUpdate -ErrorAction Stop
Install-WindowsUpdate -MicrosoftUpdate -AcceptAll -IgnoreReboot -Confirm:$false -Verbose 2>&1 |
    ForEach-Object { Write-Output $_ }
";
        var result = await _ps.RunAsync(script, onLine, ct);
        return result.Success;
    }
}
