package com.alditalk.panther.auth

import android.util.Log
import com.alditalk.panther.util.MemoryCookieJar
import com.alditalk.panther.util.generatePkce
import com.alditalk.panther.util.sha1
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.json.JSONObject
import java.net.URL
import java.util.UUID
import java.util.concurrent.TimeUnit

private const val TAG = "AuthService"

object AuthConfig {
    const val PORTAL = "https://www.alditalk-kundenportal.de"
    const val AUTH = "https://login.alditalk-kundenbetreuung.de"
    const val CLIENT_ID = "U-621-Varnish"
    const val REDIRECT_URI = "$PORTAL/logged-in-home-page/"
    const val AUTH_EP =
        "$AUTH/signin/json/realms/alditalk/authenticate?authIndexType=service&authIndexValue=Login"
    const val UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
    const val POW_DIFFICULTY = 3
    val JSON_MEDIA = "application/json".toMediaType()
}

data class LoginResult(
    val success: Boolean,
    val client: OkHttpClient? = null,
    val error: String? = null,
)

class AuthService {

    /** SHA-1 PoW: find nonce where sha1(workUuid + nonce) starts with "0"*difficulty. */
    private fun solvePow(workUuid: String, difficulty: Int = AuthConfig.POW_DIFFICULTY): Int {
        val target = "0".repeat(difficulty)
        for (nonce in 0..10_000_000) {
            if ((workUuid + nonce).sha1().startsWith(target)) return nonce
        }
        throw RuntimeException("PoW nicht gelöst (10M Versuche)")
    }

