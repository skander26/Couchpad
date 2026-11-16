using PhoneBox.Agent;
using Xunit;

[Collection("KeySender")]
public sealed class KeySenderTests : IDisposable
{
    private readonly Func<KeySender.INPUT[], uint> _originalSend = KeySender.NativeSendInput;
    private readonly List<KeySender.INPUT> _sent = [];

    public void Dispose()
    {
        KeySender.ReleaseAllMouseButtons();
        KeySender.NativeSendInput = _originalSend;
    }

    private void MockSendInput(bool succeed = true)
    {
        _sent.Clear();
        KeySender.NativeSendInput = inputs =>
        {
            _sent.AddRange(inputs);
            return succeed ? (uint)inputs.Length : 0u;
        };
    }

    [Fact]
    public void ParsesCombosAndSequences()
    {
        var combos = KeySender.Parse("Ctrl+Shift+M, cmd+Space,F13, play");
        Assert.Equal([["ctrl", "shift", "m"], ["win", "space"], ["f13"], ["playpause"]], combos);
    }

    [Theory]
    [InlineData("ctrl+a+b")]      // two keys besides modifiers
    [InlineData("ctrl+banana")]   // unknown key
    [InlineData("f25")]
    [InlineData(" , ")]           // nothing
    public void RejectsBadSpecs(string spec) => Assert.Throws<ArgumentException>(() => KeySender.Parse(spec));

    [Fact]
    public void ModifiersAloneAreAllowed() => Assert.Equal([["win"]], KeySender.Parse("Win"));

    [Fact]
    public void ClampMoveAndScrollRespectBounds()
    {
        Assert.Equal((2000, -2000), KeySender.ClampMove(5000, -5000));
        Assert.Equal((-123, 456), KeySender.ClampMove(-123, 456));
        Assert.Equal((1200, -1200), KeySender.ClampScroll(3000, -3000));
        Assert.Equal((120, -240), KeySender.ClampScroll(120, -240));
    }

    [Theory]
    [InlineData("left", "left")]
    [InlineData(" RIGHT ", "right")]
    [InlineData("Middle", "middle")]
    public void NormalizeButtonAcceptsWhitelistedNames(string input, string expected) =>
        Assert.Equal(expected, KeySender.NormalizeButton(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("side")]
    [InlineData("button4")]
    public void NormalizeButtonRejectsUnknownNames(string? input) =>
        Assert.Throws<ArgumentException>(() => KeySender.NormalizeButton(input));

    [Fact]
    public void MoveMouseEmitsClampedRelativeInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput();

        Assert.Null(KeySender.MoveMouse(0, 0));
        Assert.Empty(_sent);

        Assert.Null(KeySender.MoveMouse(5000, -20));
        var inp = Assert.Single(_sent);
        Assert.Equal(0u, inp.type);
        Assert.Equal(2000, inp.u.mi.dx);
        Assert.Equal(-20, inp.u.mi.dy);
        Assert.Equal(0x0001u, inp.u.mi.dwFlags);
    }

    [Theory]
    [InlineData("left", 0x0002u, 0x0004u)]
    [InlineData("right", 0x0008u, 0x0010u)]
    [InlineData("middle", 0x0020u, 0x0040u)]
    public void ClickAndDoubleClickEmitExpectedMouseFlags(string button, uint downFlag, uint upFlag)
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput();

        Assert.Null(KeySender.ClickMouse(button));
        Assert.Equal([downFlag, upFlag], _sent.Select(x => x.u.mi.dwFlags));
        Assert.All(_sent, x => Assert.Equal(0u, x.type));

        _sent.Clear();
        Assert.Null(KeySender.DoubleClickMouse(button));
        Assert.Equal([downFlag, upFlag, downFlag, upFlag], _sent.Select(x => x.u.mi.dwFlags));
    }

    [Fact]
    public void ScrollMouseEmitsVerticalAndHorizontalWheelEvents()
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput();

        Assert.Null(KeySender.ScrollMouse(0, 0));
        Assert.Empty(_sent);

        Assert.Null(KeySender.ScrollMouse(3000, -120));
        Assert.Equal(2, _sent.Count);
        Assert.Equal(0x0800u, _sent[0].u.mi.dwFlags);
        Assert.Equal(unchecked((uint)-120), _sent[0].u.mi.mouseData);
        Assert.Equal(0x1000u, _sent[1].u.mi.dwFlags);
        Assert.Equal(1200u, _sent[1].u.mi.mouseData);
    }

    [Fact]
    public void StuckButtonAutoReleasesAfterFiveSeconds()
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput();

        Assert.Null(KeySender.MouseDown("left", nowMs: 1000));
        Assert.Equal(["left"], KeySender.HeldButtons);
        Assert.Equal(0, KeySender.SweepStuckButtons(nowMs: 5999));
        Assert.Equal(["left"], KeySender.HeldButtons);

        _sent.Clear();
        Assert.Equal(1, KeySender.SweepStuckButtons(nowMs: 6000));
        Assert.Empty(KeySender.HeldButtons);
        var up = Assert.Single(_sent);
        Assert.Equal(0x0004u, up.u.mi.dwFlags);
    }

    [Fact]
    public void ExplicitMouseUpClearsStuckButtonTracking()
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput();

        Assert.Null(KeySender.MouseDown("right", nowMs: 1000));
        Assert.Null(KeySender.MouseUp("right"));
        Assert.Empty(KeySender.HeldButtons);
        Assert.Equal(0, KeySender.SweepStuckButtons(nowMs: 100_000));
    }

    [Fact]
    public void SendInputFailureReturnsErrorWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows()) return;
        MockSendInput(succeed: false);

        Assert.NotNull(KeySender.MoveMouse(10, 10));
        Assert.NotNull(KeySender.ClickMouse("left"));
        Assert.NotNull(KeySender.MouseDown("left", nowMs: 1000));
        Assert.Empty(KeySender.HeldButtons); // failed down must not register as held
        Assert.NotNull(KeySender.ScrollMouse(0, 120));
        Assert.NotNull(KeySender.SendRemoteKeys("enter"));
        Assert.NotNull(KeySender.SendRemoteText("hello"));
    }
}
