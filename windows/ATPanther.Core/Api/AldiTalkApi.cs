using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ATPanther.Core.Auth;

namespace ATPanther.Core.Api;

public sealed record DataStatus(
    double RemainingMb,
    string OfferId,
    string SubscriptionId,
    string ResourceId,
    string OnDemandAmount,
    string RefillThreshold);

public sealed record BookingResult(
    bool Success,
    bool IsUpdated,
    int StatusCode,
    string Message);

/// <summary>
/// 1:1 port of the Android <c>AldiTalkApi</c> (app/src/main/java/.../api/AldiTalkApi.kt).
/// All three BFF calls rely on the session cookies set during login.
/// </summary>
public sealed class AldiTalkApi : IDisposable
{
    private readonly HttpClient _http;

    public AldiTalkApi(HttpClient client)
    {
        _http = client;
    }

    /// <summary>Releases the wrapped HTTP client (session cookie container included).</summary>
    public void Dispose() => _http.Dispose();

    private static string BffBase(int bff) =>
        $"{AuthConfig.Portal}/scs/bff/scs-{bff}-{(bff == 207 ? "customer-master-data" : "selfcare-dashboard")}-bff";

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, bool jsonPost = false)
    {
        var request = new HttpRequestMessage(method, url);
        // Ulefone parity: Android sends the app User-Agent explicitly on every
        // BFF call (in addition to Accept/Referer/correlation headers).
        request.Headers.TryAddWithoutValidation("User-Agent", AuthConfig.UserAgent);
        var headers = BffHeaders();
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    private static Dictionary<string, string> BffHeaders()
    {
        // Content-Type is a CONTENT header: it is set on HttpContent in
        // Book1GbAsync only (Android parity: exactly one Content-Type).
        return new Dictionary<string, string>
        {
            ["Accept"] = "application/json, text/plain, */*",
            ["Referer"] = $"{AuthConfig.Portal}/portal/auth/uebersicht/",
            ["X-CORRELATION-ID"] = $"C_{Guid.NewGuid()}",
            ["X-TRANSACTION-ID"] = $"T_{Guid.NewGuid()}"
        };
    }

    /// <summary>
    /// Port of resolveContractId(): reads userDetails.subscriptions from the
    /// customer-master-data navigation-list BFF. Prefers the subscription whose msisdn
    /// matches the phone number, falls back to the first entry.
    /// </summary>
    public async Task<string?> ResolveContractIdAsync(string msisdn, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{BffBase(207)}/customer-master-data/v1/navigation-list";
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body)?.AsObject();
            var subscriptions = root?["userDetails"]?["subscriptions"] as JsonArray;
            if (subscriptions == null || subscriptions.Count == 0)
            {
                return null;
            }

            // 1) preferred: entry whose msisdn matches the phone number
            foreach (var s in subscriptions)
            {
                var sub = s?.AsObject();
                if (sub == null) continue;
                if (OptString(sub, "msisdn") == msisdn)
                {
                    return OptString(sub, "contractId");
                }
            }

            // 2) fallback: first entry
            var first = subscriptions[0]?.AsObject();
            return first == null ? null : OptString(first, "contractId");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Port of getRemainingData(): fetches the subscribed offers and extracts the
    /// remaining data volume (allocated - used) plus the fields needed for a booking.
    /// </summary>
    public async Task<DataStatus?> GetRemainingDataAsync(string contractId, CancellationToken cancellationToken = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(contractId);
            var url = $"{BffBase(209)}/selfcare-dashboard/v1/offers?contractId={encoded}";
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body)?.AsObject();
            var subscribedOffers = root?["subscribedOffers"] as JsonArray;
            if (subscribedOffers == null || subscribedOffers.Count == 0)
            {
                return null;
            }

            var offer = subscribedOffers[0]?.AsObject();
            if (offer == null) return null;

            var pack = offer["pack"] as JsonArray;
            long remainingKb = 0;
            if (pack != null)
            {
                foreach (var p in pack)
                {
                    var packEntry = p?.AsObject();
                    if (packEntry == null) continue;
                    if (OptString(packEntry, "balanceAttributeReference") == "dataGrantAmount")
                    {
                        remainingKb = OptLong(packEntry, "allocated") - OptLong(packEntry, "used");
                    }
                }
            }

            var remainingMb = remainingKb / 1024.0;
            return new DataStatus(
                remainingMb,
                Required(offer, "offerId"),
                Required(offer, "subscriptionId"),
                Required(offer, "resourceId"),
                Required(offer, "onDemandAmountValueUid"),
                Required(offer, "refillThresholdValueUid"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Port of book1Gb(): triggers the on-demand top-up (1 GB) via updateUnlimited.
    /// </summary>
    public async Task<BookingResult> Book1GbAsync(DataStatus status, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new JsonObject
            {
                ["offerId"] = status.OfferId,
                ["subscriptionId"] = status.SubscriptionId,
                ["updateOfferResourceID"] = status.ResourceId,
                ["amount"] = status.OnDemandAmount,
                ["refillThresholdValue"] = status.RefillThreshold
            };

            var url = $"{BffBase(209)}/selfcare-dashboard/v1/offer/updateUnlimited";
            using var request = CreateRequest(HttpMethod.Post, url, jsonPost: true);
            var json = payload.ToJsonString();
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(AuthConfig.JsonMediaType);

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var isUpdated = false;
            try
            {
                isUpdated = JsonNode.Parse(body)?["isUpdated"]?.GetValue<bool>() ?? false;
            }
            catch
            {
                // body not JSON / field not boolean -> keep false (same as org.json optBoolean)
            }

            return new BookingResult(
                Success: response.IsSuccessStatusCode && isUpdated,
                IsUpdated: isUpdated,
                StatusCode: (int)response.StatusCode,
                Message: body);
        }
        catch (Exception e)
        {
            return new BookingResult(false, false, -1, e.Message ?? "Unbekannter Fehler");
        }
    }

    private static string? OptString(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node == null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s;
        return node.ToString();
    }

    private static long OptLong(JsonObject obj, string name)
    {
        // Mirrors org.json optLong semantics: integral JSON numbers pass through,
        // fractional numbers are truncated (as if read as double), strings are parsed.
        var node = obj[name];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return (long)d;
            if (value.TryGetValue<string>(out var s)
                && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string Required(JsonObject obj, string name)
        => OptString(obj, name) ?? throw new InvalidOperationException($"Feld {name} fehlt");
}
