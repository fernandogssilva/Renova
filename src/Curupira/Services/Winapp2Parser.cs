using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Curupira.Services;

/// <summary>
/// Uma entrada do winapp2.ini — corresponde a um alvo de limpeza de um
/// aplicativo específico (ex.: "Google Chrome - Cache").
/// </summary>
public sealed class Winapp2Entry
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Aplicativos";
    public bool DefaultChecked { get; init; }
    public string? Warning { get; init; }

    /// <summary>Chaves de registro que provam que o app está instalado (OR entre elas).</summary>
    public List<string> DetectRegistry { get; } = new();

    /// <summary>Caminhos que provam que o app está instalado (OR entre eles).</summary>
    public List<string> DetectFiles { get; } = new();

    /// <summary>Alvos de arquivo: caminho + filtro + flags.</summary>
    public List<FileTarget> FileTargets { get; } = new();

    /// <summary>Exclusões declaradas pela entrada.</summary>
    public List<string> Excludes { get; } = new();

    /// <summary>
    /// Chaves de registro que a entrada apagaria. O Curupira PARSEIA mas NÃO
    /// EXECUTA — limpeza de registro não traz ganho mensurável e é a principal
    /// fonte de sistemas quebrados por "otimizadores". Mesma posição do
    /// FluentCleaner. Mantidas aqui só para exibir transparência ao usuário.
    /// </summary>
    public List<string> RegistryTargets { get; } = new();

    public bool HasDetection => DetectRegistry.Count > 0 || DetectFiles.Count > 0;
}

public sealed record FileTarget(string Path, string Filter, bool Recurse, bool RemoveSelf);

/// <summary>
/// Parser do formato winapp2.ini — a base comunitária de regras de limpeza
/// mantida há mais de 15 anos (github.com/MoscaDotTo/Winapp2).
/// </summary>
public static class Winapp2Parser
{
    /// <summary>Categorias numéricas do winapp2 (LangSecRef).</summary>
    private static readonly Dictionary<string, string> LangSecRefs = new()
    {
        ["3021"] = "Aplicativos",
        ["3022"] = "Internet",
        ["3023"] = "Multimídia",
        ["3024"] = "Utilitários",
        ["3025"] = "Windows",
        ["3026"] = "Firefox / Mozilla",
        ["3027"] = "Opera",
        ["3028"] = "Safari",
        ["3029"] = "Jogos",
        ["3030"] = "Google Chrome",
        ["3031"] = "Thunderbird",
        ["3032"] = "Windows Store",
    };

    public static List<Winapp2Entry> Parse(string iniContent)
    {
        var entries = new List<Winapp2Entry>();

        string name = "", category = "Aplicativos", warning = "";
        bool defaultChecked = false;
        var detectReg = new List<string>();
        var detectFile = new List<string>();
        var fileKeys = new List<string>();
        var regKeys = new List<string>();
        var excludes = new List<string>();

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var e = new Winapp2Entry
            {
                Name = name,
                Category = category,
                DefaultChecked = defaultChecked,
                Warning = string.IsNullOrWhiteSpace(warning) ? null : warning
            };
            e.DetectRegistry.AddRange(detectReg);
            e.DetectFiles.AddRange(detectFile);
            e.RegistryTargets.AddRange(regKeys);
            e.Excludes.AddRange(excludes);
            foreach (var fk in fileKeys)
            {
                var t = ParseFileKey(fk);
                if (t is not null) e.FileTargets.Add(t);
            }
            if (e.FileTargets.Count > 0) entries.Add(e);
        }

        foreach (var raw in iniContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                name = line[1..^1].Trim();
                category = "Aplicativos"; warning = ""; defaultChecked = false;
                detectReg.Clear(); detectFile.Clear();
                fileKeys.Clear(); regKeys.Clear(); excludes.Clear();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length == 0) continue;

            // Chaves numeradas: FileKey1, Detect2, ExcludeKey3...
            var bare = Regex.Replace(key, @"\d+$", "");

