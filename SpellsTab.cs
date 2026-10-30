using System.Drawing;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The drawer's Spells tab: the orb inventory (<see cref="SpellOrbsView"/>) above the Wiz composer. The orbs read
/// Input Spine's bound Spells and the spellbook's Wiz drafts, nothing else. The composer is the shared one
/// (<see cref="ComposerSurface"/>, <see cref="PromptTextBox"/>, a Spell kind chip that Omnitek fills from the goal's
/// words as in Wiz) and crafts through Wiz's own path (<see cref="ArkStationClient.AskWizAsync"/>, which offers the
/// connected <see cref="HolodexTools"/>). A craft that answers is kept in the <see cref="Spellbook"/> and lands in its
/// kind's orb; one that does not says why on the status line and keeps nothing.
/// </summary>
internal sealed class SpellsTab : Panel
{
    /// <summary>Wiz's crafting call: (prompt, kind) to (ok, runtime, model, draft).</summary>
    internal delegate Task<(bool Ok, string Runtime, string Model, string Output)> Crafter(string prompt, string kind, CancellationToken ct);

    private readonly ArkStationClient _ark;
    private readonly Spellbook _spellbook;
    private readonly Crafter _craft;
    private readonly Func<Task<Dictionary<string, List<SpellChoice>>>> _readSpine;
    private readonly SpellOrbsView _orbs;
    private readonly Panel _composerHost;
    private readonly PromptTextBox _prompt;
    private readonly SuggestionChip _kindChip;
    private readonly PhosphorButton _craftBtn;
    private readonly LedRow _status;
    private string _kind = "spell";
    private bool _kindByHand;
    private bool _working;
    private Task? _refresh;
    private CancellationTokenSource? _cancel;

    /// <summary>The model Wiz crafts with: its own reasoner, as the Wiz widget defaults to.</summary>
    public const string CraftModel = WizAgent.DefaultReasoningModel;

