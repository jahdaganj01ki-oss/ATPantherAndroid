using System.Text.Json.Nodes;

namespace ATPanther.Core;

/// <summary>
/// Ergebnis der Tarif-/Add-on-Analyse.
/// 1:1-Port von TariffStatus.kt (Ulefone-Variante).
/// Entscheidung "Warnen?": hasBaseTariff == false && hasActiveAddon == false => WARNEN.
/// </summary>
public sealed record TariffStatus(
    bool HasBaseTariff,
    bool HasActiveAddon,
    double RemainingMb = -1.0,
    List<string>? BaseOfferNames = null,
    List<string>? AddonOfferNames = null,
    int RawOfferCount = 0,
    string DebugInfo = "",
    DataStatus? PrimaryDataStatus = null,
    /// <summary>
    /// true wenn subscribedOffers existieren, mindestens ein Offer aktiv ist,
    /// aber KEINES klassifiziert werden konnte. Dann NICHT warnen (kein Fehlalarm).
    /// Fix 26.09.2026 (Android) / 27.09.2026 (Windows): "Surf-Ticket Unlimited"
    /// wurde nicht erkannt, weil nur wenige Felder geprueft wurden.
    /// </summary>
    bool Uncertain = false,
    List<string>? AllOfferNames = null)
{
    public bool ShouldWarn => !HasBaseTariff && !HasActiveAddon && !Uncertain;

    public static TariffStatus Error(string debug) => new(false, false, -1.0, null, null, -1, debug);
}

/// <summary>Reine Entscheidungslogik – testbar ohne UI-Abhaengigkeiten. Port von TariffEvaluator.kt.</summary>
public static class TariffEvaluator
{
    /// <summary>Keywords die ein Angebot als Zusatzoption / Surf-Ticket klassifizieren.</summary>
    private static readonly string[] AddonKeywords =
    {
        "surf-ticket", "surf ticket", "surfticket", "surf",
        "dayflat", "day flat", "tagesflat", "tages-flat", "tages flat",
        "unlimited",
        "addon", "add-on", "zusatz", "zussatz", "option",
        "on demand", "ondemand", "on-demand",
        "data snack", "snack", "extra", "nachbuch",
        "internetflat", "internet flat", "internet-flat",
        "speed bucket", "bucket", "speed",
        "flat", "ticket", "pass",
    };

    /// <summary>Begriffsfragmente die auf einen Basis-Tarif hindeuten.</summary>
    private static readonly string[] BaseKeywords =
    {
        "kombi", "kombi-paket", "paket", "package",
        "talk", "aldi talk",
        "starter", "basic", "basis", "base",
        "tarif", "tariff",
        "monat", "month", "28 tage", "30 tage",
        "prepaid",
    };

    /// <summary>Statuswerte die ein Angebot als inaktiv/expired markieren.</summary>
    private static readonly string[] InactiveStatus =
    {
        "expired", "inactive", "cancelled", "canceled", "terminated", "deactivated", "closed",
        "ausgelaufen", "abgelaufen", "gekündigt", "gekundigt", "inaktiv", "beendet", "deaktiviert",
    };

    public static bool IsAddonOffer(params string?[] texts)
    {
        var haystack = string.Join(" ", texts.Where(t => !string.IsNullOrEmpty(t))).ToLowerInvariant();
        return AddonKeywords.Any(haystack.Contains);
    }

    public static bool IsBaseOffer(params string?[] texts)
    {
        var haystack = string.Join(" ", texts.Where(t => !string.IsNullOrEmpty(t))).ToLowerInvariant();
        return BaseKeywords.Any(haystack.Contains);
    }

    public static bool IsActiveStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true; // fehlendes Feld => aktiv (kein Fehlalarm)
        var lower = status.ToLowerInvariant();
        // Zuerst Inaktiv-Check: "inactive" enthaelt "active".
        if (InactiveStatus.Any(lower.Contains)) return false;
        return true;
    }
}
