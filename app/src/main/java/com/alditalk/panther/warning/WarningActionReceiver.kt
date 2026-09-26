package com.alditalk.panther.warning

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import android.widget.Toast
import com.alditalk.panther.data.AppDatabase
import com.alditalk.panther.data.LogEntry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Empfängt die zwei Notification-Actions der Guthaben-Warnung.
 *
 * - ACTION_CONTINUE: Dialog schließen, Verkehr erlauben (Snooze 12h)
 * - ACTION_DISABLE_DATA: Zur System-Einstellung leiten (Snooze 24h + Notification entfernen)
 *
 * Zusätzlich wird ein Log-Eintrag geschrieben.
 */
class WarningActionReceiver : BroadcastReceiver() {

    companion object { private const val TAG = "WarningAction" }

    override fun onReceive(context: Context, intent: Intent) {
        Log.d(TAG, "onReceive action=${intent.action}")

        // goAsync: Broadcast darf kurz nach return weiterleben für IO
        val pending = goAsync()

        when (intent.action) {
            NoTariffWarningManager.ACTION_CONTINUE -> handleContinue(context, pending)
            NoTariffWarningManager.ACTION_DISABLE_DATA -> handleDisable(context, pending)
            else -> pending.finish()
        }
    }

    private fun handleContinue(context: Context, pending: PendingResult) {
        WarningPrefs.recordContinue(context)
        NoTariffWarningManager.dismiss(context)
        // Log auf IO schreiben
        logAsync(context, "WARN", "Warnung bestätigt: Weiterhin nutzen (Snooze 12h)", pending) {
            try {
                Toast.makeText(context, "Hinweis bestätigt — Datenverkehr bleibt aktiv", Toast.LENGTH_SHORT).show()
            } catch (_: Exception) {}
        }
    }

    private fun handleDisable(context: Context, pending: PendingResult) {
        WarningPrefs.recordDisableChosen(context)
        NoTariffWarningManager.dismiss(context)
        logAsync(context, "WARN", "Warnung: Nutzer wählte 'Internet abschalten' — öffne Einstellungen", pending) {
            NoTariffWarningManager.openMobileDataSettings(context)
            try {
                Toast.makeText(context, "Bitte mobile Daten in den Einstellungen deaktivieren", Toast.LENGTH_LONG).show()
            } catch (_: Exception) {}
        }
        // Falls WarningDialogActivity im Vordergrund liegt, schließen
        try {
            val closeIntent = Intent(WarningDialogActivity.ACTION_CLOSE_DIALOG).apply {
                setPackage(context.packageName)
            }
            context.sendBroadcast(closeIntent)
        } catch (_: Exception) {}
    }

    private fun logAsync(
        context: Context,
        type: String,
        message: String,
        pending: PendingResult,
        onDone: () -> Unit = {}
    ) {
        CoroutineScope(Dispatchers.IO).launch {
            try {
                val db = AppDatabase.getDatabase(context.applicationContext)
                db.logDao().insert(LogEntry(type = type, message = message))
            } catch (e: Exception) {
                Log.w(TAG, "Log insert failed", e)
            } finally {
                try { onDone() } catch (_: Exception) {}
                pending.finish()
            }
        }
    }
}
