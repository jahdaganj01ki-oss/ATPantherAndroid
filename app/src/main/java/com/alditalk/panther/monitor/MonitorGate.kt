package com.alditalk.panther.monitor

import android.content.Context
import android.provider.Settings
import android.util.Log
import com.alditalk.panther.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/** Ergebniszustand der Freigabe-Abfrage. */
enum class GateStatus {
    /** Dieses Geraet darf das Portal abfragen. */
    ALLOWED,

    /** Ein anderes Geraet haelt die Freigabe -> Bereitschaftsmodus. */
    NOT_OWNER,

    /** Niemand haelt die Freigabe (oder sie ist abgelaufen) -> "Uebernehmen" noetig. */
    FREE,

    /** In der App ist keine Worker-URL eingetragen. */
    NOT_CONFIGURED,

    /** Freigabe-Server nicht erreichbar, kein brauchbarer Cache. */
    UNREACHABLE,

    /** Unerwarteter Fehler. */
    ERROR,
}

/** Ergebnis einer Freigabe-Abfrage – [allowed] entscheidet ueber den Portal-Kontakt. */
data class GateResult(
    val allowed: Boolean,
    val status: GateStatus,
    val owner: String?,
    val detail: String,
)

/**
 * Zentrale Monitor-Freigabe: nur der Inhaber der Lease darf das
 * ALDI-Talk-Portal abfragen. Verhindert, dass mehrere Varianten
 * (Windows, Ulefone, Moto) parallel pollen und das Konto dadurch gesperrt wird.
 *
 * Ablauf:
 *  - [evaluate] wird vor dem Login und vor jedem Poll aufgerufen. Intern
 *    gibt es hoechstens alle [CHECK_INTERVAL_MS] einen Netzabruf; dazwischen
 *    entscheidet der Cache. Das halt den Zusatzverkehr bei ~12 Abrufen/Std.
 *  - Der Inhaber verlaengert seine Lease bei jedem Abruf um [TTL_SECONDS].
 *  - Faellt ein Geraet aus, laeuft die Lease ab und ein anderes Geraet darf
 *    automatisch uebernehmen – ohne Zutun auf dem alten Geraet.
 *  - [claim] verdraengt sofort ("Uebernehmen"-Button). Das alte Geraet
 *    bemerkt das beim naechsten Abruf und geht selbst in Bereitschaft.
 *
 * Ausfallverhalten: fail-closed (Server nicht erreichbar = nicht abfragen).
 * Der letzte bekannte Stand wird bis [CACHE_GRACE_MS] weiterverwendet, damit
 * ein kurzer Netzausfall den Monitor nicht sofort anhaelt. Mit
 * [failOpen] = true wird bewusst "weiterlaufen" gewaehlt.
 *
 * Diese Datei ist in allen Android-Varianten identisch – die Variante selbst
 * kommt aus der Ressource `R.string.monitor_variant_id`.
 */
class MonitorGate(context: Context) {

    companion object {
        private const val TAG = "MonitorGate"

        /** Lease-Laenge. Muss groesser als [CHECK_INTERVAL_MS] sein. */
        const val TTL_SECONDS = 900

        /** Hoechstens ein Netzabruf in diesem Zeitraum (Cache-Fenster). */
        const val CHECK_INTERVAL_MS = 5 * 60 * 1000L

        /** Wie lange der letzte bekannte Stand bei Server-Ausfall gilt. */
        const val CACHE_GRACE_MS = 30 * 60 * 1000L

        private const val PREFS = "at_panther_lock"
        private const val PREF_URL = "worker_url"
        private const val PREF_TOKEN = "worker_token"
        private const val PREF_FAIL_OPEN = "fail_open"
        private const val PREF_CACHED = "cached_state"
        private const val PREF_CACHED_AT = "cached_at"
        private const val FALLBACK_VARIANT = "unknown"

        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        /**
         * Eigenes OkHttp-Client: kurze Timeouts, damit ein haengender
         * Freigabe-Server den Monitor nicht ausbremst. Kein Cookie- und kein
         * Proxy-Fehler: der Aufruf geht an einen festen Endpunkt.
         */
        private val client: OkHttpClient by lazy {
            OkHttpClient.Builder()
                .connectTimeout(6, TimeUnit.SECONDS)
                .readTimeout(10, TimeUnit.SECONDS)
                .callTimeout(15, TimeUnit.SECONDS)
                .retryOnConnectionFailure(false)
                .build()
        }
    }

    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** Varianten-ID aus den Ressourcen – pro Variante unterschiedlich. */
    val variantId: String = try {
        appContext.getString(R.string.monitor_variant_id).ifBlank { FALLBACK_VARIANT }
    } catch (e: Exception) {
        FALLBACK_VARIANT
    }

