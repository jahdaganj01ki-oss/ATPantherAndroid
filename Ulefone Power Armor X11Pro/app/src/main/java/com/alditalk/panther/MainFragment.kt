package com.alditalk.panther

import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
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
 * X11Pro v1.3: Haupt-Seite (ViewPager Seite 0).
 * Screenshot-Layout: Login-Daten + Einstellungen + Speichern in Card 1,
 * Monitor + Wartung in Card 2. Service-Steuerung und Export laufen ueber
 * die MainActivity (dort liegen Prefs/Service-State), das Fragment meldet
 * nur Aktionen zurueck.
 */
class MainFragment : Fragment() {

    companion object {
        fun newInstance() = MainFragment()
    }

    private lateinit var etPhone: android.widget.EditText
    private lateinit var etPassword: android.widget.EditText
    private lateinit var etThreshold: android.widget.EditText
    private lateinit var etInterval: android.widget.EditText
    private lateinit var tvStatus: android.widget.TextView
    private lateinit var btnToggle: MaterialButton
    private lateinit var btnSave: MaterialButton
    private lateinit var btnBatteryOpt: MaterialButton

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

        // X11Pro v1.3: Eingaben des Nutzers sofort in uiState spiegeln, damit
        // Drehen (configChanges, kein View-Neuaufbau) und Speichern/Starten
        // immer den getippten Text sehen – auch wenn das Fragment pausiert ist.
        etPhone.addTextChangedListener(SimpleTextWatcher { activity.uiState.phone.value = it })
        etPassword.addTextChangedListener(SimpleTextWatcher { activity.uiState.password.value = it })
        etThreshold.addTextChangedListener(SimpleTextWatcher { activity.uiState.thresholdText.value = it })
        etInterval.addTextChangedListener(SimpleTextWatcher { activity.uiState.intervalText.value = it })

        btnToggle.setOnClickListener { activity.onToggleClicked() }
        btnSave.setOnClickListener { activity.onSaveClicked() }
        btnBatteryOpt.setOnClickListener { activity.onBatteryOptClicked() }
    }

    fun currentPhone(): String =
        if (::etPhone.isInitialized) etPhone.text.toString().trim() else ""
    fun currentPassword(): String =
        if (::etPassword.isInitialized) etPassword.text.toString().trim() else ""
}
