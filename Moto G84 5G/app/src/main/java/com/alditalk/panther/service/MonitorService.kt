package com.alditalk.panther.service

import android.app.*
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import com.alditalk.panther.MainActivity
import com.alditalk.panther.R
import com.alditalk.panther.api.AldiTalkApi
import com.alditalk.panther.auth.AuthService
import com.alditalk.panther.data.AppDatabase
import com.alditalk.panther.data.LogDao
import com.alditalk.panther.data.LogEntry
import com.alditalk.panther.monitor.GateResult
import com.alditalk.panther.monitor.MonitorGate
import com.alditalk.panther.warning.NoTariffWarningManager
import com.alditalk.panther.warning.WarningPrefs
import kotlinx.coroutines.*

/**
 * Foreground service that monitors ALDI Talk data volume and auto-books 1 GB
 * when remaining data drops below the threshold.
 * Sonderfall: Steht das Volumen komplett auf 0,0 MB, wird nach erfolgreicher
 * Erstbuchung – nach kurzer Pause (3 s) plus frischer Datenabfrage –
 * ein zweites Mal 1 GB gebucht (wie 2x Klick auf "+1GB" im Portal).
 *
 * Moto G84 5G-Optimierung (Motorola Moto G84 5G, Android 13, Snapdragon 695):
 *  - Läuft als Foreground Service mit permanenter sichtbarer Notification.
 *  - Hält zusätzlich einen PARTIAL_WAKE_LOCK waehrend des Monitor-Loops,
 *    damit Android-Doze/App-Standby die CPU nicht abhaengt.
 *  - Registriert einen AlarmManager-Fallback, der den Service nach Kill
 *    (z.B. durchs System) erneut startet.
 */
class MonitorService : Service() {

    companion object {
        private const val TAG = "MonitorService"
        private const val CHANNEL_ID = "at_panther_monitor"
        private const val NOTIFICATION_ID = 1

        // Aparte Alarm-Kanal/-ID: Die Pause-Meldung muss das Service-Stop
        // ueberleben (die FGS-Notification verschwindet mit stopSelf()).
        private const val CHANNEL_ID_ALERTS = "at_panther_alerts"
        private const val NOTIFICATION_ID_ALERT = 2
        private const val NOTIFICATION_ID_STANDBY = 3
        private const val MAX_CONSECUTIVE_CONNECTION_FAILURES = 3

        // Schutz vor Account-Sperre: pausiert auch den Fall "Login klappt,
        // aber die Datenafrage danach wiederholt fehlschlaegt" – ohne Cap
        // wuerde sonst bei jedem Schleifendurchlauf eine komplette
        // Login-Kette aufs Portal feuern.
        private const val MAX_RELOGINS_WITHOUT_POLL = 5
        // MotoG84-Freeze-Fix: harte Obergrenze fuer die Log-Tabelle
        private const val MAX_LOG_ROWS = 5000
        private const val PREFS_NAME = "at_panther_monitor_state"
        private const val PREF_CONNECTION_FAILURES = "consecutive_connection_failures"
        private const val PREF_PAUSED_AFTER_FAILURES = "paused_after_connection_failures"
        private const val PREF_STANDBY = "lock_standby"
        private const val WAKELOCK_TAG = "ATPanther:MonitorWake"

        // Default-Schwelle (Anforderung 2) – 850 MB
        private const val DEFAULT_THRESHOLD_MB = 850f
        private const val DEFAULT_INTERVAL_SEC = 60

        // Doppelbuchung bei komplett leerem Volumen: Anzeige "0,0 MB"
        // entspricht (durch "%.1f"-Rundung) allem < 0,05 MB. In dem Fall
        // wird nach der ersten 1-GB-Buchung – bei Erfolg – nach kurzer
        // Pause (3 s) plus frischer Datenabfrage ein zweites Mal gebucht
        // (wie 2x Klick auf "+1GB" im Portal, je mit Benachrichtigung).
        private const val ZERO_VOLUME_EPSILON_MB = 0.05
        private const val SECOND_BOOKING_DELAY_MS = 3_000L

        const val EXTRA_PHONE = "phone"
        const val EXTRA_PASSWORD = "password"
        const val EXTRA_THRESHOLD_MB = "threshold_mb"
        const val EXTRA_INTERVAL_SEC = "interval_sec"
        const val ACTION_STOP = "com.alditalk.panther.STOP"
        const val ACTION_RESET_CONNECTION_PAUSE = "com.alditalk.panther.RESET_CONNECTION_PAUSE"

        /** Broadcast action sent on status update. */
        const val ACTION_STATUS_UPDATE = "com.alditalk.panther.STATUS_UPDATE"
        const val EXTRA_STATUS_TEXT = "status_text"
        const val EXTRA_REMAINING_MB = "remaining_mb"
    }

