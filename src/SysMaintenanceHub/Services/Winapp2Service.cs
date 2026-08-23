using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Motor de limpeza baseado no winapp2.ini — base comunitária com milhares de
/// regras mantida há 15+ anos. O CCleaner 7 abandonou esse formato; aqui ele é
/// implementado com parser próprio.
///
/// DESEMPENHO: a varredura roda em paralelo com grau limitado pelo número de
/// núcleos, ignora reparse points e diretórios inacessíveis sem lançar exceção,
/// e respeita cancelamento a cada alvo — para não travar em máquinas lentas.
/// </summary>
public sealed class Winapp2Service
{
    private const string OfficialUrl =
        "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ILogger _log = Log.ForContext<Winapp2Service>();

    private List<Winapp2Entry>? _cache;

    public string DatabasePath { get; set; } =
        Path.Combine(App.DataDirectory, "winapp2.ini");

    public bool DatabaseExists => File.Exists(DatabasePath);

    public DateTime? DatabaseDate =>
        DatabaseExists ? File.GetLastWriteTime(DatabasePath) : null;

    /// <summary>Enumeração rápida e tolerante a falhas.</summary>
    private static EnumerationOptions FastEnum(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
        ReturnSpecialDirectories = false
    };

    // ================= BASE DE DADOS =================

    public async Task<bool> DownloadDatabaseAsync(Action<string>? onLine, CancellationToken ct)
    {
        try
        {
            onLine?.Invoke($"Baixando base winapp2.ini de {OfficialUrl}...");
            var content = await Http.GetStringAsync(OfficialUrl, ct);

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await File.WriteAllTextAsync(DatabasePath, content, ct);

            _cache = null;
            var kb = content.Length / 1024.0;
            onLine?.Invoke($"Base salva: {kb:N0} KB em {DatabasePath}");
            _log.Information("winapp2.ini baixado ({Kb} KB)", (int)kb);
            return true;
        }
        catch (Exception ex)
        {
            onLine?.Invoke($"[ERRO] Falha ao baixar base: {ex.Message}");
            _log.Error(ex, "Falha ao baixar winapp2.ini");
            return false;
        }
    }

    public async Task<List<Winapp2Entry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        if (!DatabaseExists) return new List<Winapp2Entry>();

