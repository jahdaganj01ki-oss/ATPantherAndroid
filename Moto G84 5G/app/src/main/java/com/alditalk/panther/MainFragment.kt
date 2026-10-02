package com.alditalk.panther

import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.text.method.HideReturnsTransformationMethod
import android.text.method.PasswordTransformationMethod
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import androidx.fragment.app.Fragment
import com.google.android.material.button.MaterialButton

/** Kleiner TextWatcher-Adapter (nur onChanged relevant). */
private class SimpleTextWatcher(private val onChanged: (String) -> Unit) : TextWatcher {
    override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
    override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
        onChanged(s?.toString().orEmpty())
    }
    override fun afterTextChanged(s: Editable?) = Unit
}

/**
 * MotoG84 v1.6: Die EINE Seite – Freigabe, Login-Daten, Monitor und Verlauf.
 * Screenshot-Layout mit den beiden Karten (Login/Monitor) plus der neuen
 * Freigabe-Karte oben und dem Verlauf unten. Service-Steuerung und Export
 * laufen ueber die MainActivity (dort liegen Prefs/Service-State), das Fragment
 * meldet nur Aktionen zurueck.
 *
 * Der Verlauf (RecyclerView mit fester Hoehe) ist bewusst hier und nicht in
 * einem eigenen ViewPager: er sitzt in einem ScrollView mit FESTER Hoehe, damit
 * das Recycling erhalten bleibt (kein Messen aller 200 Zeilen).
 */
class MainFragment : Fragment() {

    companion object {
        fun newInstance() = MainFragment()
    }

    private lateinit var etPhone: EditText
    private lateinit var etPassword: EditText
    private lateinit var etThreshold: EditText
    private lateinit var etInterval: EditText
    private lateinit var tvStatus: android.widget.TextView
    private lateinit var btnToggle: MaterialButton
    private lateinit var btnSave: MaterialButton
    private lateinit var btnBatteryOpt: MaterialButton
    private lateinit var btnTogglePassword: MaterialButton

    // v1.6: Freigabe direkt auf der Seite
    private lateinit var tvLockInline: android.widget.TextView
    private lateinit var btnLockClaimInline: MaterialButton
    private lateinit var btnLockReleaseInline: MaterialButton
    private lateinit var btnLockSettings: MaterialButton

    // v1.6: Verlauf + Wartung ebenfalls auf dieser Seite
    private lateinit var rvLog: androidx.recyclerview.widget.RecyclerView
    private lateinit var btnClearCache: View
    private lateinit var btnExportLog: View

    private val adapter = LogListAdapter()
    private var passwordVisible = false
    private var lastTopLogId: Long? = null

