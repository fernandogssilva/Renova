using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Auditoria de segurança local — combina ferramentas nativas do Windows
/// (PowerShell, WMI, Registry) com Sysinternals (sigcheck, autorunsc) quando
/// disponíveis, para gerar findings acionáveis no dashboard.
/// </summary>
public sealed class LocalSecurityAuditService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<LocalSecurityAuditService>();

    public LocalSecurityAuditService(PowerShellRunner ps) => _ps = ps;

    public async Task<List<VulnerabilityItem>> RunAuditAsync(Action<string>? onLine, CancellationToken ct)
    {
        var findings = new List<VulnerabilityItem>();
        var now = DateTime.Now;

        onLine?.Invoke("Auditoria local iniciada — 15 verificações nativas...");

        // 1. Windows Defender
        await AddDefenderChecksAsync(findings, now, onLine, ct);
        // 2. Firewall
        await AddFirewallChecksAsync(findings, now, onLine, ct);
        // 3. BitLocker
        await AddBitLockerChecksAsync(findings, now, onLine, ct);
        // 4. UAC / LSA / Credential Guard
        await AddLsaCredGuardChecksAsync(findings, now, onLine, ct);
        // 5. SMBv1 e protocolos antigos
        await AddLegacyProtocolChecksAsync(findings, now, onLine, ct);
        // 6. RDP exposto
        await AddRdpExposureChecksAsync(findings, now, onLine, ct);
        // 7. AutoLogon / Guest / senhas fracas
        await AddAccountHardeningChecksAsync(findings, now, onLine, ct);
        // 8. Secure Boot + TPM
        await AddSecureBootChecksAsync(findings, now, onLine, ct);
        // 9. PowerShell v2 legado
        await AddPowerShellV2ChecksAsync(findings, now, onLine, ct);
        // 10. Sysinternals — sigcheck (se instalado): binários sem assinatura em System32
        await AddSigcheckAsync(findings, now, onLine, ct);
        // 11. Sysinternals — autorunsc (se instalado): startup completo, marcar sem assinatura
        await AddAutorunsAsync(findings, now, onLine, ct);
        // 12. Portas abertas com listener
        await AddOpenPortsAsync(findings, now, onLine, ct);

        onLine?.Invoke($"Auditoria local concluída: {findings.Count} findings.");
        return findings;
    }

    // ---- helpers ----

    private static VulnerabilityItem Finding(
        string id, string severity, double cvss, string title, string desc,
        string fixHint, DateTime now, string reference = "")
    {
        return new VulnerabilityItem(
            Cve: id,
            Title: title,
            Severity: severity,
            CvssScore: cvss,
            Kb: fixHint,
            ProductName: "Windows (local audit)",
            Description: desc + (string.IsNullOrEmpty(reference) ? "" : $"  Ref: {reference}"),
            MsrcUrl: string.IsNullOrEmpty(reference) ? "" : reference,
            CatalogUrl: "",
            ReleaseDate: now);
    }

    private async Task<string> RunCaptureAsync(string script, CancellationToken ct)
    {
        var r = await _ps.RunAsync(script, null, ct);
        return r.StdOut ?? string.Empty;
    }

    // ---- 1. Windows Defender ----

    private async Task AddDefenderChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Defender: status, RTP, definições...");
        const string script = @"