    private var serviceJob: Job? = null
    private var serviceScope: CoroutineScope? = null
    private var isRunning = false
    private var wakeLock: PowerManager.WakeLock? = null

    // MotoG84: Snapdragon 695 mit nur 12 GB RAM – das System killt Apps bei
    // Speicherdruck schneller als flagships; ein zweiter Guard-Alarm
    // gewaehrleistet, dass der Loop auch nach WakeLock-Timeout oder
    // System-Stop weiterlaeuft. Siehe acquireWakeLock().
    private var wakeLockGuardJob: Job? = null

    /** Aktueller Parameter-Satz, damit ein AlarmManager-Restart möglich ist. */
    private var lastPhone: String = ""
    private var lastPassword: String = ""
    private var lastThresholdMb: Float = DEFAULT_THRESHOLD_MB
    private var lastIntervalSec: Int = DEFAULT_INTERVAL_SEC

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        createAlertChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            cancelFallbackAlarm()
            cancelPausedAlert()
            cancelStandbyAlert()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }

        if (intent?.action == ACTION_RESET_CONNECTION_PAUSE) {
            clearConnectionPause()
            cancelPausedAlert()
            updateNotification("Verbindungspause aufgehoben — starte neu...")
            stopSelf()
            return START_NOT_STICKY
        }

        if (isConnectionPaused()) {
            startForeground(NOTIFICATION_ID, buildNotification("⛔ Verbindung pausiert: 3 Fehler — bitte manuell neu starten"))
            broadcastStatus("⛔ Verbindung pausiert: 3 Fehler — bitte Monitor manuell neu starten", -1f)
            return START_NOT_STICKY
        }

        // Parameter aktualisieren, falls ein echter Start-Intent vorliegt
        if (intent != null && intent.action != ACTION_STOP) {
            lastPhone = intent.getStringExtra(EXTRA_PHONE) ?: lastPhone
            lastPassword = intent.getStringExtra(EXTRA_PASSWORD) ?: lastPassword
            lastThresholdMb = intent.getFloatExtra(EXTRA_THRESHOLD_MB, DEFAULT_THRESHOLD_MB)
            lastIntervalSec = intent.getIntExtra(EXTRA_INTERVAL_SEC, DEFAULT_INTERVAL_SEC)
        }

        if (lastPhone.isEmpty() || lastPassword.isEmpty()) {
            stopSelf()
            return START_NOT_STICKY
        }

        // Foreground-Status direkt sichern – sonst crasht startForegroundService
        startForeground(NOTIFICATION_ID, buildNotification("Starte Monitor..."))
        // Eine noch sichtbare Bereitschafts-Meldung aus dem letzten Lauf raeumen.
        cancelStandbyAlert()
        // Evtl. noch sichtbare Pause-Alarm-Meldung aus dem letzten Lauf raeumen.
        cancelPausedAlert()

        // Service-Laufparameter für AlarmManager-Restart merken
        scheduleFallbackAlarm(lastIntervalSec)

        if (serviceJob?.isActive == true) {
            return START_STICKY
        }

        isRunning = true
        // MotoG84-Fix: Scope als Member halten und in onDestroy() sauber
        // schliessen – sonst bleibt der SupervisorJob als Leak zurueck,
        // wenn der Service mehrfach gestoppt/gestartet wird.
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        serviceScope = scope
        serviceJob = scope.launch {
            monitorLoop(lastPhone, lastPassword, lastThresholdMb, lastIntervalSec)
        }
        // WakeLock NACH dem Scope aufnehmen – der Guard-Job haengt am Scope.
        acquireWakeLock()

        // Motorolas Moto-Doze/App-Standby killt den Prozess bei niedrigem Memory gelegentlich –
        // START_STICKY bittet das System um Neustart.
        return START_STICKY
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        // Nutzer hat die App aus dem Recents-Stack gewischt – Service
        // ueber AlarmManager wieder einplanen, damit Moto-Doze/App-Standby sie nicht beendet.
        scheduleFallbackAlarm(lastIntervalSec)
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        isRunning = false
        serviceJob?.cancel()
        wakeLockGuardJob?.cancel()
        serviceScope?.cancel()
        serviceScope = null
        releaseWakeLock()
        Log.d(TAG, "MonitorService gestoppt")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // ── WakeLock-Fallback ──

    /**
     * Partielles WakeLock halten, solange der Monitor aktiv ist.
     * Setzt voraus: android.permission.WAKE_LOCK (siehe Manifest).
     *
     * MotoG84-Anpassung: Das alte 10-Minuten-Timeout lief still aus – danach
     * schlief die CPU zwischen den 60s-Pollings ein und der Service hing.
     * Jetzt: sehr langes Timeout + Guard-Job, der alle 9 Minuten verlaengert,
     * solange der Loop lebt.
     */
    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKELOCK_TAG).apply {
                setReferenceCounted(false)
                // Langer Timeout als Sicherheitsnetz; verlaengert wird aktiv.
                acquire(30 * 60 * 1000L) // 30 Minuten
            }
        } catch (e: Exception) {
            Log.w(TAG, "WakeLock konnte nicht gehalten werden", e)
            return
        }
        startWakeLockGuard()
    }

    /**
     * Verlaengert das WakeLock alle 9 Minuten, solange der Monitor-Loop
     * aktiv ist. Laeuft der Service runter (onDestroy), wird der Guard
     * mit abgeschoessen und das WakeLock schliesslich freigegeben.
     */
    private fun startWakeLockGuard() {
        wakeLockGuardJob?.cancel()
        wakeLockGuardJob = serviceScope?.launch {
            while (isActive && isRunning) {
                delay(9 * 60 * 1000L) // 9 Minuten
                if (!isRunning) break
                try {
                    if (wakeLock?.isHeld != true) {
                        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
                        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKELOCK_TAG).apply {
                            setReferenceCounted(false)
                            acquire(30 * 60 * 1000L)
                        }
                        Log.d(TAG, "WakeLock re-acquired (Guard)")
                    } else {
                        // Re-acquire verlaengert die 30-Minuten-Frist
                        wakeLock?.acquire(30 * 60 * 1000L)
                        Log.d(TAG, "WakeLock renewed (Guard)")
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "WakeLock-Guard fehlgeschlagen", e)
                }
            }
        }
    }

    private fun releaseWakeLock() {
        try {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        } catch (_: Exception) {
            // ignore
        }
        wakeLock = null
    }

    // ── AlarmManager-Fallback ──

    /**
     * Plant einen AlarmManager-Ping, der den Service nach Ablauf des Intervalls
     * erneut startet – selbst wenn Moto-Doze/App-Standby den Job vorher beendet hat.
     *
     * Wir richten den PendingIntent gegen [MonitorWakeReceiver] (Broadcast),
     * da Hintergrund-Service-Starts unter Android 8+ (Doze/Standby) Restriktionen
     * unterliegen, ein dynamischer Broadcast-Receiver jedoch weiterhin aufwachen darf.
     */
    private fun scheduleFallbackAlarm(intervalSec: Int) {
        try {
            val alarmMgr = getSystemService(Context.ALARM_SERVICE) as AlarmManager
            val intent = Intent(this, MonitorWakeReceiver::class.java).apply {
                action = MonitorWakeReceiver.ACTION_RESTART_MONITOR
                putExtra(EXTRA_PHONE, lastPhone)
                putExtra(EXTRA_PASSWORD, lastPassword)
                putExtra(EXTRA_THRESHOLD_MB, lastThresholdMb)
                putExtra(EXTRA_INTERVAL_SEC, intervalSec)
            }
            val triggerAt = System.currentTimeMillis() + intervalSec * 1000L
            val pi = PendingIntent.getBroadcast(
                this, 0, intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            // setAndAllowWhileIdle funktioniert auch im Doze-Modus – ideal als Fallback.
            alarmMgr.setAndAllowWhileIdle(
                AlarmManager.RTC_WAKEUP, triggerAt, pi
            )
        } catch (e: Exception) {
            Log.w(TAG, "AlarmManager-Fallback konnte nicht geplant werden", e)
        }
    }

    private fun cancelFallbackAlarm() {
        try {
            val alarmMgr = getSystemService(Context.ALARM_SERVICE) as AlarmManager
            val intent = Intent(this, MonitorWakeReceiver::class.java).apply {
                action = MonitorWakeReceiver.ACTION_RESTART_MONITOR
            }
            val pi = PendingIntent.getBroadcast(
                this, 0, intent,
                PendingIntent.FLAG_NO_CREATE or PendingIntent.FLAG_IMMUTABLE
            )
            if (pi != null) alarmMgr.cancel(pi)
        } catch (_: Exception) {
            // ignore
        }
    }

    private suspend fun monitorLoop(
        phone: String,
        password: String,
        thresholdMb: Float,
        intervalSec: Int,
    ) {
        val logDao = AppDatabase.getDatabase(this).logDao()
        val gate = MonitorGate(this)

        // -- Freigabe-Gate ------------------------------------------------
        // Nur der Inhaber der Lease darf das Portal abfragen. Die Pruefung
        // steht bewusst VOR dem Login: die ForgeRock-PoW-Kette ist der
        // teuerste Teil eines Durchlaufs und wuerde sonst auf jedem Geraet
        // parallel feuern.
        updateNotification("Pruefe Freigabe...")
        broadcastStatus("Pruefe Freigabe...", -1f)
        val startGate = gate.evaluate(autoClaim = true)
        if (!startGate.allowed) {
            enterStandby(logDao, startGate)
            return
        }
        clearStandby()

        // Initialer Login
        updateNotification("Anmelde...")
        broadcastStatus("Anmelden...", -1f)

        var session = performLogin(phone, password)
        var reloginsWithoutPoll = 0
        if (session == null) {
            val failures = recordConnectionFailure()
            if (failures >= MAX_CONSECUTIVE_CONNECTION_FAILURES) {
                val stopMsg = "⛔ Verbindung pausiert: $failures Fehler — bitte Monitor manuell neu starten"
                Log.e(TAG, stopMsg)
                logDao.insert(LogEntry(type = "CHECK", message = stopMsg))
                updateNotification(stopMsg)
                broadcastStatus(stopMsg, -1f)
                // Persistente Pause: Alarm gecancelt, Boot-Restart blockiert,
                // Alarm-Benachrichtigung bleibt. Fortsetzung nur manuell.
                pauseAfterConnectionFailures()
                return
            }
            val msg = "Login fehlgeschlagen (Verbindungsfehler $failures/$MAX_CONSECUTIVE_CONNECTION_FAILURES)"
            Log.e(TAG, msg)
            logDao.insert(LogEntry(type = "CHECK", message = msg))
            updateNotification(msg)
            broadcastStatus(msg, -1f)
            // Service stoppen – der geplante Fallback-Alarm startet ihn im
            // naechsten Intervall erneut (Zaehler bleibt in Prefs erhalten).
            stopSelf()
            return
        }

        clearConnectionFailures()

        var api = session.first
        var contractId = session.second
        logDao.insert(LogEntry(type = "CHECK", message = "Login erfolgreich"))
        logDao.insert(LogEntry(type = "CHECK", message = "Vertrags-ID erkannt: $contractId"))

        var consecutiveLoginFailures = 0
        // MotoG84 v1.2: DB-Trim NICHT bei jedem 60-s-Durchlauf (2 Schreib-
        // Transaktionen pro Poll weckten die DB auf dem eMMC und triggerten
        // jedes Mal einen Flow-Requery + DiffUtil-Durchlauf in der UI).
        // Stattdessen: Alter nur ca. stündlich löschen, Limit nur bei Bedarf.
        var loopCount = 0

        while (isRunning && serviceJob?.isActive == true) {
            try {
                // Freigabe vor jedem Poll. evaluate() nutzt einen 5-Minuten-
                // Cache und kostet damit hoechstens einen Abruf / 5 min. Verliert
                // ein anderes Geraet die Freigabe, endet dieser Loop hier.
                val gateResult = gate.evaluate(autoClaim = true)
                if (!gateResult.allowed) {
                    enterStandby(logDao, gateResult)
                    return
                }
                loopCount++
                if (loopCount % 60 == 1) {
                    // ca. 1× pro Stunde: Einträge älter als 7 Tage löschen
                    val sevenDays = System.currentTimeMillis() - 7 * 24 * 3600_000L
                    logDao.deleteOlderThan(sevenDays)
                }
                if (loopCount % 10 == 1) {
                    // ca. alle 10 Minuten: nur trimmen, wenn wirklich zu voll
                    if (logDao.count() > MAX_LOG_ROWS) {
                        logDao.deleteBeyondLimit(MAX_LOG_ROWS)
                    }
                }

                // ── Unified fetch: Tarif-Status + Datenvolumen in EINEM Call ──
                val tariffStatus = api.getTariffStatus(contractId)
                if (tariffStatus == null) {
                    // Session wahrscheinlich abgelaufen -> re-login versuchen
                    val msg = "Datenvolumen konnte nicht abgefragt werden — re-login..."
                    Log.w(TAG, msg)
                    logDao.insert(LogEntry(type = "CHECK", remainingMb = -1f, message = msg))
                    updateNotification(msg)
                    broadcastStatus(msg, -1f)

                    session = performLogin(phone, password)
                    if (session != null) {
                        consecutiveLoginFailures = 0
                        reloginsWithoutPoll++
                        // Endlosschleifen-Schutz: Klappt der Login zwar, aber die
                        // Datenafrage weiterhin nicht – ohne Cap wuerde hier
                        // jede Iteration eine komplette Login-Kette aufs Portal
                        // feuern => temporaere Account-Sperre.
                        if (reloginsWithoutPoll >= MAX_RELOGINS_WITHOUT_POLL) {
                            val stopMsg = "⛔ $reloginsWithoutPoll Re-Logins ohne erfolgreiche Abfrage — Monitor pausiert, bitte manuell neu starten"
                            Log.e(TAG, stopMsg)
                            logDao.insert(LogEntry(type = "CHECK", message = stopMsg))
                            updateNotification(stopMsg)
                            broadcastStatus(stopMsg, -1f)
                            pauseAfterConnectionFailures()
                            return
                        }
                        clearConnectionFailures()
                        api = session.first
                        contractId = session.second
                        val reloginMsg = "Re-Login erfolgreich — frage Datenvolumen sofort erneut ab..."
                        Log.i(TAG, reloginMsg)
                        logDao.insert(LogEntry(type = "CHECK", message = reloginMsg))
                        updateNotification(reloginMsg)
                        broadcastStatus(reloginMsg, -1f)
                        // Kein delay: direkt erneut abfragen und ggf. nachbuchen.
                        // Schutz vor Login-Sturm: MAX_RELOGINS_WITHOUT_POLL-Cap oben
                        // pausiert bei wiederholt fehlschlagender Abfrage; der Login
                        // selbst (PoW + Redirect-Kette) dauert bereits Sekunden.
                        continue
                    } else {
                        consecutiveLoginFailures++
                        val connectionFailures = recordConnectionFailure()
                        if (connectionFailures >= MAX_CONSECUTIVE_CONNECTION_FAILURES) {
                            val stopMsg = "⛔ Verbindung pausiert: $connectionFailures Fehler — bitte Monitor manuell neu starten"
                            Log.e(TAG, stopMsg)
                            logDao.insert(LogEntry(type = "CHECK", message = stopMsg))
                            updateNotification(stopMsg)
                            broadcastStatus(stopMsg, -1f)
                            pauseAfterConnectionFailures()
                            return
                        }
                        val failMsg = "Re-Login fehlgeschlagen (Versuch $consecutiveLoginFailures; Verbindungsfehler $connectionFailures/$MAX_CONSECUTIVE_CONNECTION_FAILURES)"
                        Log.w(TAG, failMsg)
                        logDao.insert(LogEntry(type = "CHECK", message = failMsg))
                        updateNotification(failMsg)
                        broadcastStatus(failMsg, -1f)
                        delay(intervalSec * 1000L)
                        continue
                    }
                }

                // ── Guthaben-Warnsystem auswerten (WLAN-Guard + Cooldown innen) ──
                try {
                    val diagMsg = "Tarif-Check: ${tariffStatus.debugInfo} | Offers: " +
                            tariffStatus.allOfferNames.joinToString("; ").take(300)
                    Log.d(TAG, diagMsg)
                    logDao.insert(
                        LogEntry(
                            type = "CHECK",
                            remainingMb = tariffStatus.remainingMb.toFloat(),
                            message = diagMsg.take(500),
                        )
                    )
                    if (tariffStatus.shouldWarn) {
                        Log.w(TAG, "Tarif-Warnung: ${tariffStatus.debugInfo}")
                    }
                    val warned = NoTariffWarningManager.maybeWarn(this@MonitorService, tariffStatus)
                    if (warned) {
                        val rm = tariffStatus.remainingMb.takeIf { it >= 0 }?.toFloat() ?: -1f
                        val warnMsg = "⚠ Warnung: ${NoTariffWarningManager.WARNING_TEXT.take(120)}"
                        logDao.insert(LogEntry(type = "WARN", remainingMb = rm, message = warnMsg))
                    } else if (!tariffStatus.shouldWarn) {
                        WarningPrefs.clearIfTariffActive(this@MonitorService)
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Tariff-Check Auswertung fehlgeschlagen (ignoriert)", e)
                }

                val status = tariffStatus.primaryDataStatus
                if (status == null) {
                    val noDataMsg = when {
                        tariffStatus.rawOfferCount == 0 ->
                            "Kein Tarif gebucht — keine Buchung möglich (Guthaben-Risiko)"
                        tariffStatus.uncertain || (!tariffStatus.shouldWarn) ->
                            "Tarif aktiv (unklassifiziert: ${tariffStatus.allOfferNames.joinToString("; ").take(150)}) — keine Auto-Buchung möglich, aber Guthaben geschützt"
                        else ->
                            "Kein Daten-Pack im aktiven Offer — Warnung aktiv, keine Auto-Buchung"
                    }
                    Log.w(TAG, noDataMsg)
                    logDao.insert(LogEntry(type = "CHECK", remainingMb = tariffStatus.remainingMb.toFloat(), message = noDataMsg))
                    updateNotification(noDataMsg)
                    broadcastStatus(noDataMsg, tariffStatus.remainingMb.toFloat())
                    delay(intervalSec * 1000L)
                    continue
                }

                // Erfolgreicher Abruf -> Session lebt, Counter reset
                consecutiveLoginFailures = 0
                reloginsWithoutPoll = 0
                clearConnectionFailures()

                val remainingStr = "%.1f".format(status.remainingMb)
                val msg = "Verbleibend: $remainingStr MB"

                if (status.remainingMb < thresholdMb) {
                    Log.w(TAG, "$msg — unter Schwelle ($thresholdMb MB), buche 1 GB")
                    logDao.insert(LogEntry(type = "CHECK", remainingMb = status.remainingMb.toFloat(), message = msg))

                    // Book 1 GB
                    updateNotification("Buche 1 GB...")
                    broadcastStatus("Buche 1 GB...", status.remainingMb.toFloat())

                    val booking = api.book1Gb(status)
                    val bookMsg = if (booking.success) {
                        "✅ 1 GB erfolgreich gebucht"
                    } else {
                        "❌ Buchung fehlgeschlagen (${booking.statusCode}): ${booking.message.take(100)}"
                    }
                    logDao.insert(LogEntry(type = "BOOKING", remainingMb = status.remainingMb.toFloat(), message = bookMsg))
                    updateNotification(bookMsg)
                    broadcastStatus(bookMsg, status.remainingMb.toFloat())

                    // Sonderfall: Volumen komplett auf 0,0 MB -> 2 Mal
                    // hintereinander buchen (wie 2x Klick auf "+1GB" im Portal,
                    // je mit eigener Benachrichtigung). Vor der 2. Buchung wird
                    // das Volumen frisch abgefragt, damit sie mit aktuellem
                    // offer-/resource-State laeuft (Fallback: alte status-Daten,
                    // falls die Zwischenabfrage fehlschlaegt).
                    // Nur bei erfolgreicher Erstbuchung, sonst wuerde die
                    // Zweitbuchung denselben Fehler nur wiederholen.
                    if (booking.success && status.remainingMb < ZERO_VOLUME_EPSILON_MB) {
                        val pauseMsg = "Volumen 0,0 MB — zweite 1-GB-Buchung in ${SECOND_BOOKING_DELAY_MS / 1000} s..."
                        Log.w(TAG, pauseMsg)
                        logDao.insert(LogEntry(type = "CHECK", remainingMb = status.remainingMb.toFloat(), message = pauseMsg))
                        updateNotification(pauseMsg)
                        broadcastStatus(pauseMsg, status.remainingMb.toFloat())

                        delay(SECOND_BOOKING_DELAY_MS)
                        // Service koennte waehrend der Pause gestoppt worden sein.
                        if (!isRunning || serviceJob?.isActive != true) return

                        val refreshMsg = "Frage Volumen vor 2. Buchung erneut ab..."
                        Log.d(TAG, refreshMsg)
                        logDao.insert(LogEntry(type = "CHECK", remainingMb = status.remainingMb.toFloat(), message = refreshMsg))
                        updateNotification(refreshMsg)
                        broadcastStatus(refreshMsg, status.remainingMb.toFloat())

                        val freshStatus = api.getRemainingData(contractId) ?: status
                        if (freshStatus !== status) {
                            val freshStr = "%.1f".format(freshStatus.remainingMb)
                            val freshMsg = "Verbleibend vor 2. Buchung: $freshStr MB"
                            Log.d(TAG, freshMsg)
                            logDao.insert(LogEntry(type = "CHECK", remainingMb = freshStatus.remainingMb.toFloat(), message = freshMsg))
                        } else {
                            val staleMsg = "Zwischenabfrage fehlgeschlagen — 2. Buchung mit vorherigen Daten"
                            Log.w(TAG, staleMsg)
                            logDao.insert(LogEntry(type = "CHECK", remainingMb = status.remainingMb.toFloat(), message = staleMsg))
                        }

                        updateNotification("Buche 2. GB (0,0 MB)...")
                        broadcastStatus("Buche 2. GB (0,0 MB)...", freshStatus.remainingMb.toFloat())

                        val secondBooking = api.book1Gb(freshStatus)
                        val secondMsg = if (secondBooking.success) {
                            "✅ 2. GB erfolgreich gebucht (0,0 MB-Doppelbuchung)"
                        } else {
                            "❌ 2. Buchung fehlgeschlagen (${secondBooking.statusCode}): ${secondBooking.message.take(100)}"
                        }
                        Log.w(TAG, secondMsg)
                        logDao.insert(LogEntry(type = "BOOKING", remainingMb = freshStatus.remainingMb.toFloat(), message = secondMsg))
                        updateNotification(secondMsg)
                        broadcastStatus(secondMsg, freshStatus.remainingMb.toFloat())
                    }
                } else {
                    Log.d(TAG, msg)
                    logDao.insert(LogEntry(type = "CHECK", remainingMb = status.remainingMb.toFloat(), message = msg))
                    updateNotification(msg)
                    broadcastStatus(msg, status.remainingMb.toFloat())
                }

            } catch (e: Exception) {
                Log.e(TAG, "Monitor-Fehler", e)
                // Netzwerk-/Laufzeitfehler zaehlen ebenfalls als
                // Verbindungsfehler – sonst laeuft der Retry-Loop bei
                // WLAN-/DNS-Problemen endlos weiter (kein null-Pfad).
                val failures = recordConnectionFailure()
                if (failures >= MAX_CONSECUTIVE_CONNECTION_FAILURES) {
                    val stopMsg = "⛔ Verbindung pausiert: $failures Fehler — bitte Monitor manuell neu starten"
                    Log.e(TAG, stopMsg)
                    logDao.insert(LogEntry(type = "CHECK", message = stopMsg))
                    updateNotification(stopMsg)
                    broadcastStatus(stopMsg, -1f)
                    pauseAfterConnectionFailures()
                    return
                }
                val errMsg = "Fehler: ${e.message?.take(80)} (Verbindungsfehler $failures/$MAX_CONSECUTIVE_CONNECTION_FAILURES)"
                logDao.insert(LogEntry(type = "CHECK", message = errMsg))
                updateNotification(errMsg)
                broadcastStatus(errMsg, -1f)
            }

            delay(intervalSec * 1000L)
        }
    }

    /**
     * Fuehrt Login + Vertrags-ID-Ermittlung durch.
     * Liefert (api, contractId) bei Erfolg, null bei Misserfolg.
     * Wird sowohl fuer den initialen Login als auch fuer Re-Logins verwendet.
     */
    private suspend fun performLogin(
        phone: String,
        password: String,
    ): Pair<AldiTalkApi, String>? {
        return try {
            val authService = AuthService()
            val loginResult = authService.login(phone, password)
            if (!loginResult.success || loginResult.client == null) {
                Log.e(TAG, "Login fehlgeschlagen: ${loginResult.error}")
                return null
            }
            val api = AldiTalkApi(loginResult.client)
            val contractId = api.resolveContractId(phone)
            if (contractId.isNullOrEmpty()) {
                Log.e(TAG, "Vertrags-ID konnte nicht ermittelt werden")
                return null
            }
            api to contractId
        } catch (e: Exception) {
            Log.e(TAG, "Fehler bei performLogin", e)
            null
        }
    }

    private fun monitorState() = getSharedPreferences(PREFS_NAME, MODE_PRIVATE)

    private fun isConnectionPaused(): Boolean =
        monitorState().getBoolean(PREF_PAUSED_AFTER_FAILURES, false)

    private fun recordConnectionFailure(): Int {
        val failures = monitorState().getInt(PREF_CONNECTION_FAILURES, 0) + 1
        monitorState().edit()
            .putInt(PREF_CONNECTION_FAILURES, failures)
            .putBoolean(PREF_PAUSED_AFTER_FAILURES, failures >= MAX_CONSECUTIVE_CONNECTION_FAILURES)
            .apply()
        return failures
    }

    private fun clearConnectionFailures() {
        monitorState().edit()
            .putInt(PREF_CONNECTION_FAILURES, 0)
            .putBoolean(PREF_PAUSED_AFTER_FAILURES, false)
            .apply()
    }

    private fun clearConnectionPause() = clearConnectionFailures()

    /**
     * Endgueltige Pause nach [MAX_CONSECUTIVE_CONNECTION_FAILURES] fehlgeschlagenen
     * Verbindungs-/Login-Versuchen hintereinander (oder nach dem Re-Login-Cap):
     *  - Pause-Flag persistiert in Prefs (blockiert Fallback-Alarm & Boot-Restart,
     *    siehe [MonitorWakeReceiver])
     *  - Fallback-Wecker wird gecancelt – KEIN automatischer Neustart mehr
     *  - Aparte High-Priority-Benachrichtigung (ID 2), die das Service-Stop
     *    ueberlebt (die FGS-Notification verschwindet mit stopSelf())
     *  - Service-Stop; Fortsetzung nur durch manuellen Start in der App
     */
    private fun pauseAfterConnectionFailures() {
        cancelFallbackAlarm()
        stopForeground(STOP_FOREGROUND_REMOVE)
        showPausedAlert()
        stopSelf()
    }

    private fun showPausedAlert() {
        try {
            val contentIntent = Intent(this, MainActivity::class.java)
            val pendingIntent = PendingIntent.getActivity(
                this, 1, contentIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val notification = NotificationCompat.Builder(this, CHANNEL_ID_ALERTS)
                .setContentTitle("AT Panther pausiert")
                .setContentText("Login/Verbindung 3x fehlgeschlagen — Automatik gestoppt")
                .setStyle(
                    NotificationCompat.BigTextStyle().bigText(
                        "Login/Verbindung ist 3x hintereinander fehlgeschlagen — " +
                            "der Monitor versucht es NICHT weiter automatisch. " +
                            "Zum Fortsetzen App öffnen und Monitor neu starten."
                    )
                )
                .setSmallIcon(android.R.drawable.ic_dialog_alert)
                .setColor(getColor(R.color.primary))
                .setPriority(NotificationCompat.PRIORITY_MAX)
                .setCategory(NotificationCompat.CATEGORY_ALARM)
                .setAutoCancel(true)
                .setContentIntent(pendingIntent)
                .build()
            getSystemService(NotificationManager::class.java)
                .notify(NOTIFICATION_ID_ALERT, notification)
        } catch (e: Exception) {
            Log.w(TAG, "Pause-Benachrichtigung fehlgeschlagen", e)
        }
    }

    private fun cancelPausedAlert() {
        try {
            getSystemService(NotificationManager::class.java)
                .cancel(NOTIFICATION_ID_ALERT)
        } catch (_: Exception) {
            // ignore
        }
    }    // -- Freigabe / Bereitschaftsmodus --

    /**
     * Bereitschaftsmodus: eine andere Variante haelt die Freigabe, oder der
     * Freigabe-Server ist nicht erreichbar. Es wird bewusst KEIN Login
     * versucht - das Portal bleibt unberuehrt. Der Fallback-Wecker wird
     * abbestellt, damit nicht im Minutentakt neu gestartet wird; weiter geht
     * es erst, wenn die Freigabe aktiv uebernommen wird.
     *
     *  - Log-Eintrag, damit der Zustand im Verlauf nachvollziehbar bleibt
     *  - eigene Benachrichtigung, die das Service-Stop ueberlebt
     *  - Status-Flag blockiert Boot- und AlarmManager-Neustarts
     */
    private suspend fun enterStandby(logDao: LogDao, result: GateResult) {
        Log.w(TAG, "Bereitschaftsmodus: ${result.detail}")
        val msg = "⏸ Bereitschaft – ${result.detail}"
        try {
            logDao.insert(LogEntry(type = "CHECK", message = msg))
        } catch (e: Exception) {
            Log.w(TAG, "Standby-Log fehlgeschlagen", e)
        }
        cancelFallbackAlarm()
        stopForeground(STOP_FOREGROUND_REMOVE)
        showStandbyAlert(msg)
        setStandby(true)
        stopSelf()
    }

    private fun showStandbyAlert(msg: String) {
        try {
            val contentIntent = Intent(this, MainActivity::class.java)
            val pendingIntent = PendingIntent.getActivity(
                this, 2, contentIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val notification = NotificationCompat.Builder(this, CHANNEL_ID_ALERTS)
                .setContentTitle("AT Panther im Bereitschaftsmodus")
                .setContentText(msg)
                .setStyle(NotificationCompat.BigTextStyle().bigText(
                    "$msg\n\nDiese Variante fragt das ALDI-Talk-Portal NICHT ab. " +
                        "In der App bei „Freigabe“ auf „Übernehmen“ tippen, um die " +
                        "Überwachung auf dieses Gerät zu holen."
                ))
                .setSmallIcon(android.R.drawable.ic_menu_share)
                .setColor(getColor(R.color.primary))
                .setPriority(NotificationCompat.PRIORITY_DEFAULT)
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setAutoCancel(true)
                .setContentIntent(pendingIntent)
                .build()
            getSystemService(NotificationManager::class.java)
                .notify(NOTIFICATION_ID_STANDBY, notification)
        } catch (e: Exception) {
            Log.w(TAG, "Bereitschafts-Benachrichtigung fehlgeschlagen", e)
        }
    }

    private fun cancelStandbyAlert() {
        try {
            getSystemService(NotificationManager::class.java)
                .cancel(NOTIFICATION_ID_STANDBY)
        } catch (_: Exception) {
            // ignore
        }
    }

    private fun setStandby(standby: Boolean) {
        monitorState().edit().putBoolean(PREF_STANDBY, standby).apply()
    }

    private fun clearStandby() = setStandby(false)


    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = getString(R.string.channel_description)
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun createAlertChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID_ALERTS,
                getString(R.string.channel_alerts_name),
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = getString(R.string.channel_alerts_description)
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
        // Guthaben-Warnung (Heads-Up) – eigener Kanal mit HIGH Importance
        com.alditalk.panther.warning.NoTariffWarningManager.ensureChannel(this)
    }

    /**
     * Foreground-Notification – permanent, sichtbar, mit Tap-Target MainActivity.
     * Monochrom-Akzent-Farbe im Black Theme (colorPrimary) fuer konsistentes Look&Feel.
     */
    private fun buildNotification(text: String): Notification {
        val contentIntent = Intent(this, MainActivity::class.java)
        val pendingIntent = PendingIntent.getActivity(
            this, 0, contentIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("AT Panther")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_compass)
            .setColor(getColor(R.color.primary))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(pendingIntent)
            .build()
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun broadcastStatus(statusText: String, remainingMb: Float) {
        val intent = Intent(ACTION_STATUS_UPDATE).apply {
            putExtra(EXTRA_STATUS_TEXT, statusText)
            putExtra(EXTRA_REMAINING_MB, remainingMb)
            setPackage(packageName)
        }
        sendBroadcast(intent)
    }
}
