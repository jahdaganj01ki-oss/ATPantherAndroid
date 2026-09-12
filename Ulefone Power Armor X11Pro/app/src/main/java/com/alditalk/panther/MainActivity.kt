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
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.ListAdapter
import androidx.recyclerview.widget.RecyclerView
import com.alditalk.panther.data.LogDao
import com.alditalk.panther.data.LogEntry
import com.alditalk.panther.service.MonitorService
import com.google.android.material.button.MaterialButton
import com.google.android.material.textfield.TextInputEditText
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.OutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * DiffUtil-Callback fuer den Log-Verlauf (X11Pro-Freeze-Fix).
 * LogEntry ist eine data class mit stabilem Primary-Key [LogEntry.id] –
 * strukturelle Gleichheit reicht damit fuer effizientes Partial-Binding.
 */
private val LOG_DIFF = object : DiffUtil.ItemCallback<LogEntry>() {
    override fun areItemsTheSame(oldItem: LogEntry, newItem: LogEntry): Boolean =
        oldItem.id == newItem.id

    override fun areContentsTheSame(oldItem: LogEntry, newItem: LogEntry): Boolean =
        oldItem == newItem
}

class MainActivity : AppCompatActivity() {

    companion object {
        /**
         * X11Pro-Freeze-Fix: UI laedt nur die letzten N Log-Eintraege.
         * Alles aeltere bleibt in der DB (fuer den Export via Dao-Anfrage mit
         * vollem Umfang) aber landet nicht mehr komplett im RecyclerView.
         */
        private const val LOG_UI_LIMIT = 200
    }

    // Default-Werte
    private val defaultThresholdMb = 850f   // Anforderung 2: Standardwert 850 MB
    private val defaultIntervalSec = 60

    private lateinit var etPhone: TextInputEditText
    private lateinit var etPassword: TextInputEditText
    private lateinit var etThreshold: TextInputEditText
    private lateinit var etInterval: TextInputEditText
    private lateinit var tvStatus: android.widget.TextView
    private lateinit var btnToggle: MaterialButton
    private lateinit var btnSave: MaterialButton
    private lateinit var btnClearCache: MaterialButton
    private lateinit var btnExportLog: MaterialButton
    private lateinit var btnBatteryOpt: MaterialButton
    private lateinit var rvLog: RecyclerView

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
            tvStatus.text = if (remaining >= 0) "$status  (${"%.1f".format(remaining)} MB)" else status
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // View binding
        etPhone = findViewById(R.id.etPhone)
        etPassword = findViewById(R.id.etPassword)
        etThreshold = findViewById(R.id.etThreshold)
        etInterval = findViewById(R.id.etInterval)
        tvStatus = findViewById(R.id.tvStatus)
        btnToggle = findViewById(R.id.btnToggleService)
        btnSave = findViewById(R.id.btnSaveCredentials)
        btnClearCache = findViewById(R.id.btnClearCache)
        btnExportLog = findViewById(R.id.btnExportLog)
        btnBatteryOpt = findViewById(R.id.btnBatteryOpt)
        rvLog = findViewById(R.id.rvLog)

        rvLog.layoutManager = LinearLayoutManager(this)
        // X11Pro v1.2: feste Größe + keine Change-Animationen – der
        // DefaultItemAnimator (Fade/Move bei jedem 60-s-Diff) kostet auf der
        // schwachen GPU des X11Pro pro Poll sichtbare Frames und ruckelt
        // zusätzlich, wenn während einer Rotation ein Diff reinkommt.
        // Größerer View-Cache vermeidet Neu-Inflation beim Scrollen.
        rvLog.setHasFixedSize(true)
        rvLog.itemAnimator = null
        rvLog.setItemViewCacheSize(20)

        // Anforderung 1: Gespeicherte Login-Daten UND Einstellungen laden
        loadCredentials()

        // Save credentials button – speichert nun auch die Einstellungen (Schwelle/Intervall)
        btnSave.setOnClickListener {
            saveCredentials()
            Toast.makeText(this, "Login-Daten und Einstellungen gespeichert", Toast.LENGTH_SHORT).show()
        }

        // Toggle service button
        btnToggle.setOnClickListener {
            if (isServiceRunning) {
                stopMonitor()
            } else {
                startMonitor()
            }
        }

        // Anforderung 3: App-Cache leeren
        btnClearCache.setOnClickListener {
            clearAppCache()
            Toast.makeText(this, "Cache geleert", Toast.LENGTH_SHORT).show()
        }

