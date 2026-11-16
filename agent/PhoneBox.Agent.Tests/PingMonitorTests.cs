using PhoneBox.Agent;
using Xunit;

public sealed class PingMonitorTests
{
    private static void WithConfig(string json, Action<ConfigStore> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-ping-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { check(new ConfigStore(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingConfigsKeepPingingTheDefaultHost() => WithConfig("{}", store =>
        Assert.Equal(PingMonitor.DefaultHost, store.Get().Ping.Host));

    [Theory]
    [InlineData("""{"ping":{"host":""}}""", "")]
    [InlineData("""{"ping":{"host":null}}""", "")]
    [InlineData("""{"ping":{"host":"  192.168.1.1 "}}""", "192.168.1.1")]
    [InlineData("""{"ping":{"host":"bad host"}}""", PingMonitor.DefaultHost)]
    public void HostIsTrimmedEmptyMeansOffAndJunkFallsBack(string json, string expected) => WithConfig(json, store =>
        Assert.Equal(expected, store.Get().Ping.Host));

    [Fact]
    public async Task AnEmptyHostSendsNothingAndClearsTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-ping-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"ping":{"host":""}}""");
        try
        {
            var monitor = new PingMonitor(new ConfigStore(path));
            monitor.Sample(12, "stale"); // as if a host had been set before
            using var cts = new CancellationTokenSource();
            await monitor.StartAsync(cts.Token);
            await Task.Delay(300);
            Assert.Null(monitor.Snapshot());
            await monitor.StopAsync(CancellationToken.None);
        }
        finally { File.Delete(path); }
    }
}
