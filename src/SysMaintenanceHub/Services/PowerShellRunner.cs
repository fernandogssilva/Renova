using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Executes a PowerShell script block, streams stdout/stderr to a callback,
/// and returns the full output plus the exit code.
///
/// SEGURANÇA: scripts são gravados em pasta privada do app com ACL restrita
/// a Administrators + SYSTEM, evitando que outro processo do mesmo usuário
/// substitua o conteúdo entre o write e o exec (TOCTOU / EoP local).
/// </summary>
public sealed class PowerShellRunner
{
    private static readonly string ScriptDir = InitScriptDir();
    private readonly ILogger _log = Log.ForContext<PowerShellRunner>();

    private static string InitScriptDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMaintenanceHub", ".ps");
        Directory.CreateDirectory(dir);
        try
        {
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.SetAccessRuleProtection(true, false); // remove inherited rules
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            sec.AddAccessRule(new FileSystemAccessRule(admins,
                FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(system,
                FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch { /* ACL best-effort; continua com ACL herdada se falhar */ }
        return dir;
    }

    public async Task<PsResult> RunAsync(
        string script,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var scriptFile = Path.Combine(ScriptDir, $"smh_{Guid.NewGuid():N}.ps1");
        // Write com FileShare.None para evitar substituição concorrente
        await using (var fs = new FileStream(scriptFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(script);
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

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
