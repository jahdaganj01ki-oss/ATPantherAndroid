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
     */
    fun parseOffersToTariffStatus(json: JSONObject): TariffStatus {
        val subscribedOffers = json.optJSONArray("subscribedOffers")
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
        var remainingMb: Double = -1.0
        var primaryStatus: DataStatus? = null
        var primaryRemainingKb = -1L

        for (i in 0 until subscribedOffers.length()) {
            val offer = subscribedOffers.getJSONObject(i)
            val offerId = offer.optString("offerId", "")
            val offerName = offer.optString("offerName",
                offer.optString("displayName",
                    offer.optString("name", "")))
            val offerType = offer.optString("offerType", offer.optString("type", ""))
            val offerSubType = offer.optString("offerSubType", offer.optString("subType", ""))
            val status = offer.optString("status", offer.optString("state", offer.optString("offerStatus", "")))
            val active = TariffEvaluator.isActiveStatus(status)

            val isAddon = TariffEvaluator.isAddonOffer(
                offerName.takeIf { it.isNotEmpty() },
                offerId.takeIf { it.isNotEmpty() },
                offerType.takeIf { it.isNotEmpty() },
                offerSubType.takeIf { it.isNotEmpty() },
            )

            Log.d(TAG, "Offer[$i] id=$offerId name=$offerName type=$offerType subType=$offerSubType status=$status active=$active isAddon=$isAddon")

            if (isAddon) {
                if (active) {
                    hasActiveAddon = true
                }
                addonNames.add(offerName.ifEmpty { offerId })
                continue
            }

            // Alles Nicht-Add-on => potenzieller Basis-Tarif, wenn es Daten-Packs enthält
            // oder offerType auf Tarif hindeutet und Status aktiv ist.
            val pack = offer.optJSONArray("pack")
            var hasDataGrant = false
            var kbForThisOffer = -1L
            if (pack != null) {
                for (j in 0 until pack.length()) {
                    val p = pack.getJSONObject(j)
                    if (p.optString("balanceAttributeReference") == "dataGrantAmount") {
                        hasDataGrant = true
                        val allocated = p.optLong("allocated", 0L)
                        val used = p.optLong("used", 0L)
                        kbForThisOffer = allocated - used
                        break
                    }
                }
            }

            val isBaseCandidate = hasDataGrant || offerType.lowercase().contains("data")
                    || offerSubType.lowercase().contains("data")

            if (isBaseCandidate && active) {
                hasBaseTariff = true
                baseNames.add(offerName.ifEmpty { offerId })
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0) {
                    primaryRemainingKb = kbForThisOffer
                    remainingMb = kbForThisOffer / 1024.0
                    // DataStatus für Kompatibilität mit book1Gb
                    primaryStatus = try {
                        DataStatus(
                            remainingMb = remainingMb,
                            offerId = offer.getString("offerId"),
                            subscriptionId = offer.getString("subscriptionId"),
                            resourceId = offer.getString("resourceId"),
                            onDemandAmount = offer.optString("onDemandAmountValueUid", ""),
                            refillThreshold = offer.optString("refillThresholdValueUid", ""),
                        )
                    } catch (_: Exception) { null }
                }
            } else if (isBaseCandidate && !active) {
                // Abgelaufener Basis-Tarif zählt als inaktiv – nicht als aktiver Tarif
                baseNames.add("${offerName.ifEmpty { offerId }} (expired)")
            }
        }

        val debug = "offers=${subscribedOffers.length()} base=$hasBaseTariff addon=$hasActiveAddon " +
                "baseNames=$baseNames addonNames=$addonNames remaining=${if (remainingMb >= 0) "%.1f".format(remainingMb) else "?"}MB"

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
        )
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
