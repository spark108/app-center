using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Spark108.Core;

namespace Spark108.Server;

internal static class AdminRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/acp/login", (HttpContext c, AuthFlow auth) => auth.Start(c, false));
        var acp = app.MapGroup("/acp");
        acp.AddEndpointFilter(async (invocation, next) =>
        {
            var c = invocation.HttpContext;
            var db = c.RequestServices.GetRequiredService<Store>();
            var user = await db.Authenticate(c.Request.Cookies[AuthFlow.AdminCookie], "admin");
            if (user is null)
                return HttpMethods.IsGet(c.Request.Method) ? AdminView.Login() : Results.Unauthorized();
            if (!user.IsAdmin)
                return AdminView.Message(403, "Доступ закрыт", "Требуются права администратора.");
            c.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name)], "admin"));
            c.Items[typeof(UserProfile)] = user;
            try
            {
                if (HttpMethods.IsPost(c.Request.Method))
                    await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
                return await next(invocation);
            }
            catch (AntiforgeryValidationException)
            {
                return AdminView.Message(400, "Форма устарела", "Откройте страницу панели заново и повторите действие.");
            }
            catch (RuleException ex)
            {
                return AdminView.Message(400, "Изменения не сохранены", ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return AdminView.Message(400, "Файл не принят", ex.Message);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode is 400 or 413)
            {
                return AdminView.Message(ex.StatusCode, "Запрос не принят", "Проверьте формат и размер загружаемого файла.");
            }
        });

        acp.MapGet("", async (HttpContext c, Store db) => AdminView.Dashboard(c, await db.Catalog(Actor(c), true)));
        acp.MapGet("/installer", () => Results.File(Path.Combine(AppContext.BaseDirectory, "Install-AppCenter.ps1"),
            "application/octet-stream", "Install-AppCenter.ps1"));
        acp.MapGet("/apps/new", async (HttpContext c, Store db) =>
            AdminView.Edit(c, null, await db.Catalog(Actor(c), true), await db.Users(), []));
        acp.MapGet("/apps/{id}", async (string id, HttpContext c, Store db) =>
        {
            var snapshot = await db.Catalog(Actor(c), true);
            return snapshot.Apps.TryGetValue(id, out var row)
                ? AdminView.Edit(c, row, snapshot, await db.Users(), await db.Access(id)) : Results.NotFound();
        });
        acp.MapPost("/apps/new", (HttpContext c, Store db) => SaveApp(c, db, null));
        acp.MapPost("/apps/{id}", (string id, HttpContext c, Store db) => SaveApp(c, db, id));
        acp.MapPost("/apps/{id}/releases", Upload);
        acp.MapGet("/apps/{id}/releases/{release:long}/package", async (
            string id, long release, HttpContext c, Store db, Storage files) =>
        {
            var snapshot = await db.Catalog(Actor(c), true);
            var row = snapshot.Releases[id].SingleOrDefault(x => x.Info.Id == release);
            if (row is null)
                return Results.NotFound();
            var path = files.Package(row.StorageKey);
            return File.Exists(path) ? Results.File(path, "application/zip",
                $"{id}-{row.Info.Version}.zip", enableRangeProcessing: true) : Results.NotFound();
        });
        acp.MapPost("/apps/{id}/releases/{release:long}", async (string id, long release, HttpContext c, Store db) =>
        {
            var form = await c.Request.ReadFormAsync();
            await db.SaveRelease(id, release, Field(form, "notes", 8000), form.ContainsKey("published"), Actor(c).Id);
            return Results.Redirect($"/acp/apps/{id}#releases");
        });
        acp.MapGet("/users", async (HttpContext c, Store db) => AdminView.Users(c, await db.Users(), Actor(c).Id));
        acp.MapPost("/users/{id:long}", async (long id, HttpContext c, Store db) =>
        {
            var form = await c.Request.ReadFormAsync();
            await db.SaveUser(id, form.ContainsKey("admin"), form.ContainsKey("enabled"), Actor(c).Id);
            return Results.Redirect("/acp/users");
        });
        acp.MapPost("/logout", async (HttpContext c, Store db, AuthFlow auth) =>
        {
            await db.Logout(c.Request.Cookies[AuthFlow.AdminCookie]!, "admin");
            c.Response.Cookies.Delete(AuthFlow.AdminCookie, auth.Cookie("/acp"));
            return Results.Redirect("/acp");
        });
    }

    private static UserProfile Actor(HttpContext context) => (UserProfile)context.Items[typeof(UserProfile)]!;

    private static string Field(IFormCollection form, string key, int max, bool required = false)
    {
        var value = form[key].ToString().Trim();
        if (value.Length > max || (required && value.Length == 0))
            throw new RuleException($"Проверьте длину поля «{key}».");
        return value;
    }

    private static async Task<IResult> SaveApp(HttpContext context, Store db, string? existing)
    {
        var actor = Actor(context);
        var form = await context.Request.ReadFormAsync();
        var id = existing ?? Field(form, "id", 64, true);
        if (!PackageRules.ValidId(id) || (existing is null && id is PackageRules.AppCenterId or "new"))
            throw new RuleException("Используйте строчные латинские буквы, цифры и дефис для идентификатора.");
        var kind = form["kind"].ToString() switch
        {
            "application" => PackageKind.Application,
            "component" => PackageKind.Component,
            _ when existing is not null => PackageKind.Application,
            _ => throw new RuleException("Выберите тип приложения.")
        };
        if (existing is not null && !(await db.Catalog(actor, true)).Apps.TryGetValue(id, out _))
            return Results.NotFound();
        var dependencies = form["dependency"].Select(x => x ?? "").Distinct().ToArray();
        var access = form["access"].Select(x => long.TryParse(x, out var value) ? value :
            throw new RuleException("Некорректный пользователь.")).Distinct().ToArray();
        await db.SaveApp(new AppRow(id, Field(form, "name", 120, true), Field(form, "description", 8000), kind,
            form.ContainsKey("restricted")), existing is null, dependencies, access, actor.Id);
        return Results.Redirect($"/acp/apps/{id}");
    }

    private static async Task<IResult> Upload(string id, HttpContext context, Store db, Storage files)
    {
        var actor = Actor(context);
        if (!(await db.Catalog(actor, true)).Apps.TryGetValue(id, out var app))
            return Results.NotFound();
        var form = await context.Request.ReadFormAsync();
        var upload = form.Files.GetFile("package");
        if (upload is null || upload.Length is <= 0 or > PackageRules.MaxArchive ||
            !Path.GetExtension(upload.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("Выберите ZIP размером до 512 МБ.");
        var version = Field(form, "version", 32, true);
        if (!PackageRules.ValidVersion(version))
            throw new RuleException("Укажите версию в формате 1.2.3.");
        var setup = Installer(form, app.Kind);
        string? entry = null;
        if (app.Kind == PackageKind.Application && setup is null)
        {
            entry = Field(form, "entry", 240, true).Replace('\\', '/');
            if (!PackageRules.ValidPath(entry) || !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new RuleException("Укажите относительный путь к .exe внутри ZIP.");
        }

        var key = Guid.NewGuid().ToString("N");
        await using var input = upload.OpenReadStream();
        var info = await files.Stage(key, app, version, Field(form, "notes", 8000), entry, input, context.RequestAborted, setup);
        try
        {
            await db.AddRelease(info, key, form.ContainsKey("published"), actor.Id);
        }
        catch (RuleException)
        {
            files.Discard(key);
            throw;
        }
        return Results.Redirect($"/acp/apps/{id}#releases");
    }

    private static InstallerSettings? Installer(IFormCollection form, PackageKind kind)
    {
        var install = Field(form, "installer", 240).Replace('\\', '/');
        if (install.Length == 0)
            return null;
        if (kind == PackageKind.AppCenter || PackageRules.ExternalWindowsPath(install) ||
            !PackageRules.ValidPackagePath(install) || !PackageRules.InstallerExtension(install))
            throw new RuleException("Укажите .exe, .msi или .ps1 внутри ZIP.");

        var uninstall = Field(form, "uninstaller", 400).Replace('\\', '/');
        if (uninstall.Length > 0 && (!ValidTarget(uninstall) || !PackageRules.InstallerExtension(uninstall)))
            throw new RuleException("Проверьте путь к установщику удаления.");
        var launch = Field(form, "entry", 400).Replace('\\', '/');
        if (launch.Length > 0 && (!ValidTarget(launch) || !launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new RuleException("Укажите путь к .exe для запуска приложения.");
        var registryKey = Field(form, "registry_key", 160);
        if (registryKey.Length > 0 && !PackageRules.ValidRegistryKey(registryKey))
            throw new RuleException("Укажите имя ключа удаления Windows, например GUID продукта MSI.");
        return new InstallerSettings(new PackageCommand(install, Args(form, "install_args"), form.ContainsKey("install_elevated")),
            uninstall.Length == 0 ? null : new PackageCommand(uninstall, Args(form, "uninstall_args"), form.ContainsKey("uninstall_elevated")),
            launch.Length == 0 ? null : launch, registryKey.Length == 0 ? null : registryKey);
    }

    private static bool ValidTarget(string path) => !path.Any(char.IsControl) &&
        (PackageRules.ExternalWindowsPath(path) || PackageRules.ValidPackagePath(path));

    private static string[] Args(IFormCollection form, string key)
    {
        var args = Field(form, key, 8000).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length > 64 || args.Any(arg => arg.Any(char.IsControl)))
            throw new RuleException("Укажите до 64 аргументов, по одному на строку.");
        return args;
    }
}
