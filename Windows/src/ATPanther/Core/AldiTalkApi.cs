using System.Text;
using System.Text.Json.Nodes;

namespace ATPanther.Core;

public sealed record DataStatus(double RemainingMb, string OfferId, string SubscriptionId, string ResourceId, string OnDemandAmount, string RefillThreshold);
public sealed record BookingResult(bool Success, bool IsUpdated, int StatusCode, string Message);

/// <summary>1:1-Port von AldiTalkApi.kt (Ulefone-Variante).</summary>
public sealed class AldiTalkApi(HttpClient client)
{
    private void AddBffHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        req.Headers.Referrer = new Uri($"{AppConfig.Portal}/portal/auth/uebersicht/");
        req.Headers.Add("X-CORRELATION-ID", $"C_{Guid.NewGuid()}");
        req.Headers.Add("X-TRANSACTION-ID", $"T_{Guid.NewGuid()}");
        req.Headers.UserAgent.ParseAdd(AppConfig.UserAgent);
    }

    public async Task<string?> ResolveContractIdAsync(string msisdn, CancellationToken ct = default)
    {
        try
        {
            var url = $"{AppConfig.Portal}/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBffHeaders(req);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var subs = json?["userDetails"]?["subscriptions"]?.AsArray();
            if (subs == null || subs.Count == 0) return null;
            foreach (var s in subs)
            {
                if (s?["msisdn"]?.GetValue<string>() == msisdn)
                    return s?["contractId"]?.GetValue<string>();
            }
            return subs[0]?["contractId"]?.GetValue<string>();
        }
        catch { return null; }
    }

    public async Task<DataStatus?> GetRemainingDataAsync(string contractId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{AppConfig.Portal}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={Uri.EscapeDataString(contractId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBffHeaders(req);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var offers = json?["subscribedOffers"]?.AsArray();
            if (offers == null || offers.Count == 0) return null;
            var offer = offers[0]!;
            long remainingKb = 0;
            foreach (var p in offer?["pack"]?.AsArray() ?? new JsonArray())
            {
                if (p?["balanceAttributeReference"]?.GetValue<string>() == "dataGrantAmount")
                    remainingKb = (p?["allocated"]?.GetValue<long>() ?? 0) - (p?["used"]?.GetValue<long>() ?? 0);
            }
            return new DataStatus(
                remainingKb / 1024.0,
                offer?["offerId"]?.GetValue<string>() ?? "",
                offer?["subscriptionId"]?.GetValue<string>() ?? "",
                offer?["resourceId"]?.GetValue<string>() ?? "",
                offer?["onDemandAmountValueUid"]?.GetValue<string>() ?? "",
                offer?["refillThresholdValueUid"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
    }

    public async Task<BookingResult> Book1GbAsync(DataStatus status, CancellationToken ct = default)
    {
        try
        {
            var url = $"{AppConfig.Portal}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offer/updateUnlimited";
            var body = new JsonObject
            {
                ["offerId"] = status.OfferId,
                ["subscriptionId"] = status.SubscriptionId,
                ["updateOfferResourceID"] = status.ResourceId,
                ["amount"] = status.OnDemandAmount,
                ["refillThresholdValue"] = status.RefillThreshold,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddBffHeaders(req);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var isUpdated = JsonNode.Parse(string.IsNullOrWhiteSpace(respBody) ? "{}" : respBody)?["isUpdated"]?.GetValue<bool>() ?? false;
            return new BookingResult(resp.IsSuccessStatusCode && isUpdated, isUpdated, (int)resp.StatusCode, respBody);
        }
        catch (Exception ex)
        {
            return new BookingResult(false, false, -1, ex.Message);
        }
    }
}
