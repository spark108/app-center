using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using MySqlConnector;
using Spark108.Core;

namespace Spark108.Server;

internal sealed record AppRow(string Id, string Name, string Description, PackageKind Kind, bool Restricted)
{
    public CatalogApp ToCatalog(string[] dependencies, ReleaseInfo? latest) => new(Id, Name, Description, Kind, dependencies, latest);
}

internal sealed record ReleaseRow(ReleaseInfo Info, string StorageKey, bool Published);
internal sealed record CatalogSnapshot(Dictionary<string, AppRow> Apps, ILookup<string, string> Dependencies, ILookup<string, ReleaseRow> Releases)
{
    public CatalogApp Item(string id) => Apps[id].ToCatalog(Dependencies[id].ToArray(), Releases[id].FirstOrDefault()?.Info);
}

internal sealed class RuleException(string message) : Exception(message);

internal sealed class Store(IConfiguration config)
{
    private readonly string connectionString = config.GetConnectionString("MariaDb") ?? new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = config.GetValue("Database:Port", 3307u), Database = "spark108",
        UserID = "spark108", Password = config["Database:Password"]
            ?? throw new InvalidOperationException("Database__Password is required."),
        SslMode = MySqlSslMode.None
    }.ConnectionString;

    public async Task Initialize()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql"));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = await Open();
                await using var cmd = Cmd(connection, null, script);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (MySqlException) when (attempt < 19)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private async Task<MySqlConnection> Open()
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static MySqlCommand Cmd(MySqlConnection connection, MySqlTransaction? tx, string sql,
        params (string Name, object? Value)[] args)
    {
        var cmd = new MySqlCommand(sql, connection, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task<int> Execute(MySqlConnection c, MySqlTransaction? tx, string sql,
        params (string, object?)[] args)
    {
        await using var cmd = Cmd(c, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T[]> Read<T>(MySqlConnection c, MySqlTransaction? tx, string sql,
        Func<MySqlDataReader, T> map, params (string, object?)[] args)
    {
        await using var cmd = Cmd(c, tx, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(map(reader));
        return rows.ToArray();
    }

    private static async Task LockWrites(MySqlConnection c, MySqlTransaction tx)
    {
        await using var cmd = Cmd(c, tx, "SELECT id FROM write_gate WHERE id=1 FOR UPDATE");
        await cmd.ExecuteScalarAsync();
    }

    private static UserProfile User(MySqlDataReader row) => new(row.GetInt64(0), row.GetString(1),
        row.GetString(2), row.GetString(3), row.GetBoolean(4), row.GetBoolean(5));
    private static AppRow App(MySqlDataReader row) => new(row.GetString(0), row.GetString(1),
        row.GetString(2), (PackageKind)row.GetInt32(3), row.GetBoolean(4));
    private static ReleaseRow Release(MySqlDataReader row) => new(new ReleaseInfo(row.GetInt64(0),
        row.GetString(1), row.GetString(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4),
        row.GetInt64(5), row.GetString(6)), row.GetString(7), row.GetBoolean(8));

    private const string UserFields = "u.id,u.subject,u.display_name,u.email,u.is_admin,u.enabled";
    private const string AppFields = "a.id,a.name,a.description,a.kind,a.restricted";
    private const string ReleaseFields = "r.id,r.app_id,r.version,r.notes,r.entry_point,r.size_bytes,r.sha256,r.storage_key,r.published";
    private const string Visible = "(@admin OR NOT a.restricted OR EXISTS (SELECT 1 FROM app_access x WHERE x.app_id=a.id AND x.user_id=@user))";

    public async Task<UserProfile> Register(string subject, string name, string email)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockWrites(c, tx);
        var user = (await Read(c, tx, $"SELECT {UserFields} FROM users u WHERE subject=@s", User, ("@s", subject))).SingleOrDefault();
        if (user is null)
        {
            var first = (await Read(c, tx, "SELECT id FROM users LIMIT 1", r => r.GetInt64(0))).Length == 0;
            if (first && config["Auth:BootstrapSubject"] is { Length: > 0 } expected && subject != expected)
                throw new RuleException("Первый вход разрешён только владельцу, указанному при настройке сервера.");
            await Execute(c, tx, "INSERT INTO users(subject,display_name,email,is_admin) VALUES(@s,@n,@e,@a)",
                ("@s", subject), ("@n", name), ("@e", email), ("@a", first));
        }
        else
        {
            await Execute(c, tx, "UPDATE users SET display_name=@n,email=@e WHERE id=@id",
                ("@n", name), ("@e", email), ("@id", user.Id));
        }
        var current = (await Read(c, tx, $"SELECT {UserFields} FROM users u WHERE subject=@s", User, ("@s", subject))).Single();
        await tx.CommitAsync();
        return current;
    }

    public static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    public static string Challenge(string verifier) => WebEncoders.Base64UrlEncode(Hash(verifier));

    public async Task<string> NewDesktopCode(long userId, string challenge)
    {
        var code = RandomToken();
        await using var c = await Open();
        await Execute(c, null, "DELETE FROM desktop_codes WHERE expires_at <= UTC_TIMESTAMP()");
        await Execute(c, null, "INSERT INTO desktop_codes VALUES(@hash,@user,@challenge,@expires)",
            ("@hash", Hash(code)), ("@user", userId), ("@challenge", challenge), ("@expires", DateTime.UtcNow.AddMinutes(2)));
        return code;
    }

    public async Task<DesktopSession?> Exchange(string code, string verifier)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var hash = Hash(code);
        var user = (await Read(c, tx, $"""
            SELECT {UserFields} FROM desktop_codes d JOIN users u ON u.id=d.user_id
            WHERE d.code_hash=@hash AND d.challenge=@challenge AND d.expires_at>UTC_TIMESTAMP() AND u.enabled
            FOR UPDATE
            """, User, ("@hash", hash), ("@challenge", Challenge(verifier)))).SingleOrDefault();
        if (user is null)
            return null;
        await Execute(c, tx, "DELETE FROM desktop_codes WHERE code_hash=@hash", ("@hash", hash));
        var session = await InsertSession(c, tx, user, "desktop", TimeSpan.FromDays(30));
        await tx.CommitAsync();
        return session;
    }

    private static async Task<DesktopSession> InsertSession(MySqlConnection c, MySqlTransaction? tx,
        UserProfile user, string purpose, TimeSpan lifetime)
    {
        var token = RandomToken();
        var expires = DateTime.UtcNow.Add(lifetime);
        await Execute(c, tx, "INSERT INTO sessions VALUES(@hash,@user,@purpose,@expires)",
            ("@hash", Hash(token)), ("@user", user.Id), ("@purpose", purpose), ("@expires", expires));
        return new DesktopSession(token, expires, user);
    }

    public async Task<DesktopSession> AdminSession(UserProfile user)
    {
        await using var c = await Open();
        await Execute(c, null, "DELETE FROM sessions WHERE expires_at <= UTC_TIMESTAMP()");
        return await InsertSession(c, null, user, "admin", TimeSpan.FromHours(8));
    }

    public async Task<UserProfile?> Authenticate(string? token, string purpose)
    {
        if (!AuthFlow.TokenFormat(token))
            return null;
        await using var c = await Open();
        return (await Read(c, null, $"""
            SELECT {UserFields} FROM sessions s JOIN users u ON u.id=s.user_id
            WHERE s.token_hash=@hash AND s.purpose=@purpose AND s.expires_at>UTC_TIMESTAMP() AND u.enabled
            """, User, ("@hash", Hash(token!)), ("@purpose", purpose))).SingleOrDefault();
    }

    public async Task Logout(string token, string purpose)
    {
        await using var c = await Open();
        await Execute(c, null, "DELETE FROM sessions WHERE token_hash=@hash AND purpose=@purpose",
            ("@hash", Hash(token)), ("@purpose", purpose));
    }

    public async Task<CatalogSnapshot> Catalog(UserProfile user, bool administration = false)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var args = new (string, object?)[] { ("@admin", user.IsAdmin), ("@user", user.Id) };
        var apps = await Read(c, tx, $"SELECT {AppFields} FROM apps a WHERE {Visible} ORDER BY a.name", App, args);
        var dependencies = await Read(c, tx, $"""
            SELECT d.app_id,d.dependency_id FROM app_dependencies d JOIN apps a ON a.id=d.app_id
            WHERE {Visible} ORDER BY d.dependency_id
            """, r => (Id: r.GetString(0), Dependency: r.GetString(1)), args);
        var releaseFilter = administration ? "" : "AND r.published";
        var releases = await Read(c, tx, $"""
            SELECT {ReleaseFields} FROM releases r JOIN apps a ON a.id=r.app_id
            WHERE {Visible} {releaseFilter} ORDER BY r.major DESC,r.minor DESC,r.patch DESC
            """, Release, args);
        await tx.CommitAsync();
        return new CatalogSnapshot(apps.ToDictionary(x => x.Id),
            dependencies.ToLookup(x => x.Id, x => x.Dependency), releases.ToLookup(x => x.Info.AppId));
    }

    public async Task<ReleaseRow?> Download(UserProfile user, long id)
    {
        await using var c = await Open();
        return (await Read(c, null, $"""
            SELECT {ReleaseFields} FROM releases r JOIN apps a ON a.id=r.app_id
            WHERE r.id=@id AND r.published AND {Visible}
            """, Release, ("@id", id), ("@admin", user.IsAdmin), ("@user", user.Id))).SingleOrDefault();
    }

    public async Task<UserProfile[]> Users()
    {
        await using var c = await Open();
        return await Read(c, null, $"SELECT {UserFields} FROM users u ORDER BY u.display_name,u.id", User);
    }

    public async Task<long[]> Access(string id)
    {
        await using var c = await Open();
        return await Read(c, null, "SELECT user_id FROM app_access WHERE app_id=@id", r => r.GetInt64(0), ("@id", id));
    }

    public async Task SaveUser(long id, bool admin, bool enabled, long actorId)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockWrites(c, tx);
        await RequireAdmin(c, tx, actorId);
        if (id == actorId && (!admin || !enabled))
            throw new RuleException("Текущую учётную запись администратора измените из другой учётной записи.");
        var exists = await Read(c, tx, "SELECT id FROM users WHERE id=@id", r => r.GetInt64(0), ("@id", id));
        if (exists.Length == 0)
            throw new RuleException("Пользователь не найден.");
        await Execute(c, tx, "UPDATE users SET is_admin=@a,enabled=@e WHERE id=@id",
            ("@a", admin), ("@e", enabled), ("@id", id));
        if (!enabled || !admin)
            await Execute(c, tx, "DELETE FROM sessions WHERE user_id=@id AND (@disabled OR purpose='admin')",
                ("@id", id), ("@disabled", !enabled));
        await tx.CommitAsync();
    }

    private static async Task RequireAdmin(MySqlConnection c, MySqlTransaction tx, long actor)
    {
        if ((await Read(c, tx, "SELECT id FROM users WHERE id=@id AND enabled AND is_admin",
                r => r.GetInt64(0), ("@id", actor))).Length == 0)
            throw new RuleException("Требуются полномочия администратора.");
    }

    public async Task SaveApp(AppRow app, bool create, string[] dependencies, long[] access, long actor)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockWrites(c, tx);
        await RequireAdmin(c, tx, actor);
        var apps = (await Read(c, tx, $"SELECT {AppFields} FROM apps a", App)).ToDictionary(x => x.Id);
        if (create == apps.ContainsKey(app.Id))
            throw new RuleException(create ? "Такой идентификатор уже занят." : "Приложение не найдено.");
        if (dependencies.Any(id => id == app.Id || !apps.TryGetValue(id, out var dep) || dep.Kind != PackageKind.Component))
            throw new RuleException("В зависимостях разрешены только другие служебные компоненты.");

        var edges = await Read(c, tx, "SELECT app_id,dependency_id FROM app_dependencies WHERE app_id<>@id",
            r => (From: r.GetString(0), To: r.GetString(1)), ("@id", app.Id));
        var graph = edges.ToLookup(x => x.From, x => x.To);
        var remaining = new Stack<string>(dependencies);
        var visited = new HashSet<string>();
        while (remaining.TryPop(out var dependency))
        {
            if (dependency == app.Id)
                throw new RuleException("Обнаружена циклическая зависимость.");
            if (visited.Add(dependency))
                foreach (var next in graph[dependency])
                    remaining.Push(next);
        }
        if (create)
            await Execute(c, tx, "INSERT INTO apps VALUES(@id,@n,@d,@kind,@r)", ("@id", app.Id),
                ("@n", app.Name), ("@d", app.Description), ("@kind", (int)app.Kind), ("@r", app.Restricted));
        else
            await Execute(c, tx, "UPDATE apps SET name=@n,description=@d,restricted=@r WHERE id=@id",
                ("@n", app.Name), ("@d", app.Description), ("@r", app.Restricted), ("@id", app.Id));
        await Execute(c, tx, "DELETE FROM app_dependencies WHERE app_id=@id", ("@id", app.Id));
        foreach (var dependency in dependencies)
            await Execute(c, tx, "INSERT INTO app_dependencies VALUES(@id,@dep)", ("@id", app.Id), ("@dep", dependency));
        await Execute(c, tx, "DELETE FROM app_access WHERE app_id=@id", ("@id", app.Id));
        foreach (var user in access)
        {
            if (await Execute(c, tx, """
                INSERT INTO app_access SELECT @id,id FROM users WHERE id=@user
                """, ("@id", app.Id), ("@user", user)) == 0)
                throw new RuleException("Один из пользователей в списке доступа не найден.");
        }
        await tx.CommitAsync();
    }

    public async Task<bool> HasRelease(string appId, string version)
    {
        await using var c = await Open();
        return (await Read(c, null, "SELECT id FROM releases WHERE app_id=@app AND version=@v",
            r => r.GetInt64(0), ("@app", appId), ("@v", version))).Length > 0;
    }

    public async Task AddRelease(ReleaseInfo info, string key, bool published, long? actor)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockWrites(c, tx);
        if (actor is { } user)
            await RequireAdmin(c, tx, user);
        else if (info.AppId != PackageRules.AppCenterId)
            throw new RuleException("Автоматическая публикация допустима только для App Center.");
        var version = Version.Parse(info.Version);
        try
        {
            await Execute(c, tx, """
                INSERT INTO releases(app_id,version,major,minor,patch,notes,entry_point,size_bytes,sha256,storage_key,published)
                VALUES(@app,@v,@major,@minor,@patch,@notes,@entry,@size,@sha,@key,@published)
                """, ("@app", info.AppId), ("@v", info.Version), ("@major", version.Major), ("@minor", version.Minor),
                ("@patch", version.Build), ("@notes", info.Notes), ("@entry", info.EntryPoint), ("@size", info.Size),
                ("@sha", info.Sha256), ("@key", key), ("@published", published));
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw new RuleException("Такая версия уже существует.");
        }
        await tx.CommitAsync();
    }

    public async Task SaveRelease(string app, long id, string notes, bool published, long actor)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockWrites(c, tx);
        await RequireAdmin(c, tx, actor);
        var exists = await Read(c, tx, "SELECT id FROM releases WHERE id=@id AND app_id=@app",
            r => r.GetInt64(0), ("@id", id), ("@app", app));
        if (exists.Length == 0)
            throw new RuleException("Версия не найдена.");
        await Execute(c, tx, "UPDATE releases SET notes=@n,published=@p WHERE id=@id AND app_id=@app",
            ("@n", notes), ("@p", published), ("@id", id), ("@app", app));
        await tx.CommitAsync();
    }
}
