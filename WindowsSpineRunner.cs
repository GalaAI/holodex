using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Holodex;

/// <summary>
/// The live edge of the Input Spine inside Holodex: real keys, typing and clicks through SendInput, apps through the
/// shell, commands through PowerShell or bash, and agent prompts through Holodex's own model routes. Input Studio sent
/// keys, typing and clicks to PC Mod on :8830, which no longer answers those operations, so Holodex does them itself.
/// </summary>
internal sealed class WindowsSpineRunner(ArkStationClient ark) : ISpineRunner
{
    public long Now() => Environment.TickCount64;

    public Task<SpineOutcome> InputAsync(string operation, KitAction action) => Task.FromResult(operation switch
    {
        "pc.key.press" => PressKey(action.Key!, action.Modifiers),
        "pc.text.type" => TypeText(action.Text!),
        "pc.pointer.click" => Click(action.X, action.Y, action.Button, action.ClickCount),
        "pc.app.open" => Open(action.Target!),
        _ => SpineOutcome.Fail($"unknown input operation \"{operation}\""),
    });

    // ------------------------------------------------------------------------------------------------------- keys

    private static readonly Dictionary<string, ushort> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["arrowup"] = 0x26, ["arrowdown"] = 0x28, ["arrowleft"] = 0x25, ["arrowright"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD, ["mute"] = 0xAD,
        ["medianext"] = 0xB0, ["mediaprev"] = 0xB1, ["mediaprevious"] = 0xB1, ["mediastop"] = 0xB2,
        ["mediaplaypause"] = 0xB3, ["playpause"] = 0xB3, ["apps"] = 0x5D, ["menu"] = 0x5D,
        ["win"] = 0x5B, ["meta"] = 0x5B, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10,
    };

