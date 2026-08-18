using System.Text;
using System.Text.Json;

namespace ATPanther.AldiTalk;

/// <summary>
/// BFF-API des ALDI-Talk-Kundenportals – Port von AldiTalkApi.kt (Android).
/// </summary>
public sealed class AldiTalkApi : IDisposable
{
    private readonly HttpClient _client;

    public AldiTalkApi(HttpClient client) => _client = client;

    /// <summary>Gibt den zugehörigen HttpClient (inkl. Session-Cookies) frei.</summary>
    public void Dispose() => _client.Dispose();

    private Dictionary<string, string> BffHeaders() => new()
    {
        ["Accept"] = "application/json, text/plain, */*",
        ["Referer"] = AuthConfig.Portal + "/portal/auth/uebersicht/",
        ["X-CORRELATION-ID"] = "C_" + Guid.NewGuid(),
        ["X-TRANSACTION-ID"] = "T_" + Guid.NewGuid(),
    };

    /// <summary>
    /// Ermittelt Vertrags- und Subscription-Daten automatisch aus der
    /// navigation-list (customer-master-data BFF) ohne weitere Eingabe –
    /// die Authentifizierung läuft über die nach dem Login gesetzten
    /// Session-Cookies. Bevorzugt wird der zur Rufnummer passende Eintrag,
    /// sonst der erste. Neben der contractId wird auch die subscriptionId
    /// (falls vorhanden) mitgeliefert, da die selfcare-dashboard BFF den
    /// offers-Parameter als subscriptionId interpretiert.
    /// </summary>
    public async Task<ContractInfo?> ResolveContractIdAsync(string msisdn)
    {
        try
        {
            var url = AuthConfig.Portal + "/scs/bff/scs-207-customer-master-data-bff" +
                      "/customer-master-data/v1/navigation-list";
            using var resp = await GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            var root = doc.RootElement;
            if (!root.TryGetProperty("userDetails", out var userDetails) ||
                !userDetails.TryGetProperty("subscriptions", out var subscriptions) ||
                subscriptions.GetArrayLength() == 0)
            {
                return null;
            }

            ContractInfo? Pick(JsonElement sub)
            {
                string? Get(string prop) => sub.TryGetProperty(prop, out var v) ? v.GetString() : null;
                var contractId = Get("contractId");
                return string.IsNullOrEmpty(contractId)
                    ? null
                    : new ContractInfo(contractId, Get("subscriptionId"), Get("msisdn"));
            }

            // 1) bevorzugt: Eintrag, dessen msisdn zur Rufnummer passt
            foreach (var sub in subscriptions.EnumerateArray())
            {
                var info = Pick(sub);
                if (info != null && info.Msisdn == msisdn) return info;
            }
            // 2) Fallback: erster Eintrag
            return Pick(subscriptions[0]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Holt das verbleibende Datenvolumen (dataGrantAmount = allocated − used, KB → MB).
    /// Der BFF interpretiert den Query-Parameter als subscriptionId – bei einem
    /// Autorisierungsfehler mit der contractId wird deshalb automatisch die echte
    /// subscriptionId probiert (falls bekannt und verschieden).
    /// Liefert bei Misserfolg Fehlerdetails (HTTP-Status + Antwort) fürs Log.
    /// </summary>
    public async Task<VolumeQueryResult> GetRemainingDataAsync(string contractId, string? subscriptionId)
    {
        var candidates = new List<string> { contractId };
        if (!string.IsNullOrEmpty(subscriptionId) && subscriptionId != contractId)
            candidates.Add(subscriptionId);

        VolumeQueryResult? last = null;
        foreach (var id in candidates)
        {
            last = await TryGetRemainingDataAsync(id).ConfigureAwait(false);
            if (last.Status != null) return last;
        }
        return last ?? new VolumeQueryResult(null, 0, "kein Abruf möglich");
    }

    private async Task<VolumeQueryResult> TryGetRemainingDataAsync(string id)
    {
        try
        {
            var url = AuthConfig.Portal + "/scs/bff/scs-209-selfcare-dashboard-bff" +
                      "/selfcare-dashboard/v1/offers?contractId=" + Uri.EscapeDataString(id);
            using var resp = await GetAsync(url).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new VolumeQueryResult(null, (int)resp.StatusCode, Truncate(body, 300));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("subscribedOffers", out var subscribedOffers) ||
                subscribedOffers.GetArrayLength() == 0)
            {
                return new VolumeQueryResult(null, (int)resp.StatusCode,
                    "subscribedOffers fehlt/leer: " + Truncate(body, 300));
            }

            var offer = subscribedOffers[0];
            long remainingKb = 0;
            if (offer.TryGetProperty("pack", out var pack))
            {
                foreach (var p in pack.EnumerateArray())
                {
                    if (!p.TryGetProperty("balanceAttributeReference", out var balanceRef) ||
                        balanceRef.GetString() != "dataGrantAmount")
                    {
                        continue;
                    }
                    // allocated/used kommen je nach Antwort als Zahl ODER String
                    // (Kotlins optLong toleriert beides; GetInt64 würde bei String werfen)
                    var allocated = p.TryGetProperty("allocated", out var a) ? ReadLong(a) : 0;
                    var used = p.TryGetProperty("used", out var u) ? ReadLong(u) : 0;
                    remainingKb = allocated - used;
                }
            }

            string Get(string prop) => offer.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

            var status = new DataStatus(
                RemainingMb: remainingKb / 1024.0,
                OfferId: Get("offerId"),
                SubscriptionId: Get("subscriptionId"),
                ResourceId: Get("resourceId"),
                OnDemandAmount: Get("onDemandAmountValueUid"),
                RefillThreshold: Get("refillThresholdValueUid"));

            return new VolumeQueryResult(status, (int)resp.StatusCode, "");
        }
        catch (Exception e)
        {
            return new VolumeQueryResult(null, 0, e.Message);
        }
    }

    /// <summary>Bucht 1 GB zusätzliches Datenvolumen nach (offer/updateUnlimited).</summary>
    public async Task<BookingResult> Book1GbAsync(DataStatus status)
    {
        try
        {
            var payload = new
            {
                offerId = status.OfferId,
                subscriptionId = status.SubscriptionId,
                updateOfferResourceID = status.ResourceId,
                amount = status.OnDemandAmount,
                refillThresholdValue = status.RefillThreshold,
            };
            var json = JsonSerializer.Serialize(payload);

            var url = AuthConfig.Portal + "/scs/bff/scs-209-selfcare-dashboard-bff" +
                      "/selfcare-dashboard/v1/offer/updateUnlimited";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            ApplyBffHeaders(req);
            req.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _client.SendAsync(req).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var isUpdated = false;
            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("isUpdated", out var updated))
                    isUpdated = ReadBool(updated);
            }
            catch
            {
                // Nicht-JSON-Antwort (z. B. Fehlerseite) → isUpdated bleibt false
            }

            return new BookingResult(
                Success: resp.IsSuccessStatusCode && isUpdated,
                IsUpdated: isUpdated,
                StatusCode: (int)resp.StatusCode,
                Message: respBody);
        }
        catch (Exception e)
        {
            return new BookingResult(false, false, -1, e.Message);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBffHeaders(req);
        req.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
        return await _client.SendAsync(req).ConfigureAwait(false);
    }

    private void ApplyBffHeaders(HttpRequestMessage req)
    {
        foreach (var (key, value) in BffHeaders())
            req.Headers.TryAddWithoutValidation(key, value);
    }

    /// <summary>Liest eine Zahl tolerant: echte Zahlen UND numerische Strings (wie Android optLong).</summary>
    private static long ReadLong(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.GetInt64();
            case JsonValueKind.String:
                return long.TryParse(el.GetString(), out var v) ? v : 0;
            default:
                return 0;
        }
    }

    /// <summary>Liest einen Boolean tolerant: echte Booleans UND Strings "true"/"false".</summary>
    private static bool ReadBool(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return bool.TryParse(el.GetString(), out var v) && v;
            default:
                return false;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
