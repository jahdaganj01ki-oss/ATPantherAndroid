using System.Globalization;
using System.Text;
using System.Text.Json;
using ATPanther.Core.Api;
using ATPanther.Core.Auth;

namespace ATPanther.Windows;

public sealed record LogEntry(DateTime Timestamp, string Type, double RemainingMb, string Message);

/// <summary>
/// Port of the Android <c>MonitorService.monitorLoop()</c> to a background task:
/// initial login → periodic data check → auto-booking of 1 GB below the threshold,
/// with the same pause/failure protection (3 consecutive failures or 5 relogins
/// without a successful poll stop the monitor until the user explicitly starts again).
///
/// Android semantics preserved:
///  - A failed INITIAL login does not kill the monitor: the Android service stops and
///    the fallback alarm restarts it one interval later (attempt counter persists).
///    In-process equivalent: retry once per interval until 3 consecutive failures
///    trigger the permanent pause.
///  - The Room log store is trimmed every cycle (7-day window + hard cap of
///    MAX_LOG_ROWS). The Windows port mirrors this for the history file too, so the
///    file cannot grow without bound.
///  - While the monitor runs, the system is kept awake (Android partial wake lock;
///    Windows: SetThreadExecutionState) and released when the loop ends.
/// </summary>
public sealed class MonitorController : IDisposable
{
    // Mirrors the Android constants in MonitorService.kt / MainActivity.kt.
    private const int MaxConsecutiveConnectionFailures = 3;
    private const int MaxReloginsWithoutPoll = 5;
    private const int MaxLogRows = 5000;   // DB hard cap (X11Pro freeze fix)
    private const int SevenDaysMs = 7 * 24 * 60 * 60 * 1000;
    private const int LogUiLimit = 200;    // Android UI limit (MainActivity.LOG_UI_LIMIT)

    private readonly object _gate = new();
    private readonly object _logLock = new();
    private readonly List<LogEntry> _log = new();
    private readonly string _statePath;
    private readonly string _logFilePath;

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _disposed;
    private int _appendsSinceCompact;

    private int _connectionFailures;
    private bool _pausedAfterConnectionFailures;

    public event Action<LogEntry>? LogAdded;
    public event Action<string, float>? StatusChanged;

    public bool IsRunning => _monitorTask is { IsCompleted: false };
    public bool IsPaused => _pausedAfterConnectionFailures;

