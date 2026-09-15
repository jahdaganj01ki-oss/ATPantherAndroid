using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ATPanther.Core;

public sealed record LoginResult(bool Success, HttpClient? Client, CookieContainer? Cookies, string? Error);

/// <summary>
/// 1:1-Port von AuthService.kt (Ulefone-Variante).
/// ForgeRock-PoW - Credentials - PKCE-Authorize - Redirect-Kette (bis 8 Hops).
/// </summary>
public sealed class AuthService
{
    private static readonly Regex WorkRegex = new("var work = \"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex DiffRegex = new(@"var difficulty = (\d+)", RegexOptions.Compiled);
 
    public async Task<LoginResult> LoginAsync(string phone, string password, CancellationToken ct = default)
    {
        var cookies = new CookieContainer();
        var loginHandler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(loginHandler) { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            // Step 1: Callbacks — ECHT LEERER Body (0 Bytes), NICHT "{}".
            using var step1 = new HttpRequestMessage(HttpMethod.Post, AppConfig.AuthEp);
            step1.Headers.UserAgent.ParseAdd(AppConfig.UserAgent);
            step1.Headers.Accept.ParseAdd("application/json");
            step1.Headers.AcceptLanguage.ParseAdd("de-DE,de;q=0.9");
            step1.Content = new ByteArrayContent(Array.Empty<byte>());
            step1.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            using var step1Resp = await client.SendAsync(step1, ct).ConfigureAwait(false);
            var step1Body = await step1Resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!step1Resp.IsSuccessStatusCode)
                return new LoginResult(false, null, null, $"Step 1 failed: {(int)step1Resp.StatusCode}");

            var data = JsonNode.Parse(step1Body)?.AsObject()
                ?? throw new InvalidOperationException("Step1: kein JSON");
            var callbacks = data["callbacks"]?.AsArray()
                ?? throw new InvalidOperationException("Step1: keine callbacks");

            var powMessage = "";
            foreach (var cb in callbacks)
            {
                if (cb?["type"]?.GetValue<string>() == "TextOutputCallback")
                {
                    foreach (var o in cb?["output"]?.AsArray() ?? new JsonArray())
                    {
                        if (o?["name"]?.GetValue<string>() == "message")
                            powMessage = o?["value"]?.GetValue<string>() ?? "";
                    }
                }
            }

            var workMatch = WorkRegex.Match(powMessage);
            var diffMatch = DiffRegex.Match(powMessage);
            if (!workMatch.Success || !diffMatch.Success)
                return new LoginResult(false, null, null, "PoW-Parameter nicht gefunden");

            var workUuid = workMatch.Groups[1].Value;
            var difficulty = int.Parse(diffMatch.Groups[1].Value);
            var nonce = await Crypto.SolvePowAsync(workUuid, difficulty, ct).ConfigureAwait(false);

            // Step 2: Credentials — ALLE Values als String.
            foreach (var cb in callbacks)
            {
                foreach (var inp in cb?["input"]?.AsArray() ?? new JsonArray())
                {
                    switch (inp?["name"]?.GetValue<string>())
                    {
                        case "IDToken1": inp!["value"] = JsonValue.Create(nonce.ToString()); break;
                        case "IDToken3": inp!["value"] = JsonValue.Create(phone); break;
                        case "IDToken4": inp!["value"] = JsonValue.Create(password); break;
                        case "IDToken5": inp!["value"] = JsonValue.Create("2"); break;
                    }
                }
            }
            using var step2 = new HttpRequestMessage(HttpMethod.Post, AppConfig.AuthEp);
            step2.Headers.UserAgent.ParseAdd(AppConfig.UserAgent);
            step2.Headers.Accept.ParseAdd("application/json");
            step2.Content = new StringContent(data.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

            using var step2Resp = await client.SendAsync(step2, ct).ConfigureAwait(false);
            if (!step2Resp.IsSuccessStatusCode)
                return new LoginResult(false, null, null, $"Step 2 failed: {(int)step2Resp.StatusCode}");
            var step2Body = await step2Resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tokenId = JsonNode.Parse(step2Body)?["tokenId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tokenId))
                return new LoginResult(false, null, null, $"Login fehlgeschlagen: {step2Body.Substring(0, Math.Min(300, step2Body.Length))}");

            cookies.Add(new Uri(AppConfig.Auth), new Cookie("iPlanetDirectoryPro", tokenId, "/", "login.alditalk-kundenbetreuung.de"));

            // Step 3: PKCE Authorize.
            var (verifier, challenge) = Crypto.GeneratePkce();
            _ = verifier;
            var state = Guid.NewGuid().ToString("N");
            var nonceParam = Guid.NewGuid().ToString("N");
            var authUrl = $"{AppConfig.Auth}/signin/oauth2/authorize?client_id={Uri.EscapeDataString(AppConfig.ClientId)}"
                + $"&response_type=code&scope=openid&redirect_uri={Uri.EscapeDataString(AppConfig.RedirectUri)}"
                + $"&code_challenge={challenge}&code_challenge_method=S256&nonce={nonceParam}&state={state}"
                + "&ui_locales=de&acr_values=password&prompt=none&realm=%2Falditalk";

            using var authReq = new HttpRequestMessage(HttpMethod.Get, authUrl);
            authReq.Headers.UserAgent.ParseAdd(AppConfig.UserAgent);
            using var authResp = await client.SendAsync(authReq, ct).ConfigureAwait(false);
            var location = authResp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location))
                return new LoginResult(false, null, null, "Kein Location-Header im OAuth-Response");

            // Step 4: Redirect-Kette (bis 8 Hops), Basis je Hop aktualisieren.
            string? nextUrl = location;
            var baseUrl = authUrl;
            for (var hop = 0; hop < 8 && nextUrl != null; hop++)
            {
                var resolved = ResolveUrl(nextUrl, baseUrl);
                using var hopReq = new HttpRequestMessage(HttpMethod.Get, resolved);
                hopReq.Headers.UserAgent.ParseAdd(AppConfig.UserAgent);
                using var hopResp = await client.SendAsync(hopReq, ct).ConfigureAwait(false);
                var code = (int)hopResp.StatusCode;
                if (code is >= 301 and <= 308)
                {
                    var loc = hopResp.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(loc))
                        return new LoginResult(false, null, null, $"Hop {hop}: kein Location");
                    nextUrl = loc;
                    baseUrl = resolved;
                }
                else break;
            }

            var apiHandler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
            var apiClient = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
            return new LoginResult(true, apiClient, cookies, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LoginResult(false, null, null, ex.Message);
        }
    }

    /// <summary>'//user/...' ist Pfad auf Basis-Domain, kein Host.</summary>
    public static string ResolveUrl(string possiblyRelative, string baseUrl)
    {
        if (possiblyRelative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            possiblyRelative.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return possiblyRelative;
        if (possiblyRelative.StartsWith("//", StringComparison.Ordinal))
        {
            var host = new Uri(baseUrl).Host;
            return $"https://{host}/{possiblyRelative.Substring(2)}";
        }
        return new Uri(new Uri(baseUrl), possiblyRelative).ToString();
    }
}