        var text = await File.ReadAllTextAsync(DatabasePath, ct);
        _cache = await Task.Run(() => Winapp2Parser.Parse(text), ct);
        _log.Information("{Count} entradas carregadas do winapp2.ini", _cache.Count);
        return _cache;
    }

    // ================= DETECÇÃO + VARREDURA =================

    /// <summary>
    /// Detecta quais entradas se aplicam a esta máquina e calcula o tamanho
    /// recuperável de cada uma. Paralelizado e cancelável.
    /// </summary>
    public async Task<List<Winapp2Result>> ScanAsync(
        Action<string>? onLine, IProgress<int>? progress, CancellationToken ct)
    {
        var entries = await LoadEntriesAsync(ct);
        if (entries.Count == 0)
        {
            onLine?.Invoke("Base winapp2.ini ausente. Baixe a base para habilitar as regras da comunidade.");
            return new List<Winapp2Result>();
        }

        onLine?.Invoke($"Detectando quais das {entries.Count} regras se aplicam a esta máquina...");

        // Detecção é barata (abrir chave de registro / File.Exists)
        var present = await Task.Run(
            () => entries.AsParallel()
                         .WithCancellation(ct)
                         .Where(Winapp2Parser.IsPresent)
                         .ToList(), ct);

        onLine?.Invoke($"{present.Count} regras aplicáveis. Calculando espaço recuperável...");

        var results = new ConcurrentBag<Winapp2Result>();
        int done = 0;
        var total = present.Count;

        var parallel = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1)
        };

        await Task.Run(() =>
        {
            Parallel.ForEach(present, parallel, entry =>
            {
                try
                {
                    var (bytes, files) = MeasureEntry(entry, ct);
                    if (bytes > 0)
                    {
                        results.Add(new Winapp2Result(entry)
                        {
                            SizeMB = Math.Round(bytes / 1024.0 / 1024.0, 2),
                            FileCount = files,
                            IsSelected = true
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* uma regra ruim nao derruba a varredura */ }
                finally
                {
                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0) progress?.Report((int)(n * 100.0 / Math.Max(1, total)));
                }
            });
        }, ct);

        var ordered = results.OrderByDescending(r => r.SizeMB).ToList();
        var totalMb = ordered.Sum(r => r.SizeMB);
        onLine?.Invoke($"winapp2: {ordered.Count} alvos com conteúdo · {totalMb:N1} MB recuperáveis");
        return ordered;
    }

    private (long Bytes, int Files) MeasureEntry(Winapp2Entry entry, CancellationToken ct)
    {
        long bytes = 0;
        int count = 0;

        foreach (var t in entry.FileTargets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var dir in ResolveDirs(t.Path))
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, NormalizeFilter(t.Filter), FastEnum(t.Recurse)))
                    {
                        try
                        {
                            bytes += new FileInfo(f).Length;
                            count++;
                            // Guarda de sanidade: nao gastar minutos numa unica regra
                            if (count > 60_000) return (bytes, count);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return (bytes, count);
    }

    // ================= LIMPEZA =================

    public async Task<Winapp2CleanSummary> CleanAsync(
        IEnumerable<Winapp2Result> selected, Action<string>? onLine,
        IProgress<int>? progress, CancellationToken ct)
    {
        var list = selected.ToList();
        long freed = 0;
        int deleted = 0, failed = 0, done = 0;

        await Task.Run(() =>
        {
            foreach (var r in list)
            {
                ct.ThrowIfCancellationRequested();
                onLine?.Invoke($"Limpando: {r.Name} ({r.SizeMB:N1} MB)");

                foreach (var t in r.Entry.FileTargets)
                {
                    foreach (var dir in ResolveDirs(t.Path))
                    {
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(dir, NormalizeFilter(t.Filter), FastEnum(t.Recurse)))
                            {
                                ct.ThrowIfCancellationRequested();
                                try
                                {
                                    var len = new FileInfo(f).Length;
                                    File.SetAttributes(f, FileAttributes.Normal);
                                    File.Delete(f);
                                    freed += len;
                                    deleted++;
                                }
                                catch { failed++; }
                            }

                            if (t.RemoveSelf)
                            {
                                try { Directory.Delete(dir, true); } catch { }
                            }
                        }
                        catch { }
                    }
                }

                done++;
                progress?.Report((int)(done * 100.0 / Math.Max(1, list.Count)));
            }
        }, ct);

        var mb = Math.Round(freed / 1024.0 / 1024.0, 1);
        onLine?.Invoke($"winapp2: {deleted} arquivos removidos · {mb:N1} MB liberados · {failed} em uso/protegidos");
        _log.Information("winapp2 limpeza: {Deleted} arquivos, {Mb} MB", deleted, mb);
        return new Winapp2CleanSummary(deleted, failed, mb, list.Count);
    }

    // ================= HELPERS =================

    /// <summary>Resolve curingas no meio do caminho (ex.: User Data\*\Cache).</summary>
    private static IEnumerable<string> ResolveDirs(string rawPath)
    {
        var path = Winapp2Parser.ExpandPath(rawPath).TrimEnd('\\');
        if (!path.Contains('*')) { yield return path; yield break; }

        var idx = path.IndexOf('*');
        var sepBefore = path.LastIndexOf('\\', idx);
        if (sepBefore < 0) yield break;

        var baseDir = path[..sepBefore];
        var rest = path[(sepBefore + 1)..];
        if (!Directory.Exists(baseDir)) yield break;

        var nextSep = rest.IndexOf('\\');
        var segment = nextSep < 0 ? rest : rest[..nextSep];
        var tail = nextSep < 0 ? "" : rest[(nextSep + 1)..];

        IEnumerable<string> matches;
        try { matches = Directory.EnumerateDirectories(baseDir, segment, FastEnum(false)); }
        catch { yield break; }

        foreach (var m in matches)
        {
            if (tail.Length == 0) yield return m;
            else foreach (var sub in ResolveDirs(Path.Combine(m, tail))) yield return sub;
        }
    }

    private static string NormalizeFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "*.*") return "*";
        // winapp2 aceita multiplos filtros separados por ';' — usamos o primeiro
        var first = filter.Split(';')[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? "*" : first;
    }
}

/// <summary>Resultado da varredura de uma entrada winapp2.</summary>
public sealed class Winapp2Result : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public Winapp2Entry Entry { get; }
    public Winapp2Result(Winapp2Entry e) => Entry = e;

    public string Name => Entry.Name;
    public string Category => Entry.Category;
    public string? Warning => Entry.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Entry.Warning);
    public int TargetCount => Entry.FileTargets.Count;

    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private double _sizeMB;
    public double SizeMB { get => _sizeMB; set => SetProperty(ref _sizeMB, value); }

    private int _fileCount;
    public int FileCount { get => _fileCount; set => SetProperty(ref _fileCount, value); }

    public string SizeText => SizeMB >= 1024 ? $"{SizeMB / 1024:N2} GB" : $"{SizeMB:N1} MB";
}

public sealed record Winapp2CleanSummary(int FilesDeleted, int FilesFailed, double FreedMB, int EntriesProcessed);
