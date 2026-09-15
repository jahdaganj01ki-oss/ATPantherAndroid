using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ATPanther.Core;

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

    private static string Truncate(string? s, int max = 500) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...[truncated]");

    public async Task<string?> ResolveContractIdAsync(string msisdn, CancellationToken ct = default)
    {
        try
        {
            var url = $"{AppConfig.Portal}/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBffHeaders(req);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                FileLogger.Warning($"ResolveContractId: {(int)resp.StatusCode} {resp.ReasonPhrase} url={url} body={Truncate(body)}");
                return null;
            }

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var subs = json?["userDetails"]?["subscriptions"]?.AsArray();
            if (subs == null || subs.Count == 0)
            {
                FileLogger.Warning($"ResolveContractId: no subscriptions url={url} json={Truncate(json?.ToJsonString())}");
                return null;
            }

            foreach (var s in subs)
            {
                if (s?["msisdn"]?.GetValue<string>() == msisdn)
                {
                    var cid = s?["contractId"]?.GetValue<string>();
                    FileLogger.Info($"ResolveContractId: matched msisdn={msisdn} contractId={cid}");
                    return cid;
                }
            }

            var fallback = subs[0]?["contractId"]?.GetValue<string>();
            FileLogger.Info($"ResolveContractId: fallback contractId={fallback}");
            return fallback;
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return null;
        }
    }

    public async Task<DataStatus?> GetRemainingDataAsync(string contractId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{AppConfig.Portal}/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId={Uri.EscapeDataString(contractId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddBffHeaders(req);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                FileLogger.Warning($"GetRemainingData: {(int)resp.StatusCode} {resp.ReasonPhrase} contractId={contractId} body={Truncate(body)}");
                return null;
            }

            var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonNode.Parse(bodyText);
            var offers = json?["subscribedOffers"]?.AsArray();
            if (offers == null || offers.Count == 0)
            {
                FileLogger.Warning($"GetRemainingData: no subscribedOffers contractId={contractId} json={Truncate(bodyText)}");
                return null;
            }

            var offer = offers[0]!;
            long remainingKb = 0;
            var pack = offer?["pack"]?.AsArray() ?? new JsonArray();
            var grantFound = false;
            foreach (var p in pack)
            {
                var balanceRef = p?["balanceAttributeReference"]?.GetValue<string>();
                var allocated = p?["allocated"];
                var used = p?["used"];
                FileLogger.Info($"GetRemainingData: pack entry balanceRef={balanceRef} allocated={allocated} used={used}");
                if (balanceRef == "dataGrantAmount")
                {
                    grantFound = true;
                    remainingKb = ParseLong(allocated) - ParseLong(used);
                }
            }
            if (!grantFound)
            {
                FileLogger.Warning($"GetRemainingData: dataGrantAmount not found in pack contractId={contractId} offer={Truncate(offer?.ToJsonString())}");
            }

            var remainingMb = remainingKb / 1024.0;
            FileLogger.Info($"GetRemainingData: remaining={remainingMb:F1} MB contractId={contractId}");
            return new DataStatus(
                remainingMb,
                offer?["offerId"]?.GetValue<string>() ?? "",
                offer?["subscriptionId"]?.GetValue<string>() ?? "",
                offer?["resourceId"]?.GetValue<string>() ?? "",
                offer?["onDemandAmountValueUid"]?.GetValue<string>() ?? "",
                offer?["refillThresholdValueUid"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return null;
        }
    }

    private static long ParseLong(JsonNode? node)
    {
        if (node == null) return 0;
        try
        {
            return node.GetValue<long>();
        }
        catch (FormatException)
        {
            var s = node.GetValue<string>();
            if (long.TryParse(s, out var v)) return v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (long)d;
            return 0;
        }
        catch (InvalidOperationException)
        {
            var s = node.GetValue<string>();
            if (long.TryParse(s, out var v)) return v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (long)d;
            return 0;
        }
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

            bool isUpdated;
            try
            {
                isUpdated = JsonNode.Parse(string.IsNullOrWhiteSpace(respBody) ? "{}" : respBody)?["isUpdated"]?.GetValue<bool>() ?? false;
            }
            catch (Exception ex)
            {
                FileLogger.Warning($"Book1Gb: invalid JSON response status={(int)resp.StatusCode} body={Truncate(respBody)} error={ex.Message}");
                return new BookingResult(false, false, (int)resp.StatusCode, Truncate(respBody));
            }

            FileLogger.Info($"Book1Gb: success={resp.IsSuccessStatusCode && isUpdated} status={(int)resp.StatusCode} isUpdated={isUpdated}");
            return new BookingResult(resp.IsSuccessStatusCode && isUpdated, isUpdated, (int)resp.StatusCode, respBody);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return new BookingResult(false, false, -1, ex.Message);
        }
    }
}
