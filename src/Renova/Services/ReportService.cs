using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace Renova.Services;

/// <summary>
/// Monta um pacote de report de falha: descrição do usuário + imagens
/// anexadas + diagnóstico técnico coletado automaticamente.
///
/// PRIVACIDADE: todo texto que entra no pacote passa por sanitização —
/// nome de usuário, e-mail, número de série e chave de produto são
/// mascarados antes de gravar. O pacote fica no disco do usuário; nada é
/// enviado automaticamente. Quem decide compartilhar é ele.
/// </summary>
public sealed class ReportService
{
    private readonly ILogger _log = Log.ForContext<ReportService>();
    private readonly PowerShellRunner _ps;

    public ReportService(PowerShellRunner ps) => _ps = ps;

    public string ReportsDirectory { get; } =
        Path.Combine(App.DataDirectory, "reports");

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ================= SANITIZAÇÃO =================

    /// <summary>
    /// Remove dados que identificam a pessoa ou a máquina. Chamado em tudo
    /// que vai para o pacote — inclusive no texto digitado pelo usuário,
    /// que pode conter um caminho colado sem ele perceber.
    /// </summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;

        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user))
            s = Regex.Replace(s, Regex.Escape(user), "%USUARIO%", RegexOptions.IgnoreCase);

        var machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(machine))
            s = Regex.Replace(s, Regex.Escape(machine), "%MAQUINA%", RegexOptions.IgnoreCase);

        // e-mails
        s = Regex.Replace(s, @"[\w\.\-\+]+@[\w\-]+\.[\w\.\-]+", "%EMAIL%");
        // chaves de produto no formato XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
        s = Regex.Replace(s, @"\b([A-Z0-9]{5}-){4}[A-Z0-9]{5}\b", "%CHAVE%");
        // IPs privados e públicos
        s = Regex.Replace(s, @"\b\d{1,3}(\.\d{1,3}){3}\b", "%IP%");
        // tokens longos (sk-, ghp-, etc.)
        s = Regex.Replace(s, @"\b(sk|ghp|gho|github_pat)[-_][A-Za-z0-9_\-]{16,}\b", "%TOKEN%");

        return s;
    }

    // ================= DIAGNÓSTICO =================

    public async Task<string> CollectDiagnosticsAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Diagnóstico automático");
        sb.AppendLine();
        sb.AppendLine("| Item | Valor |");
        sb.AppendLine("|---|---|");

        var asm = typeof(App).Assembly.GetName();
        sb.AppendLine($"| Renova | {asm.Version} |");
        sb.AppendLine($"| Executando como admin | {AdminGuard.IsAdministrator()} |");
        sb.AppendLine($"| .NET | {Environment.Version} |");
        sb.AppendLine($"| Arquitetura | {(Environment.Is64BitProcess ? "x64" : "x86")} |");
        sb.AppendLine($"| Núcleos lógicos | {Environment.ProcessorCount} |");
        sb.AppendLine($"| Idioma | {System.Globalization.CultureInfo.CurrentUICulture.Name} |");
        sb.AppendLine($"| Data do report | {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} |");

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString();
            var display = key?.GetValue("DisplayVersion")?.ToString();
            var build = key?.GetValue("CurrentBuild")?.ToString();
            var ubr = key?.GetValue("UBR");
            sb.AppendLine($"| Windows | {product} {display} (build {build}.{ubr}) |");
        }
        catch { sb.AppendLine("| Windows | não foi possível ler |"); }

        try
        {
            var proc = Process.GetCurrentProcess();
            sb.AppendLine($"| Memória do app | {proc.WorkingSet64 / 1024 / 1024:N0} MB |");
            sb.AppendLine($"| Tempo aberto | {(DateTime.Now - proc.StartTime):hh\\:mm\\:ss} |");
        }
        catch { }

        sb.AppendLine();
        return await Task.FromResult(sb.ToString());
    }

    /// <summary>Últimas linhas do log, já sanitizadas.</summary>
    public async Task<string> CollectLogTailAsync(int lines, CancellationToken ct)
    {
        try
        {
            var dir = App.LogDirectory;
            if (!Directory.Exists(dir)) return "_Nenhum log encontrado._";

            var newest = new DirectoryInfo(dir)
                .GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (newest is null) return "_Nenhum log encontrado._";

            var all = await File.ReadAllLinesAsync(newest.FullName, ct);
            var tail = all.Skip(Math.Max(0, all.Length - lines));
            return Sanitize(string.Join(Environment.NewLine, tail));
        }
        catch (Exception ex)
        {
            return $"_Falha ao ler o log: {ex.Message}_";
        }
    }

    // ================= MONTAGEM DO PACOTE =================

    public async Task<ReportResult> BuildAsync(
        ReportInput input, Action<string>? onLine, CancellationToken ct)
    {
        if (input.Attachments.Count == 0)
            return new ReportResult(false, "", "", "É obrigatório anexar ao menos uma imagem do problema.");

        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length < 20)
            return new ReportResult(false, "", "", "Descreva o problema com pelo menos 20 caracteres.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var folder = Path.Combine(ReportsDirectory, $"report-{stamp}");
        var imagesFolder = Path.Combine(folder, "imagens");
        Directory.CreateDirectory(imagesFolder);

        onLine?.Invoke($"Montando pacote em {folder}...");

        // ---- copia as imagens ----
        int idx = 1;
        var copied = new List<string>();
        foreach (var att in input.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ext = Path.GetExtension(att.Path);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var destName = $"imagem-{idx:00}{ext}";
                File.Copy(att.Path, Path.Combine(imagesFolder, destName), true);
                copied.Add(destName);
                onLine?.Invoke($"  anexada: {destName} ({att.SizeKB:N0} KB)");
                idx++;
            }
            catch (Exception ex)
            {
                onLine?.Invoke($"  falha ao anexar {Path.GetFileName(att.Path)}: {ex.Message}");
            }
        }

        if (copied.Count == 0)
            return new ReportResult(false, "", "", "Nenhuma imagem pôde ser copiada para o pacote.");

        // ---- monta o report.md ----
        var diag = await CollectDiagnosticsAsync(ct);
        var logs = await CollectLogTailAsync(150, ct);

        var md = new StringBuilder();
        md.AppendLine($"# Report de falha — Renova");
        md.AppendLine();
        md.AppendLine($"**Título:** {Sanitize(input.Title)}");
        md.AppendLine($"**Módulo afetado:** {input.Module}");
        md.AppendLine($"**Severidade:** {input.Severity}");
        md.AppendLine($"**Reprodutível:** {(input.Reproducible ? "sim, acontece sempre" : "não, aconteceu uma vez")}");
        md.AppendLine();
        md.AppendLine("## O que aconteceu");
        md.AppendLine();
        md.AppendLine(Sanitize(input.Description.Trim()));
        md.AppendLine();

        if (!string.IsNullOrWhiteSpace(input.StepsToReproduce))
        {
            md.AppendLine("## Passos para reproduzir");
            md.AppendLine();
            md.AppendLine(Sanitize(input.StepsToReproduce.Trim()));
            md.AppendLine();
        }

        md.AppendLine("## Evidências");
        md.AppendLine();
        foreach (var img in copied)
            md.AppendLine($"- ![{img}](imagens/{img})");
        md.AppendLine();

        md.Append(diag);

        md.AppendLine("## Log (últimas 150 linhas, sanitizado)");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine(logs);
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine("_Dados pessoais foram mascarados automaticamente: nome de usuário, nome da máquina, e-mails, IPs, chaves de produto e tokens._");

        var mdPath = Path.Combine(folder, "report.md");
        await File.WriteAllTextAsync(mdPath, md.ToString(), new UTF8Encoding(false), ct);

        // ---- compacta ----
        var zipPath = Path.Combine(ReportsDirectory, $"renova-report-{stamp}.zip");
        try
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, false);
            onLine?.Invoke($"Pacote compactado: {Path.GetFileName(zipPath)}");
        }
        catch (Exception ex)
        {
            onLine?.Invoke($"Não foi possível compactar ({ex.Message}). A pasta continua disponível.");
            zipPath = "";
        }

        _log.Information("Report gerado: {Folder} com {Count} imagem(ns)", folder, copied.Count);
        return new ReportResult(true, folder, zipPath, "");
    }

    // ================= ANEXOS =================

    /// <summary>Abre o seletor de arquivos filtrado para imagens.</summary>
    public static List<string> PickImages()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecione as imagens do problema",
            Multiselect = true,
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Todos os arquivos|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? dlg.FileNames.ToList() : new List<string>();
    }

    /// <summary>
    /// Salva a imagem da área de transferência num arquivo temporário.
    /// É o caminho mais rápido: Print Screen, Win+Shift+S e depois colar aqui.
    /// </summary>
    public string? SaveClipboardImage()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var img = System.Windows.Clipboard.GetImage();
            if (img is null) return null;

            var temp = Path.Combine(ReportsDirectory, "tmp");
            Directory.CreateDirectory(temp);
            var path = Path.Combine(temp, $"colada-{DateTime.Now:HHmmssfff}.png");

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using var fs = new FileStream(path, FileMode.Create);
            encoder.Save(fs);
            return path;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao colar imagem da area de transferencia");
            return null;
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", path);
            else if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { _log.Warning(ex, "Falha ao abrir {Path}", path); }
    }

    // ================= PUBLICACAO NO GITHUB =================

    /// <summary>Pagina de novo issue do repositorio publico do Renova.</summary>
    public const string IssuesUrl = "https://github.com/fernandogssilva/Renova/issues/new";

    /// <summary>
    /// Abre no navegador um issue do GitHub ja preenchido com o report.
    ///
    /// NUNCA publica sozinho e nao usa token: o usuario revisa, anexa o .zip
    /// e clica em "Submit". O issue e PUBLICO - o texto ja vem sanitizado,
    /// mas imagens nao passam por mascaramento e precisam ser revisadas.
    /// O GitHub nao aceita anexo por URL, entao o Explorer abre com o .zip
    /// selecionado e o caminho vai para a area de transferencia.
    /// </summary>
    public IssueResult OpenGitHubIssue(ReportInput input, ReportResult report, Action<string>? onLine)
    {
        var titulo = $"[{input.Severity}] {input.Module} - {Sanitize(input.Title)}";

        var corpo = new StringBuilder();
        corpo.AppendLine($"**Modulo:** {input.Module}  ");
        corpo.AppendLine($"**Severidade:** {input.Severity}  ");
        corpo.AppendLine($"**Reproduz:** {(input.Reproducible ? "sempre" : "aconteceu uma vez")}  ");
        corpo.AppendLine($"**Versao:** {typeof(App).Assembly.GetName().Version}");
        corpo.AppendLine();
        corpo.AppendLine("### O que aconteceu");
        corpo.AppendLine(Sanitize(input.Description.Trim()));
        if (!string.IsNullOrWhiteSpace(input.StepsToReproduce))
        {
            corpo.AppendLine();
            corpo.AppendLine("### Passos para reproduzir");
            corpo.AppendLine(Sanitize(input.StepsToReproduce.Trim()));
        }
        corpo.AppendLine();
        corpo.AppendLine("### Pacote de diagnostico");
        corpo.AppendLine("_Arraste aqui o .zip gerado pelo Renova (imagens, diagnostico e log sanitizados)._");

        var anexo = !string.IsNullOrEmpty(report.ZipPath) && File.Exists(report.ZipPath)
            ? report.ZipPath : "";

        try
        {
            // URL longa demais e recusada pelo GitHub; o pacote completo vai no anexo
            var texto = corpo.ToString();
            if (texto.Length > 3000) texto = texto[..3000] + "\n\n[...] ver o pacote anexado.";

            var url = $"{IssuesUrl}?title={Uri.EscapeDataString(titulo)}&body={Uri.EscapeDataString(texto)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            if (anexo.Length > 0)
            {
                try { System.Windows.Clipboard.SetText(anexo); } catch { }
                OpenFolder(anexo);
            }
            onLine?.Invoke("Issue aberto no navegador. Arraste o .zip selecionado no Explorer para o campo de texto, " +
                           "revise as imagens (o issue e publico) e clique em Submit.");
            _log.Information("Issue de report aberto no GitHub");
            return new IssueResult(true, "");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao abrir o navegador para o issue");
            return new IssueResult(false,
                $"Nao foi possivel abrir o navegador: {ex.Message}. " +
                $"Abra {IssuesUrl} e anexe {anexo}");
        }
    }
}

/// <summary>Resultado da abertura do issue no GitHub.</summary>
public sealed record IssueResult(bool Success, string Error);


// ================= MODELOS =================

public sealed class ReportAttachment : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Path { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public double SizeKB { get; init; }
    public string SizeText => SizeKB >= 1024 ? $"{SizeKB / 1024:N1} MB" : $"{SizeKB:N0} KB";

    /// <summary>Miniatura para a lista. Carregada sob demanda e congelada.</summary>
    public System.Windows.Media.ImageSource? Thumbnail { get; init; }
}

public sealed class ReportInput
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string StepsToReproduce { get; set; } = "";
    public string Module { get; set; } = "Não sei dizer";
    public string Severity { get; set; } = "Média";
    public bool Reproducible { get; set; }
    public List<ReportAttachment> Attachments { get; set; } = new();
}

public sealed record ReportResult(bool Success, string Folder, string ZipPath, string Error);