    public MonitorController()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ATPanther");
        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, "monitor_state.json");
        _logFilePath = Path.Combine(folder, "history.log");

        LoadState();
        LoadHistoryFromFile();
    }

    // ── Public control surface ──

    /// <summary>
    /// Android semantics: while the monitor is paused after failures, the first
    /// "Start" tap ONLY lifts the pause (no login attempt). The second tap starts.
    /// </summary>
    public bool Start(string phone, string password, double thresholdMb, int intervalSeconds)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MonitorController));

            if (_pausedAfterConnectionFailures)
            {
                _pausedAfterConnectionFailures = false;
                _connectionFailures = 0;
                SaveStateLocked();
                DiagLog.Info("Monitor", "Pause lifted by user (first Start tap).");
                RaiseStatus("⛔ Pause aufgehoben — tippe erneut auf Start, um den Monitor zu starten", -1f);
                return false;
            }

            StopLoopLocked();

            DiagLog.Info("Monitor",
                $"Start requested (threshold {thresholdMb} MB, interval {intervalSeconds} s).");

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var task = Task.Run(
                () => MonitorLoopAsync(phone, password, thresholdMb, intervalSeconds, token),
                CancellationToken.None);
            _monitorTask = task;
        }

        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLoopLocked();
        }

        DiagLog.Info("Monitor", "Stopped by user.");
        RaiseStatus("Monitor gestoppt", -1f);
    }

    /// <summary>Lifts the pause flag without starting the monitor (manual resume flow).</summary>
    public void ResumeRequested()
    {
        lock (_gate)
        {
            _pausedAfterConnectionFailures = false;
            _connectionFailures = 0;
            SaveStateLocked();
        }

        RaiseStatus("Pause aufgehoben — klicke Start zum Fortsetzen", -1f);
    }

    // ── Monitor loop (port of monitorLoop()) ──

    private async Task MonitorLoopAsync(
        string phone,
        string password,
        double thresholdMb,
        int intervalSeconds,
        CancellationToken cancellationToken)
    {
        // Android holds a partial wake lock for as long as the foreground service
        // lives; release it again on every exit path (stop, pause, dispose).
        SystemSleep.PreventSleep();
        try
        {
            RaiseStatus("Anmelden...", -1f);

            // ── Initial login (+ contract-id resolution) ──
            // Android: a failed initial login stops the service; the fallback alarm
            // restarts it after one interval and the attempt counter persists in
            // prefs. Equivalent here: keep retrying once per interval until the
            // 3-failure pause or a user stop.
            AldiTalkApi? api = null;
            var contractId = string.Empty;
            while (!cancellationToken.IsCancellationRequested)
            {
                (api, contractId) = await PerformLoginAsync(phone, password, cancellationToken);
                if (api != null) break;

                var failures = RecordConnectionFailure();
                if (failures >= MaxConsecutiveConnectionFailures)
                {
                    PauseAndStop(
                        $"⛔ Verbindung pausiert: {failures} Fehler — bitte Monitor manuell neu starten");
                    return;
                }

                var msg = $"Login fehlgeschlagen (Verbindungsfehler {failures}/{MaxConsecutiveConnectionFailures})";
                AddLog(new LogEntry(DateTime.Now, "CHECK", -1, msg));
                RaiseStatus(msg, -1f);
                // Next attempt after one full interval (Android fallback alarm).
                await DelayAsync(intervalSeconds, cancellationToken);
            }

            if (api == null) return; // stopped while retrying

            ClearConnectionFailures();
            AddLog(new LogEntry(DateTime.Now, "CHECK", -1, "Login erfolgreich"));
            AddLog(new LogEntry(DateTime.Now, "CHECK", -1, $"Vertrags-ID erkannt: {contractId}"));

            var reloginsWithoutPoll = 0;
            var consecutiveLoginFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Clean log store (7-day window + hard cap, X11Pro freeze fix).
                    TrimLog();

                    var status = await api.GetRemainingDataAsync(contractId, cancellationToken);
                    if (status == null)
                    {
                        // Session probably expired → re-login.
                        var msg = "Datenvolumen konnte nicht abgefragt werden — re-login...";
                        AddLog(new LogEntry(DateTime.Now, "CHECK", -1, msg));
                        RaiseStatus(msg, -1f);

                        var (newApi, newContractId) = await PerformLoginAsync(phone, password, cancellationToken);
                        if (newApi != null)
                        {
                            consecutiveLoginFailures = 0;
                            reloginsWithoutPoll++;
                            if (reloginsWithoutPoll >= MaxReloginsWithoutPoll)
                            {
                                PauseAndStop(
                                    $"⛔ {reloginsWithoutPoll} Re-Logins ohne erfolgreiche Abfrage — Monitor pausiert, bitte manuell neu starten");
                                return;
                            }

                            ClearConnectionFailures();
                            // Dispose the previous session's HTTP client before swapping.
                            api.Dispose();
                            api = newApi;
                            contractId = newContractId;
                            AddLog(new LogEntry(DateTime.Now, "CHECK", -1, "Re-Login erfolgreich"));
                            RaiseStatus("Re-Login erfolgreich", -1f);
                            // Wait the normal interval before polling again to avoid a
                            // login storm against the portal.
                            await DelayAsync(intervalSeconds, cancellationToken);
                            continue;
                        }

                        consecutiveLoginFailures++;
                        var connectionFailures = RecordConnectionFailure();
                        if (connectionFailures >= MaxConsecutiveConnectionFailures)
                        {
                            PauseAndStop(
                                $"⛔ Verbindung pausiert: {connectionFailures} Fehler — bitte Monitor manuell neu starten");
                            return;
                        }

                        AddLog(new LogEntry(
                            DateTime.Now, "CHECK", -1,
                            $"Re-Login fehlgeschlagen (Versuch {consecutiveLoginFailures}; Verbindungsfehler {connectionFailures}/{MaxConsecutiveConnectionFailures})"));
                        RaiseStatus(
                            $"Re-Login fehlgeschlagen (Versuch {consecutiveLoginFailures}; Verbindungsfehler {connectionFailures}/{MaxConsecutiveConnectionFailures})", -1f);
                        await DelayAsync(intervalSeconds, cancellationToken);
                        continue;
                    }

                    // Successful poll → session alive, reset counters.
                    consecutiveLoginFailures = 0;
                    reloginsWithoutPoll = 0;
                    ClearConnectionFailures();

                    var remainingStr = status.RemainingMb.ToString("0.0", CultureInfo.InvariantCulture);
                    var checkMsg = $"Verbleibend: {remainingStr} MB";

                    if (status.RemainingMb < thresholdMb)
                    {
                        AddLog(new LogEntry(DateTime.Now, "CHECK", status.RemainingMb, checkMsg));
                        RaiseStatus("Buche 1 GB...", (float)status.RemainingMb);

                        var booking = await api.Book1GbAsync(status, cancellationToken);
                        var bookMsg = booking.Success
                            ? "✅ 1 GB erfolgreich gebucht"
                            : $"❌ Buchung fehlgeschlagen ({booking.StatusCode}): {Truncate(booking.Message, 100)}";
                        AddLog(new LogEntry(DateTime.Now, "BOOKING", status.RemainingMb, bookMsg));
                        RaiseStatus(bookMsg, (float)status.RemainingMb);
                    }
                    else
                    {
                        AddLog(new LogEntry(DateTime.Now, "CHECK", status.RemainingMb, checkMsg));
                        RaiseStatus(checkMsg, (float)status.RemainingMb);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Network/runtime errors count as connection failures too, otherwise
                    // the retry loop would run forever on WiFi/DNS problems.
                    var failures = RecordConnectionFailure();
                    if (failures >= MaxConsecutiveConnectionFailures)
                    {
                        PauseAndStop(
                            $"⛔ Verbindung pausiert: {failures} Fehler — bitte Monitor manuell neu starten");
                        return;
                    }

                    var errMsg =
                        $"Fehler: {Truncate(e.Message ?? "", 80)} (Verbindungsfehler {failures}/{MaxConsecutiveConnectionFailures})";
                    AddLog(new LogEntry(DateTime.Now, "CHECK", -1, errMsg));
                    DiagLog.Warn("Monitor", "Loop iteration failed.", e);
                    RaiseStatus(errMsg, -1f);
                }

                await DelayAsync(intervalSeconds, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception e)
        {
            DiagLog.Error("Monitor", "Monitor loop terminated with exception.", e);
            RaiseStatus($"Monitor-Fehler: {e.Message}", -1f);
        }
        finally
        {
            SystemSleep.AllowSleep();
        }
    }

    /// <summary>Login + contract-id resolution; port of performLogin().</summary>
    private async Task<(AldiTalkApi? Api, string ContractId)> PerformLoginAsync(
        string phone,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            var authenticator = new AldiTalkAuthenticator();
            var login = await authenticator.LoginAsync(phone, password, cancellationToken,
                msg => DiagLog.Info("Auth", msg));
            if (!login.Success || login.ApiClient == null)
            {
                // The detail string (Step N / PoW / OAuth / snippet) is the key
                // diagnostic: keep it in the file log, not just the status box.
                DiagLog.Warn("Monitor", "Login attempt failed: " + (login.Error ?? "?") + ".");
                RaiseStatus($"Login fehlgeschlagen: {login.Error}", -1f);
                return (null, string.Empty);
            }

            var api = new AldiTalkApi(login.ApiClient);
            var contractId = await api.ResolveContractIdAsync(phone, cancellationToken);
            if (string.IsNullOrEmpty(contractId))
            {
                DiagLog.Warn("Monitor", "Login OK, but contract id could not be resolved.");
                RaiseStatus("Vertrags-ID konnte nicht ermittelt werden", -1f);
                api.Dispose();
                return (null, string.Empty);
            }

            return (api, contractId);
        }
        catch (Exception e)
        {
            DiagLog.Warn("Monitor", "performLogin threw.", e);
            RaiseStatus($"Fehler bei performLogin: {e.Message}", -1f);
            return (null, string.Empty);
        }
    }

    // ── Pause / failure bookkeeping (SharedPreferences port) ──

    private int RecordConnectionFailure()
    {
        lock (_gate)
        {
            _connectionFailures++;
            _pausedAfterConnectionFailures = _connectionFailures >= MaxConsecutiveConnectionFailures;
            SaveStateLocked();
            return _connectionFailures;
        }
    }

    private void ClearConnectionFailures()
    {
        lock (_gate)
        {
            _connectionFailures = 0;
            _pausedAfterConnectionFailures = false;
            SaveStateLocked();
        }
    }

    private void PauseAndStop(string message)
    {
        lock (_gate)
        {
            _pausedAfterConnectionFailures = true;
            SaveStateLocked();
        }

        DiagLog.Warn("Monitor", "Monitor paused: " + message);
        AddLog(new LogEntry(DateTime.Now, "CHECK", -1, message));
        RaiseStatus(message, -1f);
        StopLoopLocked();
    }

    // ── Log store (Room DB port, bounded) ──

    public void AddLog(LogEntry entry)
    {
        lock (_logLock)
        {
            _log.Add(entry);
            while (_log.Count > MaxLogRows)
            {
                _log.RemoveAt(0);
            }

            AppendToHistoryFile(entry);
        }

        LogAdded?.Invoke(entry);
    }

    /// <summary>Newest first, capped at [max] entries (UI loads max 200 in Android).</summary>
    public IReadOnlyList<LogEntry> RecentLogs(int max = LogUiLimit)
    {
        lock (_logLock)
        {
            var result = new List<LogEntry>(_log);
            result.Reverse();
            return result.Take(max).ToList();
        }
    }

    /// <summary>
    /// Writes the recent log to a timestamped text file next to the app history.
    /// Mirrors the Android "Log exportieren" action (writes a file + shows its path).
    /// </summary>
    public bool TryExportLog(out string path)
    {
        path = string.Empty;
        var text = BuildExportText();
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            var exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ATPanther");
            Directory.CreateDirectory(exportDir);
            path = Path.Combine(exportDir, $"atpanther_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, text, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string BuildExportText()
    {
        var entries = RecentLogs(LogUiLimit).OrderBy(e => e.Timestamp).ToList();
        if (entries.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("AT Panther – Log-Export");
        sb.AppendLine($"Erstellt am: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Anzahl Einträge: {entries.Count}");
        sb.AppendLine("────────────────────────────────────────────────");
        foreach (var e in entries)
        {
            var time = e.Timestamp.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            var icon = e.Type == "BOOKING" ? "📦" : "📡";
            var remaining = e.RemainingMb >= 0
                ? $"  [{e.RemainingMb.ToString("0.0", CultureInfo.InvariantCulture)} MB]"
                : "";
            sb.AppendLine($"{time}  {icon}  {e.Message}{remaining}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Trim mirroring Room deleteOlderThan(7d) + deleteBeyondLimit(5000). Runs once per
    /// loop cycle; also compacts the persistent history file so it cannot grow forever.
    /// </summary>
    private void TrimLog()
    {
        var cutoff = DateTime.Now.AddMilliseconds(-SevenDaysMs);
        lock (_logLock)
        {
            var before = _log.Count;
            _log.RemoveAll(e => e.Timestamp < cutoff);
            var pruned = _log.Count != before;

            // Compact the file when old rows were pruned or it outgrew the hard cap.
            if (pruned || _appendsSinceCompact >= MaxLogRows)
            {
                RewriteHistoryFileLocked();
                _appendsSinceCompact = 0;
            }
        }
    }

    private void LoadHistoryFromFile()
    {
        if (!File.Exists(_logFilePath)) return;

        try
        {
            var lines = File.ReadAllLines(_logFilePath);
            lock (_logLock)
            {
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        continue;
                    }

                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var remaining);
                    _log.Add(new LogEntry(ts, parts[1], remaining, parts[3]));
                }

                while (_log.Count > MaxLogRows)
                {
                    _log.RemoveAt(0);
                }

                _appendsSinceCompact = 0;
            }
        }
        catch
        {
            // corrupted history file -> ignore, fresh start
        }
    }

    private void AppendToHistoryFile(LogEntry entry)
    {
        try
        {
            File.AppendAllText(_logFilePath, FormatLine(entry) + Environment.NewLine, Encoding.UTF8);
            _appendsSinceCompact++;
        }
        catch
        {
            // history persistence is best-effort
        }
    }

    private void RewriteHistoryFileLocked()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var e in _log)
            {
                sb.AppendLine(FormatLine(e));
            }

            File.WriteAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // best-effort compaction
        }
    }

    private static string FormatLine(LogEntry entry)
    {
        var ts = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return string.Join("|",
            ts,
            entry.Type,
            entry.RemainingMb.ToString(CultureInfo.InvariantCulture),
            entry.Message.Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' '));
    }

    // ── State persistence (SharedPreferences port) ──

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("consecutive_connection_failures", out var f))
            {
                _connectionFailures = f.GetInt32();
            }

            if (root.TryGetProperty("paused_after_connection_failures", out var p))
            {
                _pausedAfterConnectionFailures = p.GetBoolean();
            }
        }
        catch
        {
            _connectionFailures = 0;
            _pausedAfterConnectionFailures = false;
        }
    }

    private void SaveStateLocked()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                consecutive_connection_failures = _connectionFailures,
                paused_after_connection_failures = _pausedAfterConnectionFailures
            });
            File.WriteAllText(_statePath, payload, Encoding.UTF8);
        }
        catch
        {
            // best effort
        }
    }

    // ── Helpers ──

    private void StopLoopLocked()
    {
        if (_cts == null) return;
        _cts.Cancel();

        // PauseAndStop runs inside the monitor task itself; never Wait on our own task.
        if (Task.CurrentId != _monitorTask?.Id)
        {
            try
            {
                _monitorTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // cancellation
            }
        }

        _cts.Dispose();
        _cts = null;
        _monitorTask = null;
    }

    private async Task DelayAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // stop requested
        }
    }

    private void RaiseStatus(string text, float remainingMb)
    {
        StatusChanged?.Invoke(text, remainingMb);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            StopLoopLocked();
        }
    }
}
