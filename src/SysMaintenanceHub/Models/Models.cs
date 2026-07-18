using System;
using System.Collections.Generic;

namespace SysMaintenanceHub.Models;

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
