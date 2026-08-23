using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Curupira.Services;

/// <summary>
/// Liberacao de memoria RAM.
///
/// HONESTIDADE TECNICA: o Windows gerencia memoria bem sozinho. Esta funcao
/// NAO "cria" RAM. O que ela faz de fato:
///
///  1. EmptyWorkingSet por processo - move paginas do working set para a
///     standby list. Util depois de fechar apps pesados (IDE, VM, browser)
///     que seguram working set inflado.
///  2. Purge da standby list - descarta cache de arquivos ja gravado em disco,
///     convertendo "Em espera" em "Livre" de verdade.
///
/// Ganho real: perceptivel quando a standby list esta grande e voce vai abrir
/// um app que precisa de muita RAM (jogo, VM, render).
/// Efeito colateral: proximos acessos a arquivos ficam mais lentos ate o cache
/// reaquecer. Por isso nao deve rodar de minuto em minuto.
/// </summary>
public sealed class MemoryService
{
    private readonly ILogger _log = Log.ForContext<MemoryService>();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out long luid);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int Count;
        public long Luid;
        public int Attr;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr retLen);

    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const int SE_PRIVILEGE_ENABLED = 0x2;
    private const string SE_PROFILE_SINGLE_PROCESS = "SeProfileSingleProcessPrivilege";

    public MemorySnapshot GetSnapshot()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st))
            return new MemorySnapshot(0, 0, 0);

        double totalMB = st.ullTotalPhys / 1024.0 / 1024.0;
        double availMB = st.ullAvailPhys / 1024.0 / 1024.0;
        return new MemorySnapshot(Math.Round(totalMB, 0), Math.Round(availMB, 0), (int)st.dwMemoryLoad);
    }

    public async Task<MemoryCleanResult> CleanAsync(Action<string>? onLine, CancellationToken ct)
    {
        var before = GetSnapshot();
        onLine?.Invoke($"RAM antes: {before.AvailableMB:N0} MB livres de {before.TotalMB:N0} MB ({before.LoadPercent}% em uso)");

        int trimmed = 0, failed = 0;

        await Task.Run(() =>
        {
            onLine?.Invoke("Compactando working set dos processos...");
            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var n = p.ProcessName;
                    if (n is "System" or "Idle" or "Memory Compression" or "csrss" or "wininit" or "smss")
                        continue;

                    if (SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1)))
                        trimmed++;
                    else
                        failed++;
                }
                catch { failed++; }
                finally { try { p.Dispose(); } catch { } }
            }
            onLine?.Invoke($"  {trimmed} processos compactados ({failed} protegidos - normal)");

            onLine?.Invoke("Descartando standby list (cache de arquivos)...");
            if (TryPurgeStandbyList(out var err))
                onLine?.Invoke("  Standby list liberada.");
            else
                onLine?.Invoke($"  Standby list nao liberada ({err}). Requer administrador.");
        }, ct);

        await Task.Delay(700, ct);

        var after = GetSnapshot();
        var freed = Math.Max(0, after.AvailableMB - before.AvailableMB);
        onLine?.Invoke($"RAM depois: {after.AvailableMB:N0} MB livres ({after.LoadPercent}% em uso) - ganho de {freed:N0} MB");

        _log.Information("RAM liberada: {Freed} MB ({Before}% -> {After}%)", freed, before.LoadPercent, after.LoadPercent);
        return new MemoryCleanResult(before, after, freed, trimmed);
    }

    private bool TryPurgeStandbyList(out string error)
    {
        error = "";
        try
        {
            if (!EnablePrivilege(SE_PROFILE_SINGLE_PROCESS))
            {
                error = "sem privilegio SeProfileSingleProcess";
                return false;
            }

            IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buf, MemoryPurgeStandbyList);
                int status = NtSetSystemInformation(SystemMemoryListInformation, buf, sizeof(int));
                if (status != 0)
                {
                    error = $"NTSTATUS 0x{status:X8}";
                    return false;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Warning(ex, "Falha ao purgar standby list");
            return false;
        }
    }

    private static bool EnablePrivilege(string name)
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        const uint TOKEN_QUERY = 0x8;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        if (!LookupPrivilegeValue(null, name, out var luid)) return false;
        var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = SE_PRIVILEGE_ENABLED };
        return AdjustTokenPrivileges(token, false, ref tp,
            Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero);
    }

    // ================= PROCESSOS =================

    /// <summary>Processos que NUNCA podem ser encerrados - matar qualquer um destes
    /// derruba a sessao ou causa tela azul.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "fontdrvhost", "dwm", "Memory Compression",
        "svchost", "explorer", "sihost", "ctfmon", "RuntimeBroker", "Curupira"
    };

    /// <summary>Top processos por memoria de trabalho.</summary>
    public List<ProcessMemoryInfo> GetTopProcesses(int take = 25)
    {
        var list = new List<ProcessMemoryInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (name is "Idle" or "System") continue;

                double mb = p.WorkingSet64 / 1024.0 / 1024.0;
                if (mb < 5) { p.Dispose(); continue; }

                string title = "";
                string company = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }
                try { company = p.MainModule?.FileVersionInfo?.CompanyName ?? ""; } catch { }

                list.Add(new ProcessMemoryInfo(
                    Pid: p.Id,
                    Name: name,
                    WindowTitle: title,
                    Company: company,
                    MemoryMB: Math.Round(mb, 1),
                    IsProtected: Protected.Contains(name),
                    HasWindow: !string.IsNullOrWhiteSpace(title)));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        // Agrupa por nome somando instancias (Chrome/Brave abrem dezenas)
        var grouped = list
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var main = g.OrderByDescending(x => x.HasWindow).ThenByDescending(x => x.MemoryMB).First();
                return main with
                {
                    MemoryMB = Math.Round(g.Sum(x => x.MemoryMB), 1),
                    InstanceCount = g.Count()
                };
            })
            .OrderByDescending(x => x.MemoryMB)
            .Take(take)
            .ToList();

        _log.Debug("{Count} processos agrupados por memoria", grouped.Count);
        return grouped;
    }

    /// <summary>Encerra todas as instancias de um processo pelo nome.</summary>
    public ProcessActionResult CloseByName(string name, Action<string>? onLine)
    {
        if (Protected.Contains(name))
        {
            onLine?.Invoke($"[BLOQUEADO] {name} e um processo critico do sistema e nao pode ser encerrado.");
            return new ProcessActionResult(false, 0, 0, "Processo protegido");
        }

        int closed = 0;
        double freed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                freed += p.WorkingSet64 / 1024.0 / 1024.0;
                // Tenta fechar pela janela primeiro (permite salvar), depois forca
                if (!p.CloseMainWindow() || !p.WaitForExit(2500))
                    p.Kill(entireProcessTree: true);
                closed++;
            }
            catch (Exception ex) { onLine?.Invoke($"  Falha em PID {p.Id}: {ex.Message}"); }
            finally { try { p.Dispose(); } catch { } }
        }

        onLine?.Invoke(closed > 0
            ? $"{name}: {closed} instancia(s) encerrada(s), ~{freed:N0} MB devolvidos"
            : $"{name}: nenhuma instancia encerrada");
        _log.Information("Encerrado {Name}: {Closed} instancias, {Freed} MB", name, closed, freed);
        return new ProcessActionResult(closed > 0, closed, Math.Round(freed, 1), "");
    }

    /// <summary>Compacta o working set apenas de um processo, sem encerra-lo.</summary>
    public ProcessActionResult TrimByName(string name, Action<string>? onLine)
    {
        int trimmed = 0;
        double before = 0, after = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                before += p.WorkingSet64 / 1024.0 / 1024.0;
                if (SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1))) trimmed++;
                p.Refresh();
                after += p.WorkingSet64 / 1024.0 / 1024.0;
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        var freed = Math.Max(0, Math.Round(before - after, 1));
        onLine?.Invoke($"{name}: working set compactado em {trimmed} instancia(s), ~{freed:N0} MB");
        return new ProcessActionResult(trimmed > 0, trimmed, freed, "");
    }
}

public sealed record ProcessMemoryInfo(
    int Pid, string Name, string WindowTitle, string Company,
    double MemoryMB, bool IsProtected, bool HasWindow)
{
    public int InstanceCount { get; init; } = 1;

    public string Display => string.IsNullOrWhiteSpace(WindowTitle)
        ? Name
        : (WindowTitle.Length > 60 ? WindowTitle[..60] + "..." : WindowTitle);

    public string Detail => InstanceCount > 1
        ? $"{Name} · {InstanceCount} processos"
        : $"{Name} · PID {Pid}";

    public string Origin => string.IsNullOrWhiteSpace(Company) ? "origem desconhecida" : Company;

    public bool CanClose => !IsProtected;

    public string ProtectedNote => IsProtected ? "Componente do Windows" : "";
}

public sealed record ProcessActionResult(bool Success, int Count, double FreedMB, string Error);

public sealed record MemorySnapshot(double TotalMB, double AvailableMB, int LoadPercent)
{
    public double UsedMB => TotalMB - AvailableMB;
}

public sealed record MemoryCleanResult(
    MemorySnapshot Before, MemorySnapshot After, double FreedMB, int ProcessesTrimmed);
