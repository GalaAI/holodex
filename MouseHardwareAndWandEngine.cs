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
/// Detected physical mouse hardware metadata from Windows PnP / HID VID &amp; PID inspection.
/// </summary>
public sealed record MouseHardwareReport(
    string Brand,
    string Model,
    string VidPid,
    string PackageId,
    int WindowsPointerSpeed,
    int EstimatedDpi,
    bool HasSideButtons,
    bool HasGestureButton,
    IReadOnlyList<string> RawDeviceIds);

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

    private static readonly (string Pattern, string Brand, string Model, string PackageId, int DefaultDpi, bool GestureBtn)[] KnownMice =
    {
        ("VID_046D&PID_B042", "Logitech", "MX Master 4", "mx-master-4", 4000, true),
        ("2B042",             "Logitech", "MX Master 4", "mx-master-4", 4000, true),
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

    public static MouseHardwareReport DetectConnectedMouse()
    {
        var rawIds = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-PnpDevice -Class Mouse,HIDClass -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { $_.InstanceId + ' | ' + $_.FriendlyName }\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1800);
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        rawIds.Add(trimmed);
                }
            }
        }
        catch
        {
            // Fallback if WMI/PnP is unavailable
        }

        int speed = GetWindowsMouseSpeed();
        string combined = string.Join("\n", rawIds);

        foreach (var entry in KnownMice)
        {
            if (combined.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                string vidPid = ExtractVidPid(rawIds, entry.Pattern);
                int estDpi = (int)Math.Round(entry.DefaultDpi * (speed / 10.0));
                return new MouseHardwareReport(
                    entry.Brand,
                    entry.Model,
                    vidPid,
                    entry.PackageId,
                    speed,
                    estDpi,
                    HasSideButtons: true,
                    HasGestureButton: entry.GestureBtn,
                    rawIds.Take(8).ToList());
            }
        }

        string fallbackVidPid = ExtractVidPid(rawIds, "VID_");
        int genericDpi = (int)Math.Round(1600 * (speed / 10.0));
        return new MouseHardwareReport(
            "Universal HID",
            "5-Button Optical / Laser Mouse",
            string.IsNullOrEmpty(fallbackVidPid) ? "HID-STANDARD" : fallbackVidPid,
            "universal-mouse",
            speed,
            genericDpi,
            HasSideButtons: true,
            HasGestureButton: true,
            rawIds.Take(8).ToList());
    }

    private static string ExtractVidPid(IEnumerable<string> lines, string hint)
    {
        foreach (string line in lines)
        {
            if (!line.Contains(hint, StringComparison.OrdinalIgnoreCase)) continue;
            int idx = line.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int end = line.IndexOfAny(new[] { '\\', ' ', '|' }, idx);
                string token = end > idx ? line[idx..end] : line[idx..];
                if (token.Length <= 24) return token.ToUpperInvariant();
            }
        }
        foreach (string line in lines)
        {
            int idx = line.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int end = line.IndexOfAny(new[] { '\\', ' ', '|' }, idx);
                string token = end > idx ? line[idx..end] : line[idx..];
                if (token.Length <= 24) return token.ToUpperInvariant();
            }
        }
        return hint;
    }

    public const int MinDpi = 100;
    public const int MaxDpi = 4000;
    private static int _customDpi;

    public static event Action<int, int>? DpiChanged;

    /// <summary>Gets the live Windows pointer speed (1..20, default 10).</summary>
    public static int GetWindowsMouseSpeed()
    {
        int speed = 10;
        try
        {
            if (SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref speed, 0))
            {
                return Math.Clamp(speed, 1, 20);
            }
        }
        catch { }
        return 10;
    }

    public static int SpeedToDpi(int speed1To20)
    {
        int s = Math.Clamp(speed1To20, 1, 20);
        if (s <= 1) return MinDpi;
        double t = (s - 1) / 19.0;
        int raw = (int)Math.Round(MinDpi + t * (MaxDpi - MinDpi));
        return Math.Clamp((raw / 50) * 50, MinDpi, MaxDpi);
    }

    public static int DpiToSpeed(int dpi)
    {
        int clamped = Math.Clamp(dpi, MinDpi, MaxDpi);
        double t = (clamped - MinDpi) / (double)(MaxDpi - MinDpi);
        return Math.Clamp(1 + (int)Math.Round(t * 19.0), 1, 20);
    }

    public static int GetCurrentDpi()
    {
        if (_customDpi >= MinDpi && _customDpi <= MaxDpi)
            return _customDpi;
        return SpeedToDpi(GetWindowsMouseSpeed());
    }

    /// <summary>Sets the live mouse DPI (100..4000) and synchronizes Windows pointer speed (1..20).</summary>
    public static int SetMouseDpi(int targetDpi)
    {
        int clampedDpi = Math.Clamp((int)Math.Round(targetDpi / 50.0) * 50, MinDpi, MaxDpi);
        _customDpi = clampedDpi;
        int speed = DpiToSpeed(clampedDpi);
        try
        {
            SystemParametersInfo(
                SPI_SETMOUSESPEED,
                0,
                new IntPtr(speed),
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch { }
        DpiChanged?.Invoke(clampedDpi, speed);
        return clampedDpi;
    }

    /// <summary>Sets the live Windows pointer speed (1..20) immediately via Win32 SystemParametersInfo.</summary>
    public static bool SetWindowsMouseSpeed(int speed1To20)
    {
        int clamped = Math.Clamp(speed1To20, 1, 20);
        _customDpi = SpeedToDpi(clamped);
        try
        {
            bool ok = SystemParametersInfo(
                SPI_SETMOUSESPEED,
                0,
                new IntPtr(clamped),
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            DpiChanged?.Invoke(_customDpi, clamped);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static string MacroConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "mouse-macros.json");

    public static List<MouseMacroBinding> LoadOrCreateMacroBindings(MouseHardwareReport? hw = null)
    {
        try
        {
            if (File.Exists(MacroConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<List<MouseMacroBinding>>(File.ReadAllText(MacroConfigPath));
                if (loaded is { Count: > 0 }) return loaded;
            }
        }
        catch { }

        var defaults = GetDefaultMacroBindings(hw);
        SaveMacroBindings(defaults);
        return defaults;
    }

    public static List<MouseMacroBinding> GetDefaultMacroBindings(MouseHardwareReport? hw = null)
    {
        string brandTag = hw != null ? $"{hw.Brand} {hw.Model}" : "Connected Mouse";
        return new List<MouseMacroBinding>
        {
            new()
            {
                TriggerId = "Mouse4",
                TriggerLabel = $"Mouse4 (Back Thumb · {brandTag})",
                ActionId = "snip:lasso",
                ActionLabel = "Free-Form Lasso & Vision Annotator",
                Parameter = "Alt+S"
            },
            new()
            {
                TriggerId = "Mouse5",
                TriggerLabel = $"Mouse5 (Forward Thumb · {brandTag})",
                ActionId = "holodex:toggle",
                ActionLabel = "Toggle Holodex Lite Drawer",
                Parameter = "Alt+H"
            },
            new()
            {
                TriggerId = "GestureNorth",
                TriggerLabel = "Right-Hold + Flick North (Up)",
                ActionId = "element:inspect",
                ActionLabel = "Wand Point // Desktop Element & Palette Inspector",
                Parameter = "Alt+E"
            },
            new()
            {
                TriggerId = "GestureEast",
                TriggerLabel = "Right-Hold + Flick East (Right)",
                ActionId = "wiz:summon",
                ActionLabel = "Summon Wiz Crafting Thread",
                Parameter = "Alt+W"
            },
            new()
            {
                TriggerId = "GestureSouth",
                TriggerLabel = "Right-Hold + Flick South (Down)",
                ActionId = "hud:summon",
                ActionLabel = "Summon Omniwand Phosphor HUD",
                Parameter = "Alt+Space"
            },
            new()
            {
                TriggerId = "GestureWest",
                TriggerLabel = "Right-Hold + Flick West (Left)",
                ActionId = "ainbox:summon",
                ActionLabel = "Open AInbox Pod Intake + Handoff Scroll",
                Parameter = "Alt+I"
            }
        };
    }

    public static void SaveMacroBindings(IEnumerable<MouseMacroBinding> bindings)
    {
        try
        {
            string dir = Path.GetDirectoryName(MacroConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                MacroConfigPath,
                JsonSerializer.Serialize(bindings.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
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
            g.RotateTransform(angleDeg);
            float handBoost = role == WindowsCursorRole.Hand ? scale * 1.07f : scale;
            g.ScaleTransform(handBoost, handBoost);

            // State-reactive globe color:
            // - Wiz / AI active (isAiThinking): Coral (#E08B73) with multi-ring radial Bloom effect
            // - Hand (link/button hover): Coral (#E08B73) with subtle Bloom
            // - Help / UpArrow (inspect): Seal amber (#D4A359)
            // - Default Arrow (operate): Patina / Emblem Globe green
            Color stateGlobeColor = isAiThinking
                ? PhosphorTheme.Coral
                : role switch
                {
                    WindowsCursorRole.Hand => PhosphorTheme.Coral,
                    WindowsCursorRole.Help or WindowsCursorRole.UpArrow => PhosphorTheme.Seal,
                    _ => Color.FromArgb(255, PhosphorTheme.EmblemGlobe)
                };

            bool bloomActive = isAiThinking || role == WindowsCursorRole.Hand;
            float bloomPulse = isAiThinking
                ? 1.08f + 0.22f * (float)Math.Sin(spinAngleDeg * Math.PI / 60.0)
                : 0.95f;

            // Pure Ark Aperture icon only (no extra cursor arrow attached):
            // Top apex of the Aperture 'A' sits directly at the cursor hotspot (0, 0).
            var apertureRect = new RectangleF(-12f, 0f, 24f, 24f);
            ArkEmblem.DrawAperture(
                g,
                apertureRect,
                solidGlobe: true,
                globeColorOverride: stateGlobeColor,
                asCursor: true,
                bloomGlobe: bloomActive,
                bloomPulse: bloomPulse);
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
/// Relic Phosphor custom-drawn horizontal DPI slider (100 DPI to 4000 DPI) with live draggable thumb,
/// tick markers (100 / 800 / 1600 / 3200 / 4000), and real-time Windows pointer speed synchronization.
/// </summary>
public sealed class PhosphorDpiSlider : Control
{
    private int _dpiValue;
    private bool _dragging;
    private bool _hovered;

    public event Action<int>? ValueChanged;

    public int Value
    {
        get => _dpiValue;
        set
        {
            int clamped = Math.Clamp(value, MouseHardwareAndWandEngine.MinDpi, MouseHardwareAndWandEngine.MaxDpi);
            if (_dpiValue == clamped) return;
            _dpiValue = clamped;
            Invalidate();
        }
    }

    public PhosphorDpiSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Height = 46;
        Cursor = Cursors.Hand;
        _dpiValue = MouseHardwareAndWandEngine.GetCurrentDpi();

        MouseHardwareAndWandEngine.DpiChanged += (dpi, _) =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(new Action(() => Value = dpi));
            else Value = dpi;
        };
    }

    private Rectangle TrackRect => new(14, 14, Math.Max(40, Width - 28), 8);

    private void UpdateFromMouseX(int mouseX, bool commitToast)
    {
        var tr = TrackRect;
        float ratio = Math.Clamp((mouseX - tr.Left) / (float)tr.Width, 0f, 1f);
        int rawDpi = (int)Math.Round(
            MouseHardwareAndWandEngine.MinDpi +
            ratio * (MouseHardwareAndWandEngine.MaxDpi - MouseHardwareAndWandEngine.MinDpi));
        int stepped = Math.Clamp((int)Math.Round(rawDpi / 50.0) * 50, MouseHardwareAndWandEngine.MinDpi, MouseHardwareAndWandEngine.MaxDpi);
        if (stepped != _dpiValue || commitToast)
        {
            _dpiValue = MouseHardwareAndWandEngine.SetMouseDpi(stepped);
            ValueChanged?.Invoke(_dpiValue);
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Capture = true;
            UpdateFromMouseX(e.X, false);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            UpdateFromMouseX(e.X, false);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
            UpdateFromMouseX(e.X, true);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int delta = e.Delta > 0 ? 100 : -100;
        int next = Math.Clamp(_dpiValue + delta, MouseHardwareAndWandEngine.MinDpi, MouseHardwareAndWandEngine.MaxDpi);
        _dpiValue = MouseHardwareAndWandEngine.SetMouseDpi(next);
        ValueChanged?.Invoke(_dpiValue);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var tr = TrackRect;
        float ratio = Math.Clamp(
            (_dpiValue - MouseHardwareAndWandEngine.MinDpi) /
            (float)(MouseHardwareAndWandEngine.MaxDpi - MouseHardwareAndWandEngine.MinDpi),
            0f,
            1f);
        int thumbX = tr.Left + (int)Math.Round(ratio * tr.Width);
        int thumbY = tr.Top + tr.Height / 2;

        // Track background
        using (var trackBg = new SolidBrush(PhosphorTheme.Ground))
        using (var trackBorder = new Pen(PhosphorTheme.Line2, 1f))
        {
            g.FillRectangle(trackBg, tr);
            g.DrawRectangle(trackBorder, tr);
        }

        // Filled active track (Patina normal, Coral while dragging)
        Color activeColor = _dragging ? PhosphorTheme.Coral : PhosphorTheme.Patina;
        int fillW = Math.Max(2, thumbX - tr.Left);
        using (var fillBrush = new SolidBrush(Color.FromArgb(210, activeColor)))
        {
            g.FillRectangle(fillBrush, tr.Left + 1, tr.Top + 1, fillW, tr.Height - 2);
        }

        // Tick marks & labels (100, 800, 1600, 2800, 4000)
        int[] ticks = { 100, 800, 1600, 2800, 4000 };
        using var tickPen = new Pen(PhosphorTheme.Line2, 1f);
        using var tickFont = PhosphorFonts.Mono(9f);
        using var dimBrush = new SolidBrush(PhosphorTheme.Dim);
        using var activeTextBrush = new SolidBrush(PhosphorTheme.Bone);

        foreach (int tVal in ticks)
        {
            float tRatio = (tVal - MouseHardwareAndWandEngine.MinDpi) /
                           (float)(MouseHardwareAndWandEngine.MaxDpi - MouseHardwareAndWandEngine.MinDpi);
            int tx = tr.Left + (int)Math.Round(tRatio * tr.Width);
            g.DrawLine(tickPen, tx, tr.Bottom + 2, tx, tr.Bottom + 5);
            string lbl = $"{tVal}";
            var sz = g.MeasureString(lbl, tickFont);
            float lx = Math.Clamp(tx - sz.Width / 2f, 2f, Width - sz.Width - 2f);
            bool nearThumb = Math.Abs(tVal - _dpiValue) <= 150;
            g.DrawString(lbl, tickFont, nearThumb ? activeTextBrush : dimBrush, lx, tr.Bottom + 6);
        }

        // Thumb halo + Relic Phosphor thumb handle
        if (_hovered || _dragging)
        {
            using var halo = new SolidBrush(Color.FromArgb(65, activeColor));
            g.FillEllipse(halo, thumbX - 11, thumbY - 11, 22, 22);
        }
        using (var thumbFill = new SolidBrush(PhosphorTheme.Bone))
        using (var thumbCore = new SolidBrush(activeColor))
        using (var thumbOutline = new Pen(PhosphorTheme.Ground, 1.6f))
        {
            g.FillEllipse(thumbFill, thumbX - 7, thumbY - 7, 14, 14);
            g.DrawEllipse(thumbOutline, thumbX - 7, thumbY - 7, 14, 14);
            g.FillEllipse(thumbCore, thumbX - 3.5f, thumbY - 3.5f, 7f, 7f);
        }
    }
}
