using Spark108.Core;

namespace Spark108.Server;

internal static class BuiltInPublisher
{
    public static async Task Run(Store store, Storage storage, string path, string? version)
    {
        if (version is null || !PackageRules.ValidVersion(version))
            throw new InvalidOperationException("A valid --client-version is required.");
        var id = PackageRules.AppCenterId;
        if (await store.HasRelease(id, version))
        {
            Console.WriteLine($"App Center {version} already exists. The catalog entry was preserved.");
            return;
        }
        await using var package = File.OpenRead(path);
        if (package.Length is <= 0 or > PackageRules.MaxArchive)
            throw new InvalidDataException("Invalid App Center package size.");

        var key = Guid.NewGuid().ToString("N");
        var row = new AppRow(id, "Spark108 App Center", "", PackageKind.AppCenter, false);
        var info = await storage.Stage(key, row, version, "", null, package, CancellationToken.None);
        try
        {
            await store.AddRelease(info, key, true, null);
        }
        catch (RuleException)
        {
            File.Delete(storage.Package(key));
            if (!await store.HasRelease(id, version))
                throw;
        }
        Console.WriteLine($"App Center {version} is published in the catalog.");
    }
}
