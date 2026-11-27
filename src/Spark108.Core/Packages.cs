using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Spark108.Core;

public enum PackageKind { Application, Component, AppCenter }

public sealed record UserProfile(long Id, string Subject, string Name, string Email, bool IsAdmin, bool Enabled);
public sealed record CatalogApp(string Id, string Name, string Description, PackageKind Kind, string[] Dependencies, ReleaseInfo? Latest);
public sealed record ReleaseInfo(long Id, string AppId, string Version, string Notes, string? EntryPoint, long Size, string Sha256,
    InstallerSettings? Installer = null);
public sealed record PackageCommand(string Path, string[] Arguments, bool Elevated);
public sealed record InstallerSettings(PackageCommand Install, PackageCommand? Uninstall, string? Launch, string? RegistryKey);
public sealed record PackageFile(string Path, long Size, string? Sha256);
public sealed record FileUpdateRequest(Dictionary<string, string> Files);
public sealed record FileUpdatePlan(PackageFile[] Files, int[] Download);
public sealed record InstallItem(CatalogApp App, ReleaseInfo Release);
public sealed record DesktopExchange(string Code, string Verifier);
public sealed record DesktopSession(string Token, DateTime ExpiresAt, UserProfile User);

public static partial class PackageRules
{
    public const string AppCenterId = "spark108-app-center";
    public const string AppCenterExe = "Spark108.Desktop.exe";
    public const string UpdaterExe = "Spark108.Updater.exe";
    public const string InstallMarker = ".spark108-package.json";
    public const string ReleaseMarker = "spark-release.txt";
    public const long MaxArchive = 512L * 1024 * 1024;
    public const long MaxUnpacked = 2L * 1024 * 1024 * 1024;
    public const int MaxFiles = 20000;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\z")]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\\$|CONOUT\\$)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    public static bool ValidId(string id) => IdPattern().IsMatch(id) && !DevicePattern().IsMatch(id);
    public static bool ValidVersion(string version) => VersionPattern().IsMatch(version);

    public static bool ValidPath(string path) => path.Length is > 0 and <= 240 &&
        path.Split('/').All(part => part.Length > 0 && !part.EndsWith('.') && !part.EndsWith(' ') &&
            part.IndexOfAny(['<', '>', ':', '"', '\\', '|', '?', '*']) < 0 &&
            !part.Any(char.IsControl) && !DevicePattern().IsMatch(part.Split('.')[0].TrimEnd()));

    public static bool ValidPackagePath(string path) => ValidPath(path) &&
        !path.Split('/')[0].Equals(InstallMarker, StringComparison.OrdinalIgnoreCase) &&
        !path.Split('/')[0].Equals(InstallMarker + ".tmp", StringComparison.OrdinalIgnoreCase);

    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static bool ExternalWindowsPath(string path) => path.StartsWith('%') || path.StartsWith("{installDir}", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("{packageDir}", StringComparison.OrdinalIgnoreCase) ||
        (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');

    public static bool InstallerExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".msi" or ".ps1";

    public static bool ValidRegistryKey(string key) => key.Length is > 0 and <= 160 &&
        key is not ("." or "..") && !key.Any(char.IsControl) && key.IndexOfAny(['/', '\\']) == -1;

    public static void CheckArchive(ZipArchive archive, string? entryPoint, string? selfVersion = null,
        IEnumerable<string>? requiredPaths = null)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        if (archive.Entries.Count is 0 or > MaxFiles)
            throw new InvalidDataException("Недопустимое количество файлов в ZIP.");

        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!ValidPackagePath(path) || !entries.Add(path) ||
                (entry.ExternalAttributes & 0x400) != 0 || unixType is not (0 or 0x4000 or 0x8000))
                throw new InvalidDataException("ZIP содержит недопустимый или повторяющийся путь.");
            if (directory)
                directories.Add(path);
            else
                files.Add(path);

            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                directories.Add(path[..slash]);
                if (slash == 0) break;
            }

            if (entry.Length < 0 || entry.Length > MaxUnpacked - size || (directory && entry.Length != 0))
                throw new InvalidDataException("Содержимое ZIP превышает допустимый размер или повреждено.");
            size += entry.Length;
        }

        if (files.Count == 0 || files.Overlaps(directories))
            throw new InvalidDataException("ZIP пуст или содержит конфликтующие пути.");
        if (entryPoint is not null && (!ValidPath(entryPoint) || !files.Contains(entryPoint)))
            throw new InvalidDataException("Указанный исполняемый файл отсутствует в ZIP.");
        if (requiredPaths is not null && requiredPaths.Any(path => !ValidPackagePath(path) || !files.Contains(path)))
            throw new InvalidDataException("Указанный установщик или файл удаления отсутствует в ZIP.");
        if (selfVersion is null)
            return;

        var marker = archive.GetEntry(ReleaseMarker);
        if (archive.GetEntry(AppCenterExe) is null || archive.GetEntry(UpdaterExe) is null ||
            marker is null || marker.Length is < 5 or > 64)
            throw new InvalidDataException("ZIP Центра приложений должен быть собран скриптом сборки клиента.");
        using var reader = new StreamReader(marker.Open());
        if (reader.ReadToEnd().Trim() != selfVersion)
            throw new InvalidDataException("Версия Центра приложений внутри ZIP отличается от указанной.");
    }
}
