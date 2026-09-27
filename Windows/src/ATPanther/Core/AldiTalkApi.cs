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
        var tariff = await GetTariffStatusAsync(contractId, ct).ConfigureAwait(false);
        return tariff?.PrimaryDataStatus;
    }

    /// <summary>
    /// Holt alle subscribedOffers und klassifiziert Basis-Tarif vs. Zusatzoptionen.
    /// 1:1-Port von getTariffStatus/parseOffersToTariffStatus (AldiTalkApi.kt, Ulefone-Variante).
    ///
    /// Fix 26.09.2026 (Android) / 27.09.2026 (Windows): Das echte BFF-Format war
    /// unbekannt – der erste Parser pruefte nur wenige Felder und forderte fuer einen
    /// Basis-Tarif zwingend ein dataGrantAmount-Pack. Ein gebuchtes "Surf-Ticket
    /// Unlimited" (Portal: "Unbegrenzt GB", evtl. ohne dataGrantAmount, mit
    /// abweichenden Feldnamen wie marketingName) wurde weder als Basis noch als
    /// Add-on erkannt. Neuer Ansatz:
    ///  1. Alle String-Felder rekursiv einsammeln (DumpOfferTexts) + Keyword-Listen.
    ///  2. JEDES aktive subscribedOffer = Tarifschutz (Fallback, kein Fehlalarm).
    ///  3. Nur bei KEINEM aktiven Offer gilt ShouldWarn.
    ///  4. uncertain=true wenn aktive Offers existieren, aber nichts klassifizierbar.
    /// Wirft nie – liefert null nur bei HTTP-/Netzwerkfehler (kein Fehlalarm).
    /// </summary>
    public async Task<TariffStatus?> GetTariffStatusAsync(string contractId, CancellationToken ct = default)
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
                FileLogger.Warning($"GetTariffStatus: {(int)resp.StatusCode} {resp.ReasonPhrase} contractId={contractId} body={Truncate(body)}");
                return null;
            }

            var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonNode.Parse(bodyText);
            return ParseOffersToTariffStatus(json);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return null;
        }
    }

    /// <summary>Reine JSON-zu-TariffStatus Konvertierung – isoliert fuer Tests.</summary>
    public static TariffStatus ParseOffersToTariffStatus(JsonNode? json)
    {
        var offers = json?["subscribedOffers"]?.AsArray()
            ?? json?["offers"]?.AsArray()
            ?? json?["subscribed_offer"]?.AsArray()
            ?? new JsonArray();

        if (offers.Count == 0)
        {
            FileLogger.Warning("TariffStatus: 0 subscribedOffers -> kein Tarif, kein Add-on");
            return new TariffStatus(false, false, -1.0, new List<string>(), new List<string>(), 0, "0 subscribedOffers", null, false, new List<string>());
        }

        var hasBaseTariff = false;
        var hasActiveAddon = false;
        var baseNames = new List<string>();
        var addonNames = new List<string>();
        var allNames = new List<string>();
        var anyActive = false;
        var anyClassified = false;
        double remainingMb = -1.0;
        DataStatus? primaryStatus = null;
        var primaryRemainingKb = -1L;

        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            if (offer == null) continue;
            var offerId = FirstNonBlank(
                offer["offerId"]?.GetValue<string>() ?? "",
                offer["id"]?.GetValue<string>() ?? "");
            var offerName = FirstNonBlank(
                offer["offerName"]?.GetValue<string>() ?? "",
                offer["displayName"]?.GetValue<string>() ?? "",
                offer["marketingName"]?.GetValue<string>() ?? "",
                offer["productName"]?.GetValue<string>() ?? "",
                offer["name"]?.GetValue<string>() ?? "",
                offer["title"]?.GetValue<string>() ?? "",
                offer["label"]?.GetValue<string>() ?? "");
            var displayName = !string.IsNullOrEmpty(offerName) ? offerName
                : (!string.IsNullOrEmpty(offerId) ? offerId : $"offer#{i}");
            allNames.Add(displayName);

            var status = FirstNonBlank(
                offer["status"]?.GetValue<string>() ?? "",
                offer["state"]?.GetValue<string>() ?? "",
                offer["offerStatus"]?.GetValue<string>() ?? "",
                offer["subscriptionStatus"]?.GetValue<string>() ?? "",
                offer["lifecycleStatus"]?.GetValue<string>() ?? "");
            var active = TariffEvaluator.IsActiveStatus(status);
            if (active) anyActive = true;

            var fullText = DumpOfferTexts(offer);
            var isAddon = TariffEvaluator.IsAddonOffer(offerName, offerId, fullText);
            var looksBase = TariffEvaluator.IsBaseOffer(offerName, offerId, fullText);

            FileLogger.Info($"TariffStatus Offer[{i}] id={offerId} name={offerName} status={status} active={active} isAddon={isAddon} looksBase={looksBase}");

            var pack = offer["pack"]?.AsArray()
                ?? offer["packs"]?.AsArray()
                ?? offer["balances"]?.AsArray();
            var hasDataGrant = false;
            var kbForThisOffer = -1L;
            if (pack != null)
            {
                foreach (var p in pack)
                {
                    if (p == null) continue;
                    var balanceRef = p["balanceAttributeReference"]?.GetValue<string>()
                        ?? p["balanceType"]?.GetValue<string>()
                        ?? p["unit"]?.GetValue<string>() ?? "";
                    if (balanceRef == "dataGrantAmount" || balanceRef.ToLowerInvariant().Contains("data"))
                    {
                        if (p["allocated"] != null || p["used"] != null || p["remaining"] != null)
                        {
                            hasDataGrant = true;
                            var allocated = ParseLong(p["allocated"] ?? p["total"]);
                            var used = ParseLong(p["used"]);
                            var remaining = p["remaining"] != null ? ParseLong(p["remaining"]) : allocated - used;
                            kbForThisOffer = remaining;
                            // Log Pack-Details wie bisher (Kompatibilitaet mit alten Logs)
                            FileLogger.Info($"GetRemainingData: pack entry balanceRef={balanceRef} allocated={allocated} used={used}");
                            break;
                        }
                    }
                }
            }

            if (isAddon)
            {
                anyClassified = true;
                if (active) hasActiveAddon = true;
                addonNames.Add(active ? displayName : $"{displayName} (inaktiv)");
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0)
                {
                    primaryRemainingKb = kbForThisOffer;
                    remainingMb = kbForThisOffer / 1024.0;
                    primaryStatus = BuildDataStatus(offer, remainingMb);
                }
                continue;
            }

            var isBaseCandidate = hasDataGrant || looksBase
                || fullText.ToLowerInvariant().Contains("data");

            if (isBaseCandidate)
            {
                anyClassified = true;
                if (active)
                {
                    hasBaseTariff = true;
                    baseNames.Add(displayName);
                }
                else
                {
                    baseNames.Add($"{displayName} (inaktiv)");
                }
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0)
                {
                    primaryRemainingKb = kbForThisOffer;
                    remainingMb = kbForThisOffer / 1024.0;
                    primaryStatus = BuildDataStatus(offer, remainingMb);
                }
                continue;
            }

            // Weder Add-on noch Basis erkennbar:
            if (active)
            {
                // Fallback-Regel: AKTIVES subscribedOffer = Tarifschutz vorhanden.
                // Lieber keine Warnung als einen Fehlalarm (Surf-Ticket-Fix).
                hasBaseTariff = true;
                baseNames.Add($"{displayName} (unklassifiziert, aktiv→Schutz)");
                if (kbForThisOffer >= 0 && primaryRemainingKb < 0)
                {
                    primaryRemainingKb = kbForThisOffer;
                    remainingMb = kbForThisOffer / 1024.0;
                    primaryStatus = BuildDataStatus(offer, remainingMb);
                }
            }
            else
            {
                baseNames.Add($"{displayName} (inaktiv, unklassifiziert)");
            }
        }

        var uncertain = anyActive && !anyClassified && !hasBaseTariff && !hasActiveAddon;
        var debug = $"offers={offers.Count} base={hasBaseTariff} addon={hasActiveAddon} " +
            $"uncertain={uncertain} baseNames=[{string.Join("|", baseNames)}] addonNames=[{string.Join("|", addonNames)}] " +
            $"remaining={(remainingMb >= 0 ? $"{remainingMb:F1}" : "?")}MB";
        FileLogger.Info($"TariffStatus -> {debug}");

        return new TariffStatus(hasBaseTariff, hasActiveAddon, remainingMb,
            baseNames, addonNames, offers.Count, debug, primaryStatus, uncertain, allNames);
    }

    private static string FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>
    /// Sammelt alle String-/Zahlen-/Boolean-Werte eines Offer-JSON rekursiv
    /// (max. Tiefe 3) zu einem durchsuchbaren Text. Faengt Feldnamen ab, die
    /// der Parser nicht explizit kennt (z.B. marketingName, productName).
    /// </summary>
    private static string DumpOfferTexts(JsonNode? node, int depth = 0)
    {
        if (node == null || depth > 3) return "";
        var sb = new StringBuilder();
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var k = kv.Key;
                if (k.Equals("subscriptionId", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("resourceId", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("contractId", StringComparison.OrdinalIgnoreCase)
                    || k.ToLowerInvariant().Contains("correlation")
                    || k.ToLowerInvariant().Contains("transaction")) continue;
                var v = kv.Value;
                if (v is JsonObject || v is JsonArray) sb.Append(' ').Append(DumpOfferTexts(v, depth + 1));
                else if (v != null) sb.Append(' ').Append(k).Append(':').Append(v.ToString());
            }
        }
        else if (node is JsonArray arr)
        {
            var n = Math.Min(arr.Count, 20);
            for (var i = 0; i < n; i++)
            {
                var el = arr[i];
                if (el is JsonObject || el is JsonArray) sb.Append(' ').Append(DumpOfferTexts(el, depth + 1));
                else if (el != null) sb.Append(' ').Append(el.ToString());
            }
        }
        else
        {
            sb.Append(' ').Append(node.ToString());
        }
        return sb.ToString();
    }

    private static DataStatus? BuildDataStatus(JsonNode offer, double remainingMb)
    {
        try
        {
            return new DataStatus(
                remainingMb,
                offer["offerId"]?.GetValue<string>() ?? offer["id"]?.GetValue<string>() ?? "",
                offer["subscriptionId"]?.GetValue<string>() ?? "",
                offer["resourceId"]?.GetValue<string>() ?? "",
                offer["onDemandAmountValueUid"]?.GetValue<string>() ?? "",
                offer["refillThresholdValueUid"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
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
