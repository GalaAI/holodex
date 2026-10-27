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
    private const int ThreadMaxHeight = 300;

    private const string ThreadIntro =
        "Wiz turns a goal into a Spell and can act on it: read the screen around the wand, open the lasso or picker, draft an AInbox handoff, list Spell slots, and, with your approval, save to Codex or queue a work order.\n\n" +
        "Pick a starter below, or describe what you want done. Turn on the screen icon when the goal is about what you are looking at.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly WizAgent _agent;
    private string _agentModel = WizAgent.DefaultReasoningModel;
    private CancellationTokenSource? _runCancel;

    // Approval: a card in the thread, shown while an action that saves waits for the operator.
    private TaskCompletionSource<bool>? _approval;
    private ThreadCard? _approvalCard;
    private ThreadCard? _lastAction;

    private readonly ThreadView _thread;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _attachScreenToggle;
    private readonly FlowLayoutPanel _starterRow;
    private readonly SuggestionChip _lensChip;
    private readonly ModelChip _modelChip;
    private readonly LedRow _hostLine;
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
        Name = "Wiz";
        Size = new Size(620, 560);
        Padding = WidgetChrome.FramePadding();

        // The thread is the body: its intro card at rest, then a card per turn, in the AInbox card language.
        _thread = new ThreadView("Wiz.ThreadBox") { Dock = DockStyle.Top, Height = 160 };
        _thread.ContentChanged += (_, _) => SizeThread();
        ShowIntro();

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
        _hostLine = new LedRow { Dock = DockStyle.Fill, Height = 44, Name = "Wiz.RuntimeLine" };
        _sendBtn = new PhosphorButton("Send to Wiz", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 146, Name = "Wiz.CraftButton" };
        _sendBtn.Click += async (_, _) =>
        {
            if (_working) { _runCancel?.Cancel(); return; }
            await SubmitCraftingPromptAsync();
        };
        hostRow.Controls.Add(_hostLine);
        hostRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        hostRow.Controls.Add(_sendBtn);

        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_thread);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(chipRow);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = Color.Transparent });
        Controls.Add(hostRow);
        WidgetChrome.Attach(this, "4GE // GRIMX SPELLS", "Wiz", PhosphorTheme.Coral, null);
        WidgetDrag.Enable(this);

        SetLens("flow", confident: false);
        ShowRuntime(null, null);
        FitToThread();
        ResumeLayout(false);
    }

    /// <summary>The thread fits its cards up to a cap, then scrolls; the widget follows it.</summary>
    private void SizeThread()
    {
        int want = Math.Clamp(_thread.ContentHeight(_thread.CardWidth) + 2, 96, ThreadMaxHeight);
        if (_thread.Height == want) return;
        _thread.Height = want;
        if (IsHandleCreated) FitToThread();
    }

    /// <summary>The thread shows at rest, so the widget is always its full stack.</summary>
    private void FitToThread() => FitToContent.Apply(this, _ => true);

    private void ShowIntro()
    {
        _thread.Clear();
        _lastAction = null;
        _thread.Add(ThreadTone.Intro, "WIZ // READY", ThreadIntro, PhosphorGlyph.Wiz);
    }

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
    /// The host line. Before a run it names where the chip's model will run; after one, the host that actually
    /// answered. The model is said once by the chip, and each step card names the model that wrote it.
    /// </summary>
    private void ShowRuntime(string? runtime, string? model)
    {
        model ??= _agentModel;
        string host = runtime != null
            ? HostName(runtime)
            : ArkStationClient.IsNemotronModel(model) || !_ark.LastGatewayOnline ? "NEBIUS TOKEN FACTORY" : "ANTIGRAVITY CLI";
        _hostLine.Set((PhosphorTheme.Moss, host));
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
            _thread.Clear();
        }
        Say(ThreadTone.You, $"YOU // {lensTitle.ToUpperInvariant()}", prompt);

        var cancel = new CancellationTokenSource();
        _runCancel = cancel;
        try
        {
            if (WizAgent.CanAct(model))
            {
                Say(ThreadTone.Step, $"WIZ // AGENT // {Short(model)} // NEBIUS TOKEN FACTORY",
                    $"Up to {WizAgent.MaxSteps} steps.{(attachScreen ? $" {ModelChip.ShortName(WizAgent.VisionModel)} reads the screen first." : "")}");
                var outcome = await _agent.RunAsync(prompt, lens, model, _summonPos, attachScreen, this, cancel.Token);
                _modelChip.ShowRuntime("nebius-token-factory");
                ShowRuntime("nebius-token-factory", outcome.Model);
                Say(outcome.Ok ? ThreadTone.Agent : ThreadTone.Fail,
                    outcome.Ok
                        ? $"WIZ // {lensTitle.ToUpperInvariant()} DRAFT // {Short(outcome.Model)}"
                        : $"WIZ // STOPPED // {Short(outcome.Model)}",
                    outcome.Text);
                if (outcome.Ok) ToastCrafted(lensTitle);
                else ToastIfAway("Wiz stopped", outcome.Text, false);
            }
            else
            {
                // Plan-only route: tool calls need a Nemotron model on Token Factory.
                Say(ThreadTone.Step, "WIZ // PLAN ONLY", $"{ModelChip.ShortName(model)} drafts without tools. Pick a Nemotron model on the chip to let Wiz act.");
                string? snapshot = attachScreen ? ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y) : null;
                var (_, runtime, answered, output) = await _ark.AskWizGeminiArgonAsync(prompt, lens, snapshot, model).WaitAsync(cancel.Token);
                _modelChip.ShowRuntime(runtime);
                ShowRuntime(runtime, answered);
                Say(ThreadTone.Agent, $"WIZ // {lensTitle.ToUpperInvariant()} DRAFT // {Short(answered)}", output);
                ToastCrafted(lensTitle);
            }
        }
        catch (OperationCanceledException)
        {
            Say(ThreadTone.Fail, "WIZ // STOPPED", "Stopped by the operator.");
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

    /// <summary>The catalog's Flow crafted toast: the draft is in the thread, and the next step is a ring slot.</summary>
    private static void ToastCrafted(string lensTitle) =>
        PhosphorToast.Show($"{lensTitle} crafted", $"{lensTitle} drafted in Wiz. Bind it to a ring slot in Input Studio.", "WIZ // 4GE BENCH", 3200, ToastTone.Success);

    /// <summary>A run that ends while Wiz is hidden or behind another window says so with a toast.</summary>
    private void ToastIfAway(string title, string text, bool ok)
    {
        if (Visible && ContainsFocus) return;
        string first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        PhosphorToast.Show(title, first.Length > 140 ? first[..140] + "..." : first, "WIZ // ALT+W OPENS IT", 4200, ok ? ToastTone.Success : ToastTone.Warning);
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

    /// <summary>Adds one card to the thread.</summary>
    private ThreadCard? Say(ThreadTone tone, string kicker, string? body, PhosphorGlyph? glyph = null)
    {
        if (IsDisposed) return null;
        return _thread.Add(tone, kicker, body, glyph);
    }

    /// <summary>The tool's own glyph on its action card, so a run reads like the AInbox stack.</summary>
    private static PhosphorGlyph ToolGlyph(string call) => call.Split('(')[0].Trim() switch
    {
        "capture_screen" => PhosphorGlyph.Screen,
        "open_lasso" => PhosphorGlyph.Lasso,
        "open_element_picker" => PhosphorGlyph.Pick,
        "draft_ainbox_handoff" => PhosphorGlyph.AInbox,
        "save_to_codex" => PhosphorGlyph.Codex,
        "queue_work_order" => PhosphorGlyph.Holodex,
        _ => PhosphorGlyph.Wiz
    };

    // ---- IWizThread: the agent writes every turn here --------------------------------------------------------------

    void IWizThread.Vision(string model, string description) =>
        Say(ThreadTone.Action, $"VISION // {Short(model)}", description, PhosphorGlyph.Screen);

    void IWizThread.Step(int step, string model, string reasoning) =>
        Say(ThreadTone.Step, $"STEP {step} OF {WizAgent.MaxSteps} // {Short(model)}", reasoning);

    void IWizThread.Action(string call) =>
        _lastAction = Say(ThreadTone.Action, $"ACTION // {call.Split('(')[0].Trim()}", call, ToolGlyph(call));

    void IWizThread.Result(string text, bool ok)
    {
        if (_lastAction is { Result: null } card) card.SetResult(text, ok);
        else Say(ok ? ThreadTone.Ok : ThreadTone.Fail, ok ? "RESULT" : "RESULT // NOT DONE", text);
        _lastAction = null;
    }

    async Task<bool> IWizThread.ApproveAsync(string call, string effect, CancellationToken ct)
    {
        var pending = BeginApproval(call, effect);
        if (!Visible) Show();
        try
        {
            bool approved = await pending.Task.WaitAsync(ct);
            _approvalCard?.Settle(approved);
            _approvalCard = null;
            return approved;
        }
        finally
        {
            EndApproval();
        }
    }

    /// <summary>An approval card at the bottom of the thread: what Wiz wants to do, with Skip and Approve.</summary>
    private TaskCompletionSource<bool> BeginApproval(string call, string effect)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = pending;
        // The action card right above shows the call; this card is only the decision.
        _approvalCard = Say(ThreadTone.Approval, $"NEEDS YOUR APPROVAL // {effect}", "Approve lets Wiz do this; Skip leaves everything as it is.", ToolGlyph(call));
        if (_approvalCard != null) _approvalCard.Answered += a => pending.TrySetResult(a);
        return pending;
    }

    /// <summary>Closes a pending approval; an unanswered request resolves as Skip.</summary>
    private void EndApproval()
    {
        if (_approval == null) return;
        _approval.TrySetResult(false);
        _approval = null;
        _approvalCard?.Settle(false);
        _approvalCard = null;
    }

    /// <summary>
    /// Live capture (the pipe's wiz:submit): sends <paramref name="goal"/> through the real composer and Send path,
    /// without the screen. A run that reaches an approval waits there; nothing here approves it.
    /// </summary>
    internal async Task SubmitForCaptureAsync(string goal)
    {
        bool screen = _attachScreenToggle.Selected;
        _attachScreenToggle.Selected = false;
        _promptBox.Text = goal;
        try { await SubmitCraftingPromptAsync(); }
        finally { _attachScreenToggle.Selected = screen; }
    }

    /// <summary>
    /// As-built render states (SurfaceCapture): "agent" draws a sample agent thread written through the same
    /// IWizThread calls a live run makes, stopped on an approval; "reset" puts the intro back.
    /// </summary>
    internal void PreviewState(string state)
    {
        if (_runCancel != null) return; // never draw over a live run
        EndApproval();
        if (state != "agent")
        {
            _threadStarted = false;
            ShowIntro();
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
        _thread.Clear();
        Say(ThreadTone.You, "YOU // RITUAL", "Queue a nightly worktree cleanup Ritual for Arbiter and keep a note of it.");
        t.Step(1, model, "Check which Spell Orb slot performs Rituals before queuing anything.");
        t.Action("list_spell_slots()");
        t.Result("[2] RITUAL // PERFORM // Scheduled cadence", true);
        t.Step(2, model, "Slot 2 performs Rituals; queue the cleanup for Arbiter.");
        const string queue = "queue_work_order(agent=\"arbiter\", task=\"Nightly worktree cleanup Ritual\")";
        t.Action(queue);
        BeginApproval(queue, "QUEUES A HOLODEX WORK ORDER");
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