    /**
     * Geraete-ID innerhalb der Variante. Zusammen mit [variantId] eindeutig –
     * ein zweites Geraet derselben Variante gilt als fremd.
     */
    val deviceId: String = "$variantId-" +
        (Settings.Secure.getString(appContext.contentResolver, Settings.Secure.ANDROID_ID)
            ?: "noandroidid")
            .replace(Regex("[^A-Za-z0-9]"), "")
            .take(8)

    /** URL des Cloudflare Workers, z. B. `https://at-panther-lock.<name>.workers.dev`. */
    var workerUrl: String
        get() = normalizeUrl(prefs.getString(PREF_URL, "") ?: "")
        set(value) = prefs.edit().putString(PREF_URL, normalizeUrl(value)).apply()

    /** Optionaler Token, falls im Worker `LOCK_TOKEN` gesetzt ist. */
    var workerToken: String
        get() = (prefs.getString(PREF_TOKEN, "") ?: "").trim()
        set(value) = prefs.edit().putString(PREF_TOKEN, value.trim()).apply()

    /** true = bei Server-Ausfall weiter abfragen (nicht empfohlen). */
    var failOpen: Boolean
        get() = prefs.getBoolean(PREF_FAIL_OPEN, false)
        set(value) = prefs.edit().putBoolean(PREF_FAIL_OPEN, value).apply()

    fun isConfigured(): Boolean = workerUrl.isNotEmpty()

    private fun normalizeUrl(raw: String): String {
        val trimmed = raw.trim().trimEnd('/')
        if (!trimmed.startsWith("http://") && !trimmed.startsWith("https://")) return ""
        return trimmed
    }

    private data class CachedState(
        val owner: String?,
        val deviceId: String?,
        val expiresAt: Long,
        val at: Long,
    )

    // ── Cache ──────────────────────────────────────────────────────────────

    private fun readCache(): CachedState? {
        val raw = prefs.getString(PREF_CACHED, null) ?: return null
        return try {
            val o = JSONObject(raw)
            CachedState(
                owner = if (o.isNull("owner")) null else o.optString("owner").ifBlank { null },
                deviceId = if (o.isNull("deviceId")) null else o.optString("deviceId").ifBlank { null },
                expiresAt = o.optLong("expiresAt", 0L),
                at = prefs.getLong(PREF_CACHED_AT, 0L),
            )
        } catch (e: Exception) {
            Log.w(TAG, "Freigabe-Cache unlesbar, wird ignoriert", e)
            null
        }
    }

    private fun writeCache(state: CachedState) {
        val json = JSONObject()
            .put("owner", state.owner ?: JSONObject.NULL)
            .put("deviceId", state.deviceId ?: JSONObject.NULL)
            .put("expiresAt", state.expiresAt)
        prefs.edit()
            .putString(PREF_CACHED, json.toString())
            .putLong(PREF_CACHED_AT, state.at)
            .apply()
    }

    // ── Entscheidung ───────────────────────────────────────────────────────

    /**
     * Rein lokale Entscheidung aus einem Lock-Stand. Auch der Windows-Port und
     * der Test in `worker/test/lease-sim.mjs` bilden genau diese Faelle ab.
     */
    private fun decide(state: CachedState, now: Long): GateResult {
        val mine = state.deviceId == deviceId && state.expiresAt > now
        return when {
            mine -> GateResult(true, GateStatus.ALLOWED, variantId, "Freigabe aktiv: $variantId")
            state.owner == null ->
                GateResult(false, GateStatus.FREE, null, "Freigabe ist frei – Übernehmen tippen")
            state.expiresAt <= now ->
                GateResult(false, GateStatus.FREE, state.owner,
                    "Freigabe von ${state.owner} abgelaufen – Übernehmen tippen")
            else ->
                GateResult(false, GateStatus.NOT_OWNER, state.owner,
                    "Bereitschaft: ${state.owner} fragt ab")
        }
    }

    /**
     * Freigabe pruefen. Wird vor dem Login und vor jedem Poll aufgerufen.
     *
     * @param force true = Cache ignorieren (z. B. UI-Aktualisierung)
     * @param autoClaim true = freie/abgelaufene Freigabe sofort uebernehmen
     *                   (das ist der Normalfall beim Start des Monitors)
     */
    suspend fun evaluate(force: Boolean = false, autoClaim: Boolean = true): GateResult {
        val url = workerUrl
        if (url.isEmpty()) return notConfigured()

        val now = System.currentTimeMillis()
        val cache = readCache()
        if (!force && cache != null && now - cache.at < CHECK_INTERVAL_MS) {
            val cached = decide(cache, now)
            // Freigegeben genuegt der Cache. Nicht freigegeben mit autoClaim
            // braucht einen echten Aufruf – nur der Server kann die Lease setzen.
            if (cached.allowed || !autoClaim) return cached
        }
        return sync(url, claimIfFree = autoClaim, steal = false)
    }

