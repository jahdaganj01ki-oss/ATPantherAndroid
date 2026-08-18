using System.Text;
using System.Text.Json;

namespace ATPanther.AldiTalk;

/// <summary>
/// BFF-API des ALDI-Talk-Kundenportals – 1:1-Port von AldiTalkApi.kt (Android).
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
    /// Ermittelt die contractId (subscriptionId) automatisch aus der
    /// navigation-list (customer-master-data BFF) ohne weitere Eingabe –
    /// die Authentifizierung läuft über die nach dem Login gesetzten
    /// Session-Cookies. Liefert userDetails.subscriptions[0].contractId,
    /// bzw. den zur Rufnummer passenden Vertrag bei mehreren Verträgen.
    /// </summary>
    public async Task<string?> ResolveContractIdAsync(string msisdn)
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

            // 1) bevorzugt: Eintrag, dessen msisdn zur Rufnummer passt
            foreach (var sub in subscriptions.EnumerateArray())
            {
                if (sub.TryGetProperty("msisdn", out var msisdnProp) &&
                    msisdnProp.GetString() == msisdn &&
                    sub.TryGetProperty("contractId", out var contractIdProp))
                {
                    return contractIdProp.GetString();
                }
            }
            // 2) Fallback: erster Eintrag
            return subscriptions[0].TryGetProperty("contractId", out var firstContractId)
                ? firstContractId.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Holt das verbleibende Datenvolumen (dataGrantAmount = allocated − used, KB → MB).</summary>
    public async Task<DataStatus?> GetRemainingDataAsync(string contractId)
    {
        try
        {
            var url = AuthConfig.Portal + "/scs/bff/scs-209-selfcare-dashboard-bff" +
                      "/selfcare-dashboard/v1/offers?contractId=" + Uri.EscapeDataString(contractId);
            using var resp = await GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            var root = doc.RootElement;
            if (!root.TryGetProperty("subscribedOffers", out var subscribedOffers) ||
                subscribedOffers.GetArrayLength() == 0)
            {
                return null;
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
                    var allocated = p.TryGetProperty("allocated", out var a) ? a.GetInt64() : 0;
                    var used = p.TryGetProperty("used", out var u) ? u.GetInt64() : 0;
                    remainingKb = allocated - used;
                }
            }

            string Get(string prop) => offer.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

            return new DataStatus(
                RemainingMb: remainingKb / 1024.0,
                OfferId: Get("offerId"),
                SubscriptionId: Get("subscriptionId"),
                ResourceId: Get("resourceId"),
                OnDemandAmount: Get("onDemandAmountValueUid"),
                RefillThreshold: Get("refillThresholdValueUid"));
        }
        catch
        {
            return null;
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
                    isUpdated = updated.GetBoolean();
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
}
