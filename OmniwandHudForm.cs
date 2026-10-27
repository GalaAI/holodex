using System;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Summonable Phosphor Shell HUD (Alt+Space, Actions Ring "Summon HUD", pipe hud:summon). One composer: ask about
/// what the wand points at, with the screen around the tip attached by a toggle. The header badge is the wand
/// status (Online, Ready, No key) beside the icons that open Holodex, Lasso, Wiz and AInbox; the model chip says
/// which model answers. Under the answer: one telemetry line (gateway, Input Spine 8842, the connected mouse) and
/// the wand shortcuts behind a collapsed SHORTCUTS row (the answer area is not a help panel, operator 2026-10-18).
/// </summary>
public sealed class OmniwandHudForm : Form
{
    private const int AnswerMaxHeight = 260;

    private const string ShortcutsText =
        "Actions Ring casts the eight GrimX Spell slots.\n" +
        "F15 or Ctrl+Shift+Space opens the fallback orb.\n" +
        "F16 streams the GhostTrailer ribbon (Alt+V for it is planned, not bound yet).\n" +
        "Alt+W opens Wiz. Alt+I opens AInbox.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _visionToggle;
    private readonly DataLabel _visionHint;
    private readonly ThreadView _answers;
    private readonly Panel _responseGap;
    private readonly LedRow _telemetryLine;
    private static bool _gatewayNoticeShown;
    private readonly DisclosureRow _shortcutsRow;
    private readonly Label _shortcutsBody;
    private readonly ModelChip _modelChip;
    private readonly DataLabel _statusLine;
    private readonly PhosphorButton _askBtn;
    private Point _summonCursorPos;
    private bool _working;
    private bool _gatewayOnline;
    private bool? _spineOnline;
    private string? _mouseModel;
    private Task? _mouseScan;