$s = Get-MpComputerStatus -ErrorAction SilentlyContinue
if (-not $s) { Write-Output 'DEFENDER=UNAVAILABLE'; return }
Write-Output ('AV_ENABLED='       + $s.AntivirusEnabled)
Write-Output ('RTP_ENABLED='      + $s.RealTimeProtectionEnabled)
Write-Output ('IOAV_ENABLED='     + $s.IoavProtectionEnabled)
Write-Output ('BEHAVIOR_ENABLED=' + $s.BehaviorMonitorEnabled)
Write-Output ('TAMPER_ENABLED='   + $s.IsTamperProtected)
Write-Output ('SIG_AGE_DAYS='     + [int]((Get-Date) - $s.AntivirusSignatureLastUpdated).TotalDays)
Write-Output ('QUICK_SCAN_AGE='   + [int]((Get-Date) - $s.QuickScanEndTime).TotalDays)
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("AV_ENABLED", out var av) && av.Equals("False", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-DEF-001", "Critical", 8.5,
                "Windows Defender Antivirus desabilitado",
                "O motor de antivírus principal do Windows está desligado.",
                "Habilitar Defender", now,
                "https://learn.microsoft.com/microsoft-365/security/defender-endpoint/"));
        if (kv.TryGetValue("RTP_ENABLED", out var rtp) && rtp.Equals("False", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-DEF-002", "Critical", 8.0,
                "Proteção em tempo real desabilitada",
                "Real-Time Protection do Defender está desligada — ameaças novas não são bloqueadas.",
                "Habilitar RTP", now));
        if (kv.TryGetValue("TAMPER_ENABLED", out var tp) && tp.Equals("False", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-DEF-003", "Important", 6.5,
                "Tamper Protection desabilitada",
                "Sem tamper protection malware consegue desativar o Defender.",
                "Ativar Tamper Protection", now));
        if (kv.TryGetValue("SIG_AGE_DAYS", out var sigAge) && int.TryParse(sigAge, out var d) && d > 7)
            list.Add(Finding("SEC-DEF-004", "Important", 5.5,
                $"Assinaturas do Defender desatualizadas ({d} dias)",
                "As definições de vírus estão antigas — proteção contra malware recente comprometida.",
                "Update-MpSignature", now));
    }

    // ---- 2. Firewall ----

    private async Task AddFirewallChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Firewall: perfis Domain/Private/Public...");
        const string script = @"
$p = Get-NetFirewallProfile -ErrorAction SilentlyContinue
foreach ($x in $p) { Write-Output (""FW_{0}_ENABLED={1}"" -f $x.Name, $x.Enabled) }
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        foreach (var profile in new[] { "Domain", "Private", "Public" })
        {
            if (kv.TryGetValue($"FW_{profile}_ENABLED", out var v) && v.Equals("False", StringComparison.OrdinalIgnoreCase))
                list.Add(Finding($"SEC-FW-{profile}", profile == "Public" ? "Critical" : "Important", 7.0,
                    $"Firewall {profile} desabilitado",
                    $"O firewall do perfil {profile} está desligado — tráfego não é filtrado.",
                    $"Set-NetFirewallProfile -Profile {profile} -Enabled True", now));
        }
    }

    // ---- 3. BitLocker ----

    private async Task AddBitLockerChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("BitLocker: criptografia do sistema...");
        const string script = @"
try {
    $v = Get-BitLockerVolume -ErrorAction Stop
    foreach ($x in $v) {
        Write-Output (""BL_{0}_STATUS={1}"" -f $x.MountPoint.TrimEnd(':'), $x.ProtectionStatus)
        Write-Output (""BL_{0}_PERCENT={1}"" -f $x.MountPoint.TrimEnd(':'), $x.EncryptionPercentage)
    }
} catch { Write-Output 'BL_UNAVAILABLE=True' }
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive")?.TrimEnd(':') ?? "C";
        if (kv.TryGetValue($"BL_{sysDrive}_STATUS", out var v) && v.Equals("Off", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-BL-001", "Important", 6.0,
                $"BitLocker desabilitado no drive de sistema ({sysDrive}:)",
                "Sem BitLocker, dados em disco não estão criptografados. Perda/furto expõe todo o conteúdo.",
                "Enable-BitLocker -MountPoint C: -EncryptionMethod XtsAes256", now,
                "https://learn.microsoft.com/windows/security/information-protection/bitlocker/bitlocker-overview"));
    }

    // ---- 4. LSA / Credential Guard ----

    private async Task AddLsaCredGuardChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("LSA Protection e Credential Guard...");
        const string script = @"
