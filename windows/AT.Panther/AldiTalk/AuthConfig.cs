namespace ATPanther.AldiTalk;

/// <summary>
/// Konfiguration für das ALDI-Talk-Kundenportal – 1:1 aus AuthConfig der
/// Android-Version übernommen (Endpunkte, Client-ID, User-Agent).
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
}
