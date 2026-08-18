using ATPanther.AldiTalk;

namespace ATPanther.Monitor;

/// <summary>
/// Hintergrund-Monitor – Port von MonitorService.kt (Android):
/// Login → Datenvolumen abfragen → unter Schwelle 1 GB buchen → wiederholen.
/// Re-Login bei abgelaufener Session, max. 5 aufeinanderfolgende Fehlversuche,
/// Log-Retention 7 Tage. Events werden auf dem Worker-Thread gefeuert; die UI
/// marshallt selbst auf den UI-Thread (BeginInvoke).
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    public const int MaxConsecutiveLoginFailures = 5;
    public const int MinIntervalSec = 10;

    public event Action<LogEntry>? LogAdded;
    public event Action<string, double?>? StatusChanged;

    public bool IsRunning { get; private set; }

    /// <summary>Letzter Status (für Tray-Tooltip etc.).</summary>
    public string LastStatus { get; private set; } = "Gestoppt";
    public double? LastRemainingMb { get; private set; }

    private CancellationTokenSource? _cts;
    private readonly AuthService _authService = new();

    public void Start(string phone, string password, float thresholdMb, int intervalSec)
    {
        Stop();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => MonitorLoopAsync(phone, password, thresholdMb, intervalSec, token));
    }

    public void Stop()
    {
        IsRunning = false;
        // Nur canceln, nicht auf die Task warten: Stop() wird auch aus der
        // Loop selbst heraus aufgerufen (Login-Fehler); ein Wait auf die
        // eigene Task würde blockieren. Die Loop beendet sich über
        // Token-Checks nach jedem await.
        _cts?.Cancel();
        _cts = null;
    }

    private void FireStatus(string text, double? remainingMb)
    {
        LastStatus = text;
        LastRemainingMb = remainingMb;
        StatusChanged?.Invoke(text, remainingMb);
    }

    private void FireLog(string type, float remainingMb, string message)
    {
        LogStore.Add(type, remainingMb, message);
        LogAdded?.Invoke(LogStore.GetAll()[0]);
    }

    private async Task MonitorLoopAsync(
        string phone,
        string password,
        float thresholdMb,
        int intervalSec,
        CancellationToken token)
    {
        var intervalMs = Math.Max(MinIntervalSec, intervalSec) * 1000;

        // Initialer Login
        FireStatus("Anmelden...", null);
        var session = await PerformLoginAsync(phone, password).ConfigureAwait(false);
        if (session == null)
        {
            var msg = "Login fehlgeschlagen (siehe Log)";
            FireLog("CHECK", -1f, msg);
            FireStatus(msg, null);
            Stop();
            return;
        }

        var api = session.Value.Api;
        var contractId = session.Value.ContractId;
        FireLog("CHECK", -1f, "Login erfolgreich");
        FireLog("CHECK", -1f, "Vertrags-ID erkannt: " + contractId);

        var consecutiveLoginFailures = 0;

        try
        {
        while (IsRunning && !token.IsCancellationRequested)
        {
            try
            {
                // Datenstatus abfragen (die 7-Tage-Retention übernimmt LogStore bei jedem Eintrag)
                var status = await api.GetRemainingDataAsync(contractId).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (status == null)
                {
                    // Session wahrscheinlich abgelaufen → Re-Login versuchen
                    var msg = "Datenvolumen konnte nicht abgefragt werden — re-login...";
                    FireLog("CHECK", -1f, msg);
                    FireStatus(msg, null);

                    session = await PerformLoginAsync(phone, password).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    if (session != null)
                    {
                        consecutiveLoginFailures = 0;
                        api.Dispose();
                        api = session.Value.Api;
                        contractId = session.Value.ContractId;
                        FireLog("CHECK", -1f, "Re-Login erfolgreich");
                        FireStatus("Re-Login erfolgreich", null);
                        continue; // direkt weiter zum nächsten Abruf, nicht warten
                    }

                    consecutiveLoginFailures++;
                    if (consecutiveLoginFailures >= MaxConsecutiveLoginFailures)
                    {
                        var stopMsg = "Re-Login 5x fehlgeschlagen, Monitor gestoppt";
                        FireLog("CHECK", -1f, stopMsg);
                        FireStatus(stopMsg, null);
                        Stop();
                        return;
                    }

                    var failMsg = $"Re-Login fehlgeschlagen (Versuch {consecutiveLoginFailures}/{MaxConsecutiveLoginFailures})";
                    FireLog("CHECK", -1f, failMsg);
                    FireStatus(failMsg, null);
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    continue;
                }

                // Erfolgreicher Abruf → Session lebt, Zähler zurücksetzen
                consecutiveLoginFailures = 0;

                var remainingStr = status.RemainingMb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                var volumeMsg = "Verbleibend: " + remainingStr + " MB";
                var remainingFloat = (float)status.RemainingMb;

                if (status.RemainingMb < thresholdMb)
                {
                    FireLog("CHECK", remainingFloat, volumeMsg);

                    FireStatus("Buche 1 GB...", status.RemainingMb);
                    var booking = await api.Book1GbAsync(status).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    var bookMsg = booking.Success
                        ? "✅ 1 GB erfolgreich gebucht"
                        : $"❌ Buchung fehlgeschlagen ({booking.StatusCode}): {Truncate(booking.Message, 100)}";

                    FireLog("BOOKING", remainingFloat, bookMsg);
                    FireStatus(bookMsg, status.RemainingMb);
                }
                else
                {
                    FireLog("CHECK", remainingFloat, volumeMsg);
                    FireStatus(volumeMsg, status.RemainingMb);
                }

                await Task.Delay(intervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // Stopp angefordert
            }
            catch (Exception e)
            {
                var errMsg = "Fehler: " + Truncate(e.Message, 80);
                FireLog("CHECK", -1f, errMsg);
                FireStatus(errMsg, null);
                try
                {
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        }
        finally
        {
            // Session-Client (HttpClient + Cookies) immer freigeben
            api.Dispose();
        }
    }

    /// <summary>Login + Vertrags-ID-Ermittlung; null bei Misserfolg.</summary>
    private async Task<(AldiTalkApi Api, string ContractId)?> PerformLoginAsync(string phone, string password)
    {
        try
        {
            var loginResult = await _authService.LoginAsync(phone, password).ConfigureAwait(false);
            if (!loginResult.Success || loginResult.Client == null) return null;

            var api = new AldiTalkApi(loginResult.Client);
            var contractId = await api.ResolveContractIdAsync(phone).ConfigureAwait(false);
            if (string.IsNullOrEmpty(contractId))
            {
                loginResult.Client.Dispose();
                return null;
            }
            return (api, contractId);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        Stop();
    }
}
