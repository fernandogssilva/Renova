using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

public sealed class WingetService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<WingetService>();

    public WingetService(PowerShellRunner ps) => _ps = ps;

    public async Task<List<WingetUpdateItem>> ListUpgradesAsync(Action<string>? onLine, CancellationToken ct)
    {
        const string script = @"
$env:LC_ALL='C.UTF-8'
winget upgrade --accept-source-agreements --disable-interactivity 2>&1
";
        var result = await _ps.RunAsync(script, onLine, ct);
        var list = new List<WingetUpdateItem>();
        var lines = result.StdOut.Split('\n');
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (Regex.IsMatch(l, @"^\-{5,}"))
            {
                headerIndex = i;
                break;
            }
        }
        if (headerIndex < 0) return list;

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(" ") || line.StartsWith("\t")) continue;
            if (line.Contains("upgrades available") || line.Contains("atualiza")) break;

            var cols = Regex.Split(line, @"\s{2,}");
            if (cols.Length < 4) continue;

            list.Add(new WingetUpdateItem(
                cols.Length > 1 ? cols[1] : "",
                cols[0],
                cols.Length > 2 ? cols[2] : "",
                cols.Length > 3 ? cols[3] : ""));
        }
        _log.Information("{Count} atualizações Winget pendentes", list.Count);
        return list;
    }

    public async Task<bool> UpgradeAllAsync(Action<string>? onLine, CancellationToken ct)
    {
        const string script = @"
$env:LC_ALL='C.UTF-8'
winget upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown 2>&1 |
    ForEach-Object { Write-Output $_ }
";
        var result = await _ps.RunAsync(script, onLine, ct);
        return result.Success;
    }
}
