package com.alditalk.panther

import android.app.Application
import androidx.appcompat.app.AppCompatDelegate
import com.alditalk.panther.data.AppDatabase

/**
 * Application-Einstiegspunkt.
 *
 * Da Android 8.0 (API 26, Huawei AGS2-L09) keinen systemweiten Dark Mode kennt,
 * erzwingen wir das dunkle Theme programmatisch beim App-Start über
 * [AppCompatDelegate.setDefaultNightMode] mit [AppCompatDelegate.MODE_NIGHT_YES].
 * Das garantiert ueber Neustarts hinweg ein konsistentes Black-Theme-Verhalten.
 */
class PantherApp : Application() {
    val database: AppDatabase by lazy { AppDatabase.getDatabase(this) }

    override fun onCreate() {
        // Black Theme / Dark Mode programmatisch erzwingen (Android 8.0 kompatibel).
        AppCompatDelegate.setDefaultNightMode(AppCompatDelegate.MODE_NIGHT_YES)
        super.onCreate()
        database // eagerly initialize
    }
}
