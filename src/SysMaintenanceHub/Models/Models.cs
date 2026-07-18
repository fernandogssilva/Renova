using System;
using System.Collections.Generic;

namespace SysMaintenanceHub.Models;

public sealed record WindowsUpdateItem(string Kb, string Title, string Category, double SizeMB);

public sealed record WingetUpdateItem(string Id, string Name, string CurrentVersion, string AvailableVersion);

public sealed record StartupApp(string Name, string Command, string Location, bool Enabled);

public sealed record DriveDefragInfo(string Drive, double FragmentationPercent, DateTime? LastDefrag);

public sealed record CleanableItem(string Name, double SizeMB, string Path);

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
