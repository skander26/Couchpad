using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

[Collection("KeySender")]
public sealed class EndpointTests : IDisposable
{
    private readonly Func<KeySender.INPUT[], uint> _originalSend = KeySender.NativeSendInput;

    public void Dispose()
    {
        KeySender.ReleaseAllMouseButtons();
        KeySender.NativeSendInput = _originalSend;
    }

    [Fact]
    public void DefaultsAndIsolatedInstancesHaveDistinctUrlsAndMutexes()
    {
        var standard = AgentEndpoint.FromArgs([]);
        var isolated = AgentEndpoint.FromArgs(["--config", "test.json", "--port", "18765"]);
        Assert.Equal(8765, standard.Port);
        Assert.Equal(@"Local\PhoneBox.Agent", standard.MutexName);
        Assert.Equal("http://127.0.0.1:18765/", isolated.SettingsUrl);
        Assert.NotEqual(standard.MutexName, isolated.MutexName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    [InlineData("8765; command")]
    public void InvalidPortsAreRejected(string value) => Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", value]));

    [Fact]
    public void MissingAndRepeatedPortsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port"]));
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", "18765", "--port", "18766"]));
    }

    [Fact]
    public void RestartPreservesTheConfigPathAndPortAsSeparateArguments()
    {
        var info = AppRestart.StartInfo("PhoneBoxAgent.exe", ["--config", "C:/test folder/config.json", "--port", "18765", "--restart"]);
        Assert.Equal(new[] { "--config", "C:/test folder/config.json", "--port", "18765", "--restart" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        var hosted = AppRestart.StartInfo("dotnet", ["--port", "18765"]);
        Assert.EndsWith("PhoneBoxAgent.dll", hosted.ArgumentList[0]);
    }

    [Fact]
    public void RemoteInputDefaultsOffAndReturns403WhenDisabled()
    {
        var cfg = new AgentConfig();
        Assert.False(cfg.AllowRemoteInput);
        var publicJson = JsonSerializer.Serialize(cfg.ToPublic());
        Assert.Contains("\"allowRemoteInput\":false", publicJson);

        Assert.Equal(StatusCodes.Status403Forbidden, KeySender.HandleRemoteRequest(cfg, "move", 5, 5, null, null, null).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, KeySender.HandleRemoteRequest(cfg, "click", null, null, "left", null, null).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, KeySender.HandleRemoteRequest(cfg, "key", null, null, null, "enter", null).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, KeySender.HandleRemoteRequest(cfg, "text", null, null, null, null, "hi").StatusCode);
    }

    [Fact]
    public void RemoteInputAcceptsValidOperationsWhenEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        KeySender.NativeSendInput = inputs => (uint)inputs.Length;
        var cfg = new AgentConfig { AllowRemoteInput = true };

        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "move", 10, -5, null, null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "click", null, null, null, null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "dblclick", null, null, "left", null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "click", null, null, "right", null, null, dbl: true).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "down", null, null, "middle", null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "up", null, null, "middle", null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "scroll", null, -120, null, null, null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "key", null, null, null, "ctrl+c", null).StatusCode);
        Assert.Equal(200, KeySender.HandleRemoteRequest(cfg, "text", null, null, null, null, "hello").StatusCode);
    }

    [Theory]
    [InlineData(null, null, null, null, null, null)]
    [InlineData("", null, null, null, null, null)]
    [InlineData("hack", null, null, null, null, null)]
    [InlineData("move", null, null, null, null, null)]
    [InlineData("scroll", null, null, null, null, null)]
    [InlineData("click", null, null, "", null, null)]
    [InlineData("click", null, null, "fourth", null, null)]
    [InlineData("down", null, null, "xbutton1", null, null)]
    [InlineData("up", null, null, "invalid", null, null)]
    [InlineData("key", null, null, null, "", null)]
    [InlineData("key", null, null, null, "ctrl+banana", null)]
    [InlineData("text", null, null, null, null, null)]
    [InlineData("text", null, null, null, null, "")]
    public void RemoteInputRejectsInvalidRequestsWith400(string? op, int? dx, int? dy, string? btn, string? key, string? text)
    {
        var cfg = new AgentConfig { AllowRemoteInput = true };
        Assert.Equal(StatusCodes.Status400BadRequest, KeySender.HandleRemoteRequest(cfg, op, dx, dy, btn, key, text).StatusCode);
    }

    [Fact]
    public void RemoteInputRejectsOversizedTextAndKeyWith400()
    {
        var cfg = new AgentConfig { AllowRemoteInput = true };
        Assert.Equal(StatusCodes.Status400BadRequest,
            KeySender.HandleRemoteRequest(cfg, "text", null, null, null, null, new string('a', KeySender.MaxTextLength + 1)).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest,
            KeySender.HandleRemoteRequest(cfg, "key", null, null, null, new string('a', 129), null).StatusCode);
    }

    [Fact]
    public void RemoteInputReturns409WhenSendInputFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        KeySender.NativeSendInput = _ => 0u;
        var cfg = new AgentConfig { AllowRemoteInput = true };

        var (status, body) = KeySender.HandleRemoteRequest(cfg, "move", 5, 5, null, null, null);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("\"ok\":false", json);
        Assert.Contains("blocked", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteInputDisableTurnsOffSettingAndReleasesHeldButtons()
    {
        if (!OperatingSystem.IsWindows()) return;
        KeySender.NativeSendInput = inputs => (uint)inputs.Length;
        var cfg = new AgentConfig { AllowRemoteInput = true };
        var disabledCallbackCalled = false;

        Assert.Null(KeySender.MouseDown("left", nowMs: 1000));
        Assert.Equal(["left"], KeySender.HeldButtons);

        var (status, body) = KeySender.HandleRemoteRequest(cfg, "disable", null, null, null, null, null, onDisable: () => disabledCallbackCalled = true);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(cfg.AllowRemoteInput);
        Assert.True(disabledCallbackCalled);
        Assert.Empty(KeySender.HeldButtons);
        Assert.Contains("\"allowRemoteInput\":false", JsonSerializer.Serialize(body));
    }

    [Fact]
    public void RemoteBrowserAndRewritePathsAreBlocked()
    {
        Assert.False(BrowserGuard.ShouldRewritePanelPath(new PathString("/agent/api/remote"), out _));
        Assert.True(BrowserGuard.ShouldRewritePanelPath(new PathString("/agent/api/state"), out var rest));
        Assert.Equal("/state", rest.Value);

        Assert.True(BrowserGuard.AllowRemote("", ""));
        Assert.True(BrowserGuard.AllowRemote(null, null));
        Assert.False(BrowserGuard.AllowRemote("http://127.0.0.1:8765", null));
        Assert.False(BrowserGuard.AllowRemote("https://evil.example", null));
        Assert.False(BrowserGuard.AllowRemote("", "same-origin"));
        Assert.False(BrowserGuard.AllowRemote("", "cross-site"));
        Assert.True(BrowserGuard.IsForeign("cross-site", "cors", "POST", new PathString("/api/remote")));
        Assert.True(BrowserGuard.IsForeign("same-site", "cors", "POST", new PathString("/agent/api/remote")));
    }

    [Fact]
    public void UpdateDefaultsAndSourceBuildGuardAreSafe()
    {
        Assert.False(new UpdateConfig().Auto);
        Assert.False(UpdateService.ShouldAutoUpdate(auto: true, singleFile: false));
        Assert.True(UpdateService.ShouldAutoUpdate(auto: true, singleFile: true));

        var tmp = Path.Combine(Path.GetTempPath(), $"phonebox-upd-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, """{ "updates": { "auto": false, "repo": "https://evil.example/a/b" } }""");
            var cfg = new ConfigStore(tmp).Get();
            Assert.False(cfg.Updates.Auto);
            Assert.Equal("skander26/Couchpad", cfg.Updates.Repo);

            File.WriteAllText(tmp, """{ "updates": { "auto": true, "repo": "myfork/phonebox" } }""");
            var cfg2 = new ConfigStore(tmp).Get();
            Assert.Equal("myfork/phonebox", cfg2.Updates.Repo);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
