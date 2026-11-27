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
            Console.WriteLine($"Версия Центра приложений {version} уже есть в каталоге.");
            return;
        }
        await using var package = File.OpenRead(path);
        if (package.Length is <= 0 or > PackageRules.MaxArchive)
            throw new InvalidDataException("Некорректный размер пакета Центра приложений.");

        var key = Guid.NewGuid().ToString("N");
        var row = new AppRow(id, "Центр приложений", "", PackageKind.AppCenter, false);
        var info = await storage.Stage(key, row, version, "", null, package, CancellationToken.None);
        try
        {
            await store.AddRelease(info, key, true, null);
        }
        catch (RuleException)
        {
            storage.Discard(key);
            if (!await store.HasRelease(id, version))
                throw;
        }
        Console.WriteLine($"Версия Центра приложений {version} опубликована в каталоге.");
    }
}
