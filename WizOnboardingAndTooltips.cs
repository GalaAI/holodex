using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

public sealed record WizEnvironmentScanReport(
    string AgentFolder,
    string BamfManifestPath,
    MouseHardwareReport MouseHardware,
    string DisplaySummary,
    IReadOnlyList<string> DetectedClis,
    bool WinCredVaultReady,
    bool NebiusReady,
    bool ArkHiveGatewayReady,
    string CompletedAt);

/// <summary>
/// Zero-manual Wiz automated onboarding engine + Animated Phosphor Tooltip Tutorial overlay.
/// Scans environment, detects mouse brand/model &amp; DPI, sets up dependencies, creates the local
/// ~/.ark/agents/omniwand workspace folder &amp; .bamf manifest, and walks the user through Omniwand
/// with animated Phosphor tooltips.
/// </summary>
public static class WizOnboardingEngine
{
    public static string ReceiptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "wiz-onboarding-receipt.json");

    public static bool HasCompletedOnboarding() => File.Exists(ReceiptPath);

    public static WizEnvironmentScanReport RunZeroManualProvisioning(ArkStationClient ark)
    {
        var mouse = MouseHardwareAndWandEngine.DetectConnectedMouse();
        ark.ActiveMousePackageId = mouse.PackageId == "mx-master-4" ? "mx-master-4" : "universal-mouse";

        // 1. Scan monitors & DPI
        var screens = Screen.AllScreens;
        var primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        string displaySummary = $"{screens.Length} Display(s) · Primary {primary.Width}x{primary.Height}";

        // 2. Scan installed CLIs on PATH without blocking
        var detectedClis = new List<string>();
        foreach (string cli in new[] { "dotnet", "node", "git", "python", "agy", "gemini" })
        {
            if (IsCommandOnPath(cli)) detectedClis.Add(cli);
        }

        // 3. Provision ~/.ark/agents/omniwand and %LOCALAPPDATA%\Omniwand\agents\wiz
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string agentFolder = Path.Combine(userProfile, ".ark", "agents", "omniwand");
        string wizSubFolder = Path.Combine(userProfile, ".ark", "agents", "wiz");
        Directory.CreateDirectory(agentFolder);
        Directory.CreateDirectory(wizSubFolder);

        // 4. Auto-configure mouse macros tailored to the detected mouse brand/model
        var macros = MouseHardwareAndWandEngine.LoadOrCreateMacroBindings(mouse);

        // 5. Check Vault / Token state
        bool nebiusReady = !string.IsNullOrWhiteSpace(ark.ReadNebiusApiKey());
        bool gatewayReady = !string.IsNullOrWhiteSpace(ark.ReadInboundKey());
        bool winCredReady = nebiusReady || gatewayReady || ArkStationClient.HasWinCredVaultBundle();

        // 6. Write .bamf agent manifest & SKILL.md
        string bamfPath = Path.Combine(agentFolder, "omniwand.agent.bamf.json");
        var bamfManifest = new
        {
            schema = "ark.bamf.agent/v1",
            id = "agent:omniwand:wiz-conductor",
            title = "Omniwand Desktop Augment & Wiz Conductor",
            owner = "wiz",
            faction = "syndicate",
            provisionedAt = DateTime.UtcNow.ToString("O"),
            hardware = new
            {
                mouseBrand = mouse.Brand,
                mouseModel = mouse.Model,
                vidPid = mouse.VidPid,
                packageId = mouse.PackageId,
                windowsPointerSpeed = mouse.WindowsPointerSpeed,
                estimatedDpi = mouse.EstimatedDpi
            },
            capabilities = new[]
            {
                "wand.point",
                "wand.annotate",
                "wand.capture",
                "wand.prompt",
                "wand.palette",
                "wand.architect",
                "grimx.flow.orb",
                "holodex.lite.drawer",
                "syndicate.session.chat",
                "vault.wincred.sync"
            },
            detectedToolchains = detectedClis,
            macroBindings = macros
        };
        File.WriteAllText(bamfPath, JsonSerializer.Serialize(bamfManifest, new JsonSerializerOptions { WriteIndented = true }));

        string skillMdPath = Path.Combine(wizSubFolder, "SKILL.md");
        if (!File.Exists(skillMdPath))
        {
            File.WriteAllText(skillMdPath,
                "---\nname: wiz-omniwand\ndescription: Wiz 4GE Workflow-Crafting, Mouse Macro Intelligence, and GrimX Governance Agent\n---\n\n" +
                "# Wiz // Omniwand Desktop Conductor\n\n" +
                $"Auto-provisioned for **{mouse.Brand} {mouse.Model}** (`{mouse.VidPid}`).\n" +
                "- **Alt+Space**: Summon Omniwand HUD\n" +
                "- **Alt+H**: Open Holodex Lite Drawer (Session, AInbox, Codex, GrimX, Mods)\n" +
                "- **Alt+S**: Free-Form Lasso & Live Vision Annotator\n" +
                "- **Alt+E**: Desktop Element Picker (`wand.point` / `wand.palette` / `wand.architect`)\n" +
                "- **Alt+W**: Wiz Crafting Thread\n");
        }

        var report = new WizEnvironmentScanReport(
            agentFolder,
            bamfPath,
            mouse,
            displaySummary,
            detectedClis,
            winCredReady,
            nebiusReady,
            gatewayReady,
            DateTime.UtcNow.ToString("O"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
            File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        return report;
    }

    private static bool IsCommandOnPath(string cmd)
    {
        try
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv)) return false;
            string[] exts = { ".exe", ".cmd", ".bat", ".ps1" };
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string ext in exts)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir.Trim(), cmd + ext))) return true;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }
}

