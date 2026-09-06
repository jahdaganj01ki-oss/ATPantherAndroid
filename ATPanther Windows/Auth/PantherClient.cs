using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ATPanther.Auth;

/// <summary>
/// Antwort eines <see cref="PantherClient"/>. Header werden kopiert, damit das
/// Resultat unabhängig von der Lebensdauer der HttpResponseMessage lesbar bleibt.
/// </summary>
public sealed class HttpResult
{
    private readonly Dictionary<string, List<string>> headers = new(StringComparer.OrdinalIgnoreCase);

    internal HttpResult(HttpResponseMessage response, string body)
    {
        StatusCode = (int)response.StatusCode;
        IsSuccessful = response.IsSuccessStatusCode;
        Body = body;
        RequestUrl = response.RequestMessage?.RequestUri;

        foreach (KeyValuePair<string, IEnumerable<string>> h in response.Headers)
            headers[h.Key] = h.Value.ToList();
        if (response.Content != null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> h in response.Content.Headers)
                headers[h.Key] = h.Value.ToList();
        }
    }

    public int StatusCode { get; }
    public bool IsSuccessful { get; }
    public string Body { get; }
    public Uri? RequestUrl { get; }

    /// <summary>Erster Wert des Headers oder null – wie <c>response.headers[name]</c>.</summary>
    public string? Header(string name) =>
        headers.TryGetValue(name, out List<string>? values) && values.Count > 0 ? values[0] : null;

    /// <summary>Alle Werte (z. B. mehrfaches Set-Cookie) – leere Liste, wenn keiner da ist.</summary>
    public IReadOnlyList<string> HeaderValues(string name) =>
        headers.TryGetValue(name, out List<string>? values) ? values : (IReadOnlyList<string>)Array.Empty<string>();
}

/// <summary>
/// OkHttp-Absicherung: ein Client mit <see cref="MemoryCookieJar"/>, Cookie-Zustellung
/// und -Rettung wie der BridgeInterceptor. Zwei Instanzen wie im Original
/// (API-CONTRACT 1.1/1.2): Auth ohne Auto-Redirect, API mit.
/// </summary>
public sealed class PantherClient : IDisposable
{
    private readonly HttpClient http;

    public MemoryCookieJar Jar { get; }

    public PantherClient(HttpMessageHandler handler, MemoryCookieJar jar)
    {
        http = new HttpClient(handler);
        // OkHttp kennt kein Gesamt-Timeout, nur Connect/Read (AuthService.kt:58–59).
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        Jar = jar;
    }

    /// <summary>
    /// Handler wie im Android-Original: 30 s Connect, 30 s Read, keine Auto-Redirects
    /// im Auth-Pfad, transparente gzip-Dekompression (OkHttp sendet Accept-Encoding: gzip).
    /// UseCookies=false, damit ausschließlich unser Jar entscheidet.
    /// </summary>
    public static HttpMessageHandler NewHandler(bool allowAutoRedirect) => new SocketsHttpHandler
    {
        AllowAutoRedirect = allowAutoRedirect,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ReadTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    public Task<HttpResult> SendAsync(
        string method,
        Uri url,
        IReadOnlyList<(string Name, string Value)> headers,
        HttpContent? content,
        CancellationToken ct)
        => SendInternalAsync(method, url, headers, content, ct);

    private async Task<HttpResult> SendInternalAsync(
        string method,
        Uri url,
        IReadOnlyList<(string Name, string Value)> headers,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        foreach ((string name, string value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        string? cookieHeader = Jar.CookieHeaderFor(url);
        if (cookieHeader != null) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        if (content != null) request.Content = content;

        using HttpResponseMessage response = await http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        string body = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var result = new HttpResult(response, body);

        // Cookies übernehmen, bevor die Antwort verworfen wird (OkHttp-Bridge-Reihenfolge).
        Jar.SaveFromResponse(url, result.HeaderValues("Set-Cookie"));
        return result;
    }

    public void Dispose()
    {
        http.Dispose();
    }
}

/// <summary>
/// JSON-Bodies mit exakt dem Content-Type des Originals: <c>application/json</c>
/// ohne charset-Parameter (OkHttp schreibt ihn ebenfalls nicht).
/// </summary>
public static class JsonContent
{
    /// <summary>0-Byte-Body – Schritt 1 der Login-Kette (API-CONTRACT 2.1).</summary>
    public static StringContent Empty() => Prepare(new StringContent(string.Empty, Encoding.UTF8, AuthConfig.JSON_MEDIA));

    public static StringContent Body(string json) => Prepare(new StringContent(json, Encoding.UTF8, AuthConfig.JSON_MEDIA));

    private static StringContent Prepare(StringContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(AuthConfig.JSON_MEDIA);
        return content;
    }
}
