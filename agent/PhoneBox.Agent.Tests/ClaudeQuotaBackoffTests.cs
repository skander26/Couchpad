using System.Net.Http.Headers;
using PhoneBox.Agent;
using Xunit;

public sealed class ClaudeQuotaBackoffTests
{
    [Fact]
    public void HealthyChecksRunEveryThreeMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(3), QuotaMonitor.ClaudeDelay(ok: true, failures: 0, retryAfter: null));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 15)]
    [InlineData(40, 15)]
    public void FailuresBackOffUpToFifteenMinutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), QuotaMonitor.ClaudeDelay(ok: false, failures, retryAfter: null));

    [Fact]
    public void ARetryAfterLongerThanTheBackOffWins() =>
        // the 429 seen on 2026-11-15 asked for 230 s, while the old code asked again after 60 s
        Assert.Equal(TimeSpan.FromSeconds(235), QuotaMonitor.ClaudeDelay(ok: false, failures: 1, TimeSpan.FromSeconds(230)));

    [Fact]
    public void TheBackOffWinsOverAShortRetryAfterAndAHugeOneIsCapped()
    {
        Assert.Equal(TimeSpan.FromMinutes(4), QuotaMonitor.ClaudeDelay(ok: false, failures: 3, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(3605), QuotaMonitor.ClaudeDelay(ok: false, failures: 1, TimeSpan.FromDays(2)));
    }

    [Fact]
    public void RetryAfterIsReadAsSecondsOrAsADate()
    {
        Assert.Equal(TimeSpan.FromSeconds(230), QuotaMonitor.RetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(230))));
        var left = QuotaMonitor.RetryAfter(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2)));
        Assert.InRange(left!.Value.TotalSeconds, 100, 121);
        Assert.Equal(TimeSpan.Zero, QuotaMonitor.RetryAfter(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-2))));
        Assert.Null(QuotaMonitor.RetryAfter(null));
    }
}