    /// <summary>
    /// The virtual-key code for a Cursor Kit key name: F1 to F24, a letter or digit, a named key ("Enter", "PageUp",
    /// "VolumeUp") or a Windows Keys name. Null for a name that is not a key, so the run says so instead of guessing.
    /// </summary>
    public static ushort? VirtualKeyFor(string name)
    {
        string k = name.Trim();
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return null; // punctuation goes through a type action, which is layout-safe
        }
        if (KeyAliases.TryGetValue(k, out var alias)) return alias;
        if ((k[0] is 'F' or 'f') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24) return (ushort)(0x70 + f - 1);
        if (Enum.TryParse<Keys>(k, ignoreCase: true, out var key) && Enum.IsDefined(key) && (int)key is > 0 and < 0xFF) return (ushort)key;
        return null;
    }

    public static ushort ModifierKey(string m) => m switch
    {
        "CTRL" => 0x11,
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        _ => 0x5B, // META
    };

    private static SpineOutcome PressKey(string key, IReadOnlyList<string> modifiers)
    {
        if (VirtualKeyFor(key) is not ushort vk) return SpineOutcome.Fail($"\"{key}\" is not a key Holodex can press");
        var inputs = new List<INPUT>();
        foreach (var m in modifiers) inputs.Add(KeyInput(ModifierKey(m), up: false));
        inputs.Add(KeyInput(vk, up: false));
        inputs.Add(KeyInput(vk, up: true));
        foreach (var m in modifiers.Reverse()) inputs.Add(KeyInput(ModifierKey(m), up: true));
        return Send(inputs, $"pressed {string.Join("+", modifiers.Append(key))}");
    }

    private static SpineOutcome TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n') { inputs.Add(KeyInput(0x0D, false)); inputs.Add(KeyInput(0x0D, true)); continue; }
            inputs.Add(UnicodeInput(c, up: false));
            inputs.Add(UnicodeInput(c, up: true));
        }
        return Send(inputs, $"typed {text.Length} characters");
    }

    private static SpineOutcome Click(int x, int y, string button, int count)
    {
        if (!SetCursorPos(x, y)) return SpineOutcome.Fail($"could not move the pointer to {x},{y}");
        (uint down, uint up) = button switch
        {
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u),
        };
        var inputs = new List<INPUT>();
        for (int i = 0; i < count; i++) { inputs.Add(MouseInput(down)); inputs.Add(MouseInput(up)); }
        return Send(inputs, $"{button} click x{count} at {x},{y}");
    }

    private static SpineOutcome Open(string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return SpineOutcome.Success($"opened {target}");
        }
        catch (Exception ex)
        {
            return SpineOutcome.Fail($"could not open {target}: {ex.Message}");
        }
    }

    private static SpineOutcome Send(List<INPUT> inputs, string done)
    {
        if (inputs.Count == 0) return SpineOutcome.Success(done);
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        // SendInput refuses input to a window of higher integrity (an elevated app) without saying which; say it plainly.
        return sent == inputs.Count ? SpineOutcome.Success(done)
            : SpineOutcome.Fail($"Windows accepted {sent} of {inputs.Count} input events (an elevated window may be in front)");
    }

    // ------------------------------------------------------------------------------------------------------ shell

    public async Task<SpineOutcome> ShellAsync(KitAction action)
    {
        var psi = action.Shell == "bash"
            ? new ProcessStartInfo("bash") { ArgumentList = { "-lc", action.Command! } }
            : new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", action.Command! } };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (action.Cwd != null) psi.WorkingDirectory = action.Cwd;
        return await RunProcessAsync(psi, action.TimeoutMs);
    }

    /// <summary>
    /// Runs a process and keeps both streams. A non-zero exit is a normal way for a command to report a result (an
    /// audit that found something exits 1 with its findings on stdout), so a failure carries stdout and stderr too.
    /// </summary>
    internal static async Task<SpineOutcome> RunProcessAsync(ProcessStartInfo psi, int timeoutMs)
    {
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start"); }
        catch (Exception ex) { return SpineOutcome.Fail($"could not start {psi.FileName}: {ex.Message}"); }
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return SpineOutcome.Fail($"timed out after {timeoutMs} ms");
            }
            string output = JoinOutput(await stdout, await stderr);
            return p.ExitCode == 0 ? SpineOutcome.Success(output)
                : new SpineOutcome(false, null, output.Length > 0 ? output : $"exited with code {p.ExitCode}");
        }
    }

    internal static string JoinOutput(string stdout, string stderr) =>
        string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));

    // ------------------------------------------------------------------------------------------------------ agent

    /// <summary>
    /// A read-only prompt goes to Holodex's model routes (Ark API, Hivemind, then a Nebius key). A code or terminal
    /// prompt needs a runtime with tools, which is Claude Code; the spine gates those like a shell action.
    /// </summary>
    public async Task<SpineOutcome> AgentAsync(string prompt, IReadOnlyList<string> capabilities)
    {
        if (!NeedsHands(capabilities))
        {
            var answer = await ark.AskAsync(prompt);
            return answer.Ok ? SpineOutcome.Success(answer.Output) : SpineOutcome.Fail(answer.Output);
        }
        var psi = new ProcessStartInfo("claude") { ArgumentList = { "-p", prompt } };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var outcome = await RunProcessAsync(psi, 600_000);
        return outcome.Error?.StartsWith("could not start", StringComparison.Ordinal) == true
            ? SpineOutcome.Fail("A code or terminal agent action needs the claude CLI on PATH, and this PC does not have it.")
            : outcome;
    }

    public static bool NeedsHands(IReadOnlyList<string> capabilities) =>
        capabilities.Contains("code") || capabilities.Contains("terminal");

    // ----------------------------------------------------------------------------------------------------- cursor

    /// <summary>Where cursor profiles live: Ark's runtimes folder on an Ark PC, else the user's local Ark folder.</summary>
    public static string CursorDir
    {
        get
        {
            string ark = Path.Combine(Path.GetDirectoryName(ArkStationClient.DefaultInputStudioStateDir)!, "cursors");
            return Directory.Exists(ark) ? ark
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ark", "cursors");
        }
    }

    public Task<SpineOutcome> CursorAsync(string profile)
    {
        if (profile.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || profile.Contains(".."))
            return Task.FromResult(SpineOutcome.Fail($"\"{profile}\" is not a cursor profile name"));
        string dir = Path.Combine(CursorDir, profile);
        return Task.FromResult(Directory.Exists(dir)
            ? SpineOutcome.Success($"cursor profile {profile}")
            : SpineOutcome.Fail($"no cursor profile at {dir}"));
    }

    // ---------------------------------------------------------------------------------------------------- interop

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 0x0002u : 0) | (IsExtended(vk) ? 0x0001u : 0) } },
    };

    private static INPUT UnicodeInput(char c, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004u | (up ? 0x0002u : 0) } },
    };

    private static INPUT MouseInput(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };

    // Arrows, Insert/Delete, Home/End and Page keys sit in the extended block; without the flag some apps read them as the numpad.
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
}