    public OmniwandHudForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<Point> openWizCrafting,
        Action<Point> openAInboxIntake,
        Action<Point>? openHolodex = null,
        Action<Point>? openSnipLasso = null)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;

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
        Name = "Hud";
        Size = new Size(560, 300);
        Padding = WidgetChrome.FramePadding();

        // Header: the launchers as icons. Wand status is the gateway LED in the telemetry line, said once.
        var headerTools = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Name = "Hud.HeaderTools"
        };
        void AddLauncher(string name, PhosphorGlyph glyph, string label, Action<Point>? open)
        {
            if (open == null) return;
            var b = new IconToolButton(name, glyph, label) { Margin = Padding.Empty };
            b.Click += (_, _) => { Hide(); open(_summonCursorPos); };
            headerTools.Controls.Add(b);
        }
        AddLauncher("Hud.HolodexButton", PhosphorGlyph.Holodex, "Holodex", openHolodex);
        AddLauncher("Hud.SnipButton", PhosphorGlyph.Lasso, "Lasso", openSnipLasso);
        AddLauncher("Hud.WizButton", PhosphorGlyph.Wiz, "Wiz", openWizCrafting);
        AddLauncher("Hud.AInboxButton", PhosphorGlyph.AInbox, "AInbox", openAInboxIntake);

        // Composer: the question, and a bar with the screen toggle.
        _composer = new ComposerSurface { Dock = DockStyle.Top, Name = "Hud.Composer" };
        _promptBox = new PromptTextBox { Dock = DockStyle.Top, Height = 44, Prompt = "Ask about what the wand points at.", Name = "Hud.PromptField" };
        _promptBox.KeyDown += OnPromptKeyDown;
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Hud.ComposerBar" };
        _visionToggle = new IconToolButton("Hud.VisionToggle", PhosphorGlyph.Screen, "Attach the screen") { Dock = DockStyle.Left, Selected = true };
        _visionToggle.Click += (_, _) => { _visionToggle.Selected = !_visionToggle.Selected; SyncVisionHint(); };
        _visionHint = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Hud.VisionHint" };
        bar.Controls.Add(_visionHint);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        bar.Controls.Add(_visionToggle);
        _composer.Content.Controls.Add(bar);
        _composer.Content.Controls.Add(_promptBox);
        _composer.Height = _composer.HeightFor(_promptBox.Height + bar.Height);

        // The answers appear under the question once there are any: a card per question and per answer, in the
        // AInbox card language, fitting their content up to a cap and then scrolling.
        _answers = new ThreadView("Hud.ResponseBox") { Dock = DockStyle.Top, Height = 0 };
        _answers.ContentChanged += (_, _) => SizeAnswers();
        _responseGap = WidgetChrome.Spacer(10);

        // Telemetry: gateway state, Input Spine 8842 and the connected mouse, one LED each on one line.
        _telemetryLine = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "Hud.Telemetry" };

        // Wand shortcuts: one click away, collapsed by default.
        _shortcutsRow = new DisclosureRow
        {
            Dock = DockStyle.Top,
            Height = 44,
            Kicker = "SHORTCUTS",
            Sentence = "Actions Ring, fallback orb, GhostTrailer, Wiz, AInbox",
            OpenWord = "SHOW",
            Name = "Hud.Shortcuts"
        };
        _shortcutsRow.Expanded = false;
        _shortcutsRow.Toggled += (_, _) => { _shortcutsRow.Expanded = !_shortcutsRow.Expanded; FitToAnswer(); };
        _shortcutsBody = new Label
        {
            Dock = DockStyle.Top,
            Height = 92,
            AutoSize = false,
            UseMnemonic = false,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            Font = PhosphorFonts.Body(12.5f),
            Padding = new Padding(14, 8, 0, 0),
            Text = ShortcutsText,
            Name = "Hud.ShortcutsBody"
        };

        // Footer: which model answers, what is happening, and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Hud.Footer" };
        _modelChip = new ModelChip(_ark) { Dock = DockStyle.Left, Name = "Hud.ModelPicker" };
        _modelChip.ModelChanged += (_, _) => { SyncAskLabel(); SyncVisionHint(); };
        _statusLine = new DataLabel("ENTER ASKS") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Hud.StatusLine" };
        _askBtn = new PhosphorButton("Ask", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Hud.AskButton" };
        _askBtn.Click += async (_, _) => await AskAsync();
        footer.Controls.Add(_statusLine);
        footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_modelChip);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_askBtn);

        // Top stack (the first added sits lowest): composer, answer, telemetry, shortcuts.
        Controls.Add(_shortcutsBody);
        Controls.Add(_shortcutsRow);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(_telemetryLine);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_answers);
        Controls.Add(_responseGap);
        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "ARK // WAND", "Omniwand", PhosphorTheme.Patina, headerTools);
        WidgetDrag.Enable(this);
        ShowWandStatus(gatewayOnline: false);
        SyncVisionHint();
        SyncAskLabel();
        FitToAnswer();
        ResumeLayout(false);
    }

    private bool HasAnswer => _answers.Cards.Any();

    private void SizeAnswers()
    {
        int want = HasAnswer ? Math.Min(_answers.ContentHeight(_answers.CardWidth) + 2, AnswerMaxHeight) : 0;
        if (_answers.Height == want) return;
        _answers.Height = want;
        if (IsHandleCreated) FitToAnswer();
    }

    /// <summary>No answer yet: no answer box. An answer (or the asking state) opens it under the question.</summary>
    private void FitToAnswer() => FitToContent.Apply(this, c =>
        c == _answers || c == _responseGap ? HasAnswer
        : c == _shortcutsBody ? _shortcutsRow.Expanded
        : true);

    /// <summary>
    /// The model that actually answers: on the Nebius path a text-only Nemotron hands an attached screen to
    /// Nemotron 3.5 Super VL (ArkStationClient.AskNebiusTokenFactoryAsync), so say that rather than the chip's model.
    /// </summary>
    private string AnsweringModel(bool withScreen)
    {
        string model = _ark.SelectedModel;
        bool nebiusPath = !_ark.LastGatewayOnline || ArkStationClient.IsNemotronModel(model);
        bool vision = model.Contains("vl", StringComparison.OrdinalIgnoreCase) || model.Contains("omni", StringComparison.OrdinalIgnoreCase) || model.Contains("vision", StringComparison.OrdinalIgnoreCase);
        return withScreen && nebiusPath && !vision ? "nvidia/Nemotron-3.5-Super-VL" : model;
    }

    private void SyncVisionHint() => _visionHint.Text = _visionToggle.Selected
        ? $"SCREEN GOES WITH IT // {ModelChip.ShortName(AnsweringModel(true)).ToUpperInvariant()} READS IT"
        : "TEXT ONLY";

    private void SyncAskLabel()
    {
        if (_working) return;
        string first = _modelChip.Label.Split(' ')[0];
        _askBtn.Text = first.Length > 0 ? $"Ask {first}" : "Ask";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { _ = AskAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public async void SummonAt(Point cursorPos)
    {
        _summonCursorPos = cursorPos;

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 18, cursorPos.Y + 18);

        Show();
        Activate();
        _promptBox.Focus();
        _promptBox.SelectAll();

        // The local key state is instant, so the gateway LED never waits on the probe; the probe below upgrades it.
        if (!_ark.LastGatewayOnline) ShowWandStatus(false);
        _ = DetectMouseOnceAsync();

        var status = await _ark.CheckStatusAsync();
        _spineOnline = status.InputSpineOnline;
        ShowWandStatus(status.GatewayOnline);
        if (!_gatewayNoticeShown && !status.GatewayOnline && !_ark.HasGatewayInboundKey && !_ark.ForceStandaloneMode)
        {
            _gatewayNoticeShown = true;
            PhosphorToast.Show("Gateway unavailable",
                status.NebiusKeyLoaded
                    ? "No inbound key in Keymaster, so the HUD asks Nemotron directly through Token Factory. Wand and ring still work."
                    : "The HUD cannot ask the Ark gateway: no inbound key in Keymaster. Wand and ring still work; add a Nebius key in Holodex (Alt+H).",
                "GATEWAY // KEYMASTER", 4200, status.NebiusKeyLoaded ? ToastTone.Info : ToastTone.Warning);
        }
        await _modelChip.RefreshAsync();
        ShowKeyState();
    }

    /// <summary>The mouse scan shells out to PowerShell for a second or two, so it runs off the UI thread, once.</summary>
    private Task DetectMouseOnceAsync() => _mouseScan ??= DetectMouseAsync();

    private async Task DetectMouseAsync()
    {
        try
        {
            var mouse = await Task.Run(MouseHardwareAndWandEngine.DetectConnectedMouse);
            _mouseModel = mouse.Model;
        }
        catch
        {
            _mouseModel = "";
        }
        if (!IsDisposed) SyncTelemetry();
    }

    /// <summary>Gateway online, Nebius direct with a key, or no key: the gateway LED carries it.</summary>
    private void ShowWandStatus(bool gatewayOnline)
    {
        _gatewayOnline = gatewayOnline;
        SyncTelemetry();
    }

    private void SyncTelemetry()
    {
        bool keyed = _gatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());
        var gateway = _gatewayOnline ? (PhosphorTheme.Moss, "GATEWAY ONLINE")
            : keyed ? (PhosphorTheme.Patina, "NEBIUS DIRECT")
            : (PhosphorTheme.Ember, "NO MODEL KEY // ALT+H ADDS ONE");
        var spine = _spineOnline switch
        {
            true => (PhosphorTheme.Moss, "SPINE 8842"),
            false => (PhosphorTheme.Ember, "SPINE 8842 DOWN"),
            null => (PhosphorTheme.Ghost, "SPINE 8842")
        };
        var mouse = _mouseModel == null ? (PhosphorTheme.Ghost, "MOUSE")
            : _mouseModel.Length == 0 ? (PhosphorTheme.Ember, "NO MOUSE")
            : (PhosphorTheme.Moss, ShortMouse(_mouseModel));
        _telemetryLine.Set(gateway, spine, mouse);
    }

    /// <summary>"Logitech G PRO X Superlight 2 (Lightspeed)" as "G PRO X SUPERLIGHT 2": the LED row has one line.</summary>
    private static string ShortMouse(string model)
    {
        string m = model.Split('(')[0].Trim();
        foreach (var brand in new[] { "Logitech ", "Razer ", "SteelSeries ", "Corsair " })
            if (m.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) m = m[brand.Length..];
        return m.Length > 24 ? m[..24].TrimEnd() : m;
    }

    /// <summary>
    /// The footer status is only what is happening now, in words that fit beside the chip: the answer card names
    /// the model that answered and the gateway LED names a missing key, so neither is repeated here.
    /// </summary>
    private void ShowKeyState()
    {
        if (!_working) SetStatus("ENTER ASKS", PhosphorTheme.Ghost);
        SyncTelemetry();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLine.ForeColor = color;
        _statusLine.Text = text;
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        await AskAsync();
    }

    private async Task AskAsync()
    {
        string prompt = _promptBox.Text.Trim();
        if (_working) return;
        if (string.IsNullOrEmpty(prompt)) { _promptBox.Focus(); return; }

        string? snapshot = _visionToggle.Selected
            ? ArkStationClient.CaptureRegionAroundCursor(_summonCursorPos.X, _summonCursorPos.Y)
            : null;

        _working = true;
        _promptBox.Enabled = false;
        _askBtn.Enabled = false;
        _answers.Add(ThreadTone.You, snapshot != null ? "YOU // WITH THE SCREEN" : "YOU", prompt, snapshot != null ? PhosphorGlyph.Screen : null);
        SetStatus("ASKING", PhosphorTheme.Patina);
        FitToAnswer();
        Invalidate();
        _setThinkingState(true);

        try
        {
            var (ok, output) = await _ark.AskArkGatewayAsync(prompt, snapshot);
            string answeredBy = ModelChip.ShortName(AnsweringModel(snapshot != null)).ToUpperInvariant();
            _answers.Add(ok ? ThreadTone.Agent : ThreadTone.Fail, ok ? $"ANSWERED BY {answeredBy}" : "NOT ANSWERED", output);
            _promptBox.Clear();
            SetStatus("ENTER ASKS", PhosphorTheme.Ghost);
            if (!Visible)
            {
                // Esc hid the HUD mid-ask: the answer still arrives, so say where it is.
                string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                PhosphorToast.Show(ok ? $"Answered by {answeredBy}" : "Not answered", first.Length > 140 ? first[..140] + "..." : first,
                    "WAND // ALT+SPACE OPENS IT", 4200, ok ? ToastTone.Success : ToastTone.Warning);
            }
            FitToAnswer();
        }
        finally
        {
            _working = false;
            _setThinkingState(false);
            _promptBox.Enabled = true;
            _askBtn.Enabled = true;
            _promptBox.Focus();
            Invalidate();
        }
    }

    /// <summary>
    /// Live capture (the pipe's hud:submit): asks <paramref name="question"/> through the real composer and Ask path,
    /// with the screen toggle off so no screen pixels leave the machine for a capture.
    /// </summary>
    internal async Task AskForCaptureAsync(string question)
    {
        bool screen = _visionToggle.Selected;
        _visionToggle.Selected = false;
        _promptBox.Text = question;
        try { await AskAsync(); }
        finally { _visionToggle.Selected = screen; }
    }

    /// <summary>
    /// As-built render states (SurfaceCapture): "answered" shows a question with the screen and its answer as the
    /// cards a live ask adds; "reset" clears them.
    /// </summary>
    internal void PreviewState(string state)
    {
        if (_working) return; // never draw over a live ask
        _answers.Clear();
        if (state == "answered")
        {
            string model = ModelChip.ShortName(AnsweringModel(true)).ToUpperInvariant();
            _answers.Add(ThreadTone.You, "YOU // WITH THE SCREEN", "Why would this hold the PR?", PhosphorGlyph.Screen);
            _answers.Add(ThreadTone.Agent, $"ANSWERED BY {model}",
                "Navigate holds it because the Verify lane is paused, not because of the diff: the check reads \"could not execute this validation lane\". Resume the release, then re-request the check.");
        }
        ShowKeyState();
        SizeAnswers();
        FitToAnswer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
