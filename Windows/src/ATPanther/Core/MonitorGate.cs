using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATPanther.Core;

/// <summary>Ergebniszustand der Freigabe-Abfrage.</summary>
public enum GateStatus
{
    /// <summary>Dieses Geraet darf das Portal abfragen.</summary>
    Allowed,
    /// <summary>Ein anderes Geraet haelt die Freigabe -&gt; Bereitschaftsmodus.</summary>
    NotOwner,
    /// <summary>Niemand haelt die Freigabe (oder sie ist abgelaufen).</summary>
    Free,
    /// <summary>Keine Worker-URL eingetragen.</summary>
    NotConfigured,
    /// <summary>Freigabe-Server nicht erreichbar, kein brauchbarer Cache.</summary>
    Unreachable,
    /// <summary>Unerwarteter Fehler.</summary>
    Error,
}

/// <summary>Ergebnis einer Freigabe-Abfrage - <see cref="GateResult.Allowed"/> entscheidet ueber den Portal-Kontakt.</summary>
public sealed record GateResult(bool Allowed, GateStatus Status, string? Owner, string Detail);

/// <summary>Stand der Lease, wie der Worker ihn meldet.</summary>
public sealed record LockState(string? Owner, string? DeviceId, long AcquiredAt, long ExpiresAt, long UpdatedAt, long Now)
{
    public static readonly LockState Empty = new(null, null, 0, 0, 0, 0);
}

/// <summary>
/// Reine Entscheidungslogik der Freigabe - ohne Netz und ohne Dateisystem,
/// damit sie direkt im Testprojekt geprueft werden kann. Die Android-Variante
/// (MonitorGate.kt) bildet genau dieselben Faelle ab.
/// </summary>
public static class LockRules
{
    public static GateResult Decide(LockState state, string deviceId, string variantId, long now, string? prefix = null)
    {
        GateResult result;

        if (state.DeviceId == deviceId && state.ExpiresAt > now)
        {
            result = new GateResult(true, GateStatus.Allowed, variantId, $"Freigabe aktiv: {variantId}");
        }
        else if (string.IsNullOrEmpty(state.Owner))
        {
            result = new GateResult(false, GateStatus.Free, null, "Freigabe ist frei - Uebernehmen tippen");
        }
        else if (state.ExpiresAt <= now)
        {
            result = new GateResult(false, GateStatus.Free, state.Owner,
                $"Freigabe von {state.Owner} abgelaufen - Uebernehmen tippen");
        }
        else
        {
            result = new GateResult(false, GateStatus.NotOwner, state.Owner,
                $"Bereitschaft: {state.Owner} fragt ab");
        }

        if (string.IsNullOrEmpty(prefix)) return result;
        return result with { Detail = result.Detail + prefix };
    }
}

