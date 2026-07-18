using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

public sealed class CleanupService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger _log = Log.ForContext<CleanupService>();
    private static readonly string LastCleanupFile =
        Path.Combine(App.DataDirectory, "last_cleanup.txt");

    public CleanupService(PowerShellRunner ps) => _ps = ps;

    public List<CleanableItem> Enumerate()
    {
        var items = new List<CleanableItem>();
        var candidates = new (string Name, string Path)[]
        {
            ("User TEMP", Environment.ExpandEnvironmentVariables("%TEMP%")),
            ("Windows TEMP", @"C:\Windows\Temp"),
            ("Windows Prefetch", @"C:\Windows\Prefetch"),
            ("Windows Update Downloads", @"C:\Windows\SoftwareDistribution\Download"),
            ("Windows Old", @"C:\Windows.old"),
            ("Delivery Optimization Cache", @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
            ("Brave Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"BraveSoftware\Brave-Browser\User Data\Default\Cache")),
            ("Edge Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Default\Cache")),
            ("Chrome Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Default\Cache")),
            ("NuGet Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget")),
            ("npm Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache")),
            ("pip Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"pip\Cache")),
            ("Go Modules Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"go\pkg")),
        };
        foreach (var (name, path) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            try
            {
                double mb = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { mb += new FileInfo(f).Length / 1024.0 / 1024.0; } catch { }
                }
                if (mb >= 0.5) items.Add(new CleanableItem(name, Math.Round(mb, 1), path));
            }
            catch (Exception ex) { _log.Warning(ex, "Falha lendo {Path}", path); }
        }
        _log.Information("{Count} alvos de limpeza mapeados", items.Count);
        return items;
    }

    public async Task<double> CleanAsync(IEnumerable<CleanableItem> items, Action<string>? onLine, CancellationToken ct)
    {
        double freed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            onLine?.Invoke($"Limpando {item.Name} ({item.SizeMB:N1} MB) em {item.Path}...");
            try
            {
                if (!Directory.Exists(item.Path)) continue;
                var sizeBefore = TryMeasure(item.Path);
                foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(item.Path))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
                var sizeAfter = TryMeasure(item.Path);
                var delta = Math.Max(0, sizeBefore - sizeAfter);
                freed += delta;
                onLine?.Invoke($"  {item.Name}: {delta:N1} MB liberados");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Erro limpando {Path}", item.Path);
                onLine?.Invoke($"  ERRO em {item.Name}: {ex.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            await File.WriteAllTextAsync(LastCleanupFile, DateTime.Now.ToString("O"), ct);
        }
        catch { }

        return Math.Round(freed, 1);
    }

    public async Task RunDiskCleanupAsync(Action<string>? onLine, CancellationToken ct)
    {
        const string script = @"
$caches = @('Active Setup Temp Folders','BranchCache','D3D Shader Cache',
'Delivery Optimization Files','Diagnostic Data Viewer database files',
'Downloaded Program Files','Internet Cache Files','Language Pack',
'Offline Pages Files','Old ChkDsk Files','Recycle Bin',
'RetailDemo Offline Content','Setup Log Files',
'System error memory dump files','System error minidump files',
'Temporary Files','Temporary Setup Files','Temporary Sync Files',
'Thumbnail Cache','Update Cleanup','Upgrade Discarded Files',
'User file versions','Windows Defender','Windows Error Reporting Files',
'Windows ESD installation files','Windows Upgrade Log Files')
foreach ($c in $caches) {
    $key = ""HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\$c""
    if (Test-Path -LiteralPath $key) {
        New-ItemProperty -Path $key -Name StateFlags0099 -Value 2 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    }
}
Write-Output 'Executando cleanmgr /sagerun:99...'
Start-Process -FilePath cleanmgr.exe -ArgumentList '/sagerun:99' -Wait -WindowStyle Hidden
Write-Output 'Disk Cleanup concluido.'
";
        await _ps.RunAsync(script, onLine, ct);
    }

    public DateTime? LastCleanup()
    {
        try
        {
            if (File.Exists(LastCleanupFile) &&
                DateTime.TryParse(File.ReadAllText(LastCleanupFile), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }
        catch { }
        return null;
    }

    private static double TryMeasure(string path)
    {
        double mb = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { mb += new FileInfo(f).Length / 1024.0 / 1024.0; } catch { }
            }
        }
        catch { }
        return mb;
    }
}
