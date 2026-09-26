package com.alditalk.panther

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.res.Configuration
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.EditText
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.fragment.app.Fragment
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import androidx.viewpager2.adapter.FragmentStateAdapter
import androidx.viewpager2.widget.ViewPager2
import com.alditalk.panther.data.LogEntry
import com.alditalk.panther.service.MonitorService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.OutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : AppCompatActivity() {

    companion object {
        /**
         * X11Pro-Freeze-Fix: UI laedt nur die letzten N Log-Eintraege.
         * Alles aeltere bleibt in der DB (fuer den Export via Dao-Anfrage mit
         * vollem Umfang) aber landet nicht mehr komplett im RecyclerView.
         */
        private const val LOG_UI_LIMIT = 200

        /** ViewPager-Seiten: 0 = Haupt (Screenshot-Layout), 1 = Verlauf. */
        const val PAGE_MAIN = 0
        const val PAGE_LOG = 1
    }

    // Default-Werte (v1.5: Standard-Schwelle 950 MB)
    private val defaultThresholdMb = 950f
    private val defaultIntervalSec = 60

    /** X11Pro v1.3: geteilter Zustand mit beiden ViewPager-Fragmenten. */
    val uiState = MainUiState()
    val logState = LogUiState()

    private lateinit var viewPager: ViewPager2

    /** X11Pro v1.3: Referenzen auf die beiden Pager-Fragmente (kein Tag-Lookup). */
    private var mainFragment: MainFragment? = null

    private var isServiceRunning = false

    /** Liste aller Log-Einträge (für den Log-Export gehalten). */
    private var currentLogEntries: List<LogEntry> = emptyList()

    /**
     * SAF Launcher für ACTION_CREATE_DOCUMENT – oeffnet den System-Dateidialog,
     * damit der Nutzer den Speicherort der .txt-Datei frei waehlen kann.
     */
    private val createDocumentLauncher = registerForActivityResult(
        ActivityResultContracts.CreateDocument("text/plain")
    ) { uri: Uri? ->
        if (uri != null) {
            exportLogToUri(uri)
        } else {
            Toast.makeText(this, "Export abgebrochen", Toast.LENGTH_SHORT).show()
        }
    }

    private val statusReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            val status = intent?.getStringExtra(MonitorService.EXTRA_STATUS_TEXT) ?: "—"
            val remaining = intent?.getFloatExtra(MonitorService.EXTRA_REMAINING_MB, -1f) ?: -1f
            // BroadcastReceiver kann auf Hintergrund-Threads laufen -> postValue.
            uiState.statusText.postValue(
                if (remaining >= 0) "$status  (${"%.1f".format(remaining)} MB)" else status
            )
        }
    }

    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (!granted) {
            Toast.makeText(this, "Benachrichtigungen verweigert — Warn-Dialog erscheint dennoch", Toast.LENGTH_LONG).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // POST_NOTIFICATIONS (Android 13+) anfragen – ohne sie können Guthaben-Warnungen
        // nur als Dialog gezeigt werden, nicht als Heads-Up Notification.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val perm = android.Manifest.permission.POST_NOTIFICATIONS
            if (checkSelfPermission(perm) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                notificationPermissionLauncher.launch(perm)
            }
        }
        // Statusleiste transparent + helle Icons erzwingen (Theme-Absicherung),
        // damit die Benachrichtigungszeile normal sichtbar bleibt.
        window.statusBarColor = android.graphics.Color.TRANSPARENT
        androidx.core.view.WindowInsetsControllerCompat(window, window.decorView)
            .isAppearanceLightStatusBars = false
        setContentView(R.layout.activity_main)

        // X11Pro v1.3: ViewPager mit 2 Seiten (Haupt + Verlauf).
        viewPager = findViewById(R.id.viewPager)
        viewPager.adapter = object : FragmentStateAdapter(this) {
            override fun getItemCount(): Int = 2
            override fun createFragment(position: Int): Fragment =
                if (position == PAGE_LOG) LogFragment.newInstance()
                else MainFragment.newInstance().also { mainFragment = it }
        }
        // X11Pro v1.5: Seiten-Umschalter oben (● aktiv / ○ inaktiv).
        val tabMain = findViewById<com.google.android.material.button.MaterialButton>(R.id.btnTabMain)
        val tabLog = findViewById<com.google.android.material.button.MaterialButton>(R.id.btnTabLog)
        fun refreshTabs(position: Int) {
            tabMain.text = if (position == PAGE_MAIN) "● Haupt" else "○ Haupt"
            tabLog.text = if (position == PAGE_LOG) "● Verlauf" else "○ Verlauf"
        }
        tabMain.setOnClickListener { showMainPage() }
        tabLog.setOnClickListener { showLogPage() }
        viewPager.registerOnPageChangeCallback(object : ViewPager2.OnPageChangeCallback() {
            override fun onPageSelected(position: Int) {
                refreshTabs(position)
            }
        })
        refreshTabs(PAGE_MAIN)

        // Anforderung 1: Gespeicherte Login-Daten UND Einstellungen laden
        loadCredentials()

        // Observe log entries – begrenzt auf LOG_UI_LIMIT (X11Pro-Freeze-Fix).
        // X11Pro v1.2: repeatOnLifecycle(STARTED) statt Dauer-Collect (kein
        // Diff im Hintergrund) + distinctUntilChanged (kein redundanter
        // DiffUtil-Durchlauf, wenn der Service nur getrimmt/gelöscht hat).
        // X11Pro v1.3: Collector befuellt logState + currentLogEntries
        // (LogFragment beobachtet logState und bindet via ListAdapter).
        val logDao = (application as PantherApp).database.logDao()
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                logDao.getRecent(LOG_UI_LIMIT)
                    .distinctUntilChanged()
                    .collectLatest { entries ->
                        currentLogEntries = entries
                        logState.entries.postValue(entries)
                    }
            }
        }
    }

    /** X11Pro v1.3: zur Haupt-Seite (Seite 0) wechseln. */
    fun showMainPage() {
        if (::viewPager.isInitialized) viewPager.setCurrentItem(PAGE_MAIN, true)
    }

    /** X11Pro v1.3: zur Verlauf-Seite (Seite 1) wechseln. */
    fun showLogPage() {
        if (::viewPager.isInitialized) viewPager.setCurrentItem(PAGE_LOG, true)
    }

    /** X11Pro v1.3: Klick-Handler des MainFragments (Toggle Monitor). */
    fun onToggleClicked() {
        if (isServiceRunning) {
            stopMonitor()
        } else {
            startMonitor()
        }
    }

    /** X11Pro v1.3: Klick-Handler des MainFragments (Speichern). */
    fun onSaveClicked() {
        saveCredentials()
        Toast.makeText(this, "Login-Daten und Einstellungen gespeichert", Toast.LENGTH_SHORT).show()
    }

    /** X11Pro v1.3: Klick-Handler des MainFragments (Cache leeren). */
    fun onClearCacheClicked() {
        clearAppCache()
        Toast.makeText(this, "Cache geleert", Toast.LENGTH_SHORT).show()
    }

    /** X11Pro v1.3: Klick-Handler des MainFragments (Log exportieren). */
    fun onExportLogClicked() {
        val timestamp = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.GERMAN).format(Date())
        createDocumentLauncher.launch("at_panther_log_$timestamp.txt")
    }

    /** X11Pro v1.3: Klick-Handler des MainFragments (Batterie-Optimierung). */
    fun onBatteryOptClicked() {
        requestIgnoreBatteryOptimizations()
    }

    /**
     * X11Pro v1.2: Mit configChanges (siehe Manifest) wird die Activity beim
     * Drehen NICHT neu erzeugt – kein manueller Scroll-Erhalt mehr noetig,
     * da der Verlauf in einem eigenen Fragment mit eigenem Layout lebt.
     */
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
    }

    override fun onResume() {
        super.onResume()
        val filter = IntentFilter(MonitorService.ACTION_STATUS_UPDATE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(statusReceiver, filter, RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(statusReceiver, filter)
        }
    }

    override fun onPause() {
        super.onPause()
        unregisterReceiver(statusReceiver)
    }

    // ── SharedPreferences (Login-Daten + Einstellungen) ──

    private fun getEncryptedPrefs() = getSharedPreferences("at_panther_secure", MODE_PRIVATE)

    /**
     * Anforderung 1: Login-Daten plus Einstellungen (Schwelle/Intervall) speichern.
     * Schwelle/Intervall werden als String gespeichert, damit das inputType=number-
     * Feld beim Laden exakt den vom Nutzer getippten Wert zurück erhält.
     * X11Pro v1.3: Felder leben im MainFragment – uiState wird vorher synchronisiert.
     */
    private fun saveCredentials() {
        syncUiStateFromFragment()
        val prefs = getEncryptedPrefs()
        prefs.edit()
            .putString("phone", uiState.phone.value.orEmpty().trim())
            .putString("password", uiState.password.value.orEmpty().trim())
            .putString("threshold_mb", uiState.thresholdText.value.orEmpty().trim())
            .putString("interval_sec", uiState.intervalText.value.orEmpty().trim())
            .apply()
    }

    /**
     * Gespeicherte Login-Daten und Einstellungen laden. Default-Schwelle = 950 MB.
     * X11Pro v1.3: Werte landen in uiState; das Fragment bindet sie bei onViewCreated.
     */
    private fun loadCredentials() {
        val prefs = getEncryptedPrefs()
        uiState.phone.value = prefs.getString("phone", "").orEmpty()
        uiState.password.value = prefs.getString("password", "").orEmpty()
        // v1.5: Standardwert 950 MB beim ersten App-Start.
        uiState.thresholdText.value =
            prefs.getString("threshold_mb", defaultThresholdMb.toInt().toString()).orEmpty()
        uiState.intervalText.value =
            prefs.getString("interval_sec", defaultIntervalSec.toString()).orEmpty()
    }

    /** X11Pro v1.3: aktuelle Texte aus dem MainFragment in uiState spiegeln. */
    private fun syncUiStateFromFragment() {
        val frag = mainFragment ?: return
        val phoneView = frag.view?.findViewById<EditText>(R.id.etPhone)
        val passView = frag.view?.findViewById<EditText>(R.id.etPassword)
        val thrView = frag.view?.findViewById<EditText>(R.id.etThreshold)
        val intView = frag.view?.findViewById<EditText>(R.id.etInterval)
        phoneView?.text?.toString()?.let { uiState.phone.value = it }
        passView?.text?.toString()?.let { uiState.password.value = it }
        thrView?.text?.toString()?.let { uiState.thresholdText.value = it }
        intView?.text?.toString()?.let { uiState.intervalText.value = it }
    }

    private fun parseThreshold(): Float =
        uiState.thresholdText.value.orEmpty().trim().toFloatOrNull() ?: defaultThresholdMb

    private fun parseInterval(): Int =
        uiState.intervalText.value.orEmpty().trim().toIntOrNull() ?: defaultIntervalSec

    // ── Cache leeren ──

    /**
     * Anforderung 3: Loescht den lokalen App-Cache sowie (soweit moeglich)
     * die Code-Caches des Prozesses. Nach dem Loeschen wird der Status
     * vom Aufrufer per Toast bestaetigt.
     */
    private fun clearAppCache() {
        try {
            // Cache-Verzeichnis der App loeschen
            cacheDir?.let { it.deleteRecursively() }
            // Wennture eine sekundaere Cache-Dir vorhanden ist, ebenfalls leeren
            externalCacheDir?.let { it.deleteRecursively() }

            // Code-Caches (API 23+) leeren, ohne die App zu killen
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                codeCacheDir?.let { it.deleteRecursively() }
            }
        } catch (e: Exception) {
            // Einzelfehler beim Cache-Loeschen nicht crashen lassen – nur loggen
            android.util.Log.w("MainActivity", "Cache leeren teilweise fehlgeschlagen", e)
        }
    }

    // ── Log-Export via SAF ──

    /**
     * Anforderung 4: Schreibt den gesamten Log-Verlauf als Text in die per SAF
     * ausgewaehlte Datei (Uri). Header mit Erstellungszeit, danach alle
     * Log-Einträge chronologisch (aehlteste zuerst).
     */
    private fun exportLogToUri(uri: Uri) {
        val entries = currentLogEntries
        if (entries.isEmpty()) {
            Toast.makeText(this, "Kein Log-Verlauf vorhanden", Toast.LENGTH_SHORT).show()
            return
        }

        // X11Pro v1.2: Sortieren + Formatieren (bis zu 200 Einträge mit
        // String.format) lief komplett auf dem UI-Thread → sichtbarer Hänger
        // beim Tippen auf Export auf dem Helio G25. Jetzt alles auf IO,
        // Toast direkt (lifecycleScope läuft bereits auf Main).
        lifecycleScope.launch {
            val (ok, count) = withContext(Dispatchers.IO) {
                val sdf = SimpleDateFormat("dd.MM.yyyy HH:mm:ss", Locale.GERMAN)
                val sb = StringBuilder()
                sb.appendLine("AT Panther – Log-Export")
                sb.appendLine("Erstellt am: ${sdf.format(Date())}")
                sb.appendLine("Anzahl Einträge: ${entries.size}")
                sb.appendLine("────────────────────────────────────────")
                // In der DB (getRecent) ist neueste zuerst – im Export aelteste zuerst ausgeben:
                entries.sortedBy { it.timestamp }.forEach { e ->
                    val time = sdf.format(Date(e.timestamp))
                    val typeIcon = if (e.type == "BOOKING") "📦" else "📡"
                    val remaining = if (e.remainingMb >= 0) "  [${"%.1f".format(e.remainingMb)} MB]" else ""
                    sb.appendLine("$time  $typeIcon  ${e.message}$remaining")
                }
                val written = writeTextToUri(uri, sb.toString())
                written to entries.size
            }
            if (ok) {
                Toast.makeText(
                    this@MainActivity,
                    "Log exportiert ($count Einträge)",
                    Toast.LENGTH_LONG
                ).show()
            } else {
                Toast.makeText(this@MainActivity, "Export fehlgeschlagen", Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun writeTextToUri(uri: Uri, text: String): Boolean {
        return try {
            contentResolver.openOutputStream(uri, "w")?.use { os: OutputStream ->
                os.write(text.toByteArray(Charsets.UTF_8))
                os.flush()
            } ?: return false
            true
        } catch (e: Exception) {
            android.util.Log.e("MainActivity", "writeTextToUri failed", e)
            false
        }
    }

    // ── Batterie-Optimierung / DuraSpeed-Whitelist (X11Pro) ──

    /**
     * Anforderung 5: Oeffnet direkt den Systemdialog, um die App von der
     * Batterie-Optimierung auszunehmen (ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).
     * Wichtig auf dem X11Pro: Ulefones DuraSpeed + Android-12-App-Standby
     * beenden Hintergrund-Monitore aggressiv (siehe README).
     */
    private fun requestIgnoreBatteryOptimizations() {
        try {
            // Falls die App bereits auf der Whitelist steht -> nur Hinweis
            val pm = getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
            if (pm.isIgnoringBatteryOptimizations(packageName)) {
                Toast.makeText(this, "App ist bereits auf der Whitelist", Toast.LENGTH_SHORT).show()
                return
            }
            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
        } catch (e: Exception) {
            // Fallback: allgemeine Batterie-Optimierungs-Einstellungen oeffnen
            android.util.Log.w("MainActivity", "Whitelist-Dialog nicht verfügbar", e)
            try {
                val fallback = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
                startActivity(fallback)
            } catch (_: Exception) {
                Toast.makeText(
                    this,
                    "Bitte manuell unter Einstellungen > Batterie hinzufügen",
                    Toast.LENGTH_LONG
                ).show()
            }
        }
    }

    // ── Service control ──

    private fun startMonitor() {
        val state = getSharedPreferences("at_panther_monitor_state", MODE_PRIVATE)
        if (state.getBoolean("paused_after_connection_failures", false)) {
            // Erster Start-Tipp hebt die Pause NUR auf – der Monitor startet
            // erst mit dem zweiten Tipp. Verhindert versehentliche Sofort-
            // Logins nach einer Pause (Schutz vor Account-Sperre).
            state.edit()
                .putInt("consecutive_connection_failures", 0)
                .putBoolean("paused_after_connection_failures", false)
                .apply()
            Toast.makeText(
                this,
                "⛔ Pause aufgehoben — tippe erneut auf Start, um den Monitor neu zu starten",
                Toast.LENGTH_LONG
            ).show()
            uiState.statusText.value = "Pausiert — Start zum Fortsetzen"
            uiState.statusColorRes.value = R.color.status_warn
            return
        }

        syncUiStateFromFragment()
        val phone = uiState.phone.value.orEmpty().trim()
        val password = uiState.password.value.orEmpty().trim()
        if (phone.isEmpty() || password.isEmpty()) {
            Toast.makeText(this, "Bitte Rufnummer und Passwort eingeben", Toast.LENGTH_LONG).show()
            return
        }

        val threshold = parseThreshold()
        val interval = parseInterval()

        val intent = Intent(this, MonitorService::class.java).apply {
            putExtra(MonitorService.EXTRA_PHONE, phone)
            putExtra(MonitorService.EXTRA_PASSWORD, password)
            putExtra(MonitorService.EXTRA_THRESHOLD_MB, threshold)
            putExtra(MonitorService.EXTRA_INTERVAL_SEC, interval)
        }

        startForegroundService(intent)
        isServiceRunning = true
        uiState.isServiceRunning.value = true
        uiState.toggleStopMode.value = true
        uiState.statusText.value = "Starte..."
        uiState.statusColorRes.value = R.color.status_warn
        // X11Pro v1.3: Nach "Monitor starten" automatisch die Verlaufs-Seite
        // oeffnen, damit der Nutzer direkt sieht, was passiert.
        showLogPage()
    }

    private fun stopMonitor() {
        val intent = Intent(this, MonitorService::class.java).apply {
            action = MonitorService.ACTION_STOP
        }
        startService(intent)  // Send stop action
        isServiceRunning = false
        uiState.isServiceRunning.value = false
        uiState.toggleStopMode.value = false
        uiState.statusText.value = "Gestoppt"
        uiState.statusColorRes.value = R.color.text_secondary
    }
}
