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
import com.alditalk.panther.data.LogEntry
import kotlinx.coroutines.*

/**
 * Foreground service that monitors ALDI Talk data volume and auto-books 1 GB
 * when remaining data drops below the threshold.
 *
 * Anforderung 5 – Optimierung für Huawei AGS2-L09 (Android 8.0 / EMUI):
 *  - Läuft als Foreground Service mit permanenter sichtbarer Notification.
 *  - Hält zusätzlich einen PARTIAL_WAKE_LOCK waehrend des Monitor-Loops,
 *    damit EMUI's aggressives Stromspar-Management die CPU nicht abhaengt.
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
        private const val MAX_CONSECUTIVE_CONNECTION_FAILURES = 3

        // Schutz vor Account-Sperre: pausiert auch den Fall "Login klappt,
        // aber die Datenafrage danach wiederholt fehlschlaegt" – ohne Cap
        // wuerde sonst bei jedem Schleifendurchlauf eine komplette
        // Login-Kette aufs Portal feuern.
        private const val MAX_RELOGINS_WITHOUT_POLL = 5
        // Freeze-Fix: harte Obergrenze fuer die Log-Tabelle
        private const val MAX_LOG_ROWS = 5000
        private const val PREFS_NAME = "at_panther_monitor_state"
        private const val PREF_CONNECTION_FAILURES = "consecutive_connection_failures"
        private const val PREF_PAUSED_AFTER_FAILURES = "paused_after_connection_failures"
        private const val WAKELOCK_TAG = "ATPanther:MonitorWake"

        // Default-Schwelle (Anforderung 2) – 850 MB
        private const val DEFAULT_THRESHOLD_MB = 850f
        private const val DEFAULT_INTERVAL_SEC = 60

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

    // Redmi: MIUI killt Hintergrund-Apps und Foreground-Services aggressiv
    // (Akkusparen, RAM-Aufraeumen im Recents-Screen); ein zweiter Guard-Alarm
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
        // Evtl. noch sichtbare Pause-Alarm-Meldung aus dem letzten Lauf raeumen.
        cancelPausedAlert()

        // Service-Laufparameter für AlarmManager-Restart merken
        scheduleFallbackAlarm(lastIntervalSec)

        if (serviceJob?.isActive == true) {
            return START_STICKY
        }

        isRunning = true
        // Freeze-Fix: Scope als Member halten und in onDestroy() sauber
        // schliessen – sonst bleibt der SupervisorJob als Leak zurueck,
        // wenn der Service mehrfach gestoppt/gestartet wird.
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        serviceScope = scope
        serviceJob = scope.launch {
            monitorLoop(lastPhone, lastPassword, lastThresholdMb, lastIntervalSec)
        }
        // WakeLock NACH dem Scope aufnehmen – der Guard-Job haengt am Scope.
        acquireWakeLock()

        // EMUI killt den Prozess bei niedrigem Memory gelegentlich –
        // START_STICKY bittet das System um Neustart.
        return START_STICKY
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        // Nutzer hat die App aus dem Recents-Stack gewischt – Service
        // ueber AlarmManager wieder einplanen, damit EMUI sie nicht beendet.
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
     * Freeze-Fix: Das alte 10-Minuten-Timeout lief still aus – danach
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
     * erneut startet – selbst wenn EMUI den Job vorher beendet hat.
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
        // Redmi Note 9 Pro v1.2: DB-Trim NICHT bei jedem 60-s-Durchlauf (2 Schreib-
        // Transaktionen pro Poll weckten die DB auf dem eMMC und triggerten
        // jedes Mal einen Flow-Requery + DiffUtil-Durchlauf in der UI).
        // Stattdessen: Alter nur ca. stündlich löschen, Limit nur bei Bedarf.
        var loopCount = 0

        while (isRunning && serviceJob?.isActive == true) {
            try {
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

                // Fetch data status
                val status = api.getRemainingData(contractId)
                if (status == null) {
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
                        Log.i(TAG, "Re-Login erfolgreich")
                        logDao.insert(LogEntry(type = "CHECK", message = "Re-Login erfolgreich"))
                        updateNotification("Re-Login erfolgreich")
                        broadcastStatus("Re-Login erfolgreich", -1f)
                        // Erst das normale Intervall abwarten, dann erneut abfragen –
                        // sonst feuern Login-Ketten ohne Pause aufs Portal.
                        delay(intervalSec * 1000L)
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
    }

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
    }

    /**
     * Foreground-Notification – permanent, sichtbar, mit Tap-Target MainActivity.
     * Violett-Akzent-Farbe im Black Theme (colorPrimary) fuer konsistentes Look&Feel.
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
