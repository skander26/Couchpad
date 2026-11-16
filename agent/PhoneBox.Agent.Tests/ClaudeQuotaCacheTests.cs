using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ClaudeQuotaCacheTests
{
    private static (QuotaMonitor Monitor, string Dir) New()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-claude-cache-").FullName;
        return (new QuotaMonitor(NullLogger<QuotaMonitor>.Instance) { ClaudeCachePath = Path.Combine(dir, "claude-quota.json") }, dir);
    }

    private static QuotaProvider Reading(long updatedAt, long? resetsAt) =>
        new("claude", "Claude", "Max", [new QuotaLimit("5-hour", 42, resetsAt, 18_000, "normal")], null, updatedAt, null);

    [Fact]
    public void ARestartShowsTheLastReadingWithItsAge()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (writer, dir) = New();
        try
        {
            writer.SaveClaudeCache(Reading(now - 600_000, now + 3_600_000));
            var restarted = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance) { ClaudeCachePath = writer.ClaudeCachePath };
            restarted.LoadClaudeCache(now);
            var claude = Assert.Single(restarted.Snapshot(), p => p.Id == "claude");
            Assert.Equal(42, Assert.Single(claude.Limits).Percent);
            Assert.Equal(now - 600_000, claude.UpdatedAt);
            Assert.Null(claude.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AReadingOlderThanAWeekOrABrokenFileIsIgnored()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (monitor, dir) = New();
        try
        {
            monitor.SaveClaudeCache(Reading(now - 8L * 24 * 3600 * 1000, null));
            monitor.LoadClaudeCache(now);
            Assert.DoesNotContain(monitor.Snapshot(), p => p.Id == "claude");

            File.WriteAllText(monitor.ClaudeCachePath, "{ not json");
            monitor.LoadClaudeCache(now);
            Assert.DoesNotContain(monitor.Snapshot(), p => p.Id == "claude");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AWindowThatResetSinceTheReadingShowsAsReset()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (monitor, dir) = New();
        try
        {
            monitor.SaveClaudeCache(Reading(now - 7_200_000, now - 60_000));
            monitor.LoadClaudeCache(now);
            var claude = Assert.Single(monitor.Snapshot(), p => p.Id == "claude");
            Assert.Equal(0, Assert.Single(claude.Limits).Percent);
        }
        finally { Directory.Delete(dir, true); }
    }
}