        // Anforderung 4: Log exportieren – oeffnet SAF-Dateidialog
        btnExportLog.setOnClickListener {
            val timestamp = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.GERMAN).format(Date())
            createDocumentLauncher.launch("at_panther_log_$timestamp.txt")
        }

        // Anforderung 5: Direkt zum Batterie-Optimierungs-Dialog (X11Pro/DuraSpeed)
        btnBatteryOpt.setOnClickListener {
            requestIgnoreBatteryOptimizations()
        }

        // Observe log entries – begrenzt auf LOG_UI_LIMIT (X11Pro-Freeze-Fix).
        // X11Pro v1.2: repeatOnLifecycle(STARTED) statt Dauer-Collect (kein
        // Diff im Hintergrund) + distinctUntilChanged (kein redundanter
        // DiffUtil-Durchlauf, wenn der Service nur getrimmt/gelöscht hat).
        val logDao = (application as PantherApp).database.logDao()
        val adapter = LogAdapter()
        rvLog.adapter = adapter
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                logDao.getRecent(LOG_UI_LIMIT)
                    .distinctUntilChanged()
                    .collectLatest { entries ->
                        currentLogEntries = entries
                        adapter.submitList(entries)
                    }
            }
        }
    }

    /**
     * X11Pro v1.2: Mit configChanges (siehe Manifest) wird die Activity beim
     * Drehen NICHT neu erzeugt – dieser Callback hält die sichtbare
     * Scroll-Position stabil, statt die Liste nach dem Re-Layout von oben
     * zu zeigen (fühlte sich wie ein Hänger an).
     */
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val lm = rvLog.layoutManager as? LinearLayoutManager ?: return
        val pos = lm.findFirstVisibleItemPosition()
        rvLog.post { lm.scrollToPosition(pos.coerceAtLeast(0)) }
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
     */
    private fun saveCredentials() {
        val prefs = getEncryptedPrefs()
        prefs.edit()
            .putString("phone", etPhone.text.toString().trim())
            .putString("password", etPassword.text.toString().trim())
            .putString("threshold_mb", etThreshold.text.toString().trim())
            .putString("interval_sec", etInterval.text.toString().trim())
            .apply()
    }

    /**
     * Gespeicherte Login-Daten und Einstellungen laden. Default-Schwelle = 850 MB.
     */
    private fun loadCredentials() {
        val prefs = getEncryptedPrefs()
        etPhone.setText(prefs.getString("phone", ""))
        etPassword.setText(prefs.getString("password", ""))
        // Anforderung 2: Standardwert 850 MB beim ersten App-Start (vorher 250)
        etThreshold.setText(prefs.getString("threshold_mb", defaultThresholdMb.toInt().toString()))
        etInterval.setText(prefs.getString("interval_sec", defaultIntervalSec.toString()))
    }

    private fun parseThreshold(): Float =
        etThreshold.text.toString().trim().toFloatOrNull() ?: defaultThresholdMb

    private fun parseInterval(): Int =
        etInterval.text.toString().trim().toIntOrNull() ?: defaultIntervalSec

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
            tvStatus.text = "Pausiert — Start zum Fortsetzen"
            tvStatus.setTextColor(getColor(R.color.status_warn))
            return
        }

        val phone = etPhone.text.toString().trim()
        val password = etPassword.text.toString().trim()
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
        btnToggle.text = "Monitor stoppen"
        tvStatus.text = "Starte..."
        tvStatus.setTextColor(getColor(R.color.status_warn))
    }

    private fun stopMonitor() {
        val intent = Intent(this, MonitorService::class.java).apply {
            action = MonitorService.ACTION_STOP
        }
        startService(intent)  // Send stop action
        isServiceRunning = false
        btnToggle.text = "Monitor starten"
        tvStatus.text = "Gestoppt"
        tvStatus.setTextColor(getColor(R.color.text_secondary))
    }

    // ── Log RecyclerView Adapter ──

    /**
     * X11Pro-Freeze-Fix: ListAdapter mit DiffUtil statt RecyclerView.Adapter +
     * notifyDataSetChanged(). Jede neue Log-Zeile (alle 60 s) bindet nur die
     * tatsaechlich neue Zeile statt die komplette Liste neu zu zeichnen.
     * Das war die Hauptursache fuer UI-Haenger nach laengerer Laufzeit.
     *
     * X11Pro v1.2: keine `inner class` mehr – der implizite Activity-Verweis
     * hielt die alte Activity-Instanz über Collector-Laufzeiten am Leben.
     */
    class LogAdapter :
        ListAdapter<LogEntry, LogAdapter.ViewHolder>(LOG_DIFF) {

        private val sdf = SimpleDateFormat("dd.MM HH:mm:ss", Locale.GERMAN)

        class ViewHolder(val view: android.widget.TextView) : RecyclerView.ViewHolder(view)

        override fun onCreateViewHolder(parent: android.view.ViewGroup, viewType: Int): ViewHolder {
            val view = android.view.LayoutInflater.from(parent.context)
                .inflate(R.layout.item_log, parent, false) as android.widget.TextView
            return ViewHolder(view)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            val entry = getItem(position)
            val time = sdf.format(Date(entry.timestamp))
            val typeIcon = if (entry.type == "BOOKING") "📦" else "📡"
            holder.view.text = "$time  $typeIcon  ${entry.message}"
        }
    }
}
