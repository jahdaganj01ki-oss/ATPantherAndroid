package com.alditalk.panther.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log

/**
 * Empfangs-Receiver für AlarmManager-Fallback und (optional) Boot-Completed.
 *
 * Anforderung 5 – Hintergrundprozess fuer Huawei AGS2-L09 (EMUI / Android 8):
 * Falls das System den [MonitorService] killt, plant der Service vorab einen
 * AlarmManager-Ping – dieser Receiver startet den Service dann neu ab. Eingebundene
 * Aktionen:
 *   - [ACTION_RESTART_MONITOR]            : expliziter Neustart durch AlarmManager
 *   - Intent.ACTION_BOOT_COMPLETED        : Start nach Reboot, falls der Nutzer vorher
 *                                          aktiv war (Werte kommen aus Prefs)
 */
class MonitorWakeReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent?) {
        Log.d(TAG, "onReceive: action=${intent?.action}")
        when (intent?.action) {
            ACTION_RESTART_MONITOR,
            Intent.ACTION_BOOT_COMPLETED,
            "android.intent.action.QUICKBOOT_POWERON" -> restartMonitor(context, intent)
        }
    }

    /**
     * Startet den [MonitorService] als Foreground Service neu. Vorhandene Extras
     * (phone/password/threshold/interval) werden weitergereicht; fehlen diese,
     *liest der Service beim naechsten Nutzer-Klick neu ein.
     */
    private fun restartMonitor(context: Context, source: Intent?) {
        val state = context.getSharedPreferences("at_panther_monitor_state", Context.MODE_PRIVATE)
        if (state.getBoolean("paused_after_connection_failures", false)) {
            Log.i(TAG, "Monitor-Neustart übersprungen: Verbindung ist nach 3 Fehlern pausiert")
            return
        }
        // Bereitschaftsmodus: eine andere Variante hat die Monitor-Freigabe.
        // Ein Neustart wuerde nur wieder in den Bereitschaftsmodus laufen.
        if (state.getBoolean("lock_standby", false)) {
            Log.i(TAG, "Monitor-Neustart übersprungen: Monitor-Freigabe liegt bei einer anderen Variante")
            return
        }

        val svcIntent = Intent(context, MonitorService::class.java).apply {
            source?.getStringExtra(MonitorService.EXTRA_PHONE)?.let { putExtra(MonitorService.EXTRA_PHONE, it) }
            source?.getStringExtra(MonitorService.EXTRA_PASSWORD)?.let { putExtra(MonitorService.EXTRA_PASSWORD, it) }
            source?.getFloatExtra(MonitorService.EXTRA_THRESHOLD_MB, 850f)?.let {
                putExtra(MonitorService.EXTRA_THRESHOLD_MB, it)
            }
            source?.getIntExtra(MonitorService.EXTRA_INTERVAL_SEC, 60)?.let {
                putExtra(MonitorService.EXTRA_INTERVAL_SEC, it)
            }
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(svcIntent)
            } else {
                context.startService(svcIntent)
            }
        } catch (e: Exception) {
            // Hintergrundstart-Restriktionen o.a. – Controller-Fehler melden, nicht crashen.
            Log.w(TAG, "Restart des MonitorService fehlgeschlagen", e)
        }
    }

    companion object {
        private const val TAG = "MonitorWakeReceiver"
        const val ACTION_RESTART_MONITOR = "com.alditalk.panther.RESTART_MONITOR"
    }
}
