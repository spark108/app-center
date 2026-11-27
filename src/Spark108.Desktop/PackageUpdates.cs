using System.IO;
using System.Security.Cryptography;
using Spark108.Core;

namespace Spark108.Desktop;

internal static class PackageUpdates
{
    public static async Task<bool> Prepare(ApiClient api, InstallItem item, string source, string destination, Action<int> progress,
        CancellationToken ct)
    {
        var hashes = await Task.Run(() => Snapshot(source, ct), ct);
        var plan = await api.FilePlan(item.Release.Id, hashes, ct);
        if (plan is null)
            return false;
        Validate(plan, item);
        var changed = plan.Download.ToHashSet();
        for (var i = 0; i < plan.Files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = plan.Files[i];
            var target = Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(file.Sha256 is null ? target : Path.GetDirectoryName(target)!);
            if (file.Sha256 is not null)
            {
                if (changed.Contains(i) || !await CopyExisting(source, file, target, ct))
                    await api.DownloadFile(item.Release.Id, i, file, target, ct);
            }
            progress((i + 1) * 100 / plan.Files.Length);
        }
        if (item.App.Kind == PackageKind.AppCenter &&
            (await File.ReadAllTextAsync(Path.Combine(destination, PackageRules.ReleaseMarker), ct)).Trim() != item.Release.Version)
            throw new InvalidDataException("Номер версии в пакете Центра приложений не совпадает.");
        return true;
    }

    private static Dictionary<string, string> Snapshot(string source, CancellationToken ct)
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Каталог установки является ссылкой.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var path in Directory.EnumerateFiles(source, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (current.Count >= PackageRules.MaxFiles)
                break;
            var name = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (!PackageRules.ValidPackagePath(name))
                continue;
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= PackageRules.MaxUnpacked)
                    current.TryAdd(name, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return current;
    }

    private static async Task<bool> CopyExisting(string root, PackageFile file, string destination, CancellationToken ct)
    {
        var source = root;
        try
        {
            foreach (var part in file.Path.Split('/'))
            {
                source = Path.Combine(source, part);
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            await using var input = File.OpenRead(source);
            if (input.Length != file.Size)
                return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[128 * 1024];
            long remaining = file.Size;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                remaining -= count;
                if (remaining < 0) break;
                hash.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (remaining == 0 && Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.Delete(destination);
        return false;
    }

    private static void Validate(FileUpdatePlan plan, InstallItem item)
    {
        if (plan.Files is not { Length: > 0 and <= PackageRules.MaxFiles } || plan.Download is null ||
            plan.Download.Any(i => (uint)i >= plan.Files.Length))
            throw new InvalidDataException("Сервер вернул недопустимый список файлов.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in plan.Files)
        {
            if (entry is null || entry.Path is null || !PackageRules.ValidPackagePath(entry.Path) || !names.Add(entry.Path) ||
                entry.Size < 0 || entry.Size > PackageRules.MaxUnpacked - total ||
                (entry.Sha256 is null ? entry.Size != 0 : !PackageRules.ValidHash(entry.Sha256)))
                throw new InvalidDataException("Манифест обновления содержит недопустимый файл.");
            total += entry.Size;
            (entry.Sha256 is null ? directories : files).Add(entry.Path);
            for (var slash = entry.Path.IndexOf('/'); slash >= 0; slash = entry.Path.IndexOf('/', slash + 1))
                directories.Add(entry.Path[..slash]);
        }
        if (files.Count == 0 || files.Overlaps(directories) ||
            (item.Release.EntryPoint is { } entryPoint && !files.Contains(entryPoint)) ||
            (item.Release.Installer is { } setup && !files.Contains(setup.Install.Path)) ||
            (item.App.Kind == PackageKind.AppCenter &&
                (!files.Contains(PackageRules.AppCenterExe) || !files.Contains(PackageRules.UpdaterExe) ||
                 !plan.Files.Any(f => f.Path == PackageRules.ReleaseMarker && f.Size is >= 5 and <= 64 && f.Sha256 is not null))))
            throw new InvalidDataException("В манифесте отсутствуют нужные файлы или есть конфликт путей.");
    }
}
