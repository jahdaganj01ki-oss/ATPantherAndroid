package com.alditalk.panther.warning

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.util.Log
import androidx.core.app.NotificationCompat
import com.alditalk.panther.MainActivity
import com.alditalk.panther.R

/**
 * Zentrale Stelle für die Guthaben-Warnung:
 *   1. Heads-Up Notification (High Priority) mit denselben zwei Actions wie der Dialog
 *   2. Full-Screen/Dialog-Intent für den On-Screen Dialog
 *
 * Edge-Cases:
 *  - WLAN-only      -> keine Warnung (siehe NetworkStateHelper)
 *  - Cooldown/Snooze -> keine Spam-Warnungen
 *  - Flugmodus / mobile Daten aus -> keine Warnung
 *  - Ohne POST_NOTIFICATIONS-Permission (Android 13+) -> nur Dialog versuchen
 */
object NoTariffWarningManager {

    private const val TAG = "NoTariffWarning"

    const val CHANNEL_ID_NOSTOCK = "at_panther_no_tariff_warning"
    const val NOTIFICATION_ID_NO_TARIFF = 100

    const val ACTION_CONTINUE = "com.alditalk.panther.WARNING_CONTINUE"
    const val ACTION_DISABLE_DATA = "com.alditalk.panther.WARNING_DISABLE_DATA"
    const val ACTION_SHOW_DIALOG = "com.alditalk.panther.WARNING_SHOW_DIALOG"

    const val WARNING_TEXT = "Aktuell ist kein Datentarif gebucht. Verbrauch von Datenvolumen kostet jetzt direkt Guthaben. " +
            "Möchten Sie weiterhin Internet nutzen oder das Internet abschalten?"

    const val NOTIFICATION_TITLE = "⚠ Kein Datentarif aktiv"
    const val NOTIFICATION_TEXT_SHORT = "Datenverbrauch kostet jetzt direkt Guthaben!"

    fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val mgr = context.getSystemService(NotificationManager::class.java) ?: return
        val channel = NotificationChannel(
            CHANNEL_ID_NOSTOCK,
            context.getString(R.string.channel_no_tariff_warning_name),
            NotificationManager.IMPORTANCE_HIGH // Heads-Up + Ton/Vibration
        ).apply {
            description = context.getString(R.string.channel_no_tariff_warning_description)
            enableVibration(true)
            setShowBadge(true)
        }
        mgr.createNotificationChannel(channel)
    }

    /**
     * Prüft alle Vorbedingungen und zeigt ggf. Notification + Dialog.
     *
     * @return true wenn eine Warnung angezeigt wurde, false wenn unterdrückt
     */
    fun maybeWarn(context: Context, status: TariffStatus): Boolean {
        Log.d(TAG, "maybeWarn status=$status")

        // 1) Tarif-Bedingung: nur wenn wirklich kein Tarif & kein Add-on
        if (!status.shouldWarn) {
            Log.d(TAG, "maybeWarn: Tarif vorhanden -> clear throttle + cancel notification")
            WarningPrefs.clearIfTariffActive(context)
            // Falls vorher eine Warnung hing, zurückziehen
            try {
                context.getSystemService(NotificationManager::class.java)?.cancel(NOTIFICATION_ID_NO_TARIFF)
            } catch (_: Exception) {}
            return false
        }

        // 2) Netzwerk-Guard
        if (!NetworkStateHelper.shouldTriggerWarning(context)) {
            Log.d(TAG, "maybeWarn: Network guard blockiert Warnung")
            return false
        }

        // 3) Cooldown / Snooze
        if (!WarningPrefs.canShowWarning(context, status)) {
            Log.d(TAG, "maybeWarn: Cooldown/Snooze blockiert Warnung (${WarningPrefs.getStateForDebug(context)})")
            return false
        }

        Log.w(TAG, "maybeWarn: ZEIGE Warnung (${status.debugInfo})")
        showNotification(context, status)
        showDialog(context, status)

        WarningPrefs.recordWarningShown(context, status)
        return true
    }

    fun showNotification(context: Context, status: TariffStatus = TariffStatus.error("")) {
        ensureChannel(context)

        val nm = context.getSystemService(NotificationManager::class.java)
        if (nm == null) {
            Log.w(TAG, "NotificationManager null")
            return
        }
        // Auf Android 13+ ohne Permission knallt notify() nicht, aber wir schalten es still
        // und lassen wenigstens den Dialog wirken.
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                if (!nm.areNotificationsEnabled()) {
                    Log.w(TAG, "Notifications disabled system-wide")
                }
            } catch (_: Exception) {}
        }

        val continueIntent = Intent(context, WarningActionReceiver::class.java).apply { action = ACTION_CONTINUE }
        val disableIntent = Intent(context, WarningActionReceiver::class.java).apply { action = ACTION_DISABLE_DATA }
        // Fullscreen/Dialog-Intent
        val dialogIntent = Intent(context, WarningDialogActivity::class.java).apply {
            action = ACTION_SHOW_DIALOG
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            putExtra("debugInfo", status.debugInfo)
        }

        val flags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE

        val piContinue = PendingIntent.getBroadcast(context, 101, continueIntent, flags)
        val piDisable = PendingIntent.getBroadcast(context, 102, disableIntent, flags)
        val piDialog = PendingIntent.getActivity(context, 103, dialogIntent, flags)
        // Tap auf Notification -> Dialog öffnen
        val contentIntent = piDialog

        val style = NotificationCompat.BigTextStyle().bigText(WARNING_TEXT)

        val notification = NotificationCompat.Builder(context, CHANNEL_ID_NOSTOCK)
            .setContentTitle(NOTIFICATION_TITLE)
            .setContentText(NOTIFICATION_TEXT_SHORT)
            .setStyle(style)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setColor(context.getColor(R.color.primary))
            .setPriority(NotificationCompat.PRIORITY_MAX) // Heads-Up Trigger
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(false) // bleibt bis Nutzer handelt oder Tarif wieder da ist
            .setOngoing(false)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setContentIntent(contentIntent)
            .setFullScreenIntent(piDialog, true) // Heads-Up / Fullscreen auf manchen OEMs
            .setOnlyAlertOnce(false)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Weiterhin nutzen", piContinue)
            .addAction(android.R.drawable.ic_dialog_info, "Internet abschalten", piDisable)
            .build()

        try {
            nm.notify(NOTIFICATION_ID_NO_TARIFF, notification)
            Log.d(TAG, "Notification posted id=$NOTIFICATION_ID_NO_TARIFF")
        } catch (e: SecurityException) {
            Log.w(TAG, "notify SecurityException (POST_NOTIFICATIONS fehlt?)", e)
        } catch (e: Exception) {
            Log.w(TAG, "notify failed", e)
        }
    }

    fun showDialog(context: Context, status: TariffStatus) {
        // Dialog als Activity (kein SYSTEM_ALERT_WINDOW nötig, funktioniert auch im Hintergrund
        // über Notification-Tap und direkten Start).
        // Wir starten die Activity; wenn App im Vordergrund ist, wirkt es wie ein Overlay.
        try {
            val intent = Intent(context, WarningDialogActivity::class.java).apply {
                action = ACTION_SHOW_DIALOG
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
                putExtra("debugInfo", status.debugInfo)
            }
            context.startActivity(intent)
        } catch (e: Exception) {
            Log.w(TAG, "showDialog startActivity failed", e)
        }
    }

    fun dismiss(context: Context) {
        try {
            context.getSystemService(NotificationManager::class.java)?.cancel(NOTIFICATION_ID_NO_TARIFF)
        } catch (_: Exception) {}
    }

    /**
     * Öffnet die System-Seite für mobile Daten / Netzwerkeinstellungen.
     * Direktes programatisches Abschalten ist ab Android 10+ ohne MODIFY_PHONE_STATE
     * (nur System-Apps) nicht mehr möglich; daher leiten wir weiter.
     */
    fun openMobileDataSettings(context: Context) {
        val intents = listOf(
            // 1) Direkte mobile Netzwerkeinstellungen (funktioniert auf Ulefone X11Pro / Stock Android)
            Intent(Settings.ACTION_DATA_ROAMING_SETTINGS),
            Intent(Settings.ACTION_NETWORK_OPERATOR_SETTINGS),
            // Fallback: allgemeine Netzwerkeinstellungen
            Intent(Settings.ACTION_WIRELESS_SETTINGS),
            Intent(Settings.ACTION_SETTINGS),
        )
        // Bevorzugt: DATA_ROAMING zeigt oft die "Mobile Daten"-Schalter-Seite.
        // Robust: wir versuchen mehrere Intents, der erste funktionierende gewinnt.
        var launched = false
        for (intent in intents) {
            try {
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                context.startActivity(intent)
                launched = true
                break
            } catch (_: Exception) {
                continue
            }
        }
        if (!launched) {
            // Letzter Fallback über MainActivity
            try {
                val fallback = Intent(context, MainActivity::class.java).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                context.startActivity(fallback)
            } catch (_: Exception) {}
        }
    }
}
