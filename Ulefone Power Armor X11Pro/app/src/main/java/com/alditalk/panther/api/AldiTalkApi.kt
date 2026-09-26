package com.alditalk.panther.api

import android.util.Log
import com.alditalk.panther.auth.AuthConfig
import com.alditalk.panther.warning.TariffEvaluator
import com.alditalk.panther.warning.TariffStatus
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

private const val TAG = "AldiTalkApi"

data class DataStatus(
    val remainingMb: Double,
    val offerId: String,
    val subscriptionId: String,
    val resourceId: String,
    val onDemandAmount: String,
    val refillThreshold: String,
)

data class BookingResult(
    val success: Boolean,
    val isUpdated: Boolean,
    val statusCode: Int,
    val message: String,
)

class AldiTalkApi(private val client: OkHttpClient) {

    private fun bffHeaders(): Map<String, String> = mapOf(
        "Accept" to "application/json, text/plain, */*",
        "Referer" to "${AuthConfig.PORTAL}/portal/auth/uebersicht/",
        "X-CORRELATION-ID" to "C_${UUID.randomUUID()}",
        "X-TRANSACTION-ID" to "T_${UUID.randomUUID()}",
    )

    /**
     * Ermittelt die contractId (subscriptionId) automatisch aus der
     * navigation-list (customer-master-data BFF) OHNE Eingabe.
     * Der Endpunkt braucht keine Parameter – die Authentifizierung läuft
     * über die nach dem Login gesetzten Session-Cookies.
     * Liefert userDetails.subscriptions[0].contractId.
     * Bei mehreren Verträgen wird der zum [msisdn] passende gewählt.
     */
    suspend fun resolveContractId(msisdn: String): String? = withContext(Dispatchers.IO) {
        try {
            val url = ("${AuthConfig.PORTAL}/scs/bff/scs-207-customer-master-data-bff"
                    + "/customer-master-data/v1/navigation-list").toHttpUrl()

            val request = Request.Builder()
                .url(url)
                .apply { bffHeaders().forEach { (k, v) -> header(k, v) } }
                .header("User-Agent", AuthConfig.UA)
                .get()
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                Log.e(TAG, "resolveContractId failed: ${response.code}")
                return@withContext null
            }

            val json = JSONObject(response.body!!.string())
            val subs = json.optJSONObject("userDetails")?.optJSONArray("subscriptions")
            if (subs == null || subs.length() == 0) {
                Log.e(TAG, "navigation-list: keine subscriptions")
                return@withContext null
            }

            // 1) bevorzugt: Eintrag, dessen msisdn zur Rufnummer passt
            for (i in 0 until subs.length()) {
                val s = subs.getJSONObject(i)
                if (s.optString("msisdn") == msisdn) {
                    val cid = s.optString("contractId")
                    Log.d(TAG, "contractId fuer $msisdn: $cid")
                    return@withContext cid
                }
            }
            // 2) Fallback: erster Eintrag
            val cid = subs.getJSONObject(0).optString("contractId")
            Log.d(TAG, "contractId (fallback): $cid")
            cid
        } catch (e: Exception) {
            Log.e(TAG, "Fehler bei resolveContractId", e)
            null
        }
    }

    /** Fetch offers and extract remaining data volume. */
    suspend fun getRemainingData(contractId: String): DataStatus? = withContext(Dispatchers.IO) {
        try {
            val url = ("${AuthConfig.PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff"
                    + "/selfcare-dashboard/v1/offers?contractId=$contractId").toHttpUrl()

            val request = Request.Builder()
                .url(url)
                .apply { bffHeaders().forEach { (k, v) -> header(k, v) } }
                .header("User-Agent", AuthConfig.UA)
                .get()
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                Log.e(TAG, "getOffers failed: ${response.code}")
                return@withContext null
            }

            val json = JSONObject(response.body!!.string())
            val subscribedOffers = json.getJSONArray("subscribedOffers")
            if (subscribedOffers.length() == 0) {
                Log.e(TAG, "Keine subscribedOffers")
                return@withContext null
            }

            val offer = subscribedOffers.getJSONObject(0)
            val pack = offer.getJSONArray("pack")
            var remainingKb = 0L

            for (i in 0 until pack.length()) {
                val p = pack.getJSONObject(i)
                if (p.optString("balanceAttributeReference") == "dataGrantAmount") {
                    remainingKb = p.optLong("allocated", 0) - p.optLong("used", 0)
                }
            }

            val remainingMb = remainingKb / 1024.0
            Log.d(TAG, "Verbleibend: ${"%.1f".format(remainingMb)} MB")

            DataStatus(
                remainingMb = remainingMb,
                offerId = offer.getString("offerId"),
                subscriptionId = offer.getString("subscriptionId"),
                resourceId = offer.getString("resourceId"),
                onDemandAmount = offer.getString("onDemandAmountValueUid"),
                refillThreshold = offer.getString("refillThresholdValueUid"),
            )
        } catch (e: Exception) {
            Log.e(TAG, "Fehler bei getRemainingData", e)
            null
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tarif-/Addon-Erkennung (Surf-Ticket & Co.)
    // ──────────────────────────────────────────────────────────────────────────

    /**
     * Holt alle subscribedOffers und klassifiziert Basis-Tarif vs. Zusatzoptionen.
     *
     * Schnittstelle: `GET /scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId=…`
     * Response enthält `subscribedOffers[]`, je mit u.a.:
     *   offerId, offerName/displayName, offerType, offerSubType, status/state,
     *   subscriptionId/resourceId, onDemandAmountValueUid, refillThresholdValueUid, pack[].
     *
     * Basis-Tarif-Erkennung: mindestens ein subscribedOffer das als aktiver
     * Daten-Tarif erkennbar ist (dataGrantAmount im pack ODER offerType/offersubType
     * deutet auf Tarif hin) UND nicht als Add-on klassifiziert wurde.
     *
     * Add-on-Erkennung (Surfticket Unlimited etc.): offerName/Type enthält Keywords
     * (siehe TariffEvaluator) UND Status gilt als aktiv (nicht expired/cancelled).
     *
     * WICHTIG: Diese Methode wirft nie – sie liefert immer ein TariffStatus.
     *   * Bei HTTP-Fehler/JSON-Fehler -> null (kein Tarif-Check möglich, kein Fehlalarm).
     *   * Bei leerem subscribedOffers -> TarifStatus mit shouldWarn=true (Edge-Case: prepaid
     *     ohne gebuchten Tarif; aber nur warnen wenn Network-Guard zustimmt).
     *   * Bei vorhandenem subscribedOffers ohne Datenvolumen aber mit Status active
     *     -> Basis gilt als vorhanden (z.B. gerade aufgebraucht, aber noch im Zeitraum).
     */
    suspend fun getTariffStatus(contractId: String): TariffStatus? = withContext(Dispatchers.IO) {
        try {
            val url = ("${AuthConfig.PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff"
                    + "/selfcare-dashboard/v1/offers?contractId=$contractId").toHttpUrl()

            val request = Request.Builder()
                .url(url)
                .apply { bffHeaders().forEach { (k, v) -> header(k, v) } }
                .header("User-Agent", AuthConfig.UA)
                .get().build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                Log.e(TAG, "getTariffStatus HTTP ${response.code}")
                return@withContext null // kein Fehlalarm bei API-Fehler
            }
            val json = JSONObject(response.body!!.string())
            parseOffersToTariffStatus(json)
        } catch (e: Exception) {
            Log.e(TAG, "getTariffStatus failed", e)
            null
        }
    }

    /**
     * Reine JSON-zu-[TariffStatus] Konvertierung – isoliert für Tests.
     * Erwartet das bereits geparste JSONObject des Offers-Endpunkts.
     *
     * Fix 26.09.2026: Das echte BFF-Format war unbekannt – der erste Parser
     * prüfte nur wenige Felder (offerName/offerType/offerSubType) und forderte
     * für einen Basis-Tarif zwingend ein dataGrantAmount-Pack. Ergebnis: ein
     * gebuchtes "Surf-Ticket Unlimited" (evtl. ohne dataGrantAmount, mit
     * abweichenden Feldnamen) wurde weder als Basis noch als Add-on erkannt,
     * die App warnte fälschlich. Neuer Ansatz:
     *  1. Alle String-Felder des Offers rekursiv einsammeln (dumpOfferTexts)
     *     und gegen erweiterte Keyword-Listen prüfen.
     *  2. JEDES aktive subscribedOffer zählt als vorhandener Tarifschutz
     *     (Fallback-Regel) – auch wenn es nicht klassifizierbar ist.
     *  3. Nur wenn wirklich KEIN aktives Offer existiert, gilt shouldWarn.
     *  4. Kann nichts klassifiziert werden, obwohl aktive Offers existieren,
     *     wird uncertain=true gesetzt (keine Warnung, nur Diagnose).
     */
    fun parseOffersToTariffStatus(json: JSONObject): TariffStatus {
        val subscribedOffers = json.optJSONArray("subscribedOffers")
            ?: json.optJSONArray("offers")
            ?: json.optJSONArray("subscribed_offer")
            ?: JSONArray()

        if (subscribedOffers.length() == 0) {
            // Kein Tarif gebucht – eindeutiger Guthaben-Gefahr-Zustand
            Log.w(TAG, "TariffStatus: 0 subscribedOffers -> kein Tarif, kein Add-on")
            return TariffStatus(
                hasBaseTariff = false,
                hasActiveAddon = false,
                remainingMb = -1.0,
                rawOfferCount = 0,
                debugInfo = "0 subscribedOffers",
            )
        }

        var hasBaseTariff = false
        var hasActiveAddon = false
        val baseNames = mutableListOf<String>()
        val addonNames = mutableListOf<String>()
        val allNames = mutableListOf<String>()
        var anyActive = false
        var anyClassified = false
        var remainingMb: Double = -1.0
        var primaryStatus: DataStatus? = null
        var primaryRemainingKb = -1L

        for (i in 0 until subscribedOffers.length()) {
            val offer = subscribedOffers.optJSONObject(i) ?: continue
            val offerId = offer.optString("offerId", offer.optString("id", ""))
            val offerName = firstNonBlank(
                offer.optString("offerName", ""),
                offer.optString("displayName", ""),
                offer.optString("marketingName", ""),
                offer.optString("productName", ""),
                offer.optString("name", ""),
                offer.optString("title", ""),
                offer.optString("label", ""),
            )
            val displayName = offerName.ifEmpty { offerId.ifEmpty { "offer#$i" } }
            allNames.add(displayName)

            val status = firstNonBlank(
                offer.optString("status", ""),
                offer.optString("state", ""),
                offer.optString("offerStatus", ""),
                offer.optString("subscriptionStatus", ""),
                offer.optString("lifecycleStatus", ""),
            )
            val active = TariffEvaluator.isActiveStatus(status)
            if (active) anyActive = true

            // Gesamten Offer-Text einsammeln (alle String-Felder rekursiv) –
            // fängt auch unerwartete Feldnamen wie "marketingName" ab.
            val fullText = dumpOfferTexts(offer)
            val isAddon = TariffEvaluator.isAddonOffer(offerName, offerId, fullText)
            val looksBase = TariffEvaluator.isBaseOffer(offerName, offerId, fullText)

            Log.d(
                TAG,
                "Offer[$i] id=$offerId name=$offerName status=$status active=$active " +
                    "isAddon=$isAddon looksBase=$looksBase keys=${offer.keys().asSequence().toList()}"
            )

            // dataGrantAmount-Pack suchen (für Restvolumen + Buchungs-IDs)
            val pack = offer.optJSONArray("pack")
                ?: offer.optJSONArray("packs")
                ?: offer.optJSONArray("balances")
            var hasDataGrant = false
            var kbForThisOffer = -1L
            if (pack != null) {
                for (j in 0 until pack.length()) {
                    val p = pack.optJSONObject(j) ?: continue
                    val ref = p.optString("balanceAttributeReference",
                        p.optString("balanceType",
                            p.optString("unit", "")))
                    if (ref == "dataGrantAmount" || ref.lowercase().contains("data")) {
                        // Nur zählen wenn allocated/used vorhanden sind
                        if (p.has("allocated") || p.has("used") || p.has("remaining")) {
                            hasDataGrant = true
                            val allocated = p.optLong("allocated", p.optLong("total", 0L))
                            val used = p.optLong("used", 0L)
                            val remaining = p.optLong("remaining", allocated - used)
                            kbForThisOffer = if (p.has("remaining")) remaining else allocated - used
                            break
                        }
                    }
                }
            }

            if (isAddon) {
                anyClassified = true
                if (active) hasActiveAddon = true
                addonNames.add(if (active) displayName else "$displayName (inaktiv)")
                // Auch Add-ons können Buchungs-IDs liefern (Surf-Ticket nachbuchen)
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0) {
                    primaryRemainingKb = kbForThisOffer
                    remainingMb = kbForThisOffer / 1024.0
                    primaryStatus = buildDataStatus(offer, remainingMb)
                }
                continue
            }

            val isBaseCandidate = hasDataGrant || looksBase ||
                    fullText.lowercase().contains("data")

            if (isBaseCandidate) {
                anyClassified = true
                if (active) {
                    hasBaseTariff = true
                    baseNames.add(displayName)
                } else {
                    baseNames.add("$displayName (inaktiv)")
                }
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0) {
                    primaryRemainingKb = kbForThisOffer
                    remainingMb = kbForThisOffer / 1024.0
                    primaryStatus = buildDataStatus(offer, remainingMb)
                }
                continue
            }

            // Weder Add-on noch Basis erkennbar:
            if (active) {
                // Fallback-Regel: AKTIVES subscribedOffer = Tarifschutz vorhanden.
                // Lieber keine Warnung als einen Fehlalarm (Fix 26.09.2026).
                hasBaseTariff = true
                baseNames.add("$displayName (unklassifiziert, aktiv→Schutz)")
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0) {
                    primaryRemainingKb = kbForThisOffer
                    remainingMb = kbForThisOffer / 1024.0
                    primaryStatus = buildDataStatus(offer, remainingMb)
                }
            } else {
                baseNames.add("$displayName (inaktiv, unklassifiziert)")
            }
        }

        // Unsicher-Fall: aktive Offers vorhanden, aber nichts klassifizierbar
        // (sollte durch die Fallback-Regel oben nicht mehr auftreten – dient als
        // Sicherheitsnetz, falls sich das Format künftig ändert).
        val uncertain = anyActive && !anyClassified && !hasBaseTariff && !hasActiveAddon

        val debug = "offers=${subscribedOffers.length()} base=$hasBaseTariff addon=$hasActiveAddon " +
                "uncertain=$uncertain baseNames=$baseNames addonNames=$addonNames " +
                "remaining=${if (remainingMb >= 0) "%.1f".format(remainingMb) else "?"}MB"

        Log.d(TAG, "TariffStatus -> $debug")

        return TariffStatus(
            hasBaseTariff = hasBaseTariff,
            hasActiveAddon = hasActiveAddon,
            remainingMb = remainingMb,
            baseOfferNames = baseNames.toList(),
            addonOfferNames = addonNames.toList(),
            rawOfferCount = subscribedOffers.length(),
            debugInfo = debug,
            primaryDataStatus = primaryStatus,
            uncertain = uncertain,
            allOfferNames = allNames.toList(),
        )
    }

    /** Erste nicht-leere Zeichenkette aus der Liste. */
    private fun firstNonBlank(vararg values: String): String =
        values.firstOrNull { it.isNotBlank() } ?: ""

    /**
     * Sammelt alle String-/Zahlen-/Boolean-Werte eines Offer-JSON rekursiv
     * (max. Tiefe 3) zu einem durchsuchbaren Text. Fängt Feldnamen ab, die
     * der Parser nicht explizit kennt (z.B. marketingName, productName).
     */
    private fun dumpOfferTexts(obj: JSONObject, depth: Int = 0): String {
        if (depth > 3) return ""
        val sb = StringBuilder()
        val keys = obj.keys()
        while (keys.hasNext()) {
            val k = keys.next()
            // Technische IDs/Correlations-IDs verwässern nur die Suche
            if (k.equals("subscriptionId", true) || k.equals("resourceId", true) ||
                k.equals("contractId", true) || k.lowercase().contains("correlation") ||
                k.lowercase().contains("transaction")
            ) continue
            when (val v = obj.opt(k)) {
                is JSONObject -> sb.append(' ').append(dumpOfferTexts(v, depth + 1))
                is JSONArray -> {
                    for (idx in 0 until minOf(v.length(), 20)) {
                        val el = v.opt(idx)
                        when (el) {
                            is JSONObject -> sb.append(' ').append(dumpOfferTexts(el, depth + 1))
                            else -> if (el != null) sb.append(' ').append(el.toString())
                        }
                    }
                }
                else -> if (v != null && v.toString().isNotBlank()) {
                    sb.append(' ').append(k).append(':').append(v.toString())
                }
            }
        }
        return sb.toString()
    }

    /** Baut ein DataStatus für die Auto-Buchung, oder null wenn IDs fehlen. */
    private fun buildDataStatus(offer: JSONObject, remainingMb: Double): DataStatus? {
        return try {
            DataStatus(
                remainingMb = remainingMb,
                offerId = offer.optString("offerId", offer.optString("id", "")),
                subscriptionId = offer.optString("subscriptionId", ""),
                resourceId = offer.optString("resourceId", ""),
                onDemandAmount = offer.optString("onDemandAmountValueUid", ""),
                refillThreshold = offer.optString("refillThresholdValueUid", ""),
            )
        } catch (_: Exception) { null }
    }

    /** Book 1 GB additional data. */
    suspend fun book1Gb(status: DataStatus): BookingResult = withContext(Dispatchers.IO) {
        try {
            val bodyJson = JSONObject().apply {
                put("offerId", status.offerId)
                put("subscriptionId", status.subscriptionId)
                put("updateOfferResourceID", status.resourceId)
                put("amount", status.onDemandAmount)
                put("refillThresholdValue", status.refillThreshold)
            }

            val url = ("${AuthConfig.PORTAL}/scs/bff/scs-209-selfcare-dashboard-bff"
                    + "/selfcare-dashboard/v1/offer/updateUnlimited").toHttpUrl()

            val request = Request.Builder()
                .url(url)
                .apply { bffHeaders().forEach { (k, v) -> header(k, v) } }
                .header("User-Agent", AuthConfig.UA)
                .header("Content-Type", "application/json")
                .post(bodyJson.toString().toRequestBody("application/json".toMediaType()))
                .build()

            val response = client.newCall(request).execute()
            val respBody = response.body?.string() ?: "{}"
            val respJson = JSONObject(respBody)
            val isUpdated = respJson.optBoolean("isUpdated", false)

            Log.d(TAG, "Booking: ${response.code}, isUpdated=$isUpdated")
            BookingResult(
                success = response.isSuccessful && isUpdated,
                isUpdated = isUpdated,
                statusCode = response.code,
                message = respBody,
            )
        } catch (e: Exception) {
            Log.e(TAG, "Fehler bei book1Gb", e)
            BookingResult(false, false, -1, e.message ?: "Unbekannter Fehler")
        }
    }
}
