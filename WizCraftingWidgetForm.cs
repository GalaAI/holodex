using System;
using System.Drawing;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Wiz: the GrimX Flow crafting thread (Alt+W, Actions Ring slot 7, pipe wiz:summon).
/// Zone CREATE, so the panel edge and the selected lens read coral.
/// </summary>
public sealed class WizCraftingWidgetForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly PhosphorBadge _runtimeBadge;
    private readonly DataLabel _runtimeLine;
    private readonly ComboBox _flowLensSelect;
    private readonly ModelPicker _modelPicker;
    private readonly RichTextBox _threadBox;
    private readonly PhosphorCheckBox _attachScreenCheck;
    private readonly PhosphorField _promptField;
    private readonly PhosphorButton _sendBtn;

    private Point _summonPos;
    private string _activeFlowLens = "flow";
    private bool _working;

    private static readonly (string Id, string Label)[] FlowLenses =
    {
        ("flow",     "All GrimX Flows // overview and crafting"),
        ("spell",    "Spell // cast // one-shot execution"),
        ("ritual",   "Ritual // perform // scheduled cadence"),
        ("symphony", "Symphony // conduct // orchestra"),
        ("ultima",   "Ultima // pursue // deluge synthesis"),
        ("charm",    "Charm // bind // relic enchantment"),
        ("summon",   "Summon // summon // automaton")
    };

    public WizCraftingWidgetForm(ArkStationClient ark, Action<bool> setThinkingState)
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
        Name = "Wiz";
        Size = new Size(620, 560);
        Padding = WidgetChrome.FramePadding();

        _runtimeBadge = new PhosphorBadge(
            ArkStationClient.IsNemotronModel(_ark.SelectedModel) ? "Nemotron" : "Gemini Argon",
            PhosphorBadgeState.Accent)
        { AccentColor = PhosphorTheme.Coral, Name = "Wiz.RuntimeBadge" };
        _runtimeBadge.FitToText();

        // Lens row: flow lens select plus the shared model picker.
        var lensRow = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.Transparent, Name = "Wiz.LensBar" };
        _flowLensSelect = new PhosphorComboBox { Dock = DockStyle.Fill, Name = "Wiz.FlowLensSelect" };
        foreach (var lens in FlowLenses) _flowLensSelect.Items.Add(lens.Label);
        _flowLensSelect.SelectedIndex = 0;
        _flowLensSelect.SelectedIndexChanged += (_, _) =>
        {
            int idx = Math.Clamp(_flowLensSelect.SelectedIndex, 0, FlowLenses.Length - 1);
            _activeFlowLens = FlowLenses[idx].Id;
        };
        _modelPicker = new ModelPicker(_ark) { Dock = DockStyle.Right, Width = 210, Name = "Wiz.ModelPicker" };
        lensRow.Controls.Add(_flowLensSelect);
        lensRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        lensRow.Controls.Add(_modelPicker);
        var lensLabeled = WidgetChrome.Labeled("Flow lens // Model", lensRow, 26);

        string initialHost = ArkStationClient.IsNemotronModel(_ark.SelectedModel) ? "NEBIUS TOKEN FACTORY" : "ANTIGRAVITY CLI";
        _runtimeLine = new DataLabel($"HOST // {initialHost} // MODEL // {_ark.SelectedModel.ToUpperInvariant()}") { Dock = DockStyle.Top, Height = 20, Name = "Wiz.RuntimeLine" };

        _threadBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(13f),
            Name = "Wiz.ThreadBox",
            Text =
                "Wiz crafts GrimX Flows: Spell (cast), Ritual (perform), Symphony (conduct), Ultima (pursue), Charm (bind), Summon (summon), and Omnitek Augments.\n\n" +
                "Describe the Flow, Spell or Augment to craft."
        };

        // Composer: attach toggle, multiline field, the region's one primary button.
        // Opaque on the panel surface: transparent containers here left stale paint (a black bar, a ghost button).
        var composer = new Panel { Dock = DockStyle.Bottom, Height = 26 + 8 + 72, BackColor = PhosphorTheme.Surface, Name = "Wiz.Composer" };
        _attachScreenCheck = new PhosphorCheckBox("Attach the screen around the cursor tip")
        {
            Dock = DockStyle.Top,
            Checked = false,
            Name = "Wiz.AttachScreenToggle"
        };
        var inputRow = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Wiz.InputRow" };
        _promptField = new PhosphorField("Craft a GrimX Flow or Omnitek Augment. Enter sends, Shift+Enter breaks a line.", true, 72) { Dock = DockStyle.Fill, Name = "Wiz.PromptField" };
        _promptField.Box.Name = "Wiz.PromptBox";
        _promptField.Box.KeyDown += OnPromptKeyDown;
        // Button column keeps the primary at the 44px contract height, bottom-aligned to the field.
        var sendColumn = new Panel { Dock = DockStyle.Right, Width = 150, BackColor = PhosphorTheme.Surface };
        _sendBtn = new PhosphorButton("Craft Flow", PhosphorButtonVariant.Primary) { Dock = DockStyle.Bottom, Height = PhosphorTheme.TouchMin, Name = "Wiz.CraftButton" };
        _sendBtn.Click += async (_, _) => await SubmitCraftingPromptAsync();
        sendColumn.Controls.Add(_sendBtn);
        inputRow.Controls.Add(_promptField);
        inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = PhosphorTheme.Surface });
        inputRow.Controls.Add(sendColumn);
        composer.Controls.Add(inputRow);
        composer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = PhosphorTheme.Surface });
        composer.Controls.Add(_attachScreenCheck);

        Controls.Add(_threadBox);
        Controls.Add(composer);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_runtimeLine);
        Controls.Add(WidgetChrome.Spacer(4));
        Controls.Add(lensLabeled);
        Controls.Add(WidgetChrome.Spacer(8));
        WidgetChrome.Attach(this, "4GE // GRIMX FLOW CRAFTING", "Wiz", PhosphorTheme.Coral, _runtimeBadge);
        WidgetDrag.Enable(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SummonAt(Point cursorPos, string? initialFlowLens = null)
    {
        _summonPos = cursorPos;
        if (!string.IsNullOrWhiteSpace(initialFlowLens))
        {
            for (int i = 0; i < FlowLenses.Length; i++)
            {
                if (string.Equals(FlowLenses[i].Id, initialFlowLens, StringComparison.OrdinalIgnoreCase))
                {
                    _flowLensSelect.SelectedIndex = i;
                    _activeFlowLens = FlowLenses[i].Id;
                    break;
                }
            }
        }

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        var placed = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);
        int x = placed.X, y = placed.Y;
        Location = new Point(x, y);

        Show();
        Activate();
        _promptField.Box.Focus();
        _ = RefreshWizHeaderAsync();
    }

    private async System.Threading.Tasks.Task RefreshWizHeaderAsync()
    {
        await _modelPicker.RefreshAsync();
        bool nemotron = ArkStationClient.IsNemotronModel(_ark.SelectedModel) || !_ark.LastGatewayOnline;
        _runtimeBadge.Text = nemotron ? "Nemotron" : "Gemini Argon";
        _runtimeBadge.FitToText();
        string host = nemotron ? "NEBIUS TOKEN FACTORY" : "ANTIGRAVITY CLI";
        _runtimeLine.Text = $"HOST // {host} // MODEL // {_ark.SelectedModel.ToUpperInvariant()}";
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            await SubmitCraftingPromptAsync();
        }
    }

    private async System.Threading.Tasks.Task SubmitCraftingPromptAsync()
    {
        string prompt = _promptField.Box.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        _promptField.Box.Clear();
        _promptField.Box.Enabled = false;
        _sendBtn.Enabled = false;
        _working = true;
        Invalidate();
        _setThinkingState(true);

        _threadBox.ForeColor = PhosphorTheme.Bone;
        _threadBox.AppendText($"\n\nOPERATOR // {_activeFlowLens.ToUpperInvariant()}\n{prompt}\n\nWIZ // {_ark.SelectedModel.ToUpperInvariant()} // crafting");
        _threadBox.SelectionStart = _threadBox.TextLength;
        _threadBox.ScrollToCaret();

        string? snapshot = _attachScreenCheck.Checked
            ? ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y)
            : null;

        try
        {
            var (ok, runtime, model, output) = await _ark.AskWizGeminiArgonAsync(prompt, _activeFlowLens, snapshot);
            _runtimeBadge.Text = model;
            _runtimeBadge.FitToText();
            _runtimeLine.Text = $"HOST // {runtime.ToUpperInvariant()} // MODEL // {model.ToUpperInvariant()}";
            _threadBox.AppendText($"\n\n{runtime.ToUpperInvariant()} // {model.ToUpperInvariant()}\n{output}");
            _threadBox.SelectionStart = _threadBox.TextLength;
            _threadBox.ScrollToCaret();
        }
        finally
        {
            _working = false;
            Invalidate();
            _setThinkingState(false);
            _promptField.Box.Enabled = true;
            _sendBtn.Enabled = true;
            _promptField.Box.Focus();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Agent edge: grey at rest, patina while Wiz is working (sb-agent working state).
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
