using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// On-brand ArkStation cursor icon styles rendered at VSync in the overlay window.
/// </summary>
public enum CursorIconStyle
{
    RelicWand,
    PhosphorArrow,
    ArkAperture,
    ViewfinderReticle
}

/// <summary>
/// One physical mouse, from its Raw Input device path (VID and PID) and the KnownMice table, or its HID product
/// string when the table does not know it. <see cref="Key"/> keys its saved profile (speed and button bindings).
/// </summary>
public sealed record MouseHardwareReport(
    string Brand,
    string Model,
    string VidPid,
    string PackageId,
    int WindowsPointerSpeed,
    bool CanSetDpi,
    bool HasSideButtons,
    bool HasGestureButton,
    IReadOnlyList<string> RawDeviceIds)
{
    public string Key => VidPid;
    public string Name => Brand.Length > 0 && !Model.StartsWith(Brand, StringComparison.OrdinalIgnoreCase) ? $"{Brand} {Model}" : Model;
}

/// <summary>
/// Configurable mouse button or directional gesture macro binding managed by Wiz.
/// </summary>
public sealed class MouseMacroBinding
{
    public string TriggerId { get; set; } = "";
    public string TriggerLabel { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public string Parameter { get; set; } = "";
}

/// <summary>
/// Intelligent mouse brand/model detection, live Windows pointer speed / DPI control,
/// on-brand Relic Wand cursor vector paths, and gesture flick + macro binding engine.
/// </summary>
public static class MouseHardwareAndWandEngine
{
    private const uint SPI_GETMOUSESPEED = 0x0070;
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    public static CursorIconStyle ActiveCursorStyle { get; set; } = CursorIconStyle.ArkAperture;

    private static readonly (string Pattern, string Brand, string Model, string PackageId, int DefaultDpi, bool GestureBtn)[] KnownMice_ =
    {
        ("VID_046D&PID_B042", "Logitech", "MX Master 4", "mx-master-4", 4000, true),
        ("VID_046D&PID_B034", "Logitech", "MX Master 3S", "mx-master-4", 8000, true),
        ("VID_046D&PID_B023", "Logitech", "MX Master 3", "mx-master-4", 4000, true),
        ("VID_046D&PID_4082", "Logitech", "MX Master 3 (Unifying)", "mx-master-4", 4000, true),
        ("VID_046D&PID_C547", "Logitech", "G PRO X Superlight 2 (Lightspeed)", "logitech-g-pro", 3200, false),
        ("VID_046D&PID_C539", "Logitech", "G PRO / G502 Lightspeed", "logitech-g-pro", 3200, false),
        ("VID_046D&PID_C094", "Logitech", "G PRO X Superlight", "logitech-g-pro", 3200, false),
        ("VID_046D&PID_C08B", "Logitech", "G502 HERO", "logitech-g-pro", 2400, true),
        ("VID_046D",          "Logitech", "HID Precision Mouse", "logitech-universal", 1600, true),
        ("VID_1532&PID_00B6", "Razer",    "DeathAdder V3 Pro", "razer-hyperspeed", 3200, false),
        ("VID_1532&PID_00A5", "Razer",    "Viper V2 Pro", "razer-hyperspeed", 3200, false),
        ("VID_1532&PID_00B9", "Razer",    "Basilisk V3 Pro", "razer-hyperspeed", 3200, true),
        ("VID_1532&PID_0099", "Razer",    "Basilisk V3", "razer-hyperspeed", 2400, true),
        ("VID_1532",          "Razer",    "Chroma / HyperSpeed Gaming Mouse", "razer-hyperspeed", 2400, false),
        ("VID_1B1C",          "Corsair",  "Slipstream / Dark Core Mouse", "corsair-icue", 2000, true),
        ("VID_1038",          "SteelSeries", "Aerox / Rival Gaming Mouse", "steelseries-gg", 1800, false),
        ("VID_258A",          "Glorious", "Model O / Model D Wireless", "glorious-core", 1600, false),
        ("VID_320F",          "Glorious", "Model I / Forge Series", "glorious-core", 2400, true),
        ("VID_361D",          "Finalmouse", "UltralightX (ULX)", "finalmouse-ulx", 3200, false),
        ("VID_3554",          "Pulsar",   "X2 / Xlite Wireless", "pulsar-fusion", 2400, false),
        ("VID_0B05",          "ASUS ROG", "Harpe Ace / Chakram", "asus-rog", 2400, true),
        ("VID_045E",          "Microsoft", "Precision Bluetooth / Intellimouse", "microsoft-precision", 1200, false),
        ("VID_05AC",          "Apple",    "Magic Mouse / Trackpad Bridge", "universal-mouse", 1200, true)
    };

