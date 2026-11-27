using System.IO;
using System.Security;
using Microsoft.Win32;
using Spark108.Core;

namespace Spark108.Desktop;

internal static class PackageRegistration
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Prefix = "Spark108.AppCenter.";
    private static bool Enabled => Environment.GetEnvironmentVariable("SPARK108_APP_CENTER_HOME") is null;
    internal sealed record NativeRegistration(string? Command, bool WindowsInstaller);

    public static NativeRegistration? FindNative(string key)
    {
        if (!PackageRules.ValidRegistryKey(key) || key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var parent = RegistryKey.OpenBaseKey(hive, view);
            using var entry = parent.OpenSubKey($@"{UninstallPath}\{key}");
            if (entry is not null)
                return new NativeRegistration(entry.GetValue("UninstallString") as string, entry.GetValue("WindowsInstaller") is 1);
        }
        return null;
    }

    public static void Sync(InstalledPackage package)
    {
        if (!Enabled || package.Kind == PackageKind.AppCenter)
            return;
        if (package.UninstallKey is not null)
        {
            Remove(package.Id);
            return;
        }
        var launcher = Path.Combine(LocalPackages.Client, PackageRules.AppCenterExe);
        if (!File.Exists(launcher))
            return;
        try
        {
            using var entry = Registry.CurrentUser.CreateSubKey(OwnedKey(package.Id));
            entry.SetValue("DisplayName", package.Name);
            entry.SetValue("DisplayVersion", package.Version);
            entry.SetValue("InstallLocation", package.Installer is null ? Path.Combine(LocalPackages.Apps, package.Id) :
                NativeSetup.InstallDirectory(package.Id));
            entry.SetValue("UninstallString", $"\"{launcher}\" --uninstall {package.Id}");
            entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
            entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    public static void Remove(string id)
    {
        if (!Enabled || !PackageRules.ValidId(id))
            return;
        try { Registry.CurrentUser.DeleteSubKeyTree(OwnedKey(id), throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    private static string OwnedKey(string id) => $@"{UninstallPath}\{Prefix}{id}";
}
