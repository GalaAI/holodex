using System;
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
    private const int AnswerHeight = 200;

    private const string ShortcutsText =
        "Actions Ring casts the eight GrimX Spell slots.\n" +
        "F15 or Ctrl+Shift+Space opens the fallback orb.\n" +
        "F16 streams the GhostTrailer ribbon (Alt+V for it is planned, not bound yet).\n" +
        "Alt+W opens Wiz. Alt+I opens AInbox.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly PhosphorBadge _statusBadge;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _visionToggle;
    private readonly DataLabel _visionHint;
    private readonly RichTextBox _responseBox;
    private readonly Panel _responseGap;
    private readonly DataLabel _telemetryLine;
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
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
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

        // Header: the wand status badge, then the launchers as icons.
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
        _statusBadge = new PhosphorBadge("Checking") { Margin = new Padding(0, 11, 10, 0), Name = "Hud.StatusBadge" };
        headerTools.Controls.Add(_statusBadge);
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

        // The answer appears under the question once there is one.
        _responseBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = AnswerHeight,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(13f),
            Name = "Hud.ResponseBox",
            Text = string.Empty
        };
        _responseGap = WidgetChrome.Spacer(10);

        // Telemetry: gateway state, Input Spine 8842 and the connected mouse, always on one line.
        _telemetryLine = new DataLabel("GATEWAY // CHECKING") { Dock = DockStyle.Top, Height = 22, ForeColor = PhosphorTheme.Dim, Name = "Hud.Telemetry" };

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
        Controls.Add(_responseBox);
        Controls.Add(_responseGap);
        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "ARK // WAND", "Omniwand", PhosphorTheme.Patina, headerTools);
        WidgetDrag.Enable(this);
        ShowWandStatus(gatewayOnline: false, keyed: !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey()));
        SyncVisionHint();
        SyncAskLabel();
        FitToAnswer();
    }

    private bool HasAnswer => !string.IsNullOrEmpty(_responseBox.Text);

    /// <summary>No answer yet: no answer box. An answer (or the asking state) opens it under the question.</summary>
    private void FitToAnswer() => FitToContent.Apply(this, c =>
        c == _responseBox || c == _responseGap ? HasAnswer
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

        // The local key state is instant, so the badge never sits on "Checking"; the probe below upgrades it.
        if (!_ark.LastGatewayOnline) ShowWandStatus(false, !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey()));
        _ = DetectMouseOnceAsync();

        var status = await _ark.CheckStatusAsync();
        _spineOnline = status.InputSpineOnline;
        ShowWandStatus(status.GatewayOnline, status.NebiusKeyLoaded);
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

    /// <summary>Online when the ArkStation gateway answers, Ready when Nebius is direct with a key, No key otherwise.</summary>
    private void ShowWandStatus(bool gatewayOnline, bool keyed)
    {
        _gatewayOnline = gatewayOnline;
        _statusBadge.Text = gatewayOnline ? "Online" : keyed ? "Ready" : "No key";
        _statusBadge.State = gatewayOnline || keyed ? PhosphorBadgeState.Validated : PhosphorBadgeState.Blocked;
        _statusBadge.FitToText();
        _statusBadge.AccessibleName = $"Wand status: {_statusBadge.Text}";
        SyncTelemetry();
    }

    private void SyncTelemetry()
    {
        bool keyed = _gatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());
        string gateway = _gatewayOnline ? "GATEWAY ONLINE"
            : keyed ? "GATEWAY STANDBY // NEBIUS DIRECT"
            : "GATEWAY STANDBY // NO KEY";
        string spine = _spineOnline switch { true => "SPINE 8842 UP", false => "SPINE 8842 DOWN", null => "SPINE 8842 CHECKING" };
        string mouse = _mouseModel == null ? "MOUSE DETECTING" : _mouseModel.Length == 0 ? "NO MOUSE DETECTED" : _mouseModel.ToUpperInvariant();
        _telemetryLine.Text = $"{gateway} // {spine} // {mouse}";
        _telemetryLine.ForeColor = keyed ? PhosphorTheme.Dim : PhosphorTheme.Ember;
    }

    private void ShowKeyState()
    {
        bool keyed = _ark.LastGatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());
        if (keyed) { if (!_working) SetStatus("ENTER ASKS", PhosphorTheme.Ghost); }
        else SetStatus("NO MODEL KEY // ADD ONE IN HOLODEX (ALT+H)", PhosphorTheme.Ember);
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
        _responseBox.ForeColor = PhosphorTheme.Dim;
        _responseBox.Text = "...";
        SetStatus($"ASKING {_modelChip.Label.ToUpperInvariant()}", PhosphorTheme.Patina);
        FitToAnswer();
        Invalidate();
        _setThinkingState(true);

        try
        {
            var (ok, output) = await _ark.AskArkGatewayAsync(prompt, snapshot);
            _responseBox.ForeColor = ok ? PhosphorTheme.Bone : PhosphorTheme.Rust;
            _responseBox.Text = output;
            SetStatus(ok ? $"ANSWERED BY {ModelChip.ShortName(AnsweringModel(snapshot != null)).ToUpperInvariant()}" : "NOT ANSWERED", ok ? PhosphorTheme.Ghost : PhosphorTheme.Rust);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
