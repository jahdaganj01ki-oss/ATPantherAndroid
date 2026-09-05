using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ATPanther.Auth;

/// <summary>Pendant zu <c>data class LoginResult</c> (AuthService.kt:34–38).</summary>
public sealed class LoginResult
{
    private LoginResult(bool success, PantherClient? client, string? error)
    {
        Success = success;
        Client = client;
        Error = error;
    }

    public bool Success { get; }
    public PantherClient? Client { get; }
    public string? Error { get; }

    public static LoginResult Failed(string error) => new(false, null, error);
    public static LoginResult Ok(PantherClient client) => new(true, client, null);
}

/// <summary>
/// Komplette Login-Kette aus <c>auth/AuthService.kt:52–261</c>:
/// ForgeRock-PoW ⇒ Credentials ⇒ OAuth2/PKCE ⇒ manuelle Redirect-Kette.
/// Umsetzung strikt nach API-CONTRACT.md Abschnitt 2.
/// </summary>
public sealed class AuthService
{
    private static readonly Regex WorkRegex = new("var work = \"([^\"]+)\"");
    private static readonly Regex DifficultyRegex = new("var difficulty = (\\d+)");

    /// <summary>
    /// org.json schreibt nur zwingend zu flüchtende Zeichen escaped; der relaxed
    /// Encoder hält dieselbe Bedingung (kein \u für Umlauttexte, kein \/).
    /// </summary>
    private static readonly JsonSerializerOptions NodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly (string Name, string Value)[] UaOnly =
    {
        ("User-Agent", AuthConfig.UA),
    };

    private readonly Action<string>? trace;

    public AuthService(Action<string>? trace = null) => this.trace = trace;