    /** "Übernehmen": verdraengt einen anderen Inhaber sofort. */
    suspend fun claim(): GateResult {
        val url = workerUrl
        if (url.isEmpty()) return notConfigured()
        return sync(url, claimIfFree = true, steal = true)
    }

    /** "Freigeben": nur der Inhaber gibt seine Lease ab. */
    suspend fun release(): GateResult {
        val url = workerUrl
        if (url.isEmpty()) return notConfigured()
        return try {
            val json = post(url, "/release", JSONObject().put("deviceId", deviceId).toString())
            val state = parseState(json) ?: return unreachable()
            writeCache(state)
            GateResult(false, GateStatus.FREE, state.owner, "Freigabe abgegeben")
        } catch (e: Exception) {
            Log.w(TAG, "Freigabe abgeben fehlgeschlagen: ${e.message}")
            unreachable()
        }
    }

    /** Sofortiger UI-Stand aus dem Cache – ohne Netzwerk, daher nie blockierend. */
    fun cachedStatus(): GateResult {
        if (!isConfigured()) return notConfigured()
        val cache = readCache() ?: return GateResult(
            false, GateStatus.FREE, null, "Freigabe noch nicht abgefragt"
        )
        return decide(cache, System.currentTimeMillis())
    }

    // ── Netzwerk ───────────────────────────────────────────────────────────

    private suspend fun sync(url: String, claimIfFree: Boolean, steal: Boolean): GateResult {
        val body = JSONObject()
            .put("deviceId", deviceId)
            .put("variant", variantId)
            .put("ttlSeconds", TTL_SECONDS)
            .put("claimIfFree", claimIfFree)
            .put("steal", steal)
        return try {
            val json = post(url, "/sync", body.toString())
            val state = parseState(json) ?: return unreachable()
            writeCache(state)
            val result = decide(state, System.currentTimeMillis())
            if (steal && result.allowed) {
                result.copy(detail = "Freigabe übernommen: $variantId")
            } else if (steal) {
                GateResult(false, result.status, result.owner,
                    "Übernehmen fehlgeschlagen – ${result.owner ?: "unbekannt"} ist aktiv")
            } else {
                result
            }
        } catch (e: Exception) {
            Log.w(TAG, "Freigabe-Server nicht erreichbar: ${e.message}")
            unreachable()
        }
    }

    private suspend fun post(url: String, path: String, body: String): JSONObject =
        withContext(Dispatchers.IO) {
            val builder = Request.Builder()
                .url(url + path)
                .header("Cache-Control", "no-store")
                .post(body.toRequestBody(JSON_MEDIA))
            val token = workerToken
            if (token.isNotEmpty()) builder.header("X-Lock-Token", token)

            client.newCall(builder.build()).execute().use { response ->
                val text = response.body?.string().orEmpty()
                if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
                JSONObject(text)
            }
        }

    private fun parseState(json: JSONObject): CachedState? {
        if (!json.optBoolean("ok", false)) return null
        val s = json.optJSONObject("state") ?: return null
        return CachedState(
            owner = if (s.isNull("owner")) null else s.optString("owner").ifBlank { null },
            deviceId = if (s.isNull("deviceId")) null else s.optString("deviceId").ifBlank { null },
            expiresAt = s.optLong("expiresAt", 0L),
            at = System.currentTimeMillis(),
        )
    }

    // ── Ausfallbehandlung ──────────────────────────────────────────────────

    private fun notConfigured() = GateResult(
        false, GateStatus.NOT_CONFIGURED, null,
        "Keine Freigabe-URL eingetragen – unten bei „Freigabe“ eintragen"
    )

    private fun unreachable(): GateResult {
        if (failOpen) {
            return GateResult(
                true, GateStatus.ERROR, readCache()?.owner,
                "⚠ Freigabe-Server nicht erreichbar – Not-Aus-Modus (fail-open) ist aktiv"
            )
        }
        val cache = readCache()
        val now = System.currentTimeMillis()
        if (cache != null && now - cache.at < CACHE_GRACE_MS) {
            val decided = decide(cache, now)
            return decided.copy(
                detail = decided.detail + " (Server nicht erreichbar, Stand von vor ${ago(now - cache.at)})"
            )
        }
        return GateResult(
            false, GateStatus.UNREACHABLE, cache?.owner,
            "Freigabe-Server nicht erreichbar – Monitor bleibt gesperrt (fail-closed)"
        )
    }

    private fun ago(ms: Long): String {
        val min = ms / 60_000
        return if (min < 1) "weniger als 1 min" else "$min min"
    }
}
