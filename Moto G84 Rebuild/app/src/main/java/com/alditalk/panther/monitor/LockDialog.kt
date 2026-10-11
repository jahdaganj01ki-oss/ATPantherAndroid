package com.alditalk.panther.monitor

import android.view.LayoutInflater
import android.widget.CheckBox
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.lifecycleScope
import com.alditalk.panther.R
import com.google.android.material.button.MaterialButton
import com.google.android.material.textfield.TextInputEditText
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Dialog fuer die Monitor-Freigabe.
 *
 * Bewusst als Dialog und nicht als eingebautes Formular auf der Hauptseite:
 * die Geraetevarianten (X11Pro 5,45") haben sehr knappe Layout-Budgets ohne
 * Scroll, und die Freigabe wird nur selten geaendert.
 *
 * In allen Android-Varianten identisch.
 */
object LockDialog {

    fun show(activity: FragmentActivity) {
        val gate = MonitorGate(activity)
        val view = LayoutInflater.from(activity).inflate(R.layout.dialog_lock, null, false)

        val tvStatus = view.findViewById<TextView>(R.id.tvLockStatus)
        val etUrl = view.findViewById<TextInputEditText>(R.id.etLockUrl)
        val etToken = view.findViewById<TextInputEditText>(R.id.etLockToken)
        val chkFailOpen = view.findViewById<CheckBox>(R.id.chkLockFailOpen)
        val btnSave = view.findViewById<MaterialButton>(R.id.btnLockSave)
        val btnClaim = view.findViewById<MaterialButton>(R.id.btnLockClaim)
        val btnRelease = view.findViewById<MaterialButton>(R.id.btnLockRelease)
        val btnClose = view.findViewById<MaterialButton>(R.id.btnLockClose)

        etUrl.setText(gate.workerUrl)
        etToken.setText(gate.workerToken)
        chkFailOpen.isChecked = gate.failOpen

        val dialog = AlertDialog.Builder(activity)
            .setTitle(R.string.lock_title)
            .setView(view)
            .create()

        fun render(result: GateResult) {
            val headline = when (result.status) {
                GateStatus.ALLOWED -> "✅ Freigabe aktiv: ${result.owner}"
                GateStatus.NOT_OWNER -> "⏸ Bereitschaft: ${result.owner} fragt ab"
                GateStatus.FREE -> "⚪ Freigabe frei – übernehmen tippen"
                GateStatus.NOT_CONFIGURED -> "⚠ Freigabe nicht konfiguriert"
                GateStatus.UNREACHABLE -> "⛔ Freigabe-Server nicht erreichbar"
                GateStatus.ERROR -> "⚠ Freigabe unsicher"
            }
            tvStatus.text = "$headline\n${result.detail}"
            tvStatus.setTextColor(
                activity.getColor(if (result.allowed) R.color.status_ok else R.color.status_warn)
            )
        }

        /** Fuehrt einen Gate-Aufruf aus und blendet die Buttons waehrenddessen aus. */
        fun withGate(block: suspend () -> GateResult) {
            btnSave.isEnabled = false
            btnClaim.isEnabled = false
            btnRelease.isEnabled = false
            activity.lifecycleScope.launch {
                val result = withContext(Dispatchers.IO) { block() }
                render(result)
                btnSave.isEnabled = true
                btnClaim.isEnabled = true
                btnRelease.isEnabled = true
            }
        }

        btnSave.setOnClickListener {
            gate.workerUrl = etUrl.text.toString()
            gate.workerToken = etToken.text.toString()
            gate.failOpen = chkFailOpen.isChecked
            // Ungueltige Eingaben zurueckspiegeln, damit klar ist, was gespeichert ist
            etUrl.setText(gate.workerUrl)
            etToken.setText(gate.workerToken)
            withGate { gate.evaluate(force = true, autoClaim = false) }
        }

        btnClaim.setOnClickListener {
            if (!gate.isConfigured()) {
                withGate { gate.evaluate(force = true, autoClaim = false) }
                return@setOnClickListener
            }
            AlertDialog.Builder(activity)
                .setTitle(R.string.lock_claim)
                .setMessage(R.string.lock_claim_confirm)
                .setPositiveButton(android.R.string.ok) { _, _ -> withGate { gate.claim() } }
                .setNegativeButton(android.R.string.cancel, null)
                .show()
        }

        btnRelease.setOnClickListener { withGate { gate.release() } }

        btnClose.setOnClickListener { dialog.dismiss() }

        render(gate.cachedStatus())
        dialog.show()

        // Einmal frisch holen – evaluate() nutzt intern den 5-Minuten-Cache.
        if (gate.isConfigured()) {
            withGate { gate.evaluate(force = false, autoClaim = false) }
        }
    }
}
