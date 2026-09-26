package com.alditalk.panther.warning

import android.content.Context

/**
 * Persistenter Zustand für das Guthaben-Warnsystem.
 *
 * Verhindert Notification-Spam durch Cooldown + Snooze.
 * - Nach einer angezeigten Warnung: Cooldown 6h bevor erneut gewarnt wird
 * - Nach "Weiterhin nutzen": Snooze 12h (Nutzer hat bewusst entschieden)
 * - Nach Reboot / Neuinstallation: unverzüglich warnen falls Bedingung erfüllt
 */
object WarningPrefs {

    private const val PREFS = "at_panther_warning_state"
    private const val KEY_LAST_WARNING_MS = "last_warning_ms"
    private const val KEY_LAST_HASH = "last_status_hash"
    private const val KEY_SNOOZE_UNTIL_MS = "snooze_until_ms"
    private const val KEY_DISMISS_COUNT = "dismiss_count"
    // Wie oft wurde hintereinander gewarnt (für exponentiellen Backoff optional)
    private const val KEY_CONSECUTIVE_WARNINGS = "consecutive_warnings"
    private const val KEY_USER_DISABLED_MOBILE = "user_disabled_mobile"

    // Edge: Cooldown zwischen zwei Warnungen (auch wenn Bedingung dauerhaft erfüllt)
    const val COOLDOWN_MS: Long = 6 * 60 * 60 * 1000L // 6 Stunden
    // Nach "Weiterhin nutzen" längerer Snooze
    const val SNOOZE_CONTINUE_MS: Long = 12 * 60 * 60 * 1000L // 12 Stunden
    // Nach "Internet abschalten" – nicht mehr warnen bis neue Buchung erkannt wird
    // (wir setzen Snooze auf 24h, die nächste Tariff-Abfrage mit aktivem Tarif cleared es)
    const val SNOOZE_DISABLE_MS: Long = 24 * 60 * 60 * 1000L

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun canShowWarning(context: Context, status: TariffStatus): Boolean {
        val p = prefs(context)
        val now = System.currentTimeMillis()
        val snoozeUntil = p.getLong(KEY_SNOOZE_UNTIL_MS, 0L)
        if (now < snoozeUntil) return false

        val last = p.getLong(KEY_LAST_WARNING_MS, 0L)
        if (last != 0L && now - last < COOLDOWN_MS) {
            // Gleicher Zustand? Noch strenger drosseln. Unterschiedlicher Zustand
            // (z.B. anderer Tarifname) darf etwas früher warnen – aber nicht vor Cooldown.
            return false
        }
        // Wenn Nutzer bereits manuell abgeschaltet hat, nicht erneut warnen
        // solange Snooze läuft – wird durch clearIfTariffActive zurückgesetzt.
        return true
    }

    fun recordWarningShown(context: Context, status: TariffStatus) {
        val p = prefs(context)
        p.edit()
            .putLong(KEY_LAST_WARNING_MS, System.currentTimeMillis())
            .putString(KEY_LAST_HASH, status.debugInfo.hashCode().toString())
            .putInt(KEY_CONSECUTIVE_WARNINGS, p.getInt(KEY_CONSECUTIVE_WARNINGS, 0) + 1)
            .apply()
    }

    fun recordContinue(context: Context) {
        val p = prefs(context)
        p.edit()
            .putLong(KEY_SNOOZE_UNTIL_MS, System.currentTimeMillis() + SNOOZE_CONTINUE_MS)
            .putInt(KEY_DISMISS_COUNT, p.getInt(KEY_DISMISS_COUNT, 0) + 1)
            .apply()
    }

    fun recordDisableChosen(context: Context) {
        val p = prefs(context)
        p.edit()
            .putLong(KEY_SNOOZE_UNTIL_MS, System.currentTimeMillis() + SNOOZE_DISABLE_MS)
            .putBoolean(KEY_USER_DISABLED_MOBILE, true)
            .putLong(KEY_LAST_WARNING_MS, System.currentTimeMillis())
            .apply()
    }

    /**
     * Wenn wieder ein aktiver Tarif/Add-on erkannt wird, alle Drosseln zurücksetzen
     * damit bei erneutem Wegfall sofort wieder gewarnt werden kann.
     */
    fun clearIfTariffActive(context: Context) {
        val p = prefs(context)
        // Nur wenn wirklich wieder Tarif aktiv ist – einfache Methode: Snooze auf 0 setzen
        if (p.getLong(KEY_SNOOZE_UNTIL_MS, 0L) != 0L || p.getInt(KEY_CONSECUTIVE_WARNINGS, 0) != 0) {
            p.edit()
                .putLong(KEY_SNOOZE_UNTIL_MS, 0L)
                .putInt(KEY_CONSECUTIVE_WARNINGS, 0)
                .putBoolean(KEY_USER_DISABLED_MOBILE, false)
                .apply()
        }
    }

    /** Für Debugging / manuellen Reset aus UI. */
    fun clearAll(context: Context) {
        prefs(context).edit().clear().apply()
    }

    fun getStateForDebug(context: Context): String {
        val p = prefs(context)
        return "last=${p.getLong(KEY_LAST_WARNING_MS, 0)} snoozeUntil=${p.getLong(KEY_SNOOZE_UNTIL_MS, 0)} " +
            "warnings=${p.getInt(KEY_CONSECUTIVE_WARNINGS, 0)}"
    }
}
