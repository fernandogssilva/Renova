using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

public sealed class DefragService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<DefragService>();

    public DefragService(PowerShellRunner ps) => _ps = ps;

    public async Task<List<DriveDefragInfo>> AnalyzeAsync(Action<string>? onLine, CancellationToken ct)
    {
        var list = new List<DriveDefragInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var letter = d.RootDirectory.Name.TrimEnd('\\').TrimEnd(':');
            onLine?.Invoke($"Analisando {letter}:...");
            var script = $@"
try {{
    $r = Optimize-Volume -DriveLetter {letter} -Analyze -Verbose 4>&1 | Out-String
    Write-Output $r
}} catch {{ Write-Output ('ERRO: ' + $_.Exception.Message) }}
";
            var result = await _ps.RunAsync(script, onLine, ct);
            double frag = ExtractFragmentation(result.StdOut);
            list.Add(new DriveDefragInfo($"{letter}:", frag, LastDefragFromRegistry(letter)));
        }
        return list;
    }

    public async Task OptimizeAsync(string driveLetter, Action<string>? onLine, CancellationToken ct)
    {
        var letter = driveLetter.TrimEnd(':').TrimEnd('\\');
        var script = $@"
try {{
    Optimize-Volume -DriveLetter {letter} -Defrag -Verbose 4>&1 | Out-String
}} catch {{ Write-Output ('ERRO: ' + $_.Exception.Message) }}
";
        await _ps.RunAsync(script, onLine, ct);
    }

    private static double ExtractFragmentation(string output)
    {
        var m = Regex.Match(output, @"(\d+)\s*%\s*fragment", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var p)) return p;
        m = Regex.Match(output, @"fragmenta[cç][aã]o\s*(?:total)?\s*[:=]?\s*(\d+)\s*%", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var p2)) return p2;
        return 0;
    }

    private static DateTime? LastDefragFromRegistry(string letter)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Dfrg\Statistics\Volume{letter}");
            var val = key?.GetValue("LastRunTime") as byte[];
            if (val is { Length: 8 })
            {
                var ticks = BitConverter.ToInt64(val, 0);
                if (ticks > 0) return DateTime.FromFileTimeUtc(ticks).ToLocalTime();
            }
        }
        catch { }
        return null;
    }
}
