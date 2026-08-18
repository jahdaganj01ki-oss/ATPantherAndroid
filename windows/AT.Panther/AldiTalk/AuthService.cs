using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ATPanther.AldiTalk;

/// <summary>
/// Kompletter ALDI-Talk-Login – 1:1-Port von AuthService.kt (Android):
///
///   1. POST authenticate (leerer Body!) → ForgeRock liefert PoW-Challenge
///   2. POST authenticate mit Credentials + gelöstem PoW-Nonce → tokenId
///   3. OAuth2 /authorize mit PKCE (S256) → Location-Header
///   4. Manuelle Redirect-Kette (bis 8 Hops, inkl. '//user/...'-Sonderfall)
///
/// Danach steht ein HttpClient mit allen Session-Cookies für die BFF-API bereit.
/// </summary>
public sealed class AuthService
{
    /// <summary>
    /// SHA-1-PoW: finde nonce, so dass sha1(workUuid + nonce) mit "0"*difficulty beginnt.
    /// </summary>
    private static int SolvePow(string workUuid, int difficulty)
    {
        var target = new string('0', difficulty);
        for (var nonce = 0; nonce <= 10_000_000; nonce++)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(workUuid + nonce)));
            if (hash.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return nonce;
        }
        throw new InvalidOperationException("PoW nicht gelöst (10M Versuche)");
    }

    public async Task<LoginResult> LoginAsync(string phone, string password)
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = false,   // Redirects manuell verfolgen (wie OkHttp followRedirects=false)
            UseCookies = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        try
        {
            // ── Schritt 1: Login-Callbacks holen ──
            // WICHTIG: Echter LEERER Body (0 Bytes) mit Content-Type application/json –
            // wie in der Android-Version kommentiert liefert "{}" die PoW-Challenge
            // als JS-Funktion statt als var-Zuweisung, die Regex findet dann nichts.
            using (var step1 = new HttpRequestMessage(HttpMethod.Post, AuthConfig.AuthEndpoint))
            {
                step1.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
                step1.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");
                step1.Headers.TryAddWithoutValidation("Accept", "application/json");
                step1.Content = new ByteArrayContent(Array.Empty<byte>());
                step1.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                using var step1Resp = await client.SendAsync(step1).ConfigureAwait(false);
                var step1Body = await step1Resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!step1Resp.IsSuccessStatusCode)
                    return new LoginResult(false, null, $"Step 1 failed: {(int)step1Resp.StatusCode}");

                var root = JsonNode.Parse(step1Body) as JsonObject
                           ?? throw new InvalidOperationException("Step 1: kein JSON-Objekt erhalten");

                var callbacks = root["callbacks"] as JsonArray
                                ?? throw new InvalidOperationException("Step 1: keine callbacks erhalten");

                // PoW-Parameter aus TextOutputCallback extrahieren
                var powMessage = "";
                foreach (var cb in callbacks.OfType<JsonObject>())
                {
                    if (cb["type"]?.GetValue<string>() != "TextOutputCallback") continue;
                    if (cb["output"] is not JsonArray outputs) continue;
                    foreach (var o in outputs.OfType<JsonObject>())
                    {
                        if (o["name"]?.GetValue<string>() == "message")
                            powMessage = o["value"]?.GetValue<string>() ?? "";
                    }
                }

                var workMatch = Regex.Match(powMessage, "var work = \"([^\"]+)\"");
                var diffMatch = Regex.Match(powMessage, "var difficulty = (\\d+)");
                if (!workMatch.Success || !diffMatch.Success)
                    return new LoginResult(false, null, "PoW-Parameter nicht gefunden");

                var nonce = SolvePow(workMatch.Groups[1].Value, int.Parse(diffMatch.Groups[1].Value));

                // ── Schritt 2: Credentials einsetzen – ALLE Werte als Strings ──
                foreach (var cb in callbacks.OfType<JsonObject>())
                {
                    if (cb["input"] is not JsonArray inputs) continue;
                    foreach (var inp in inputs.OfType<JsonObject>())
                    {
                        switch (inp["name"]?.GetValue<string>())
                        {
                            case "IDToken1": inp["value"] = nonce.ToString(); break; // PoW als STRING
                            case "IDToken3": inp["value"] = phone; break;
                            case "IDToken4": inp["value"] = password; break;
                            case "IDToken5": inp["value"] = "2"; break;              // loginbtn als STRING
                        }
                    }
                }

                using var step2 = new HttpRequestMessage(HttpMethod.Post, AuthConfig.AuthEndpoint);
                step2.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
                step2.Headers.TryAddWithoutValidation("Accept", "application/json");
                step2.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");

                using var step2Resp = await client.SendAsync(step2).ConfigureAwait(false);
                if (!step2Resp.IsSuccessStatusCode)
                    return new LoginResult(false, null, $"Step 2 failed: {(int)step2Resp.StatusCode}");

                var step2Body = await step2Resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var step2Doc = System.Text.Json.JsonDocument.Parse(step2Body);
                var tokenId = step2Doc.RootElement.TryGetProperty("tokenId", out var tokenProp)
                    ? tokenProp.GetString()
                    : null;
                if (string.IsNullOrEmpty(tokenId))
                    return new LoginResult(false, null,
                        "Login fehlgeschlagen: " + step2Body[..Math.Min(300, step2Body.Length)]);

                // iPlanetDirectoryPro-Cookie setzen
                cookieContainer.Add(new Uri("https://login.alditalk-kundenbetreuung.de"),
                    new Cookie("iPlanetDirectoryPro", tokenId)
                    {
                        Path = "/",
                        Domain = "login.alditalk-kundenbetreuung.de",
                    });
            }

            // ── Schritt 3: OAuth2 Authorize mit PKCE ──
            var (_, challenge) = Pkce.Generate();
            var state = Guid.NewGuid().ToString("N");
            var nonceParam = Guid.NewGuid().ToString("N");

            var authUrl = AuthConfig.Auth + "/signin/oauth2/authorize"
                + "?client_id=" + Uri.EscapeDataString(AuthConfig.ClientId)
                + "&response_type=code"
                + "&scope=openid"
                + "&redirect_uri=" + Uri.EscapeDataString(AuthConfig.RedirectUri)
                + "&code_challenge=" + challenge
                + "&code_challenge_method=S256"
                + "&nonce=" + nonceParam
                + "&state=" + state
                + "&ui_locales=de"
                + "&acr_values=password"
                + "&prompt=none"
                + "&realm=%2Falditalk";

            string? location;
            using (var authReq = new HttpRequestMessage(HttpMethod.Get, authUrl))
            {
                authReq.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
                using var authResp = await client.SendAsync(authReq).ConfigureAwait(false);
                location = GetRawHeader(authResp, "Location");
            }
            if (string.IsNullOrEmpty(location))
                return new LoginResult(false, null, "Kein Location-Header im OAuth-Response");

            // ── Schritt 4: Redirect-Kette manuell verfolgen (max. 8 Hops) ──
            string? nextUrl = location;
            var baseUrl = authUrl;
            var hop = 0;
            while (nextUrl != null && hop < 8)
            {
                var resolved = ResolveUrl(nextUrl, baseUrl);
                using var hopReq = new HttpRequestMessage(HttpMethod.Get, resolved);
                hopReq.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
                using var hopResp = await client.SendAsync(hopReq).ConfigureAwait(false);

                var locHdr = GetRawHeader(hopResp, "Location");
                if ((int)hopResp.StatusCode is >= 301 and <= 308)
                {
                    if (string.IsNullOrEmpty(locHdr))
                        return new LoginResult(false, null, $"Hop {hop}: kein Location");
                    nextUrl = locHdr;
                    baseUrl = resolved; // Basis für den nächsten Hop aktualisieren
                }
                else
                {
                    break; // Redirect-Kette abgeschlossen
                }
                hop++;
            }

            // Für API-Aufrufe: Client, der Redirects normal folgt, mit denselben Cookies
            var apiClient = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                AllowAutoRedirect = true,
                UseCookies = true,
            })
            {
                Timeout = TimeSpan.FromSeconds(60),
            };

            return new LoginResult(true, apiClient, null);
        }
        catch (Exception e)
        {
            return new LoginResult(false, null, e.Message);
        }
        finally
        {
            // Login-Client wird nicht mehr gebraucht; Cookies liegen im CookieContainer.
            client.Dispose();
            handler.Dispose();
        }
    }

    /// <summary>Location-Header als Roh-String lesen (wie OkHttp), damit relative Werte nicht normalisiert werden.</summary>
    private static string? GetRawHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    /// <summary>
    /// Möglicherweise relativen Redirect gegen eine Basis-URL auflösen.
    /// ForgeRock/AldiTalk liefert Redirects wie '//user/auth/account-overview/':
    /// '//x' ist hier ein Pfad-Segment der Basis-Domain → 'https://&lt;basis-host&gt;/x'.
    /// </summary>
    private static string ResolveUrl(string possiblyRelative, string baseUrl)
    {
        if (possiblyRelative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            possiblyRelative.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return possiblyRelative;
        }
        if (possiblyRelative.StartsWith("//"))
        {
            var baseHost = new Uri(baseUrl).Host;
            return $"https://{baseHost}/{possiblyRelative[2..]}";
        }
        return new Uri(new Uri(baseUrl), possiblyRelative).ToString();
    }
}
