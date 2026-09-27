package com.alditalk.panther.warning

/**
 * Ergebnis der Tarif-/Add-on-Analyse.
 *
 * Wird aus dem Selfcare-Dashboard-BFF (`/selfcare-dashboard/v1/offers`)
 * abgeleitet. Die Entscheidung "Warnen?" ist:
 *   hasBaseTariff == false && hasActiveAddon == false  =>  WARNEN
 *
 * @param hasBaseTariff  true wenn ein Basis-Datentarif aktiv/gebucht ist
 * @param hasActiveAddon true wenn eine Zusatzoption (z.B. Surf-Ticket Unlimited) aktiv ist
 * @param remainingMb    verbleibendes Datenvolumen in MB (aus dataGrantAmount, -1 wenn unbekannt)
 * @param baseOfferNames  Namen/IDs der als Basis erkannten Offers (Debug/Anzeige)
 * @param addonOfferNames Namen/IDs der als Add-on erkannten Offers
 * @param rawOfferCount   Anzahl subscribedOffers im Response
 * @param debugInfo       kompakte Diagnosezeile für Log/Notification
 */
data class TariffStatus(
    val hasBaseTariff: Boolean,
    val hasActiveAddon: Boolean,
    val remainingMb: Double = -1.0,
    val baseOfferNames: List<String> = emptyList(),
    val addonOfferNames: List<String> = emptyList(),
    val rawOfferCount: Int = 0,
    val debugInfo: String = "",
    val primaryDataStatus: com.alditalk.panther.api.DataStatus? = null,
    /**
     * true wenn subscribedOffers existieren, mindestens ein Offer aktiv ist,
     * aber KEINES klassifiziert werden konnte (weder Basis noch Add-on).
     * In diesem Fall ist die Lage unsicher (unbekanntes JSON-Format) und es
     * darf NICHT gewarnt werden (kein Fehlalarm) – nur diagnostisch loggen.
     * Siehe Fix 26.09.2026: "Surf-Ticket Unlimited" wurde nicht erkannt.
     */
    val uncertain: Boolean = false,
    /** Alle erkannten Offer-Namen/IDs (auch unklassifizierte) für Diagnose im Log-Export. */
    val allOfferNames: List<String> = emptyList(),
) {
    /**
     * True => Warnbedingungen erfüllt (kein Basis UND kein Add-on UND nicht unsicher).
     * Unsichere Fälle (aktive Offers, aber unbekanntes Format) warnen bewusst NICHT.
     */
    val shouldWarn: Boolean get() = !hasBaseTariff && !hasActiveAddon && !uncertain

    companion object {
        /** Fehlerfall – konservativ: null zurückgeben statt Fehlalarm. */
        fun error(debug: String) = TariffStatus(
            hasBaseTariff = false,
            hasActiveAddon = false,
            rawOfferCount = -1,
            debugInfo = debug,
        )
    }
}

/**
 * Reine Entscheidungslogik – testbar ohne Android-Abhängigkeiten.
 */
object TariffEvaluator {

    /** Keywords die ein Angebot als Zusatzoption / Surf-Ticket klassifizieren. */
    private val ADDON_KEYWORDS = listOf(
        "surf-ticket", "surf ticket", "surfticket", "surf",
        "dayflat", "day flat", "tagesflat", "tages-flat", "tages flat",
        "unlimited", "unbegrenzt",
        "addon", "add-on", "zusatz", "zussatz", "option",
        "on demand", "ondemand", "on-demand",
        "data snack", "snack", "extra", "nachbuch",
        "internetflat", "internet flat", "internet-flat",
        "speed bucket", "bucket", "speed",
        "flat", "ticket", "pass"
    )

    /** Begriffsfragmente die auf einen Basis-Tarif hindeuten. */
    private val BASE_KEYWORDS = listOf(
        "kombi", "kombi-paket", "paket", "package",
        "talk", "aldi talk",
        "starter", "basic", "basis", "base",
        "tarif", "tariff",
        "monat", "month", "28 tage", "30 tage",
        "prepaid"
    )

    /** Statuswerte die ein Angebot als inaktiv/expired markieren. */
    private val INACTIVE_STATUS = setOf(
        "expired", "inactive", "cancelled", "canceled", "terminated", "deactivated", "closed",
        "ausgelaufen", "abgelaufen", "gekündigt", "gekundigt", "inaktiv", "beendet", "deaktiviert"
    )

    /**
     * Klassifiziert ein einzelnes Offer-JSON-Objekt.
     *
     * Fix 26.09.2026: Der Aufrufer übergibt jetzt das komplette Offer-JSON
     * als Dump (alle String-Felder rekursiv), daher wird auch ein Surf-Ticket
     * erkannt, dessen Name in einem unerwarteten Feld steht
     * (z.B. "marketingName" statt "offerName").
     */
    fun isAddonOffer(vararg texts: String?): Boolean {
        val haystack = texts.filterNotNull().joinToString(" ").lowercase()
        return ADDON_KEYWORDS.any { haystack.contains(it) }
    }

    /** True wenn der Text auf einen Basis-Tarif hindeutet. */
    fun isBaseOffer(vararg texts: String?): Boolean {
        val haystack = texts.filterNotNull().joinToString(" ").lowercase()
        return BASE_KEYWORDS.any { haystack.contains(it) }
    }

    fun isActiveStatus(status: String?): Boolean {
        if (status.isNullOrBlank()) return true // fehlendes Feld => als aktiv werten (kein Fehlalarm)
        val lower = status.lowercase()
        // Teilexakte Prüfung: "active" in "Subscription is active since..." muss zählen,
        // "inactive" enthält aber auch "active" -> daher zuerst Inaktiv-Check.
        if (INACTIVE_STATUS.any { lower.contains(it) }) return false
        return true
    }
}
