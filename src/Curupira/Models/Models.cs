using System;
using System.Collections.Generic;

namespace Curupira.Models;

public sealed record WindowsUpdateItem(string Kb, string Title, string Category, double SizeMB);

public sealed record WingetUpdateItem(string Id, string Name, string CurrentVersion, string AvailableVersion);

public sealed record StartupApp(string Name, string Command, string Location, bool Enabled);

public sealed record DriveDefragInfo(string Drive, double FragmentationPercent, DateTime? LastDefrag);

public sealed record CleanableItem(string Name, double SizeMB, string Path);

public sealed record OsBuildInfo(
    string ProductName,
    string EditionId,
    string DisplayVersion,
    int Build,
    int UBR,
    string FullVersion,
    bool IsWindows11);

public sealed record LatestKbFromCatalog(
    string Kb,
    int BuildMajor,
    int BuildMinor,
    string BuildString,
    DateTime PublishedAt,
    string CatalogUrl);

public sealed record VulnerabilityItem(
    string Cve,
    string Title,
    string Severity,
    double CvssScore,
    string Kb,
    string ProductName,
    string Description,
    string MsrcUrl,
    string CatalogUrl,
    DateTime ReleaseDate);

public sealed record KbPendingItem(
    string Kb,
    string BuildString,
    DateTime PublishedAt,
    string CatalogUrl,
    bool IsCritical);

// --- Scan report (varredura ao vivo, estilo CCleaner) ---
public enum ScanState { Pending, Running, Done, Failed }

public sealed class ScanModuleReport : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private ScanState _state = ScanState.Pending;
    public ScanState State { get => _state; set { SetProperty(ref _state, value); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(StateColor)); } }

    public string StateText => State switch
    {
        ScanState.Pending => "Aguardando",
        ScanState.Running => "Analisando…",
        ScanState.Done    => "Concluído",
        ScanState.Failed  => "Falhou",
        _ => ""
    };

    public string StateColor => State switch
    {
        ScanState.Pending => "#64748B",
        ScanState.Running => "#38BDF8",
        ScanState.Done    => "#22C55E",
        ScanState.Failed  => "#EF4444",
        _ => "#64748B"
    };

    private string _currentActivity = "";
    public string CurrentActivity { get => _currentActivity; set => SetProperty(ref _currentActivity, value); }

    private int _foundCount;
    public int FoundCount { get => _foundCount; set => SetProperty(ref _foundCount, value); }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private string _actionHint = "";
    public string ActionHint { get => _actionHint; set => SetProperty(ref _actionHint, value); }

    private string _lastFoundItem = "";
    public string LastFoundItem { get => _lastFoundItem; set => SetProperty(ref _lastFoundItem, value); }
}

public sealed class MaintenanceSnapshot
{
    public List<WindowsUpdateItem> WindowsUpdates { get; set; } = new();
    public List<WingetUpdateItem> WingetUpdates { get; set; } = new();
    public List<StartupApp> StartupApps { get; set; } = new();
    public List<DriveDefragInfo> Drives { get; set; } = new();
    public List<CleanableItem> Cleanables { get; set; } = new();
    public DateTime? LastCleanup { get; set; }
    public double TotalCleanableMB => Sum(Cleanables);
    private static double Sum(List<CleanableItem> list)
    {
        double s = 0; foreach (var c in list) s += c.SizeMB; return s;
    }
}

// ============================================================
// Linha selecionavel da tabela de vulnerabilidades.
// Permite corrigir varias de uma vez (multi-select + acao em lote).
// ============================================================
public sealed class VulnRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public VulnerabilityItem Item { get; }

    public VulnRow(VulnerabilityItem item) => Item = item;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private FixState _state = FixState.Pending;
    public FixState State
    {
        get => _state;
        set { SetProperty(ref _state, value); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(StateColor)); }
    }

    public string StateText => State switch
    {
        FixState.Pending   => "",
        FixState.Queued    => "Na fila",
        FixState.Applying  => "Aplicando...",
        FixState.Fixed     => "Corrigido",
        FixState.Failed    => "Falhou",
        FixState.Skipped   => "Manual",
        _ => ""
    };

    public string StateColor => State switch
    {
        FixState.Queued   => "#FBBF24",
        FixState.Applying => "#34D399",
        FixState.Fixed    => "#4ADE80",
        FixState.Failed   => "#F87171",
        FixState.Skipped  => "#8FB5A2",
        _ => "#5F8271"
    };

    // Atalhos de binding
    public string Cve         => Item.Cve;
    public string Title       => Item.Title;
    public string Severity    => Item.Severity;
    public double CvssScore   => Item.CvssScore;
    public string Kb          => Item.Kb;
    public string MsrcUrl     => Item.MsrcUrl;
    public string CatalogUrl  => Item.CatalogUrl;
    public DateTime ReleaseDate => Item.ReleaseDate;

    /// <summary>KBs reais (KBxxxxxxx) podem ser aplicadas automaticamente.
    /// Findings locais trazem cmdlet/registry no campo Kb e sao manuais.</summary>
    public bool IsAutoFixable =>
        System.Text.RegularExpressions.Regex.IsMatch(Item.Kb ?? "", @"^KB\d{5,8}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public bool IsLocalFinding => Item.Cve.StartsWith("SEC-", StringComparison.OrdinalIgnoreCase);
}

public enum FixState { Pending, Queued, Applying, Fixed, Failed, Skipped }

// ============================================================
// Card do painel principal: alem do numero, explica o que a
// area significa e o que muda no computador ao resolver.
// ============================================================
public sealed class DashboardItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Area { get; init; } = "";
    public string Icon { get; init; } = "";

    /// <summary>Indice da secao no menu lateral (para o botao de acao).</summary>
    public int TargetSection { get; init; }

    public string ActionLabel { get; init; } = "";

    /// <summary>O que essa area representa tecnicamente.</summary>
    public string WhatItIs { get; init; } = "";

    /// <summary>O que melhora na maquina depois de resolver.</summary>
    public string WhatItImproves { get; init; } = "";

    private string _value = "0";
    public string Value { get => _value; set => SetProperty(ref _value, value); }

    private string _valueCaption = "";
    public string ValueCaption { get => _valueCaption; set => SetProperty(ref _valueCaption, value); }

    private HealthLevel _level = HealthLevel.Ok;
    public HealthLevel Level
    {
        get => _level;
        set { SetProperty(ref _level, value); OnPropertyChanged(nameof(LevelColor)); OnPropertyChanged(nameof(LevelText)); }
    }

    public string LevelColor => Level switch
    {
        HealthLevel.Critical  => "#EF4444",
        HealthLevel.Attention => "#FBBF24",
        HealthLevel.Ok        => "#4ADE80",
        _ => "#5F8271"
    };

    public string LevelText => Level switch
    {
        HealthLevel.Critical  => "Requer atencao imediata",
        HealthLevel.Attention => "Pode melhorar",
        HealthLevel.Ok        => "Tudo certo",
        _ => ""
    };

    private bool _needsAction;
    public bool NeedsAction { get => _needsAction; set => SetProperty(ref _needsAction, value); }
}

public enum HealthLevel { Ok, Attention, Critical }
