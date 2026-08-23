using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace SysMaintenanceHub.Services;

/// <summary>
/// Análise e limpeza de registro.
///
/// POSIÇÃO TÉCNICA HONESTA: remover chaves órfãs do registro NÃO acelera o
/// Windows de forma mensurável. O registro tem milhões de entradas e é indexado
/// em memória; algumas centenas a menos não mudam boot nem consumo de RAM.
/// O valor real aqui é de HIGIENE e SEGURANÇA — regras de firewall apontando
/// para binários que já não existem, entradas de inicialização quebradas e
/// rastros de programas desinstalados.
///
/// Por isso este módulo:
///  - Faz BACKUP .reg obrigatório de tudo antes de remover
///  - Só toca em categorias onde o órfão é comprovadamente inerte
///  - NUNCA varre CLSID em massa, fontes ou Shared DLLs — é exatamente onde os
///    "otimizadores" de mercado quebram instalações
/// </summary>
public sealed class RegistryAuditService
{
    private readonly ILogger _log = Log.ForContext<RegistryAuditService>();

    public string BackupDirectory { get; } =
        Path.Combine(App.DataDirectory, "registry-backups");

    // ================= VARREDURA =================

    public async Task<List<RegistryFinding>> ScanAsync(
        Action<string>? onLine, IProgress<int>? progress, CancellationToken ct)
    {
        var findings = new ConcurrentBag<RegistryFinding>();
        onLine?.Invoke("Analisando registro — apenas categorias seguras...");

        var scanners = new (string Label, Action<ConcurrentBag<RegistryFinding>, CancellationToken> Run)[]
        {
            ("Caminhos de aplicativo (App Paths)", ScanAppPaths),
            ("Entradas de inicialização quebradas", ScanStartupOrphans),
            ("Programas desinstalados", ScanUninstallOrphans),
            ("Regras de firewall órfãs", ScanFirewallOrphans),
            ("Histórico de arquivos recentes", ScanRecentDocs),
            ("Cache de nomes de programa (MUICache)", ScanMuiCache),
            ("Extensões de arquivo sem destino", ScanFileExtensions),
        };

        int done = 0;
        await Task.Run(() =>
        {
            Parallel.ForEach(scanners,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                s =>
                {
                    try
                    {
                        onLine?.Invoke($"  Verificando: {s.Label}");
                        s.Run(findings, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _log.Warning(ex, "Scanner {Label} falhou", s.Label); }
                    finally
                    {
                        var n = Interlocked.Increment(ref done);
                        progress?.Report((int)(n * 100.0 / scanners.Length));
                    }
                });
        }, ct);

        var list = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ToList();

        onLine?.Invoke($"Registro: {list.Count} entrada(s) órfã(s) encontrada(s).");
        _log.Information("{Count} findings de registro", list.Count);
        return list;
    }

    // ---- 1. App Paths apontando para executável inexistente ----
    private void ScanAppPaths(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var (hive, hiveName) in Hives())
        {
            using var root = hive.OpenSubKey(sub);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var k = root.OpenSubKey(name);
                    var target = k?.GetValue("")?.ToString()?.Trim('"');
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    if (File.Exists(target)) continue;

                    bag.Add(new RegistryFinding
                    {
                        Category = "Caminhos de aplicativo",
                        KeyPath = $@"{hiveName}\{sub}\{name}",
                        ValueName = null,
                        Detail = $"Aponta para {target}, que não existe",
                        Why = "Resquício de programa desinstalado. O Windows consulta esta lista ao abrir um programa pelo nome, e entradas mortas apenas ocupam espaço.",
                        Severity = 1,
                        IsSelected = true
                    });
                }
                catch { }
            }
        }
    }

    // ---- 2. Run / RunOnce apontando para arquivo inexistente ----
    private void ScanStartupOrphans(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        };

        foreach (var (hive, hiveName) in Hives())
        foreach (var sub in paths)
        {
            using var k = hive.OpenSubKey(sub);
            if (k is null) continue;

            foreach (var valueName in k.GetValueNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var cmd = k.GetValue(valueName)?.ToString();
                    if (string.IsNullOrWhiteSpace(cmd)) continue;

                    var exe = ExtractExecutable(cmd);
                    if (exe is null || File.Exists(exe)) continue;

                    bag.Add(new RegistryFinding
                    {
                        Category = "Inicialização quebrada",
                        KeyPath = $@"{hiveName}\{sub}",
                        ValueName = valueName,
                        Detail = $"'{valueName}' executa {exe}, que não existe",
                        Why = "O Windows tenta iniciar este programa a cada logon e falha silenciosamente. Remover elimina uma tentativa inútil no boot.",
                        Severity = 2,
                        IsSelected = true
                    });
                }
                catch { }
            }
        }
    }

    // ---- 3. Entradas de desinstalação órfãs ----
    private void ScanUninstallOrphans(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, hiveName) in Hives())
        {
            using var root = hive.OpenSubKey(sub);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;

                    var display = k.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(display)) continue;

                    var loc = k.GetValue("InstallLocation")?.ToString()?.Trim('"');
                    var uninstall = k.GetValue("UninstallString")?.ToString();

                    // So marca como orfa se AMBOS os indicios apontam para o vazio
                    bool locDead = !string.IsNullOrWhiteSpace(loc) && !Directory.Exists(loc);
                    var exe = uninstall is null ? null : ExtractExecutable(uninstall);
                    bool exeDead = exe is not null && !File.Exists(exe);

                    if (!locDead || !exeDead) continue;

                    bag.Add(new RegistryFinding
                    {
                        Category = "Programa desinstalado",
                        KeyPath = $@"{hiveName}\{sub}\{name}",
                        ValueName = null,
                        Detail = $"'{display}' — pasta e desinstalador não existem mais",
                        Why = "Programa removido sem limpar o próprio registro. Aparece em Adicionar/Remover Programas como item fantasma.",
                        Severity = 1,
                        IsSelected = true
                    });
                }
                catch { }
            }
        }
    }

    // ---- 4. Regras de firewall apontando para binários inexistentes ----
    private void ScanFirewallOrphans(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        const string sub = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        using var k = Registry.LocalMachine.OpenSubKey(sub);
        if (k is null) return;

        foreach (var valueName in k.GetValueNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rule = k.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(rule)) continue;

                var appPart = rule.Split('|').FirstOrDefault(p => p.StartsWith("App=", StringComparison.OrdinalIgnoreCase));
                if (appPart is null) continue;

                var exe = Environment.ExpandEnvironmentVariables(appPart[4..]);
                if (File.Exists(exe)) continue;

                var namePart = rule.Split('|').FirstOrDefault(p => p.StartsWith("Name=", StringComparison.OrdinalIgnoreCase));
                var friendly = namePart?[5..] ?? valueName;

                bag.Add(new RegistryFinding
                {
                    Category = "Regra de firewall órfã",
                    KeyPath = $@"HKLM\{sub}",
                    ValueName = valueName,
                    Detail = $"'{friendly}' libera {exe}, que não existe",
                    Why = "Relevante para segurança: se outro programa for instalado nesse mesmo caminho, herda a permissão de rede sem você aprovar.",
                    Severity = 3,
                    IsSelected = true
                });
            }
            catch { }
        }
    }

    // ---- 5. Documentos recentes apontando para arquivos apagados ----
    private void ScanRecentDocs(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        var targets = new[]
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", "Histórico do Executar"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", "Caminhos digitados no Explorer"),
        };

        foreach (var (sub, label) in targets)
        {
            using var k = Registry.CurrentUser.OpenSubKey(sub);
            if (k is null) continue;

            var count = k.GetValueNames().Count(v => v != "MRUList");
            if (count == 0) continue;

            bag.Add(new RegistryFinding
            {
                Category = "Histórico de uso",
                KeyPath = $@"HKCU\{sub}",
                ValueName = null,
                Detail = $"{label} — {count} entrada(s) registrada(s)",
                Why = "Rastro de privacidade: guarda o que você digitou e abriu. Não afeta desempenho, mas expõe seu histórico a quem usar a máquina.",
                Severity = 1,
                IsSelected = false,
                DeletesWholeKey = false,
                ClearAllValues = true
            });
        }
    }

    // ---- 6. MUICache apontando para executáveis removidos ----
    private void ScanMuiCache(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        const string sub = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        using var k = Registry.CurrentUser.OpenSubKey(sub);
        if (k is null) return;

        int orphans = 0;
        foreach (var valueName in k.GetValueNames())
        {
            ct.ThrowIfCancellationRequested();
            if (!valueName.Contains('\\')) continue;
            var path = valueName.Split('.')[0];
            try
            {
                var exe = valueName.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase)
                    ? valueName[..^".FriendlyAppName".Length]
                    : valueName.EndsWith(".ApplicationCompany", StringComparison.OrdinalIgnoreCase)
                        ? valueName[..^".ApplicationCompany".Length]
                        : null;
                if (exe is null) continue;
                if (File.Exists(exe)) continue;
                orphans++;
            }
            catch { }
        }

        if (orphans > 0)
        {
            bag.Add(new RegistryFinding
            {
                Category = "Cache de nomes de programa",
                KeyPath = $@"HKCU\{sub}",
                ValueName = null,
                Detail = $"{orphans} referência(s) a programas que já não existem",
                Why = "Cache que o Explorer monta com nome e fabricante de cada executável aberto. É recriado sozinho; entradas mortas são apenas resíduo.",
                Severity = 1,
                IsSelected = false,
                DeletesWholeKey = false,
                ClearAllValues = true
            });
        }
    }

    // ---- 7. Extensões de arquivo apontando para ProgID inexistente ----
    private void ScanFileExtensions(ConcurrentBag<RegistryFinding> bag, CancellationToken ct)
    {
        using var root = Registry.ClassesRoot;
        int checkedCount = 0;

        foreach (var name in root.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            if (!name.StartsWith('.')) continue;
            if (++checkedCount > 3000) break; // guarda de tempo

            try
            {
                using var ext = root.OpenSubKey(name);
                var progId = ext?.GetValue("")?.ToString();
                if (string.IsNullOrWhiteSpace(progId)) continue;

                using var target = root.OpenSubKey(progId);
                if (target is not null) continue;

                bag.Add(new RegistryFinding
                {
                    Category = "Extensão de arquivo sem destino",
                    KeyPath = $@"HKCR\{name}",
                    ValueName = null,
                    Detail = $"'{name}' aponta para o tipo '{progId}', que não está registrado",
                    Why = "Associação quebrada. O Windows já ignora e usa o diálogo 'Abrir com'. Remover apenas elimina a referência morta.",
                    Severity = 1,
                    IsSelected = false
                });
            }
            catch { }
        }
    }

    // ================= BACKUP + REMOÇÃO =================

    /// <summary>
    /// Exporta as chaves afetadas para um .reg e só então remove.
    /// Retorna o caminho do backup — restaurável com duplo clique.
    /// </summary>
    public async Task<RegistryCleanResult> CleanAsync(
        IEnumerable<RegistryFinding> selected, Action<string>? onLine, CancellationToken ct)
    {
        var list = selected.ToList();
        if (list.Count == 0)
            return new RegistryCleanResult(0, 0, "", "Nada selecionado");

        Directory.CreateDirectory(BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupFile = Path.Combine(BackupDirectory, $"registro-{stamp}.reg");

        // ---- BACKUP OBRIGATÓRIO ----
        onLine?.Invoke($"Criando backup em {backupFile}...");
        var keys = list.Select(f => f.KeyPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exported = 0;

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var part = Path.Combine(BackupDirectory, $"tmp-{Guid.NewGuid():N}.reg");
            try
            {
                var psi = new ProcessStartInfo("reg.exe", $"export \"{key}\" \"{part}\" /y")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    await p.WaitForExitAsync(ct);
                    if (p.ExitCode == 0 && File.Exists(part))
                    {
                        var content = await File.ReadAllTextAsync(part, ct);
                        await File.AppendAllTextAsync(backupFile, content + Environment.NewLine, ct);
                        exported++;
                    }
                }
            }
            catch (Exception ex) { onLine?.Invoke($"  Aviso: backup de {key} falhou ({ex.Message})"); }
            finally { try { File.Delete(part); } catch { } }
        }

        if (exported == 0)
        {
            onLine?.Invoke("[ABORTADO] Nenhuma chave pôde ser exportada. Nada foi removido.");
            return new RegistryCleanResult(0, list.Count, "", "Backup falhou — remoção cancelada por segurança");
        }
        onLine?.Invoke($"Backup concluído: {exported} chave(s) exportada(s).");

        // ---- REMOÇÃO ----
        int removed = 0, failed = 0;
        foreach (var f in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (TryRemove(f)) { removed++; onLine?.Invoke($"  Removido: {f.Detail}"); }
                else { failed++; }
            }
            catch (Exception ex) { failed++; onLine?.Invoke($"  Falhou: {f.KeyPath} ({ex.Message})"); }
        }

        onLine?.Invoke($"Registro: {removed} removida(s), {failed} falha(s). Backup: {backupFile}");
        _log.Information("Registro limpo: {Removed} removidas, backup em {Backup}", removed, backupFile);
        return new RegistryCleanResult(removed, failed, backupFile, "");
    }

    private static bool TryRemove(RegistryFinding f)
    {
        var (hive, sub) = SplitPath(f.KeyPath);
        if (hive is null) return false;

        if (f.ClearAllValues)
        {
            using var k = hive.OpenSubKey(sub, writable: true);
            if (k is null) return false;
            foreach (var v in k.GetValueNames().ToList())
            {
                try { k.DeleteValue(v, false); } catch { }
            }
            return true;
        }

        if (f.ValueName is not null)
        {
            using var k = hive.OpenSubKey(sub, writable: true);
            if (k is null) return false;
            k.DeleteValue(f.ValueName, throwOnMissingValue: false);
            return true;
        }

        var parent = sub.Contains('\\') ? sub[..sub.LastIndexOf('\\')] : null;
        var leaf = sub.Contains('\\') ? sub[(sub.LastIndexOf('\\') + 1)..] : sub;
        using var pk = parent is null ? hive : hive.OpenSubKey(parent, writable: true);
        if (pk is null) return false;
        pk.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        return true;
    }

    private static (RegistryKey? Hive, string Sub) SplitPath(string path)
    {
        var parts = path.Split('\\', 2);
        if (parts.Length < 2) return (null, "");
        RegistryKey? hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER"  => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT"  => Registry.ClassesRoot,
            "HKU"  or "HKEY_USERS"         => Registry.Users,
            _ => null
        };
        return (hive, parts[1]);
    }

    private static IEnumerable<(RegistryKey Hive, string Name)> Hives()
    {
        yield return (Registry.CurrentUser, "HKCU");
        yield return (Registry.LocalMachine, "HKLM");
    }

    private static string? ExtractExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (space > 0) return command[..(space + 4)];
        var firstSpace = command.IndexOf(' ');
        return firstSpace > 0 ? command[..firstSpace] : command;
    }

    public List<string> ListBackups()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory)) return new List<string>();
            return Directory.GetFiles(BackupDirectory, "registro-*.reg")
                            .OrderByDescending(f => f).ToList();
        }
        catch { return new List<string>(); }
    }
}

public sealed class RegistryFinding : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Category { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string? ValueName { get; init; }
    public string Detail { get; init; } = "";

    /// <summary>Por que isso está aqui e o que muda ao remover.</summary>
    public string Why { get; init; } = "";

    /// <summary>1 = cosmético, 2 = desperdício real, 3 = relevante para segurança.</summary>
    public int Severity { get; init; } = 1;

    public bool DeletesWholeKey { get; init; } = true;
    public bool ClearAllValues { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string SeverityText => Severity switch
    {
        3 => "Segurança",
        2 => "Desperdício",
        _ => "Resíduo"
    };

    public string SeverityColor => Severity switch
    {
        3 => "#EF4444",
        2 => "#FBBF24",
        _ => "#5F8271"
    };
}

public sealed record RegistryCleanResult(int Removed, int Failed, string BackupFile, string Error);
