using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Sends key combinations, text, and mouse input to the computer for shortcut keys, macros, and /api/remote:
/// "ctrl+shift+m", "win+d", "f13", "playpause"; several combos separated by commas are pressed one after another.
///  - Windows: SendInput (keyboard and mouse).
///  - Linux: xdotool (X11) or wtype (Wayland) for keyboard; xdotool for mouse.
///  - macOS: System Events via osascript for keyboard (needs Accessibility permission); mouse input is unsupported.
/// </summary>
public static class KeySender
{
    public const int MaxMoveDelta = 2000;
    public const int MaxScrollDelta = 1200;
    public const int MaxTextLength = 500;
    public const int StuckButtonTimeoutMs = 5000;
    public static readonly string[] MouseButtons = ["left", "right", "middle"];

    private static readonly string[] Modifiers = ["ctrl", "shift", "alt", "win"];

    private const uint INPUT_MOUSE = 0u;
    private const uint INPUT_KEYBOARD = 1u;
    private const uint KEYEVENTF_EXTENDEDKEY = 1u;
    private const uint KEYEVENTF_KEYUP = 2u;
    private const uint KEYEVENTF_UNICODE = 4u;

    private const uint MOUSEEVENTF_MOVE = 0x0001u;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002u;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004u;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008u;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010u;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020u;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040u;
    private const uint MOUSEEVENTF_WHEEL = 0x0800u;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000u;

    private static readonly Lock _heldLock = new();
    private static readonly Dictionary<string, (long HeldAt, bool Synthetic)> _heldButtons = [];
    private static System.Threading.Timer? _stuckTimer;

    internal static Func<INPUT[], uint> NativeSendInput { get; set; } =
        inputs => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    public static IReadOnlyCollection<string> HeldButtons
    {
        get { lock (_heldLock) return _heldButtons.Keys.ToArray(); }
    }

    /// <summary>Splits "Ctrl+Shift+M, Win+D" into combos of normalized key names; throws on unknown keys.</summary>
    public static List<List<string>> Parse(string spec)
    {
        var combos = new List<List<string>>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var keys = part.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).ToList();
            if (keys.Count == 0) continue;
            if (keys.Count(k => !Modifiers.Contains(k)) > 1) throw new ArgumentException($"\"{part}\" has more than one key besides Ctrl / Shift / Alt / Win");
            foreach (var k in keys) if (!Modifiers.Contains(k) && !IsKnown(k)) throw new ArgumentException($"unknown key \"{k}\"");
            combos.Add(keys);
        }
        if (combos.Count == 0) throw new ArgumentException("no keys given");
        return combos;
    }

    private static string Normalize(string k) => k.ToLowerInvariant() switch
    {
        "control" or "ctl" => "ctrl",
        "cmd" or "command" or "super" or "meta" or "windows" or "⌘" => "win",
        "option" or "opt" or "⌥" => "alt",
        "return" => "enter",
        "escape" => "esc",
        "del" => "delete",
        "bksp" => "backspace",
        "pgup" => "pageup",
        "pgdn" or "pagedn" => "pagedown",
        "prtsc" or "print" => "printscreen",
        "play" or "pause" or "play/pause" => "playpause",
        "next" => "nexttrack",
        "prev" or "previous" => "prevtrack",
        "volup" => "volumeup",
        "voldown" => "volumedown",
        var x => x,
    };

    private static bool IsKnown(string k) =>
        (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) || Special.ContainsKey(k) || (k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24);

    // name -> (Windows virtual key, xdotool name, macOS key code)
    private static readonly Dictionary<string, (byte Vk, string X, int Mac)> Special = new()
    {
        ["enter"] = (0x0D, "Return", 36), ["tab"] = (0x09, "Tab", 48), ["esc"] = (0x1B, "Escape", 53), ["space"] = (0x20, "space", 49),
        ["backspace"] = (0x08, "BackSpace", 51), ["delete"] = (0x2E, "Delete", 117), ["insert"] = (0x2D, "Insert", 114),
        ["home"] = (0x24, "Home", 115), ["end"] = (0x23, "End", 119), ["pageup"] = (0x21, "Prior", 116), ["pagedown"] = (0x22, "Next", 121),
        ["up"] = (0x26, "Up", 126), ["down"] = (0x28, "Down", 125), ["left"] = (0x25, "Left", 123), ["right"] = (0x27, "Right", 124),
        ["printscreen"] = (0x2C, "Print", 105), ["minus"] = (0xBD, "minus", 27), ["equals"] = (0xBB, "equal", 24),
        ["comma"] = (0xBC, "comma", 43), ["period"] = (0xBE, "period", 47), ["slash"] = (0xBF, "slash", 44), ["semicolon"] = (0xBA, "semicolon", 41),
        ["playpause"] = (0xB3, "XF86AudioPlay", -1), ["nexttrack"] = (0xB0, "XF86AudioNext", -1), ["prevtrack"] = (0xB1, "XF86AudioPrev", -1),
        ["mute"] = (0xAD, "XF86AudioMute", -1), ["volumeup"] = (0xAF, "XF86AudioRaiseVolume", -1), ["volumedown"] = (0xAE, "XF86AudioLowerVolume", -1),
    };

    public static void SendKeys(string spec)
    {
        var combos = Parse(spec);
        foreach (var combo in combos)
        {
            Send(combo);
            if (combos.Count > 1) Thread.Sleep(60);
        }
    }

    private static void Send(List<string> combo)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeSendInput(BuildComboInputs(combo));
            return;
        }
