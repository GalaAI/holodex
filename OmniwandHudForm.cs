using System;
using System.Drawing;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Summonable Phosphor Shell HUD (Alt+Space, Actions Ring "Summon HUD", pipe hud:summon).
/// The Windows surface of the Wand capability: prompt the Ark gateway with pointed context,
/// and open Wiz (Alt+W) or AInbox intake (Alt+I).
/// </summary>
public sealed class OmniwandHudForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly Action<Point> _openWizCrafting;
    private readonly Action<Point> _openAInboxIntake;
    private readonly Action<Point>? _openHolodex;
    private readonly Action<Point>? _openSnipLasso;
    private readonly PhosphorField _promptField;
    private readonly PhosphorCheckBox _visionToggle;
    private readonly PhosphorBadge _statusBadge;
    private readonly DataLabel _statusLine;
    private readonly RichTextBox _responseBox;
    private readonly ModelPicker _modelPicker;
    private Point _summonCursorPos;

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
        _openWizCrafting = openWizCrafting;
        _openAInboxIntake = openAInboxIntake;
        _openHolodex = openHolodex;
        _openSnipLasso = openSnipLasso;

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
        Size = new Size(560, FullHeight);
        Padding = WidgetChrome.FramePadding();

        _statusBadge = new PhosphorBadge("Checking") { Name = "Hud.StatusBadge" };
        _statusBadge.FitToText();

        // Quick actions: Holodex Lite, Lasso/Vision, Wiz, AInbox
        var quickBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Hud.QuickBar" };
        var holodexBtn = new PhosphorButton("Holodex") { Dock = DockStyle.Left, Width = 122, Name = "Hud.HolodexButton" };
        holodexBtn.Click += (_, _) => { Hide(); _openHolodex?.Invoke(_summonCursorPos); };
        var snipBtn = new PhosphorButton("Lasso / UI") { Dock = DockStyle.Left, Width = 126, Name = "Hud.SnipButton" };
        snipBtn.Click += (_, _) => { Hide(); _openSnipLasso?.Invoke(_summonCursorPos); };
        var wizBtn = new PhosphorButton("Open Wiz") { Dock = DockStyle.Left, Width = 122, Name = "Hud.WizButton" };
        wizBtn.Click += (_, _) => { Hide(); _openWizCrafting(_summonCursorPos); };
        var ainboxBtn = new PhosphorButton("AInbox") { Dock = DockStyle.Left, Width = 116, Name = "Hud.AInboxButton" };
        ainboxBtn.Click += (_, _) => { Hide(); _openAInboxIntake(_summonCursorPos); };
        quickBar.Controls.Add(ainboxBtn);
        quickBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6, BackColor = Color.Transparent });
        quickBar.Controls.Add(snipBtn);
        quickBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6, BackColor = Color.Transparent });
        quickBar.Controls.Add(wizBtn);
        quickBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6, BackColor = Color.Transparent });
        quickBar.Controls.Add(holodexBtn);

        _modelPicker = new ModelPicker(_ark) { Name = "Hud.ModelPicker" };
        var modelRow = WidgetChrome.Labeled("Model // Gateway", _modelPicker, 26);

        _promptField = new PhosphorField("Point and ask ArkStation. Enter sends, Esc closes.", false, 40) { Name = "Hud.PromptField" };
        _promptField.Box.Name = "Hud.PromptBox";
        _promptField.Box.KeyDown += OnPromptKeyDown;
        var promptRow = WidgetChrome.Labeled("Ask // Pointed context", _promptField, 40);

        _visionToggle = new PhosphorCheckBox("Attach the screen around the cursor tip")
        {
            Dock = DockStyle.Top,
            Checked = true,
            Name = "Hud.VisionToggle"
        };

        _statusLine = new DataLabel("Gateway // checking") { Dock = DockStyle.Top, Height = 20, Name = "Hud.StatusLine" };

        _responseBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(13f),
            Name = "Hud.ResponseBox",
            // Empty until a question is asked: the answer area, not a help panel (operator call, 2026-10-18).
            Text = string.Empty
        };

        Controls.Add(_responseBox);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_statusLine);
        Controls.Add(_visionToggle);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(promptRow);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(modelRow);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(quickBar);
        Controls.Add(WidgetChrome.Spacer(8));
        WidgetChrome.Attach(this, "ARK://WAND // WINDOWS SURFACE", "Omniwand", PhosphorTheme.Patina, _statusBadge);
        WidgetDrag.Enable(this);
        ShowStandaloneStatus(!string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey()));
        FitToAnswer();
    }

    private const int FullHeight = 420;

    /// <summary>
    /// No answer yet: hide the answer box and end the panel one gutter below the status line, so the HUD has
    /// no dead space. An answer (or the asking state) restores the full height with the box.
    /// </summary>
    private void FitToAnswer()
    {
        bool hasAnswer = !string.IsNullOrEmpty(_responseBox.Text);
        _responseBox.Visible = hasAnswer;
        if (hasAnswer)
        {
            Height = (int)Math.Round(FullHeight * PhosphorPaint.Scale(this));
            return;
        }
        PerformLayout();
        Height = _statusLine.Bottom + Padding.Bottom;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public async void SummonAt(Point cursorPos)
    {
        _summonCursorPos = cursorPos;

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 18, cursorPos.Y + 18);

        Show();
        Activate();
        _promptField.Box.Focus();
        _promptField.Box.SelectAll();

        _ = _modelPicker.RefreshAsync();

        // Local key state is instant; show it now so the HUD never opens on "Checking".
        // The network probe below only upgrades it when an ArkStation gateway is running.
        if (!_ark.LastGatewayOnline)
            ShowStandaloneStatus(!string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey()));

        var status = await _ark.CheckStatusAsync();
        if (status.GatewayOnline)
        {
            _statusBadge.Text = "Online";
            _statusBadge.State = PhosphorBadgeState.Validated;
            _statusBadge.FitToText();
            _statusLine.Text = "ARKSTATION GATEWAY // ONLINE";
        }
        else
        {
            ShowStandaloneStatus(status.NebiusKeyLoaded);
        }
    }

    private void ShowStandaloneStatus(bool keyLoaded)
    {
        _statusBadge.Text = keyLoaded ? "Ready" : "No key";
        _statusBadge.State = keyLoaded ? PhosphorBadgeState.Validated : PhosphorBadgeState.Blocked;
        _statusBadge.FitToText();
        _statusLine.Text = keyLoaded
            ? "NEMOTRON // NEBIUS TOKEN FACTORY // READY"
            : "NO MODEL KEY // ADD A NEBIUS KEY IN HOLODEX (ALT+H)";
        _statusLine.ForeColor = keyLoaded ? PhosphorTheme.Dim : PhosphorTheme.Ember;
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        e.Handled = true;

        string prompt = _promptField.Box.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        string? snapshot = null;
        if (_visionToggle.Checked)
        {
            snapshot = ArkStationClient.CaptureRegionAroundCursor(_summonCursorPos.X, _summonCursorPos.Y);
        }

        _promptField.Box.Enabled = false;
        _responseBox.ForeColor = PhosphorTheme.Dim;
        _responseBox.Text = _ark.LastGatewayOnline
            ? "Asking the Ark gateway with pointed context..."
            : $"Asking NVIDIA Nemotron ({ArkStationClient.ResolveNemotronModel(_ark.SelectedModel)}) via Nebius Token Factory...";
        FitToAnswer();
        _setThinkingState(true);

        try
        {
            var (ok, output) = await _ark.AskArkGatewayAsync(prompt, snapshot);
            _responseBox.ForeColor = ok ? PhosphorTheme.Bone : PhosphorTheme.Rust;
            _responseBox.Text = output;
            FitToAnswer();
        }
        finally
        {
            _setThinkingState(false);
            _promptField.Box.Enabled = true;
            _promptField.Box.Focus();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }
}
