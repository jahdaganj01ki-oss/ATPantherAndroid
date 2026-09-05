using System.Globalization;
using ATPanther.Api;
using ATPanther.Auth;
using ATPanther.Data;

namespace ATPanther.Monitor;

/// <summary>
/// Transkription von <c>service/MonitorService.kt</c>: Foreground-Service,
/// Monitor-Loop, Fehlerzähler, dauerhafte Pause, Alarm-Fallback, WakeLock.
/// Auf Windows: Engine-Klasse + NotifyIcon (PARITY.md 3.7–3.12).
/// Alle Meldungs­texte sind wörtlich übernommen, inklusive des Tippfehlers
/// „Anmelde..." in der Notification (MonitorService.kt:306).
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    /// <summary>MON:47 MAX_RELOGINS_WITHOUT_POLL.</summary>
    public const int MaxReloginsWithoutPoll = 5;

    /// <summary>MON:56–57 Defaults.</summary>
    public const float DefaultThresholdMb = 850f;
    public const int DefaultIntervalSec = 60;

    /// <summary>null hebt die dauerhafte Statusmeldung auf (stopForeground REMOVE).</summary>
    public event Action<string?>? Notification;
    public event Action<string, float>? StatusBroadcast;
    public event Action? PausedAlert;
    public event Action? PausedAlertCancelled;
    public event Action? LogChanged;
    public event Action<bool>? RunningChanged;
    public event Action<string>? Trace;

    private readonly LogStore log;
    private readonly MonitorStateStore state;
    private readonly object gate = new();

    private System.Threading.Timer? watchdog;
    private CancellationTokenSource? cancellation;
    private bool shouldRun;
    private bool isRunning;
    private bool loopActive;

    private string lastPhone = string.Empty;
    private string lastPassword = string.Empty;
    private float lastThresholdMb = DefaultThresholdMb;
    private int lastIntervalSec = DefaultIntervalSec;

    public MonitorEngine(LogStore log, MonitorStateStore state)
    {
        this.log = log;
        this.state = state;
    }

    public bool IsRunning
    {
        get { lock (gate) return isRunning; }
    }

    /// <summary>Pendant zu <c>onStartCommand</c> mit Start-Intent (MON:95–158).</summary>
    public void Start(string phone, string password, float thresholdMb, int intervalSec)
    {
        lock (gate)
        {
            // MON:112–116: pausiert => nur Meldung, kein Loop.
            if (state.IsPaused)
            {
                Notification?.Invoke("⛔ Verbindung pausiert: 3 Fehler — bitte manuell neu starten");
                StatusBroadcast?.Invoke(
                    "⛔ Verbindung pausiert: 3 Fehler — bitte Monitor manuell neu starten", -1f);
                return;
            }

            // MON:119–124: Parameter übernehmen
            if (!string.IsNullOrEmpty(phone)) lastPhone = phone;
            if (!string.IsNullOrEmpty(password)) lastPassword = password;
            lastThresholdMb = thresholdMb;
            lastIntervalSec = intervalSec;

            // MON:126–129: ohne Zugangsdaten sofortiger Stopp
            if (lastPhone.Length == 0 || lastPassword.Length == 0) return;

            shouldRun = true;
            Notification?.Invoke("Starte Monitor...");
            PausedAlertCancelled?.Invoke();
            ArmWatchdog(lastIntervalSec);

            // MON:139–141: laeuft bereits ein Job => kein zweiter Loop
            if (loopActive) return;

            isRunning = true;
            loopActive = true;   // hier gesetzt, nicht im Task: Start() darf kein zweites Mal laufen
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            KeepAwake.Acquire();
            _ = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Pendant zu <c>ACTION_STOP</c> (MON:96–102).</summary>
    public void Stop()
    {
        lock (gate)
        {
            shouldRun = false;
            CancelWatchdog();
            PausedAlertCancelled?.Invoke();
            EndLoop();
            Notification?.Invoke(null);
        }
    }

    /// <summary>Pendant zu <c>ACTION_RESET_CONNECTION_PAUSE</c> (MON:104–110).</summary>
    public void ResetConnectionPause()
    {
        lock (gate)
        {
            state.Clear();
            PausedAlertCancelled?.Invoke();
            Notification?.Invoke("Verbindungspause aufgehoben — starte neu...");
            shouldRun = false;
            CancelWatchdog();
            EndLoop();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            shouldRun = false;
            CancelWatchdog();
            EndLoop();
        }
    }

    // ── Loop ──────────────────────────────────────────────────────────────

    private sealed record Session(AldiTalkApi Api, PantherClient Client, string ContractId);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        PantherClient? client = null;

        try
        {
            Notification?.Invoke("Anmelde...");
            StatusBroadcast?.Invoke("Anmelden...", -1f);

            Session? session = await PerformLoginAsync(ct).ConfigureAwait(false);
            int reloginsWithoutPoll = 0;

            if (session == null)
            {
                int failures = state.RecordFailure();
                if (failures >= MonitorStateStore.MaxConsecutiveConnectionFailures)
                {
                    string stopMsg = PauseMessage(failures);
                    Trace?.Invoke(stopMsg);
                    InsertLog("CHECK", stopMsg);
                    Notification?.Invoke(stopMsg);
                    StatusBroadcast?.Invoke(stopMsg, -1f);
                    PauseAfterConnectionFailures();
                    return;
                }

                string msg = $"Login fehlgeschlagen (Verbindungsfehler {failures}/" +
                             $"{MonitorStateStore.MaxConsecutiveConnectionFailures})";
                Trace?.Invoke(msg);
                InsertLog("CHECK", msg);
                Notification?.Invoke(msg);
                StatusBroadcast?.Invoke(msg, -1f);
                // MON:331 stopSelf(): der Fallback-Wecker startet im naechsten Intervall.
                EndLoop();
                return;
            }

            state.Clear();
            client = session.Client;
            AldiTalkApi api = session.Api;
            string contractId = session.ContractId;

            InsertLog("CHECK", "Login erfolgreich");
            InsertLog("CHECK", $"Vertrags-ID erkannt: {contractId}");

            int consecutiveLoginFailures = 0;

            while (isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    // MON:346–352: 7 Tage Aufbewahrung + harte Zeilengrenze
                    log.DeleteOlderThan(LogStore.NowMs() - LogStore.MaxAgeMs);
                    log.DeleteBeyondLimit(LogStore.MaxLogRows);

                    DataStatus? status = await api.GetRemainingDataAsync(contractId, ct)
                        .ConfigureAwait(false);

                    if (status == null)
                    {
                        string msg = "Datenvolumen konnte nicht abgefragt werden — re-login...";
                        InsertLog("CHECK", msg, -1f);
                        Notification?.Invoke(msg);
                        StatusBroadcast?.Invoke(msg, -1f);

                        Session? relogin = await PerformLoginAsync(ct).ConfigureAwait(false);

                        if (relogin != null)
                        {
                            consecutiveLoginFailures = 0;
                            reloginsWithoutPoll++;

                            // MON:372–380 Endlosschleifen-Schutz
                            if (reloginsWithoutPoll >= MaxReloginsWithoutPoll)
                            {
                                string stopMsg = $"⛔ {reloginsWithoutPoll} Re-Logins ohne erfolgreiche " +
                                                 "Abfrage — Monitor pausiert, bitte manuell neu starten";
                                Trace?.Invoke(stopMsg);
                                InsertLog("CHECK", stopMsg);
                                Notification?.Invoke(stopMsg);
                                StatusBroadcast?.Invoke(stopMsg, -1f);
                                relogin.Client.Dispose();
                                PauseAfterConnectionFailures();
                                return;
                            }

                            state.Clear();
                            api = relogin.Api;
                            contractId = relogin.ContractId;
                            client?.Dispose();
                            client = relogin.Client;

                            Trace?.Invoke("Re-Login erfolgreich");
                            InsertLog("CHECK", "Re-Login erfolgreich");
                            Notification?.Invoke("Re-Login erfolgreich");
                            StatusBroadcast?.Invoke("Re-Login erfolgreich", -1f);

                            // MON:390 erst das Intervall abwarten, dann erneut abfragen
                            if (!await DelayAsync(lastIntervalSec, ct).ConfigureAwait(false)) break;
                            continue;
                        }

                        consecutiveLoginFailures++;
                        int connectionFailures = state.RecordFailure();
                        if (connectionFailures >= MonitorStateStore.MaxConsecutiveConnectionFailures)
                        {
                            string stopMsg = PauseMessage(connectionFailures);
                            Trace?.Invoke(stopMsg);
                            InsertLog("CHECK", stopMsg);
                            Notification?.Invoke(stopMsg);
                            StatusBroadcast?.Invoke(stopMsg, -1f);
                            PauseAfterConnectionFailures();
                            return;
                        }

                        string failMsg = $"Re-Login fehlgeschlagen (Versuch {consecutiveLoginFailures}; " +
                                         $"Verbindungsfehler {connectionFailures}/" +
                                         $"{MonitorStateStore.MaxConsecutiveConnectionFailures})";
                        InsertLog("CHECK", failMsg);
                        Notification?.Invoke(failMsg);
                        StatusBroadcast?.Invoke(failMsg, -1f);
                        if (!await DelayAsync(lastIntervalSec, ct).ConfigureAwait(false)) break;
                        continue;
                    }

                    // MON:414–417 erfolgreicher Abruf => alle Zaehler zurueck
                    consecutiveLoginFailures = 0;
                    reloginsWithoutPoll = 0;
                    state.Clear();

                    string remainingStr = status.RemainingMb.ToString("F1", CultureInfo.CurrentCulture);
                    string statusMsg = $"Verbleibend: {remainingStr} MB";
                    float remainingMb = (float)status.RemainingMb;

                    if (status.RemainingMb < lastThresholdMb)
                    {
                        InsertLog("CHECK", statusMsg, remainingMb);
                        Notification?.Invoke("Buche 1 GB...");
                        StatusBroadcast?.Invoke("Buche 1 GB...", remainingMb);

                        BookingResult booking = await api.Book1GbAsync(status, ct).ConfigureAwait(false);
                        string bookMsg = booking.Success
                            ? "✅ 1 GB erfolgreich gebucht"
                            : $"❌ Buchung fehlgeschlagen ({booking.StatusCode}): " +
                              $"{JsonText.Truncate(booking.Message, 100)}";

                        InsertLog("BOOKING", bookMsg, remainingMb);
                        Notification?.Invoke(bookMsg);
                        StatusBroadcast?.Invoke(bookMsg, remainingMb);
                    }
                    else
                    {
                        InsertLog("CHECK", statusMsg, remainingMb);
                        Notification?.Invoke(statusMsg);
                        StatusBroadcast?.Invoke(statusMsg, remainingMb);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // MON:446–464: Ausnahmen zaehlen als Verbindungsfehler
                    Trace?.Invoke("Monitor-Fehler: " + e.Message);
                    int failures = state.RecordFailure();
                    if (failures >= MonitorStateStore.MaxConsecutiveConnectionFailures)
                    {
                        string stopMsg = PauseMessage(failures);
                        InsertLog("CHECK", stopMsg);
                        Notification?.Invoke(stopMsg);
                        StatusBroadcast?.Invoke(stopMsg, -1f);
                        PauseAfterConnectionFailures();
                        return;
                    }

                    string error = string.IsNullOrEmpty(e.Message) ? "Unbekannter Fehler" : e.Message;
                    string errMsg = $"Fehler: {JsonText.Truncate(error, 80)} (Verbindungsfehler " +
                                    $"{failures}/{MonitorStateStore.MaxConsecutiveConnectionFailures})";
                    InsertLog("CHECK", errMsg);
                    Notification?.Invoke(errMsg);
                    StatusBroadcast?.Invoke(errMsg, -1f);
                }

                if (!await DelayAsync(lastIntervalSec, ct).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() waehrend eines laufenden Requests – regulärer Ausstieg.
        }
        finally
        {
            client?.Dispose();
            EndLoop();
        }
    }

    /// <summary>Pendant zu <c>performLogin</c> (MON:476–498).</summary>
    private async Task<Session?> PerformLoginAsync(CancellationToken ct)
    {
        PantherClient? client = null;
        try
        {
            var authService = new AuthService(message => Trace?.Invoke(message));
            LoginResult result = await authService.LoginAsync(lastPhone, lastPassword, ct)
                .ConfigureAwait(false);

            if (!result.Success || result.Client == null)
            {
                Trace?.Invoke("Login fehlgeschlagen: " + result.Error);
                return null;
            }

            client = result.Client;
            var api = new AldiTalkApi(client);
            string? contractId = await api.ResolveContractIdAsync(lastPhone, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(contractId))
            {
                Trace?.Invoke("Vertrags-ID konnte nicht ermittelt werden");
                client.Dispose();
                return null;
            }

            return new Session(api, client, contractId!);
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            throw;
        }
        catch (Exception e)
        {
            Trace?.Invoke("Fehler bei performLogin: " + e.Message);
            client?.Dispose();
            return null;
        }
    }

    // ── Zustandswechsel ───────────────────────────────────────────────────

    private static string PauseMessage(int failures) =>
        $"⛔ Verbindung pausiert: {failures} Fehler — bitte Monitor manuell neu starten";

    /// <summary>MON:533–538 pauseAfterConnectionFailures().</summary>
    private void PauseAfterConnectionFailures()
    {
        lock (gate)
        {
            shouldRun = false;
            CancelWatchdog();
            Notification?.Invoke(null);   // stopForeground(STOP_FOREGROUND_REMOVE)
            PausedAlert?.Invoke();        // Alarm-Benachrichtigung ID 2, ueberlebt den Stop
            EndLoop();
        }
    }

    private void InsertLog(string type, string message, float remainingMb = 0f)
    {
        log.Insert(type, message, remainingMb);
        LogChanged?.Invoke();
    }

    private static async Task<bool> DelayAsync(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds < 0 ? 0 : seconds), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// AlarmManager-Fallback (MON:257–295) als Watchdog: feuert im Intervall,
    /// startet den Loop nur bei Bedarf neu und respektiert das Pause-Flag
    /// (MonitorWakeReceiver.kt:38–41).
    /// </summary>
    private void ArmWatchdog(int intervalSec)
    {
        CancelWatchdog();
        int seconds = intervalSec < 1 ? 1 : intervalSec;
        watchdog = new System.Threading.Timer(
            _ => WatchdogTick(), null, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    private void CancelWatchdog()
    {
        watchdog?.Dispose();
        watchdog = null;
    }

    private void WatchdogTick()
    {
        lock (gate)
        {
            if (!shouldRun) return;
            if (state.IsPaused)
            {
                Trace?.Invoke("Monitor-Neustart übersprungen: Verbindung ist nach 3 Fehlern pausiert");
                shouldRun = false;
                CancelWatchdog();
                return;
            }
            if (loopActive) return;   // Service lebt noch => onStartCommand waere ein No-Op
        }

        Start(lastPhone, lastPassword, lastThresholdMb, lastIntervalSec);
    }

    /// <summary>Loop beenden, WakeLock freigeben (onDestroy, MON:167–176).</summary>
    private void EndLoop()
    {
        lock (gate)
        {
            if (!loopActive && !isRunning)
            {
                KeepAwake.Release();
                return;
            }

            isRunning = false;
            loopActive = false;

            try
            {
                cancellation?.Cancel();
                cancellation?.Dispose();
            }
            catch
            {
                // Token-Quellen sind bereits entsorgt
            }
            cancellation = null;

            KeepAwake.Release();
            RunningChanged?.Invoke(false);
        }
    }
}
