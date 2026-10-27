using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "ArkStation_Omniwand_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance)
        {
            return;
        }

        // Always restore system cursors on any exit path
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            NativeCursorManager.RestoreSystemCursors();
            MouseHardwareAndWandEngine.RestoreOriginalSpeed();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => NativeCursorManager.RestoreSystemCursors();
        Application.ApplicationExit += (_, _) => NativeCursorManager.RestoreSystemCursors();

        // A UI exception becomes a toast, never the WinForms crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Always logged; surfaced at most once per 15 s, and never for a toast's own failure, so the handler
        // can never feed itself.
        DateTime lastErrorToast = DateTime.MinValue;
        Application.ThreadException += (_, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "omniwand-errors.log"), $"{DateTime.Now:O} {e.Exception}\n");
                bool fromToast = e.Exception.StackTrace?.Contains(nameof(PhosphorToast), StringComparison.Ordinal) == true
                    || e.Exception.Message.Contains(nameof(PhosphorToast), StringComparison.Ordinal);
                if (!fromToast && DateTime.Now - lastErrorToast > TimeSpan.FromSeconds(15))
                {
                    lastErrorToast = DateTime.Now;
                    PhosphorToast.Show("Something went wrong", e.Exception.Message, "OMNIWAND // RECOVERED", 5200, ToastTone.Error);
                }
            }
            catch { }
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new OmniwandAppContext();
        Application.Run(context);
    }
}

/// <summary>
/// Critically-damped spring (zero overshoot, zero oscillation).
/// </summary>
internal sealed class CriticallyDampedSpring
{
    public double Current { get; private set; }
    public double Target { get; set; }
    public double Velocity { get; private set; }
    public double Stiffness { get; set; }

    public CriticallyDampedSpring(double initial, double stiffness)
    {
        Current = initial;
        Target = initial;
        Stiffness = stiffness;
    }

    public void Jump(double value)
    {
        Current = value;
        Target = value;
        Velocity = 0;
    }

    public void Step(double dtSeconds)
    {
        if (dtSeconds <= 0) return;
        if (dtSeconds > 0.05) dtSeconds = 0.05;

        double damping = 2.0 * Math.Sqrt(Stiffness);
        const double maxStep = 0.0005;
        double remaining = dtSeconds;
        while (remaining > 0)
        {
            double h = remaining > maxStep ? maxStep : remaining;
            double displacement = Current - Target;
            double acceleration = -Stiffness * displacement - damping * Velocity;

            Velocity += acceleration * h;
            Current += Velocity * h;
            remaining -= h;
        }

        if (Math.Abs(Current - Target) < 0.0005 && Math.Abs(Velocity) < 0.0005)
        {
            Current = Target;
            Velocity = 0;
        }
    }
}

internal enum RotationMode
{
    TruePathTrailer,
    SubtleBank,
    LockedAngle
}

/// <summary>
/// All 14 Windows cursor roles from Input Studio (runtimes/cursors/profiles/smooth-cursor.json).
/// </summary>
public enum WindowsCursorRole
{
    Arrow = 32512,
    IBeam = 32513,
    Wait = 32514,
    Crosshair = 32515,
    UpArrow = 32516,
    SizeNWSE = 32642,
    SizeNESW = 32643,
    SizeWE = 32644,
    SizeNS = 32645,
    SizeAll = 32646,
    No = 32648,
    Hand = 32649,
    AppStarting = 32650,
    Help = 32651
}

internal sealed class OmniwandAppContext : ApplicationContext
{
    private readonly SynchronizationContext _uiSync;
    private readonly ArkStationClient _ark = new();
    private readonly GrimXFlowOrb _orb = new();
    private readonly GhostTrailerEngine _ghostTrailer = new();
    private readonly CursorOverlayWindow _overlay;
    private readonly OverlayZGuard _zGuard = new();
    private readonly OmniwandHudForm _hud;
    private readonly WizCraftingWidgetForm _wizWidget;
    private readonly AInboxIntakeWidgetForm _ainboxWidget;
    private readonly ScreenSnipAndElementPickerForm _snipWidget;
    private readonly HolodexLiteDrawerForm _holodexDrawer;
    private readonly PhosphorAnimatedTooltipForm _tutorialTooltip;
    private readonly WizEnvironmentScanReport _onboardingReport;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _hideNativeMenuItem;
    private readonly ToolStripMenuItem _mx4PkgItem;
    private readonly ToolStripMenuItem _univPkgItem;
    private readonly Thread _renderThread;

    private volatile bool _running = true;
    private volatile bool _isAiThinking;
    private volatile float _baseScaleMultiplier = 1.25f;
    private volatile VrStreamMode _vrMode = VrStreamMode.Auto;
    private string? _vrStreamName;
    private float _preVrScaleMultiplier = 1.25f;
    private System.Threading.Timer? _vrTimer;
    private ToolStripMenuItem? _vrStatusItem;
    private const float VrScaleBoost = 1.6f;
    private volatile RotationMode _rotationMode = RotationMode.SubtleBank;
    private volatile WindowsCursorRole _currentRole = WindowsCursorRole.Arrow;

    // Critically damped position & morph springs
    private readonly CriticallyDampedSpring _cursorX = new(0, stiffness: 900);
    private readonly CriticallyDampedSpring _cursorY = new(0, stiffness: 900);
    private readonly CriticallyDampedSpring _scale = new(1.0, stiffness: 650);
    private readonly CriticallyDampedSpring _roleMorph = new(0.0, stiffness: 580);

    // Kinematic trailer state for poll-rate-independent, spinout-free orientation
    private double _trailX;
    private double _trailY;
    private double _dirX = -0.3826834;
    private double _dirY = -0.9238795;
    private double _targetDirX = -0.3826834;
    private double _targetDirY = -0.9238795;

    private double _lastActiveMoveTimeSec;
    private bool _wasMouseDown;
    private bool _wasOrbTriggerDown;
    private bool _wasGhostTriggerDown;
    private static readonly bool HardwareMacrosEnabled =
        string.Equals(Environment.GetEnvironmentVariable("OMNIWAND_MOUSE_MACROS"), "1", StringComparison.Ordinal);
    private bool _wasMouse4Down;
    private bool _wasMouse5Down;
    private bool _wasRightMouseDown;
    private Point _rightGestureAnchor;
    private double _rightDownStartSec = -1;
    private double _middleMouseDownStartSec = -1;
    private int _rolePollCounter;
    private float _spinAngleDeg;