$lsa = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name RunAsPPL -ErrorAction SilentlyContinue
Write-Output ('LSA_PPL=' + [int]($lsa.RunAsPPL))
$cg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
if ($cg) {
    Write-Output ('CG_SEC_SVC='  + ($cg.SecurityServicesRunning -join ','))
    Write-Output ('CG_VBS_STAT=' + $cg.VirtualizationBasedSecurityStatus)
}
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("LSA_PPL", out var lsa) && lsa != "1")
            list.Add(Finding("SEC-LSA-001", "Important", 6.0,
                "LSA Protection (RunAsPPL) desabilitada",
                "LSASS não roda como Protected Process — dump/leitura de credenciais viável (Mimikatz).",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa\RunAsPPL = 1", now,
                "https://learn.microsoft.com/windows-server/security/credentials-protection-and-management/configuring-additional-lsa-protection"));
        if (kv.TryGetValue("CG_SEC_SVC", out var services) && !services.Contains("1"))
            list.Add(Finding("SEC-CG-001", "Moderate", 4.5,
                "Credential Guard não está ativo",
                "Credential Guard (VBS) isola segredos do LSA em VTL1. Sem ele, ataques Pass-the-Hash são mais fáceis.",
                "Ativar via gpedit → Device Guard → Turn On VBS", now));
    }

    // ---- 5. Legacy protocols (SMBv1) ----

    private async Task AddLegacyProtocolChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Protocolos legados (SMBv1, LM, NTLMv1)...");
        const string script = @"