    private fun host(): MainActivity = requireActivity() as MainActivity

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?
    ): View = inflater.inflate(R.layout.fragment_main, container, false)

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        val activity = host()
        etPhone = view.findViewById(R.id.etPhone)
        etPassword = view.findViewById(R.id.etPassword)
        etThreshold = view.findViewById(R.id.etThreshold)
        etInterval = view.findViewById(R.id.etInterval)
        tvStatus = view.findViewById(R.id.tvStatus)
        btnToggle = view.findViewById(R.id.btnToggleMonitor)
        btnSave = view.findViewById(R.id.btnSaveCredentials)
        btnBatteryOpt = view.findViewById(R.id.btnBatteryOpt)
        btnTogglePassword = view.findViewById(R.id.btnTogglePassword)

        // v1.6: Freigabe direkt auf der Seite
        tvLockInline = view.findViewById(R.id.tvLockStatusInline)
        btnLockClaimInline = view.findViewById(R.id.btnLockClaimInline)
        btnLockReleaseInline = view.findViewById(R.id.btnLockReleaseInline)
        btnLockSettings = view.findViewById(R.id.btnLockSettings)

        // v1.6: Verlauf + Wartung ebenfalls auf dieser Seite
        rvLog = view.findViewById(R.id.rvLog)
        btnClearCache = view.findViewById(R.id.btnClearCache)
        btnExportLog = view.findViewById(R.id.btnExportLog)

        // MotoG84 v1.5: Passwort anzeigen/verbergen (Auge-Button).
        btnTogglePassword.setOnClickListener {
            passwordVisible = !passwordVisible
            val selStart = etPassword.selectionStart.coerceAtLeast(0)
            val selEnd = etPassword.selectionEnd.coerceAtLeast(0)
            if (passwordVisible) {
                etPassword.transformationMethod = HideReturnsTransformationMethod.getInstance()
                btnTogglePassword.setIconResource(R.drawable.ic_visibility_off)
                btnTogglePassword.contentDescription = "Passwort verbergen"
            } else {
                etPassword.transformationMethod = PasswordTransformationMethod.getInstance()
                btnTogglePassword.setIconResource(R.drawable.ic_visibility)
                btnTogglePassword.contentDescription = "Passwort anzeigen"
            }
            etPassword.setSelection(selStart.coerceAtMost(etPassword.length()),
                selEnd.coerceAtMost(etPassword.length()))
        }

        etPhone.setText(activity.uiState.phone.value)
        etPassword.setText(activity.uiState.password.value)
        etThreshold.setText(activity.uiState.thresholdText.value)
        etInterval.setText(activity.uiState.intervalText.value)
        tvStatus.text = activity.uiState.statusText.value
        btnToggle.text = if (activity.uiState.toggleStopMode.value == true) "Monitor stoppen"
        else getString(R.string.monitor_start)

        activity.uiState.statusText.observe(viewLifecycleOwner) { tvStatus.text = it }
        activity.uiState.statusColorRes.observe(viewLifecycleOwner) { colorRes ->
            tvStatus.setTextColor(requireContext().getColor(colorRes ?: R.color.text_secondary))
        }
        activity.uiState.toggleStopMode.observe(viewLifecycleOwner) { stopMode ->
            btnToggle.text = if (stopMode == true) "Monitor stoppen"
            else getString(R.string.monitor_start)
        }

        // MotoG84 v1.3: Eingaben des Nutzers sofort in uiState spiegeln, damit
        // Drehen (configChanges, kein View-Neuaufbau) und Speichern/Starten
        // immer den getippten Text sehen – auch wenn das Fragment pausiert ist.
        etPhone.addTextChangedListener(SimpleTextWatcher { activity.uiState.phone.value = it })
        etPassword.addTextChangedListener(SimpleTextWatcher { activity.uiState.password.value = it })
        etThreshold.addTextChangedListener(SimpleTextWatcher { activity.uiState.thresholdText.value = it })
        etInterval.addTextChangedListener(SimpleTextWatcher { activity.uiState.intervalText.value = it })

        btnToggle.setOnClickListener { activity.onToggleClicked() }
        btnSave.setOnClickListener { activity.onSaveClicked() }
        btnBatteryOpt.setOnClickListener { activity.onBatteryOptClicked() }

        // ── v1.6: Freigabe direkt bedienbar ────────────────────────────────
        // Übernehmen = Freigabe an dieses Handy holen (verdrängt die andere
        // Variante, die daraufhin selbst in Bereitschaft geht).
        btnLockClaimInline.setOnClickListener { activity.onLockClaimInline() }
        btnLockReleaseInline.setOnClickListener { activity.onLockReleaseInline() }
        btnLockSettings.setOnClickListener { activity.onLockClicked() }

        // ── v1.6: Verlauf + Wartung ────────────────────────────────────────
        val lm = androidx.recyclerview.widget.LinearLayoutManager(requireContext())
        rvLog.layoutManager = lm
        rvLog.adapter = adapter
        rvLog.setHasFixedSize(true)
        // itemAnimator = null: die Default-Animation lief bei jedem 60-s-Diff
        // auf der schwachen GPU und erzeugte Ruckler auf dem 120-Hz-Display.
        rvLog.itemAnimator = null
        rvLog.setItemViewCacheSize(20)

        btnClearCache.setOnClickListener { activity.onClearCacheClicked() }
        btnExportLog.setOnClickListener { activity.onExportLogClicked() }

        activity.logState.entries.observe(viewLifecycleOwner) { entries ->
            val newTopId = entries.firstOrNull()?.id
            // Nur automatisch nach oben springen, wenn der Nutzer ohnehin am
            // Anfang der Liste steht – sonst würde jede Aktualisierung den
            // gerade gelesenen Eintrag wegschieben.
            val stickToNewest = lastTopLogId == null || lm.findFirstVisibleItemPosition() <= 1
            lastTopLogId = newTopId
            adapter.submitList(entries) {
                if (stickToNewest && entries.isNotEmpty()) rvLog.scrollToPosition(0)
            }
        }

        // Startzustand der Freigabe aus dem Cache (blockiert nie).
        renderLock(activity.cachedLockStatus())
    }

    /** Zeigt den Freigabe-Status in der Karte oben an. */
    fun renderLock(result: com.alditalk.panther.monitor.GateResult) {
        if (!::tvLockInline.isInitialized) return
        val headline = when (result.status) {
            com.alditalk.panther.monitor.GateStatus.ALLOWED ->
                "✅ Freigabe aktiv: ${result.owner}"
            com.alditalk.panther.monitor.GateStatus.NOT_OWNER ->
                "⏸ Bereitschaft – aktiv: ${result.owner}"
            com.alditalk.panther.monitor.GateStatus.FREE ->
                "⚪ Freigabe frei – „Hier übernehmen“ tippen"
            com.alditalk.panther.monitor.GateStatus.NOT_CONFIGURED ->
                "⚠ Keine Freigabe-URL eingetragen"
            com.alditalk.panther.monitor.GateStatus.UNREACHABLE ->
                "⛔ Freigabe-Server nicht erreichbar"
            else -> "⚠ Freigabe unsicher"
        }
        val text = if (result.allowed && result.cooldownMs > 0) {
            "$headline\n${result.detail}\n⏳ Erste Abfrage in ${result.cooldownMs / 1000} s"
        } else {
            "$headline\n${result.detail}"
        }
        tvLockInline.text = text
        tvLockInline.setTextColor(
            requireContext().getColor(
                if (result.allowed) R.color.status_ok else R.color.status_warn
            )
        )
    }

    fun currentPhone(): String =
        if (::etPhone.isInitialized) etPhone.text.toString().trim() else ""
    fun currentPassword(): String =
        if (::etPassword.isInitialized) etPassword.text.toString().trim() else ""
}