    public OmniwandAppContext()
    {
        _uiSync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Win32.timeBeginPeriod(1);
        StartControlPipeServer();

        // Zero-manual Wiz automated onboarding & environment/hardware scan
        bool isFirstRun = !WizOnboardingEngine.HasCompletedOnboarding();
        _onboardingReport = WizOnboardingEngine.RunZeroManualProvisioning(_ark);

        Win32.GetCursorPos(out Win32.POINT startPt);
        _cursorX.Jump(startPt.X);
        _cursorY.Jump(startPt.Y);
        _trailX = startPt.X - _dirX * 22.0;
        _trailY = startPt.Y - _dirY * 22.0;

        // Wiz acts through the widgets that own each behavior: snip tools, AInbox's composer, Holodex work orders
        // and Codex. The lambdas read the fields when Wiz calls them, after every widget below exists.
        _wizWidget = new WizCraftingWidgetForm(_ark, thinking => _isAiThinking = thinking, new WizAgentHost
        {
            OpenLasso = pt => _snipWidget?.SummonAt(pt, WandCaptureTool.FreeformLasso),
            OpenElementPicker = pt => _snipWidget?.SummonAt(pt, WandCaptureTool.ElementPicker),
            OpenAInboxWithAsk = (pt, ask) =>
            {
                _ainboxWidget?.SummonAt(pt);
                _ainboxWidget?.PrefillAsk(ask);
            },
            QueueWorkOrder = (agent, task) =>
            {
                if (_holodexDrawer == null) return (null, "Holodex is not ready yet.");
                string? orderId = _holodexDrawer.QueueWorkOrder(agent, task, out string error);
                return (orderId, error);
            },
            CodexSaved = entry =>
            {
                _holodexDrawer?.RefreshCodexEntries();
                PhosphorToast.Show("Saved to Codex", entry.Title, "WIZ // CODEX", 2600, ToastTone.Success);
            }
        });
        _ainboxWidget = new AInboxIntakeWidgetForm(_ark, thinking => _isAiThinking = thinking);
        _snipWidget = new ScreenSnipAndElementPickerForm(
            _ark,
            thinking => _isAiThinking = thinking,
            _ => _holodexDrawer?.RefreshCodexEntries());
        // The AInbox well's Pick and Lasso chips open the snip tools, and Send to AInbox brings the capture back.
        _ainboxWidget.RequestCapture = tool => _snipWidget.SummonAt(GetCursorPoint(), tool);
        _snipWidget.OnSendToAInbox = (pt, dataUrl, name) => _ainboxWidget.SummonAt(pt, dataUrl, name);
        _holodexDrawer = new HolodexLiteDrawerForm(
            _ark,
            thinking => _isAiThinking = thinking,
            pt => _wizWidget.SummonAt(pt),
            pt => _ainboxWidget.SummonAt(pt),
            (pt, tool) => _snipWidget.SummonAt(pt, tool),
            () => SummonTutorialTooltips(),
            style =>
            {
                MouseHardwareAndWandEngine.ActiveCursorStyle = style;
                _overlay?.ForceCursorRedraw();
            });
        _hud = new OmniwandHudForm(
            _ark,
            thinking => _isAiThinking = thinking,
            pt => _wizWidget.SummonAt(pt),
            pt => _ainboxWidget.SummonAt(pt),
            pt => _holodexDrawer.SummonDrawer(pt),
            pt => _snipWidget.SummonAt(pt, WandCaptureTool.FreeformLasso));
        _tutorialTooltip = new PhosphorAnimatedTooltipForm(
            _ark,
            _onboardingReport,
            () => _holodexDrawer.SummonDrawer(GetCursorPoint(), "session"),
            () => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.FreeformLasso),
            () => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.ElementPicker),
            () => ToggleOrbFromHotkey(),
            () => CycleCursorStyleFromHotkey(),
            () => SummonHud(),
            () => SummonAInboxIntake());

        _overlay = new CursorOverlayWindow(_orb, _ghostTrailer);
        _overlay.RegisterHotkeys(
            onSummonHud: SummonHud,
            onToggleNative: ToggleNativeCursor,
            onQuit: ExitApp,
            onOpenWiz: () => SummonWizCrafting(),
            onOpenAInbox: () => SummonAInboxIntake(),
            onToggleOrb: ToggleOrbFromHotkey,
            onToggleGhostTrailer: ToggleGhostTrailerFromHotkey,
            onCycleProfile: CycleProfileFromHotkey,
            onOpenHolodex: () => ToggleHolodexDrawer(),
            onOpenSnipLasso: () => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.FreeformLasso),
            onOpenElementPicker: () => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.ElementPicker),
            onOpenTutorial: () => SummonTutorialTooltips());

        NativeCursorManager.CaptureStockCursorHandles();
        NativeCursorManager.HideSystemCursors();

        PhosphorTips.Install();
        ToolStripManager.Renderer = new PhosphorMenuRenderer();
        // PhosphorTrayMenu owns the geometry: one column width for every panel, submenus always open right, and
        // the menu shifts left of the tray far enough for them to fit (PhosphorMenuLayout).
        var menu = new PhosphorTrayMenu
        {
            Renderer = new PhosphorMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false // its one toggle shows its check in the shortcut column
        };
        var titleItem = new PhosphorMenuHeader(
            "Omniwand // v0.3",
            "Relic Phosphor",
            $"{_onboardingReport.MouseHardware.Brand} {_onboardingReport.MouseHardware.Model}");
        menu.Items.Add(titleItem);

        var summonItem = new ToolStripMenuItem("Phosphor HUD") { ShortcutKeyDisplayString = "Alt+Space" };
        summonItem.Click += (_, _) => SummonHud();
        menu.Items.Add(summonItem);

        var holodexItem = new ToolStripMenuItem("Holodex Lite drawer") { ShortcutKeyDisplayString = "Alt+H" };
        holodexItem.Click += (_, _) => ToggleHolodexDrawer();
        menu.Items.Add(holodexItem);

        var wizItem = new ToolStripMenuItem("Wiz crafting") { ShortcutKeyDisplayString = "Alt+W" };
        wizItem.Click += (_, _) => SummonWizCrafting();
        menu.Items.Add(wizItem);

        var captureMenu = new ToolStripMenuItem("Capture && intake");
        var lassoItem = new ToolStripMenuItem("Lasso + Vision annotator") { ShortcutKeyDisplayString = "Alt+S" };
        lassoItem.Click += (_, _) => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.FreeformLasso);
        captureMenu.DropDownItems.Add(lassoItem);

        var elemItem = new ToolStripMenuItem("Element + Palette picker") { ShortcutKeyDisplayString = "Alt+E" };
        elemItem.Click += (_, _) => _snipWidget.SummonAt(GetCursorPoint(), WandCaptureTool.ElementPicker);
        captureMenu.DropDownItems.Add(elemItem);

        var ainboxItem = new ToolStripMenuItem("AInbox intake") { ShortcutKeyDisplayString = "Alt+I" };
        ainboxItem.Click += (_, _) => SummonAInboxIntake();
        captureMenu.DropDownItems.Add(ainboxItem);
        menu.Items.Add(captureMenu);

        var orbMenu = new ToolStripMenuItem("Spell Orb");
        foreach (var slot in GrimXFlowOrb.Slots)
        {
            var s = slot;
            string label = s.Id == "ainbox" ? "AInbox" : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Title.ToLowerInvariant());
            var slotItem = new ToolStripMenuItem(label) { ShortcutKeyDisplayString = $"{s.Index + 1} // {s.Verb}", ToolTipText = s.Subtitle };
            slotItem.Click += (_, _) => ExecuteOrbSlotOnUiThread(s, GetCursorPoint());
            orbMenu.DropDownItems.Add(slotItem);
        }
        menu.Items.Add(orbMenu);

        // ── Consolidated Settings Submenu ────────────────────────────────────
        menu.Items.Add(new ToolStripSeparator());
        var settingsMenu = new ToolStripMenuItem("Settings");

        var openSettingsPanelItem = new ToolStripMenuItem("Open Settings && Mods drawer...") { ShortcutKeyDisplayString = "Alt+H" };
        openSettingsPanelItem.Click += (_, _) => _holodexDrawer.SummonDrawer(GetCursorPoint(), "mods");
        settingsMenu.DropDownItems.Add(openSettingsPanelItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());

        // 1. Mouse DPI (100 - 4000 slider + presets)
        var dpiMenu = new ToolStripMenuItem($"Mouse DPI ({_onboardingReport.MouseHardware.Brand})");
        var trayDpiSlider = new PhosphorDpiSlider { Size = new Size(220, 44), BackColor = PhosphorTheme.Surface };
        var sliderHost = new ToolStripControlHost(trayDpiSlider)
        {
            AutoSize = false,
            Size = new Size(224, 46),
            Margin = new Padding(6, 4, 6, 4)
        };
        dpiMenu.DropDownItems.Add(sliderHost);
        dpiMenu.DropDownItems.Add(new ToolStripSeparator());
        AddDpiPresetOption(dpiMenu, "Ultra-Low Sniper (100 DPI)", 1);
        AddDpiPresetOption(dpiMenu, "Low Precision (400 DPI)", 3);
        AddDpiPresetOption(dpiMenu, "Precision (800 DPI)", 6);
        AddDpiPresetOption(dpiMenu, "Balanced (1600 DPI)", 10);
        AddDpiPresetOption(dpiMenu, "High Speed (3200 DPI)", 15);
        AddDpiPresetOption(dpiMenu, "Max (4000 DPI)", 20);
        settingsMenu.DropDownItems.Add(dpiMenu);

        // 2. Wand Gestures & Spells (ported from MagiXLab Video Bench)
        var gestureMenu = new ToolStripMenuItem("Wand gestures && spells");
        var ctrlGestureToggle = new ToolStripMenuItem("Hold Ctrl + gesture casts spell")
        {
            Checked = OmniwandSpellGestureEngine.HoldCtrlToArm,
            CheckOnClick = true,
            ShortcutKeyDisplayString = "CTRL"
        };
        ctrlGestureToggle.CheckedChanged += (_, _) => OmniwandSpellGestureEngine.HoldCtrlToArm = ctrlGestureToggle.Checked;
        gestureMenu.DropDownItems.Add(ctrlGestureToggle);

        var rmbGestureToggle = new ToolStripMenuItem("Hold Right-Click + gesture casts spell")
        {
            Checked = OmniwandSpellGestureEngine.HoldRightMouseToArm,
            CheckOnClick = true,
            ShortcutKeyDisplayString = "RMB"
        };
        rmbGestureToggle.CheckedChanged += (_, _) => OmniwandSpellGestureEngine.HoldRightMouseToArm = rmbGestureToggle.Checked;
        gestureMenu.DropDownItems.Add(rmbGestureToggle);
        gestureMenu.DropDownItems.Add(new ToolStripSeparator());

        var spell1Item = new ToolStripMenuItem("Shake L-R  ->  Time Splitter (Wiz Inspect)") { ShortcutKeyDisplayString = "SHAKE X" };
        spell1Item.Click += (_, _) => ExecuteWandSpellGesture(WandSpellGestureKind.ShakeHorizontal, GetCursorPoint());
        gestureMenu.DropDownItems.Add(spell1Item);

        var spell2Item = new ToolStripMenuItem("Shake U-D  ->  Copycat (Lasso Snip)") { ShortcutKeyDisplayString = "SHAKE Y" };
        spell2Item.Click += (_, _) => ExecuteWandSpellGesture(WandSpellGestureKind.ShakeVertical, GetCursorPoint());
        gestureMenu.DropDownItems.Add(spell2Item);

        var spell3Item = new ToolStripMenuItem("Draw Circle  ->  Mutation (GrimX Spell)") { ShortcutKeyDisplayString = "CIRCLE" };
        spell3Item.Click += (_, _) => ExecuteWandSpellGesture(WandSpellGestureKind.CircleLoop, GetCursorPoint());
        gestureMenu.DropDownItems.Add(spell3Item);

        var spell4Item = new ToolStripMenuItem("Sweep Right  ->  Synthesize (Holodex)") { ShortcutKeyDisplayString = "SWEEP R" };
        spell4Item.Click += (_, _) => ExecuteWandSpellGesture(WandSpellGestureKind.SweepRight, GetCursorPoint());
        gestureMenu.DropDownItems.Add(spell4Item);
        settingsMenu.DropDownItems.Add(gestureMenu);

        // 3. Cursor Icon Style & Aperture Morph Engine
        var iconStyleMenu = new ToolStripMenuItem("Cursor icon style");
        AddCursorStyleOption(iconStyleMenu, "Ark Aperture", "ARK", CursorIconStyle.ArkAperture, true);
        AddCursorStyleOption(iconStyleMenu, "Relic Wand", "WAND", CursorIconStyle.RelicWand, false);
        AddCursorStyleOption(iconStyleMenu, "Phosphor Arrow", "ARR", CursorIconStyle.PhosphorArrow, false);
        AddCursorStyleOption(iconStyleMenu, "Viewfinder Reticle", "CAD", CursorIconStyle.ViewfinderReticle, false);
        settingsMenu.DropDownItems.Add(iconStyleMenu);

        var morphMenu = new ToolStripMenuItem("Aperture morph mode");
        AddMorphModeOption(morphMenu, "Auto-morph all 14 states", "AUTO", ApertureMorphMode.AutoContextual, true);
        AddMorphModeOption(morphMenu, "Holoscope Iris", "IRIS", ApertureMorphMode.HoloscopeIris, false);
        AddMorphModeOption(morphMenu, "Syndicate Compass Rose", "COMP", ApertureMorphMode.SyndicateCompass, false);
        AddMorphModeOption(morphMenu, "Wiz Wand Star", "STAR", ApertureMorphMode.WizWandStar, false);
        AddMorphModeOption(morphMenu, "Aegis Shield", "SHLD", ApertureMorphMode.AegisShield, false);
        morphMenu.DropDownItems.Add(new ToolStripSeparator());
        var morphShowcaseItem = new ToolStripMenuItem("Run live morph showcase") { ShortcutKeyDisplayString = "DEMO" };
        morphShowcaseItem.Click += (_, _) =>
        {
            ArkApertureMorphEngine.StartMorphShowcase();
            PhosphorToast.Show(
                "Aperture Morph Showcase",
                "Watch the cursor morph through Hand (Coral Bloom), solid Patina IBeam, Crosshair, Resize, Compass, Turbine & Aegis Lock.",
                "ARK APERTURE // 240HZ MORPH ENGINE",
                6000);
        };
        morphMenu.DropDownItems.Add(morphShowcaseItem);
        settingsMenu.DropDownItems.Add(morphMenu);

        // 4. Rotation & Size
        var modeMenu = new ToolStripMenuItem("Rotation mode");
        AddModeOption(modeMenu, "Subtle banking", "Default operator tilt, switches to True Path when Wiz is active", RotationMode.SubtleBank, true);
        AddModeOption(modeMenu, "True path trailer", "Smooth directional, no spinout", RotationMode.TruePathTrailer, false);
        AddModeOption(modeMenu, "Locked angle", "Smooth position only", RotationMode.LockedAngle, false);
        RotationPreviewItem.Animate(modeMenu);
        settingsMenu.DropDownItems.Add(modeMenu);

        var sizeMenu = new ToolStripMenuItem("Cursor size");
        AddScaleOption(sizeMenu, "Compact", "1.0x", 1.0f, false);
        AddScaleOption(sizeMenu, "Default", "1.25x", 1.25f, true);
        AddScaleOption(sizeMenu, "Large", "1.5x", 1.5f, false);
        settingsMenu.DropDownItems.Add(sizeMenu);

        _hideNativeMenuItem = new ToolStripMenuItem("Hide Windows cursor")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+C",
            Checked = NativeCursorManager.IsHidden,
            CheckOnClick = false
        };
        _hideNativeMenuItem.Click += (_, _) => ToggleNativeCursor();
        settingsMenu.DropDownItems.Add(_hideNativeMenuItem);

        // 5. Device, Onboarding & VR
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        var pkgMenu = new ToolStripMenuItem("Mouse package");
        _mx4PkgItem = new ToolStripMenuItem("MX Master 4")
        {
            ShortcutKeyDisplayString = "1",
            ToolTipText = "Logitech MX Master 4: Actions Ring slots, F15 fallback orb, F16 GhostTrailer",
            Checked = _ark.ActiveMousePackageId == "mx-master-4"
        };
        _univPkgItem = new ToolStripMenuItem("Universal mouse")
        {
            ShortcutKeyDisplayString = "2",
            ToolTipText = "Any mouse plus keyboard shortcuts",
            Checked = _ark.ActiveMousePackageId != "mx-master-4"
        };
        _mx4PkgItem.Click += (_, _) =>
        {
            _ark.ActiveMousePackageId = "mx-master-4";
            _mx4PkgItem.Checked = true;
            _univPkgItem.Checked = false;
        };
        _univPkgItem.Click += (_, _) =>
        {
            _ark.ActiveMousePackageId = "universal-mouse";
            _mx4PkgItem.Checked = false;
            _univPkgItem.Checked = true;
        };
        pkgMenu.DropDownItems.Add(_mx4PkgItem);
        pkgMenu.DropDownItems.Add(_univPkgItem);
        settingsMenu.DropDownItems.Add(pkgMenu);

        var tutorialItem = new ToolStripMenuItem("Wiz onboarding + tooltips") { ShortcutKeyDisplayString = "Alt+T" };
        tutorialItem.Click += (_, _) => SummonTutorialTooltips();
        settingsMenu.DropDownItems.Add(tutorialItem);

        var vrMenu = new ToolStripMenuItem("VR streaming");
        _vrStatusItem = new ToolStripMenuItem("Not streaming") { Enabled = false };
        vrMenu.DropDownItems.Add(_vrStatusItem);
        vrMenu.DropDownItems.Add(new ToolStripSeparator());
        AddVrModeOption(vrMenu, "Auto-detect", "Default: adapt when a stream is detected", VrStreamMode.Auto, true);
        AddVrModeOption(vrMenu, "Always on", "Always use VR mode", VrStreamMode.AlwaysOn, false);
        AddVrModeOption(vrMenu, "Off", "Never adapt", VrStreamMode.Off, false);
        var arrowHeader = new PhosphorMenuEyebrow("Arrow test");
        vrMenu.DropDownItems.Add(arrowHeader);
        AddArrowStyleOption(vrMenu, "ARGB empty", "Default, Air Link verified", ArrowSlotStyle.ArgbClear, true);
        AddArrowStyleOption(vrMenu, "Monochrome", "Monochrome transparent arrow slot", ArrowSlotStyle.Mono, false);
        AddArrowStyleOption(vrMenu, "ARGB near-empty", "ARGB arrow slot, near-empty alpha", ArrowSlotStyle.NearClear, false);
        var flushCacheItem = new ToolStripMenuItem("Flush cursor cache") { Name = "Tray.VrFlushCache", ToolTipText = "Flush the OculusDash cursor cache now" };
        flushCacheItem.Click += (_, _) =>
        {
            NativeCursorManager.HideSystemCursors(force: true);
            VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
            _overlay.PulseCursorHandleForVr(_uiSync);
        };
        vrMenu.DropDownItems.Add(flushCacheItem);
        var diagItem = new ToolStripMenuItem("Log pointer state") { CheckOnClick = true, ToolTipText = "Writes %TEMP%\\omniwand-pointer.log" };
        diagItem.CheckedChanged += (_, _) => SetPointerDiagnostics(diagItem.Checked);
        vrMenu.DropDownItems.Add(diagItem);
        settingsMenu.DropDownItems.Add(vrMenu);

        menu.Items.Add(settingsMenu);

        menu.Name = "Tray.Menu";
        titleItem.Name = "Tray.Title";
        vrMenu.Name = "Tray.VrStreaming";
        _vrStatusItem.Name = "Tray.VrStatus";
        arrowHeader.Name = "Tray.VrArrowTestHeader";
        diagItem.Name = "Tray.VrPointerLog";
        _vrTimer = new System.Threading.Timer(_ => VrTick(), null, 600, 1500);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit and restore cursor") { ShortcutKeyDisplayString = "Ctrl+Alt+Q", Tag = PhosphorMenuRenderer.DangerTag, Name = "Tray.Exit" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);
        menu.Items.Add(new PhosphorMenuSpacer());

        PhosphorMenuFrame.Apply(captureMenu, "Capture & intake");
        PhosphorMenuFrame.Apply(orbMenu, "Spell Orb // ring slots");
        PhosphorMenuFrame.Apply(settingsMenu, "Omniwand settings");
        PhosphorMenuFrame.Apply(gestureMenu, "Spells // Ctrl + gesture");
        PhosphorMenuFrame.Apply(iconStyleMenu, "Cursor icon // on-brand");
        PhosphorMenuFrame.Apply(morphMenu, "Aperture morph // 240Hz");
        PhosphorMenuFrame.Apply(modeMenu, "Wand rotation");
        PhosphorMenuFrame.Apply(sizeMenu, "Cursor size");
        PhosphorMenuFrame.Apply(pkgMenu, "Mouse package");
        PhosphorMenuFrame.Apply(dpiMenu, "Mouse DPI // speed");
        PhosphorMenuFrame.Apply(vrMenu, "VR streaming");
        // Size the panels now, so the first open already anchors the finished width to the tray.
        PhosphorMenuLayout.Apply(menu);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Omniwand v0.3 · Relic Phosphor (Alt+Space HUD | Alt+H Holodex | Alt+S Lasso | Alt+E Point UI)",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                try
                {
                    var mi = typeof(NotifyIcon).GetMethod(
                        "ShowContextMenu",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        mi.Invoke(_trayIcon, null);
                        return;
                    }
                }
                catch
                {
                    // Fallback to direct ContextMenuStrip.Show below
                }
                // Same anchor as NotifyIcon's own path: the menu sits above and left of the pointer.
                menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
            }
        };

        _renderThread = new Thread(RenderLoop)
        {
            Name = "OmniwandVSyncRenderThread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _renderThread.Start();

        PhosphorToast.UiContext = _uiSync;
        PhosphorToast.Show(
            $"Omniwand v0.3 · {_onboardingReport.MouseHardware.Brand} {_onboardingReport.MouseHardware.Model}",
            "Alt+H  Holodex Lite & Syndicate Session\nAlt+S  Free-Form Lasso   Alt+E  Element Picker\nAlt+W  Wiz Crafting      Alt+T  Animated Tooltips",
            "PHOSPHOR SHELL // WIZ PROVISIONED",
            5400);

        if (isFirstRun)
        {
            _uiSync.Post(_ => SummonTutorialTooltips(), null);
        }
    }

    private void StartControlPipeServer()
    {
        Task.Run(async () =>
        {
            while (_running)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        "ArkStation.Omniwand.Control",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    var connectedPipe = pipe;
                    pipe = null;
                    _ = HandleControlPipeClientAsync(connectedPipe);
                }
                catch
                {
                    pipe?.Dispose();
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        });
    }

    private async Task HandleControlPipeClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            var buf = new byte[1024];
            var sb = new StringBuilder();
            while (pipe.IsConnected && sb.Length < 1024)
            {
                int n = await pipe.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().IndexOf('\n') >= 0) break;
            }

            string rawCmd = sb.ToString().Trim('\uFEFF', '\r', '\n', ' ', '\t');
            if (!string.IsNullOrWhiteSpace(rawCmd))
            {
                int newlineIdx = rawCmd.IndexOfAny(new[] { '\r', '\n' });
                if (newlineIdx >= 0) rawCmd = rawCmd[..newlineIdx].Trim();
                string reply = HandleControlPipeCommand(rawCmd) + "\r\n";
                using var writeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
                byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                await pipe.WriteAsync(replyBytes.AsMemory(0, replyBytes.Length), writeCts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(writeCts.Token).ConfigureAwait(false);
                try { pipe.WaitForPipeDrain(); } catch { }
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore transient client disconnect
        }
        finally
        {
            try
            {
                if (pipe.IsConnected) pipe.Disconnect();
            }
            catch { }
            pipe.Dispose();
        }
    }

    private string HandleControlPipeCommand(string cmd)
    {
        var pt = GetCursorPoint();
        if (string.Equals(cmd, "orb:toggle", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => ToggleOrbFromHotkey(), null);
            return "ok:orb:toggle";
        }
        if (string.Equals(cmd, "ghost:toggle", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => ToggleGhostTrailerFromHotkey(), null);
            return "ok:ghost:toggle";
        }
        if (string.Equals(cmd, "hud:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => SummonHud(), null);
            return "ok:hud:summon";
        }
        if (string.Equals(cmd, "wiz:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => SummonWizCrafting("flow"), null);
            return "ok:wiz:summon";
        }
        if (string.Equals(cmd, "ainbox:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => SummonAInboxIntake(), null);
            return "ok:ainbox:summon";
        }
        if (string.Equals(cmd, "holodex:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _holodexDrawer.SummonDrawer(pt), null);
            return "ok:holodex:summon";
        }
        if (cmd.StartsWith("holodex:tab:", StringComparison.OrdinalIgnoreCase))
        {
            string tab = cmd["holodex:tab:".Length..].Trim().ToLowerInvariant();
            _uiSync.Post(_ => _holodexDrawer.SummonDrawer(pt, tab), null);
            return $"ok:holodex:tab:{tab}";
        }
        if (string.Equals(cmd, "snip:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _snipWidget.SummonAt(pt, WandCaptureTool.FreeformLasso), null);
            return "ok:snip:summon";
        }
        if (string.Equals(cmd, "element:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _snipWidget.SummonAt(pt, WandCaptureTool.ElementPicker), null);
            return "ok:element:summon";
        }
        if (string.Equals(cmd, "tutorial:summon", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => SummonTutorialTooltips(), null);
            return "ok:tutorial:summon";
        }
        if (string.Equals(cmd, "morph:demo", StringComparison.OrdinalIgnoreCase))
        {
            ArkApertureMorphEngine.StartMorphShowcase();
            _uiSync.Post(_ =>
            {
                PhosphorToast.Show(
                    "Aperture Morph Showcase",
                    "Cycling live through Hand (Coral Bloom), IBeam Caliper, Crosshair Reticle, Resize Caliper, Compass Rose, Turbine, Wiz Star & Aegis Lock.",
                    "ARK APERTURE // 240HZ MORPH ENGINE",
                    6500);
            }, null);
            return "ok:morph:demo";
        }
        if (cmd.StartsWith("morph:mode:", StringComparison.OrdinalIgnoreCase))
        {
            string modeArg = cmd["morph:mode:".Length..].Trim();
            if (Enum.TryParse<ApertureMorphMode>(modeArg, true, out var parsedMode))
            {
                ArkApertureMorphEngine.ActiveMorphMode = parsedMode;
                _overlay.ForceCursorRedraw();
                return $"ok:morph:mode:{parsedMode}";
            }
            return "error:morph:mode:invalid";
        }
        if (cmd.StartsWith("render:", StringComparison.OrdinalIgnoreCase))
        {
            // As-built captures of every surface, drawn by the running app (SurfaceCapture).
            string dir = cmd.Substring("render:".Length).Trim();
            if (dir.Length == 0) return "error:render:folder required";
            string result = "error:render:not run";
            _uiSync.Send(_ =>
            {
                try
                {
                    var files = SurfaceCapture.CaptureAll(dir, _trayIcon.ContextMenuStrip!,
                        ("hud", _hud),
                        ("wiz", _wizWidget),
                        ("ainbox", _ainboxWidget),
                        ("holodex", _holodexDrawer),
                        ("snip", _snipWidget),
                        ("tutorial", _tutorialTooltip));
                    result = $"ok:render:{files.Count}:{string.Join(",", files)}";
                }
                catch (Exception ex) { result = $"error:render:{ex.Message}"; }
            }, null);
            return result;
        }
        if (string.Equals(cmd, "widgets:close", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ =>
            {
                _hud.Hide();
                _wizWidget.Hide();
                _ainboxWidget.Hide();
                _holodexDrawer.Hide();
                _snipWidget.Hide();
                _tutorialTooltip.Hide();
            }, null);
            return "ok:widgets:close";
        }
        if (string.Equals(cmd, "hud:close", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _hud.Hide(), null);
            return "ok:hud:close";
        }
        if (string.Equals(cmd, "wiz:close", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _wizWidget.Hide(), null);
            return "ok:wiz:close";
        }
        if (string.Equals(cmd, "ainbox:close", StringComparison.OrdinalIgnoreCase))
        {
            _uiSync.Post(_ => _ainboxWidget.Hide(), null);
            return "ok:ainbox:close";
        }
        if (cmd.StartsWith("wiz:ask:", StringComparison.OrdinalIgnoreCase))
        {
            string q = cmd["wiz:ask:".Length..].Trim();
            string reply = "error:wiz:ask:not run";
            var res = _ark.AskWizGeminiArgonAsync(string.IsNullOrEmpty(q) ? "Say hello in one sentence." : q, "spell").GetAwaiter().GetResult();
            reply = $"ok:wiz:ask:ok={res.Ok}:runtime={res.Runtime}:model={res.Model}:output={res.Output.Replace('\r', ' ').Replace('\n', ' ')}";
            return reply;
        }
        if (cmd.StartsWith("hud:ask:", StringComparison.OrdinalIgnoreCase))
        {
            string q = cmd["hud:ask:".Length..].Trim();
            var res = _ark.AskArkGatewayAsync(string.IsNullOrEmpty(q) ? "Say hello in one sentence." : q).GetAwaiter().GetResult();
            return $"ok:hud:ask:ok={res.Ok}:output={res.Output.Replace('\r', ' ').Replace('\n', ' ')}";
        }
        if (cmd.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
        {
            string slotKey = cmd["slot:".Length..].Trim();
            var matched = GrimXFlowOrb.Slots.FirstOrDefault(s =>
                string.Equals(s.Id, slotKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((s.Index + 1).ToString(), slotKey, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                ExecuteOrbSlotOnUiThread(matched, pt);
                return $"ok:slot:{matched.Id}:{matched.Verb}";
            }
        }
        if (cmd.StartsWith("profile:cycle", StringComparison.OrdinalIgnoreCase))
        {
            string next = _ark.CycleCursorProfile();
            _uiSync.Post(_ =>
            {
                PhosphorToast.Show("Cursor profile", $"Active Input Studio profile: {next}", "MX MASTER 4", 1800);
            }, null);
            return $"ok:profile:{next}";
        }
        if (cmd.StartsWith("scale:", StringComparison.OrdinalIgnoreCase))
        {
            string deltaStr = cmd["scale:".Length..].Trim();
            if (int.TryParse(deltaStr, out int delta))
            {
                float nextScale = Math.Clamp(_baseScaleMultiplier + delta * 0.05f, 0.8f, 2.2f);
                _baseScaleMultiplier = nextScale;
                return $"ok:scale:{nextScale:0.00}x";
            }
        }
        return $"ok:{cmd}";
    }

    private static Point GetCursorPoint()
    {
        return Win32.GetCursorPos(out Win32.POINT pt) ? new Point(pt.X, pt.Y) : Point.Empty;
    }

    private void SummonHud() => _hud.SummonAt(GetCursorPoint());

    private void SummonWizCrafting(string? flowLens = null) => _wizWidget.SummonAt(GetCursorPoint(), flowLens);

    private void SummonAInboxIntake() => _ainboxWidget.SummonAt(GetCursorPoint());

    private void ToggleHolodexDrawer()
    {
        if (_holodexDrawer.Visible) _holodexDrawer.Hide();
        else _holodexDrawer.SummonDrawer(GetCursorPoint());
    }

    private void SummonTutorialTooltips() => _tutorialTooltip.StartTutorial(GetCursorPoint());

    private void CycleCursorStyleFromHotkey()
    {
        var values = Enum.GetValues<CursorIconStyle>();
        int nextIdx = ((int)MouseHardwareAndWandEngine.ActiveCursorStyle + 1) % values.Length;
        MouseHardwareAndWandEngine.ActiveCursorStyle = values[nextIdx];
        _overlay.ForceCursorRedraw();
        PhosphorToast.Show(
            "Cursor Icon Updated",
            $"Active on-brand cursor style: {MouseHardwareAndWandEngine.ActiveCursorStyle}",
            "OMNIWAND // CURSOR ENGINE",
            2200);
    }

    private void ExecuteWandSpellGesture(WandSpellGestureKind spell, Point cursorPt)
    {
        _uiSync.Post(_ =>
        {
            switch (spell)
            {
                case WandSpellGestureKind.ShakeHorizontal:
                    PhosphorToast.Show(
                        "TIME SPLITTER · WIZ INSPECT",
                        "Horizontal shake spell cast — summoning Wiz Crafting on active target.",
                        "OMNIWAND SPELL // SHAKE L-R",
                        2800,
                        ToastTone.Success);
                    SummonWizCrafting("flow");
                    break;

                case WandSpellGestureKind.ShakeVertical:
                    PhosphorToast.Show(
                        "COPYCAT · LASSO CAPTURE",
                        "Vertical shake spell cast — arming Free-Form Lasso & Vision Annotator.",
                        "OMNIWAND SPELL // SHAKE U-D",
                        2800,
                        ToastTone.Success);
                    _snipWidget.SummonAt(cursorPt, WandCaptureTool.FreeformLasso);
                    break;

                case WandSpellGestureKind.CircleLoop:
                    PhosphorToast.Show(
                        "MUTATION · GRIMX SPELL CAST",
                        "Circle gesture recognized — casting GrimX Spell on target under cursor.",
                        "OMNIWAND SPELL // CIRCLE LOOP",
                        2800,
                        ToastTone.Success);
                    ExecuteOrbSlotOnUiThread(GrimXFlowOrb.Slots[0], cursorPt);
                    break;

                case WandSpellGestureKind.SweepRight:
                    PhosphorToast.Show(
                        "SYNTHESIZE · HOLODEX BRIDGE",
                        "Rightward sweep recognized — opening Holodex Syndicate Session.",
                        "OMNIWAND SPELL // SWEEP RIGHT",
                        2800,
                        ToastTone.Success);
                    _holodexDrawer.SummonDrawer(cursorPt, "session");
                    break;
            }
        }, null);
    }

    private void ExecuteMouseMacroTrigger(string triggerId, Point cursorPt)
    {
        _uiSync.Post(_ =>
        {
            var macros = MouseHardwareAndWandEngine.LoadOrCreateMacroBindings(_onboardingReport.MouseHardware);
            var match = macros.FirstOrDefault(m => string.Equals(m.TriggerId, triggerId, StringComparison.OrdinalIgnoreCase));
            string action = match?.ActionId ?? "";

            switch (action)
            {
                case "snip:lasso":
                    _snipWidget.SummonAt(cursorPt, WandCaptureTool.FreeformLasso);
                    break;
                case "element:inspect":
                    _snipWidget.SummonAt(cursorPt, WandCaptureTool.ElementPicker);
                    break;
                case "holodex:toggle":
                    ToggleHolodexDrawer();
                    break;
                case "wiz:summon":
                    SummonWizCrafting("flow");
                    break;
                case "hud:summon":
                    SummonHud();
                    break;
                case "ainbox:summon":
                    SummonAInboxIntake();
                    break;
                default:
                    PhosphorToast.Show("Wiz Mouse Macro", $"Triggered {triggerId}: {match?.ActionLabel ?? action}", "WIZ // MACRO", 2000);
                    break;
            }
        }, null);
    }

    private void ToggleOrbFromHotkey()
    {
        var pt = GetCursorPoint();
        if (!_orb.IsActive)
        {
            _orb.OpenAt(pt);
        }
        else
        {
            var chosen = _orb.CloseAndResolve();
            if (chosen != null)
            {
                ExecuteOrbSlotOnUiThread(chosen, pt);
            }
        }
    }

    private void ToggleGhostTrailerFromHotkey()
    {
        var pt = GetCursorPoint();
        if (!_ghostTrailer.IsStreaming)
        {
            _ghostTrailer.BeginStream(pt, _currentRole.ToString(), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            // Alt+V is held back until speech capture lands, so F16 is the one way to stop it.
            PhosphorToast.Show("GhostTrailer on", "Streaming the ribbon. F16 stops it.", "GHOSTTRAILER // F16", 2400, ToastTone.Info);
        }
        else
        {
            CompleteGhostTrailerOnUiThread();
        }
    }

    private void CycleProfileFromHotkey()
    {
        string next = _ark.CycleCursorProfile();
        PhosphorToast.Show("Cursor profile", $"Active Input Studio profile: {next}", "OMNIWAND", 1800);
    }

    private void ExecuteOrbSlotOnUiThread(GrimXOrbSlot slot, Point pt)
    {
        _uiSync.Post(async _ =>
        {
            if (slot.Id == "crafting")
            {
                _wizWidget.SummonAt(pt, "flow");
                return;
            }
            if (slot.Id == "ainbox")
            {
                _ainboxWidget.SummonAt(pt);
                return;
            }

            // Slots 1-6: GrimX Spell kinds (Quick Cast, Ritual, Symphony, Ultima, Charm, Summon).
            // A cast reports in a toast; it never opens Wiz (Wiz is slot 7 only).
            _isAiThinking = true;
            try
            {
                var (outcome, message) = await _ark.TriggerGrimXFlowAsync(slot.Id, slot.Verb, pt, _currentRole.ToString());
                string spell = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot.Title.ToLowerInvariant());
                string eyebrow = $"GRIMX SPELL // {slot.Verb}";
                switch (outcome)
                {
                    case SpellCastOutcome.Cast:
                        PhosphorToast.Show($"Cast: {spell}", message, eyebrow, 2400, ToastTone.Success);
                        break;
                    case SpellCastOutcome.NoBinding:
                    case SpellCastOutcome.Refused:
                        PhosphorToast.Show($"{spell} not cast", message, eyebrow, 3600, ToastTone.Warning);
                        break;
                    case SpellCastOutcome.Unreachable:
                        PhosphorToast.Show("Input Spine unreachable", message, eyebrow, 4200, ToastTone.Error);
                        break;
                    default:
                        // Standalone build: no local Spell runtime, so Wiz crafts the Spell on Nemotron instead.
                        PhosphorToast.Show($"{spell} opened in Wiz", "No local Spell runtime found, so Wiz will craft this Spell with Nemotron.", eyebrow, 2600, ToastTone.Info);
                        _wizWidget.SummonAt(pt, slot.Id);
                        break;
                }
            }
            finally
            {
                _isAiThinking = false;
            }
        }, null);
    }

    private void CompleteGhostTrailerOnUiThread()
    {
        var (wasDictation, transcript, origin, role) = _ghostTrailer.EndStream();
        if (string.IsNullOrWhiteSpace(transcript)) return;

        _uiSync.Post(async _ =>
        {
            if (wasDictation)
            {
                try
                {
                    Clipboard.SetText(transcript);
                    SendKeys.SendWait("^v");
                }
                catch
                {
                    // Ignore clipboard contention
                }
            }
            else
            {
                _isAiThinking = true;
                try
                {
                    string? snap = ArkStationClient.CaptureRegionAroundCursor(origin.X, origin.Y);
                    await _ark.AskArkGatewayAsync($"[GhostTrailer Voice-Wand over {role}]: {transcript}", snap);
                }
                finally
                {
                    _isAiThinking = false;
                }
            }
        }, null);
    }

    private void AddVrModeOption(ToolStripMenuItem parent, string label, string detail, VrStreamMode mode, bool isDefault)
    {
        var item = new ToolStripMenuItem(label) { Checked = isDefault, Tag = "vrmode", ToolTipText = detail };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>().Where(c => Equals(c.Tag, "vrmode")))
                child.Checked = false;
            item.Checked = true;
            _vrMode = mode;
            VrTick();
        };
        parent.DropDownItems.Add(item);
    }

    private void AddArrowStyleOption(ToolStripMenuItem parent, string label, string detail, ArrowSlotStyle style, bool isDefault)
    {
        var item = new ToolStripMenuItem(label) { Checked = isDefault, Tag = "arrowstyle", ToolTipText = detail };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>().Where(c => Equals(c.Tag, "arrowstyle")))
                child.Checked = false;
            item.Checked = true;
            NativeCursorManager.ArrowStyle = style;
            NativeCursorManager.HideSystemCursors(force: true);
            VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
            _overlay.PulseCursorHandleForVr(_uiSync);
        };
        parent.DropDownItems.Add(item);
    }

    private System.Threading.Timer? _pointerDiagTimer;
    private string _lastPointerDiag = "";

    /// <summary>Appends a line to %TEMP%\omniwand-pointer.log whenever what Windows reports for the pointer changes.</summary>
    private void SetPointerDiagnostics(bool on)
    {
        _pointerDiagTimer?.Dispose();
        _pointerDiagTimer = null;
        if (!on) return;

        string path = Path.Combine(Path.GetTempPath(), "omniwand-pointer.log");
        _pointerDiagTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                string snap = NativeCursorManager.Snapshot();
                // Position changes every tick; compare the rest.
                string key = System.Text.RegularExpressions.Regex.Replace(snap, @"pt=\([^)]*\) ", "");
                if (key == _lastPointerDiag) return;
                _lastPointerDiag = key;
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} vr={_vrStreamName ?? "none"} {snap}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never disturb the cursor loop.
            }
        }, null, 0, 150);
    }

    /// <summary>
    /// Adapts to an active headset stream: re-applies the invisible system cursor, flushes any stale
    /// OculusDash [ReactSystem + 0x218] HCURSOR texture cache, and enlarges the wand for VR legibility.
    /// </summary>
    private void VrTick()
    {
        if (!_running) return;

        string? stream = _vrMode switch
        {
            VrStreamMode.Off => null,
            VrStreamMode.AlwaysOn => VrStreamDetector.Detect() ?? "Forced VR mode",
            _ => VrStreamDetector.Detect()
        };

        bool wasActive = _vrStreamName != null;
        bool active = stream != null;

        if (active)
        {
            if (!wasActive)
            {
                _preVrScaleMultiplier = _baseScaleMultiplier;
                _baseScaleMultiplier = Math.Max(_baseScaleMultiplier, VrScaleBoost);
                NativeCursorManager.HideSystemCursors(force: true);
                VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
                _overlay.PulseCursorHandleForVr(_uiSync);
            }
            else if (NativeCursorManager.IsHidden && VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: false))
            {
                NativeCursorManager.HideSystemCursors(force: true);
                VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
                _overlay.PulseCursorHandleForVr(_uiSync);
            }
        }
        else if (wasActive)
        {
            _baseScaleMultiplier = _preVrScaleMultiplier;
        }

        if (stream != _vrStreamName)
        {
            _vrStreamName = stream;
            string text = stream != null ? $"Streaming via {stream}" : "Not streaming";
            _uiSync.Post(_ =>
            {
                if (_vrStatusItem != null) _vrStatusItem.Text = text;
                if (stream != null)
                    PhosphorToast.Show("VR mode", $"{stream} detected. Native cursor hidden, wand enlarged.", "OMNIWAND // VR", 3000);
            }, null);
        }
    }

    private void AddCursorStyleOption(ToolStripMenuItem parent, string label, string tagText, CursorIconStyle style, bool isDefault)
    {
        var item = new ToolStripMenuItem(label) { Checked = isDefault, ShortcutKeyDisplayString = tagText };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>())
                child.Checked = false;
            item.Checked = true;
            MouseHardwareAndWandEngine.ActiveCursorStyle = style;
            _overlay.ForceCursorRedraw();
        };
        parent.DropDownItems.Add(item);
    }

    private void AddMorphModeOption(ToolStripMenuItem parent, string label, string tagText, ApertureMorphMode mode, bool isDefault)
    {
        var item = new ToolStripMenuItem(label) { Checked = isDefault, ShortcutKeyDisplayString = tagText };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (child.ShortcutKeyDisplayString != "DEMO")
                    child.Checked = false;
            }
            item.Checked = true;
            ArkApertureMorphEngine.ActiveMorphMode = mode;
            _overlay.ForceCursorRedraw();
            PhosphorToast.Show("Aperture Morph Mode", $"Active morph formation: {label}", "ARK APERTURE // MORPH", 2200);
        };
        parent.DropDownItems.Add(item);
    }

    private void AddDpiPresetOption(ToolStripMenuItem parent, string label, int speed1To20)
    {
        var item = new ToolStripMenuItem(label) { ShortcutKeyDisplayString = $"{speed1To20}/20" };
        item.Click += (_, _) =>
        {
            MouseHardwareAndWandEngine.SetWindowsMouseSpeed(speed1To20);
            var hw = MouseHardwareAndWandEngine.DetectConnectedMouse();
            PhosphorToast.Show("Mouse DPI Updated", $"{hw.Brand} {hw.Model}: speed {speed1To20}/20 (~{hw.EstimatedDpi} DPI).", "DEVICE // DPI", 2400);
        };
        parent.DropDownItems.Add(item);
    }

    private void AddModeOption(ToolStripMenuItem parent, string label, string detail, RotationMode mode, bool isDefault)
    {
        var item = new RotationPreviewItem(label, mode) { Checked = isDefault, ToolTipText = detail };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>())
                child.Checked = false;
            item.Checked = true;
            _rotationMode = mode;
        };
        parent.DropDownItems.Add(item);
    }

    private void AddScaleOption(ToolStripMenuItem parent, string label, string factor, float mult, bool isDefault)
    {
        var item = new ToolStripMenuItem(label) { Checked = isDefault, ShortcutKeyDisplayString = factor };
        item.Click += (_, _) =>
        {
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>())
                child.Checked = false;
            item.Checked = true;
            _baseScaleMultiplier = mult;
        };
        parent.DropDownItems.Add(item);
    }

    private void ToggleNativeCursor()
    {
        if (NativeCursorManager.IsHidden)
        {
            NativeCursorManager.RestoreSystemCursors();
            _hideNativeMenuItem.Checked = false;
            PhosphorToast.Show("Windows cursor shown", "Native cursor restored. Ctrl+Alt+C hides it again.", "OMNIWAND // CURSOR", 2400, ToastTone.Info);
        }
        else
        {
            NativeCursorManager.HideSystemCursors();
            _hideNativeMenuItem.Checked = true;
        }
    }

    /// <summary>
    /// Detects the contextual cursor role at the pointer (IBeam for edit/text controls,
    /// Hand for links/buttons, or stock cursor handle match).
    /// </summary>
    private static WindowsCursorRole DetectContextualCursorRole(Point pt)
    {
        var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
        if (Win32.GetCursorInfo(ref ci) && ci.hCursor != IntPtr.Zero)
        {
            if (NativeCursorManager.TryResolveRoleFromHandle(ci.hCursor, out var matchedRole))
            {
                return matchedRole;
            }
        }

        // Inspect window class under cursor when system cursors are replaced with transparent handles
        IntPtr hwnd = Win32.WindowFromPoint(new Win32.POINT { X = pt.X, Y = pt.Y });
        if (hwnd != IntPtr.Zero)
        {
            var sb = new StringBuilder(64);
            if (Win32.GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Scintilla", StringComparison.OrdinalIgnoreCase))
                {
                    return WindowsCursorRole.IBeam;
                }
                if (cls.Contains("SysLink", StringComparison.OrdinalIgnoreCase))
                {
                    return WindowsCursorRole.Hand;
                }
            }
        }

        return WindowsCursorRole.Arrow;
    }

    private void RenderLoop()
    {
        var clock = Stopwatch.StartNew();
        double lastTimeSec = clock.Elapsed.TotalSeconds;

        const double TetherLengthPx = 24.0;
        const double DeadzonePx = 4.0;

        while (_running)
        {
            int hr = Win32.DwmFlush();
            if (hr != 0)
            {
                Thread.Sleep(4);
            }

            if (!_running) break;

            double nowSec = clock.Elapsed.TotalSeconds;
            double dtSec = nowSec - lastTimeSec;
            lastTimeSec = nowSec;
            if (dtSec <= 0.0001) continue;

            Win32.GetCursorPos(out Win32.POINT pt);
            var cursorPt = new Point(pt.X, pt.Y);
            _cursorX.Target = pt.X;
            _cursorY.Target = pt.Y;

            // Poll cursor role every 4 frames (~240Hz -> 60Hz) for zero overhead
            if ((_rolePollCounter++ & 3) == 0)
            {
                // Thinking reaches the morph separately (coral globe) and switches rotation to True path below.
                // Forcing AppStarting here tripped the role rotation lock, so Wiz never got True path.
                var detectedRole = DetectContextualCursorRole(cursorPt);
                _currentRole = detectedRole;
                _roleMorph.Target = detectedRole == WindowsCursorRole.Arrow ? 0.0 : 1.0;
            }

            // Check hardware / mouse triggers for the Spell Orb (F15 = 0x7E or Middle Mouse Hold > 180ms)
            bool f15Down = (Win32.GetAsyncKeyState(0x7E) & 0x8000) != 0;
            bool mmbDown = (Win32.GetAsyncKeyState(0x04) & 0x8000) != 0;
            if (mmbDown && _middleMouseDownStartSec < 0)
            {
                _middleMouseDownStartSec = nowSec;
            }
            else if (!mmbDown)
            {
                _middleMouseDownStartSec = -1;
            }
            bool mmbHoldActive = mmbDown && (nowSec - _middleMouseDownStartSec) >= 0.18;
            bool orbTriggerDown = f15Down || mmbHoldActive;

            if (orbTriggerDown && !_wasOrbTriggerDown && !_orb.IsActive)
            {
                _orb.OpenAt(cursorPt);
            }
            else if (!orbTriggerDown && _wasOrbTriggerDown && _orb.IsActive)
            {
                var chosen = _orb.CloseAndResolve();
                if (chosen != null)
                {
                    ExecuteOrbSlotOnUiThread(chosen, cursorPt);
                }
            }
            _wasOrbTriggerDown = orbTriggerDown;

            // Check hardware trigger for GhostTrailer PTT (F16 = 0x7F)
            bool f16Down = (Win32.GetAsyncKeyState(0x7F) & 0x8000) != 0;
            if (f16Down && !_wasGhostTriggerDown && !_ghostTrailer.IsStreaming)
            {
                _ghostTrailer.BeginStream(cursorPt, _currentRole.ToString(), nowSec);
            }
            else if (!f16Down && _wasGhostTriggerDown && _ghostTrailer.IsStreaming)
            {
                CompleteGhostTrailerOnUiThread();
            }
            _wasGhostTriggerDown = f16Down;

            // Check Mouse4 (XButton1 = 0x05), Mouse5 (XButton2 = 0x06), and Right-Hold Gesture Flicks.
            // Off by default: the side buttons are browser Back/Forward. Opt in with OMNIWAND_MOUSE_MACROS=1.
            bool mouse4Down = HardwareMacrosEnabled && (Win32.GetAsyncKeyState(0x05) & 0x8000) != 0;
            if (mouse4Down && !_wasMouse4Down)
            {
                ExecuteMouseMacroTrigger("Mouse4", cursorPt);
            }
            _wasMouse4Down = mouse4Down;

            bool mouse5Down = HardwareMacrosEnabled && (Win32.GetAsyncKeyState(0x06) & 0x8000) != 0;
            if (mouse5Down && !_wasMouse5Down)
            {
                ExecuteMouseMacroTrigger("Mouse5", cursorPt);
            }
            _wasMouse5Down = mouse5Down;

            bool rmbDown = (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
            bool ctrlDown = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
            if (OmniwandSpellGestureEngine.Step(cursorPt, nowSec, dtSec, ctrlDown, rmbDown, out var spellGesture))
            {
                _rightDownStartSec = -1;
                ExecuteWandSpellGesture(spellGesture, cursorPt);
            }
            if (rmbDown && !_wasRightMouseDown)
            {
                _rightGestureAnchor = cursorPt;
                _rightDownStartSec = nowSec;
            }
            else if (!rmbDown && _wasRightMouseDown && _rightDownStartSec > 0)
            {
                double heldSec = nowSec - _rightDownStartSec;
                int gdx = cursorPt.X - _rightGestureAnchor.X;
                int gdy = cursorPt.Y - _rightGestureAnchor.Y;
                double gdist = Math.Sqrt(gdx * gdx + gdy * gdy);
                if (HardwareMacrosEnabled && heldSec >= 0.16 && gdist >= 88.0)
                {
                    string gestureTrigger = Math.Abs(gdx) > Math.Abs(gdy)
                        ? (gdx > 0 ? "GestureEast" : "GestureWest")
                        : (gdy < 0 ? "GestureNorth" : "GestureSouth");
                    ExecuteMouseMacroTrigger(gestureTrigger, cursorPt);
                }
                _rightDownStartSec = -1;
            }
            _wasRightMouseDown = rmbDown;

            _cursorX.Step(dtSec);
            _cursorY.Step(dtSec);
            _roleMorph.Step(dtSec);

            double tipX = _cursorX.Current;
            double tipY = _cursorY.Current;

            _orb.UpdateCursor(cursorPt, dtSec, out _);
            if (_ghostTrailer.IsStreaming)
            {
                _ghostTrailer.StepRibbon(new PointF((float)tipX, (float)tipY), dtSec);
            }

            double dx = tipX - _trailX;
            double dy = tipY - _trailY;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // Lock rotation automatically on non-Arrow / non-Hand roles so IBeam, Crosshair, and Resize never tilt.
            // Keep operator cursor on SubtleBank by default, and switch to TruePathTrailer when Wiz / AI is active (_isAiThinking).
            bool lockRoleRotation = _currentRole != WindowsCursorRole.Arrow && _currentRole != WindowsCursorRole.Hand;
            RotationMode activeRotation = _isAiThinking ? RotationMode.TruePathTrailer : _rotationMode;

            if (!lockRoleRotation && activeRotation == RotationMode.TruePathTrailer)
            {
                if (dist > DeadzonePx)
                {
                    double nx = dx / dist;
                    double ny = dy / dist;

                    double pullWeight = Math.Clamp((dist - DeadzonePx) / (TetherLengthPx - DeadzonePx), 0.0, 1.0);
                    _targetDirX += (nx - _targetDirX) * pullWeight;
                    _targetDirY += (ny - _targetDirY) * pullWeight;
                    double tLen = Math.Sqrt(_targetDirX * _targetDirX + _targetDirY * _targetDirY);
                    if (tLen > 1e-6)
                    {
                        _targetDirX /= tLen;
                        _targetDirY /= tLen;
                    }

                    if (dist > TetherLengthPx)
                    {
                        _trailX = tipX - nx * TetherLengthPx;
                        _trailY = tipY - ny * TetherLengthPx;
                    }

                    _scale.Target = 0.96;
                    _lastActiveMoveTimeSec = nowSec;
                }
                else if (nowSec - _lastActiveMoveTimeSec > 0.12 && !_wasMouseDown)
                {
                    _scale.Target = 1.0;
                }
            }
            else if (!lockRoleRotation && activeRotation == RotationMode.SubtleBank)
            {
                double bankRad = (-22.5 + Math.Clamp(_cursorX.Velocity * 0.018, -28.0, 28.0) - 90.0) * (Math.PI / 180.0);
                _targetDirX = Math.Cos(bankRad);
                _targetDirY = Math.Sin(bankRad);
                _trailX = tipX;
                _trailY = tipY;
            }
            else
            {
                double lockedAngle = lockRoleRotation ? 0.0 : -22.5;
                double lockedRad = (lockedAngle - 90.0) * (Math.PI / 180.0);
                _targetDirX = Math.Cos(lockedRad);
                _targetDirY = Math.Sin(lockedRad);
                _trailX = tipX;
                _trailY = tipY;
            }

            double dirAlpha = 1.0 - Math.Exp(-dtSec * 22.0);
            _dirX += (_targetDirX - _dirX) * dirAlpha;
            _dirY += (_targetDirY - _dirY) * dirAlpha;
            double dirLen = Math.Sqrt(_dirX * _dirX + _dirY * _dirY);
            if (dirLen > 1e-6)
            {
                _dirX /= dirLen;
                _dirY /= dirLen;
            }
            else
            {
                _dirX = 0.0;
                _dirY = -1.0;
            }

            float angleDeg = (float)(Math.Atan2(_dirY, _dirX) * (180.0 / Math.PI) + 90.0);
            _spinAngleDeg = (_spinAngleDeg + (float)(dtSec * 320.0)) % 360f;

            bool isMouseDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (isMouseDown)
            {
                _scale.Target = 0.86;
                _wasMouseDown = true;
            }
            else if (_wasMouseDown)
            {
                _wasMouseDown = false;
                _scale.Target = 1.0;
            }

            _scale.Step(dtSec);
            ArkApertureMorphEngine.Step(dtSec, _currentRole, _isAiThinking, _spinAngleDeg);

            if (_zGuard.Tick(_overlay.Handle, cursorPt.X, cursorPt.Y, nowSec))
            {
                if (_zGuard.Covered) NativeCursorManager.SuspendForCoveredUi();
                else NativeCursorManager.ResumeAfterCoveredUi();
                _overlay.SetSuppressed(_zGuard.Covered);
            }

            _overlay.RenderAndMove(
                tipX,
                tipY,
                angleDeg,
                (float)(_scale.Current * _baseScaleMultiplier),
                _isAiThinking,
                _currentRole,
                (float)_roleMorph.Current,
                _spinAngleDeg,
                nowSec);
        }
    }

    private void ExitApp()
    {
        _running = false;
        _vrTimer?.Dispose();
        NativeCursorManager.RestoreSystemCursors();
        MouseHardwareAndWandEngine.RestoreOriginalSpeed();
        _trayIcon.Visible = false;
        try { _renderThread.Join(300); } catch { }
        _hud.Dispose();
        _wizWidget.Dispose();
        _ainboxWidget.Dispose();
        _snipWidget.Dispose();
        _holodexDrawer.Dispose();
        _tutorialTooltip.Dispose();
        _overlay.Dispose();
        Win32.timeEndPeriod(1);
        Application.Exit();
    }

    /// <summary>
    /// Tray icon: the aperture favicon with no background (operator request 2026-10-16), drawn at the
    /// system's small-icon size so the 16px drawing is used at 100% scale and the 32px one above it.
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            ArkEmblem.DrawAperture(g, new RectangleF(0, 0, size, size), solidGlobe: true);
        }
        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}