    public async Task<LoginResult> LoginAsync(string phone, string password, CancellationToken ct)
    {
        var jar = new MemoryCookieJar();
        var authClient = new PantherClient(PantherClient.NewHandler(allowAutoRedirect: false), jar);
        PantherClient? apiClient = null;

        try
        {
            // ── Schritt 1: Challenge holen (AuthService.kt:68–82) ──
            // Echter leerer Body (0 Bytes) + Content-Type application/json.
            // "{}" bringt ForgeRock dazu, die PoW-Challenge als JS-Funktion zu liefern –
            // die Regex unten findet dann nichts. Bewusster Fix aus dem Original.
            var step1Headers = new[]
            {
                ("User-Agent", AuthConfig.UA),
                ("Accept-Language", "de-DE,de;q=0.9"),
                ("Accept", "application/json"),
            };

            HttpResult step1 = await authClient
                .SendAsync("POST", new Uri(AuthConfig.AUTH_EP), step1Headers, JsonContent.Empty(), ct)
                .ConfigureAwait(false);

            if (!step1.IsSuccessful)
                return LoginResult.Failed($"Step 1 failed: {step1.StatusCode}");

            JsonNode? parsed = JsonNode.Parse(step1.Body)
                ?? throw new JsonException("Value of type null for body");
            var data = parsed as JsonObject
                ?? throw new JsonException("Antwort ist kein JSON-Objekt");

            // ── PoW-Parameter aus TextOutputCallback (AuthService.kt:84–110) ──
            var callbacks = data["callbacks"] as JsonArray
                ?? throw new JsonException("No value for callbacks");

            string powMessage = string.Empty;
            foreach (JsonNode? cbNode in callbacks)
            {
                if (cbNode is not JsonObject cb) continue;
                if (JsonText.OptString(cb, "type") != "TextOutputCallback") continue;
                if (cb["output"] is not JsonArray outputs) continue;
                foreach (JsonNode? outNode in outputs)
                {
                    if (outNode is not JsonObject o) continue;
                    if (JsonText.OptString(o, "name") == "message")
                        powMessage = JsonText.OptString(o, "value");
                }
            }

            Match workMatch = WorkRegex.Match(powMessage);
            Match diffMatch = DifficultyRegex.Match(powMessage);
            if (!workMatch.Success || !diffMatch.Success)
                return LoginResult.Failed("PoW-Parameter nicht gefunden");

            string workUuid = workMatch.Groups[1].Value;
            int difficulty = int.Parse(diffMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            trace?.Invoke($"PoW: work={workUuid}, diff={difficulty}");

            int nonce = Pow.Solve(workUuid, difficulty);
            trace?.Invoke($"PoW gelöst: nonce={nonce}");

            // ── Schritt 2: Credentials – alle Werte als String (AuthService.kt:114–137) ──
            foreach (JsonNode? cbNode in callbacks)
            {
                if (cbNode is not JsonObject cb) continue;
                if (!cb.TryGetPropertyValue("input", out JsonNode? inputNode)) continue;
                if (inputNode is not JsonArray inputs) continue;

                foreach (JsonNode? inpNode in inputs)
                {
                    if (inpNode is not JsonObject inp) continue;
                    switch (JsonText.OptString(inp, "name"))
                    {
                        case "IDToken1":
                            inp["value"] = JsonValue.Create(nonce.ToString(CultureInfo.InvariantCulture));
                            break;
                        case "IDToken3":
                            inp["value"] = JsonValue.Create(phone);
                            break;
                        case "IDToken4":
                            inp["value"] = JsonValue.Create(password);
                            break;
                        case "IDToken5":
                            inp["value"] = JsonValue.Create("2");
                            break;
                    }
                }
            }

            var step2Headers = new[]
            {
                ("User-Agent", AuthConfig.UA),
                ("Accept", "application/json"),
            };

            HttpResult step2 = await authClient
                .SendAsync("POST", new Uri(AuthConfig.AUTH_EP), step2Headers,
                           JsonContent.Body(data.ToJsonString(NodeOptions)), ct)
                .ConfigureAwait(false);

            if (!step2.IsSuccessful)
                return LoginResult.Failed($"Step 2 failed: {step2.StatusCode}");

            var step2Data = JsonNode.Parse(step2.Body) as JsonObject
                ?? throw new JsonException("Antwort ist kein JSON-Objekt");

            string tokenId = JsonText.OptString(step2Data, "tokenId");
            if (tokenId.Length == 0)
                return LoginResult.Failed("Login fehlgeschlagen: " +
                    JsonText.Truncate(step2Data.ToJsonString(NodeOptions), 300));

            // iPlanetDirectoryPro manuell setzen (AuthService.kt:151–159):
            // Domain login.alditalk-kundenbetreuung.de, Path /
            jar.SaveFromResponse(new Uri(AuthConfig.AUTH_EP), new[]
            {
                CookieEntry.Manual("iPlanetDirectoryPro", tokenId,
                                   "login.alditalk-kundenbetreuung.de", "/"),
            });
            trace?.Invoke("TokenID erhalten, Cookie gesetzt");

            // ── Schritt 3: OAuth2 Authorize mit PKCE (AuthService.kt:162–193) ──
            PkcePair pkce = Pkce.Generate();
            string state = Guid.NewGuid().ToString("N");
            string nonceParam = Guid.NewGuid().ToString("N");

            var authUrl = new Uri(AuthConfig.AUTH + "/signin/oauth2/authorize"
                + "?client_id=" + EncodeQueryComponent(AuthConfig.CLIENT_ID)
                + "&response_type=code"
                + "&scope=openid"
                + "&redirect_uri=" + EncodeQueryComponent(AuthConfig.REDIRECT_URI)
                + "&code_challenge=" + EncodeQueryComponent(pkce.CodeChallenge)
                + "&code_challenge_method=S256"
                + "&nonce=" + EncodeQueryComponent(nonceParam)
                + "&state=" + EncodeQueryComponent(state)
                + "&ui_locales=de"
                + "&acr_values=password"
                + "&prompt=none"
                + "&realm=" + EncodeQueryComponent("/alditalk"));

            HttpResult authResp = await authClient
                .SendAsync("GET", authUrl, UaOnly, null, ct)
                .ConfigureAwait(false);

            string? location = authResp.Header("Location");
            if (string.IsNullOrEmpty(location))
                return LoginResult.Failed("Kein Location-Header im OAuth-Response");

            // ── Schritt 4: Redirect-Kette von Hand, max. 8 Hops (AuthService.kt:195–230) ──
            string? nextUrl = location;
            string baseUrl = authUrl.ToString();
            int hop = 0;

            while (nextUrl != null && hop < 8)
            {
                string resolved = ResolveUrl(nextUrl, baseUrl);
                HttpResult hopResp = await authClient
                    .SendAsync("GET", new Uri(resolved), UaOnly, null, ct)
                    .ConfigureAwait(false);

                if (hopResp.StatusCode >= 301 && hopResp.StatusCode <= 308)
                {
                    string? hopLocation = hopResp.Header("Location");
                    if (string.IsNullOrEmpty(hopLocation))
                        return LoginResult.Failed($"Hop {hop}: kein Location");
                    nextUrl = hopLocation;
                    baseUrl = resolved;   // Basis mit jedem Hop aktualisieren!
                }
                else
                {
                    trace?.Invoke($"Redirect-Kette abgeschlossen nach {hop} Hops");
                    break;
                }
                hop++;
            }

            // API-Client folgt Redirects, teilt sich aber denselben CookieJar
            // (AuthService.kt:233–236).
            apiClient = new PantherClient(PantherClient.NewHandler(allowAutoRedirect: true), jar);
            return LoginResult.Ok(apiClient);
        }
        catch (OperationCanceledException)
        {
            return LoginResult.Failed("Login abgebrochen");
        }
        catch (Exception e)
        {
            // Kotlin: e.message ?: "Unbekannter Fehler"
            return LoginResult.Failed(string.IsNullOrEmpty(e.Message) ? "Unbekannter Fehler" : e.Message);
        }
        finally
        {
            authClient.Dispose();
        }
    }

    /// <summary>
    /// Relative Redirects auflösen – exakt wie <c>AuthService.kt:251–261</c>.
    /// <c>//user/…</c> ist hier ein Pfad der Basis-Domain, kein protokollrelativer Host.
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
            string host = new Uri(baseUrl).Host;
            return "https://" + host + "/" + possiblyRelative.Substring(2);
        }

        if (Uri.TryCreate(new Uri(baseUrl), possiblyRelative, out Uri? combined) && combined != null)
            return combined.ToString();

        return possiblyRelative;
    }

    /// <summary>
    /// OkHttp <c>addQueryParameter</c> kodiert genau die Zeichen
    /// <c>" ' &lt; &gt; # &amp; =</c> und Leerzeichen – nicht mehr.
    /// <c>Uri.EscapeDataString</c> würde zusätzlich <c>:</c> und <c>/</c> kodieren und
    /// damit eine andere URL senden als das Original.
    /// </summary>
    internal static string EncodeQueryComponent(string value)
    {
        const string encodeSet = " \"'<>#&=";
        if (value.Length == 0) return value;

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (encodeSet.IndexOf(ch) >= 0) sb.Append('%').Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
