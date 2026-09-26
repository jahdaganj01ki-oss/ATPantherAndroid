package com.alditalk.panther.warning

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.telephony.TelephonyManager
import android.util.Log

/**
 * Prüft ob eine Warnung überhaupt relevant ist.
 *
 * Edge-Case "WLAN":
 *  - Wenn das Gerät nur per WLAN verbunden ist und kein mobiler Transport
 *    aktiv ist, kann kein Guthaben über mobile Daten verbraucht werden → keine Warnung.
 *  - Wenn WLAN + mobil gleichzeitig aktiv sind (z.B. WLAN-Schwäche) oder nur mobil
 *    aktiv ist → Warnung darf auslösen.
 *  - Wenn mobile Daten systemweit deaktiviert sind → keine Warnung.
 */
object NetworkStateHelper {

    private const val TAG = "NetworkStateHelper"

    fun isWifiConnected(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                val network = cm.activeNetwork ?: return false
                val caps = cm.getNetworkCapabilities(network) ?: return false
                caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
            } else {
                @Suppress("DEPRECATION")
                cm.activeNetworkInfo?.let { it.type == ConnectivityManager.TYPE_WIFI && it.isConnected } ?: false
            }
        } catch (e: Exception) {
            Log.w(TAG, "isWifiConnected failed", e)
            false
        }
    }

    fun isMobileConnected(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                // Prüfe alle Netzwerke, nicht nur activeNetwork – bei WLAN ist activeNetwork WIFI,
                // aber CELLULAR kann parallel verbunden sein (z.B. für MMS/IMS).
                val networks = cm.allNetworks
                for (nw in networks) {
                    val caps = cm.getNetworkCapabilities(nw) ?: continue
                    if (caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) &&
                        caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                    ) {
                        // NET_CAPABILITY_VALIDATED optional, aber nicht strikt erforderlich
                        return true
                    }
                }
                false
            } else {
                @Suppress("DEPRECATION")
                cm.allNetworkInfo?.any { it.type == ConnectivityManager.TYPE_MOBILE && it.isConnected } ?: false
            }
        } catch (e: Exception) {
            Log.w(TAG, "isMobileConnected failed", e)
            false
        }
    }

    fun isMobileDataEnabled(context: Context): Boolean {
        return try {
            val tm = context.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                tm?.isDataEnabled ?: true // konservativ: wenn unbekannt, nicht blocken
            } else {
                // Vor API 26 kein öffentlicher Getter – konservativ true
                true
            }
        } catch (e: Exception) {
            Log.w(TAG, "isMobileDataEnabled failed", e)
            true
        }
    }

    fun isAirplaneModeOn(context: Context): Boolean {
        return try {
            android.provider.Settings.Global.getInt(
                context.contentResolver,
                android.provider.Settings.Global.AIRPLANE_MODE_ON, 0
            ) != 0
        } catch (_: Exception) {
            false
        }
    }

    /**
     * Zentrale Entscheidung: Soll überhaupt eine Guthaben-Warnung geprüft/angezeigt werden?
     *
     * false wenn:
     *  - Flugzeugmodus an
     *  - mobile Daten systemweit aus
     *  - nur WLAN verbunden und kein mobiler Transport aktiv
     */
    fun shouldTriggerWarning(context: Context): Boolean {
        if (isAirplaneModeOn(context)) {
            Log.d(TAG, "shouldTrigger: false – Flugzeugmodus")
            return false
        }
        if (!isMobileDataEnabled(context)) {
            Log.d(TAG, "shouldTrigger: false – mobile Daten deaktiviert")
            return false
        }
        val wifi = isWifiConnected(context)
        val mobile = isMobileConnected(context)
        Log.d(TAG, "shouldTrigger: wifi=$wifi mobile=$mobile")

        // Reines WLAN ohne mobile Konnektivität → kein Guthabenrisiko
        if (wifi && !mobile) {
            Log.d(TAG, "shouldTrigger: false – nur WLAN, kein mobiler Transport")
            return false
        }
        // Alle anderen Fälle (nur mobil, oder beide, oder kein aktiver Transport aber mobile Daten an
        // – z.B. gerade im Funkloch) => Warnung zulassen. Letzteres ist konservativ korrekt,
        // da beim Wiederverbinden sofort Kosten entstehen können.
        return true
    }
}
