using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Wiz: the GrimX Spell crafting agent (Alt+W, Actions Ring slot 7, pipe wiz:summon). The thread is the body and
/// shows its intro at rest; one composer under it takes the goal, with the screen toggle and three starters. The
/// Spell type chip (Omnitek fills it from the goal's words, its description beside it) and the model chip sit
/// above the host line, which names the runtime and model that actually answered. The header badge names the
/// runtime model. Zone CREATE.
/// With a Nemotron model on the chip, <see cref="WizAgent"/> acts through tools and the thread shows every turn:
/// the model and its reason, each action, its result or an Approve/Skip row for anything that saves, then the
/// draft. Any other model keeps the plan-only route (Antigravity CLI or the Ark gateway).
/// </summary>
public sealed class WizCraftingWidgetForm : Form, IWizThread
{
    private const int ThreadHeight = 260;

    private const string ThreadIntro =
        "Wiz turns a goal into a Spell and can act on it: read the screen around the wand, open the lasso or picker, draft an AInbox handoff, list Spell slots, and, with your approval, save to Codex or queue a work order.\n\n" +
        "Pick a starter below, or describe what you want done. Turn on the screen icon when the goal is about what you are looking at.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly WizAgent _agent;
    private string _agentModel = WizAgent.DefaultReasoningModel;
    private CancellationTokenSource? _runCancel;

    // Approval: one row under the thread, shown only while an action that saves waits for the operator.
    private readonly Panel _approvalRow;
    private readonly DataLabel _approvalLine;
    private TaskCompletionSource<bool>? _approval;

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

    public WizCraftingWidgetForm(ArkStationClient ark, Action<bool> setThinkingState, WizAgentHost agentHost)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;
        _agent = new WizAgent(ark, agentHost);

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
        // Wiz keeps its own model: the agent's reasoner (Nemotron 3 Super by default), not the HUD's shared pick.
        _modelChip = new ModelChip(_ark, null, () => _agentModel, m => _agentModel = m) { Dock = DockStyle.Left, Name = "Wiz.ModelPicker" };
        _modelChip.ModelChanged += (_, _) => ShowRuntime(null, null);
        chipRow.Controls.Add(_modelChip);
        chipRow.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        chipRow.Controls.Add(_lensChip);

        // Under the chips: the host line, and the one primary.
        var hostRow = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Wiz.HostRow" };
        _hostLine = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Wiz.RuntimeLine" };
        _sendBtn = new PhosphorButton("Send to Wiz", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 146, Name = "Wiz.CraftButton" };
        _sendBtn.Click += async (_, _) =>
        {
            if (_working) { _runCancel?.Cancel(); return; }
            await SubmitCraftingPromptAsync();
        };
        hostRow.Controls.Add(_hostLine);
        hostRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        hostRow.Controls.Add(_sendBtn);

