using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SysMaintenanceHub.Services;

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
}

public sealed record MemorySnapshot(double TotalMB, double AvailableMB, int LoadPercent)
{
    public double UsedMB => TotalMB - AvailableMB;
}

public sealed record MemoryCleanResult(
    MemorySnapshot Before, MemorySnapshot After, double FreedMB, int ProcessesTrimmed);