#if !WINDOWS
        SendUnixCombo(combo);
#endif
    }

    public static void TypeText(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeSendInput(BuildTextInputs(text));
            return;
        }
#if !WINDOWS
        TypeUnixText(text);
#endif
    }

    public static string NormalizeButton(string? button)
    {
        var normalized = button?.Trim().ToLowerInvariant() ?? "";
        if (!MouseButtons.Contains(normalized))
            throw new ArgumentException($"unknown mouse button \"{button}\"");
        return normalized;
    }

    public static (int Dx, int Dy) ClampMove(int dx, int dy) =>
        (Math.Clamp(dx, -MaxMoveDelta, MaxMoveDelta), Math.Clamp(dy, -MaxMoveDelta, MaxMoveDelta));

    public static (int Dx, int Dy) ClampScroll(int dx, int dy) =>
        (Math.Clamp(dx, -MaxScrollDelta, MaxScrollDelta), Math.Clamp(dy, -MaxScrollDelta, MaxScrollDelta));

    /// <summary>Moves the cursor by a relative delta; returns null on success or an error message.</summary>
    public static string? MoveMouse(int dx, int dy)
    {
        (dx, dy) = ClampMove(dx, dy);
        if (dx == 0 && dy == 0) return null;
        if (OperatingSystem.IsWindows())
            return SendWindowsInputs([Mouse(MOUSEEVENTF_MOVE, dx: dx, dy: dy)]);
        return RunUnixMouse(
            () => ["mousemove_relative", "--", dx.ToString(), dy.ToString()],
            "Mouse input is not supported on macOS",
            "Mouse input on Linux requires xdotool");
    }

    /// <summary>Presses a mouse button down and arms stuck-button auto-release; returns null on success.</summary>
    public static string? MouseDown(string button, long? nowMs = null)
    {
        var btn = NormalizeButton(button);
        var err = SendMouseButtonOp(btn, isDown: true);
        if (err is not null) return err;
        lock (_heldLock)
        {
            _heldButtons[btn] = (nowMs ?? Environment.TickCount64, nowMs.HasValue);
            if (!nowMs.HasValue)
            {
                _stuckTimer ??= new System.Threading.Timer(_ => SweepStuckButtons(), null, Timeout.Infinite, Timeout.Infinite);
                _stuckTimer.Change(250, 250);
            }
        }
        return null;
    }

    /// <summary>Releases a mouse button and clears stuck-button tracking; returns null on success.</summary>
    public static string? MouseUp(string button)
    {
        var btn = NormalizeButton(button);
        UntrackHeldButton(btn);
        return SendMouseButtonOp(btn, isDown: false);
    }

    /// <summary>Clicks (or double-clicks) a mouse button; returns null on success.</summary>
    public static string? ClickMouse(string button, bool doubleClick = false)
    {
        var btn = NormalizeButton(button);
        UntrackHeldButton(btn);
        if (OperatingSystem.IsWindows())
        {
            var down = Mouse(DownFlag(btn));
            var up = Mouse(UpFlag(btn));
            return SendWindowsInputs(doubleClick ? [down, up, down, up] : [down, up]);
        }
        var xb = XButton(btn);
        return RunUnixMouse(
            () => doubleClick ? ["click", "--repeat", "2", "--delay", "80", xb] : ["click", xb],
            "Mouse input is not supported on macOS",
            "Mouse input on Linux requires xdotool");
    }

    public static string? DoubleClickMouse(string button) => ClickMouse(button, doubleClick: true);

    /// <summary>Scrolls vertically (dy) and/or horizontally (dx); returns null on success.</summary>
    public static string? ScrollMouse(int dx, int dy)
    {
        (dx, dy) = ClampScroll(dx, dy);
        if (dx == 0 && dy == 0) return null;
        if (OperatingSystem.IsWindows())
        {
            var inputs = new List<INPUT>(2);
            if (dy != 0) inputs.Add(Mouse(MOUSEEVENTF_WHEEL, mouseData: unchecked((uint)dy)));
            if (dx != 0) inputs.Add(Mouse(MOUSEEVENTF_HWHEEL, mouseData: unchecked((uint)dx)));
            return SendWindowsInputs(inputs.ToArray());
        }
        if (OperatingSystem.IsMacOS()) return "Mouse input is not supported on macOS";
#if !WINDOWS
        if (Sh.Which("xdotool") is null) return "Mouse input on Linux requires xdotool";
        if (dy != 0)
        {
            var btn = dy > 0 ? "4" : "5";
            var count = Math.Max(1, Math.Abs(dy) / 120);
            if (Sh.Run("xdotool", "click", "--repeat", count.ToString(), "--delay", "20", btn) is null)
                return "xdotool failed to send scroll input";
        }
        if (dx != 0)
        {
            var btn = dx > 0 ? "7" : "6";
            var count = Math.Max(1, Math.Abs(dx) / 120);
            if (Sh.Run("xdotool", "click", "--repeat", count.ToString(), "--delay", "20", btn) is null)
                return "xdotool failed to send horizontal scroll input";
        }
        return null;
#else
        return "Mouse input is not supported on this platform";
#endif
    }

    /// <summary>Releases any mouse button held past <see cref="StuckButtonTimeoutMs"/>; returns how many were released.</summary>
    public static int SweepStuckButtons(long? nowMs = null)
    {
        List<string>? expired = null;
        var now = nowMs ?? Environment.TickCount64;
        lock (_heldLock)
        {
            foreach (var (btn, state) in _heldButtons)
            {
                if (!nowMs.HasValue && state.Synthetic) continue;
                if (now - state.HeldAt >= StuckButtonTimeoutMs)
                    (expired ??= []).Add(btn);
            }
            if (expired is not null)
                foreach (var btn in expired)
                    _heldButtons.Remove(btn);
            if (_heldButtons.Count == 0)
                _stuckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (expired is null) return 0;
        foreach (var btn in expired)
        {
            try { _ = SendMouseButtonOp(btn, isDown: false); } catch { }
        }
        return expired.Count;
    }

    /// <summary>Immediately releases all tracked held mouse buttons (e.g. when remote input is turned off).</summary>
    public static void ReleaseAllMouseButtons()
    {
        string[] held;
        lock (_heldLock)
        {
            held = _heldButtons.Keys.ToArray();
            _heldButtons.Clear();
            _stuckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        foreach (var btn in held)
        {
            try { _ = SendMouseButtonOp(btn, isDown: false); } catch { }
        }
    }

    /// <summary>Sends key combinations for /api/remote; returns null on success or an error message.</summary>
    public static string? SendRemoteKeys(string spec)
    {
        var combos = Parse(spec);
        if (OperatingSystem.IsWindows())
        {
            for (var i = 0; i < combos.Count; i++)
            {
                if (SendWindowsInputs(BuildComboInputs(combos[i])) is { } err) return err;
                if (i + 1 < combos.Count) Thread.Sleep(60);
            }
            return null;
        }
#if !WINDOWS
        try
        {
            for (var i = 0; i < combos.Count; i++)
            {
                SendUnixCombo(combos[i]);
                if (i + 1 < combos.Count) Thread.Sleep(60);
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
#else
        return "Keyboard input is not supported on this platform";
#endif
    }

    /// <summary>Types Unicode text for /api/remote; returns null on success or an error message.</summary>
    public static string? SendRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            throw new ArgumentException($"text must be 1–{MaxTextLength} characters");
        if (OperatingSystem.IsWindows())
            return SendWindowsInputs(BuildTextInputs(text));
#if !WINDOWS
        try { TypeUnixText(text); return null; }
        catch (Exception ex) { return ex.Message; }
#else
        return "Text input is not supported on this platform";
#endif
    }

    /// <summary>Validates and dispatches a POST /api/remote request.</summary>
    public static (int StatusCode, object Body) HandleRemoteRequest(
        AgentConfig cfg, string? op, int? dx, int? dy, string? btn, string? key, string? text, bool? dbl = null, Action? onDisable = null)
    {
        var action = (op ?? "").Trim().ToLowerInvariant();
        if (action == "disable")
        {
            ReleaseAllMouseButtons();
            cfg.AllowRemoteInput = false;
            onDisable?.Invoke();
            return (StatusCodes.Status200OK, new { ok = true, allowRemoteInput = false });
        }
        if (!cfg.AllowRemoteInput)
            return (StatusCodes.Status403Forbidden, new { ok = false, error = "Remote input is disabled in PhoneBox settings" });
        switch (action)
        {
            case "move":
            {
                if (dx is null && dy is null)
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = "move requires dx or dy" });
                var err = MoveMouse(dx ?? 0, dy ?? 0);
                return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
            }
            case "click":
            case "dblclick":
            case "double-click":
            {
                var b = btn is null ? "left" : btn.Trim().ToLowerInvariant();
                if (!MouseButtons.Contains(b))
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = $"Unknown mouse button \"{btn}\"" });
                var isDouble = dbl == true || action is "dblclick" or "double-click";
                var err = ClickMouse(b, isDouble);
                return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
            }
            case "down":
            {
                var b = btn is null ? "left" : btn.Trim().ToLowerInvariant();
                if (!MouseButtons.Contains(b))
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = $"Unknown mouse button \"{btn}\"" });
                var err = MouseDown(b);
                return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
            }
            case "up":
            {
                var b = btn is null ? "left" : btn.Trim().ToLowerInvariant();
                if (!MouseButtons.Contains(b))
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = $"Unknown mouse button \"{btn}\"" });
                var err = MouseUp(b);
                return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
            }
            case "scroll":
            {
                if (dx is null && dy is null)
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = "scroll requires dx or dy" });
                var err = ScrollMouse(dx ?? 0, dy ?? 0);
                return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
            }
            case "key":
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = "key is required (max 128 chars)" });
                try
                {
                    var err = SendRemoteKeys(key);
                    return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
                }
                catch (ArgumentException ex)
                {
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = ex.Message });
                }
            }
            case "text":
            {
                if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = $"text must be 1–{MaxTextLength} characters" });
                try
                {
                    var err = SendRemoteText(text);
                    return err is null ? (StatusCodes.Status200OK, new { ok = true }) : (StatusCodes.Status409Conflict, new { ok = false, error = err });
                }
                catch (ArgumentException ex)
                {
                    return (StatusCodes.Status400BadRequest, new { ok = false, error = ex.Message });
                }
            }
            default:
                return (StatusCodes.Status400BadRequest, new { ok = false, error = "op must be move, click, dblclick, down, up, scroll, key or text" });
        }
    }

    private static void UntrackHeldButton(string btn)
    {
        lock (_heldLock)
        {
            _heldButtons.Remove(btn);
            if (_heldButtons.Count == 0)
                _stuckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static string? SendMouseButtonOp(string btn, bool isDown)
    {
        if (OperatingSystem.IsWindows())
            return SendWindowsInputs([Mouse(isDown ? DownFlag(btn) : UpFlag(btn))]);
        var xb = XButton(btn);
        var verb = isDown ? "mousedown" : "mouseup";
        return RunUnixMouse(
            () => [verb, xb],
            "Mouse input is not supported on macOS",
            "Mouse input on Linux requires xdotool");
    }

    private static uint DownFlag(string btn) => btn switch
    {
        "left" => MOUSEEVENTF_LEFTDOWN,
        "right" => MOUSEEVENTF_RIGHTDOWN,
        "middle" => MOUSEEVENTF_MIDDLEDOWN,
        _ => throw new ArgumentException($"unknown mouse button \"{btn}\""),
    };

    private static uint UpFlag(string btn) => btn switch
    {
        "left" => MOUSEEVENTF_LEFTUP,
        "right" => MOUSEEVENTF_RIGHTUP,
        "middle" => MOUSEEVENTF_MIDDLEUP,
        _ => throw new ArgumentException($"unknown mouse button \"{btn}\""),
    };

    private static string XButton(string btn) => btn switch
    {
        "left" => "1",
        "middle" => "2",
        "right" => "3",
        _ => throw new ArgumentException($"unknown mouse button \"{btn}\""),
    };

    private static string? RunUnixMouse(Func<string[]> makeArgs, string macMsg, string missingMsg)
    {
        if (OperatingSystem.IsMacOS()) return macMsg;
#if !WINDOWS
        if (Sh.Which("xdotool") is null) return missingMsg;
        return Sh.Run("xdotool", makeArgs()) is null ? "xdotool failed to send mouse input" : null;
#else
        return "Mouse input is not supported on this platform";
#endif
    }

    private static string? SendWindowsInputs(INPUT[] inputs)
    {
        if (inputs.Length == 0) return null;
        var sent = NativeSendInput(inputs);
        return sent == (uint)inputs.Length
            ? null
            : "Input was blocked by Windows (an elevated window or secure screen may have focus)";
    }

    private static INPUT[] BuildComboInputs(List<string> combo)
    {
        var vks = combo.Select(k => k switch
        {
            "ctrl" => (byte)0x11, "shift" => (byte)0x10, "alt" => (byte)0x12, "win" => (byte)0x5B,
            _ when Special.TryGetValue(k, out var s) => s.Vk,
            _ when k[0] == 'f' && k.Length > 1 => (byte)(0x70 + int.Parse(k[1..]) - 1),
            _ => (byte)char.ToUpperInvariant(k[0]),
        }).ToList();
        var inputs = new List<INPUT>(vks.Count * 2);
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        foreach (var vk in Enumerable.Reverse(vks)) inputs.Add(Key(vk, true));
        return inputs.ToArray();
    }

    private static INPUT[] BuildTextInputs(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var inputs = new List<INPUT>(normalized.Length * 2);
        foreach (var ch in normalized)
        {
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        return inputs.ToArray();
    }

    // arrows, Home/End, Insert/Delete, PageUp/PageDown and the right-hand keys need the "extended" flag
    private static bool Extended(byte vk) => vk is >= 0x21 and <= 0x2E or 0x5B;

    private static INPUT Key(byte vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0u) | (Extended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u) } },
    };

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0u) } },
    };

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint mouseData = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags } },
    };

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

