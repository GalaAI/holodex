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
/// %LOCALAPPDATA%\Omniwand\agents\omniwand workspace folder &amp; .bamf manifest, and walks the user through Omniwand
/// with animated Phosphor tooltips.
/// </summary>
public static class WizOnboardingEngine
{
    public static string ReceiptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "wiz-onboarding-receipt.json");

    public static bool HasCompletedOnboarding() => File.Exists(ReceiptPath);

    public static WizEnvironmentScanReport RunZeroManualProvisioning(ArkStationClient ark)
    {
        var mouse = MouseHardwareAndWandEngine.ActiveMouse;

        // 1. Scan monitors & DPI
        var screens = Screen.AllScreens;
        var primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        string displaySummary = $"{screens.Length} {(screens.Length == 1 ? "display" : "displays")} · Primary {primary.Width}x{primary.Height}";

        // 2. Scan installed CLIs on PATH without blocking
        var detectedClis = new List<string>();
        foreach (string cli in new[] { "dotnet", "node", "git", "python", "agy", "gemini" })
        {
            if (IsCommandOnPath(cli)) detectedClis.Add(cli);
        }

        // 3. Provision %LOCALAPPDATA%\Omniwand\agents\omniwand and \wiz: Holodex's own folder, on any PC.
        string agentsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "agents");
        string agentFolder = Path.Combine(agentsRoot, "omniwand");
        string wizSubFolder = Path.Combine(agentsRoot, "wiz");
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
            title = "Holodex and Wiz",
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
                canSetDpi = mouse.CanSetDpi
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
                "# Wiz // Holodex Desktop Conductor\n\n" +
                $"Auto-provisioned for **{mouse.Brand} {mouse.Model}** (`{mouse.VidPid}`).\n" +
                "- **Ctrl+Alt+Space**: Summon Holodex HUD\n" +
                "- **Ctrl+Alt+H**: Open the Holodex drawer (Session, AInbox, Library, Spells, Mods)\n" +
                "- **Ctrl+Alt+S**: Free-Form Lasso & Live Vision Annotator\n" +
                "- **Ctrl+Alt+E**: Desktop Element Picker (`wand.point` / `wand.palette` / `wand.architect`)\n" +
                "- **Ctrl+Alt+W**: Wiz Crafting Thread\n");
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
    Action TryAction,
    string? Keys = null);

