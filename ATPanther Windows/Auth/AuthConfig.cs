namespace ATPanther.Auth;

/// <summary>
/// Authentifizierungs-Konfiguration – 1:1 aus <c>auth/AuthService.kt:21–32</c>
/// (siehe API-CONTRACT.md Abschnitt 0). Die Großschreibung folgt dem Kotlin-Original,
/// damit der Abgleich zwischen beiden Codebasen zeilengenau möglich ist.
/// </summary>
public static class AuthConfig
{
    public const string PORTAL = "https://www.alditalk-kundenportal.de";
    public const string AUTH = "https://login.alditalk-kundenbetreuung.de";
    public const string CLIENT_ID = "U-621-Varnish";

    public const string REDIRECT_URI = PORTAL + "/logged-in-home-page/";

    public const string AUTH_EP =
        AUTH + "/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login";

    /// <summary>
    /// Bewusst ein Windows-Chrome-UA: er steht so im Android-Original und ist Teil des
    /// Erwartungsmusters des Portals. Nicht "korrigieren".
    /// </summary>
    public const string UA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>Nur Default – real verwendet der Loop den Wert aus der Server-Challenge.</summary>
    public const int POW_DIFFICULTY = 3;

    /// <summary>OkHttp-MediaType "application/json" ohne charset (API-CONTRACT 1.4).</summary>
    public const string JSON_MEDIA = "application/json";
}