            switch (bare.ToLowerInvariant())
            {
                case "langsecref":
                    if (LangSecRefs.TryGetValue(val, out var c)) category = c;
                    break;
                case "section":
                    category = val;
                    break;
                case "default":
                    defaultChecked = val.Equals("True", StringComparison.OrdinalIgnoreCase);
                    break;
                case "warning":
                    warning = val;
                    break;
                case "detect":
                    detectReg.Add(val);
                    break;
                case "detectfile":
                    detectFile.Add(val);
                    break;
                case "filekey":
                    fileKeys.Add(val);
                    break;
                case "regkey":
                    regKeys.Add(val);
                    break;
                case "excludekey":
                    excludes.Add(val);
                    break;
            }
        }
        Flush();
        return entries;
    }

    /// <summary>
    /// FileKey tem o formato: caminho|filtro|FLAGS
    /// Ex.: %LocalAppData%\Foo\Cache|*.*|RECURSE
    /// </summary>
    private static FileTarget? ParseFileKey(string value)
    {
        var parts = value.Split('|');
        if (parts.Length == 0) return null;

        var path = parts[0].Trim();
        if (string.IsNullOrWhiteSpace(path)) return null;

        var filter = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "*.*";

        bool recurse = false, removeSelf = false;
        for (int i = 2; i < parts.Length; i++)
        {
            var flag = parts[i].Trim().ToUpperInvariant();
            if (flag == "RECURSE") recurse = true;
            else if (flag == "REMOVESELF") { recurse = true; removeSelf = true; }
        }
        return new FileTarget(path, filter, recurse, removeSelf);
    }

    // ================= EXPANSÃO DE VARIÁVEIS =================

    private static readonly Lazy<Dictionary<string, string>> Vars = new(BuildVars);

    private static Dictionary<string, string> BuildVars()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%AppData%"]             = F(Environment.SpecialFolder.ApplicationData),
            ["%LocalAppData%"]        = F(Environment.SpecialFolder.LocalApplicationData),
            ["%LocalLowAppData%"]     = Path.Combine(profile, "AppData", "LocalLow"),
            ["%CommonAppData%"]       = F(Environment.SpecialFolder.CommonApplicationData),
            ["%ProgramData%"]         = F(Environment.SpecialFolder.CommonApplicationData),
            ["%ProgramFiles%"]        = F(Environment.SpecialFolder.ProgramFiles),
            ["%ProgramFiles(x86)%"]   = F(Environment.SpecialFolder.ProgramFilesX86),
            ["%CommonProgramFiles%"]  = F(Environment.SpecialFolder.CommonProgramFiles),
            ["%UserProfile%"]         = profile,
            ["%Documents%"]           = F(Environment.SpecialFolder.MyDocuments),
            ["%Music%"]               = F(Environment.SpecialFolder.MyMusic),
            ["%Pictures%"]            = F(Environment.SpecialFolder.MyPictures),
            ["%Video%"]               = F(Environment.SpecialFolder.MyVideos),
            ["%Desktop%"]             = F(Environment.SpecialFolder.DesktopDirectory),
            ["%Favorites%"]           = F(Environment.SpecialFolder.Favorites),
            ["%StartMenu%"]           = F(Environment.SpecialFolder.StartMenu),
            ["%Startup%"]             = F(Environment.SpecialFolder.Startup),
            ["%WinDir%"]              = Environment.GetEnvironmentVariable("WinDir") ?? @"C:\Windows",
            ["%SystemRoot%"]          = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            ["%SystemDrive%"]         = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
            ["%Temp%"]                = Path.GetTempPath().TrimEnd('\\'),
            ["%tmp%"]                 = Path.GetTempPath().TrimEnd('\\'),
            ["%Public%"]              = Environment.GetEnvironmentVariable("Public") ?? @"C:\Users\Public",
        };
    }

    public static string ExpandPath(string path)
    {
        var result = path;
        foreach (var (k, v) in Vars.Value)
            result = result.Replace(k, v, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(result);
    }

    // ================= DETECÇÃO =================

    /// <summary>A entrada se aplica a esta máquina?</summary>
    public static bool IsPresent(Winapp2Entry entry)
    {
        // Sem detecção declarada: assume presente (entradas genéricas do Windows)
        if (!entry.HasDetection) return true;

        foreach (var reg in entry.DetectRegistry)
            if (RegistryKeyExists(reg)) return true;

        foreach (var file in entry.DetectFiles)
        {
            var p = ExpandPath(file);
            if (p.Contains('*'))
            {
                if (WildcardPathExists(p)) return true;
            }
            else if (File.Exists(p) || Directory.Exists(p)) return true;
        }
        return false;
    }

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            var parts = path.Split('\\', 2);
            if (parts.Length < 2) return false;

            RegistryKey? root = parts[0].ToUpperInvariant() switch
            {
                "HKCU" or "HKEY_CURRENT_USER"  => Registry.CurrentUser,
                "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                "HKCR" or "HKEY_CLASSES_ROOT"  => Registry.ClassesRoot,
                "HKU"  or "HKEY_USERS"         => Registry.Users,
                _ => null
            };
            if (root is null) return false;

            using var k = root.OpenSubKey(parts[1], writable: false);
            return k is not null;
        }
        catch { return false; }
    }

    private static bool WildcardPathExists(string pattern)
    {
        try
        {
            var dir = Path.GetDirectoryName(pattern);
            var leaf = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(dir) || dir.Contains('*')) return false;
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateFileSystemEntries(dir, leaf).Any();
        }
        catch { return false; }
    }
}
