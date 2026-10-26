using System;
using System.Drawing;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Summonable Phosphor Shell HUD (Alt+Space, Actions Ring "Summon HUD", pipe hud:summon). One composer: ask about
/// what the wand points at, with the screen around the tip attached by a toggle. The model chip is the one place
/// that says which model answers and whether it is ready. The header's icons open Holodex, Lasso, Wiz and AInbox.
/// </summary>
public sealed class OmniwandHudForm : Form
{
    private const int AnswerHeight = 200;

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _visionToggle;
    private readonly DataLabel _visionHint;
    private readonly RichTextBox _responseBox;
    private readonly Panel _responseGap;
    private readonly ModelChip _modelChip;
    private readonly DataLabel _statusLine;
    private readonly PhosphorButton _askBtn;
    private Point _summonCursorPos;
    private bool _working;

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

        // Launchers live in the header as icons, so the body is only the question.
        var launchers = new FlowLayoutPanel { Size = new Size(4 * 44, 44), WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Name = "Hud.Launchers" };
        void AddLauncher(string name, PhosphorGlyph glyph, string label, Action<Point>? open)
        {
            if (open == null) return;
            var b = new IconToolButton(name, glyph, label) { Margin = Padding.Empty };
            b.Click += (_, _) => { Hide(); open(_summonCursorPos); };
            launchers.Controls.Add(b);
        }
        AddLauncher("Hud.HolodexButton", PhosphorGlyph.Holodex, "Holodex", openHolodex);
        AddLauncher("Hud.SnipButton", PhosphorGlyph.Lasso, "Lasso", openSnipLasso);
        AddLauncher("Hud.WizButton", PhosphorGlyph.Wiz, "Wiz", openWizCrafting);
        AddLauncher("Hud.AInboxButton", PhosphorGlyph.AInbox, "AInbox", openAInboxIntake);
        launchers.Width = launchers.Controls.Count * 44;

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

        Controls.Add(_responseBox);
        Controls.Add(_responseGap);
        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "ARK // WAND", "Omniwand", PhosphorTheme.Patina, launchers);
        WidgetDrag.Enable(this);
        SyncVisionHint();
        SyncAskLabel();
        FitToAnswer();
    }

    private bool HasAnswer => !string.IsNullOrEmpty(_responseBox.Text);

    /// <summary>No answer yet: no answer box. An answer (or the asking state) opens it under the question.</summary>
    private void FitToAnswer() => FitToContent.Apply(this, c => c == _responseBox || c == _responseGap ? HasAnswer : true);

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

        await _ark.CheckStatusAsync();
        await _modelChip.RefreshAsync();
        ShowKeyState();
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

    private async System.Threading.Tasks.Task AskAsync()
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