public sealed record TutorialStepInfo(
    int StepNumber,
    string Eyebrow,
    string Title,
    string Body,
    string ActionButtonLabel,
    Color AccentColor,
    Action TryAction);

/// <summary>
/// Animated Phosphor Tooltip Tutorial overlay guided by Wiz.
/// Uses a 60fps animation timer for leading-edge Phosphor aura disclosure, pulsing target reticle,
/// and interactive one-click 'Try It Live' actions so onboarding requires zero manual configuration.
/// </summary>
public sealed class PhosphorAnimatedTooltipForm : Form
{
    private readonly List<TutorialStepInfo> _steps;
    private int _currentIndex;
    private float _animPhase;
    private float _entryProgress;
    private readonly System.Windows.Forms.Timer _animTimer;

    private readonly PhosphorBadge _stepBadge;
    private readonly PhosphorButton _tryBtn;
    private readonly PhosphorButton _nextBtn;
    private readonly PhosphorButton _prevBtn;

    public PhosphorAnimatedTooltipForm(
        ArkStationClient ark,
        WizEnvironmentScanReport report,
        Action openHolodex,
        Action openSnipLasso,
        Action openElementPicker,
        Action openOrb,
        Action cycleCursorStyle)
    {
        PhosphorFonts.EnsureLoaded();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "WizTutorialTooltip";
        Size = new Size(500, 268);

        string clis = report.DetectedClis.Count > 0 ? string.Join(", ", report.DetectedClis) : "standalone runtime";
        string vaultState = report.NebiusReady ? "Windows Vault + Nebius Ready" : "Token Login Ready in Holodex";

        _steps = new List<TutorialStepInfo>
        {
            new(1,
                "WIZ ONBOARDING // AUTO-PROVISIONED",
                $"Detected {report.MouseHardware.Brand} {report.MouseHardware.Model}",
                $"Wiz scanned your environment ({report.DisplaySummary}, CLIs: {clis}), created your agent workspace at ~/.ark/agents/omniwand, configured {report.MouseHardware.Brand} macros ({report.MouseHardware.EstimatedDpi} DPI), and linked {vaultState}.",
                "Cycle Wand Cursor",
                PhosphorTheme.Patina,
                cycleCursorStyle),
            new(2,
                "WAND + GESTURES // HARDWARE INTELLIGENCE",
                "On-Brand Relic Wand Cursor, DPI & Gesture Flicks",
                $"Your cursor now renders the on-brand Relic Wand at VSync. Hold Right-Click and flick North/East/South/West (or press Mouse4/Mouse5 on your {report.MouseHardware.Brand} mouse) to trigger Wiz spells and tools.",
                "Test GrimX Orb",
                PhosphorTheme.Coral,
                openOrb),
            new(3,
                "VISION + UI INSPECTOR // WAND.POINT",
                "Free-Form Lasso, Annotator & Element Picker",
                "Press Alt+S (or Mouse4) to draw a Free-Form Lasso with live Phosphor annotations, or press Alt+E to inspect any desktop window's HWND, geometry, and pixel hex palette with NVIDIA Nemotron VL or MiniCPM-V.",
                "Try Lasso & Snip",
                PhosphorTheme.Patina,
                openSnipLasso),
            new(4,
                "ELEMENT PICKER // WAND.PALETTE",
                "Live Desktop Element & Pixel Palette Inspector",
                "Hover over any app on Windows to inspect its live class, bounding dimensions, and exact #HEX color token, then save to Codex or route straight to Wiz.",
                "Try Point UI (Alt+E)",
                PhosphorTheme.Seal,
                openElementPicker),
            new(5,
                "HOLODEX LITE // SYNDICATE SESSION",
                "Holodex Drawer + Hivemind Syndicate Session",
                "Press Alt+H (or Mouse5) to slide open the Holodex Lite drawer: log in with a single Hivemind/Nebius token and chat with the Syndicate (Gala, Wiz, Smith, Arbiter, Scholar, Muse) in a live group session with Work Orders!",
                "Open Holodex Lite",
                PhosphorTheme.Coral,
                openHolodex)
        };

        _stepBadge = new PhosphorBadge("STEP 1 / 5", PhosphorBadgeState.Proven)
        {
            Location = new Point(Width - 138, 16),
            Name = "Tutorial.StepBadge"
        };
        _stepBadge.FitToText();
        Controls.Add(_stepBadge);

        var closeBtn = new PhosphorButton("×")
        {
            Glyph = true,
            Size = new Size(32, 32),
            Location = new Point(Width - 44, 12),
            Name = "Tutorial.CloseButton"
        };
        closeBtn.Click += (_, _) => Hide();
        Controls.Add(closeBtn);

        _prevBtn = new PhosphorButton("← Back")
        {
            Size = new Size(96, 38),
            Location = new Point(22, Height - 52),
            Name = "Tutorial.PrevButton"
        };
        _prevBtn.Click += (_, _) => StepTo(_currentIndex - 1);
        Controls.Add(_prevBtn);

        _tryBtn = new PhosphorButton("Cycle Wand Cursor")
        {
            Size = new Size(176, 38),
            Location = new Point(128, Height - 52),
            Name = "Tutorial.TryButton"
        };
        _tryBtn.Click += (_, _) => _steps[_currentIndex].TryAction?.Invoke();
        Controls.Add(_tryBtn);

        _nextBtn = new PhosphorButton("Next Step →", PhosphorButtonVariant.Primary)
        {
            Size = new Size(164, 38),
            Location = new Point(Width - 186, Height - 52),
            Name = "Tutorial.NextButton"
        };
        _nextBtn.Click += (_, _) =>
        {
            if (_currentIndex + 1 < _steps.Count)
                StepTo(_currentIndex + 1);
            else
                Hide();
        };
        Controls.Add(_nextBtn);

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += (_, _) =>
        {
            _animPhase = (_animPhase + 0.08f) % (float)(Math.PI * 2.0);
            if (_entryProgress < 1f) _entryProgress = Math.Min(1f, _entryProgress + 0.09f);
            Invalidate();
        };

        WidgetChrome.ClipToFrame(this);
        WidgetDrag.Enable(this);
        SyncStepControls();
    }

