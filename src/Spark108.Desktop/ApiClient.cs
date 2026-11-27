using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spark108.Core;

namespace Spark108.Desktop;

internal sealed class LoginRequiredException() : Exception("Сеанс завершён. Войдите снова.");

internal sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string sessionPath = Path.Combine(LocalPackages.Root, "session.bin");
    private readonly HttpClient client;
    private string? token;

    public ApiClient()
    {
        Directory.CreateDirectory(LocalPackages.Root);
        var settings = Path.Combine(LocalPackages.Root, "client.json");
        var address = File.Exists(settings)
            ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(settings), Json)?.Origin : null;
        var origin = new Uri(address ?? "https://app.godish.ru");
        if ((origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)) ||
            origin.AbsolutePath != "/" || origin.UserInfo != "" || origin.Fragment != "" || origin.Query != "")
            throw new InvalidOperationException("В client.json нужен адрес HTTPS без пути. HTTP допустим для localhost.");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("X-AppCenter-Install-Protocol", "2");
        try
        {
            if (File.Exists(sessionPath))
                token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(sessionPath),
                    Entropy(), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
    }

    private sealed record ClientSettings(string Origin);
    public bool HasSession => token is not null;
    private byte[] Entropy() => Encoding.UTF8.GetBytes(client.BaseAddress!.AbsoluteUri);
    private static string WebToken(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<UserProfile> Login(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var route = WebToken(RandomNumberGenerator.GetBytes(32));
        var verifier = WebToken(RandomNumberGenerator.GetBytes(32));
        var challenge = WebToken(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var path = "/callback/" + route;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = new Uri(client.BaseAddress!, $"/api/auth/desktop?port={port}&route={route}&challenge={challenge}");
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        string code;
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            var callback = await ReadCallback(connection, port, path, deadline.Token);
            if (callback is null)
                continue;
            if (callback.TryGetValue("error", out var error))
                throw new InvalidOperationException(error switch
                {
                    "denied" => "Доступ учётной записи отключён или ограничен.",
                    "provider" => "Сервис Alathea Auth не завершил вход.",
                    _ => "Вход отменён."
                });
            if (callback.TryGetValue("code", out var found) && found.Length == 43)
            {
                code = found;
                break;
            }
        }
        using var response = await client.PostAsJsonAsync("/api/auth/exchange", new DesktopExchange(code, verifier), Json, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Код входа истёк. Начните вход заново.");
        await Ensure(response, cancellation);
        var session = await Read<DesktopSession>(response, cancellation);
        token = session.Token;
        try
        {
            File.WriteAllBytes(sessionPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(session.Token),
                Entropy(), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
        return session.User;
    }

    private static async Task<Dictionary<string, string>?> ReadCallback(TcpClient client, int port, string path,
        CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var stream = client.GetStream();
        var bytes = new byte[8192];
        var length = 0;
        try
        {
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (count == 0) return null;
                length += count;
                if (Encoding.ASCII.GetString(bytes, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
            var header = Encoding.ASCII.GetString(bytes, 0, length).Split("\r\n");
            var request = header[0].Split(' ');
            var host = header.Skip(1).FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
            var separator = request.Length == 3 ? request[1].IndexOf('?') : -1;
            var valid = host is not null && host[5..].Trim() == $"127.0.0.1:{port}" && request.Length == 3 &&
                request[0] == "GET" && separator > 0 && request[1][..separator] == path;
            Dictionary<string, string>? query = null;
            if (valid)
            {
                query = new Dictionary<string, string>();
                foreach (var pair in request[1][(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2)
                        query[parts[0]] = Uri.UnescapeDataString(parts[1]);
                }
            }
            var status = valid ? "200 OK" : "404 Not Found";
            var body = Encoding.UTF8.GetBytes(valid
                ? "<!doctype html><html lang=ru><meta charset=utf-8><title>Spark108</title><h1>Вернитесь в Центр приложений</h1><p>Эту вкладку можно закрыть.</p></html>"
                : "Not found");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(body, deadline.Token);
            return query;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task<UserProfile> Me(CancellationToken ct) => Get<UserProfile>("/api/me", ct);
    public Task<CatalogApp[]> Catalog(CancellationToken ct) => Get<CatalogApp[]>("/api/catalog", ct);
    public Task<ReleaseInfo[]> Releases(string id, CancellationToken ct) => Get<ReleaseInfo[]>($"/api/apps/{id}/releases", ct);
    public Task<InstallItem[]> Plan(string id, long release, CancellationToken ct) => Get<InstallItem[]>($"/api/apps/{id}/plan?release={release}", ct);

    public async Task Logout(CancellationToken ct)
    {
        try
        {
            if (HasSession)
                using (await Send(HttpMethod.Post, "/api/auth/logout", ct)) { }
        }
        finally { Forget(); }
    }

    public void Forget()
    {
        token = null;
        try { File.Delete(sessionPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, path, ct);
        return await Read<T>(response, ct);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Сервер вернул пустой ответ.");

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, CancellationToken ct, HttpContent? content = null)
    {
        if (token is null)
            throw new LoginRequiredException();
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new LoginRequiredException();
            await Ensure(response, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task Ensure(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(error.GetString());
        }
        throw new HttpRequestException($"Сервер вернул ошибку {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public async Task<FileUpdatePlan?> FilePlan(long id, Dictionary<string, string> files, CancellationToken ct)
    {
        try
        {
            using var content = JsonContent.Create(new FileUpdateRequest(files), options: Json);
            using var response = await Send(HttpMethod.Post, $"/api/releases/{id}/files/plan", ct, content);
            return await Read<FileUpdatePlan>(response, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public Task Download(ReleaseInfo release, string path, IProgress<int> progress, CancellationToken ct)
    {
        if (release.Size is <= 0 or > PackageRules.MaxArchive)
            throw new InvalidDataException("Некорректные данные пакета.");
        return DownloadChecked($"/api/releases/{release.Id}/download", path, release.Size, release.Sha256, progress, ct);
    }

    public Task DownloadFile(long release, int index, PackageFile file, string path, CancellationToken ct) =>
        DownloadChecked($"/api/releases/{release}/files/{index}", path, file.Size, file.Sha256, null, ct);

    private async Task DownloadChecked(string route, string path, long size, string? expectedHash, IProgress<int>? progress,
        CancellationToken ct)
    {
        if (size is < 0 or > PackageRules.MaxUnpacked || !PackageRules.ValidHash(expectedHash))
            throw new InvalidDataException("Некорректные данные скачиваемого файла.");
        using var response = await Send(HttpMethod.Get, route, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[128 * 1024];
        long total = 0;
        var lastPercent = -1;
        while (true)
        {
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var read = await input.ReadAsync(buffer, idleTimeout.Token);
            if (read == 0) break;
            total += read;
            if (total > size)
                throw new InvalidDataException("Загруженный пакет превышает заявленный размер.");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            var percent = (int)(total * 100 / size);
            if (lastPercent != percent)
            {
                progress?.Report(percent);
                lastPercent = percent;
            }
        }
        if (total != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма пакета не совпала.");
    }

    public void Dispose() => client.Dispose();
}
