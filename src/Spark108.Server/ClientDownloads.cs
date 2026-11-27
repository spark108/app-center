using System.Globalization;
using System.Net;
using Spark108.Core;

namespace Spark108.Server;

internal static class ClientDownloads
{
    private const string ArchiveRoute = "/download/app-center/package";
    private const string InstallerName = "Spark108-AppCenter-Setup.exe";
    private const string InstallerRoute = "/download/" + InstallerName;
    private static readonly string InstallerPath = Path.Combine(AppContext.BaseDirectory, InstallerName);

    public static void Map(WebApplication app)
    {
        app.MapGet("/", async (Store db, Storage files) =>
            Page(File.Exists(InstallerPath) ? (await Find(db, files))?.Info.Version : null));
        app.MapMethods(ArchiveRoute, [HttpMethods.Get, HttpMethods.Head], async (HttpContext context, Store db, Storage files) =>
        {
            if (await Find(db, files) is not { } package)
                return Results.NotFound();
            var info = package.Info;
            context.Response.Headers["X-AppCenter-Version"] = info.Version;
            context.Response.Headers["X-AppCenter-Size"] = info.Size.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-AppCenter-Sha256"] = info.Sha256;
            return Results.File(package.Path, "application/zip", $"Spark108-AppCenter-{info.Version}-win-x64.zip",
                enableRangeProcessing: true);
        });
        app.MapMethods(InstallerRoute, [HttpMethods.Get, HttpMethods.Head], async (Store db, Storage files) =>
            File.Exists(InstallerPath) && await Find(db, files) is not null
                ? Results.File(InstallerPath, "application/octet-stream", InstallerName, enableRangeProcessing: true)
                : Results.NotFound());
    }

    private static async Task<(ReleaseInfo Info, string Path)?> Find(Store db, Storage files)
    {
        var release = await db.LatestPublishedRelease(PackageRules.AppCenterId, publicOnly: true);
        if (release is null)
            return null;
        var path = files.Package(release.StorageKey);
        return File.Exists(path) ? (release.Info, path) : null;
    }

    private static IResult Page(string? version)
    {
        var content = version is null
            ? "<p>Загрузка пока недоступна.</p>"
            : $"""
                <p>Windows x64 · версия {WebUtility.HtmlEncode(version)}</p>
                <p><a class="download" href="{InstallerRoute}">Скачать Центр приложений</a></p>
                """;
        return Results.Content($"""
            <!doctype html>
            <html lang="ru">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Центр приложений</title>
                <link rel="stylesheet" href="/download.css">
            </head>
            <body><main><h1>Центр приложений</h1>{content}</main></body>
            </html>
            """, "text/html; charset=utf-8");
    }
}