/// <summary>
/// Zentrale Monitor-Freigabe fuer die Windows-Variante: nur der Inhaber der
/// Lease darf das ALDI-Talk-Portal abfragen. Port von MonitorGate.kt.
///
/// - hoechstens ein Netzabruf pro <see cref="AppConfig.LockCheckIntervalMs"/>
/// - der Inhaber verlaengert die Lease bei jedem Abruf
/// - faellt ein Geraet aus, uebernimmt nach Ablauf automatisch ein anderes
/// - Ausfallverhalten fail-closed (ausser FailOpen ist gesetzt)
/// </summary>
public sealed class MonitorGate
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly LockStore _store = new();

    public string VariantId => AppConfig.VariantId;

    /// <summary>Geraete-ID innerhalb der Variante (stabil pro Rechner).</summary>
    public string DeviceId { get; } = BuildDeviceId();

    public MonitorGate() { }

    public string WorkerUrl => LockStore.NormalizeUrl(_store.LoadSettings().WorkerUrl);

    public bool IsConfigured() => WorkerUrl.Length > 0;

    public void SaveSettings(string url, string token, bool failOpen) =>
        _store.SaveSettings(new LockSettings { WorkerUrl = url, WorkerToken = token, FailOpen = failOpen });

    public LockSettings LoadSettings() => _store.LoadSettings();

    private static string BuildDeviceId()
    {
        var raw = Environment.MachineName;
        var sb = new StringBuilder(AppConfig.VariantId.Length + 1 + raw.Length);
        sb.Append(AppConfig.VariantId).Append('-');
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        }
        var name = sb.ToString();
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>Sofortiger UI-Stand aus dem Cache - blockiert nie.</summary>
    public GateResult CachedStatus()
    {
        if (!IsConfigured()) return NotConfigured();
        var cache = _store.LoadCache();
        if (cache is null) return new GateResult(false, GateStatus.Free, null, "Freigabe noch nicht abgefragt");
        return LockRules.Decide(ToState(cache), DeviceId, VariantId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Freigabe pruefen. Wird vor dem Login und vor jedem Poll aufgerufen.
    /// </summary>
    /// <param name="autoClaim">true = freie/abgelaufene Freigabe sofort uebernehmen.</param>
    public async Task<GateResult> EvaluateAsync(bool autoClaim = true, CancellationToken ct = default)
    {
        var settings = _store.LoadSettings();
        var url = LockStore.NormalizeUrl(settings.WorkerUrl);
        if (url.Length == 0) return NotConfigured();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cache = _store.LoadCache();
        if (cache is not null && now - cache.At < AppConfig.LockCheckIntervalMs)
        {
            var cached = LockRules.Decide(ToState(cache), DeviceId, VariantId, now);
            // Freigegeben genuegt der Cache. Nicht freigegeben mit autoClaim
            // braucht einen echten Aufruf - nur der Server kann die Lease setzen.
            if (cached.Allowed || !autoClaim) return cached;
        }

        return await SyncAsync(url, settings, autoClaim, steal: false, ct);
    }

    /// <summary>"Uebernehmen": verdraengt einen anderen Inhaber sofort.</summary>
    public Task<GateResult> ClaimAsync(CancellationToken ct = default)
    {
        var settings = _store.LoadSettings();
        var url = LockStore.NormalizeUrl(settings.WorkerUrl);
        if (url.Length == 0) return Task.FromResult(NotConfigured());
        return SyncAsync(url, settings, claimIfFree: true, steal: true, ct);
    }

    /// <summary>"Freigeben": nur der Inhaber gibt seine Lease ab.</summary>
    public async Task<GateResult> ReleaseAsync(CancellationToken ct = default)
    {
        var settings = _store.LoadSettings();
        var url = LockStore.NormalizeUrl(settings.WorkerUrl);
        if (url.Length == 0) return NotConfigured();

        try
        {
            var payload = new Dictionary<string, object?> { ["deviceId"] = DeviceId };
            var json = await PostAsync(url, "/release", payload, settings, ct);
            var state = ReadState(json);
            if (state is null) return Unreachable(settings);
            _store.SaveCache(new CachedLock
            {
                Owner = state.Owner,
                DeviceId = state.DeviceId,
                ExpiresAt = state.ExpiresAt,
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            return new GateResult(false, GateStatus.Free, state.Owner, "Freigabe abgegeben");
        }
        catch (Exception ex)
        {
            FileLogger.Warning($"MonitorGate: release fehlgeschlagen ({ex.Message})");
            return Unreachable(settings);
        }
    }

    private async Task<GateResult> SyncAsync(string url, LockSettings settings, bool claimIfFree, bool steal, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["deviceId"] = DeviceId,
            ["variant"] = VariantId,
            ["ttlSeconds"] = AppConfig.LockTtlSeconds,
            ["claimIfFree"] = claimIfFree,
            ["steal"] = steal,
        };

        try
        {
            var json = await PostAsync(url, "/sync", payload, settings, ct);
            var state = ReadState(json);
            if (state is null) return Unreachable(settings);

            _store.SaveCache(new CachedLock
            {
                Owner = state.Owner,
                DeviceId = state.DeviceId,
                ExpiresAt = state.ExpiresAt,
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = LockRules.Decide(state, DeviceId, VariantId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!steal) return result;
            return result.Allowed
                ? result with { Detail = $"Freigabe uebernommen: {VariantId}" }
                : new GateResult(false, result.Status, result.Owner,
                    $"Uebernehmen fehlgeschlagen - {result.Owner ?? "unbekannt"} ist aktiv");
        }
        catch (Exception ex)
        {
            FileLogger.Warning($"MonitorGate: sync fehlgeschlagen ({ex.Message})");
            return Unreachable(settings);
        }
    }

    private async Task<string> PostAsync(string url, string path, Dictionary<string, object?> payload, LockSettings settings, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url + path);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        if (!string.IsNullOrWhiteSpace(settings.WorkerToken))
        {
            request.Headers.TryAddWithoutValidation("X-Lock-Token", settings.WorkerToken);
        }
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        return body;
    }

    private static LockState? ReadState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean()) return null;
            if (!doc.RootElement.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.Object)
                return null;

            return new LockState(
                Str(stateEl, "owner"),
                Str(stateEl, "deviceId"),
                Num(stateEl, "acquiredAt"),
                Num(stateEl, "expiresAt"),
                Num(stateEl, "updatedAt"),
                Num(stateEl, "now"));
        }
        catch (JsonException ex)
        {
            FileLogger.Warning($"MonitorGate: Antwort unlesbar ({ex.Message})");
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static LockState ToState(CachedLock c) => new(c.Owner, c.DeviceId, 0, c.ExpiresAt, 0, 0);

    private static GateResult NotConfigured() => new(false, GateStatus.NotConfigured, null,
        "Keine Freigabe-URL eingetragen - unten bei 'Freigabe' eintragen");

    /// <summary>
    /// Ausfallbehandlung. fail-closed ist der Standard: ein nicht erreichbarer
    /// Freigabe-Server bedeutet "nicht abfragen", weil genau doppelte Abfragen
    /// das Konto gefaehrden. Der letzte bekannte Stand gilt fuer
    /// <see cref="AppConfig.LockCacheGraceMs"/>, damit ein kurzer Netzausfall
    /// den Monitor nicht sofort anhaelt.
    /// </summary>
    private GateResult Unreachable(LockSettings settings)
    {
        if (settings.FailOpen)
        {
            return new GateResult(true, GateStatus.Error, _store.LoadCache()?.Owner,
                "Freigabe-Server nicht erreichbar - Not-Aus-Modus (fail-open) ist aktiv");
        }

        var cache = _store.LoadCache();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (cache is not null && now - cache.At < AppConfig.LockCacheGraceMs)
        {
            var ago = Math.Max(0, (now - cache.At) / 60_000);
            return LockRules.Decide(ToState(cache), DeviceId, VariantId, now,
                $" (Server nicht erreichbar, Stand von vor {ago} min)");
        }

        return new GateResult(false, GateStatus.Unreachable, cache?.Owner,
            "Freigabe-Server nicht erreichbar - Monitor bleibt gesperrt (fail-closed)");
    }
}
