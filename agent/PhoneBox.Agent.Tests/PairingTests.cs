using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class PairingTests
{
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void ACodeWorksOnceAndCountsTheClaim()
    {
        var codes = new PairingCodes();
        var (code, expires, _) = codes.Start(Now);
        Assert.Equal(Now + PairingCodes.LifetimeMs, expires);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", code); // 128 bits, base64url
        Assert.True(codes.Claim(code, Now + 1000));
        Assert.Equal(1, codes.Claims);
        Assert.False(codes.Claim(code, Now + 2000));
        Assert.Equal(1, codes.Claims);
    }

    [Fact]
    public void AnExpiredCancelledOrReplacedCodeFails()
    {
        var codes = new PairingCodes();
        var (expired, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(expired, Now + PairingCodes.LifetimeMs + 1));

        var (cancelled, _, attempt) = codes.Start(Now);
        codes.Cancel(attempt);
        Assert.False(codes.Claim(cancelled, Now));

        var (old, _, _) = codes.Start(Now);
        var (current, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(old, Now));
        Assert.True(codes.Claim(current, Now));
    }

    [Fact]
    public void ALateCancelFromAnOlderDialogLeavesTheNewPairing()
    {
        var codes = new PairingCodes();
        var (_, _, first) = codes.Start(Now);
        var (second, _, _) = codes.Start(Now);
        codes.Cancel(first);
        Assert.True(codes.Claim(second, Now));
    }

    [Fact]
    public void TooManyWrongCodesDropTheCurrentOne()
    {
        var codes = new PairingCodes();
        var (code, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(null, Now));
        Assert.False(codes.Claim("", Now));
        for (var i = 0; i < PairingCodes.MaxFailures; i++) Assert.False(codes.Claim("wrong" + i, Now));
        Assert.False(codes.Claim(code, Now));
        Assert.Equal(0, codes.Claims);
    }

    [Fact]
    public void TheLinkKeepsEverythingInTheFragment()
    {
        var link = PairingCodes.Link("Desk PC & co", "ab12", 8766, ["192.168.1.10", "100.64.0.7"], "CODE_x-1");
        var uri = new Uri(link);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("ersinkoc.github.io", uri.Host);
        Assert.Equal("/phonebox/pair/", uri.AbsolutePath);
        Assert.Equal("", uri.Query); // nothing a web server would receive
        Assert.Equal("#v=1&pc=Desk%20PC%20%26%20co&fp=ab12&port=8766&h=192.168.1.10,100.64.0.7&c=CODE_x-1", uri.Fragment);
    }

    [Fact]
    public void TheCheckCodeIsTheStartOfTheFingerprint() =>
        Assert.Equal("AB12 CD34", PairingCodes.Check("ab12cd34" + new string('0', 56)));

    [Fact]
    public void TheQrCodeIsAnSvg()
    {
        var svg = PairingCodes.Svg(PairingCodes.Link("PC", new string('a', 64), 8766, ["192.168.1.10"], new string('A', 22)));
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("POST", "/api/pair/claim", true)]
    [InlineData("GET", "/api/pair/claim", false)]
    [InlineData("POST", "/api/pair/start", false)]
    [InlineData("POST", "/api/pair", false)]
    [InlineData("POST", "/api/state", false)]
    [InlineData("POST", "/api/remote", false)]
    public void OnlyTheClaimNeedsNoKeyOverWifi(string method, string path, bool open) =>
        Assert.Equal(open, Lan.IsPairingClaim(method, new PathString(path)));

    [Fact]
    public void WifiPortRequiresPairedKeyForRemoteAndRejectsWebhookOrMissingKeys()
    {
        var lan = new LanConfig { Enabled = true, Port = 8766, Token = "phone-secret", WebhookToken = "hook-secret" };

        // Paired key opens /api/remote and panel endpoints, never local-only settings/test endpoints
        Assert.Equal((true, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "", "Bearer phone-secret", null, lan));
        Assert.Equal((true, false), Lan.AuthorizeLanRequest("GET", "/api/state", "", "Bearer phone-secret", null, lan));
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("PUT", "/api/settings", "", "Bearer phone-secret", null, lan));

        // Webhook token opens webhook paths only — NEVER /api/remote (via header or ?token=)
        Assert.Equal((true, false), Lan.AuthorizeLanRequest("POST", "/api/push/temp", "", "Bearer hook-secret", null, lan));
        Assert.Equal((true, false), Lan.AuthorizeLanRequest("POST", "/api/notify", "", null, "hook-secret", lan));
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "", "Bearer hook-secret", null, lan));
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "", null, "hook-secret", lan));

        // Missing or wrong key is rejected for /api/remote (and only POST /api/pair/claim is keyless)
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "", null, null, lan));
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "", "Bearer wrong", null, lan));
        Assert.Equal((true, true), Lan.AuthorizeLanRequest("POST", "/api/pair/claim", "", null, null, lan));

        // Browser Origin header is always rejected on port 8766
        Assert.Equal((false, false), Lan.AuthorizeLanRequest("POST", "/api/remote", "https://evil.example", "Bearer phone-secret", null, lan));
    }
}
