namespace ATPanther.Core;

public static class AppConfig
{
    public const string Portal = "https://www.alditalk-kundenportal.de";
    public const string Auth = "https://login.alditalk-kundenbetreuung.de";
    public const string AuthEp = "https://login.alditalk-kundenbetreuung.de/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login";
    public const string ClientId = "U-621-Varnish";
    public const string RedirectUri = "https://www.alditalk-kundenportal.de/logged-in-home-page/";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public const float DefaultThresholdMb = 850f;
    public const int DefaultIntervalSec = 60;
    public const int MaxConsecutiveConnectionFailures = 3;
    public const int MaxReloginsWithoutPoll = 5;
    public const int MaxLogRows = 5000;
    public const int LogUiLimit = 200;
    public const double ZeroVolumeEpsilonMb = 0.05;
    public const int SecondBookingDelayMs = 3000;
    public const int LogRetentionDays = 7;

    // ── Monitor-Freigabe (Cloudflare Worker + D1) ─────────────────────────
    // Laufzeit-Werte muessen mit MonitorGate.kt in den Android-Varianten
    // uebereinstimmen (Test: Caps_Constants_MatchAndroid).
    public const string VariantId = "windows";
    public const int LockTtlSeconds = 900;      // Lease-Laenge (15 min)
    public const long LockCheckIntervalMs = 5 * 60 * 1000;   // hoechstens 1 Abruf / 5 min
    public const long LockCacheGraceMs = 30 * 60 * 1000;     // letzter Stand bei Ausfall
}