        // Approval row: the action on the left, Skip and Approve on the right, 44px targets.
        _approvalLine = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ember, Name = "Wiz.ApprovalLine" };
        var approveBtn = new PhosphorButton("Approve") { Dock = DockStyle.Right, Width = 120, Height = 44, Name = "Wiz.ApproveButton" };
        var skipBtn = new PhosphorButton("Skip") { Dock = DockStyle.Right, Width = 96, Height = 44, Name = "Wiz.SkipButton" };
        approveBtn.Click += (_, _) => _approval?.TrySetResult(true);
        skipBtn.Click += (_, _) => _approval?.TrySetResult(false);
        _approvalRow = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0), Name = "Wiz.ApprovalRow" };
        _approvalRow.Controls.Add(_approvalLine);
        _approvalRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _approvalRow.Controls.Add(skipBtn);
        _approvalRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _approvalRow.Controls.Add(approveBtn);

        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_approvalRow);
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

    /// <summary>The thread shows at rest, so the widget is its full stack; the approval row joins it only while it waits.</summary>
    private void FitToThread() => FitToContent.Apply(this, c => c != _approvalRow || _approval != null);

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
        model ??= _agentModel;
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

    private async Task SubmitCraftingPromptAsync()
    {
        string prompt = _promptBox.Text.Trim();
        if (_working) return;
        if (string.IsNullOrEmpty(prompt)) { _promptBox.Focus(); return; }

        // Clearing the box re-runs the lens suggestion on empty text; keep the lens this goal is crafted with.
        string lens = _activeFlowLens;
        string lensTitle = ActiveLensTitle;
        string model = _agentModel;
        bool attachScreen = _attachScreenToggle.Selected;
        _promptBox.Clear();
        _starterRow.Visible = false;
        _promptBox.Enabled = false;
        SetWorking(true);

        // The first craft replaces the intro.
        if (!_threadStarted)
        {
            _threadStarted = true;
            _threadBox.Clear();
        }
        _threadBox.ForeColor = PhosphorTheme.Bone;
        Say($"YOU // {lensTitle.ToUpperInvariant()}", prompt, PhosphorTheme.Dim);

        var cancel = new CancellationTokenSource();
        _runCancel = cancel;
        try
        {
            if (WizAgent.CanAct(model))
            {
                Say($"WIZ // AGENT // {Short(model)} // NEBIUS TOKEN FACTORY",
                    $"Up to {WizAgent.MaxSteps} steps.{(attachScreen ? $" {ModelChip.ShortName(WizAgent.VisionModel)} reads the screen first." : "")}", PhosphorTheme.Ghost);
                var outcome = await _agent.RunAsync(prompt, lens, model, _summonPos, attachScreen, this, cancel.Token);
                _modelChip.ShowRuntime("nebius-token-factory");
                ShowRuntime("nebius-token-factory", outcome.Model);
                Say(outcome.Ok
                        ? $"WIZ // {lensTitle.ToUpperInvariant()} DRAFT // {Short(outcome.Model)}"
                        : $"WIZ // STOPPED // {Short(outcome.Model)}",
                    outcome.Text, outcome.Ok ? PhosphorTheme.Coral : PhosphorTheme.Ember);
            }
            else
            {
                // Plan-only route: tool calls need a Nemotron model on Token Factory.
                Say("WIZ // PLAN ONLY", $"{ModelChip.ShortName(model)} drafts without tools. Pick a Nemotron model on the chip to let Wiz act.", PhosphorTheme.Ghost);
                string? snapshot = attachScreen ? ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y) : null;
                var (_, runtime, answered, output) = await _ark.AskWizGeminiArgonAsync(prompt, lens, snapshot, model).WaitAsync(cancel.Token);
                _modelChip.ShowRuntime(runtime);
                ShowRuntime(runtime, answered);
                Say($"WIZ // {lensTitle.ToUpperInvariant()} DRAFT // {Short(answered)}", output, PhosphorTheme.Coral);
            }
        }
        catch (OperationCanceledException)
        {
            Say("WIZ // STOPPED", "Stopped by the operator.", PhosphorTheme.Ember);
        }
        finally
        {
            _runCancel = null;
            cancel.Dispose();
            EndApproval();
            SetWorking(false);
            _promptBox.Enabled = true;
            _lensChosenByHand = false;
            SetLens("flow", confident: false);
            _promptBox.Focus();
        }
    }

    /// <summary>While Wiz runs, the one primary turns into Stop.</summary>
    private void SetWorking(bool working)
    {
        _working = working;
        _sendBtn.Text = working ? "Stop Wiz" : "Send to Wiz";
        _sendBtn.Invalidate();
        Invalidate();
        _setThinkingState(working);
    }

    private static string Short(string model) => ModelChip.ShortName(model).ToUpperInvariant();

    /// <summary>Appends one thread block: a mono kicker line in its color, then the body in bone.</summary>
    private void Say(string kicker, string? body, Color kickerColor)
    {
        if (IsDisposed) return;
        _threadBox.SelectionStart = _threadBox.TextLength;
        _threadBox.SelectionLength = 0;
        _threadBox.SelectionFont = PhosphorFonts.Mono(11f);
        _threadBox.SelectionColor = kickerColor;
        _threadBox.AppendText((_threadBox.TextLength > 0 ? "\n\n" : "") + kicker);
        if (!string.IsNullOrWhiteSpace(body))
        {
            _threadBox.SelectionStart = _threadBox.TextLength;
            _threadBox.SelectionFont = _threadBox.Font;
            _threadBox.SelectionColor = PhosphorTheme.Bone;
            _threadBox.AppendText("\n" + body.Trim());
        }
        _threadBox.SelectionStart = _threadBox.TextLength;
        _threadBox.ScrollToCaret();
    }

    // ---- IWizThread: the agent writes every turn here --------------------------------------------------------------

    void IWizThread.Vision(string model, string description) =>
        Say($"WIZ // VISION // {Short(model)}", description, PhosphorTheme.Patina);

    void IWizThread.Step(int step, string model, string reasoning) =>
        Say($"WIZ // STEP {step} OF {WizAgent.MaxSteps} // {Short(model)}", reasoning, PhosphorTheme.Ghost);

    void IWizThread.Action(string call) =>
        Say($"WIZ // ACTION // {call}", null, PhosphorTheme.Coral);

    void IWizThread.Result(string text, bool ok) =>
        Say(ok ? "RESULT" : "RESULT // NOT DONE", text, ok ? PhosphorTheme.Moss : PhosphorTheme.Ember);

    async Task<bool> IWizThread.ApproveAsync(string call, string effect, CancellationToken ct)
    {
        Say($"WIZ // NEEDS YOUR APPROVAL // {effect.ToUpperInvariant()}", "Approve lets Wiz do this; Skip leaves everything as it is.", PhosphorTheme.Ember);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = pending;
        _approvalLine.Text = $"APPROVE // {call}";
        FitToThread();
        if (!Visible) Show();
        try
        {
            bool approved = await pending.Task.WaitAsync(ct);
            Say(approved ? "OPERATOR // APPROVED" : "OPERATOR // SKIPPED", null, approved ? PhosphorTheme.Moss : PhosphorTheme.Ghost);
            return approved;
        }
        finally
        {
            EndApproval();
        }
    }

    /// <summary>Closes the approval row; an unanswered request resolves as Skip.</summary>
    private void EndApproval()
    {
        if (_approval == null) return;
        _approval.TrySetResult(false);
        _approval = null;
        _approvalLine.Text = "";
        FitToThread();
    }

    /// <summary>
    /// As-built render states (SurfaceCapture): "agent" draws a sample agent thread written through the same
    /// IWizThread calls a live run makes, stopped on an approval; "reset" puts the intro back.
    /// </summary>
    internal void PreviewState(string state)
    {
        if (_runCancel != null) return; // never draw over a live run
        EndApproval();
        _threadBox.Clear();
        if (state != "agent")
        {
            _threadStarted = false;
            _threadBox.ForeColor = PhosphorTheme.Dim;
            _threadBox.Text = ThreadIntro;
            _starterRow.Visible = true;
            SetWorking(false);
            ShowRuntime(null, null);
            FitToThread();
            return;
        }

        IWizThread t = this;
        string model = WizAgent.DefaultReasoningModel;
        _threadStarted = true;
        _starterRow.Visible = false;
        _threadBox.ForeColor = PhosphorTheme.Bone;
        Say("YOU // RITUAL", "Queue a nightly worktree cleanup Ritual for Arbiter and keep a note of it.", PhosphorTheme.Dim);
        Say($"WIZ // AGENT // {Short(model)} // NEBIUS TOKEN FACTORY", $"Up to {WizAgent.MaxSteps} steps.", PhosphorTheme.Ghost);
        t.Step(1, model, "Check which Spell Orb slot performs Rituals before queuing anything.");
        t.Action("list_spell_slots()");
        t.Result("[2] RITUAL // PERFORM // Scheduled cadence", true);
        t.Step(2, model, "Slot 2 performs Rituals; queue the cleanup for Arbiter.");
        t.Action("queue_work_order(agent=\"arbiter\", task=\"Nightly worktree cleanup Ritual\")");
        Say("WIZ // NEEDS YOUR APPROVAL // QUEUES A HOLODEX WORK ORDER", "Approve lets Wiz do this; Skip leaves everything as it is.", PhosphorTheme.Ember);
        _approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalLine.Text = "APPROVE // queue_work_order(agent=\"arbiter\", task=\"Nightly worktree cleanup Ritual\")";
        SetWorking(true);
        ShowRuntime("nebius-token-factory", model);
        FitToThread();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Agent edge: grey at rest, patina while Wiz is working (sb-agent working state).
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
