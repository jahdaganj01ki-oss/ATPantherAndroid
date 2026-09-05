using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ATPanther.Auth;

namespace ATPanther.Api;

/// <summary>Pendant zu <c>data class DataStatus</c> (AldiTalkApi.kt:17–24).</summary>
public sealed record DataStatus(
    double RemainingMb,
    string OfferId,
    string SubscriptionId,
    string ResourceId,
    string OnDemandAmount,
    string RefillThreshold);

/// <summary>Pendant zu <c>data class BookingResult</c> (AldiTalkApi.kt:26–31).</summary>
public sealed record BookingResult(bool Success, bool IsUpdated, int StatusCode, string Message);

/// <summary>
/// Die drei Business-Aufrufe aus <c>api/AldiTalkApi.kt</c>. Header-Reihenfolge,
/// URL-Aufbau und Schlüsselreihenfolge sind verbindlich (API-CONTRACT 3).
/// </summary>
public sealed class AldiTalkApi
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PantherClient client;

    public AldiTalkApi(PantherClient client) => this.client = client;

    /// <summary>
    /// <c>bffHeaders()</c> (AldiTalkApi.kt:35–40) plus User-Agent – der kommt im Original
    /// nach den vier BFF-Headern (AldiTalkApi.kt:58), diese Reihenfolge bleibt erhalten.
    /// </summary>
    private static List<(string Name, string Value)> BffHeaders() => new()
    {
        ("Accept", "application/json, text/plain, */*"),
        ("Referer", AuthConfig.PORTAL + "/portal/auth/uebersicht/"),
        ("X-CORRELATION-ID", "C_" + Guid.NewGuid()),
        ("X-TRANSACTION-ID", "T_" + Guid.NewGuid()),
        ("User-Agent", AuthConfig.UA),
    };

    /// <summary>
    /// contractId aus der navigation-list (AldiTalkApi.kt:50–92):
    /// bevorzugt der Eintrag mit passender msisdn, sonst der erste.
    /// </summary>
    public async Task<string?> ResolveContractIdAsync(string msisdn, CancellationToken ct)
    {
        try
        {
            var url = new Uri(AuthConfig.PORTAL +
                "/scs/bff/scs-207-customer-master-data-bff/customer-master-data/v1/navigation-list");

            HttpResult response = await client.SendAsync("GET", url, BffHeaders(), null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessful) return null;

            var json = JsonNode.Parse(response.Body) as JsonObject
                ?? throw new JsonException("navigation-list: kein JSON-Objekt");
            var subs = (json["userDetails"] as JsonObject)?["subscriptions"] as JsonArray;
            if (subs == null || subs.Count == 0) return null;

            foreach (JsonNode? node in subs)
            {
                if (node is not JsonObject s) continue;
                if (JsonText.OptString(s, "msisdn") == msisdn)
                    return JsonText.OptString(s, "contractId");
            }

            var fallback = subs[0] as JsonObject;
            return fallback == null ? null : JsonText.OptString(fallback, "contractId");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Datenvolumen aus den offers (AldiTalkApi.kt:95–146).</summary>
    public async Task<DataStatus?> GetRemainingDataAsync(string contractId, CancellationToken ct)
    {
        try
        {
            // Query-Parameter steht im Original direkt in der URL-Zeichenkette
            // (AldiTalkApi.kt:97–98), kein addQueryParameter – keine Zusatzkodierung.
            var url = new Uri(AuthConfig.PORTAL +
                "/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offers?contractId=" +
                contractId);

            HttpResult response = await client.SendAsync("GET", url, BffHeaders(), null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessful) return null;

            var json = JsonNode.Parse(response.Body) as JsonObject
                ?? throw new JsonException("offers: kein JSON-Objekt");

            JsonArray subscribedOffers = JsonText.RequireArray(json, "subscribedOffers");
            if (subscribedOffers.Count == 0) return null;

            var offer = subscribedOffers[0] as JsonObject
                ?? throw new JsonException("subscribedOffers[0] ist kein Objekt");
            JsonArray pack = JsonText.RequireArray(offer, "pack");

            long remainingKb = 0;
            foreach (JsonNode? node in pack)
            {
                if (node is not JsonObject p) continue;
                if (JsonText.OptString(p, "balanceAttributeReference") == "dataGrantAmount")
                    remainingKb = JsonText.OptLong(p, "allocated", 0) - JsonText.OptLong(p, "used", 0);
            }

            double remainingMb = remainingKb / 1024.0;

            return new DataStatus(
                remainingMb,
                JsonText.RequireString(offer, "offerId"),
                JsonText.RequireString(offer, "subscriptionId"),
                JsonText.RequireString(offer, "resourceId"),
                JsonText.RequireString(offer, "onDemandAmountValueUid"),
                JsonText.RequireString(offer, "refillThresholdValueUid"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>1 GB nachbuchen (AldiTalkApi.kt:149–186).</summary>
    public async Task<BookingResult> Book1GbAsync(DataStatus status, CancellationToken ct)
    {
        try
        {
            // Schluesselreihenfolge ist Teil des Vertrags; updateOfferResourceID mit grossem ID.
            var bodyJson = new JsonObject
            {
                ["offerId"] = status.OfferId,
                ["subscriptionId"] = status.SubscriptionId,
                ["updateOfferResourceID"] = status.ResourceId,
                ["amount"] = status.OnDemandAmount,
                ["refillThresholdValue"] = status.RefillThreshold,
            };

            var url = new Uri(AuthConfig.PORTAL +
                "/scs/bff/scs-209-selfcare-dashboard-bff/selfcare-dashboard/v1/offer/updateUnlimited");

            HttpResult response = await client
                .SendAsync("POST", url, BffHeaders(),
                           JsonContent.Body(bodyJson.ToJsonString(CompactOptions)), ct)
                .ConfigureAwait(false);

            string respBody = response.Body.Length == 0 ? "{}" : response.Body;
            var respJson = JsonNode.Parse(respBody) as JsonObject
                ?? throw new JsonException("updateUnlimited: kein JSON-Objekt");

            bool isUpdated = JsonText.OptBoolean(respJson, "isUpdated", false);

            return new BookingResult(
                response.IsSuccessful && isUpdated,
                isUpdated,
                response.StatusCode,
                respBody);
        }
        catch (Exception e)
        {
            // Kotlin faengt hier auch CancellationException und liefert statusCode -1.
            string message = string.IsNullOrEmpty(e.Message) ? "Unbekannter Fehler" : e.Message;
            return new BookingResult(false, false, -1, message);
        }
    }
}
