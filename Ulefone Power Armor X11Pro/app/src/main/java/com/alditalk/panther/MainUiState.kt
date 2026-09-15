package com.alditalk.panther

import androidx.lifecycle.MutableLiveData

/**
 * X11Pro v1.3: geteilter UI-Zustand zwischen MainFragment (Seite 0) und
 * MainActivity (Service-Steuerung + Status-Broadcasts). Die Activity besitzt
 * genau eine Instanz und reicht sie an beide Fragmente weiter.
 *
 * Hinweis: setValue() (via .value) darf nur auf dem Main-Thread laufen.
 * Hintergrund-Threads nutzen postValue(); die Activity postet
 * Status-Broadcasts daher per postValue.
 */
class MainUiState {
    val phone = MutableLiveData("")
    val password = MutableLiveData("")
    val thresholdText = MutableLiveData("")
    val intervalText = MutableLiveData("")
    val statusText = MutableLiveData("Gestoppt")
    val statusColorRes = MutableLiveData(R.color.text_secondary)
    val toggleTextRes = MutableLiveData(R.string.monitor_start)
    val toggleStopMode = MutableLiveData(false)
    val isServiceRunning = MutableLiveData(false)
}
