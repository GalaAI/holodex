using System.Runtime.InteropServices;
using System.Text;

namespace Omniwand;

/// <summary>One global shortcut: its RegisterHotKey id, modifiers and key, and what it does in plain words.</summary>
internal sealed record HotkeySpec(int Id, uint Mods, Keys Key, string What)
{
    public const uint Alt = 0x0001, Ctrl = 0x0002, Shift = 0x0004, NoRepeat = 0x4000;

    public bool CtrlAlt => (Mods & (Ctrl | Alt)) == (Ctrl | Alt);

    /// <summary>"Ctrl+Alt+H", as the tray, toasts and tips print it.</summary>
    public string Label =>
        ((Mods & Ctrl) != 0 ? "Ctrl+" : "") + ((Mods & Shift) != 0 ? "Shift+" : "") + ((Mods & Alt) != 0 ? "Alt+" : "") + KeyName(Key);

    private static string KeyName(Keys key) => key switch
    {
        Keys.Space => "Space",
        _ => key.ToString()
    };
}

/// <summary>
/// The one table of Holodex's global shortcuts, and which of them this PC actually registered. Every surface that
/// prints a shortcut asks <see cref="Shown"/>, so a shortcut that is skipped or taken is never advertised.
/// <para>
/// Quit is not a global shortcut (tray only): on an AltGr keyboard Ctrl+Alt+Q is AltGr+Q, which types @ on a German
/// layout, and a judge typing an email address would quit Holodex. Every remaining Ctrl+Alt combination is checked
/// against each keyboard layout on this PC and skipped (and logged) where it types a character.
/// </para>
/// </summary>
internal static class GlobalHotkeys
{
    private const uint CtrlAlt = HotkeySpec.Ctrl | HotkeySpec.Alt | HotkeySpec.NoRepeat;

    public static readonly HotkeySpec Ask = new(9000, CtrlAlt, Keys.Space, "Ask a question");
    public static readonly HotkeySpec ToggleCursor = new(9001, CtrlAlt, Keys.C, "Show or hide the normal pointer");
    // 9002 was Ctrl+Alt+Q (Quit). Retired: quit from the tray.
    public static readonly HotkeySpec Wiz = new(9003, CtrlAlt, Keys.W, "Make a spell with Wiz");
    public static readonly HotkeySpec AInbox = new(9004, CtrlAlt, Keys.I, "Send to AInbox");
    public static readonly HotkeySpec Orb = new(9005, HotkeySpec.Ctrl | HotkeySpec.Shift | HotkeySpec.NoRepeat, Keys.Space, "Spell Orb");
    public static readonly HotkeySpec CycleProfile = new(9007, CtrlAlt, Keys.P, "Next Input Studio profile");
    public static readonly HotkeySpec Holodex = new(9008, CtrlAlt, Keys.H, "Open Holodex");
    public static readonly HotkeySpec Lasso = new(9009, CtrlAlt, Keys.S, "Cut out a picture");
    public static readonly HotkeySpec ElementPicker = new(9010, CtrlAlt, Keys.E, "Pick a thing or a color");
    public static readonly HotkeySpec Tutorial = new(9011, CtrlAlt, Keys.T, "Show me around");

    public static readonly IReadOnlyList<HotkeySpec> All = new[]
    {
        Ask, ToggleCursor, Wiz, AInbox, Orb, CycleProfile, Holodex, Lasso, ElementPicker, Tutorial
    };

    /// <summary>The lowest and highest ids, for unregistering.</summary>
    public const int FirstId = 9000, LastId = 9011;

    private static readonly HashSet<int> Registered = new();

    public static void MarkRegistered(HotkeySpec spec)
    {
        lock (Registered) Registered.Add(spec.Id);
    }

    public static bool IsRegistered(HotkeySpec spec)
    {
        lock (Registered) return Registered.Contains(spec.Id);
    }

    /// <summary>The shortcut as a surface should print it: its label when this PC registered it, else "".</summary>
    public static string Shown(HotkeySpec spec) => IsRegistered(spec) ? spec.Label : "";

    /// <summary>"Ctrl+Alt+H Holodex" style list of the registered shortcuts, for the tray tooltip and the welcome toast.</summary>
    public static string Summary(params (HotkeySpec Spec, string Name)[] items) =>
        string.Join("   ", items.Where(i => IsRegistered(i.Spec)).Select(i => $"{i.Spec.Label} {i.Name}"));

    /// <summary>
    /// The character a Ctrl+Alt combination types on some keyboard layout of this PC (AltGr is Ctrl+Alt to Windows),
    /// with the layout it types on, or null when every layout leaves it free.
    /// </summary>
    public static (string Layout, string Typed)? AltGrCollision(HotkeySpec spec)
    {
        if (!spec.CtrlAlt) return null;
        foreach (IntPtr hkl in InstalledLayouts())
            if (CtrlAltTypes((uint)spec.Key, hkl) is { } typed)
                return (LayoutName(hkl), typed);
        return null;
    }

    /// <summary>
    /// The decision, apart from Windows: a Ctrl+Alt shortcut collides when the layout types something printable for
    /// it (or starts a dead key). <paramref name="typed"/> is what ToUnicodeEx gave with Ctrl and Alt held.
    /// </summary>
    internal static bool Collides(int toUnicodeResult, string typed) =>
        toUnicodeResult < 0 || (toUnicodeResult > 0 && typed.Any(c => !char.IsControl(c) && !char.IsWhiteSpace(c)));

    /// <summary>What <paramref name="vk"/> types with Ctrl and Alt held on <paramref name="hkl"/>, or null when nothing.</summary>
    internal static string? CtrlAltTypes(uint vk, IntPtr hkl)
    {
        var state = new byte[256];
        state[VK_CONTROL] = state[VK_LCONTROL] = 0x80;
        state[VK_MENU] = state[VK_RMENU] = 0x80;
        var buffer = new StringBuilder(8);
        uint scan = MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, hkl);
        // Flag 4: leave the keyboard's dead-key state alone (Windows 10 1607 and later).
        int n = ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, hkl);
        string typed = n > 0 ? buffer.ToString(0, Math.Min(n, buffer.Length)) : "";
        return Collides(n, typed) ? (n < 0 ? "a dead key" : typed) : null;
    }

    private static IEnumerable<IntPtr> InstalledLayouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return new[] { GetKeyboardLayout(0) };
        var list = new IntPtr[count];
        GetKeyboardLayoutList(count, list);
        return list;
    }

    private static string LayoutName(IntPtr hkl)
    {
        int lang = (int)((long)hkl & 0xFFFF);
        try { return System.Globalization.CultureInfo.GetCultureInfo(lang).DisplayName; }
        catch (System.Globalization.CultureNotFoundException) { return $"layout {lang:X4}"; }
    }

    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LCONTROL = 0xA2, VK_RMENU = 0xA5;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
