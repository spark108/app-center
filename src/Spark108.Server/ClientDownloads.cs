using System.Net;
using Spark108.Core;

namespace Spark108.Server;

internal static class ClientDownloads
{
    private const string ArchiveRoute = "/download/app-center";
    private const string InstallerRoute = "/download/Install-AppCenter.ps1";
    private const string InstallerName = "Install-AppCenter.ps1";
    private static readonly string InstallerPath = Path.Combine(AppContext.BaseDirectory, InstallerName);

    public static void Map(WebApplication app)
    {
        app.MapGet("/", async (Store db, Storage files) => Page((await Find(db, files))?.Version));
        app.MapMethods(ArchiveRoute, [HttpMethods.Get, HttpMethods.Head], async (Store db, Storage files) =>
            await Find(db, files) is { } package
                ? Results.File(package.Path, "application/zip", ArchiveName(package.Version), enableRangeProcessing: true)
                : Results.NotFound());
        app.MapMethods(InstallerRoute, [HttpMethods.Get, HttpMethods.Head], async (Store db, Storage files) =>
            File.Exists(InstallerPath) && await Find(db, files) is not null
                ? Results.File(InstallerPath, "application/octet-stream", InstallerName)
                : Results.NotFound());
    }

    private static async Task<(string Version, string Path)?> Find(Store db, Storage files)
    {
        var release = await db.LatestPublishedRelease(PackageRules.AppCenterId, publicOnly: true);
        if (release is null)
            return null;
        var path = files.Package(release.StorageKey);
        return File.Exists(path) ? (release.Info.Version, path) : null;
    }

    private static string ArchiveName(string version) => $"Spark108-AppCenter-{version}-win-x64.zip";

    private static IResult Page(string? version)
    {
        var content = version is null
            ? "<p>Загрузка пока недоступна.</p>"
            : $"""
                <p>Windows x64 · версия {WebUtility.HtmlEncode(version)}</p>
                <p><a class="download" href="{ArchiveRoute}">Скачать App Center</a></p>
                <p>Для первой установки положите <a href="{InstallerRoute}">скрипт установки</a> рядом с ZIP
                и выполните в PowerShell из этой папки.</p>
                <pre><code>.\{InstallerName} -Package .\{WebUtility.HtmlEncode(ArchiveName(version))}</code></pre>
                """;
        return Results.Content($"""
            <!doctype html>
            <html lang="ru">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Spark108 App Center</title>
                <link rel="stylesheet" href="/download.css">
            </head>
            <body><main><h1>Spark108 App Center</h1>{content}</main></body>
            </html>
            """, "text/html; charset=utf-8");
    }
}
