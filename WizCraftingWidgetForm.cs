using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Wiz: the GrimX Spell crafting thread (Alt+W, Actions Ring slot 7, pipe wiz:summon). The thread is the body and
/// shows its intro at rest; one composer under it takes the goal, with the screen toggle and three starters. The
/// Spell type chip (Omnitek fills it from the goal's words, its description beside it) and the model chip sit
/// above the host line, which names the runtime and model that actually answered. The header badge names the
/// runtime model. Zone CREATE.
/// </summary>
public sealed class WizCraftingWidgetForm : Form
{
    private const int ThreadHeight = 260;

    private const string ThreadIntro =
        "Wiz turns a goal into a Combo: an ordered chain of steps you can follow, refine or hand off.\n\n" +
        "Pick a starter below, or describe what you want done. Turn on the screen icon when the goal is about what you are looking at.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly PhosphorBadge _runtimeBadge;
    private readonly RichTextBox _threadBox;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _attachScreenToggle;
    private readonly FlowLayoutPanel _starterRow;
    private readonly SuggestionChip _lensChip;
    private readonly ModelChip _modelChip;
    private readonly DataLabel _hostLine;
    private readonly PhosphorButton _sendBtn;

    private Point _summonPos;
    private string _activeFlowLens = "flow";
    private bool _lensChosenByHand;
    private bool _working;
    private bool _threadStarted;

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

    private (string Id, string Title, string Verb) ActiveLens => FlowLenses.FirstOrDefault(l => l.Id == _activeFlowLens) is var l && l.Id != null ? l : FlowLenses[0];
    private string ActiveLensTitle => ActiveLens.Title;

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

        _runtimeBadge = new PhosphorBadge("Nemotron", PhosphorBadgeState.Accent) { AccentColor = PhosphorTheme.Coral, Name = "Wiz.RuntimeBadge" };

        // The thread is the body: its intro at rest, the crafts after.
        _threadBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = ThreadHeight,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(13f),
            Name = "Wiz.ThreadBox",
            Text = ThreadIntro
        };
        PhosphorPaint.UseDarkScrollbars(_threadBox);

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
        _promptBox.TextChanged += (_, _) => { SuggestLens(); _starterRow!.Visible = _promptBox.TextLength == 0 && !_threadStarted; };

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

        // Chips: what kind of Spell (with its description) and which model.
        var chipRow = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Wiz.ChipRow" };
        _lensChip = new SuggestionChip { Dock = DockStyle.Left, ShowArrow = false, Accent = PhosphorTheme.Coral, Name = "Wiz.FlowLensSelect" };
        _lensChip.Click += (_, _) => _lensChip.ShowChoices(FlowLenses.Select(l => new ChipChoice(
            l.Title, l.Verb, l.Id == _activeFlowLens, () => { _lensChosenByHand = true; SetLens(l.Id, confident: false); })));
        _modelChip = new ModelChip(_ark) { Dock = DockStyle.Left, Name = "Wiz.ModelPicker" };
        _modelChip.ModelChanged += (_, _) => ShowRuntime(null, null);
        chipRow.Controls.Add(_modelChip);
        chipRow.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        chipRow.Controls.Add(_lensChip);

        // Under the chips: the host line, and the one primary.
        var hostRow = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Wiz.HostRow" };
        _hostLine = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Wiz.RuntimeLine" };
        _sendBtn = new PhosphorButton("Send to Wiz", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Wiz.CraftButton" };
        _sendBtn.Click += async (_, _) => await SubmitCraftingPromptAsync();
        hostRow.Controls.Add(_hostLine);
        hostRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        hostRow.Controls.Add(_sendBtn);

        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_threadBox);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(chipRow);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = Color.Transparent });
        Controls.Add(hostRow);
        WidgetChrome.Attach(this, "4GE // GRIMX SPELLS", "Wiz", PhosphorTheme.Coral, _runtimeBadge);
        WidgetDrag.Enable(this);

        SetLens("flow", confident: false);
        ShowRuntime(null, null);
        FitToThread();
    }

    /// <summary>The thread shows at rest, so the widget is always its full stack.</summary>
    private void FitToThread() => FitToContent.Apply(this, _ => true);

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
        _lensChip.Label = ActiveLens.Title;
        _lensChip.Detail = ActiveLens.Verb;
        _lensChip.Confident = confident;
        _lensChip.Fit();
    }

    /// <summary>
    /// Badge and host line. Before a run they name the runtime the chip's model will use; after one, the runtime
    /// and model that actually answered.
    /// </summary>
    private void ShowRuntime(string? runtime, string? model)
    {
        model ??= _ark.SelectedModel;
        string host = runtime != null
            ? HostName(runtime)
            : ArkStationClient.IsNemotronModel(model) || !_ark.LastGatewayOnline ? "NEBIUS TOKEN FACTORY" : "ANTIGRAVITY CLI";
        string shortModel = ModelChip.ShortName(model);
        _runtimeBadge.Text = shortModel;
        _runtimeBadge.FitToText();
        _hostLine.Text = $"HOST // {host} // MODEL // {shortModel.ToUpperInvariant()}";
    }

    /// <summary>Runtime ids from AskWizGeminiArgonAsync ("nebius-token-factory", "subscription-gemini (agy)", "ark-model-gateway") as hosts.</summary>
    private static string HostName(string runtime)
    {
        string r = runtime.ToLowerInvariant();
        if (r.Contains("nebius") || r.Contains("token-factory")) return "NEBIUS TOKEN FACTORY";
        if (r.Contains("gateway")) return "ARK MODEL GATEWAY";
        if (r.Contains("gemini") || r.Contains("agy") || r.Contains("antigravity")) return "ANTIGRAVITY CLI";
        return runtime.Replace('-', ' ').ToUpperInvariant();
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

        // The first craft replaces the intro.
        bool first = !_threadStarted;
        if (first)
        {
            _threadStarted = true;
            _threadBox.Clear();
        }
        _threadBox.ForeColor = PhosphorTheme.Bone;
        _threadBox.AppendText($"{(first ? "" : "\n\n")}YOU // {lensTitle.ToUpperInvariant()}\n{prompt}\n\nWIZ // crafting");
        _threadBox.SelectionStart = _threadBox.TextLength;
        _threadBox.ScrollToCaret();

        string? snapshot = _attachScreenToggle.Selected
            ? ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y)
            : null;

        try
        {
            var (ok, runtime, model, output) = await _ark.AskWizGeminiArgonAsync(prompt, lens, snapshot);
            _modelChip.ShowRuntime(runtime);
            ShowRuntime(runtime, model);
            _threadBox.AppendText($"\n\nWIZ // {lensTitle.ToUpperInvariant()} DRAFT // {ModelChip.ShortName(model).ToUpperInvariant()}\n{output}");
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
