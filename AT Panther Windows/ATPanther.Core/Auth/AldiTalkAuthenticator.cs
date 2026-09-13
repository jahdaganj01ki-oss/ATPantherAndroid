using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ATPanther.Core.Auth;

/// <summary>
/// 1:1 port of the Android <c>AuthService.login()</c>:
/// ForgeRock PoW challenge → credential submit → session cookie → OAuth2 authorize with
/// PKCE → manual redirect chain (up to 8 hops) → API client that follows redirects.
/// </summary>
public sealed class AldiTalkAuthenticator
{
    private readonly TimeSpan _timeout;

    public AldiTalkAuthenticator(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<LoginResult> LoginAsync(
        string phone,
        string password,
        CancellationToken cancellationToken = default,
        Action<string>? trace = null)
    {
        var cookieContainer = new CookieContainer();

        // Auth phase: OkHttp client with followRedirects(false).
        var authHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var authClient = new HttpClient(authHandler) { Timeout = _timeout };
        SetUserAgent(authClient);

        try
        {
            // ── Step 1: Get login callbacks ──
            // IMPORTANT: a truly EMPTY body (0 bytes) with Content-Type application/json.
            // "{}" would make ForgeRock return the PoW challenge as a JavaScript
            // function instead of a var assignment, and the regex would find nothing.
            var step1Request = new HttpRequestMessage(HttpMethod.Post, AuthConfig.AuthEndpoint);
            step1Request.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");
            step1Request.Headers.Accept.ParseAdd("application/json");
            step1Request.Content = new ByteArrayContent(Array.Empty<byte>());
            step1Request.Content.Headers.ContentType = new MediaTypeHeaderValue(AuthConfig.JsonMediaType);

            string step1Body;
            int step1Status;
            using (var step1Response = await authClient.SendAsync(step1Request, cancellationToken))
            {
                step1Status = (int)step1Response.StatusCode;
                step1Body = await step1Response.Content.ReadAsStringAsync(cancellationToken);
                if (!step1Response.IsSuccessStatusCode)
                {
                    return Fail($"Step 1 failed: {step1Status}");
                }
            }

            trace?.Invoke($"Step1: HTTP {step1Status}, body={step1Body.Length} chars, " +
                           $"cookies=[{CookieNames(cookieContainer)}].");

            var data = JsonNode.Parse(step1Body)?.AsObject()
                       ?? throw new InvalidOperationException("Step 1: JSON nicht lesbar");

            // Extract PoW parameters from TextOutputCallback
            var powMessage = "";
            var callbackCount = 0;
            if (data["callbacks"] is JsonArray callbacks)
            {
                callbackCount = callbacks.Count;
                foreach (var cb in callbacks)
                {
                    var cbObj = cb?.AsObject();
                    if (cbObj == null) continue;
                    if (cbObj["type"]?.GetValue<string>() == "TextOutputCallback" &&
                        cbObj["output"] is JsonArray outputs)
                    {
                        foreach (var o in outputs)
                        {
                            var oObj = o?.AsObject();
                            if (oObj != null &&
                                oObj["name"]?.GetValue<string>() == "message")
                            {
                                powMessage = oObj["value"]?.GetValue<string>() ?? "";
                            }
                        }
                    }
                }
            }

            var workMatch = System.Text.RegularExpressions.Regex.Match(powMessage, "var work = \"([^\"]+)\"");
            var diffMatch = System.Text.RegularExpressions.Regex.Match(powMessage, "var difficulty = (\\d+)");
            trace?.Invoke($"Step1: {callbackCount} callbacks, inputs: [{DescribeInputs(data)}], " +
                          $"work={(workMatch.Success ? "found" : "MISSING")}, difficulty={(diffMatch.Success ? diffMatch.Groups[1].Value : "MISSING")}.");
            if (!workMatch.Success || !diffMatch.Success)
            {
                return Fail("PoW-Parameter nicht gefunden");
            }

            var workUuid = workMatch.Groups[1].Value;
            var difficulty = int.Parse(diffMatch.Groups[1].Value);
            var nonce = PoWSolver.Solve(workUuid, difficulty);
            trace?.Invoke($"PoW solved: difficulty={difficulty}, nonce={nonce}.");

            // ── Step 2: Submit credentials — ALL values as strings ──
            var filled = ApplyCredentials(data, nonce, phone, password);
            var step2Json = data.ToJsonString();
            trace?.Invoke($"Step2: filled inputs [{string.Join(",", filled)}], json={step2Json.Length} chars.");

            var step2Request = new HttpRequestMessage(HttpMethod.Post, AuthConfig.AuthEndpoint);
            step2Request.Headers.Accept.ParseAdd("application/json");
            step2Request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(step2Json));
            step2Request.Content.Headers.ContentType = new MediaTypeHeaderValue(AuthConfig.JsonMediaType);

            JsonObject step2Data;
            var step2Body = "";
            int step2Status;
            using (var step2Response = await authClient.SendAsync(step2Request, cancellationToken))
            {
                step2Status = (int)step2Response.StatusCode;
                step2Body = await step2Response.Content.ReadAsStringAsync(cancellationToken);
                if (!step2Response.IsSuccessStatusCode)
                {
                    trace?.Invoke($"Step2: HTTP {step2Status}, body={step2Body.Length} chars.");
                    return Fail($"Step 2 failed: {step2Status}");
                }
            }

            trace?.Invoke($"Step2: HTTP {step2Status}, body={step2Body.Length} chars, " +
                           $"callbacks=[{DescribeCallbackTypes(step2Body)}], cookies=[{CookieNames(cookieContainer)}].");
            step2Data = JsonNode.Parse(step2Body)?.AsObject()
                        ?? throw new InvalidOperationException("Step 2: JSON nicht lesbar");

            var tokenId = step2Data["tokenId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tokenId))
            {
                // No token: ForgeRock continues the tree (wrong credentials, extra
                // step, changed callbacks…). The full body goes to the trace; the
                // app log/status get the short diagnosis (HTTP + callback types)
                // so even the Log-Export shows which follow-up step the server
                // demands. (Server echo – contains no password.)
                trace?.Invoke("Step2 NO tokenId, full body: " + step2Body);
                var serverError = DescribeServerError(step2Data);
                if (!string.IsNullOrEmpty(serverError))
                {
                    // The portal rejected the login AND tells us why
                    // (e.g. accountLock): surface it plainly instead of the
                    // technical callback list. Do NOT retry aggressively –
                    // hammering a locked account only extends the lockout.
                    var hint = serverError switch
                    {
                        "custom.alditalk.accountLock.accountLockMsg" =>
                            "Konto vorübergehend gesperrt oder Zugangsdaten falsch — bitte im Portal prüfen und Sperrfrist abwarten, keine weiteren Start-Versuche",
                        _ => $"Servermeldung: {serverError} — Zugangsdaten im Portal prüfen"
                    };
                    return Fail($"Anmeldung abgelehnt ({hint})");
                }
                return Fail($"Kein tokenId (HTTP {step2Status}, Callbacks: {DescribeCallbackTypes(step2Body)})");
            }

            // Set iPlanetDirectoryPro cookie on the auth domain (Android sets it explicitly).
            cookieContainer.Add(
                new Uri(AuthConfig.Auth),
                new Cookie(AuthConfig.CookieName, tokenId, AuthConfig.CookiePath, AuthConfig.CookieDomain));

            // ── Step 3: OAuth2 authorize with PKCE ──
            var pkce = PkcePair.Generate();
            var state = Guid.NewGuid().ToString("N");
            var nonceParam = Guid.NewGuid().ToString("N");

            var query = string.Join("&", new[]
            {
                Q("client_id", AuthConfig.ClientId),
                Q("response_type", "code"),
                Q("scope", "openid"),
                Q("redirect_uri", AuthConfig.RedirectUri),
                Q("code_challenge", pkce.CodeChallenge),
                Q("code_challenge_method", "S256"),
                Q("nonce", nonceParam),
                Q("state", state),
                Q("ui_locales", "de"),
                Q("acr_values", "password"),
                Q("prompt", "none"),
                Q("realm", "/alditalk")
            });

            var authorizeUrl = $"{AuthConfig.Auth}/signin/oauth2/authorize?{query}";
            var authorizeResponse = await authClient.GetAsync(authorizeUrl, cancellationToken);
            using (authorizeResponse)
            {
                var location = RawLocation(authorizeResponse);
                if (string.IsNullOrEmpty(location))
                {
                    return Fail("Kein Location-Header im OAuth-Response");
                }

                // ── Step 4: Follow redirect chain manually (up to 8 hops) ──
                // Base URL is updated with every hop so relative Locations such as
                // '/user/...' resolve against the correct domain.
                var nextUrl = location;
                var baseUrl = authorizeUrl;
                var hop = 0;
                while (nextUrl != null && hop < AuthConfig.MaxRedirectHops)
                {
                    var resolved = ResolveUrl(nextUrl, baseUrl);
                    using (var hopResponse = await authClient.GetAsync(resolved, cancellationToken))
                    {
                        var code = (int)hopResponse.StatusCode;
                        var loc = RawLocation(hopResponse);
                        if (code is >= 301 and <= 308)
                        {
                            if (string.IsNullOrEmpty(loc))
                            {
                                return Fail($"Hop {hop}: kein Location");
                            }

                            nextUrl = loc;
                            baseUrl = resolved;
                        }
                        else
                        {
                            break;
                        }
                    }

                    hop++;
                }
            }

            // New client that follows redirects normally for API calls.
            var apiHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = cookieContainer,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var apiClient = new HttpClient(apiHandler) { Timeout = _timeout };
            SetUserAgent(apiClient);

            return new LoginResult { Success = true, ApiClient = apiClient };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Fail(e.Message ?? "Unbekannter Fehler");
        }
        finally
        {
            authClient.Dispose();
        }
    }

    /// <summary>
    /// Port of the Android resolveUrl(): ForgeRock/AldiTalk returns redirects like
    /// '//user/auth/account-overview/'. Although per RFC that would be protocol-relative
    /// (host='user'), 'user' here is a path segment of the base domain, so '//x' →
    /// 'https://&lt;base-host&gt;/x'.
    /// </summary>
    internal static string ResolveUrl(string possiblyRelative, string baseUrl)
    {
        if (possiblyRelative.StartsWith("http://", StringComparison.Ordinal) ||
            possiblyRelative.StartsWith("https://", StringComparison.Ordinal))
        {
            return possiblyRelative;
        }

        if (possiblyRelative.StartsWith("//", StringComparison.Ordinal))
        {
            var baseHost = new Uri(baseUrl).Host;
            return $"https://{baseHost}/{possiblyRelative[2..]}";
        }

        return new Uri(new Uri(baseUrl), possiblyRelative).ToString();
    }

    /// <summary>
    /// Lists the callback types of a ForgeRock response body ("type" per
    /// callback) so the trace shows which step the server wants next.
    /// Never includes input values (no credentials in the log).
    /// </summary>
    private static string DescribeCallbackTypes(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            if (root?["callbacks"] is not JsonArray callbacks) return "none";
            var parts = new List<string>();
            foreach (var cb in callbacks)
            {
                parts.Add(cb?.AsObject()?["type"]?.GetValue<string>() ?? "?");
            }
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Extracts a portal-side error key (custom.alditalk.common.error$…)
    /// from a no-token ForgeRock response, e.g. the accountLock message the
    /// portal returns when it rejects a login. Returns the key suffix or an
    /// empty string. Never includes credential values.
    /// </summary>
    private static string DescribeServerError(JsonObject data)
    {
        try
        {
            if (data["callbacks"] is not JsonArray callbacks) return string.Empty;
            foreach (var cb in callbacks)
            {
                var cbObj = cb?.AsObject();
                if (cbObj == null) continue;
                if (cbObj["type"]?.GetValue<string>() != "TextOutputCallback") continue;
                if (cbObj["output"] is not JsonArray outputs) continue;
                foreach (var o in outputs)
                {
                    var oObj = o?.AsObject();
                    if (oObj == null) continue;
                    if (oObj["name"]?.GetValue<string>() != "message") continue;
                    var value = oObj["value"]?.GetValue<string>() ?? "";
                    const string prefix = "custom.alditalk.common.error$";
                    if (value.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return value[prefix.Length..];
                    }
                }
            }
        }
        catch
        {
            // Best-effort diagnostics only: no error key, no problem.
        }
        return string.Empty;
    }

    /// <summary>Session cookie NAMES only (never values) for the trace.</summary>
    private static string CookieNames(CookieContainer jar)
    {
        try
        {
            var names = new List<string>();
            foreach (var uri in new[] { new Uri(AuthConfig.Auth), new Uri(AuthConfig.Portal) })
            {
                foreach (System.Net.Cookie c in jar.GetCookies(uri))
                {
                    if (!names.Contains(c.Name)) names.Add(c.Name);
                }
            }
            return names.Count == 0 ? "none" : string.Join(",", names);
        }
        catch
        {
            return "?";
        }
    }

    private static string? RawLocation(HttpResponseMessage response)
        => response.Headers.TryGetValues("Location", out var values)
            ? values.FirstOrDefault()
            : null;

    private static string Q(string key, string value)
        => $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

    /// <summary>
    /// Lists every input name across all callbacks ("type:name" pairs) so a
    /// changed ForgeRock tree (new/renamed inputs) shows up in the trace.
    /// </summary>
    private static string DescribeInputs(JsonObject data)
    {
        try
        {
            var parts = new List<string>();
            if (data["callbacks"] is JsonArray callbacks)
            {
                foreach (var cb in callbacks)
                {
                    var cbObj = cb?.AsObject();
                    if (cbObj == null) continue;
                    var type = cbObj["type"]?.GetValue<string>() ?? "?";
                    if (cbObj["input"] is JsonArray inputs)
                    {
                        foreach (var inp in inputs)
                        {
                            var n = inp?.AsObject()?["name"]?.GetValue<string>() ?? "?";
                            parts.Add($"{type}:{n}");
                        }
                    }
                    else
                    {
                        parts.Add(type);
                    }
                }
            }
            return string.Join(",", parts);
        }
        catch
        {
            return "?";
        }
    }

    private static List<string> ApplyCredentials(JsonObject data, string nonce, string phone, string password)
    {
        var filled = new List<string>();
        if (data["callbacks"] is not JsonArray callbacks) return filled;

        foreach (var cb in callbacks)
        {
            var cbObj = cb?.AsObject();
            if (cbObj == null || cbObj["input"] is not JsonArray inputs) continue;

            foreach (var inp in inputs)
            {
                var inpObj = inp?.AsObject();
                if (inpObj == null || inpObj["name"] is not JsonValue nameValue) continue;

                var name = nameValue.GetValue<string>();
                string? value = name switch
                {
                    "IDToken1" => nonce,    // PoW as STRING
                    "IDToken3" => phone,
                    "IDToken4" => password,
                    "IDToken5" => "2",      // loginbtn as STRING
                    _ => null
                };

                if (value != null)
                {
                    inpObj["value"] = value;
                    filled.Add(name);
                }
            }
        }

        return filled;
    }

    private static void SetUserAgent(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
    }

    private static LoginResult Fail(string error) => new() { Success = false, Error = error };
}