    // ── The active mouse ─────────────────────────────────────────────────────────────────────────────────────────
    // Every mouse Windows lists is known; the one that last moved in a real burst is active (ActiveMouseTracker).
    // Its saved speed is applied and its buttons are bound when it becomes active, and ActiveMouseChanged tells the
    // tray, Holodex and Wiz. AutoSwitch off pins the mouse the user picked.

    private static readonly object MouseGate = new();
    private static List<MouseHardwareReport>? _knownMice;
    private static MouseHardwareReport? _activeMouse;

    /// <summary>Where ActiveMouseChanged is raised (the UI thread); set by the app at startup.</summary>
    public static System.Threading.SynchronizationContext? UiContext { get; set; }

    /// <summary>Raised on <see cref="UiContext"/> when another mouse becomes the active one.</summary>
    public static event Action<MouseHardwareReport>? ActiveMouseChanged;

    /// <summary>True (the default): the mouse that moves becomes the active one. False pins the one picked last.</summary>
    public static bool AutoSwitch { get; set; } = true;

    /// <summary>
    /// The mice to offer: known mouse models, plus any device that has moved the pointer since Omniwand started.
    /// Keyboards and keypads with a mouse channel (a Razer Huntsman or Tartarus) stay out until they move it.
    /// </summary>
    public static IReadOnlyList<MouseHardwareReport> KnownMice
    {
        get
        {
            var moved = ActiveMouseTracker.SinceLastInputMs().Keys.Select(KeyFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (MouseGate)
                return (_knownMice ??= ScanMice())
                    .Where(m => IsListedModel(m.Key) || moved.Contains(m.Key) || m.Key == _activeMouse?.Key)
                    .ToList();
        }
    }

    private static bool IsListedModel(string key) =>
        KnownMice_.Any(e => e.Pattern.Contains("PID_", StringComparison.OrdinalIgnoreCase) && key.Equals(e.Pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>The mouse in use: the last one to move, or until something moves, the first known model listed.</summary>
    public static MouseHardwareReport ActiveMouse
    {
        get
        {
            lock (MouseGate)
            {
                _knownMice ??= ScanMice();
                return _activeMouse ??= _knownMice.FirstOrDefault(m => IsListedModel(m.Key)) ?? _knownMice.FirstOrDefault() ?? Unknown();
            }
        }
    }

    /// <summary>Kept for callers that ask once: the active mouse now.</summary>
    public static MouseHardwareReport DetectConnectedMouse() => ActiveMouse;

    /// <summary>Starts following which mouse moves. Call once, after <see cref="UiContext"/> is set.</summary>
    public static void StartTracking()
    {
        ActiveMouseTracker.MouseStartedMoving += OnMouseStartedMoving;
        ActiveMouseTracker.Start();
    }

    /// <summary>Makes <paramref name="key"/> the active mouse by hand and pins it (AutoSwitch off).</summary>
    public static void UseMouse(string key)
    {
        AutoSwitch = false;
        var mouse = KnownMice.FirstOrDefault(m => m.Key == key);
        if (mouse != null) Activate(mouse);
    }

    private static void OnMouseStartedMoving(string deviceName)
    {
        if (!AutoSwitch) return;
        string key = KeyFor(deviceName);
        if (key == ActiveMouse.Key) return;
        MouseHardwareReport mouse;
        lock (MouseGate)
        {
            _knownMice ??= ScanMice();
            mouse = _knownMice.FirstOrDefault(m => m.Key == key) ?? Identify(deviceName);
            if (!_knownMice.Any(m => m.Key == mouse.Key)) _knownMice.Add(mouse);
        }
        Activate(mouse);
    }

    private static void Activate(MouseHardwareReport mouse)
    {
        lock (MouseGate)
        {
            if (_activeMouse?.Key == mouse.Key) return;
            _activeMouse = mouse;
        }
        // The mouse's own speed, if it has one saved; a mouse seen for the first time keeps the current speed.
        if (Profiles.TryGetValue(mouse.Key, out var profile) && profile.Speed is int speed) ApplySpeed(speed, save: false);
        if (UiContext != null) UiContext.Post(_ => ActiveMouseChanged?.Invoke(mouse), null);
        else ActiveMouseChanged?.Invoke(mouse);
    }

    private static List<MouseHardwareReport> ScanMice()
    {
        var mice = new List<MouseHardwareReport>();
        foreach (string name in ActiveMouseTracker.ListMice())
        {
            if (ActiveMouseTracker.VidPid(name).Length == 0) continue; // virtual pointers (RDP, streaming, root devices)
            var mouse = Identify(name);
            if (mice.Any(m => m.Key == mouse.Key)) continue;
            // Two mice with the same name get a number, so the menu can tell them apart.
            int same = mice.Count(m => m.Name == mouse.Name);
            if (same > 0) mouse = mouse with { Model = $"{mouse.Model} ({same + 1})" };
            mice.Add(mouse);
        }
        return mice;
    }

    private static string KeyFor(string deviceName)
    {
        string vidPid = ActiveMouseTracker.VidPid(deviceName);
        return vidPid.Length > 0 ? vidPid : deviceName;
    }

    /// <summary>A mouse from its device path: the KnownMice entry for its VID and PID, else its HID product string.</summary>
    public static MouseHardwareReport Identify(string deviceName)
    {
        string key = KeyFor(deviceName);
        int speed = GetWindowsMouseSpeed();
        foreach (var entry in KnownMice_)
        {
            bool match = entry.Pattern.Contains("PID_", StringComparison.OrdinalIgnoreCase)
                ? key.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                : key.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            // A maker-only entry (no PID) is a guess at the family; the device's own product string names it better.
            string model = entry.Model;
            if (!entry.Pattern.Contains("PID_", StringComparison.OrdinalIgnoreCase))
            {
                string own = ActiveMouseTracker.ProductName(deviceName);
                model = own.Length > 0 ? own : "mouse";
            }
            return new MouseHardwareReport(entry.Brand, model, key, entry.PackageId, speed,
                CanSetDpi: false, HasSideButtons: true, HasGestureButton: entry.GestureBtn, new[] { deviceName });
        }
        string product = ActiveMouseTracker.ProductName(deviceName);
        return new MouseHardwareReport("", product.Length > 0 ? product : "Mouse", key, "universal-mouse", speed,
            CanSetDpi: false, HasSideButtons: true, HasGestureButton: false, new[] { deviceName });
    }

    private static MouseHardwareReport Unknown() =>
        new("", "Mouse", "HID-STANDARD", "universal-mouse", GetWindowsMouseSpeed(), false, true, false, Array.Empty<string>());

    // ── Speed ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Omniwand sets the Windows pointer speed (1 to 20), the sensitivity every mouse has. A mouse's real DPI is set
    // by its maker's app (G HUB, Options+, Synapse), so no mouse reports CanSetDpi yet and the setting is speed.

    public const int MinSpeed = 1;
    public const int MaxSpeed = 20;

    /// <summary>Raised with the new speed (1 to 20) whenever Omniwand changes it.</summary>
    public static event Action<int>? SpeedChanged;

    /// <summary>Gets the live Windows pointer speed (1..20, default 10).</summary>
    public static int GetWindowsMouseSpeed()
    {
        int speed = 10;
        try
        {
            if (SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref speed, 0))
            {
                return Math.Clamp(speed, MinSpeed, MaxSpeed);
            }
        }
        catch { }
        return 10;
    }

    private static int _originalSpeed = -1;

    /// <summary>
    /// Remembers the user's pointer speed before Omniwand first changes it, in memory and in a crash marker
    /// (<see cref="CursorCrashGuard.OriginalMouseSpeed"/>), so a killed run still gets it back at the next start.
    /// </summary>
    private static void RememberOriginalSpeed()
    {
        if (_originalSpeed >= 0) return;
        _originalSpeed = GetWindowsMouseSpeed();
        CursorCrashGuard.OriginalMouseSpeed.Set(_originalSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Puts the Windows pointer speed back to what it was before Omniwand touched it. Safe to call twice.</summary>
    public static void RestoreOriginalSpeed()
    {
        if (_originalSpeed < 1) return;
        WriteWindowsSpeed(_originalSpeed);
        CursorCrashGuard.OriginalMouseSpeed.Clear();
    }

    /// <summary>Sets the Windows pointer speed (1..20) and nothing else: no profile save, no event, no marker.</summary>
    internal static void WriteWindowsSpeed(int speed1To20)
    {
        try { SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(Math.Clamp(speed1To20, MinSpeed, MaxSpeed)), SPIF_SENDCHANGE); } catch { }
    }

    /// <summary>Sets the speed (1..20) now and saves it as the active mouse's speed.</summary>
    public static bool SetWindowsMouseSpeed(int speed1To20) => ApplySpeed(speed1To20, save: true);

    private static bool ApplySpeed(int speed1To20, bool save)
    {
        RememberOriginalSpeed();
        int clamped = Math.Clamp(speed1To20, MinSpeed, MaxSpeed);
        bool ok;
        try { ok = SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(clamped), SPIF_SENDCHANGE); }
        catch { ok = false; }
        if (save) UpdateProfile(ActiveMouse.Key, p => p.Speed = clamped);
        SpeedChanged?.Invoke(clamped);
        return ok;
    }

    // ── Per-mouse profiles ───────────────────────────────────────────────────────────────────────────────────────

    private sealed class MouseProfile
    {
        public int? Speed { get; set; }
        public List<MouseMacroBinding>? Bindings { get; set; }
    }

    public static string ProfilesPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "mouse-profiles.json");

    private static Dictionary<string, MouseProfile>? _profiles;

    private static Dictionary<string, MouseProfile> Profiles
    {
        get
        {
            lock (MouseGate)
            {
                if (_profiles != null) return _profiles;
                try
                {
                    if (File.Exists(ProfilesPath))
                        _profiles = JsonSerializer.Deserialize<Dictionary<string, MouseProfile>>(File.ReadAllText(ProfilesPath));
                }
                catch { }
                return _profiles ??= new Dictionary<string, MouseProfile>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void UpdateProfile(string key, Action<MouseProfile> change)
    {
        lock (MouseGate)
        {
            var all = Profiles;
            if (!all.TryGetValue(key, out var profile)) all[key] = profile = new MouseProfile();
            change(profile);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
                File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>The single bindings file from before per-mouse profiles; read once to seed a mouse's bindings.</summary>
    private static string LegacyMacroConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "mouse-macros.json");

    /// <summary>The bindings of <paramref name="hw"/> (the active mouse when null): saved ones, else its defaults.</summary>
    public static List<MouseMacroBinding> LoadOrCreateMacroBindings(MouseHardwareReport? hw = null)
    {
        var mouse = hw ?? ActiveMouse;
        if (Profiles.TryGetValue(mouse.Key, out var profile) && profile.Bindings is { Count: > 0 } saved) return PlainLabels(saved, mouse);

        List<MouseMacroBinding>? seed = null;
        try
        {
            if (File.Exists(LegacyMacroConfigPath))
                seed = JsonSerializer.Deserialize<List<MouseMacroBinding>>(File.ReadAllText(LegacyMacroConfigPath));
        }
        catch { }
        var bindings = seed is { Count: > 0 } && !Profiles.Values.Any(p => p.Bindings != null) ? PlainLabels(seed, mouse) : GetDefaultMacroBindings(mouse);
        UpdateProfile(mouse.Key, p => p.Bindings = bindings);
        return bindings;
    }

    /// <summary>
    /// Keeps each binding's button and action but words it the plain way the defaults do, so bindings saved before
    /// the plain labels (or seeded from the old single file) read the same as new ones.
    /// </summary>
    private static List<MouseMacroBinding> PlainLabels(List<MouseMacroBinding> bindings, MouseHardwareReport mouse)
    {
        var plain = GetDefaultMacroBindings(mouse with { HasSideButtons = true });
        foreach (var b in bindings)
        {
            if (plain.FirstOrDefault(d => d.TriggerId == b.TriggerId) is { } trigger) b.TriggerLabel = trigger.TriggerLabel;
            if (plain.FirstOrDefault(d => d.ActionId == b.ActionId) is { } action) b.ActionLabel = action.ActionLabel;
        }
        return bindings;
    }

    /// <summary>
    /// The starting buttons for <paramref name="hw"/>, in plain words: the two thumb buttons when the mouse has
    /// them, and the four right-click flicks every mouse can do.
    /// </summary>
    public static List<MouseMacroBinding> GetDefaultMacroBindings(MouseHardwareReport? hw = null)
    {
        var bindings = new List<MouseMacroBinding>();
        if (hw?.HasSideButtons ?? true)
        {
            bindings.Add(new() { TriggerId = "Mouse4", TriggerLabel = "Back thumb button", ActionId = "snip:lasso", ActionLabel = "Cut out part of the screen", Parameter = "Ctrl+Alt+S" });
            bindings.Add(new() { TriggerId = "Mouse5", TriggerLabel = "Front thumb button", ActionId = "holodex:toggle", ActionLabel = "Open or close Holodex", Parameter = "Ctrl+Alt+H" });
        }
        bindings.Add(new() { TriggerId = "GestureNorth", TriggerLabel = "Hold right-click, flick up", ActionId = "element:inspect", ActionLabel = "Point at something to learn about it", Parameter = "Ctrl+Alt+E" });
        bindings.Add(new() { TriggerId = "GestureEast", TriggerLabel = "Hold right-click, flick right", ActionId = "wiz:summon", ActionLabel = "Open Wiz to make a spell", Parameter = "Ctrl+Alt+W" });
        bindings.Add(new() { TriggerId = "GestureSouth", TriggerLabel = "Hold right-click, flick down", ActionId = "hud:summon", ActionLabel = "Open the ask box", Parameter = "Ctrl+Alt+Space" });
        bindings.Add(new() { TriggerId = "GestureWest", TriggerLabel = "Hold right-click, flick left", ActionId = "ainbox:summon", ActionLabel = "Open AInbox to send something", Parameter = "Ctrl+Alt+I" });
        return bindings;
    }

    /// <summary>Saves <paramref name="bindings"/> as the active mouse's buttons.</summary>
    public static void SaveMacroBindings(IEnumerable<MouseMacroBinding> bindings)
    {
        var list = bindings.ToList();
        UpdateProfile(ActiveMouse.Key, p => p.Bindings = list);
    }

    /// <summary>
    /// Draws the selected on-brand ArkStation cursor icon at the current cursor tip (cx, cy).
    /// Supports:
    /// - RelicWand: Tapered wand shaft with Patina/Coral tip spark, ferrule rings, and gesture aura.
    /// - ArkAperture: The signature Ark Aperture octagon/lens pointer.
    /// - ViewfinderReticle: Precision CAD/vision crosshair + corner brackets.
    /// - PhosphorArrow: Handled by default smooth arrow path.
    /// </summary>
    public static void DrawStyledCursor(
        Graphics g,
        CursorIconStyle style,
        float cx,
        float cy,
        float angleDeg,
        float scale,
        bool isAiThinking,
        WindowsCursorRole role,
        float spinAngleDeg)
    {
        var state = g.Save();
        g.TranslateTransform(cx, cy);

        Color primaryStroke = isAiThinking
            ? PhosphorTheme.Patina
            : (role == WindowsCursorRole.Hand ? PhosphorTheme.Coral : PhosphorTheme.Bone);
        Color accentGlow = isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina;

        if (style == CursorIconStyle.RelicWand)
        {
            g.RotateTransform(angleDeg);
            g.ScaleTransform(scale, scale);

            // Wand tip sits at (0, 0); tapered relic shaft extends down along +Y
            using var shadowPen = new Pen(Color.FromArgb(145, 0, 0, 0), 7.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, 0f, 2f, 0f, 36f);

            // Outer Bone/Patina casing
            using var rimPen = new Pen(primaryStroke, 5.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(rimPen, 0f, 2.5f, 0f, 35f);

            // Inner Ground core (#0B0B0B)
            using var corePen = new Pen(PhosphorTheme.Ground, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(corePen, 0f, 3.5f, 0f, 34f);

            // Relic ferrule bands along the wand shaft
            using var ferrulePen = new Pen(PhosphorTheme.Seal, 1.8f);
            g.DrawLine(ferrulePen, -2.4f, 12f, 2.4f, 12f);
            g.DrawLine(ferrulePen, -2.4f, 24f, 2.4f, 24f);

            // Glowing Wand Tip Orb + 4-point star spark at hotspot (0, 0)
            float pulse = 1.0f + 0.18f * (float)Math.Sin(spinAngleDeg * Math.PI / 90.0);
            float orbR = 4.2f * pulse;
            using (var auraBrush = new SolidBrush(Color.FromArgb(75, accentGlow)))
            {
                g.FillEllipse(auraBrush, -orbR * 1.8f, -orbR * 1.8f, orbR * 3.6f, orbR * 3.6f);
            }
            using (var tipFill = new SolidBrush(PhosphorTheme.Bone))
            using (var tipRim = new Pen(accentGlow, 1.6f))
            {
                g.FillEllipse(tipFill, -2.6f, -2.6f, 5.2f, 5.2f);
                g.DrawEllipse(tipRim, -3.4f, -3.4f, 6.8f, 6.8f);
            }

            // Subtle 4-ray spark around tip when Hand or AI thinking
            if (isAiThinking || role == WindowsCursorRole.Hand)
            {
                using var sparkPen = new Pen(PhosphorTheme.Coral, 1.4f);
                float r = 7.5f * pulse;
                g.DrawLine(sparkPen, -r, 0f, -3.8f, 0f);
                g.DrawLine(sparkPen, 3.8f, 0f, r, 0f);
                g.DrawLine(sparkPen, 0f, -r, 0f, -3.8f);
            }
        }
        else if (style == CursorIconStyle.ArkAperture)
        {
            g.Restore(state);
            ArkApertureMorphEngine.DrawMorphedApertureCursor(
                g,
                cx,
                cy,
                angleDeg,
                scale,
                isAiThinking,
                role,
                spinAngleDeg);
            return;
        }
        else if (style == CursorIconStyle.ViewfinderReticle)
        {
            g.ScaleTransform(scale, scale);
            float r = 10f;
            float b = 13f;
            float corner = 4.5f;
            using var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 3.4f);
            using var pen = new Pen(accentGlow, 1.8f);
            using var bonePen = new Pen(primaryStroke, 1.6f);

            g.DrawEllipse(shadow, -r, -r, r * 2, r * 2);
            g.DrawEllipse(pen, -r, -r, r * 2, r * 2);
            g.FillEllipse(Brushes.White, -1.5f, -1.5f, 3f, 3f);

            // Corner viewfinder brackets
            g.DrawLines(bonePen, new[] { new PointF(-b, -b + corner), new PointF(-b, -b), new PointF(-b + corner, -b) });
            g.DrawLines(bonePen, new[] { new PointF(b - corner, -b), new PointF(b, -b), new PointF(b, -b + corner) });
            g.DrawLines(bonePen, new[] { new PointF(-b, b - corner), new PointF(-b, b), new PointF(-b + corner, b) });
            g.DrawLines(bonePen, new[] { new PointF(b - corner, b), new PointF(b, b), new PointF(b, b - corner) });
        }

        g.Restore(state);
    }
}

/// <summary>
/// The mouse speed slider (Windows pointer speed 1 to 20), marked Slow, Normal and Fast. Dragging or the wheel sets
/// the speed now and saves it as the active mouse's speed; it follows changes made anywhere else.
/// </summary>
public sealed class PhosphorSpeedSlider : Control
{
    private int _speed;
    private bool _dragging;
    private bool _hovered;

    public event Action<int>? ValueChanged;

    public int Value
    {
        get => _speed;
        set
        {
            int clamped = Math.Clamp(value, MouseHardwareAndWandEngine.MinSpeed, MouseHardwareAndWandEngine.MaxSpeed);
            if (_speed == clamped) return;
            _speed = clamped;
            Invalidate();
        }
    }

    public PhosphorSpeedSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Height = 46;
        Cursor = Cursors.Hand;
        _speed = MouseHardwareAndWandEngine.GetWindowsMouseSpeed();
        MouseHardwareAndWandEngine.SpeedChanged += speed =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(new Action(() => Value = speed));
            else Value = speed;
        };
    }

    private float S => PhosphorPaint.Scale(this);

    private RectangleF TrackRect
    {
        get
        {
            float s = S;
            return new RectangleF(14 * s, 14 * s, Math.Max(40 * s, Width - 28 * s), 8 * s);
        }
    }

    private void Set(int speed)
    {
        int clamped = Math.Clamp(speed, MouseHardwareAndWandEngine.MinSpeed, MouseHardwareAndWandEngine.MaxSpeed);
        if (clamped == _speed) return;
        MouseHardwareAndWandEngine.SetWindowsMouseSpeed(clamped);
        _speed = clamped;
        ValueChanged?.Invoke(_speed);
        Invalidate();
    }

    private void SetFromX(float x)
    {
        var tr = TrackRect;
        float ratio = Math.Clamp((x - tr.Left) / tr.Width, 0f, 1f);
        Set(MouseHardwareAndWandEngine.MinSpeed + (int)Math.Round(ratio * (MouseHardwareAndWandEngine.MaxSpeed - MouseHardwareAndWandEngine.MinSpeed)));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        SetFromX(e.X);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Set(_speed + (e.Delta > 0 ? 1 : -1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintSlider(e.Graphics);
    }

    public void PaintSlider(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float s = S;
        var tr = TrackRect;
        float span = MouseHardwareAndWandEngine.MaxSpeed - MouseHardwareAndWandEngine.MinSpeed;
        float thumbX = tr.Left + (_speed - MouseHardwareAndWandEngine.MinSpeed) / span * tr.Width;
        float thumbY = tr.Top + tr.Height / 2f;

        using (var trackBg = new SolidBrush(PhosphorTheme.Ground)) g.FillRectangle(trackBg, tr);
        using (var trackBorder = new Pen(PhosphorTheme.Line2, 1f)) g.DrawRectangle(trackBorder, tr.X, tr.Y, tr.Width, tr.Height);
        Color active = _dragging ? PhosphorTheme.Coral : PhosphorTheme.Patina;
        using (var fill = new SolidBrush(Color.FromArgb(210, active))) g.FillRectangle(fill, tr.Left + 1, tr.Top + 1, Math.Max(2f, thumbX - tr.Left), tr.Height - 2);

        // Three words a child can read, at slow, normal and fast.
        using var font = PhosphorFonts.Mono(9f);
        using var dim = new SolidBrush(PhosphorTheme.Dim);
        using var bone = new SolidBrush(PhosphorTheme.Bone);
        using var tick = new Pen(PhosphorTheme.Line2, 1f);
        foreach (var (speed, label) in new[] { (MouseHardwareAndWandEngine.MinSpeed, "SLOW"), (10, "NORMAL"), (MouseHardwareAndWandEngine.MaxSpeed, "FAST") })
        {
            float tx = tr.Left + (speed - MouseHardwareAndWandEngine.MinSpeed) / span * tr.Width;
            g.DrawLine(tick, tx, tr.Bottom + 2 * s, tx, tr.Bottom + 5 * s);
            var size = g.MeasureString(label, font);
            float lx = Math.Clamp(tx - size.Width / 2f, 2f, Width - size.Width - 2f);
            g.DrawString(label, font, Math.Abs(speed - _speed) <= 1 ? bone : dim, lx, tr.Bottom + 6 * s);
        }

        if (_hovered || _dragging)
            using (var halo = new SolidBrush(Color.FromArgb(65, active))) g.FillEllipse(halo, thumbX - 11 * s, thumbY - 11 * s, 22 * s, 22 * s);
        using (var thumbFill = new SolidBrush(PhosphorTheme.Bone)) g.FillEllipse(thumbFill, thumbX - 7 * s, thumbY - 7 * s, 14 * s, 14 * s);
        using (var outline = new Pen(PhosphorTheme.Ground, 1.6f)) g.DrawEllipse(outline, thumbX - 7 * s, thumbY - 7 * s, 14 * s, 14 * s);
        using (var core = new SolidBrush(active)) g.FillEllipse(core, thumbX - 3.5f * s, thumbY - 3.5f * s, 7f * s, 7f * s);
    }
}
