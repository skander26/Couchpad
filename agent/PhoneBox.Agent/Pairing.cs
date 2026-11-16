using System.Security.Cryptography;
using System.Text;
using QRCoder;

namespace PhoneBox.Agent;

/// <summary>
/// Pairing a phone over Wi-Fi without USB. The settings page asks for a one-time code and shows a QR code with a
/// pairing link: the computer's name, addresses, Wi-Fi port, the TLS certificate fingerprint and the code. The phone
/// opens the link, pins the certificate from the fingerprint, and trades the code for the Wi-Fi key over TLS
/// (<c>POST /api/pair/claim</c>). The key itself is never in the QR code; a code works once, for a few minutes.
/// </summary>
public sealed class PairingCodes
{
    public const long LifetimeMs = 5 * 60_000;
    /// <summary>Wrong codes before the current one is dropped (a code has 128 random bits; this only stops noise).</summary>
    public const int MaxFailures = 10;

    private readonly Lock _lock = new();
    private byte[]? _code;
    private long _expiresAt;
    private int _failures;
    private long _claims;
    private long _attempt; // numbers each Start, so a late cancel from an old dialog cannot end a newer pairing

    /// <summary>How many phones have paired with a code since the agent started (the settings page watches it).</summary>
    public long Claims => Interlocked.Read(ref _claims);

    /// <summary>Starts a new pairing; the previous code stops working.</summary>
    public (string Code, long ExpiresAt, long Attempt) Start(long nowMs)
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var code = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            _code = Encoding.UTF8.GetBytes(code);
            _expiresAt = nowMs + LifetimeMs;
            _failures = 0;
            return (code, _expiresAt, ++_attempt);
        }
    }

    /// <summary>Ends a pairing when its dialog closes; a newer pairing (another attempt) is left alone.</summary>
    public void Cancel(long attempt) { lock (_lock) if (attempt == _attempt) _code = null; }

    /// <summary>True once for the current, unexpired code; a used, expired or wrong code returns false.</summary>
    public bool Claim(string? code, long nowMs)
    {
        lock (_lock)
        {
            if (_code is null || string.IsNullOrEmpty(code)) return false;
            if (nowMs > _expiresAt) { _code = null; return false; }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), _code))
            {
                if (++_failures >= MaxFailures) _code = null;
                return false;
            }
            _code = null; // one phone per code
            Interlocked.Increment(ref _claims);
            return true;
        }
    }

    /// <summary>
    /// The link in the QR code. Everything is in the fragment (after <c>#</c>), which browsers never send to a server,
    /// so opening the link in a browser instead of the app does not leak the code to the web page's host.
    /// </summary>
    public static string Link(string pc, string fingerprint, int port, IEnumerable<string> hosts, string code) =>
        "https://ersinkoc.github.io/phonebox/pair/#v=1"
        + "&pc=" + Uri.EscapeDataString(pc)
        + "&fp=" + fingerprint
        + "&port=" + port
        + "&h=" + string.Join(",", hosts.Select(Uri.EscapeDataString))
        + "&c=" + code;

    /// <summary>
    /// The start of the certificate fingerprint ("AB12 CD34"): the settings page shows it next to the QR code and the
    /// phone shows it in its confirmation, so a pairing link from anyone else is easy to spot.
    /// </summary>
    public static string Check(string fingerprint) =>
        fingerprint.Length < 8 ? fingerprint.ToUpperInvariant() : $"{fingerprint[..4]} {fingerprint[4..8]}".ToUpperInvariant();

    /// <summary>A QR code as SVG markup (shown through an &lt;img&gt;, so it cannot run anything).</summary>
    public static string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true);
    }
}
