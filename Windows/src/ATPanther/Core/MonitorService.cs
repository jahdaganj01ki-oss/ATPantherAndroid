namespace ATPanther.Core;

/// <summary>1:1-Port des MonitorService-Loops (Ulefone-Variante).</summary>
public sealed class MonitorService
{
    public event Action<string, float>? StatusChanged;
    public event Action<string>? LogAdded;
    public event Action? Paused;

    private readonly AppDb _db;
    private readonly MonitorStateStore _state;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null;

    public MonitorService(AppDb db, MonitorStateStore state)
    {
        _db = db;
        _state = state;
    }

    public void Start(string phone, string password, float thresholdMb, int intervalSec)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(phone, password, thresholdMb, intervalSec, _cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private void Log(string type, float remaining, string message)
    {
        _db.Insert(type, remaining, message);
        LogAdded?.Invoke(message);
    }

    private void Status(string text, float remaining = -1)
        => StatusChanged?.Invoke(remaining >= 0 ? $"{text}  ({remaining:F1} MB)" : text, remaining);
    private async Task RunAsync(string phone, string password, float thresholdMb, int intervalSec, CancellationToken ct)
    {
        var auth = new AuthService();
        int failures = 0, relogins = 0;
        DateTime lastAgeTrim = DateTime.MinValue, lastLimitTrim = DateTime.MinValue;

        Status("Verbinde...");
        var login = await auth.LoginAsync(phone, password, ct).ConfigureAwait(false);
        if (!login.Success)
        {
            Status($"Login fehlgeschlagen: {login.Error}");
            Log("CHECK", 0, $"Login fehlgeschlagen: {login.Error}");
            Stop();
            return;
        }
        var api = new AldiTalkApi(login.Client!);
        var contractId = await api.ResolveContractIdAsync(phone, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(contractId))
        {
            Status("Vertrags-ID konnte nicht ermittelt werden");
            Log("CHECK", 0, "Vertrags-ID konnte nicht ermittelt werden");
            Stop();
            return;
        }
        Log("CHECK", 0, "Login erfolgreich");
        Log("CHECK", 0, $"Vertrags-ID erkannt: {contractId}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastAgeTrim > TimeSpan.FromHours(1))
                {
                    _db.DeleteOlderThan(DateTimeOffset.UtcNow.AddDays(-AppConfig.LogRetentionDays).ToUnixTimeMilliseconds());
                    lastAgeTrim = DateTime.UtcNow;
                }
                if (DateTime.UtcNow - lastLimitTrim > TimeSpan.FromMinutes(10) && _db.Count() > AppConfig.MaxLogRows)
                {
                    _db.DeleteBeyondLimit(AppConfig.MaxLogRows);
                    lastLimitTrim = DateTime.UtcNow;
                }

                DataStatus? status = null;
                try { status = await api.GetRemainingDataAsync(contractId, ct).ConfigureAwait(false); }
                catch { status = null; }

                if (status == null)
                {
                    failures++; relogins++;
                    _state.Save(new MonitorState(failures, false));
                    if (failures >= AppConfig.MaxConsecutiveConnectionFailures || relogins >= AppConfig.MaxReloginsWithoutPoll)
                    {
                        _state.Save(new MonitorState(failures, true));
                        Status("⛔ AT Panther pausiert");
                        Log("CHECK", 0, "⛔ Monitor pausiert nach wiederholten Fehlern. Manuell neu starten.");
                        Paused?.Invoke();
                        Stop();
                        break;
                    }
                    var msg = $"⚠️ Datenabfrage fehlgeschlagen, erneuter Login (Versuch {relogins}/{AppConfig.MaxReloginsWithoutPoll})";
                    Status(msg); Log("CHECK", 0, msg);
                    login = await auth.LoginAsync(phone, password, ct).ConfigureAwait(false);
                    if (!login.Success) { await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false); continue; }
                    api = new AldiTalkApi(login.Client!);
                    contractId = await api.ResolveContractIdAsync(phone, ct).ConfigureAwait(false) ?? contractId;
                    continue;
                }

                failures = 0; relogins = 0;
                _state.Save(new MonitorState(0, false));
                Status($"📡 Datenstand: {status.RemainingMb:F1} MB", (float)status.RemainingMb);
                Log("CHECK", (float)status.RemainingMb, $"📡 Datenstand: {status.RemainingMb:F1} MB");

                if (status.RemainingMb < thresholdMb)
                {
                    var booking = await api.Book1GbAsync(status, ct).ConfigureAwait(false);
                    if (booking.Success)
                    {
                        Log("BOOKING", (float)status.RemainingMb, $"📦 +1 GB automatisch gebucht (Rest war {status.RemainingMb:F1} MB)");
                        Status("📦 +1 GB gebucht", (float)status.RemainingMb);
                        if (status.RemainingMb < AppConfig.ZeroVolumeEpsilonMb)
                        {
                            await Task.Delay(AppConfig.SecondBookingDelayMs, ct).ConfigureAwait(false);
                            var fresh = await api.GetRemainingDataAsync(contractId, ct).ConfigureAwait(false);
                            if (fresh != null)
                            {
                                var second = await api.Book1GbAsync(fresh, ct).ConfigureAwait(false);
                                Log("BOOKING", (float)fresh.RemainingMb,
                                    second.Success ? "📦 +1 GB erneut gebucht (Volumen war 0,0 MB)" : $"❌ Zweitbuchung: {second.StatusCode} {second.Message}");
                            }
                        }
                    }
                    else
                    {
                        Log("BOOKING", (float)status.RemainingMb, $"❌ Buchung: {booking.StatusCode} {booking.Message}");
                        Status($"❌ Buchung fehlgeschlagen ({booking.StatusCode})", (float)status.RemainingMb);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                _state.Save(new MonitorState(failures, false));
                Status($"Fehler: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }
}
