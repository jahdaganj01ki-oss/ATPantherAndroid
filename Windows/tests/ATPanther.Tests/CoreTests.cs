using ATPanther.Core;
using Xunit;

namespace ATPanther.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ResolveUrl_DoubleSlash_IsPathOnBaseHost()
    {
        var r = AuthService.ResolveUrl("//user/auth/account-overview/",
            "https://login.alditalk-kundenbetreuung.de/signin/oauth2/authorize?x=1");
        Assert.Equal("https://login.alditalk-kundenbetreuung.de/user/auth/account-overview/", r);
    }

    [Fact]
    public void ResolveUrl_Relative_UsesCurrentHop()
    {
        var r = AuthService.ResolveUrl("/user/next", "https://a.example/base/page");
        Assert.Equal("https://a.example/user/next", r);
    }

    [Fact]
    public void ResolveUrl_Absolute_Unchanged()
    {
        var r = AuthService.ResolveUrl("https://x.example/y", "https://a.example/");
        Assert.Equal("https://x.example/y", r);
    }

    [Fact]
    public void Pkce_Verifier_Is43Chars()
    {
        var (v, c) = Crypto.GeneratePkce();
        Assert.Equal(43, v.Length);
        Assert.False(string.IsNullOrEmpty(c));
    }

    [Fact]
    public void RemainingMb_KbDividedBy1024()
    {
        long allocated = 2048, used = 1024;
        Assert.Equal(1.0, (allocated - used) / 1024.0);
    }

    [Fact]
    public void Caps_Constants_MatchAndroid()
    {
        Assert.Equal(3, AppConfig.MaxConsecutiveConnectionFailures);
        Assert.Equal(5, AppConfig.MaxReloginsWithoutPoll);
        Assert.Equal(850f, AppConfig.DefaultThresholdMb);
        Assert.Equal(60, AppConfig.DefaultIntervalSec);
    }

    // ── Monitor-Freigabe: identische Werte wie MonitorGate.kt ──────────────

    [Fact]
    public void LockCaps_MatchAndroidGate()
    {
        Assert.Equal("windows", AppConfig.VariantId);
        Assert.Equal(900, AppConfig.LockTtlSeconds);
        Assert.Equal(5 * 60 * 1000, AppConfig.LockCheckIntervalMs);
        Assert.Equal(30 * 60 * 1000, AppConfig.LockCacheGraceMs);
    }

    [Fact]
    public void Lock_OwnDeviceWithLiveLease_IsAllowed()
    {
        const string me = "windows-pc1";
        var state = new LockState("windows", me, 1000, 999_000, 1000, 500_000);
        var r = LockRules.Decide(state, me, "windows", 500_000);
        Assert.True(r.Allowed);
        Assert.Equal(GateStatus.Allowed, r.Status);
    }

    [Fact]
    public void Lock_OtherDevice_IsNotOwner()
    {
        var state = new LockState("ulefone", "ulefone-abc", 1000, 999_000, 1000, 500_000);
        var r = LockRules.Decide(state, "windows-pc1", "windows", 500_000);
        Assert.False(r.Allowed);
        Assert.Equal(GateStatus.NotOwner, r.Status);
        Assert.Equal("ulefone", r.Owner);
    }

    [Fact]
    public void Lock_Empty_IsFree()
    {
        var r = LockRules.Decide(LockState.Empty, "windows-pc1", "windows", 500_000);
        Assert.False(r.Allowed);
        Assert.Equal(GateStatus.Free, r.Status);
    }

    [Fact]
    public void Lock_ExpiredLease_IsFreeEvenForSameVariant()
    {
        // Abgelaufene eigene Lease darf NICHT als erlaubt durchgehen: sonst
        // wuerde ein Geraet weiter pollen, obwohl ein anderes uebernommen
        // haben koennte.
        var state = new LockState("windows", "windows-pc1", 1000, 400_000, 1000, 500_000);
        var r = LockRules.Decide(state, "windows-pc1", "windows", 500_000);
        Assert.False(r.Allowed);
        Assert.Equal(GateStatus.Free, r.Status);
    }

    [Fact]
    public void Lock_SameVariantDifferentDevice_IsNotOwner()
    {
        // Zweites Geraet derselben Variante ist ein fremdes Geraet.
        var state = new LockState("windows", "windows-pc1", 1000, 999_000, 1000, 500_000);
        var r = LockRules.Decide(state, "windows-pc2", "windows", 500_000);
        Assert.False(r.Allowed);
        Assert.Equal(GateStatus.NotOwner, r.Status);
    }

    [Fact]
    public void Lock_ExpiryBoundary_IsExclusive()
    {
        // expiresAt == now gilt als abgelaufen (Worker rechnet genauso).
        var state = new LockState("windows", "windows-pc1", 1000, 500_000, 1000, 500_000);
        var r = LockRules.Decide(state, "windows-pc1", "windows", 500_000);
        Assert.False(r.Allowed);
    }

    [Fact]
    public void LockStore_NormalizeUrl_TrimsAndValidates()
    {
        Assert.Equal("https://a.workers.dev", LockStore.NormalizeUrl("  https://a.workers.dev/  "));
        Assert.Equal("http://127.0.0.1:8787", LockStore.NormalizeUrl("http://127.0.0.1:8787"));
        Assert.Equal("", LockStore.NormalizeUrl("workers.dev"));
        Assert.Equal("", LockStore.NormalizeUrl(""));
        Assert.Equal("", LockStore.NormalizeUrl(null));
    }

    [Fact]
    public void SurfTicketUnlimited_MarketingName_RecognizedAsAddon()
    {
        // Portal zeigt "Unbegrenzt GB" (kein dataGrantAmount) – Feldname marketingName.
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"subscribedOffers": [{
                "offerId": "ST-UNLTD",
                "marketingName": "Surf-Ticket Unlimited",
                "status": "active",
                "pack": [{"balanceAttributeReference": "unlimitedGB", "allocated": 0, "used": 0}]
            }]}
            """);
        var tariff = AldiTalkApi.ParseOffersToTariffStatus(json);
        Assert.True(tariff.HasActiveAddon);
        Assert.False(tariff.ShouldWarn);
    }

    [Fact]
    public void UnbegrenztGb_PortalText_RecognizedAsAddon()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"subscribedOffers": [{
                "offerId": "X1",
                "displayName": "Unbegrenzt GB",
                "status": "aktiv",
                "pack": []
            }]}
            """);
        var tariff = AldiTalkApi.ParseOffersToTariffStatus(json);
        Assert.True(tariff.HasActiveAddon);
        Assert.False(tariff.ShouldWarn);
    }

    [Fact]
    public void ActiveButUnclassifiableOffer_FallsBackToProtection_NoWarning()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"subscribedOffers": [{
                "offerId": "???",
                "someUnknownField": "quux-123",
                "status": "active"
            }]}
            """);
        var tariff = AldiTalkApi.ParseOffersToTariffStatus(json);
        Assert.True(tariff.HasBaseTariff);
        Assert.False(tariff.ShouldWarn);
    }

    [Fact]
    public void EmptyOffers_ShouldWarn()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""{"subscribedOffers": []}""");
        var tariff = AldiTalkApi.ParseOffersToTariffStatus(json);
        Assert.True(tariff.ShouldWarn);
    }

    [Fact]
    public void ExpiredOffer_DoesNotProtect()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            {"subscribedOffers": [{
                "offerId": "OLD",
                "offerName": "Kombi-Paket S",
                "status": "expired",
                "pack": []
            }]}
            """);
        var tariff = AldiTalkApi.ParseOffersToTariffStatus(json);
        // Inaktiv: weder Basis noch Schutz – aber auch kein aktives Offer => Warnung
        Assert.False(tariff.HasBaseTariff);
        Assert.True(tariff.ShouldWarn);
    }
}