    public void StartTutorial(Point anchorPt)
    {
        var work = Screen.FromPoint(anchorPt).WorkingArea;
        Location = new Point(
            Math.Clamp(work.Right - Width - 28, work.Left + 16, work.Right - Width - 16),
            Math.Clamp(work.Bottom - Height - 28, work.Top + 16, work.Bottom - Height - 16));
        StepTo(0);
        Show();
        Activate();
        _animTimer.Start();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _animTimer.Stop();
        else _animTimer.Start();
    }

    private void StepTo(int index)
    {
        _currentIndex = Math.Clamp(index, 0, _steps.Count - 1);
        _entryProgress = 0f;
        SyncStepControls();
        Invalidate();
    }

    private void SyncStepControls()
    {
        var step = _steps[_currentIndex];
        _stepBadge.Text = $"STEP {step.StepNumber} / {_steps.Count}";
        _stepBadge.FitToText();
        _stepBadge.Location = new Point(Width - _stepBadge.Width - 54, 16);
        _prevBtn.Enabled = _currentIndex > 0;
        _tryBtn.Text = step.ActionButtonLabel;
        _nextBtn.Text = _currentIndex + 1 < _steps.Count ? "Next Step →" : "Finish Setup";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var step = _steps[_currentIndex];
        WidgetChrome.PaintFrame(this, g, step.AccentColor);

        // Animated leading-edge Phosphor disclosure aura
        float auraStrength = 0.55f + 0.35f * (float)Math.Sin(_animPhase);
        PhosphorTheme.DrawHighlightAura(
            g,
            new Rectangle(2, 2, Width - 4, 62),
            step.AccentColor,
            auraStrength,
            PhosphorTheme.RadiusWidget);

        // Animated pulsing beacon ring on top-left
        float pulseR = (13f + 3.5f * (float)Math.Sin(_animPhase * 1.4f)) * s;
        float cx = 32f * s;
        float cy = 34f * s;
        using (var pulsePen = new Pen(Color.FromArgb(140, step.AccentColor), 2f))
        {
            g.DrawEllipse(pulsePen, cx - pulseR, cy - pulseR, pulseR * 2, pulseR * 2);
        }
        PhosphorTheme.DrawArkMark(g, new RectangleF(cx - 12 * s, cy - 12 * s, 24 * s, 24 * s));

        // Eyebrow & Title with slide-in entry motion
        float slideOffset = (1f - _entryProgress) * 10f * s;
        float textLeft = 58f * s + slideOffset;
        PhosphorPaint.DrawEyebrow(g, step.Eyebrow, step.AccentColor, textLeft, 14f * s, s);

        using var titleFont = PhosphorFonts.Ui(15f, heavy: true);
        using var bodyFont = PhosphorFonts.Body(12.5f);
        using var boneBrush = new SolidBrush(PhosphorTheme.Bone);
        using var dimBrush = new SolidBrush(PhosphorTheme.Dim);

        g.DrawString(step.Title, titleFont, boneBrush, textLeft, 32f * s);

        var bodyRect = new RectangleF(22f * s, 72f * s, Width - 44f * s, 112f * s);
        g.DrawString(step.Body, bodyFont, dimBrush, bodyRect);

        // Step progress dots bar above buttons
        int dotY = Height - 68;
        int dotStartX = 24;
        for (int i = 0; i < _steps.Count; i++)
        {
            bool active = i == _currentIndex;
            int w = active ? 28 : 10;
            var dotRect = new RectangleF(dotStartX, dotY, w, 5);
            PhosphorPaint.FillRounded(g, dotRect, 2.5f, active ? step.AccentColor : PhosphorTheme.Line2);
            dotStartX += w + 6;
        }
    }
}
