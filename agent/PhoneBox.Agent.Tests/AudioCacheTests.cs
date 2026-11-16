using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>The volume cache behind /api/state (macOS / Linux AudioControl) with a fake reader.</summary>
public class AudioCacheTests
{
    [Fact]
    public void SnapshotReturnsAtOnceAndFillsInTheBackground()
    {
        using var gate = new ManualResetEventSlim(false);
        var audio = new AudioControl(() => { gate.Wait(TimeSpan.FromSeconds(10)); return new AudioSnapshot(42, false); });

        Assert.Null(audio.Snapshot()); // nothing read yet, and the call did not wait for the reader
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => !audio.Refreshing, TimeSpan.FromSeconds(10)));
        Assert.Equal(42, audio.Snapshot()?.Volume);
    }

    [Fact]
    public void AnOlderBackgroundReadCannotReplaceANewerCurrentReading()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var audio = new AudioControl(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // the background read starts first and is slow: it saw the old volume
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new AudioSnapshot(20, false);
            }
            return new AudioSnapshot(60, false); // a control action reads later and sees the new volume
        });

        audio.Snapshot();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(60, audio.Current()?.Volume);

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !audio.Refreshing, TimeSpan.FromSeconds(10)));
        Assert.Equal(60, audio.Snapshot()?.Volume);
        Assert.Equal(60, audio.Current()?.Volume); // still fresh, and still the newer reading
    }
}