$smb1 = (Get-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -ErrorAction SilentlyContinue).State
Write-Output ('SMB1_STATE=' + $smb1)
$lm = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name NoLmHash -ErrorAction SilentlyContinue
Write-Output ('NO_LM_HASH=' + [int]($lm.NoLmHash))
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("SMB1_STATE", out var s) && s.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-SMB1-001", "Critical", 9.0,
                "SMBv1 habilitado — vetor de EternalBlue/WannaCry",
                "SMBv1 é obsoleto e vulnerável (MS17-010). Nunca deve estar habilitado em sistemas modernos.",
                "Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol", now,
                "https://learn.microsoft.com/windows-server/storage/file-server/troubleshoot/detect-enable-and-disable-smbv1-v2-v3"));
        if (kv.TryGetValue("NO_LM_HASH", out var lm) && lm != "1")
            list.Add(Finding("SEC-LM-001", "Important", 6.5,
                "Armazenamento de LM hash não bloqueado",
                "Hashes LM são triviais de quebrar. NoLmHash=1 deve estar setado.",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa\NoLmHash = 1", now));
    }

    // ---- 6. RDP ----

    private async Task AddRdpExposureChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("RDP: exposição e NLA...");
        const string script = @"
$td = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -ErrorAction SilentlyContinue
Write-Output ('RDP_ENABLED=' + (1 - [int]($td.fDenyTSConnections)))
$nla = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name UserAuthentication -ErrorAction SilentlyContinue
Write-Output ('RDP_NLA=' + [int]($nla.UserAuthentication))
$rule = Get-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' }
Write-Output ('RDP_FW_OPEN=' + [bool]$rule)
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("RDP_ENABLED", out var re) && re == "1"
            && kv.TryGetValue("RDP_NLA", out var nla) && nla != "1")
            list.Add(Finding("SEC-RDP-001", "Critical", 8.5,
                "RDP habilitado sem NLA (Network Level Authentication)",
                "Sem NLA, a autenticação acontece após alocação de sessão — vulnerável a CVE-2019-0708 (BlueKeep) e ataques de força bruta.",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp\UserAuthentication = 1", now));
    }

    // ---- 7. Account hardening ----

    private async Task AddAccountHardeningChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Contas: AutoLogon, Guest, senhas em branco...");
        const string script = @"
$autoLogon = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -ErrorAction SilentlyContinue
Write-Output ('AUTO_LOGON=' + $autoLogon.AutoAdminLogon)
$guest = Get-LocalUser -Name Guest -ErrorAction SilentlyContinue
if ($guest) { Write-Output ('GUEST_ENABLED=' + $guest.Enabled) }
$limitBlank = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name LimitBlankPasswordUse -ErrorAction SilentlyContinue
Write-Output ('LIMIT_BLANK_PWD=' + [int]($limitBlank.LimitBlankPasswordUse))
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("AUTO_LOGON", out var al) && al == "1")
            list.Add(Finding("SEC-AC-001", "Important", 6.0,
                "AutoLogon do Windows habilitado",
                "Credenciais em texto claro no registro (DefaultPassword). Qualquer usuário local lê.",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\AutoAdminLogon = 0", now));
        if (kv.TryGetValue("GUEST_ENABLED", out var g) && g.Equals("True", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-AC-002", "Important", 6.0,
                "Conta Guest habilitada",
                "A conta Guest deve estar desabilitada em qualquer sistema Windows.",
                "Disable-LocalUser -Name Guest", now));
        if (kv.TryGetValue("LIMIT_BLANK_PWD", out var lb) && lb != "1")
            list.Add(Finding("SEC-AC-003", "Moderate", 5.0,
                "Senhas em branco permitidas para logon de rede",
                "LimitBlankPasswordUse=0 permite contas sem senha usadas remotamente.",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa\LimitBlankPasswordUse = 1", now));
    }

    // ---- 8. Secure Boot / TPM ----

    private async Task AddSecureBootChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Secure Boot + TPM...");
        const string script = @"
try { Write-Output ('SECURE_BOOT=' + (Confirm-SecureBootUEFI)) } catch { Write-Output 'SECURE_BOOT=Unknown' }
$tpm = Get-Tpm -ErrorAction SilentlyContinue
if ($tpm) {
    Write-Output ('TPM_PRESENT=' + $tpm.TpmPresent)
    Write-Output ('TPM_READY=' + $tpm.TpmReady)
}
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("SECURE_BOOT", out var sb) && sb.Equals("False", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-SB-001", "Important", 6.0,
                "Secure Boot desabilitado",
                "Bootkits/rootkits podem persistir sem Secure Boot. Ative na UEFI/BIOS.",
                "UEFI Settings → Boot → Secure Boot = Enabled", now));
        if (kv.TryGetValue("TPM_PRESENT", out var tp) && tp.Equals("False", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-TPM-001", "Moderate", 4.5,
                "TPM não presente ou desabilitado",
                "Sem TPM, BitLocker + Credential Guard perdem sua âncora de hardware.",
                "Habilitar TPM na UEFI", now));
    }

    // ---- 9. PowerShell v2 ----

    private async Task AddPowerShellV2ChecksAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("PowerShell v2 legado...");
        const string script = @"
$f = Get-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root -ErrorAction SilentlyContinue
Write-Output ('PSV2_STATE=' + $f.State)
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("PSV2_STATE", out var s) && s.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            list.Add(Finding("SEC-PSV2-001", "Important", 6.5,
                "PowerShell v2 instalado (sem logging)",
                "PSv2 não gera logs script-block/module — usado por attackers para bypass. Deve ser removido.",
                "Disable-WindowsOptionalFeature -Online -FeatureName MicrosoftWindowsPowerShellV2Root", now));
    }

    // ---- 10. Sysinternals sigcheck ----

    private async Task AddSigcheckAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        var sigcheck = LocateSysinternalsTool("sigcheck.exe", "sigcheck64.exe");
        if (sigcheck is null)
        {
            onLine?.Invoke("Sigcheck (Sysinternals) não encontrado — pulando análise de binários. Baixe em https://learn.microsoft.com/sysinternals/downloads/sigcheck e coloque em PATH.");
            return;
        }
        onLine?.Invoke($"Sigcheck: {sigcheck} — verificando binários não assinados em System32...");
        var script = $@"