    /** Full login: ForgeRock PoW → credentials → PKCE authorize → 5-hop redirect chain. */
    suspend fun login(phone: String, password: String): LoginResult = withContext(Dispatchers.IO) {
        val cookieJar = MemoryCookieJar()
        val client = OkHttpClient.Builder()
            .cookieJar(cookieJar)
            .followRedirects(false)
            .followSslRedirects(true)
            .connectTimeout(30, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .build()

        try {
            // ── Step 1: Get login callbacks ──
            // WICHTIG: Echter LEERER Body (0 Bytes) mit Content-Type application/json,
            // GENAU wie Python data="" . "{}" (2 Bytes) wuerde ForgeRock veranlassen,
            // die PoW-Challenge als JavaScript-Funktion statt als var-Zuweisung zu
            // liefern -> Regex findet nichts. Accept-Language wie Python-Referenz.
            val step1 = Request.Builder()
                .url(AuthConfig.AUTH_EP)
                .header("User-Agent", AuthConfig.UA)
                .header("Accept-Language", "de-DE,de;q=0.9")
                .header("Accept", "application/json")
                .post("".toRequestBody(AuthConfig.JSON_MEDIA))
                .build()

            val step1Resp = client.newCall(step1).execute()
            val step1Body = step1Resp.body?.string() ?: ""
            Log.e(TAG, "Step1: HTTP ${step1Resp.code}, Body-Laenge=${step1Body.length}, Body=${step1Body.take(800)}")
            if (!step1Resp.isSuccessful) {
                return@withContext LoginResult(false, error = "Step 1 failed: ${step1Resp.code}")
            }
            val data = JSONObject(step1Body)

            // Extract PoW params from TextOutputCallback
            var powMessage = ""
            val callbacks = data.getJSONArray("callbacks")
            Log.e(TAG, "Step1: ${callbacks.length()} Callbacks erhalten")
            for (i in 0 until callbacks.length()) {
                val cb = callbacks.getJSONObject(i)
                Log.e(TAG, "Step1: Callback[$i] type=${cb.getString("type")}")
                if (cb.getString("type") == "TextOutputCallback") {
                    val outputs = cb.getJSONArray("output")
                    for (j in 0 until outputs.length()) {
                        val o = outputs.getJSONObject(j)
                        if (o.optString("name") == "message") powMessage = o.getString("value")
                    }
                }
            }
            Log.e(TAG, "Step1: powMessage-Laenge=${powMessage.length}")
            // Bugfix: Regex mit normalem String statt raw-string, da """([^"]+)""""
            // ein zusaetzliches " am Pattern-Ende erzeugte und nie matchte.
            val workMatch = Regex("var work = \"([^\"]+)\"").find(powMessage)
            val diffMatch = Regex("var difficulty = (\\d+)").find(powMessage)
            Log.e(TAG, "Step1: workMatch=${if (workMatch != null) "OK:" + workMatch.groupValues[1] else "NULL"}, diffMatch=${if (diffMatch != null) "OK:" + diffMatch.groupValues[1] else "NULL"}")
            if (workMatch == null || diffMatch == null) {
                return@withContext LoginResult(false, error = "PoW-Parameter nicht gefunden")
            }
            val workUuid = workMatch.groupValues[1]
            val difficulty = diffMatch.groupValues[1].toInt()
            Log.e(TAG, "PoW: work=$workUuid, diff=$difficulty")
            val nonce = solvePow(workUuid, difficulty)
            Log.e(TAG, "PoW gelöst: nonce=$nonce")

            // ── Step 2: Submit credentials — ALL values as strings ──
            for (i in 0 until callbacks.length()) {
                val cb = callbacks.getJSONObject(i)
                if (cb.has("input")) {
                    val inputs = cb.getJSONArray("input")
                    for (j in 0 until inputs.length()) {
                        val inp = inputs.getJSONObject(j)
                        when (inp.optString("name")) {
                            "IDToken1" -> inp.put("value", nonce.toString())   // PoW als STRING
                            "IDToken3" -> inp.put("value", phone)
                            "IDToken4" -> inp.put("value", password)
                            "IDToken5" -> inp.put("value", "2")                // loginbtn als STRING
                        }
                    }
                }
            }

            val step2 = Request.Builder()
                .url(AuthConfig.AUTH_EP)
                .header("User-Agent", AuthConfig.UA)
                .header("Accept", "application/json")
                .header("Content-Type", "application/json")
                .post(data.toString().toRequestBody(AuthConfig.JSON_MEDIA))
                .build()

            val step2Resp = client.newCall(step2).execute()
            if (!step2Resp.isSuccessful) {
                return@withContext LoginResult(false, error = "Step 2 failed: ${step2Resp.code}")
            }
            val step2Data = JSONObject(step2Resp.body!!.string())
            val tokenId = step2Data.optString("tokenId")
            if (tokenId.isNullOrEmpty()) {
                return@withContext LoginResult(false,
                    error = "Login fehlgeschlagen: ${step2Data.toString().take(300)}")
            }

            // Set iPlanetDirectoryPro cookie
            cookieJar.saveFromResponse(
                AuthConfig.AUTH_EP.toHttpUrl(),
                listOf(okhttp3.Cookie.Builder()
                    .name("iPlanetDirectoryPro")
                    .value(tokenId)
                    .domain("login.alditalk-kundenbetreuung.de")
                    .path("/")
                    .build())
            )
            Log.e(TAG, "TokenID erhalten, Cookie gesetzt")

            // ── Step 3: OAuth2 Authorize with PKCE ──
            val pkce = generatePkce()
            val state = UUID.randomUUID().toString().replace("-", "")
            val nonceParam = UUID.randomUUID().toString().replace("-", "")

            val authUrl = "${AuthConfig.AUTH}/signin/oauth2/authorize".toHttpUrl().newBuilder()
                .addQueryParameter("client_id", AuthConfig.CLIENT_ID)
                .addQueryParameter("response_type", "code")
                .addQueryParameter("scope", "openid")
                .addQueryParameter("redirect_uri", AuthConfig.REDIRECT_URI)
                .addQueryParameter("code_challenge", pkce.codeChallenge)
                .addQueryParameter("code_challenge_method", "S256")
                .addQueryParameter("nonce", nonceParam)
                .addQueryParameter("state", state)
                .addQueryParameter("ui_locales", "de")
                .addQueryParameter("acr_values", "password")
                .addQueryParameter("prompt", "none")
                .addQueryParameter("realm", "/alditalk")
                .build()

            val authReq = Request.Builder()
                .url(authUrl)
                .header("User-Agent", AuthConfig.UA)
                .get()
                .build()

            val authResp = client.newCall(authReq).execute()
            val location = authResp.headers["Location"]
            if (location.isNullOrEmpty()) {
                return@withContext LoginResult(false, error = "Kein Location-Header im OAuth-Response")
            }
            Log.e(TAG, "OAuth2 → ${location.take(80)}...")

            // ── Step 4: Follow redirect chain manually (up to 8 hops) ──
            // Use a nullable var guarded by the while-condition so Kotlin smart
            // casts `nextUrl` to non-null inside the body (a plain `var currentUrl
            // = location` would infer String? even though we null-checked above).
            var nextUrl: String? = location
            // Basis-URL fuer relative Redirects: mit jedem Hop aktualisieren,
            // damit Location-Header wie '/user/...' gegen die korrekte Domain
            // aufgeloest werden (vorher immer gegen die urspruengliche authUrl).
            var baseUrl: String = authUrl.toString()
            var hop = 0
            while (nextUrl != null && hop < 8) {
                val resolved = resolveUrl(nextUrl, baseUrl)
                Log.e(TAG, "Hop $hop: nextUrl='$nextUrl' -> resolved='${resolved.take(120)}'")
                val hopReq = Request.Builder()
                    .url(resolved)
                    .header("User-Agent", AuthConfig.UA)
                    .get()
                    .build()
                val hopResp = client.newCall(hopReq).execute()
                val locHdr = hopResp.headers["Location"]
                Log.e(TAG, "Hop $hop → ${hopResp.code} ${resolved.take(60)} | Location=$locHdr")

                if (hopResp.code in 301..308) {
                    hopResp.close()
                    if (locHdr.isNullOrEmpty()) {
                        return@withContext LoginResult(false, error = "Hop $hop: kein Location")
                    }
                    nextUrl = locHdr
                    baseUrl = resolved  // Basis fuer naechsten Hop aktualisieren
                } else {
                    hopResp.close()
                    Log.e(TAG, "Redirect-Kette abgeschlossen nach $hop Hops")
                    break
                }
                hop++
            }

            // New client that follows redirects normally for API calls
            val apiClient = client.newBuilder()
                .followRedirects(true)
                .followSslRedirects(true)
                .build()

            LoginResult(success = true, client = apiClient)

        } catch (e: Exception) {
            Log.e(TAG, "Login-Fehler", e)
            LoginResult(false, error = e.message ?: "Unbekannter Fehler")
        }
    }

    /** Resolve a possibly-relative URL against a base.
     *  ForgeRock/AldiTalk liefert Redirects wie '//user/auth/account-overview/'
     *  Obwohl das nach RFC protokoll-relativ (Host='user') waere, ist 'user' hier
     *  ein Pfad-Segment der Basis-Domain. Also '//x' -> 'https://<basis-host>/x'.
     */
    private fun resolveUrl(possiblyRelative: String, base: String): String {
        if (possiblyRelative.startsWith("http://") || possiblyRelative.startsWith("https://")) {
            return possiblyRelative
        }
        // '//user/...' -> 'https://<basis-host>/user/...' (Pfad, nicht Host)
        if (possiblyRelative.startsWith("//")) {
            val baseHost = URL(base).host
            return "https://$baseHost/${possiblyRelative.substring(2)}"
        }
        return URL(URL(base), possiblyRelative).toString()
    }
}
