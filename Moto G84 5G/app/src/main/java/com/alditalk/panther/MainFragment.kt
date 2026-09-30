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
 * MotoG84 v1.3: Haupt-Seite (ViewPager Seite 0).
 * Screenshot-Layout: Login-Daten + Einstellungen + Speichern in Card 1,
 * Monitor + Wartung in Card 2. Service-Steuerung und Export laufen ueber
 * die MainActivity (dort liegen Prefs/Service-State), das Fragment meldet
 * nur Aktionen zurueck.
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
    private lateinit var btnLock: MaterialButton
    private lateinit var btnTogglePassword: MaterialButton
    private var passwordVisible = false

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
        btnLock = view.findViewById(R.id.btnLock)
        btnTogglePassword = view.findViewById(R.id.btnTogglePassword)

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
        // Monitor-Freigabe: sorgt dafuer, dass nur eine Variante abfragt
        btnLock.setOnClickListener { activity.onLockClicked() }
    }

    fun currentPhone(): String =
        if (::etPhone.isInitialized) etPhone.text.toString().trim() else ""
    fun currentPassword(): String =
        if (::etPassword.isInitialized) etPassword.text.toString().trim() else ""
}
