namespace ATPanther.Core.Auth;

/// <summary>
/// 1:1 port of the Android <c>AuthConfig</c> object (app/src/main/java/.../auth/AuthService.kt).
/// </summary>
public static class AuthConfig
{
    public const string Portal = "https://www.alditalk-kundenportal.de";
    public const string Auth = "https://login.alditalk-kundenbetreuung.de";
    public const string ClientId = "U-621-Varnish";
    public const string RedirectUri = Portal + "/logged-in-home-page/";

    public const string AuthEndpoint =
        Auth + "/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public const string JsonMediaType = "application/json";

    /// <summary>Fallback difficulty; the real difficulty is parsed from the PoW message.</summary>
    public const int PowDifficulty = 3;

    public const int MaxPowNonce = 10_000_000;

    // Session cookie handling
    public const string CookieName = "iPlanetDirectoryPro";
    public const string CookieDomain = "login.alditalk-kundenbetreuung.de";
    public const string CookiePath = "/";

    // Redirect chain limit used by the Android login (max 8 hops).
    public const int MaxRedirectHops = 8;
}
