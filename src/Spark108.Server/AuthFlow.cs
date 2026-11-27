using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Spark108.Server;

internal sealed class AuthFlow(IConfiguration config, IDataProtectionProvider protection, IHttpClientFactory http,
    Store store, ILogger<AuthFlow> logger)
{
    public const string AdminCookie = "spark108_acp";
    private const string FlowCookie = "spark108_login";
    private const string DefaultClientId = "6e875752c94bfdbf7a33f747325ffbcc";
    private readonly IDataProtector protector = protection.CreateProtector("Alathea browser login v1");
    private readonly Uri origin = new(config["PublicOrigin"] ?? "https://app.godish.ru");
    private readonly string authority = (config["Auth:Authority"] ?? "https://auth.godish.ru").TrimEnd('/');
    private readonly string clientId = config["Auth:ClientId"] ?? DefaultClientId;
    private string RedirectUri => new Uri(origin, "/api/auth/callback").AbsoluteUri;
    private sealed record Attempt(string State, int Port, string? Route, string? Challenge, DateTimeOffset Expires);

    public static bool TokenFormat(string? value) => value is { Length: 43 } &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    public CookieOptions Cookie(string path, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true, Secure = origin.Scheme == Uri.UriSchemeHttps, SameSite = SameSiteMode.Lax,
        Path = path, Expires = expires, IsEssential = true
    };

    public IResult Start(HttpContext context, bool desktop)
    {
        if (string.IsNullOrWhiteSpace(config["Auth:ClientSecret"]))
            return AdminView.Message(503, "Вход недоступен", "На сервере не настроен секрет Alathea Auth.");
        var attempt = new Attempt(Store.RandomToken(), 0, null, null, DateTimeOffset.UtcNow.AddMinutes(10));
        if (desktop)
        {
            var route = context.Request.Query["route"].ToString();
            var challenge = context.Request.Query["challenge"].ToString();
            if (!int.TryParse(context.Request.Query["port"], out var port) || port is < 1024 or > 65535 ||
                !TokenFormat(route) || !TokenFormat(challenge))
                return Results.BadRequest();
            attempt = attempt with { Port = port, Route = route, Challenge = challenge };
        }

        context.Response.Cookies.Append(FlowCookie, protector.Protect(JsonSerializer.Serialize(attempt)),
            Cookie("/api/auth/callback", attempt.Expires));
        return Results.Redirect(QueryHelpers.AddQueryString(authority + "/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = RedirectUri, ["scope"] = "openid profile email", ["state"] = attempt.State
        }));
    }

    public async Task<IResult> Callback(HttpContext context)
    {
        var cookie = context.Request.Cookies[FlowCookie];
        context.Response.Cookies.Delete(FlowCookie, Cookie("/api/auth/callback"));
        Attempt? attempt = null;
        if (cookie is not null)
        {
            try { attempt = JsonSerializer.Deserialize<Attempt>(protector.Unprotect(cookie)); }
            catch (CryptographicException) { }
        }
        if (attempt is null || attempt.Expires <= DateTimeOffset.UtcNow ||
            context.Request.Query["state"].ToString() != attempt.State)
            return AdminView.Message(400, "Сеанс входа истёк", "Вернитесь в Центр приложений или в панель и начните вход заново.");

        try
        {
            var code = context.Request.Query["code"].ToString();
            if (code.Length is 0 or > 2048 || context.Request.Query.ContainsKey("error"))
                return Failed(attempt, "cancelled", 400, "Вход отменён или провайдер не вернул код.");
            var user = await ReadIdentity(code, context.RequestAborted);
            if (!user.Enabled)
                return Failed(attempt, "denied", 403, "Доступ этой учётной записи отключён.");
            if (attempt.Port != 0)
            {
                var handoff = await store.NewDesktopCode(user.Id, attempt.Challenge!);
                return Results.Redirect(QueryHelpers.AddQueryString(ReturnUrl(attempt), "code", handoff));
            }
            if (!user.IsAdmin)
                return Failed(attempt, "denied", 403, "Для входа в панель требуются права администратора.");
            var session = await store.AdminSession(user);
            context.Response.Cookies.Append(AdminCookie, session.Token, Cookie("/acp", session.ExpiresAt));
            return Results.Redirect("/acp");
        }
        catch (RuleException ex)
        {
            return Failed(attempt, "denied", 403, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Alathea Auth exchange failed ({Type}).", ex.GetType().Name);
            return Failed(attempt, "provider", 502, "Не удалось завершить вход через Alathea Auth.");
        }
    }

    private static string ReturnUrl(Attempt attempt) => $"http://127.0.0.1:{attempt.Port}/callback/{attempt.Route}";

    private static IResult Failed(Attempt attempt, string code, int status, string message) =>
        attempt.Port == 0 ? AdminView.Message(status, "Вход не выполнен", message) :
            Results.Redirect(QueryHelpers.AddQueryString(ReturnUrl(attempt), "error", code));

    private async Task<Spark108.Core.UserProfile> ReadIdentity(string code, CancellationToken cancellation)
    {
        var client = http.CreateClient("Alathea");
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["client_secret"] = config["Auth:ClientSecret"]!, ["redirect_uri"] = RedirectUri
        });
        using var tokenResponse = await client.PostAsync(authority + "/token", body, cancellation);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellation));
        var token = Property(tokens.RootElement, "access_token");
        if (string.IsNullOrEmpty(token))
            throw new JsonException("Missing access token.");
        using var request = new HttpRequestMessage(HttpMethod.Get, authority + "/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var profileResponse = await client.SendAsync(request, cancellation);
        profileResponse.EnsureSuccessStatusCode();
        using var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(cancellation));
        var subject = Property(profile.RootElement, "sub");
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
            throw new JsonException("Missing or invalid subject.");
        var email = Limit(Property(profile.RootElement, "email") ?? "");
        var name = Limit(Property(profile.RootElement, "name") ?? Property(profile.RootElement, "nickname") ?? subject);
        return await store.Register(subject, name, email);
    }

    private static string Limit(string value) => string.Concat(value.EnumerateRunes().Take(255).Select(rune => rune.ToString()));
    private static string? Property(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field))
            return null;
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString(),
            JsonValueKind.Number when key == "sub" => field.GetRawText(),
            _ => null
        };
    }
}
