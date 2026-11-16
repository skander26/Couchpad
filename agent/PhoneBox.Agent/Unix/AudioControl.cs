using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Master volume on Linux (PipeWire's wpctl, or PulseAudio's pactl) and the selected macOS output.
/// macOS falls back to CoreAudio channels and, for a matching display, optional DDC/CI readback.
/// </summary>
public sealed partial class AudioControl
{
    private const int MaxAgeMs = 900;
    private readonly Lock _lock = new();
    private readonly Func<AudioSnapshot?> _read;
    private AudioSnapshot? _cached;
    private long _at;          // when _cached was read; 0 = never read or invalidated
    private long _gen;         // bumped by Invalidate, so a read from before a volume change cannot land after it
    private long _seq, _written; // read tickets: a reading only lands if no later-started read has landed
    private int _refreshing;   // 1 while a background read runs

    public AudioControl() : this(Read) { }

    /// <summary>For tests: a fake reader instead of osascript / wpctl / pactl.</summary>
    internal AudioControl(Func<AudioSnapshot?> read) => _read = read;

    internal bool Refreshing => Volatile.Read(ref _refreshing) == 1;

    /// <summary>
    /// The last reading, refreshed in the background. Never waits for osascript / wpctl / pactl: the live stream
    /// calls this every second for every phone, and under heavy CPU load those helpers can take seconds each.
    /// </summary>
    public AudioSnapshot? Snapshot()
    {
        long at; AudioSnapshot? cached;
        lock (_lock) { at = _at; cached = _cached; }
        if (Environment.TickCount64 - at >= MaxAgeMs && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            _ = Task.Run(() =>
            {
                try { var (ticket, gen) = BeginRead(); Store(ticket, gen, _read()); }
                catch { }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        return cached;
    }

    /// <summary>A reading for a control action: the cached one while fresh, otherwise read now (may wait for the helper).</summary>
    public AudioSnapshot? Current()
    {
        lock (_lock)
            if (_at != 0 && Environment.TickCount64 - _at < MaxAgeMs) return _cached;
        var (ticket, gen) = BeginRead();
        var s = _read();
        Store(ticket, gen, s);
        return s;
    }

    private (long Ticket, long Gen) BeginRead() { lock (_lock) return (++_seq, _gen); }

    private void Store(long ticket, long gen, AudioSnapshot? s)
    {
        lock (_lock)
            if (_gen == gen && ticket > _written) { _written = ticket; _cached = s; _at = Environment.TickCount64; }
    }

    public bool SetVolume(int level)
    {
        if (OperatingSystem.IsMacOS() && Current() is not { CanControl: true }) return false;
        level = Math.Clamp(level, 0, 100);
        var ok = OperatingSystem.IsMacOS()
            ? Sh.Run("osascript", "-e", $"set volume output volume {level} without output muted") is not null
            : Sh.Which("wpctl") is not null
                ? Sh.Run("wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", (level / 100.0).ToString("0.00", CultureInfo.InvariantCulture)) is not null
                  && (level == 0 || Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0") is not null)
                : Sh.Run("pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{level}%") is not null;
        Invalidate();
        return ok;
    }

    public bool Step(int delta) => Current() is { } s && SetVolume(s.Volume + delta);

    public bool ToggleMute()
    {
        if (OperatingSystem.IsMacOS() && Current() is not { CanControl: true }) return false;
        var ok = OperatingSystem.IsMacOS()
            ? Sh.Run("osascript", "-e", "set volume output muted (not (output muted of (get volume settings)))") is not null
            : Sh.Which("wpctl") is not null
                ? Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle") is not null
                : Sh.Run("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle") is not null;
        Invalidate();
        return ok;
    }

    private void Invalidate() { lock (_lock) { _at = 0; _gen++; } }

    private static AudioSnapshot? Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = MacAudioProbe.SelectedOutput();
            var s = Sh.Run("osascript", "-e", "set v to get volume settings\nreturn ((output volume of v) as string) & \",\" & ((output muted of v) as string)")?.Trim();
            var p = s?.Split(',');
            if (p is { Length: 2 } && int.TryParse(p[0], out var vol) && bool.TryParse(p[1], out var nativeMuted))
                return new AudioSnapshot(vol, nativeMuted, output is { } selected && MacAudioProbe.CanControl(selected.Id));
            if (output is null) return null;
            var level = output.Value.Volume ?? MacAudioProbe.DisplayVolume(output.Value.Id, output.Value.Name);
            return level is { } measured ? new AudioSnapshot(measured, output.Value.Muted ?? false, false) : null;
        }
        if (Sh.Which("wpctl") is not null)
        {
            // "Volume: 0.45 [MUTED]"
            var m = WpctlRe().Match(Sh.Run("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@") ?? "");
            if (m.Success) return new AudioSnapshot((int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 100), m.Groups[2].Success);
        }
        var vol2 = PercentRe().Match(Sh.Run("pactl", "get-sink-volume", "@DEFAULT_SINK@") ?? "");
        if (!vol2.Success) return null;
        var muted = (Sh.Run("pactl", "get-sink-mute", "@DEFAULT_SINK@") ?? "").Contains("yes");
        return new AudioSnapshot(int.Parse(vol2.Groups[1].Value), muted);
    }

    [GeneratedRegex(@"Volume:\s*([\d.]+)(\s*\[MUTED\])?")] private static partial Regex WpctlRe();
    [GeneratedRegex(@"(\d+)%")] private static partial Regex PercentRe();
}
