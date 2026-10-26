using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Wiz: the GrimX Spell crafting thread (Alt+W, Actions Ring slot 7, pipe wiz:summon). One composer for the goal,
/// with the screen toggle and three starters; the Spell type is a chip Omnitek fills from the goal's words, the
/// model chip says which model plans it, and the thread opens above once there is one. Zone CREATE.
/// </summary>
public sealed class WizCraftingWidgetForm : Form
{
    private const int ThreadHeight = 260;

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly RichTextBox _threadBox;
    private readonly Panel _threadGap;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _attachScreenToggle;
    private readonly FlowLayoutPanel _starterRow;
    private readonly SuggestionChip _lensChip;
    private readonly ModelChip _modelChip;
    private readonly PhosphorButton _sendBtn;

    private Point _summonPos;
    private string _activeFlowLens = "flow";
    private bool _lensChosenByHand;
    private bool _working;

    private static readonly (string Id, string Title, string Verb)[] FlowLenses =
    {
        ("flow",     "Spell",      "any Spell type"),
        ("spell",    "Quick Cast", "one-shot execution"),
        ("combo",    "Combo",      "linked casts, run now"),
        ("ritual",   "Ritual",     "scheduled cadence"),
        ("symphony", "Symphony",   "orchestra of agents"),
        ("ultima",   "Ultima",     "deluge synthesis"),
        ("charm",    "Charm",      "relic enchantment"),
        ("summon",   "Summon",     "automaton")
    };

    private string ActiveLensTitle => FlowLenses.FirstOrDefault(l => l.Id == _activeFlowLens).Title ?? "Spell";

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
        Size = new Size(620, 320);
        Padding = WidgetChrome.FramePadding();

