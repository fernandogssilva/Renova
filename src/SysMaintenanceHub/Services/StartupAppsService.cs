using System;
using System.Collections.Generic;
using Microsoft.Win32;
using Serilog;
using SysMaintenanceHub.Models;

namespace SysMaintenanceHub.Services;

public sealed class StartupAppsService
{
    private readonly ILogger _log = Log.ForContext<StartupAppsService>();

    private static readonly (RegistryHive Hive, string Path, string Label)[] Locations = new[]
    {
        (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",         "HKCU Run"),
        (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",     "HKCU RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",         "HKLM Run"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",     "HKLM RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM Run (Wow64)"),
    };

    public List<StartupApp> List()
    {
        var apps = new List<StartupApp>();
        foreach (var (hive, path, label) in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var sub = baseKey.OpenSubKey(path, writable: false);
                if (sub is null) continue;
                foreach (var name in sub.GetValueNames())
                {
                    var val = sub.GetValue(name)?.ToString() ?? string.Empty;
                    apps.Add(new StartupApp(name, val, label, Enabled: true));
                }

                using var approved = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                if (approved is not null)
                {
                    foreach (var name in approved.GetValueNames())
                    {
                        var raw = approved.GetValue(name) as byte[];
                        bool enabled = raw is null || raw[0] == 0x02 || raw[0] == 0x00;
                        var existing = apps.FindIndex(a => a.Name == name);
                        if (existing >= 0) apps[existing] = apps[existing] with { Enabled = enabled };
                    }
                }
            }
            catch (Exception ex) { _log.Warning(ex, "Falha lendo {Path}", path); }
        }
        _log.Information("{Count} apps de inicialização listados", apps.Count);
        return apps;
    }

    public void SetEnabled(StartupApp app, bool enabled)
    {
        try
        {
            var hive = app.Location.StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            if (approved is null) return;
            var value = new byte[12];
            value[0] = enabled ? (byte)0x02 : (byte)0x03;
            approved.SetValue(app.Name, value, RegistryValueKind.Binary);
        }
        catch (Exception ex) { _log.Error(ex, "Falha ao {State} {App}", enabled ? "habilitar" : "desabilitar", app.Name); }
    }
}
