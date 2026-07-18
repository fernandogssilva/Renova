using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Executes a PowerShell script block, streams stdout/stderr to a callback,
/// and returns the full output plus the exit code.
/// </summary>
public sealed class PowerShellRunner
{
    private readonly ILogger _log = Log.ForContext<PowerShellRunner>();

    public async Task<PsResult> RunAsync(
        string script,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"smh_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(true), ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{scriptFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sbOut.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sbErr.AppendLine(e.Data);
            onLine?.Invoke($"[ERR] {e.Data}");
        };

        _log.Debug("Executando script PowerShell ({Path})", scriptFile);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }

        _log.Debug("Script terminado com código {Code}", proc.ExitCode);
        return new PsResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }
}

public sealed record PsResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}
