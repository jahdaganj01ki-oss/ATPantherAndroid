package com.alditalk.panther

import android.app.Application
import androidx.appcompat.app.AppCompatDelegate
import com.alditalk.panther.data.AppDatabase
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Application-Einstiegspunkt.
 *
 * Da Android 8.0 (API 26, Huawei AGS2-L09) keinen systemweiten Dark Mode kennt,
 * erzwingen wir das dunkle Theme programmatisch beim App-Start über
 * [AppCompatDelegate.setDefaultNightMode] mit [AppCompatDelegate.MODE_NIGHT_YES].
 * Das garantiert ueber Neustarts hinweg ein konsistentes Black-Theme-Verhalten.
 */
class PantherApp : Application() {
    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val database: AppDatabase by lazy { AppDatabase.getDatabase(this) }

    override fun onCreate() {
        // Black Theme / Dark Mode programmatisch erzwingen (Android 8.0 kompatibel).
        AppCompatDelegate.setDefaultNightMode(AppCompatDelegate.MODE_NIGHT_YES)
        super.onCreate()
        // Redmi Note 9 Pro v1.2: DB NICHT mehr synchron in onCreate aufbauen –
        // Room.openHelper-Init auf dem Main-Thread verzögerte den Kaltstart
        // spürbar (ANR-Risiko). Stattdessen im Hintergrund vorwärmen.
        appScope.launch { database.logDao().count() }
    }
}
