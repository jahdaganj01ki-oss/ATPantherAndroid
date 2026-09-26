package com.alditalk.panther.warning

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.alditalk.panther.R
import com.alditalk.panther.data.AppDatabase
import com.alditalk.panther.data.LogEntry
import com.google.android.material.button.MaterialButton
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Full-Screen / Overlay Dialog-Activity für die Guthaben-Warnung.
 *
 * Kein SYSTEM_ALERT_WINDOW nötig – wird als Activity gestartet (auch aus Service/Receiver)
 * und wirkt wie ein Heads-Up-Dialog. Im Black-Theme gestaltet.
 * Zwei Buttons:
 *   1) "Weiterhin nutzen" – schließt Dialog, Snooze 12h, erlaubt Verkehr
 *   2) "Internet abschalten" – Snooze 24h, öffnet System-Einstellungen, entfernt Notification
 *
 * Edge:
 *  - Empfängt ACTION_CLOSE_DIALOG falls Nutzer die Notification-Action "Internet abschalten"
 *    drückt während der Dialog offen ist -> automatisch schließen.
 */
class WarningDialogActivity : AppCompatActivity() {

    companion object {
        const val ACTION_CLOSE_DIALOG = "com.alditalk.panther.CLOSE_WARNING_DIALOG"
    }

    private var dialog: AlertDialog? = null

    private val closeReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action == ACTION_CLOSE_DIALOG) {
                dialog?.dismiss()
                finish()
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Transparenter Hintergrund – nur der Dialog ist sichtbar
        window.setBackgroundDrawableResource(android.R.color.transparent)
        // Tipp außerhalb schließt den Dialog NICHT automatisch (Nutzer muss entscheiden),
        // aber wir erlauben Back nur auf "Weiterhin nutzen" zu mappen ist hier Cancel = Continue
        showWarningDialog()

        val filter = IntentFilter(ACTION_CLOSE_DIALOG)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(closeReceiver, filter, RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            registerReceiver(closeReceiver, filter)
        }
    }

    override fun onDestroy() {
        try { unregisterReceiver(closeReceiver) } catch (_: Exception) {}
        dialog?.dismiss()
        super.onDestroy()
    }

    private fun showWarningDialog() {
        // Custom content: Text + Buttons im Dialog
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            val pad = dp(20)
            setPadding(pad, dp(16), pad, dp(8))
        }

        val titleView = TextView(this).apply {
            text = "⚠ Kein Datentarif aktiv"
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f)
            setTextColor(ContextCompat.getColor(context, R.color.text_primary))
            // bold
            paint.isFakeBoldText = true
            gravity = Gravity.START
            setPadding(0, 0, 0, dp(10))
        }

        val messageView = TextView(this).apply {
            text = NoTariffWarningManager.WARNING_TEXT
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            setTextColor(ContextCompat.getColor(context, R.color.text_primary))
            gravity = Gravity.START
            // Zeilenabstand leicht erhöhen
            setLineSpacing(dp(2).toFloat(), 1f)
        }

        val hintView = TextView(this).apply {
            val debug = intent.getStringExtra("debugInfo")
            if (!debug.isNullOrBlank()) {
                text = debug.take(220)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
                setTextColor(ContextCompat.getColor(context, R.color.text_secondary))
                setPadding(0, dp(8), 0, 0)
                visibility = android.view.View.VISIBLE
            } else {
                visibility = android.view.View.GONE
            }
        }

        container.addView(titleView)
        container.addView(messageView)
        container.addView(hintView)

        val dialog = MaterialAlertDialogBuilder(this)
            .setView(container)
            .setCancelable(false) // Nutzer muss bewusst wählen
            .create()
            .also { this.dialog = it }

        // Nach show() Buttons hinzufügen – sonst können wir das Layout besser steuern.
        // Stattdessen nutzen wir gleich Custom-Buttons im Container, damit beide gleichrangig sind.

        // Button-Leiste
        val buttonRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
            setPadding(0, dp(16), 0, 0)
        }

        val btnContinue = MaterialButton(this).apply {
            text = "Weiterhin nutzen"
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.bg_black))
            // heller Button, schwarze Schrift passend zum Black Theme
            setBackgroundColor(ContextCompat.getColor(context, R.color.white))
            cornerRadius = dp(18)
            insetTop = 0; insetBottom = 0
            setOnClickListener {
                onContinue()
            }
        }

        val btnDisable = MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = "Internet abschalten"
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.text_primary))
            strokeColor = android.content.res.ColorStateList.valueOf(ContextCompat.getColor(context, R.color.text_secondary))
            strokeWidth = dp(1)
            cornerRadius = dp(18)
            insetTop = 0; insetBottom = 0
            setPadding(dp(14), 0, dp(14), 0)
            setOnClickListener {
                onDisable()
            }
        }

        buttonRow.addView(btnDisable, LinearLayout.LayoutParams(0, dp(42)).apply {
            weight = 1f; marginEnd = dp(8)
        })
        buttonRow.addView(btnContinue, LinearLayout.LayoutParams(0, dp(42)).apply {
            weight = 1f
        })

        container.addView(buttonRow)

        // Hintergrund des Dialog-Fensters passend zum Black Theme (card_bg)
        dialog.setOnShowListener {
            dialog.window?.setBackgroundDrawableResource(R.drawable.bg_input) // wird unten ersetzt
            // Korrekt: Card-Farbe + Radius
            val bg = android.graphics.drawable.GradientDrawable().apply {
                shape = android.graphics.drawable.GradientDrawable.RECTANGLE
                cornerRadius = dp(14).toFloat()
                setColor(ContextCompat.getColor(this@WarningDialogActivity, R.color.card_bg))
                setStroke(dp(1), ContextCompat.getColor(this@WarningDialogActivity, R.color.input_stroke))
            }
            dialog.window?.setBackgroundDrawable(bg)
            // Breite auf 90% des Screens – dynamisch
        }

        dialog.show()

        // Dim etwas reduzieren damit Kontext sichtbar bleibt
        dialog.window?.let { w ->
            val lp = w.attributes
            lp.dimAmount = 0.55f
            w.attributes = lp
        }
    }

    private fun onContinue() {
        WarningPrefs.recordContinue(this)
        NoTariffWarningManager.dismiss(this)
        logAsync("WARN", "Warn-Dialog: Weiterhin nutzen (Snooze 12h)")
        dialog?.dismiss()
        finish()
    }

    private fun onDisable() {
        WarningPrefs.recordDisableChosen(this)
        NoTariffWarningManager.dismiss(this)
        logAsync("WARN", "Warn-Dialog: Internet abschalten — öffne Einstellungen")
        NoTariffWarningManager.openMobileDataSettings(this)
        dialog?.dismiss()
        finish()
    }

    private fun logAsync(type: String, message: String) {
        CoroutineScope(Dispatchers.IO).launch {
            try {
                AppDatabase.getDatabase(applicationContext).logDao()
                    .insert(LogEntry(type = type, message = message))
            } catch (_: Exception) {}
        }
    }

    private fun dp(v: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()
}