        _threadBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = ThreadHeight,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(13f),
            Name = "Wiz.ThreadBox",
            Text = string.Empty
        };
        PhosphorPaint.UseDarkScrollbars(_threadBox);
        _threadGap = WidgetChrome.Spacer(10);

        // Composer: the goal, then a bar with the screen toggle and the starters.
        _composer = new ComposerSurface { Dock = DockStyle.Top, Name = "Wiz.Composer" };
        _promptBox = new PromptTextBox
        {
            Dock = DockStyle.Top,
            Height = 66,
            Prompt = "Describe a goal. Wiz turns it into a Spell you can run, refine or hand off.",
            Name = "Wiz.PromptField"
        };
        _promptBox.KeyDown += OnPromptKeyDown;
        _promptBox.TextChanged += (_, _) => { SuggestLens(); _starterRow!.Visible = _promptBox.TextLength == 0 && _threadBox.TextLength == 0; };

        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Wiz.ComposerBar" };
        _attachScreenToggle = new IconToolButton("Wiz.AttachScreenToggle", PhosphorGlyph.Screen, "Attach the screen") { Dock = DockStyle.Left };
        _attachScreenToggle.Click += (_, _) => _attachScreenToggle.Selected = !_attachScreenToggle.Selected;
        _starterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0), Name = "Wiz.Starters" };
        void AddStarter(string label, string name, string prompt, bool attachScreen, int width)
        {
            var chip = new PhosphorButton(label) { Width = width, Height = 36, Margin = new Padding(0, 0, 6, 0), Name = name };
            chip.Click += (_, _) =>
            {
                _promptBox.Text = prompt;
                _attachScreenToggle.Selected = attachScreen;
                _promptBox.Focus();
                _promptBox.SelectionStart = _promptBox.TextLength;
            };
            _starterRow.Controls.Add(chip);
        }
        AddStarter("Explain screen", "Wiz.StarterExplain", "Explain what is on my screen and what I should do next.", true, 150);
        AddStarter("Checklist", "Wiz.StarterChecklist", "Turn this goal into a step-by-step checklist: ", false, 112);
        AddStarter("Automate", "Wiz.StarterAutomate", "Plan a Combo I can rerun for this task: ", false, 112);
        bar.Controls.Add(_starterRow);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        bar.Controls.Add(_attachScreenToggle);
        _composer.Content.Controls.Add(bar);
        _composer.Content.Controls.Add(_promptBox);
        _composer.Height = _composer.HeightFor(_promptBox.Height + bar.Height);

        // Footer: what kind of Spell, which model, and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Wiz.Footer" };
        _lensChip = new SuggestionChip { Dock = DockStyle.Left, ShowArrow = false, Accent = PhosphorTheme.Coral, Name = "Wiz.FlowLensSelect" };
        _lensChip.Click += (_, _) => _lensChip.ShowChoices(FlowLenses.Select(l => new ChipChoice(
            l.Title, l.Verb, l.Id == _activeFlowLens, () => { _lensChosenByHand = true; SetLens(l.Id, confident: false); })));
        _modelChip = new ModelChip(_ark) { Dock = DockStyle.Left, Name = "Wiz.ModelPicker" };
        _sendBtn = new PhosphorButton("Craft Spell", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Wiz.CraftButton" };
        _sendBtn.Click += async (_, _) => await SubmitCraftingPromptAsync();
        footer.Controls.Add(_modelChip);
        footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        footer.Controls.Add(_lensChip);
        footer.Controls.Add(_sendBtn);

        Controls.Add(_composer);
        Controls.Add(_threadGap);
        Controls.Add(_threadBox);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "4GE // GRIMX SPELLS", "Wiz", PhosphorTheme.Coral, null);
        WidgetDrag.Enable(this);

        SetLens("flow", confident: false);
        FitToThread();
    }

    private bool HasThread => _threadBox.TextLength > 0;

    /// <summary>No thread yet: the composer is the whole widget. The thread opens above it after the first craft.</summary>
    private void FitToThread() => FitToContent.Apply(this, c => c == _threadBox || c == _threadGap ? HasThread : true);

    /// <summary>Omnitek reads the goal: Spell-type words (ritual, schedule, combo, summon...) pick the chip unless chosen by hand.</summary>
    private void SuggestLens()
    {
        if (_lensChosenByHand) return;
        var d = _ark.AutoDetectOmnitekItem(null, null, _promptBox.Text);
        bool spell = d.ItemType == "flow" && FlowLenses.Any(l => l.Id == d.ItemSubtype);
        SetLens(spell ? d.ItemSubtype! : "flow", confident: spell);
    }

    private void SetLens(string id, bool confident)
    {
        _activeFlowLens = id;
        _lensChip.Label = ActiveLensTitle;
        _lensChip.Confident = confident;
        _lensChip.Fit();
        if (!_working) _sendBtn.Text = $"Craft {ActiveLensTitle}";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { _ = SubmitCraftingPromptAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SummonAt(Point cursorPos, string? initialFlowLens = null)
    {
        _summonPos = cursorPos;
        if (!string.IsNullOrWhiteSpace(initialFlowLens))
        {
            var lens = FlowLenses.FirstOrDefault(l => string.Equals(l.Id, initialFlowLens, StringComparison.OrdinalIgnoreCase));
            if (lens.Id != null) { _lensChosenByHand = true; SetLens(lens.Id, confident: false); }
        }

        FitToThread();
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);

        Show();
        Activate();
        _promptBox.Focus();
        _ = _modelChip.RefreshAsync();
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
        string prompt = _promptBox.Text.Trim();
        if (_working) return;
        if (string.IsNullOrEmpty(prompt)) { _promptBox.Focus(); return; }

        // Clearing the box re-runs the lens suggestion on empty text; keep the lens this goal is crafted with.
        string lens = _activeFlowLens;
        string lensTitle = ActiveLensTitle;
        _promptBox.Clear();
        _starterRow.Visible = false;
        _promptBox.Enabled = false;
        _sendBtn.Enabled = false;
        _working = true;
        Invalidate();
        _setThinkingState(true);

        _threadBox.ForeColor = PhosphorTheme.Bone;
        _threadBox.AppendText($"{(HasThread ? "\n\n" : "")}YOU // {lensTitle.ToUpperInvariant()}\n{prompt}\n\nWIZ // crafting");
        _threadBox.SelectionStart = _threadBox.TextLength;
        _threadBox.ScrollToCaret();
        FitToThread();

        string? snapshot = _attachScreenToggle.Selected
            ? ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y)
            : null;

        try
        {
            var (ok, runtime, model, output) = await _ark.AskWizGeminiArgonAsync(prompt, lens, snapshot);
            _modelChip.ShowRuntime(runtime);
            _threadBox.AppendText($"\n\nWIZ // {ModelChip.ShortName(model).ToUpperInvariant()}\n{output}");
            _threadBox.SelectionStart = _threadBox.TextLength;
            _threadBox.ScrollToCaret();
        }
        finally
        {
            _working = false;
            Invalidate();
            _setThinkingState(false);
            _promptBox.Enabled = true;
            _sendBtn.Enabled = true;
            _lensChosenByHand = false;
            SetLens("flow", confident: false);
            _promptBox.Focus();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Agent edge: grey at rest, patina while Wiz is working (sb-agent working state).
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