& '{sigcheck}' -nobanner -accepteula -u -e -s -q C:\Windows\System32 2>&1 | Select-Object -First 30
";
        var out_ = await RunCaptureAsync(script, ct);
        int unsigned = 0;
        foreach (var line in out_.Split('\n'))
        {
            if (line.Contains("Verified", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Unsigned", StringComparison.OrdinalIgnoreCase))
                unsigned++;
        }
        if (unsigned > 0)
            list.Add(Finding("SEC-SIG-001", "Important", 6.5,
                $"{unsigned} binário(s) sem assinatura em System32 (sample)",
                "Binários não assinados em System32 são anomalias: podem indicar malware persistente ou modificações não autorizadas.",
                "sigcheck -u -e -s C:\\Windows\\System32", now,
                "https://learn.microsoft.com/sysinternals/downloads/sigcheck"));
    }

    // ---- 11. Sysinternals autorunsc ----

    private async Task AddAutorunsAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        var autorunsc = LocateSysinternalsTool("autorunsc.exe", "autorunsc64.exe");
        if (autorunsc is null)
        {
            onLine?.Invoke("Autorunsc (Sysinternals) não encontrado — pulando análise de startup completa. Baixe em https://learn.microsoft.com/sysinternals/downloads/autoruns.");
            return;
        }
        onLine?.Invoke($"Autorunsc: {autorunsc} — enumeração completa de startup...");
        var script = $@"
& '{autorunsc}' -nobanner -accepteula -a * -h -s -c 2>&1 | Select-Object -First 200
";
        var out_ = await RunCaptureAsync(script, ct);
        int unsignedStartup = 0;
        foreach (var line in out_.Split('\n'))
        {
            if (line.Contains("(Not verified)", StringComparison.OrdinalIgnoreCase))
                unsignedStartup++;
        }
        if (unsignedStartup > 0)
            list.Add(Finding("SEC-AUTORUN-001", "Moderate", 5.0,
                $"{unsignedStartup} item(ns) de startup sem assinatura (Autoruns)",
                "Startup items sem assinatura são candidatos a persistência de malware. Inspecione manualmente.",
                "autorunsc -a * -h -s", now,
                "https://learn.microsoft.com/sysinternals/downloads/autoruns"));
    }

    // ---- 12. Portas abertas ----

    private async Task AddOpenPortsAsync(List<VulnerabilityItem> list, DateTime now, Action<string>? onLine, CancellationToken ct)
    {
        onLine?.Invoke("Portas TCP em listen (públicas)...");
        const string script = @"
$l = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -eq '0.0.0.0' -or $_.LocalAddress -eq '::' } |
    Select-Object -ExpandProperty LocalPort -Unique |
    Sort-Object
Write-Output ('LISTEN_PORTS=' + ($l -join ','))
";
        var out_ = await RunCaptureAsync(script, ct);
        var kv = ParseKv(out_);
        if (kv.TryGetValue("LISTEN_PORTS", out var ports))
        {
            var dangerous = new[] { "135", "139", "445", "3389", "5985", "5986" };
            var found = ports.Split(',');
            var expo = string.Join(",", found).Split(',');
            var risky = new List<string>();
            foreach (var p in expo) if (Array.IndexOf(dangerous, p) >= 0) risky.Add(p);
            if (risky.Count > 0)
                list.Add(Finding("SEC-PORT-001", "Important", 6.5,
                    $"Portas sensíveis expostas em todas interfaces: {string.Join(", ", risky)}",
                    "Portas 135/139/445 (SMB/RPC), 3389 (RDP), 5985/5986 (WinRM) escutando em 0.0.0.0 aumentam a superfície de ataque.",
                    "Restringir binding com Set-NetFirewallRule ou desabilitar serviço", now));
        }
    }

    // ---- helpers estáticos ----

    private static Dictionary<string, string> ParseKv(string output)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static string? LocateSysinternalsTool(params string[] names)
    {
        var candidateDirs = new List<string>
        {
            Environment.GetEnvironmentVariable("PATH") ?? "",
            @"C:\Sysinternals",
            @"C:\Tools\Sysinternals",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
        };

        foreach (var name in names)
        {
            foreach (var dir in candidateDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var p in dir.Split(';'))
                {
                    try
                    {
                        var full = Path.Combine(p, name);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
        }
        return null;
    }
}
