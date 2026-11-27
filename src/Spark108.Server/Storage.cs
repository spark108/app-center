using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Spark108.Core;

namespace Spark108.Server;

internal sealed class Storage
{
    public string Root { get; }
    private readonly string packageDirectory;
    private readonly SemaphoreSlim indexGate = new(1);

    public Storage(string root)
    {
        Root = Path.GetFullPath(root);
        packageDirectory = Directory.CreateDirectory(Path.Combine(Root, "packages")).FullName;
    }

    public string Package(string key) => Path.Combine(packageDirectory, Guid.ParseExact(key, "N").ToString("N") + ".zip");
    private string IndexPath(string key) => Package(key) + ".files.json";

    public void Discard(string key)
    {
        File.Delete(IndexPath(key));
        File.Delete(Package(key));
    }

    public async Task<ReleaseInfo> Stage(string key, AppRow app, string version, string notes, string? entryPoint,
        Stream upload, CancellationToken cancellation, InstallerSettings? installer = null)
    {
        var path = Package(key);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, true);
            await upload.CopyToAsync(file, cancellation);
            await file.FlushAsync(cancellation);
            file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            {
                string[]? required = installer is null ? null :
                    installer.Uninstall is { Path: var remove } && !PackageRules.ExternalWindowsPath(remove)
                        ? [installer.Install.Path, remove] : [installer.Install.Path];
                PackageRules.CheckArchive(zip, entryPoint, app.Kind == PackageKind.AppCenter ? version : null, required);
                await SaveIndex(key, zip, cancellation);
            }
            file.Position = 0;
            var hash = await SHA256.HashDataAsync(file, cancellation);
            return new ReleaseInfo(0, app.Id, version, notes, entryPoint, file.Length, Convert.ToHexStringLower(hash), installer);
        }
        catch
        {
            Discard(key);
            throw;
        }
    }

    public async Task<PackageFile[]> Index(ReleaseRow release, CancellationToken ct)
    {
        var path = IndexPath(release.StorageKey);
        if (File.Exists(path))
            return await ReadIndex(path, ct);

        await indexGate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
                return await ReadIndex(path, ct);
            using var zip = ZipFile.OpenRead(Package(release.StorageKey));
            PackageRules.CheckArchive(zip, release.Info.EntryPoint,
                release.Info.AppId == PackageRules.AppCenterId ? release.Info.Version : null);
            return await SaveIndex(release.StorageKey, zip, ct);
        }
        finally { indexGate.Release(); }
    }

    private static async Task<PackageFile[]> ReadIndex(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PackageFile[]>(file, cancellationToken: ct)
            ?? throw new InvalidDataException("Индекс файлов пакета повреждён.");
    }

    private async Task<PackageFile[]> SaveIndex(string key, ZipArchive zip, CancellationToken ct)
    {
        var result = new List<PackageFile>(zip.Entries.Count);
        var buffer = new byte[128 * 1024];
        foreach (var entry in zip.Entries.OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/'))
            {
                result.Add(new PackageFile(entry.FullName[..^1], 0, null));
                continue;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = entry.Open();
            var remaining = entry.Length;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0)
            {
                remaining -= count;
                if (remaining < 0)
                    throw new InvalidDataException("Размер файла в ZIP не совпадает с объявленным.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            if (remaining != 0)
                throw new InvalidDataException("ZIP содержит неполный файл.");
            result.Add(new PackageFile(entry.FullName, entry.Length, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        var index = result.ToArray();
        var path = IndexPath(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew))
                await JsonSerializer.SerializeAsync(output, index, cancellationToken: ct);
            File.Move(temporary, path, overwrite: true);
            return index;
        }
        finally { File.Delete(temporary); }
    }

    public async Task CopyFile(ReleaseRow release, PackageFile file, Stream output, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(Package(release.StorageKey));
        var entry = zip.GetEntry(file.Path);
        if (entry is null || entry.Length != file.Size)
            throw new InvalidDataException("Файл пакета не совпадает с индексом.");
        await using var input = entry.Open();
        await input.CopyToAsync(output, ct);
    }
}