    public SpellsTab(ArkStationClient ark, Action openWiz, Spellbook? spellbook = null, Crafter? craft = null,
        Func<Task<Dictionary<string, List<SpellChoice>>>>? readSpine = null)
    {
        _ark = ark;
        _spellbook = spellbook ?? new Spellbook();
        _craft = craft ?? ((prompt, kind, ct) => ark.AskWizAsync(prompt, kind, null, CraftModel).WaitAsync(ct));
        _readSpine = readSpine ?? ark.GetGrimXSpellsAsync;
        Dock = DockStyle.Fill;
        BackColor = PhosphorTheme.Surface;
        Name = "Holodex.SpellsTab";

        // One column edge, as the session: the composer keeps its TraceBeam room and every row takes the same inset.
        var column = new Padding((int)ComposerSurface.InsetPx, 0, (int)ComposerSurface.InsetPx, 0);

        _orbs = new SpellOrbsView { Dock = DockStyle.Fill, Padding = column };

        var composer = new ComposerSurface { Dock = DockStyle.Bottom, Name = "Holodex.SpellComposer" };
        _prompt = new PromptTextBox { Dock = DockStyle.Top, Height = 44, Prompt = "Describe a Spell. Wiz crafts it into the right orb.", Name = "Holodex.SpellInput" };
        _prompt.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            await CraftAsync();
        };
        _prompt.TextChanged += (_, _) => SuggestKind();
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Holodex.SpellComposerBar" };
        var wizBtn = new IconToolButton("Holodex.OpenWizButton", PhosphorGlyph.Wiz, "Open Wiz") { Dock = DockStyle.Left };
        wizBtn.Click += (_, _) => openWiz();
        bar.Controls.Add(wizBtn);
        composer.Content.Controls.Add(bar);
        composer.Content.Controls.Add(_prompt);
        composer.Height = composer.HeightFor(_prompt.Height + bar.Height);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = PhosphorTheme.Surface, Padding = column, Name = "Holodex.SpellFooter" };
        _kindChip = new SuggestionChip { Dock = DockStyle.Left, ShowArrow = false, Accent = PhosphorTheme.Coral, Name = "Holodex.SpellKindSelect" };
        _kindChip.Click += (_, _) => _kindChip.ShowChoices(SpellKindInfo.All.Select(k => new ChipChoice(
            k.Title, k.Description, k.Id == _kind, () => { _kindByHand = true; SetKind(k.Id, confident: false); }, Tone: k.Accent)));
        _craftBtn = new PhosphorButton("Craft Spell", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 150, Name = "Holodex.SpellCraftButton" };
        _craftBtn.Click += async (_, _) =>
        {
            if (_working) { _cancel?.Cancel(); return; }
            await CraftAsync();
        };
        footer.Controls.Add(_kindChip);
        footer.Controls.Add(_craftBtn);

        _status = new LedRow { Dock = DockStyle.Bottom, Height = 20, Padding = column, Name = "Holodex.SpellStatus" };

        // Bottom to top, as the session docks it: status, gap, footer, gap, composer.
        _composerHost = new Panel { Dock = DockStyle.Bottom, BackColor = PhosphorTheme.Surface, Name = "Holodex.SpellComposerHost" };
        _composerHost.Controls.Add(composer);
        _composerHost.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = PhosphorTheme.Surface });
        _composerHost.Controls.Add(footer);
        _composerHost.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = PhosphorTheme.Surface });
        _composerHost.Controls.Add(_status);
        _composerHost.Height = composer.Height + 10 + footer.Height + 4 + _status.Height;
        _orbs.CoverWhenOpen = _composerHost;
        // An empty orb asks the composer for one of its kind.
        _orbs.EmptyKindPicked += kind =>
        {
            _kindByHand = true;
            SetKind(kind.Id, confident: false);
            if (InActiveForm(this)) _prompt.Focus();
            ShowStatus(PhosphorTheme.Ghost, $"{kind.EmptyText.ToUpperInvariant()} // DESCRIBE ONE FOR WIZ");
        };

        Controls.Add(_orbs);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = PhosphorTheme.Surface });
        Controls.Add(_composerHost);

        SetKind("spell", confident: false);
        ShowRoute();
    }

    internal SpellOrbsView Orbs => _orbs;
    internal PromptTextBox Prompt => _prompt;
    internal string Kind => _kind;
    internal bool Working => _working;
    internal string StatusText => _status.Summary;

    /// <summary>Reads Input Spine and the spellbook again and shows what they hold. One read at a time: a call while one runs gets that one.</summary>
    public Task RefreshAsync() => _refresh is { IsCompleted: false } running ? running : _refresh = ReadAsync();

    /// <summary>A fresh read after the spellbook changed, after any read already running.</summary>
    private async Task ReloadAsync()
    {
        if (_refresh is { IsCompleted: false } running) await running;
        await (_refresh = ReadAsync());
    }

    private async Task ReadAsync()
    {
        Dictionary<string, List<SpellChoice>> spine;
        try { spine = await _readSpine(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { spine = new(); }
        var drafts = _spellbook.Load();
        if (IsDisposed) return;
        var merged = SpellInventory.Merge(spine, drafts);
        int bound = spine.Values.Sum(l => l.Count);
        var leds = new List<(Color, string)>
        {
            spine.Count > 0 ? (PhosphorTheme.Moss, $"Input Spine // {bound} bound") : (PhosphorTheme.Ghost, "Input Spine offline")
        };
        if (drafts.Count > 0) leds.Add((PhosphorTheme.Coral, $"{drafts.Count} Wiz {(drafts.Count == 1 ? "draft" : "drafts")}"));
        _orbs.SetSpells(merged, leds.ToArray());
    }

    /// <summary>Omnitek reads the goal: Spell-kind words pick the chip unless it was chosen by hand.</summary>
    private void SuggestKind()
    {
        if (_kindByHand || _working) return;
        var d = _ark.AutoDetectOmnitekItem(null, null, _prompt.Text);
        bool spell = d.ItemType == "flow" && SpellKindInfo.Find(d.ItemSubtype) != null;
        SetKind(spell ? d.ItemSubtype! : _kind, confident: spell);
    }

    internal void SetKind(string id, bool confident)
    {
        var kind = SpellKindInfo.Find(id) ?? SpellKindInfo.All[0];
        _kind = kind.Id;
        _kindChip.Label = kind.Title;
        _kindChip.Detail = kind.Description;
        _kindChip.Accent = kind.Accent;
        _kindChip.Confident = confident;
        _kindChip.Fit();
        _kindChip.Invalidate();
    }

    private void ShowStatus(Color led, string text) => _status.Set((led, text));

    /// <summary>Where Wiz will craft: the route its model takes now, or the sign-in it needs.</summary>
    private void ShowRoute()
    {
        string host = ArkStationClient.RouteLabelFor(_ark.NextTextRoute(CraftModel));
        if (host == "NOT CONNECTED") ShowStatus(PhosphorTheme.Ember, "WIZ NEEDS A MODEL // SIGN IN WITH HIVEMIND (CTRL+ALT+H)");
        else ShowStatus(PhosphorTheme.Moss, $"WIZ // {ModelChip.ShortName(CraftModel).ToUpperInvariant()} // {host}");
    }

    /// <summary>What Wiz is asked, so its draft names the Spell and says what it does on lines the spellbook reads.</summary>
    internal static string CraftPrompt(string goal, SpellKindInfo kind) =>
        $"Craft one {kind.Title} ({kind.Description}) for this goal: {goal}\n" +
        "Reply in plain text. First line: \"Name: \" and a two to five word name for the Spell. " +
        "Second line: \"Does: \" and one sentence on what it does. Then its steps, one per line.";

    /// <summary>
    /// Crafts the composer's goal with Wiz. An answer is kept in the spellbook and lands in its kind's orb; anything
    /// else (no route, a refusal, Stop) keeps nothing and says so.
    /// </summary>
    internal async Task<CraftedSpell?> CraftAsync()
    {
        string goal = _prompt.Text.Trim();
        if (_working) return null;
        if (goal.Length == 0) { if (InActiveForm(this)) _prompt.Focus(); return null; }
        var kind = SpellKindInfo.Find(_kind) ?? SpellKindInfo.All[0];
        _orbs.Close();
        SetWorking(true);
        ShowStatus(PhosphorTheme.Patina, $"WIZ IS CRAFTING A {kind.Title.ToUpperInvariant()} // {ModelChip.ShortName(CraftModel).ToUpperInvariant()}");
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        try
        {
            var (ok, runtime, model, output) = await _craft(CraftPrompt(goal, kind), kind.Id, cancel.Token);
            if (IsDisposed) return null;
            if (!ok || string.IsNullOrWhiteSpace(output))
            {
                // PlainFailure owns what a failure says: a raw gateway or worker line is logged, not shown.
                string why = PlainFailure.For(output, "Wiz").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Wiz could not answer this time.";
                ShowStatus(PhosphorTheme.Ember, $"NOT CRAFTED // {Clip(why, 90)}");
                return null;
            }
            var spell = Spellbook.FromDraft(goal, kind.Id, output, model, runtime);
            if (!_spellbook.Add(spell, out string error))
            {
                ShowStatus(PhosphorTheme.Ember, $"CRAFTED, NOT KEPT // {Clip(PlainFailure.For(error, "The spellbook"), 90)}");
                return null;
            }
            _prompt.Clear();
            _kindByHand = false;
            await ReloadAsync();
            _orbs.MarkAdded(kind.Id);
            ShowStatus(PhosphorTheme.Moss, $"{spell.Name} // ADDED TO {kind.Plural.ToUpperInvariant()} // {ModelChip.ShortName(model).ToUpperInvariant()}");
            PhosphorToast.Show($"{kind.Title} crafted", $"{spell.Name} is in your {kind.Plural} orb.", "WIZ // SPELLS", 3200, ToastTone.Success);
            return spell;
        }
        catch (OperationCanceledException)
        {
            ShowStatus(PhosphorTheme.Ghost, "STOPPED // NOTHING KEPT");
            return null;
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            if (!IsDisposed)
            {
                SetWorking(false);
                // Back to the box only if Holodex still has the operator: a craft can take a minute, and focus never
                // pulls the drawer in front of what they moved on to.
                if (InActiveForm(this)) _prompt.Focus();
            }
        }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        _prompt.Enabled = !working;
        _craftBtn.Text = working ? "Stop Wiz" : "Craft Spell";
        _craftBtn.Invalidate();
    }

    /// <summary>Whether <paramref name="c"/>'s window is the active one, so focusing inside it activates nothing.</summary>
    internal static bool InActiveForm(Control c) => c.FindForm() is { } f && Form.ActiveForm == f;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancel?.Cancel();
        base.Dispose(disposing);
    }
}
