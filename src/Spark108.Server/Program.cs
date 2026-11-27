using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Spark108.Core;
using Spark108.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var origin = new Uri(builder.Configuration["PublicOrigin"] ?? "https://app.godish.ru");
var authority = new Uri(builder.Configuration["Auth:Authority"] ?? "https://auth.godish.ru");
foreach (var uri in new[] { origin, authority })
    if ((uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
        uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
        throw new InvalidOperationException("PublicOrigin and Auth:Authority must be HTTPS origins (HTTP loopback is allowed).");

var storage = new Storage(builder.Configuration["DataPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
if (Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } temp)
    Directory.CreateDirectory(temp);
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<AuthFlow>();
builder.Services.AddDataProtection().SetApplicationName("Spark108.AppCenter")
    .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(storage.Root, "keys")));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "spark108_csrf";
    options.Cookie.Path = "/acp";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = origin.Scheme == "https" ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = PackageRules.MaxArchive;
    o.ValueCountLimit = 10000;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = PackageRules.MaxArchive + 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddHttpClient("Alathea", c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
if (builder.Configuration.GetValue<bool>("show-client-version"))
{
    if (await store.LatestPublishedRelease(PackageRules.AppCenterId) is { } release)
        Console.WriteLine(release.Info.Version);
    else
    {
        Console.Error.WriteLine("App Center has no published version.");
        Environment.ExitCode = 1;
    }
    return;
}
await store.Initialize();
if (builder.Configuration["publish-client"] is { Length: > 0 } builtPackage)
{
    await BuiltInPublisher.Run(store, storage, builtPackage, builder.Configuration["client-version"]);
    return;
}
app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    if (context.Request.Path.StartsWithSegments("/api"))
        await context.Response.WriteAsJsonAsync(new { error = "Ошибка сервера." });
    else
        await AdminView.Message(500, "Ошибка сервера", "Попробуйте повторить действие.").ExecuteAsync(context);
}));
app.Use(async (context, next) =>
{
    // The configured public origin is authoritative behind the local TLS reverse proxy.
    context.Request.Scheme = origin.Scheme;
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    headers.CacheControl = "no-store";
    await next(context);
});
app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = false });
ClientDownloads.Map(app);
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/api/auth/desktop", (HttpContext c, AuthFlow auth) => auth.Start(c, true));
app.MapGet("/api/auth/callback", (HttpContext c, AuthFlow auth) => auth.Callback(c));
app.MapPost("/api/auth/exchange", async (DesktopExchange request, Store db) =>
    !AuthFlow.TokenFormat(request.Code) || !AuthFlow.TokenFormat(request.Verifier)
        ? Results.BadRequest()
        : await db.Exchange(request.Code, request.Verifier) is { } session ? Results.Ok(session) : Results.Unauthorized());

var api = app.MapGroup("/api");
api.AddEndpointFilter(async (invocation, next) =>
{
    var context = invocation.HttpContext;
    if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var authorization) ||
        !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
        await store.Authenticate(authorization.Parameter, "desktop") is not { } user)
        return Results.Unauthorized();
    context.Items[typeof(UserProfile)] = user;
    return await next(invocation);
});
api.MapGet("", () => Results.Ok(new { service = "Spark108 App Center", version = 1 }));
api.MapGet("/me", (HttpContext c) => Results.Ok(DesktopUser(c)));
api.MapPost("/auth/logout", async (HttpContext c, Store db) =>
{
    var token = AuthenticationHeaderValue.Parse(c.Request.Headers.Authorization.ToString()).Parameter!;
    await db.Logout(token, "desktop");
    return Results.NoContent();
});
api.MapGet("/catalog", async (HttpContext c, Store db) =>
{
    var catalog = await db.Catalog(DesktopUser(c));
    return Results.Ok(catalog.Apps.Keys.Select(catalog.Item));
});
api.MapGet("/apps/{id}/releases", async (string id, HttpContext c, Store db) =>
{
    var catalog = await db.Catalog(DesktopUser(c));
    return catalog.Apps.ContainsKey(id) ? Results.Ok(catalog.Releases[id].Select(r => r.Info)) : Results.NotFound();
});
api.MapGet("/apps/{id}/plan", async (string id, long? release, HttpContext c, Store db) =>
{
    var catalog = await db.Catalog(DesktopUser(c));
    if (!catalog.Apps.ContainsKey(id))
        return Results.NotFound();
    var target = release is null ? catalog.Releases[id].FirstOrDefault() :
        catalog.Releases[id].SingleOrDefault(row => row.Info.Id == release);
    if (target is null)
        return Results.NotFound();

    var plan = new List<InstallItem>();
    var seen = new HashSet<string>();
    var active = new HashSet<string>();
    bool Visit(string current)
    {
        if (active.Contains(current))
            return false;
        if (!seen.Add(current))
            return true;
        if (!catalog.Apps.ContainsKey(current))
            return false;
        var published = current == id ? target : catalog.Releases[current].FirstOrDefault();
        if (published is null)
            return false;
        active.Add(current);
        foreach (var dependency in catalog.Dependencies[current])
            if (!Visit(dependency))
                return false;
        active.Remove(current);
        plan.Add(new InstallItem(catalog.Item(current), published.Info));
        return true;
    }

    return Visit(id) ? Results.Ok(plan) : Results.Conflict(new { error = "Один из компонентов недоступен или не имеет опубликованной версии." });
});
api.MapGet("/releases/{id:long}/download", async (long id, HttpContext c, Store db, Storage files) =>
{
    var release = await db.Download(DesktopUser(c), id);
    if (release is null)
        return Results.NotFound();
    var path = files.Package(release.StorageKey);
    return File.Exists(path) ? Results.File(path, "application/zip",
        $"{release.Info.AppId}-{release.Info.Version}.zip", enableRangeProcessing: true) : Results.NotFound();
});
AdminRoutes.Map(app);
app.MapFallback(() => AdminView.Message(404, "404", "Страница не найдена."));
await app.RunAsync();

static UserProfile DesktopUser(HttpContext context) => (UserProfile)context.Items[typeof(UserProfile)]!;