/// <summary>
/// Animated Phosphor Tooltip Tutorial overlay guided by Wiz.
/// Uses a 60fps animation timer for leading-edge Phosphor aura disclosure, pulsing target reticle,
/// and interactive one-click 'Try It Live' actions so onboarding requires zero manual configuration.
/// </summary>
public sealed class PhosphorAnimatedTooltipForm : Form
{
    private readonly List<TutorialStepInfo> _steps;
    private int _currentIndex;
    private float _entryProgress;
    private float _dotProgress = 1f;
    private int _previousIndex;
    private readonly Stopwatch _stepClock = Stopwatch.StartNew();
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
        Action cycleCursorStyle,
        Action openHud,
        Action openAInbox)
    {
        PhosphorFonts.EnsureLoaded();
        // Designer order: layout suspended, then 96 DPI dimensions, then the mode, so WinForms scales the whole
        // built layout once at ResumeLayout to the monitor's DPI. The reverse order scaled the empty form only.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "WizTutorialTooltip";
        Size = new Size(540, 300);
        KeyPreview = true;

        string vaultState = report.NebiusReady
            ? "Your Nebius key is linked, so answers come from NVIDIA Nemotron on Token Factory."
            : HivemindClient.HasSavedSignIn
                ? "You are signed in with Hivemind, so NVIDIA Nemotron answers through it. No key needed."
                : "To turn on answers, open Holodex (Ctrl+Alt+H) and Sign in with Hivemind. No key needed. " + HivemindClient.SignInHelp;

        _steps = new List<TutorialStepInfo>
        {
            new(1,
                "WELCOME // HOLODEX",
                "Your cursor is now a wand",
                $"Holodex lives in the tray and draws the Aperture cursor. It found your {report.MouseHardware.Brand} {report.MouseHardware.Model} and {report.DisplaySummary}. {vaultState}",
                "Cycle cursor style",
                PhosphorTheme.Patina,
                cycleCursorStyle,
                "Ctrl+Alt+P"),
            new(2,
                "COMMAND HUD // ASK",
                "Ask anything, from anywhere",
                "Press Ctrl+Alt+Space to open the HUD at your cursor. Type a question and press Enter. Nemotron answers in place, and you can attach the screen when the question is about what you are looking at.",
                "Open the HUD",
                PhosphorTheme.Patina,
                openHud,
                "Ctrl+Alt+Space"),
            new(3,
                "LASSO // SEE",
                "Circle it, then ask about it",
                "Press Ctrl+Alt+S and draw around anything on screen. Ink on it to point, then Analyze to send the region to a vision model or Save to keep it in your Library.",
                "Try the lasso",
                PhosphorTheme.Patina,
                openSnipLasso,
                "Ctrl+Alt+S"),
            new(4,
                "ELEMENT PICKER // POINT",
                "Inspect any window or pixel",
                "Press Ctrl+Alt+E and hover. Holodex reads the window class, its bounds and the exact hex color under the wand, ready to copy or hand to Wiz.",
                "Try the picker",
                PhosphorTheme.Patina,
                openElementPicker,
                "Ctrl+Alt+E"),
            new(5,
                "SPELLS // GESTURES",
                "Hold Ctrl, then draw a spell",
                "Shake left and right for Wiz. Shake up and down for the lasso. Draw a circle to cast your slot 1 Spell. Sweep right for Holodex. A ghost ribbon follows the stroke so you can see it land.",
                "Open the Spell Orb",
                PhosphorTheme.Seal,
                openOrb,
                "Ctrl+Shift+Space"),
            new(6,
                "AINBOX // HAND OFF",
                "Drop it, say what you want, send it",
                "Press Ctrl+Alt+I. Drag in files, paste a snippet or capture the screen, add one line of intent, and Holodex writes the handoff for you.",
                "Open AInbox",
                PhosphorTheme.Patina,
                openAInbox,
                "Ctrl+Alt+I"),
            new(7,
                "HOLODEX // YOUR DRAWER",
                "Everything else lives here",
                "Press Ctrl+Alt+H for session chat, saved captures, Spells and mouse DPI. Replay this tour any time with Ctrl+Alt+T. Ctrl+Alt+Q quits and restores the normal cursor.",
                "Open Holodex",
                PhosphorTheme.Patina,
                openHolodex,
                "Ctrl+Alt+H")
        };

        _stepBadge = new PhosphorBadge("STEP 1 / 7", PhosphorBadgeState.Unstated)
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

        _prevBtn = new PhosphorButton("Back")
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

        _nextBtn = new PhosphorButton("Next", PhosphorButtonVariant.Primary)
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
            Invalidate();
            if (_stepClock.ElapsedMilliseconds > PhosphorMotion.DurDisclosure + 50) _animTimer.Stop();
        };

        WidgetChrome.ClipToFrame(this);
        WidgetDrag.Enable(this);
        SyncStepControls();
        ResumeLayout(false);
    }

    public void StartTutorial(Point anchorPt)
    {
        var work = Screen.FromPoint(anchorPt).WorkingArea;
        Location = new Point(
            WidgetChrome.SafeClamp(work.Right - Width - 28, work.Left + 16, work.Right - Width - 16),
            WidgetChrome.SafeClamp(work.Bottom - Height - 28, work.Top + 16, work.Bottom - Height - 16));
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Right:
            case Keys.PageDown:
                if (_currentIndex + 1 < _steps.Count) StepTo(_currentIndex + 1); else Hide();
                e.Handled = true;
                return;
            case Keys.Left:
            case Keys.PageUp:
                StepTo(_currentIndex - 1);
                e.Handled = true;
                return;
            case Keys.Escape:
                Hide();
                e.Handled = true;
                return;
        }
        base.OnKeyDown(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        // Arrow keys would otherwise move focus between buttons before OnKeyDown sees them.
        if (keyData is Keys.Left or Keys.Right or Keys.Escape)
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    private void StepTo(int index)
    {
        _previousIndex = _currentIndex;
        _currentIndex = Math.Clamp(index, 0, _steps.Count - 1);
        _entryProgress = 0f;
        _dotProgress = 0f;
        _stepClock.Restart();
        if (Visible) _animTimer.Start();
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
        _nextBtn.Text = _currentIndex + 1 < _steps.Count ? "Next" : "Finish";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Motion is computed from the clock at paint time, so a capture of a form that never ticked is settled.
        float t = PhosphorMotion.ReducedMotion ? 1f : Math.Min(1f, _stepClock.ElapsedMilliseconds / (float)PhosphorMotion.DurDisclosure);
        _entryProgress = PhosphorMotion.EaseAnalog(t);
        _dotProgress = PhosphorMotion.EaseOut(t);

        var step = _steps[_currentIndex];
        WidgetChrome.PaintFrame(this, g, step.AccentColor);

        // Static sidelight aura (FX-02). Pulse is reserved for live state, so the tour does not breathe.
        PhosphorTheme.DrawHighlightAura(g, new Rectangle(2, 2, Width - 4, 62), PhosphorTheme.Coral, 1f, PhosphorTheme.RadiusBadge);
        float cx = 32f * s;
        float cy = 34f * s;
        PhosphorTheme.DrawArkMark(g, new RectangleF(cx - 12 * s, cy - 12 * s, 24 * s, 24 * s));

        // Eyebrow and title slide 10px and fade in on dur-disclosure; the body follows a beat later.
        float slideOffset = (1f - _entryProgress) * 10f * s;
        float textLeft = 58f * s + slideOffset;
        int alpha = (int)Math.Round(255 * _entryProgress);
        int bodyAlpha = (int)Math.Round(255 * Math.Clamp((_entryProgress - 0.25f) / 0.75f, 0f, 1f));
        PhosphorPaint.DrawEyebrow(g, step.Eyebrow, Color.FromArgb(alpha, step.AccentColor), textLeft, 14f * s, s);

        using var titleFont = PhosphorFonts.Ui(15f, heavy: true);
        using var bodyFont = PhosphorFonts.Body(12.5f);
        using var boneBrush = new SolidBrush(Color.FromArgb(alpha, PhosphorTheme.Bone));
        using var dimBrush = new SolidBrush(Color.FromArgb(bodyAlpha, PhosphorTheme.Dim));

        g.DrawString(step.Title, titleFont, boneBrush, textLeft, 32f * s);

        float bodyTop = 76f * s;
        if (step.Keys is { } keys)
        {
            using var keyLabel = new SolidBrush(Color.FromArgb(bodyAlpha, PhosphorTheme.Ghost));
            using var monoFont = PhosphorFonts.Mono(10.5f);
            g.DrawString("SHORTCUT", monoFont, keyLabel, 22f * s, bodyTop + 3f * s);
            KeycapRow.Draw(g, keys, 22f * s + g.MeasureString("SHORTCUT", monoFont).Width + 8f * s, bodyTop, s);
            bodyTop += 30f * s;
        }

        var bodyRect = new RectangleF(22f * s, bodyTop, Width - 44f * s, Height - bodyTop - 76f * s);
        g.DrawString(step.Body, bodyFont, dimBrush, bodyRect);

        // Progress pills: the active pill grows to 28px while the previous one shrinks, eased together.
        float dotY = Height - 70f * s;
        float dotX = 24f * s;
        for (int i = 0; i < _steps.Count; i++)
        {
            float grow = i == _currentIndex ? _dotProgress : i == _previousIndex && _previousIndex != _currentIndex ? 1f - _dotProgress : 0f;
            float w = (10f + 18f * grow) * s;
            Color c = i == _currentIndex ? step.AccentColor : i < _currentIndex ? PhosphorTheme.Ghost : PhosphorTheme.Line2;
            PhosphorPaint.FillRounded(g, new RectangleF(dotX, dotY, w, 5f * s), 2.5f * s, c);
            dotX += w + 6f * s;
        }
    }
}
