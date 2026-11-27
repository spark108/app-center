using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Spark108.Core;

public sealed record InstalledPackage(string Id, string Name, string Description, PackageKind Kind,
    string Version, string? EntryPoint, string[] Dependencies);

public static class LocalPackages
{
    public static string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Environment.GetEnvironmentVariable("SPARK108_APP_CENTER_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spark108", "AppCenter")));
    public static string Apps => Path.Combine(Root, "apps");
    public static string Client => Path.Combine(Root, "client");
    public static string ClientBackup => Path.Combine(Root, "client.previous");
    public static string Staging => Path.Combine(Root, "staging");
    public static string UpdateFailure => Path.Combine(Root, "update-error.txt");
    private static readonly string MutexScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..16];
    public static string InstanceMutex => @"Local\Spark108.AppCenter.Desktop." + MutexScope;
    public static string UpdateMutex => @"Local\Spark108.AppCenter.Update." + MutexScope;
    public static string ReadyEvent(string id) => @"Local\Spark108.AppCenter.Ready." + id;

    public static InstalledPackage? Read(string directory)
    {
        var path = Path.Combine(directory, PackageRules.InstallMarker);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<InstalledPackage>(File.ReadAllText(path));
    }

    public static void Write(string directory, InstalledPackage record) =>
        File.WriteAllText(Path.Combine(directory, PackageRules.InstallMarker), JsonSerializer.Serialize(record));
}
