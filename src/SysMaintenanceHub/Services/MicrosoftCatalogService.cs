using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Consulta a página oficial de release-health da Microsoft e compara com a
/// build local para identificar KBs pendentes.
/// </summary>
public sealed class MicrosoftCatalogService
{
    private const string Win11Url = "https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information";
    private const string Win10Url = "https://learn.microsoft.com/en-us/windows/release-health/release-information";
    private const string CatalogSearch = "https://www.catalog.update.microsoft.com/Search.aspx?q={0}";

    private static readonly HttpClient Http = CreateClient();
    private readonly ILogger _log = Log.ForContext<MicrosoftCatalogService>();
    private readonly PowerShellRunner _ps;

    public MicrosoftCatalogService(PowerShellRunner ps) => _ps = ps;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var c = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        c.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SysMaintenanceHub/1.1");
        c.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return c;
    }

    public OsBuildInfo GetCurrentOsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = key?.GetValue("ProductName")?.ToString() ?? "Windows";
        var displayVersion = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? "";
        var currentBuild = key?.GetValue("CurrentBuild")?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR") is int u ? u : 0;
        var editionId = key?.GetValue("EditionID")?.ToString() ?? "";

        var major = key?.GetValue("CurrentMajorVersionNumber") is int mj ? mj : Environment.OSVersion.Version.Major;
        var buildInt = int.TryParse(currentBuild, out var bi) ? bi : Environment.OSVersion.Version.Build;
        var isWin11 = buildInt >= 22000;

        var fullBuild = $"{currentBuild}.{ubr}";
        var version = $"{major}.0.{fullBuild}";

        return new OsBuildInfo(
            ProductName: isWin11 ? "Windows 11" : productName,
            EditionId: editionId,
            DisplayVersion: displayVersion,
            Build: buildInt,
            UBR: ubr,
            FullVersion: version,
            IsWindows11: isWin11);
    }

    public async Task<List<string>> GetInstalledKbsAsync(CancellationToken ct)
    {
        const string script = @"
Get-HotFix | Select-Object -ExpandProperty HotFixID
";
        var result = await _ps.RunAsync(script, null, ct);
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<LatestKbFromCatalog?> FetchLatestFromMicrosoftAsync(OsBuildInfo os, Action<string>? onLine, CancellationToken ct)
    {
        var url = os.IsWindows11 ? Win11Url : Win10Url;
        onLine?.Invoke($"Consultando release-health da Microsoft: {url}");
        try
        {
            var html = await Http.GetStringAsync(url, ct);
            var latest = ParseLatestFromHistory(html, os);
            if (latest is null)
            {
                _log.Warning("Nenhum KB extraído do HTML");
                return null;
            }
            _log.Information("Última KB oficial detectada: {Kb} → build {Build}", latest.Kb, latest.BuildString);
            onLine?.Invoke($"Última KB oficial: {latest.Kb} (build {latest.BuildString}, publicada em {latest.PublishedAt:dd/MM/yyyy})");
            return latest;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Falha ao consultar catálogo Microsoft");
            onLine?.Invoke($"[ERRO] Catálogo Microsoft: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extrai a linha mais recente da tabela release-history.
    /// A página tem linhas do formato:
    ///   &lt;tr&gt;&lt;td&gt;2026-06-11&lt;/td&gt;&lt;td&gt;OS Build 22631.3810&lt;/td&gt;&lt;td&gt;General Availability&lt;/td&gt;&lt;td&gt;&lt;a href="https://support.microsoft.com/help/KB5039212"&gt;KB5039212&lt;/a&gt;&lt;/td&gt;&lt;/tr&gt;
    /// </summary>
    private static LatestKbFromCatalog? ParseLatestFromHistory(string html, OsBuildInfo os)
    {
        var rowRegex = new Regex(@"<tr[^>]*>(?<row>[\s\S]*?)</tr>", RegexOptions.IgnoreCase);
        var cellRegex = new Regex(@"<td[^>]*>(?<cell>[\s\S]*?)</td>", RegexOptions.IgnoreCase);
        var kbLinkRegex = new Regex(@"(KB\d{5,8})", RegexOptions.IgnoreCase);
        var buildRegex = new Regex(@"(?<major>\d{4,6})\.(?<minor>\d{1,6})", RegexOptions.IgnoreCase);
        var dateRegex = new Regex(@"(?<y>\d{4})[-/](?<m>\d{2})[-/](?<d>\d{2})|(?<m2>\d{4})-(?<mo>\d{2})-(?<da>\d{2})");

        var candidates = new List<LatestKbFromCatalog>();

        foreach (Match rowMatch in rowRegex.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;
            var cells = cellRegex.Matches(row).Select(m => StripHtml(m.Groups["cell"].Value)).ToList();
            if (cells.Count < 3) continue;

            var joined = string.Join(" | ", cells);
            var buildM = buildRegex.Match(joined);
            var kbM = kbLinkRegex.Match(joined);
            if (!buildM.Success || !kbM.Success) continue;

            var buildMajor = int.Parse(buildM.Groups["major"].Value);
            var buildMinor = int.Parse(buildM.Groups["minor"].Value);
            if (Math.Abs(buildMajor - os.Build) > 1) continue;

            DateTime? published = null;
            foreach (var cell in cells)
            {
                var dm = Regex.Match(cell, @"(\d{4})-(\d{2})-(\d{2})");
                if (dm.Success && DateTime.TryParse(dm.Value, out var d))
                {
                    published = d;
                    break;
                }
            }

            candidates.Add(new LatestKbFromCatalog(
                Kb: kbM.Groups[1].Value.ToUpperInvariant(),
                BuildMajor: buildMajor,
                BuildMinor: buildMinor,
                BuildString: $"{buildMajor}.{buildMinor}",
                PublishedAt: published ?? DateTime.MinValue,
                CatalogUrl: string.Format(CatalogSearch, kbM.Groups[1].Value)));
        }

        return candidates
            .OrderByDescending(c => c.PublishedAt)
            .ThenByDescending(c => c.BuildMinor)
            .FirstOrDefault();
    }

    private static string StripHtml(string s)
    {
        var noTags = Regex.Replace(s, @"<[^>]+>", " ");
        noTags = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }
}
