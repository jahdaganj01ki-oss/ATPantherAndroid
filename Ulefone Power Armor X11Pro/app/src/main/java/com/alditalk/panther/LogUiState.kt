package com.alditalk.panther

import androidx.lifecycle.MutableLiveData
import com.alditalk.panther.data.LogEntry

/**
 * X11Pro v1.3: geteilter Log-Zustand zwischen LogFragment (RecyclerView) und
 * MainActivity (Flow-Collector + Export). Die Activity besitzt genau eine
 * Instanz und befuellt sie per postValue (Collector kann auf IO laufen);
 * das Fragment beobachtet nur.
 */
class LogUiState {
    val entries = MutableLiveData<List<LogEntry>>(emptyList())
}