/// <summary>
/// Exact vector paths from @magicui/smooth-cursor DefaultCursorSVG (viewBox 0 0 50 54)
/// plus parametric 14-role Input Studio shapes (smooth-cursor.json).
/// </summary>
internal static class MagicUiCursorPaths
{
    public static GraphicsPath CreateFillPath()
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(42.6817f, 41.1495f, 27.5103f, 6.79925f);
        path.AddBezier(27.5103f, 6.79925f, 26.7269f, 5.02557f, 24.2082f, 5.02558f, 23.3927f, 6.79925f);
        path.AddLine(23.3927f, 6.79925f, 7.59814f, 41.1495f);
        path.AddBezier(7.59814f, 41.1495f, 6.75833f, 42.9759f, 8.52712f, 44.8902f, 10.4125f, 44.1954f);
        path.AddLine(10.4125f, 44.1954f, 24.3757f, 39.0496f);
        path.AddBezier(24.3757f, 39.0496f, 24.8829f, 38.8627f, 25.4385f, 38.8627f, 25.9422f, 39.0496f);
        path.AddLine(25.9422f, 39.0496f, 39.8121f, 44.1954f);
        path.AddBezier(39.8121f, 44.1954f, 41.6849f, 44.8902f, 43.4884f, 42.9759f, 42.6817f, 41.1495f);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath CreateStrokePath()
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(43.7146f, 40.6933f, 28.5431f, 6.34306f);
        path.AddBezier(28.5431f, 6.34306f, 27.3556f, 3.65428f, 23.5772f, 3.69516f, 22.3668f, 6.32755f);
        path.AddLine(22.3668f, 6.32755f, 6.57226f, 40.6778f);
        path.AddBezier(6.57226f, 40.6778f, 5.3134f, 43.4156f, 7.97238f, 46.298f, 10.803f, 45.2549f);
        path.AddLine(10.803f, 45.2549f, 24.7662f, 40.109f);
        path.AddBezier(24.7662f, 40.109f, 25.0221f, 40.0147f, 25.2999f, 40.0156f, 25.5494f, 40.1082f);
        path.AddLine(25.5494f, 40.1082f, 39.4193f, 45.254f);
        path.AddBezier(39.4193f, 45.254f, 42.2261f, 46.2953f, 44.9254f, 43.4347f, 43.7146f, 40.6933f);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Draws parametric non-Arrow Windows cursor roles in Relic Phosphor Shell tokens with state-reactive globes.
    /// </summary>
    public static void DrawParametricRole(
        Graphics g,
        WindowsCursorRole role,
        float scale,
        float spinAngleDeg,
        bool isAiThinking = false)
    {
        using var shadowPen = new Pen(Color.FromArgb(155, PhosphorTheme.Ground), 4.2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var bonePen = new Pen(PhosphorTheme.EmblemCream, 2.15f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var patinaPen = new Pen(isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 2.2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        Color globeColor = isAiThinking
            ? PhosphorTheme.Coral
            : role switch
            {
                WindowsCursorRole.No => PhosphorTheme.Rust,
                WindowsCursorRole.Wait or WindowsCursorRole.AppStarting or WindowsCursorRole.Crosshair => PhosphorTheme.Coral,
                _ => PhosphorTheme.Patina
            };

        void DrawCenterGlobe(float rx, float ry)
        {
            using var outline = new Pen(PhosphorTheme.Ground, 1.5f * scale);
            using var fill = new SolidBrush(globeColor);
            g.DrawEllipse(outline, -rx, -ry, rx * 2f, ry * 2f);
            g.FillEllipse(fill, -rx, -ry, rx * 2f, ry * 2f);
        }

        switch (role)
        {
            case WindowsCursorRole.IBeam:
            {
                // Solid, basic vertical beam bar glowing Patina (steady, non-pulsing).
                float h = 11.2f * scale;
                using var outerGlow = new Pen(Color.FromArgb(46, PhosphorTheme.Patina), 8.2f * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                using var midGlow = new Pen(Color.FromArgb(118, PhosphorTheme.Patina), 4.6f * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                using var coreBar = new Pen(PhosphorTheme.Patina, 2.5f * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                using var centerHighlight = new Pen(Color.FromArgb(185, PhosphorTheme.EmblemCream), 1.0f * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(outerGlow, 0, -h, 0, h);
                g.DrawLine(midGlow, 0, -h, 0, h);
                g.DrawLine(shadowPen, 0, -h, 0, h);
                g.DrawLine(coreBar, 0, -h, 0, h);
                g.DrawLine(centerHighlight, 0, -h * 0.82f, 0, h * 0.82f);
                break;
            }
            case WindowsCursorRole.Crosshair:
            {
                float len = 11.5f * scale;
                float gap = 3.8f * scale;
                foreach (var pen in new[] { shadowPen, bonePen })
                {
                    g.DrawLine(pen, -len, 0, -gap, 0);
                    g.DrawLine(pen, gap, 0, len, 0);
                    g.DrawLine(pen, 0, -len, 0, -gap);
                    g.DrawLine(pen, 0, gap, 0, len);
                }
                DrawCenterGlobe(2.5f * scale, 2.5f * scale);
                break;
            }
            case WindowsCursorRole.Wait:
            case WindowsCursorRole.AppStarting:
            {
                float r = 9.5f * scale;
                var rect = new RectangleF(-r, -r, r * 2f, r * 2f);
                g.DrawEllipse(shadowPen, rect);
                using var trackPen = new Pen(PhosphorTheme.Line2, 2.0f * scale);
                g.DrawEllipse(trackPen, rect);
                g.DrawArc(patinaPen, rect, spinAngleDeg, 250f);
                DrawCenterGlobe(4.2f * scale, 4.2f * scale);
                break;
            }
            case WindowsCursorRole.No:
            {
                float r = 9.5f * scale;
                using var rustPen = new Pen(PhosphorTheme.Rust, 2.3f * scale);
                var rect = new RectangleF(-r, -r, r * 2f, r * 2f);
                g.DrawEllipse(shadowPen, rect);
                g.DrawEllipse(rustPen, rect);
                DrawCenterGlobe(3.8f * scale, 3.8f * scale);
                float d = r * 0.707f;
                g.DrawLine(rustPen, -d, -d, d, d);
                break;
            }
            case WindowsCursorRole.SizeNS:
            case WindowsCursorRole.SizeWE:
            case WindowsCursorRole.SizeNWSE:
            case WindowsCursorRole.SizeNESW:
            {
                float angle = role switch
                {
                    WindowsCursorRole.SizeNS => 90f,
                    WindowsCursorRole.SizeWE => 0f,
                    WindowsCursorRole.SizeNWSE => 45f,
                    _ => 135f
                };
                var st = g.Save();
                g.RotateTransform(angle);
                float len = 10.5f * scale;
                float head = 4.2f * scale;
                foreach (var pen in new[] { shadowPen, bonePen })
                {
                    g.DrawLine(pen, -len, 0, len, 0);
                    g.DrawLine(pen, -len + head, -head, -len, 0);
                    g.DrawLine(pen, -len + head, head, -len, 0);
                    g.DrawLine(pen, len - head, -head, len, 0);
                    g.DrawLine(pen, len - head, head, len, 0);
                }
                g.Restore(st);
                DrawCenterGlobe(3.4f * scale, 3.4f * scale);
                break;
            }
            case WindowsCursorRole.SizeAll:
            {
                float len = 11.0f * scale;
                foreach (var pen in new[] { shadowPen, bonePen })
                {
                    g.DrawLine(pen, -len, 0, len, 0);
                    g.DrawLine(pen, 0, -len, 0, len);
                }
                DrawCenterGlobe(3.6f * scale, 3.6f * scale);
                break;
            }
        }
    }
}

/// <summary>
/// Persistent 32-bit DIB section click-through layered window.
/// Renders the Smooth Cursor, 14-role parametric morphs, 8-slot Spell Orb, and GhostTrailer ribbon.
/// </summary>
internal sealed class CursorOverlayWindow : NativeWindow, IDisposable
{
    private const int CanvasSize = 440;
    private const int HotkeySummonId = 9000;
    private const int HotkeyToggleId = 9001;
    private const int HotkeyQuitId = 9002;
    private const int HotkeyWizId = 9003;
    private const int HotkeyAInboxId = 9004;
    private const int HotkeyOrbId = 9005;
    private const int HotkeyGhostId = 9006;
    private const int HotkeyCycleProfileId = 9007;
    private const int HotkeyHolodexId = 9008;
    private const int HotkeySnipLassoId = 9009;
    private const int HotkeyElementPickerId = 9010;
    private const int HotkeyTutorialId = 9011;

    private readonly GrimXFlowOrb _orb;
    private readonly GhostTrailerEngine _ghostTrailer;
    private readonly GraphicsPath _fillPath = MagicUiCursorPaths.CreateFillPath();
    private readonly GraphicsPath _strokePath = MagicUiCursorPaths.CreateStrokePath();
    private readonly Pen _boneStrokePen = new(PhosphorTheme.Bone, 2.25825f) { LineJoin = LineJoin.Round };
    private readonly Pen _patinaStrokePen = new(PhosphorTheme.Patina, 2.6f) { LineJoin = LineJoin.Round };
    private readonly Pen _coralStrokePen = new(PhosphorTheme.Coral, 2.5f) { LineJoin = LineJoin.Round };
    private readonly SolidBrush _groundFillBrush = new(PhosphorTheme.Ground);
    private readonly SolidBrush _shadowBrush1 = new(Color.FromArgb(24, 0, 0, 0));
    private readonly SolidBrush _shadowBrush2 = new(Color.FromArgb(38, 0, 0, 0));

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _hDib;
    private readonly IntPtr _oldBitmap;
    private readonly Bitmap _dibWrapper;

    private Action? _onSummonHotkey;
    private Action? _onToggleHotkey;
    private Action? _onQuitHotkey;
    private Action? _onOpenWizHotkey;
    private Action? _onOpenAInboxHotkey;
    private Action? _onToggleOrbHotkey;
    private Action? _onToggleGhostHotkey;
    private Action? _onCycleProfileHotkey;
    private Action? _onOpenHolodexHotkey;
    private Action? _onOpenSnipLassoHotkey;
    private Action? _onOpenElementPickerHotkey;
    private Action? _onOpenTutorialHotkey;

    private int _lastPixelX = int.MinValue;
    private int _lastPixelY = int.MinValue;
    private float _lastAngle = float.MinValue;
    private float _lastScale = float.MinValue;
    private bool _lastThinking;
    private WindowsCursorRole _lastRole = WindowsCursorRole.Arrow;
    private int _lastOrbSlot = -2;
    private bool _lastOrbVisible;
    private bool _lastGhostStreaming;
    private float _cachedDpiScale = 1.25f;
    private int _dpiCheckCounter;

    public CursorOverlayWindow(GrimXFlowOrb orb, GhostTrailerEngine ghostTrailer)
    {
        _orb = orb;
        _ghostTrailer = ghostTrailer;

        var cp = new CreateParams
        {
            Caption = "OmniwandSmoothCursorOverlay",
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle =
                0x00080000 | // WS_EX_LAYERED
                0x00000020 | // WS_EX_TRANSPARENT
                0x00000008 | // WS_EX_TOPMOST
                0x00000080 | // WS_EX_TOOLWINDOW
                0x08000000,  // WS_EX_NOACTIVATE
            X = 0,
            Y = 0,
            Width = CanvasSize,
            Height = CanvasSize
        };

        CreateHandle(cp);
        Win32.ShowWindow(Handle, 8); // SW_SHOWNA

        _screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(_screenDc);

        var bmi = new Win32.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = CanvasSize;
        bmi.bmiHeader.biHeight = -CanvasSize; // Top-down DIB
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        _hDib = Win32.CreateDIBSection(_screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_memDc, _hDib);
        _dibWrapper = new Bitmap(CanvasSize, CanvasSize, CanvasSize * 4, PixelFormat.Format32bppPArgb, bits);
    }

    public void RegisterHotkeys(
        Action onSummonHud,
        Action onToggleNative,
        Action onQuit,
        Action onOpenWiz,
        Action onOpenAInbox,
        Action onToggleOrb,
        Action onToggleGhostTrailer,
        Action onCycleProfile,
        Action onOpenHolodex,
        Action onOpenSnipLasso,
        Action onOpenElementPicker,
        Action onOpenTutorial)
    {
        _onSummonHotkey = onSummonHud;
        _onToggleHotkey = onToggleNative;
        _onQuitHotkey = onQuit;
        _onOpenWizHotkey = onOpenWiz;
        _onOpenAInboxHotkey = onOpenAInbox;
        _onToggleOrbHotkey = onToggleOrb;
        _onToggleGhostHotkey = onToggleGhostTrailer;
        _onCycleProfileHotkey = onCycleProfile;
        _onOpenHolodexHotkey = onOpenHolodex;
        _onOpenSnipLassoHotkey = onOpenSnipLasso;
        _onOpenElementPickerHotkey = onOpenElementPicker;
        _onOpenTutorialHotkey = onOpenTutorial;

        // Each shortcut is registered through Bind so one another app already owns is reported, not silently dead.
        var taken = new List<string>();
        void Bind(int id, uint mods, Keys key)
        {
            if (Win32.RegisterHotKey(Handle, id, mods, (int)key)) return;
            string label = ((mods & 0x0002) != 0 ? "Ctrl+" : "") + ((mods & 0x0004) != 0 ? "Shift+" : "") + ((mods & 0x0001) != 0 ? "Alt+" : "") + key;
            taken.Add(label);
        }

        // Alt+Space -> Phosphor Command HUD
        Bind(HotkeySummonId, 0x0001 | 0x4000, Keys.Space);
        // Ctrl+Alt+C -> Toggle Native Cursor
        Bind(HotkeyToggleId, 0x0002 | 0x0001 | 0x4000, Keys.C);
        // Ctrl+Alt+Q -> Quit & Restore
        Bind(HotkeyQuitId, 0x0002 | 0x0001 | 0x4000, Keys.Q);
        // Alt+W -> Slot 7 Wiz Crafting Chatbot (Gemini Argon)
        Bind(HotkeyWizId, 0x0001 | 0x4000, Keys.W);
        // Alt+I -> Slot 8 AInbox Pod Intake + Handoff Scroll
        Bind(HotkeyAInboxId, 0x0001 | 0x4000, Keys.I);
        // Ctrl+Shift+Space -> 8-slot Spell Orb
        Bind(HotkeyOrbId, 0x0002 | 0x0004 | 0x4000, Keys.Space);
        // Alt+V (GhostTrailer dictation) is held back until speech capture lands; no hotkey in v0.3.
        // Alt+C -> Cycle Input Studio Cursor Icon Style
        Bind(HotkeyCycleProfileId, 0x0001 | 0x4000, Keys.C);
        // Alt+H -> Holodex Lite Drawer (Session, AInbox, Codex, GrimX, Mods)
        Bind(HotkeyHolodexId, 0x0001 | 0x4000, Keys.H);
        // Alt+S -> Free-Form Lasso & Phosphor Annotator
        Bind(HotkeySnipLassoId, 0x0001 | 0x4000, Keys.S);
        // Alt+E -> Desktop Element & Pixel Palette Picker
        Bind(HotkeyElementPickerId, 0x0001 | 0x4000, Keys.E);
        // Alt+T -> Animated Phosphor Tooltip Tutorials
        Bind(HotkeyTutorialId, 0x0001 | 0x4000, Keys.T);
        if (taken.Count > 0)
            PhosphorToast.Show(
                "Some shortcuts are taken",
                $"Another app already uses {string.Join(", ", taken)}. Those tools are still in the tray menu.",
                "OMNIWAND // SHORTCUTS",
                7000,
                ToastTone.Warning);
    }

    public void ForceCursorRedraw()
    {
        _lastPixelX = int.MinValue;
        _lastAngle = float.MinValue;
    }

    private volatile bool _suppressed;
    // The render loop's own clock. Spell ribbon nodes are stamped with it, so the overlay must age them on it too.
    private double _loopNowSec;

    /// <summary>Hides the overlay while a shell surface covers it (the native cursor is back), and restores it.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        // Async so the render thread never waits on the UI thread's message pump.
        Win32.ShowWindowAsync(Handle, suppressed ? 0 /* SW_HIDE */ : 4 /* SW_SHOWNOACTIVATE */);
        if (!suppressed) _lastPixelX = int.MinValue; // force a full redraw at the current spot
    }

    public void RenderAndMove(
        double x,
        double y,
        float angleDeg,
        float scaleMultiplier,
        bool isAiThinking,
        WindowsCursorRole role,
        float roleMorph,
        float spinAngleDeg,
        double loopNowSec)
    {
        if (_suppressed) return;
        _loopNowSec = loopNowSec;

        int targetLeft = (int)Math.Round(x - CanvasSize / 2.0);
        int targetTop = (int)Math.Round(y - CanvasSize / 2.0);

        bool orbVis = _orb.IsVisible;
        bool ghostVis = _ghostTrailer.IsStreaming;
        bool spellVis = OmniwandSpellGestureEngine.HasVisuals;
        bool animatedRole = role is WindowsCursorRole.Wait or WindowsCursorRole.AppStarting;

        bool posChanged = targetLeft != _lastPixelX || targetTop != _lastPixelY;
        bool shapeChanged =
            orbVis ||
            ghostVis ||
            spellVis ||
            _lastOrbVisible != orbVis ||
            _lastGhostStreaming != ghostVis ||
            _orb.ActiveSlotIndex != _lastOrbSlot ||
            animatedRole ||
            ArkApertureMorphEngine.IsAnimating ||
            role != _lastRole ||
            Math.Abs(angleDeg - _lastAngle) > 0.08f ||
            Math.Abs(scaleMultiplier - _lastScale) > 0.003f ||
            isAiThinking != _lastThinking;

        if (!posChanged && !shapeChanged)
        {
            return;
        }

        // Z-order is OverlayZGuard's job (a fixed clock, not only while moving); this block only tracks DPI.
        if ((_dpiCheckCounter++ & 31) == 0)
        {
            _cachedDpiScale = GetDpiScaleAtPoint((int)x, (int)y);
        }

        var topPos = new Win32.POINT { X = targetLeft, Y = targetTop };
        var size = new Win32.SIZE { cx = CanvasSize, cy = CanvasSize };
        var pointSource = new Win32.POINT { X = 0, Y = 0 };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = 0x00,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 0x01
        };

        if (shapeChanged)
        {
            float cx = CanvasSize * 0.5f;
            float cy = CanvasSize * 0.5f;
            float finalScale = 0.5f * _cachedDpiScale * scaleMultiplier;

            using (var g = Graphics.FromImage(_dibWrapper))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 1. Draw GhostTrailer live ribbon if streaming
                if (ghostVis)
                {
                    _ghostTrailer.DrawInOverlay(g, new PointF((float)x, (float)y), cx, cy, _cachedDpiScale);
                }

                // 1b. Draw MagiXLab Omniwand Spell Ribbon & 4-Point Twinkling Star Dust when Ctrl / RMB is held
                if (spellVis)
                {
                    OmniwandSpellGestureEngine.DrawInOverlay(
                        g,
                        new PointF((float)x, (float)y),
                        cx,
                        cy,
                        _loopNowSec,
                        _cachedDpiScale);
                }

                // 2. Draw the 8-slot Spell Orb if open/closing
                if (orbVis)
                {
                    float orbCx = cx + (float)(_orb.AnchorScreenPt.X - x);
                    float orbCy = cy + (float)(_orb.AnchorScreenPt.Y - y);
                    _orb.Draw(g, orbCx, orbCy, _cachedDpiScale);
                }

                // 3. Draw Phosphor Cursor:
                // When ArkAperture is active, ALL 14 Windows cursor states & custom morph formations
                // are rendered by the 240Hz ArkApertureMorphEngine.
                if (MouseHardwareAndWandEngine.ActiveCursorStyle == CursorIconStyle.ArkAperture)
                {
                    ArkApertureMorphEngine.DrawMorphedApertureCursor(
                        g,
                        cx,
                        cy,
                        angleDeg,
                        _cachedDpiScale * scaleMultiplier,
                        isAiThinking,
                        role,
                        spinAngleDeg);
                }
                else
                {
                    bool useArrowGeometry = role is WindowsCursorRole.Arrow or WindowsCursorRole.Hand or WindowsCursorRole.UpArrow or WindowsCursorRole.Help;
                    if (useArrowGeometry)
                    {
                        if (MouseHardwareAndWandEngine.ActiveCursorStyle != CursorIconStyle.PhosphorArrow)
                        {
                            MouseHardwareAndWandEngine.DrawStyledCursor(
                                g,
                                MouseHardwareAndWandEngine.ActiveCursorStyle,
                                cx,
                                cy,
                                angleDeg,
                                _cachedDpiScale * scaleMultiplier,
                                isAiThinking,
                                role,
                                spinAngleDeg);
                        }
                        else
                        {
                            var cursorState = g.Save();
                            g.TranslateTransform(cx, cy);
                            g.RotateTransform(angleDeg);
                            float handScale = role == WindowsCursorRole.Hand ? finalScale * 1.06f : finalScale;
                            g.ScaleTransform(handScale, handScale);
                            g.TranslateTransform(-25.45f, -4.35f);

                            var state = g.Save();
                            g.TranslateTransform(0f, 3.2f);
                            g.FillPath(_shadowBrush1, _strokePath);
                            g.Restore(state);

                            state = g.Save();
                            g.TranslateTransform(0f, 1.8f);
                            g.FillPath(_shadowBrush2, _strokePath);
                            g.Restore(state);

                            g.FillPath(_groundFillBrush, _fillPath);
                            Pen activePen = isAiThinking
                                ? _patinaStrokePen
                                : (role == WindowsCursorRole.Hand ? _coralStrokePen : _boneStrokePen);
                            g.DrawPath(activePen, _strokePath);
                            g.Restore(cursorState);
                        }
                    }
                    else
                    {
                        var roleState = g.Save();
                        g.TranslateTransform(cx, cy);
                        MagicUiCursorPaths.DrawParametricRole(g, role, _cachedDpiScale * scaleMultiplier, spinAngleDeg, isAiThinking);
                        g.Restore(roleState);
                    }
                }
            }

            Win32.GdiFlush();
            Win32.UpdateLayeredWindow(Handle, _screenDc, ref topPos, ref size, _memDc, ref pointSource, 0, ref blend, 0x02);

            _lastAngle = angleDeg;
            _lastScale = scaleMultiplier;
            _lastThinking = isAiThinking;
            _lastRole = role;
            _lastOrbVisible = orbVis;
            _lastOrbSlot = _orb.ActiveSlotIndex;
            _lastGhostStreaming = ghostVis;
        }
        else
        {
            Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref topPos, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 0x02);
        }

        _lastPixelX = targetLeft;
        _lastPixelY = targetTop;
    }

    private static float GetDpiScaleAtPoint(int x, int y)
    {
        IntPtr hMonitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, 2);
        if (hMonitor != IntPtr.Zero && Win32.GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0)
        {
            return dpiX / 96.0f;
        }
        return 1.25f;
    }

    private volatile bool _pulsingVrCursor;

    /// <summary>
    /// Temporarily removes WS_EX_TRANSPARENT on the overlay for ~75ms while answering WM_SETCURSOR with
    /// IDC_HAND (65567), then restores click-through WS_EX_TRANSPARENT. This forces OculusDash's panel
    /// lastHIcon (+0x128) to transition 65539 -> 65567 -> 65539 and re-bind the freshly flushed 65539 texture.
    /// </summary>
    public void PulseCursorHandleForVr(SynchronizationContext uiSync)
    {
        uiSync.Post(async _ =>
        {
            if (Handle == IntPtr.Zero || _pulsingVrCursor) return;
            const int GWL_EXSTYLE = -20;
            const int WS_EX_TRANSPARENT = 0x00000020;

            _pulsingVrCursor = true;
            int exStyle = Win32.GetWindowLong(Handle, GWL_EXSTYLE);
            try
            {
                Win32.SetWindowLong(Handle, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
                if (Win32.GetCursorPos(out var pt))
                {
                    Win32.SetCursorPos(pt.X, pt.Y);
                }
                await Task.Delay(80);
            }
            finally
            {
                _pulsingVrCursor = false;
                if (Handle != IntPtr.Zero)
                {
                    Win32.SetWindowLong(Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
                    if (Win32.GetCursorPos(out var pt2))
                    {
                        Win32.SetCursorPos(pt2.X, pt2.Y);
                    }
                }
            }
        }, null);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_SETCURSOR = 0x0020;
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_SETCURSOR && _pulsingVrCursor)
        {
            Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, 32649));
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            switch (id)
            {
                case HotkeySummonId: _onSummonHotkey?.Invoke(); break;
                case HotkeyToggleId: _onToggleHotkey?.Invoke(); break;
                case HotkeyQuitId: _onQuitHotkey?.Invoke(); break;
                case HotkeyWizId: _onOpenWizHotkey?.Invoke(); break;
                case HotkeyAInboxId: _onOpenAInboxHotkey?.Invoke(); break;
                case HotkeyOrbId: _onToggleOrbHotkey?.Invoke(); break;
                case HotkeyGhostId: _onToggleGhostHotkey?.Invoke(); break;
                case HotkeyCycleProfileId: _onCycleProfileHotkey?.Invoke(); break;
                case HotkeyHolodexId: _onOpenHolodexHotkey?.Invoke(); break;
                case HotkeySnipLassoId: _onOpenSnipLassoHotkey?.Invoke(); break;
                case HotkeyElementPickerId: _onOpenElementPickerHotkey?.Invoke(); break;
                case HotkeyTutorialId: _onOpenTutorialHotkey?.Invoke(); break;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        for (int id = HotkeySummonId; id <= HotkeyTutorialId; id++)
        {
            Win32.UnregisterHotKey(Handle, id);
        }
        DestroyHandle();
        _dibWrapper.Dispose();
        if (_oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
        if (_hDib != IntPtr.Zero) Win32.DeleteObject(_hDib);
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, _screenDc);
        _fillPath.Dispose();
        _strokePath.Dispose();
        _boneStrokePen.Dispose();
        _patinaStrokePen.Dispose();
        _coralStrokePen.Dispose();
        _groundFillBrush.Dispose();
        _shadowBrush1.Dispose();
        _shadowBrush2.Dispose();
    }
}

internal static class NativeCursorManager
{
    public static bool IsHidden { get; private set; }
    private static readonly Dictionary<IntPtr, WindowsCursorRole> HandleToRole = new();

    private static readonly uint[] CursorIds =
    {
        32512, 32513, 32514, 32515, 32516,
        32642, 32643, 32644, 32645, 32646,
        32648, 32649, 32650, 32651
    };

    // Slots the role detector ignores but that can still render a visible pointer:
    // OCR_SIZE, OCR_ICON, OCR_ICOCUR, pen/handwriting, IDC_PIN, IDC_PERSON.
    private static readonly uint[] ExtraHiddenCursorIds =
    {
        32640, 32641, 32647, 32631, 32671, 32672
    };

    public static void CaptureStockCursorHandles()
    {
        HandleToRole.Clear();
        foreach (uint id in CursorIds)
        {
            IntPtr hCur = Win32.LoadCursor(IntPtr.Zero, (int)id);
            if (hCur != IntPtr.Zero)
            {
                HandleToRole[hCur] = (WindowsCursorRole)id;
            }
        }
    }

    public static bool TryResolveRoleFromHandle(IntPtr hCursor, out WindowsCursorRole role)
    {
        return HandleToRole.TryGetValue(hCursor, out role);
    }

    /// <summary>How the cursor slots are made invisible (ArgbClear = 32x32 ARGB alpha 0 with hotspot 16,16, verified invisible in Air Link).</summary>
    public static ArrowSlotStyle ArrowStyle { get; set; } = ArrowSlotStyle.ArgbClear;

    /// <summary>
    /// True while the native cursor is temporarily back because a shell surface covers the overlay
    /// (<see cref="OverlayZGuard"/>). <see cref="IsHidden"/> keeps the user's choice meanwhile.
    /// </summary>
    public static bool Suspended { get; private set; }

    /// <summary>
    /// Installs the on-brand 32-bit ARGB Ark Aperture directly into the Windows DWM hardware cursor slots
    /// when a higher z-band shell surface (Notifications, Start, Taskbar, Flyouts) covers the overlay HWND,
    /// so the Ark Aperture always renders 100% IN FRONT OF Windows Notifications and shell elements.
    /// </summary>
    public static void SuspendForCoveredUi()
    {
        if (!IsHidden || Suspended) return;
        foreach (uint id in CursorIds.Concat(ExtraHiddenCursorIds))
        {
            IntPtr hCur = CreateApertureHardwareCursor(id);
            if (hCur == IntPtr.Zero) continue;
            if (!Win32.SetSystemCursor(hCur, id))
                Win32.DestroyCursor(hCur);
        }
        CaptureStockCursorHandles();
        Suspended = true;
    }

    private static IntPtr CreateApertureHardwareCursor(uint cursorId)
    {
        const int size = 40;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            if (cursorId == 32513) // OCR_IBEAM: Solid Patina vertical beam bar
            {
                float cx = size * 0.5f;
                using var glowPen = new Pen(Color.FromArgb(88, PhosphorTheme.Patina), 5.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var shadowPen = new Pen(Color.FromArgb(180, PhosphorTheme.Ground), 3.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var beamPen = new Pen(PhosphorTheme.Patina, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(glowPen, cx, 9f, cx, size - 9f);
                g.DrawLine(shadowPen, cx, 9f, cx, size - 9f);
                g.DrawLine(beamPen, cx, 9f, cx, size - 9f);
            }
            else
            {
                Color globeCol = cursorId switch
                {
                    32649 or 32650 or 32514 => PhosphorTheme.Coral, // Hand / AppStarting / Wait
                    32648 => PhosphorTheme.Rust,                    // No
                    32651 => PhosphorTheme.Seal,                    // Help
                    _ => PhosphorTheme.Patina                       // Arrow & default
                };
                var state = g.Save();
                g.TranslateTransform(size * 0.5f, size * 0.5f);
                g.RotateTransform(-22.5f);
                g.TranslateTransform(-size * 0.5f, -size * 0.5f);
                ArkEmblem.DrawAperture(
                    g,
                    new RectangleF(5f, 5f, size - 10f, size - 10f),
                    solidGlobe: true,
                    globeColorOverride: globeCol,
                    asCursor: true);
                g.Restore(state);
            }
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return Win32.CopyIcon(hIcon);
        }
        finally
        {
            if (hIcon != IntPtr.Zero) Win32.DestroyIcon(hIcon);
        }
    }

    /// <summary>Hides the cursors again once the covering surface is gone.</summary>
    public static void ResumeAfterCoveredUi()
    {
        if (!Suspended) return;
        Suspended = false;
        HideSystemCursors(force: true);
    }

    public static bool HideSystemCursors(bool force = false)
    {
        // A covering shell surface owns the pointer until the guard resumes; periodic re-hides wait.
        if (Suspended) return false;
        if (IsHidden && !force) return true;

        const int size = 32;
        var andPlane = new byte[size * size / 8];
        var xorPlane = new byte[size * size / 8];
        Array.Fill(andPlane, (byte)0xFF);

        bool anySuccess = false;
        foreach (uint id in CursorIds.Concat(ExtraHiddenCursorIds))
        {
            IntPtr hCur = ArrowStyle != ArrowSlotStyle.Mono
                ? CreateArgbCursor(ArrowStyle == ArrowSlotStyle.NearClear ? (byte)1 : (byte)0)
                : Win32.CreateCursor(IntPtr.Zero, 0, 0, size, size, andPlane, xorPlane);
            if (hCur == IntPtr.Zero) continue;
            if (Win32.SetSystemCursor(hCur, id))
                anySuccess = true;
            else
                Win32.DestroyCursor(hCur);
        }

        IsHidden = anySuccess;
        VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
        return anySuccess;
    }

    /// <summary>32x32 ARGB cursor filled with a constant alpha (0 = empty, 1 = effectively invisible but non-empty).</summary>
    private static IntPtr CreateArgbCursor(byte alpha)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(alpha, 0, 0, 0));
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return Win32.CopyIcon(hIcon);
        }
        finally
        {
            if (hIcon != IntPtr.Zero) Win32.DestroyIcon(hIcon);
        }
    }

    /// <summary>One line describing what Windows reports for the pointer right now, for the VR diagnostic log.</summary>
    public static string Snapshot()
    {
        var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
        if (!Win32.GetCursorInfo(ref ci)) return "GetCursorInfo failed";

        string slot = TryResolveRoleFromHandle(ci.hCursor, out var role) ? role.ToString() : "non-stock";
        string cls = "?";
        IntPtr hwnd = Win32.WindowFromPoint(ci.ptScreenPos);
        if (hwnd != IntPtr.Zero)
        {
            var sb = new StringBuilder(64);
            if (Win32.GetClassName(hwnd, sb, sb.Capacity) > 0) cls = sb.ToString();
        }
        return $"pt=({ci.ptScreenPos.X},{ci.ptScreenPos.Y}) flags={ci.flags} hCursor={ci.hCursor} slot={slot} class={cls} arrowStyle={ArrowStyle}";
    }

    public static void RestoreSystemCursors()
    {
        const uint SPI_SETCURSORS = 0x0057;
        Win32.SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        IsHidden = false;
        Suspended = false;
        CaptureStockCursorHandles();
        VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
    }
}

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, IntPtr psize,
        IntPtr hdcSrc, IntPtr pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll")]
    public static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);
}
