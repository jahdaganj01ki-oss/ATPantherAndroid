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
) {
    /** True => Warnbedingungen erfüllt (kein Basis UND kein Add-on). */
    val shouldWarn: Boolean get() = !hasBaseTariff && !hasActiveAddon

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
        "surf-ticket", "surf ticket", "surfticket",
        "dayflat", "tagesflat", "tages-flat",
        "unlimited",
        "addon", "add-on", "zusatz", "on demand", "ondemand",
        "data snack", "snack", "extra",
        "internetflat", "internet flat",
        "speed bucket", "bucket"
    )

    /** Statuswerte die ein Angebot als inaktiv/expired markieren. */
    private val INACTIVE_STATUS = setOf(
        "expired", "inactive", "cancelled", "canceled", "terminated", "deactivated", "closed"
    )

    /**
     * Klassifiziert ein einzelnes Offer-JSON-Objekt.
     * Heuristik: offerName/offerId/offerType/offerSubType auf Keywords prüfen,
     * zusätzlich pack[] nach dataGrantAmount absuchen.
     */
    fun isAddonOffer(
        offerName: String?,
        offerId: String?,
        offerType: String?,
        offerSubType: String?,
    ): Boolean {
        val haystack = listOfNotNull(offerName, offerId, offerType, offerSubType)
            .joinToString(" ").lowercase()
        return ADDON_KEYWORDS.any { haystack.contains(it) }
    }

    fun isActiveStatus(status: String?): Boolean {
        if (status.isNullOrBlank()) return true // fehlendes Feld => als aktiv werten (kein Fehlalarm)
        return status.lowercase() !in INACTIVE_STATUS
    }
}
