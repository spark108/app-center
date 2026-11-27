using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Spark108.Core;

namespace Spark108.Server;

internal static class FileUpdates
{
    public const string ProtocolHeader = "X-AppCenter-Install-Protocol";
    public static bool SupportsInstallers(HttpContext context) => context.Request.Headers[ProtocolHeader] == "2";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/releases/{id:long}/files/plan", Plan);
        api.MapGet("/releases/{id:long}/files/{index:int}", Download);
    }

    private static async Task<IResult> Plan(long id, HttpContext c, Store db, Storage files)
    {
        var package = await Find(id, c, db, files);
        if (package is null)
            return Results.NotFound();
        if (!c.Request.HasJsonContentType())
            return Results.BadRequest();
        if (c.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 32 * 1024 * 1024;

        FileUpdateRequest? request;
        try { request = await c.Request.ReadFromJsonAsync<FileUpdateRequest>(c.RequestAborted); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request?.Files is not { Count: <= PackageRules.MaxFiles } current)
            return Results.BadRequest();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in current)
            if (!PackageRules.ValidPackagePath(path) || !PackageRules.ValidHash(hash) || !hashes.TryAdd(path, hash))
                return Results.BadRequest();

        var index = await files.Index(package, c.RequestAborted);
        var download = new List<int>();
        for (var i = 0; i < index.Length; i++)
            if (index[i].Sha256 is { } expected &&
                (!hashes.TryGetValue(index[i].Path, out var actual) || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase)))
                download.Add(i);
        return Results.Ok(new FileUpdatePlan(index, download.ToArray()));
    }

    private static async Task<IResult> Download(long id, int index, HttpContext c, Store db, Storage files)
    {
        var package = await Find(id, c, db, files);
        if (package is null)
            return Results.NotFound();
        var manifest = await files.Index(package, c.RequestAborted);
        if ((uint)index >= manifest.Length || manifest[index].Sha256 is null)
            return Results.NotFound();
        var file = manifest[index];
        c.Response.ContentLength = file.Size;
        return Results.Stream(output => files.CopyFile(package, file, output, c.RequestAborted), "application/octet-stream");
    }

    private static async Task<ReleaseRow?> Find(long id, HttpContext c, Store db, Storage files)
    {
        var user = (UserProfile)c.Items[typeof(UserProfile)]!;
        var release = await db.Download(user, id, SupportsInstallers(c));
        return release is not null && File.Exists(files.Package(release.StorageKey)) ? release : null;
    }
}
