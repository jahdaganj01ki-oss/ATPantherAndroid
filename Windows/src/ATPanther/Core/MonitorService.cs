namespace ATPanther.Core;

/// <summary>1:1-Port des MonitorService-Loops (Ulefone-Variante).</summary>
public sealed class MonitorService
{
    public event Action<string, float>? StatusChanged;
    public event Action<string>? LogAdded;
    public event Action? Paused;
    /// <summary>Wird ausgeloest, wenn eine andere Variante die Monitor-Freigabe haelt.</summary>
    public event Action<GateResult>? Standby;
    /// <summary>Wird ausgeloest wenn kein Basis-Tarif und kein Add-on aktiv ist (Guthaben-Risiko).</summary>
    public event Action<TariffStatus>? NoTariffWarning;

    private readonly AppDb _db;
    private readonly MonitorStateStore _state;
    private readonly WarningPrefs _warnings = new();
    private readonly MonitorGate _gate = new();
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
        FileLogger.Info($"Monitor: start phone={phone} threshold={thresholdMb} interval={intervalSec}s");
        _ = RunAsync(phone, password, thresholdMb, intervalSec, _cts.Token);
    }

    public void Stop() 
    { 
        _cts?.Cancel(); 
        _cts = null;
        FileLogger.Info("Monitor: stopped");
    }

    private void Log(string type, float remaining, string message)
    {
        _db.Insert(type, remaining, message);
        LogAdded?.Invoke(message);
    }

    private void Status(string text, float remaining = -1)
        => StatusChanged?.Invoke(remaining >= 0 ? $"{text}  ({remaining:F1} MB)" : text, remaining);

    /// <summary>
    /// Bereitschaftsmodus: eine andere Variante haelt die Freigabe, oder der
    /// Freigabe-Server ist nicht erreichbar. Es wird bewusst KEIN Login
    /// versucht - das Portal bleibt unberuehrt. Der Loop endet hier; weiter
    /// geht es erst, wenn die Freigabe aktiv uebernommen wird.
    /// </summary>
    private void EnterStandby(GateResult result)
    {
        var msg = $"⏸ Bereitschaft – {result.Detail}";
        FileLogger.Warning($"Monitor: standby ({result.Detail})");
        Status(msg);
        Log("CHECK", 0, msg);
        App.ShowTrayNotification("AT Panther", $"Bereitschaft: {result.Detail}");
        Standby?.Invoke(result);
        Stop();
    }

    private async Task RunAsync(string phone, string password, float thresholdMb, int intervalSec, CancellationToken ct)
    {
        var auth = new AuthService();
        int failures = 0, relogins = 0;
        DateTime lastAgeTrim = DateTime.MinValue, lastLimitTrim = DateTime.MinValue;

        // ── Freigabe-Gate ─────────────────────────────────────────────────
        // Nur der Inhaber der Lease darf das Portal abfragen. Die Pruefung steht
        // bewusst VOR dem Login: die ForgeRock-PoW-Kette ist der teuerste Teil
        // eines Durchlaufs und wuerde sonst auf mehreren Geraeten parallel feuern.
        Status("Pruefe Freigabe...");
        var startGate = await _gate.EvaluateAsync(autoClaim: true, ct).ConfigureAwait(false);
        if (!startGate.Allowed)
        {
            EnterStandby(startGate);
            return;
        }

        // Nach einer FRISCHEN Uebernahme kurz warten, bevor das Portal zum
        // ersten Mal angefasst wird. Das alte Geraet kann noch mitten in einem
        // Durchlauf stehen; ein sofortiger erster Poll wuerde die Abfragen zu
        // dicht aufeinander legen (Sperr-Risiko).
        if (startGate.CooldownMs > 0)
        {
            var waitSec = startGate.CooldownMs / 1000;
            var waitMsg = $"Freigabe uebernommen – warte {waitSec} s, bevor das Portal abgefragt wird";
            FileLogger.Info($"Monitor: cooldown {waitSec}s");
            Status(waitMsg);
            Log("CHECK", 0, waitMsg);
            try
            {
                await Task.Delay((int)Math.Min(startGate.CooldownMs, int.MaxValue), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return; // Nutzer hat den Monitor gestoppt
            }
        }

        Status("Verbinde...");
        var login = await auth.LoginAsync(phone, password, ct).ConfigureAwait(false);
        if (!login.Success)
        {
            Status($"Login fehlgeschlagen: {login.Error}");
            Log("CHECK", 0, $"Login fehlgeschlagen: {login.Error}");
            FileLogger.Warning($"Monitor: login failed error={login.Error}");
            App.ShowTrayNotification("AT Panther", $"Login fehlgeschlagen: {login.Error}");
            Stop();
            return;
        }
        var api = new AldiTalkApi(login.Client!);
        var contractId = await api.ResolveContractIdAsync(phone, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(contractId))
        {
            Status("Vertrags-ID konnte nicht ermittelt werden");
            Log("CHECK", 0, "Vertrags-ID konnte nicht ermittelt werden");
            FileLogger.Warning("Monitor: contractId resolution failed");
            Stop();
            return;
        }
        Log("CHECK", 0, "Login erfolgreich");
        Log("CHECK", 0, $"Vertrags-ID erkannt: {contractId}");
        FileLogger.Info($"Monitor: login ok contractId={contractId}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Freigabe vor jedem Poll. EvaluateAsync nutzt einen 5-Minuten-Cache
                // und kostet damit hoechstens einen Abruf / 5 min. Verliert ein
                // anderes Geraet die Freigabe, endet dieser Loop hier.
                var gateResult = await _gate.EvaluateAsync(autoClaim: true, ct).ConfigureAwait(false);
                if (!gateResult.Allowed)
                {
                    EnterStandby(gateResult);
                    return;
                }

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

                // ── Unified fetch: Tarif-Status + Datenvolumen in EINEM Call ──
                TariffStatus? tariff = null;
                try { tariff = await api.GetTariffStatusAsync(contractId, ct).ConfigureAwait(false); }
                catch { tariff = null; }
                DataStatus? status = tariff?.PrimaryDataStatus;

                if (tariff == null)
                {
                    failures++; relogins++;
                    _state.Save(new MonitorState(failures, false));
                    FileLogger.Warning($"Monitor: data query returned null failures={failures} relogins={relogins}");
                    if (failures >= AppConfig.MaxConsecutiveConnectionFailures || relogins >= AppConfig.MaxReloginsWithoutPoll)
                    {
                        _state.Save(new MonitorState(failures, true));
                        Status("⛔ AT Panther pausiert");
                        Log("CHECK", 0, "⛔ Monitor pausiert nach wiederholten Fehlern. Manuell neu starten.");
                        Paused?.Invoke();
                        FileLogger.Warning("Monitor: paused after repeated failures");
                        App.ShowTrayNotification("AT Panther", "Monitor pausiert nach wiederholten Fehlern");
                        Stop();
                        break;
                    }
                    var msg = $"⚠️ Datenabfrage fehlgeschlagen, erneuter Login (Versuch {relogins}/{AppConfig.MaxReloginsWithoutPoll})";
                    Status(msg); Log("CHECK", 0, msg);
                    FileLogger.Warning($"Monitor: data query failed, relogin attempt={relogins}");
                    login = await auth.LoginAsync(phone, password, ct).ConfigureAwait(false);
                    if (!login.Success) 
                    { 
                        FileLogger.Warning($"Monitor: relogin failed error={login.Error}");
                        await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false); 
                        continue; 
                    }
                    api = new AldiTalkApi(login.Client!);
                    contractId = await api.ResolveContractIdAsync(phone, ct).ConfigureAwait(false) ?? contractId;
                    FileLogger.Info($"Monitor: relogin ok contractId={contractId}");
                    continue;
                }

                // ── Guthaben-Warnsystem auswerten (No-Spam via WarningPrefs) ──
                // Diagnostik mit allen Offer-Namen ins Log, damit Fehlklassifikationen
                // (z.B. Surf-Ticket Unlimited mit unerwarteten Feldnamen) sichtbar werden.
                try
                {
                    var diagMsg = $"Tarif-Check: {tariff.DebugInfo} | Offers: {string.Join("; ", tariff.AllOfferNames ?? new List<string>())}";
                    if (diagMsg.Length > 300) diagMsg = diagMsg.Substring(0, 300);
                    FileLogger.Info($"Monitor: {diagMsg}");
                    Log("CHECK", (float)tariff.RemainingMb,
                        diagMsg.Length > 500 ? diagMsg.Substring(0, 500) : diagMsg);
                    if (tariff.ShouldWarn)
                    {
                        FileLogger.Warning($"Monitor: Tarif-Warnung: {tariff.DebugInfo}");
                        if (_warnings.CanShowWarning(tariff))
                        {
                            _warnings.RecordWarningShown(tariff);
                            var warnMsg = $"⚠ Warnung: {NoTariffWarningText.Value.Substring(0, Math.Min(120, NoTariffWarningText.Value.Length))}";
                            Log("WARN", tariff.RemainingMb >= 0 ? (float)tariff.RemainingMb : -1f, warnMsg);
                            NoTariffWarning?.Invoke(tariff);
                        }
                        else
                        {
                            FileLogger.Info($"Monitor: Warnung gedrosselt ({_warnings.GetStateForDebug()})");
                        }
                    }
                    else
                    {
                        _warnings.ClearIfTariffActive();
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Warning($"Monitor: Tariff-Check Auswertung fehlgeschlagen (ignoriert) error={ex.Message}");
                }

                if (status == null)
                {
                    var noDataMsg = tariff.RawOfferCount == 0
                        ? "Kein Tarif gebucht — keine Buchung möglich (Guthaben-Risiko)"
                        : (tariff.Uncertain || !tariff.ShouldWarn)
                            ? $"Tarif aktiv (unklassifiziert: {string.Join("; ", tariff.AllOfferNames ?? new List<string>())}) — keine Auto-Buchung möglich, aber Guthaben geschützt"
                            : "Kein Daten-Pack im aktiven Offer — Warnung aktiv, keine Auto-Buchung";
                    var noDataMsgShort = noDataMsg.Length > 150 ? noDataMsg.Substring(0, 150) : noDataMsg;
                    FileLogger.Warning($"Monitor: {noDataMsgShort}");
                    Log("CHECK", (float)tariff.RemainingMb, noDataMsg);
                    Status(noDataMsgShort, (float)tariff.RemainingMb);
                    await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false);
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
                            var freshTariff = await api.GetTariffStatusAsync(contractId, ct).ConfigureAwait(false);
                            var fresh = freshTariff?.PrimaryDataStatus;
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
                        App.ShowTrayNotification("AT Panther", $"Buchung fehlgeschlagen ({booking.StatusCode})");
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
                FileLogger.Error(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Warn-Text wie in der Android-Variante (NoTariffWarningManager.WARNING_TEXT).
    /// Lazy, damit Tests ohne WPF-Dependencies laufen.
    /// </summary>
    internal static class NoTariffWarningText
    {
        internal const string Value = "Aktuell ist kein Datentarif gebucht. Verbrauch von Datenvolumen kostet jetzt direkt Guthaben. " +
            "Möchten Sie weiterhin Internet nutzen oder das Internet abschalten?";
    }
}
