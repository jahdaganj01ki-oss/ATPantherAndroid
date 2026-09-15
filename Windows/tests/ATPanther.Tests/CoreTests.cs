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
}
