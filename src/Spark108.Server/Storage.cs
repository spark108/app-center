using System.IO.Compression;
using System.Security.Cryptography;
using Spark108.Core;

namespace Spark108.Server;

internal sealed class Storage
{
    public string Root { get; }
    private readonly string packageDirectory;

    public Storage(string root)
    {
        Root = Path.GetFullPath(root);
        packageDirectory = Directory.CreateDirectory(Path.Combine(Root, "packages")).FullName;
    }

    public string Package(string key) => Path.Combine(packageDirectory, Guid.ParseExact(key, "N").ToString("N") + ".zip");

    public async Task<ReleaseInfo> Stage(string key, AppRow app, string version, string notes, string? entryPoint,
        Stream upload, CancellationToken cancellation)
    {
        var path = Package(key);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, true);
            await upload.CopyToAsync(file, cancellation);
            await file.FlushAsync(cancellation);
            file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
                PackageRules.CheckArchive(zip, entryPoint, app.Kind == PackageKind.AppCenter ? version : null);
            file.Position = 0;
            var hash = await SHA256.HashDataAsync(file, cancellation);
            return new ReleaseInfo(0, app.Id, version, notes, entryPoint, file.Length, Convert.ToHexStringLower(hash));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