#if !WINDOWS
    private static void SendUnixCombo(List<string> combo)
    {
        if (OperatingSystem.IsMacOS()) { MacKeystroke(combo); return; }
        // X11: xdotool; Wayland: wtype
        var x = string.Join("+", combo.Select(k => k switch
        {
            "win" => "super", "ctrl" or "shift" or "alt" => k,
            _ when Special.TryGetValue(k, out var s) => s.X,
            _ when k[0] == 'f' && k.Length > 1 => k.ToUpperInvariant(),
            _ => k,
        }));
        if (Sh.Which("xdotool") is not null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) { Sh.Run("xdotool", "key", "--clearmodifiers", x); return; }
        if (Sh.Which("wtype") is not null)
        {
            var args = new List<string>();
            var mods = combo.Where(Modifiers.Contains).Select(m => m == "win" ? "logo" : m).ToList();
            foreach (var m in mods) args.AddRange(["-M", m]);
            var key = combo.FirstOrDefault(k => !Modifiers.Contains(k));
            if (key is not null) args.AddRange(["-k", Special.TryGetValue(key, out var s) ? s.X : key]);
            foreach (var m in Enumerable.Reverse(mods)) args.AddRange(["-m", m]);
            Sh.Run("wtype", [.. args]);
            return;
        }
        if (Sh.Which("xdotool") is not null) { Sh.Run("xdotool", "key", "--clearmodifiers", x); return; }
        throw new InvalidOperationException("sending keys needs xdotool (X11) or wtype (Wayland)");
    }

    private static void TypeUnixText(string text)
    {
        if (OperatingSystem.IsMacOS()) { Osa($"tell application \"System Events\" to keystroke \"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""); return; }
        if (Sh.Which("xdotool") is not null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null) { Sh.Run("xdotool", "type", "--delay", "8", "--", text); return; }
        if (Sh.Which("wtype") is not null) { Sh.Run("wtype", "--", text); return; }
        if (Sh.Which("xdotool") is not null) { Sh.Run("xdotool", "type", "--delay", "8", "--", text); return; }
        throw new InvalidOperationException("typing needs xdotool (X11) or wtype (Wayland)");
    }

    private static void MacKeystroke(List<string> combo)
    {
        var mods = combo.Where(Modifiers.Contains).Select(m => m switch { "win" => "command down", "ctrl" => "control down", "alt" => "option down", _ => "shift down" }).ToList();
        var key = combo.FirstOrDefault(k => !Modifiers.Contains(k));
        if (key is not null && Special.TryGetValue(key, out var s) && s.Mac < 0)
        {
            // media keys have no key code in System Events; use the media remote instead where we can
            if (key == "playpause") { Sh.Run("osascript", "-e", "tell application \"Music\" to playpause"); return; }
            throw new InvalidOperationException($"{key} cannot be sent on macOS");
        }
        var using_ = mods.Count > 0 ? $" using {{{string.Join(", ", mods)}}}" : "";
        string action;
        if (key is null) throw new InvalidOperationException("only modifier keys given");
        else if (Special.TryGetValue(key, out var sp)) action = $"key code {sp.Mac}";
        else if (key[0] == 'f' && key.Length > 1) action = $"key code {MacF(int.Parse(key[1..]))}";
        else action = $"keystroke \"{key}\"";
        Osa($"tell application \"System Events\" to {action}{using_}");
    }

    private static int MacF(int n) => n switch
    {
        1 => 122, 2 => 120, 3 => 99, 4 => 118, 5 => 96, 6 => 97, 7 => 98, 8 => 100, 9 => 101, 10 => 109, 11 => 103, 12 => 111,
        13 => 105, 14 => 107, 15 => 113, 16 => 106, 17 => 64, 18 => 79, 19 => 80, 20 => 90, _ => throw new InvalidOperationException($"F{n} cannot be sent on macOS"),
    };

    private static void Osa(string script)
    {
        if (Sh.RunWithTimeout(5000, "osascript", "-e", script) is null)
            throw new InvalidOperationException("macOS refused: allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility");
    }
#endif
}
