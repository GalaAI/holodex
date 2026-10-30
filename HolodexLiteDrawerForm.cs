using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

public sealed record SyndicateAgentInfo(
    string Id,
    string Name,
    string Role,
    string Pod,
    Color Accent);

public sealed record SyndicateWorkOrder(
    string OrderId,
    string Title,
    string OwnerAgent,
    string Status,
    string CreatedAt,
    string ExecutionId = "",
    string Result = "");

/// <summary>
/// Holodex Lite Dashboard Drawer (Ctrl+Alt+H, Mouse5, HUD button, pipe holodex:summon).
/// Docked right-edge Relic Phosphor drawer giving users a live taste of ArkStation &amp; Hivemind:
/// - Token Login bar (connects Hivemind / Arkhive token or Nebius Token Factory key into Windows Vault)
/// - 5 Lite Tabs: SESSION (Syndicate Group Chat + Work Orders + Activity), AINBOX, CODEX, GRIMX, MODS.
/// </summary>
public sealed partial class HolodexLiteDrawerForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly Action<Point> _openWiz;
    private readonly Action<Point> _openAInbox;
    private readonly Action<Point, WandCaptureTool> _openSnipOrPicker;
    private readonly Action _runWizOnboardingTutorial;
    /// <summary>Opens the Augment Inventory window (Program owns it).</summary>
    internal Action? OpenAugmentInventory { get; set; }
    /// <summary>Opens the Mouse Mod Station window (Program owns it).</summary>
    internal Action? OpenMouseModStation { get; set; }
    internal Action? OpenVault { get; set; }

    private string _activeTab = "session";
    private string _selectedSyndicateTarget = "all";

    private readonly PhosphorField _tokenField;
    private readonly Panel _tokenBar;
    private readonly Panel _tokenGap;
    private readonly LedRow _connectionTelemetry;
    private readonly PhosphorButton _replaceKeyBtn;
    private readonly PhosphorButton _hivemindBtn;
    private readonly PhosphorButton _signOutBtn;
    private readonly LedRow _hivemindLine;

    // Profile: the avatar at the foot of the rail opens the panel beside it (the account, the key and the gateway).
    private readonly AvatarButton _profileBtn;
    private readonly Panel _profilePanel;
    private readonly Panel _rail;
    private bool _profileOpen;
    private bool _connected;
    private bool _tokenBarOpen;

    private readonly Dictionary<string, RailButton> _tabButtons = new();
    /// <summary>The rail's tabs, top to bottom.</summary>
    private string[] TabOrder { get; } = Array.Empty<string>();
    private readonly Panel _contentHost;

    // Tab panels
    private Panel _sessionPanel = null!;
    private Panel _ainboxPanel = null!;
    private Panel _codexPanel = null!;
    private Panel _grimxPanel = null!;
    private Panel _modsPanel = null!;
    private HolodexStatsPanel? _statsPanel;
    private readonly HolodexProgressionService? _progression;

    // Session controls
    private ThreadView _sessionThread = null!;
    private ThreadView _workOrdersList = null!;
    private Panel _workOrdersHost = null!;
    private Control _workOrdersGap = null!;
    private IconToolButton _addOrderBtn = null!;
    /// <summary>Why Hivemind's roster did not load on the last try (null when it loaded or has not been tried).</summary>
    private string? _rosterError;
    private bool _rosterLoading;
    private PromptTextBox _sessionInputBox = null!;
    private SuggestionChip _rosterChip = null!;
    private PhosphorButton _sessionSendBtn = null!;
    private LedRow _activeRosterLabel = null!;

    // Codex: a card per saved capture.
    private ThreadView _codexList = null!;

    // GrimX: a card per mouse binding, then a card per Spell orb slot.
    private ThreadView _macroList = null!;

    // Mods / Hardware controls

    private readonly List<SyndicateWorkOrder> _workOrders = new();

    // Who the session reaches: Hivemind's live roster once signed in, Nemotron alone until then.
    private IReadOnlyList<SyndicateAgentInfo> _roster = SignedOutRoster;
    private HivemindState? _shownHivemindState;
    /// <summary>The composer, its footer and the roster line: they step aside while the profile is open over them.</summary>
    private Control[] _sessionComposerParts = Array.Empty<Control>();

    // Mouse detection launches PowerShell, so it runs once and the result is reused.
    private static MouseHardwareReport ConnectedMouse => MouseHardwareAndWandEngine.ActiveMouse;

    private bool HasNebiusKey => !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());

    /// <summary>
    /// Who the session reaches while Hivemind is not connected: one assistant, Nemotron itself, answering through
    /// <see cref="ArkStationClient.AskAsync"/>; each reply card names the model and route that answered. Once signed
    /// in, <see cref="LoadLiveRosterAsync"/> replaces it with the user's own Hivemind agents (ark_agents).
    /// </summary>
    public static readonly SyndicateAgentInfo[] SignedOutRoster =
    {
        new("nemotron", "NEMOTRON", "Answers here with NVIDIA Nemotron", "Nemotron", PhosphorTheme.Patina)
    };

    public HolodexLiteDrawerForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<Point> openWiz,
        Action<Point> openAInbox,
        Action<Point, WandCaptureTool> openSnipOrPicker,
        Action runWizOnboardingTutorial,
        HolodexProgressionService? progression = null)
    {
        _ark = ark;
        _progression = progression;
        _setThinkingState = setThinkingState;
        _openWiz = openWiz;
        _openAInbox = openAInbox;
        _openSnipOrPicker = openSnipOrPicker;
        _runWizOnboardingTutorial = runWizOnboardingTutorial;

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
        Name = "Holodex";
        Size = new Size(560, 760);
        // The rail is the drawer's left edge, flush against the frame's 3 px patina edge: with the frame's side padding
        // the rail's own surface ran on to the window edge, so its icons sat right of the column's middle.
        var frame = WidgetChrome.FramePadding();
        frame.Left = 3;
        Padding = frame;

        bool hasNebiusKey = HasNebiusKey;
        // The account, the key and the gateway live in the profile panel (the avatar in the header opens it). It opens
        // itself when something needs signing in; with everything linked the header is the title and the avatar.

        _tokenBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = PhosphorTheme.Surface, Name = "Holodex.TokenBar" };
        _tokenField = new PhosphorField("Optional: a Nebius Token Factory key, saved to Windows Credential Manager.", false, 40, mono: true)
        {
            Dock = DockStyle.Fill,
            Name = "Holodex.TokenField"
        };
        var saveTokenBtn = new PhosphorButton("Link key", PhosphorButtonVariant.Primary)
        {
            Dock = DockStyle.Right,
            Width = 124,
            Height = 40,
            Name = "Holodex.LinkTokenButton"
        };
        saveTokenBtn.Click += async (_, _) => await SaveTokenLoginAsync();
        _tokenBar.Controls.Add(_tokenField);
        _tokenBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        _tokenBar.Controls.Add(saveTokenBtn);
        _tokenGap = WidgetChrome.Spacer(8);

        // 1. Connection telemetry, always visible, with the visible way back to the key row.
        bool bundle = hasNebiusKey && ArkStationClient.HasWinCredVaultBundle();
        var connectionRow = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = PhosphorTheme.Surface, Name = "Holodex.ConnectionRow" };
        // One LED per fact, the AInbox footer's status style; it wraps whole LEDs, and FitProfile grows its row.
        _connectionTelemetry = new LedRow { Dock = DockStyle.Fill, Height = 36, Wrap = true, Name = "Holodex.ConnectionLine" };
        _connectionTelemetry.LedsChanged += (_, _) => FitProfile();
        ShowConnection(gatewayOnline: false, hasNebiusKey, bundle);
        _replaceKeyBtn = new PhosphorButton(hasNebiusKey ? "Replace key" : "Use a key") { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.ReplaceKeyButton" };
        _replaceKeyBtn.Click += (_, _) => ShowTokenBar(!_tokenBarOpen);
        connectionRow.Controls.Add(_connectionTelemetry);
        connectionRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        // Replace key keeps its 36px height at the top of the row when the LEDs wrap onto more lines.
        _replaceKeyBtn.Dock = DockStyle.Top;
        var replaceKeyHost = new Panel { Dock = DockStyle.Right, Width = 128, BackColor = PhosphorTheme.Surface };
        replaceKeyHost.Controls.Add(_replaceKeyBtn);
        connectionRow.Controls.Add(replaceKeyHost);

        // Hivemind: whether the session reaches the real agents, and the way in (the browser sign-in).
        var hivemindRow = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = PhosphorTheme.Surface, Name = "Holodex.HivemindRow" };
        _hivemindLine = new LedRow { Dock = DockStyle.Fill, Height = 36, Name = "Holodex.HivemindLine" };
        _hivemindBtn = new PhosphorButton("Sign in", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.HivemindSignInButton" };
        _hivemindBtn.Click += async (_, _) => await SignInToHivemindAsync();
        _signOutBtn = new PhosphorButton("Sign out") { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.HivemindSignOutButton" };
        _signOutBtn.Click += async (_, _) => await SignOutOfHivemindAsync();
        hivemindRow.Controls.Add(_hivemindLine);
        hivemindRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        hivemindRow.Controls.Add(_hivemindBtn);
        hivemindRow.Controls.Add(_signOutBtn);
        _ark.Hivemind.StateChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(ShowHivemindState)); };
        // A change before the window had a handle (Holodex drawn offscreen, or first opened after Hivemind connected)
        // was skipped above; catch up when the handle exists.
        HandleCreated += (_, _) => ShowHivemindState();
        // The key row is optional (Hivemind's sign-in answers without one), so Use a key is always offered.
        _connected = true;

        // Who Omniwand runs as on this machine: the Windows account, read from the system, never typed in.
        var identityLine = new LedRow { Dock = DockStyle.Top, Height = 28, Name = "Holodex.ProfileIdentity" };
        identityLine.Set((PhosphorTheme.Patina, $"WINDOWS // {Environment.UserDomainName}\\{Environment.UserName}"), (PhosphorTheme.Ghost, $"HOLODEX {HolodexVersion.Display}"));

        // The profile panel, top to bottom: identity, Hivemind (sign in or out), the gateway and key, the key row.
        // It floats beside the rail, bottom-aligned with the avatar, in the ArkStation popover look (surface, line-2
        // border, 14px corners, the AnimatedTooltip surface). Docked rows stack from the last added, so bottom first.
        _profilePanel = new Panel { BackColor = PhosphorTheme.Surface, Visible = false, Width = 440, Padding = new Padding(14, 12, 14, 8), Name = "Holodex.ProfilePanel" };
        _profilePanel.Paint += (_, e) =>
        {
            float ps = PhosphorPaint.Scale(_profilePanel);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            PhosphorPaint.StrokeRounded(e.Graphics, new RectangleF(0.5f, 0.5f, _profilePanel.Width - 1f, _profilePanel.Height - 1f), PhosphorTheme.RadiusButton * ps, PhosphorTheme.Line2);
        };
        _profilePanel.Controls.Add(_tokenBar);
        _profilePanel.Controls.Add(_tokenGap);
        _profilePanel.Controls.Add(connectionRow);
        _profilePanel.Controls.Add(WidgetChrome.Spacer(4));
        _profilePanel.Controls.Add(hivemindRow);
        _profilePanel.Controls.Add(WidgetChrome.Spacer(4));
        _profilePanel.Controls.Add(identityLine);
        // The rail is the toolbar, the full height of the drawer, as the ArkStation rail (station-shell Rail,
        // collapsed): the ArkMark on top, the tabs stacked under it (the label in each tooltip, the active tab as the
        // neutral pill), the profile avatar at its foot, every item centred on the rail's one vertical axis. Icons
        // are the web console Holodex's lucide set.
        var rail = new Panel { Dock = DockStyle.Left, Width = 60, BackColor = PhosphorTheme.Surface, Padding = new Padding(4, 0, 4, 4), Name = "Holodex.Rail" };
        _rail = rail;
        _profileBtn = new AvatarButton("Holodex.ProfileButton", "Profile") { Dock = DockStyle.Bottom, Height = 44 };
        _profileBtn.Click += (_, _) => ShowProfile(!_profileOpen);
        var tabs = new (string Id, string Label, PhosphorGlyph Glyph)[]
        {
            ("session", "Session", PhosphorGlyph.Chat),
            ("ainbox",  "AInbox",  PhosphorGlyph.Inbox),
            ("codex",   "Codex",   PhosphorGlyph.BookOpen),
            ("grimx",   "GrimX",   PhosphorGlyph.Sparkles),
            ("stats",   "Stats",   PhosphorGlyph.Stats),
            ("mods",    "Mods",    PhosphorGlyph.WandSparkles)
        };
        if (_progression is null) tabs = tabs.Where(t => t.Id != "stats").ToArray();
        TabOrder = tabs.Select(t => t.Id).ToArray();
        foreach (var (id, label, glyph) in tabs.Reverse())
        {
            string capturedId = id;
            var btn = new RailButton($"Holodex.Tab.{id}", glyph, label) { Dock = DockStyle.Top, Height = 44, Selected = id == _activeTab };
            btn.Click += (_, _) => SwitchTab(capturedId);
            _tabButtons[id] = btn;
            rail.Controls.Add(btn);
        }
        rail.Controls.Add(WidgetChrome.Spacer(Gutter));
        rail.Controls.Add(new RailMark("Holodex.RailMark") { Dock = DockStyle.Top, Height = 52 });
        rail.Controls.Add(_profileBtn);

        // 3. Content Host for the tabs
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.ContentHost" };
        BuildSessionTab();
        BuildAInboxTab();
        BuildCodexTab();
        BuildGrimXTab();
        BuildModsTab();
        if (_progression != null)
        {
            _statsPanel = new HolodexStatsPanel(_progression);
            _contentHost.Controls.Add(_statsPanel);
        }

        // Docking runs from the last control added: the rail first (the full height), then the gutter beside it, then
        // the header across the content column, then one gutter under it, then the content fills the rest.
        Controls.Add(_contentHost);
        Controls.Add(WidgetChrome.Spacer(Gutter));
        Controls.Add(_profilePanel);
        // "ARK //" only while the Ark route is really there (its gateway answered and this PC holds its key).
        var header = WidgetChrome.Attach(this, "HOLODEX // DRAWER", "Holodex", PhosphorTheme.Patina, null, mark: false,
            liveEyebrow: () => HeaderEyebrow(_ark.ArkRouteReady, "ARK // HOLODEX", "HOLODEX // DRAWER"));
        _ark.ArkApiStateChanged += (_, _) => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => header.Invalidate()); };
        header.Height = 48;
        // The rail's divider is the gutter's first pixel column, outside the rail, so the rail's whole width is the
        // column its items centre on.
        var railGutter = new Panel { Dock = DockStyle.Left, Width = Gutter, BackColor = Color.Transparent, Name = "Holodex.RailGutter" };
        railGutter.Paint += (_, e) =>
        {
            using var line = new Pen(PhosphorTheme.Line);
            e.Graphics.DrawLine(line, 0, rail.Top - railGutter.Top, 0, rail.Bottom - railGutter.Top);
        };
        Controls.Add(railGutter);
        Controls.Add(rail);
        WidgetDrag.Enable(this);
        Resize += (_, _) => PlaceProfile();

        // Sign in with Hivemind is the way in; the Nebius key row stays folded under Use a key.
        ShowTokenBar(false);
        SwitchTab("session");
        ResumeLayout(false);
    }

    /// <summary>The one gutter between the rail, the header and the content, and between stacked blocks.</summary>
    private const int Gutter = 12;

    private void BuildSessionTab()
    {
        _sessionPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionPanel" };

        // Work orders: always visible, a card per order (its empty state is a card too), like the AInbox stack.
        _workOrdersList = new ThreadView("Holodex.WorkOrdersBox") { Dock = DockStyle.Top, Height = 60, ReserveScrollbar = false };
        _workOrdersHost = WidgetChrome.Labeled("Work orders", _workOrdersList, 60);
        _workOrdersList.ContentChanged += (_, _) => SizeWorkOrders();
        var ordersGap = WidgetChrome.Spacer(Gutter);
        _workOrdersGap = ordersGap;

        // The session: a card per message, in the same card language as Wiz and the AInbox stack.
        // What the session reaches is said by an intro card each time the Hivemind state changes (ShowHivemindState).
        _sessionThread = new ThreadView("Holodex.SessionChatBox") { Dock = DockStyle.Fill, ReserveScrollbar = false };

        // Composer: the message, a bar with + work order, then who it goes to and Send.
        var composer = new ComposerSurface { Dock = DockStyle.Bottom, Name = "Holodex.SessionComposer" };
        _sessionInputBox = new PromptTextBox { Dock = DockStyle.Top, Height = 44, Prompt = "Send a message. /order queues a work order.", Name = "Holodex.SessionInput" };
        _sessionInputBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                await SendSyndicateSessionMessageAsync();
            }
        };
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Holodex.SessionBar" };
        _addOrderBtn = new IconToolButton("Holodex.AddWorkOrderBtn", PhosphorGlyph.Plus, "Add work order") { Dock = DockStyle.Left };
        _addOrderBtn.Click += (_, _) => CreateWorkOrderFromComposer();
        bar.Controls.Add(_addOrderBtn);
        composer.Content.Controls.Add(bar);
        composer.Content.Controls.Add(_sessionInputBox);
        composer.Height = composer.HeightFor(_sessionInputBox.Height + bar.Height);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionFooter" };
        _rosterChip = new SuggestionChip { Dock = DockStyle.Left, Name = "Holodex.RosterSelect" };
        // The chip picks where the message goes: an agent, or one of the owner's Hivemind Sessions.
        _rosterChip.Click += (_, _) => _rosterChip.ShowChoices(RosterChoices().ToList());
        _sessionSendBtn = new PhosphorButton("Send", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 150, Name = "Holodex.SessionSendBtn" };
        _sessionSendBtn.Click += async (_, _) => await SendSyndicateSessionMessageAsync();
        footer.Controls.Add(_rosterChip);
        footer.Controls.Add(_sessionSendBtn);

        // Roster: who the message reaches, under the chip, one LED per agent in its accent. It wraps rather than
        // dropping anyone, and refits its height when the target changes.
        _activeRosterLabel = new LedRow { Dock = DockStyle.Bottom, Height = 20, Wrap = true, Name = "Holodex.RosterLine" };
        void FitRoster() => _activeRosterLabel.Height = _activeRosterLabel.HeightFor(Math.Max(80, _sessionPanel.ClientSize.Width));
        _activeRosterLabel.LedsChanged += (_, _) => FitRoster();
        _sessionPanel.Resize += (_, _) => FitRoster();

        _sessionPanel.Controls.Add(_sessionThread);
        BuildHivemindSessions(_sessionPanel, bar);
        _sessionPanel.Controls.Add(ordersGap);
        _sessionPanel.Controls.Add(_workOrdersHost);
        var composerGap = new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = PhosphorTheme.Surface };
        var rosterGap = new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = PhosphorTheme.Surface };
        _sessionPanel.Controls.Add(composer);
        _sessionPanel.Controls.Add(composerGap);
        _sessionPanel.Controls.Add(footer);
        _sessionPanel.Controls.Add(rosterGap);
        _sessionPanel.Controls.Add(_activeRosterLabel);
        _sessionComposerParts = new Control[] { composer, composerGap, footer, rosterGap, _activeRosterLabel };

        // An empty session shows three first steps instead of a blank chat; the first real card clears them.
        _sessionThread.ControlAdded += (_, e) =>
        {
            if (_addingSteps || e.Control is not ThreadCard card || _firstSteps.Contains(card) || _firstSteps.Count == 0) return;
            if (IsHandleCreated) BeginInvoke(ClearFirstSteps);
            else ClearFirstSteps();
        };
        _ark.Hivemind.StateChanged += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshFirstSteps); };
        ShowFirstSteps();

        SetSyndicateTarget("all");
        RefreshWorkOrdersText();
        ShowHivemindState();
        _contentHost.Controls.Add(_sessionPanel);
    }

    // ---- First steps (empty session) ----

    private readonly HashSet<ThreadCard> _firstSteps = new();
    private bool _addingSteps;

    /// <summary>Opens Your ArkStation (the PC scan); set by Program.</summary>
    public Action? OpenYourArkStation { get; set; }

    /// <summary>True while the session has no conversation and shows the first steps instead.</summary>
    internal bool ShowingFirstSteps => _firstSteps.Count > 0;

    /// <summary>The first steps a session shows before any message, in order, as (name, kicker, done).</summary>
    internal static IReadOnlyList<(string Name, string Kicker, bool Done)> FirstStepsFor(bool signedIn) => new[]
    {
        ("Holodex.FirstStep.SignIn", signedIn ? "DONE // SIGNED IN WITH HIVEMIND" : "1 // SIGN IN WITH HIVEMIND", signedIn),
        ("Holodex.FirstStep.Ask", "2 // ASK NEMOTRON", false),
        ("Holodex.FirstStep.Scan", "3 // SCAN YOUR PC", false),
    };

    /// <summary>
    /// The empty session's three steps, each one real: Sign in with Hivemind (marked done once signed in), Ask
    /// Nemotron (puts the cursor in the composer) and Scan your PC (opens Your ArkStation).
    /// </summary>
    private void ShowFirstSteps()
    {
        if (_sessionThread.Cards.Any(c => !_firstSteps.Contains(c))) return;
        bool signedIn = HivemindClient.HasSavedSignIn;
        _addingSteps = true;
        try { AddFirstStepCards(signedIn); }
        finally { _addingSteps = false; }
    }

    private void AddFirstStepCards(bool signedIn)
    {
        using (_sessionThread.Batch())
        {
            foreach (var step in FirstStepsFor(signedIn))
            {
                ThreadCard card = step.Name switch
                {
                    "Holodex.FirstStep.SignIn" => _sessionThread.Add(signedIn ? ThreadTone.Ok : ThreadTone.Action, step.Kicker,
                        signedIn
                            ? $"Nemotron answers through {HivemindClient.EndpointHost}. No key needed."
                            : $"Answers need no key. {HivemindClient.SignInHelp}",
                        PhosphorGlyph.Holodex),
                    "Holodex.FirstStep.Ask" => _sessionThread.Add(ThreadTone.Action, step.Kicker,
                        "Type a question below and press Enter. NVIDIA Nemotron answers here, and the card names the model.",
                        PhosphorGlyph.Sparkles),
                    _ => _sessionThread.Add(ThreadTone.Action, step.Kicker,
                        "Your ArkStation finds the apps, local models, MCP servers and hardware on this PC and connects what Holodex can use.",
                        PhosphorGlyph.Box)
                };
                card.Name = step.Name;
                _firstSteps.Add(card);
                switch (step.Name)
                {
                    case "Holodex.FirstStep.SignIn" when !signedIn:
                        card.SetFooter("", new CardAction("Holodex.FirstStep.SignInButton", PhosphorGlyph.Link, "Sign in with Hivemind", async (c, _) =>
                        {
                            c.SetMeta("FINISH IN YOUR BROWSER");
                            bool ok = await _ark.Hivemind.SignInAsync();
                            if (!ok && !c.IsDisposed) c.SetMeta("NOT SIGNED IN // TRY AGAIN");
                        }));
                        break;
                    case "Holodex.FirstStep.Ask":
                        card.SetFooter("", new CardAction("Holodex.FirstStep.AskButton", PhosphorGlyph.Sparkles, "Ask Nemotron", (_, _) =>
                        {
                            ShowProfile(false);
                            _sessionInputBox.Focus();
                        }));
                        break;
                    case "Holodex.FirstStep.Scan":
                        card.SetFooter("", new CardAction("Holodex.FirstStep.ScanButton", PhosphorGlyph.Box, "Scan your PC", (_, _) => OpenYourArkStation?.Invoke()));
                        break;
                }
            }
        }
    }

    /// <summary>A sign-in or sign-out while the steps show redraws them (step 1 turns done, or back).</summary>
    private void RefreshFirstSteps()
    {
        if (!ShowingFirstSteps) return;
        ClearFirstSteps();
        ShowFirstSteps();
    }

    private void ClearFirstSteps()
    {
        if (_firstSteps.Count == 0) return;
        var steps = _firstSteps.ToList();
        _firstSteps.Clear();
        foreach (var card in steps)
        {
            _sessionThread.Controls.Remove(card);
            card.Dispose();
        }
        _sessionThread.Relayout();
    }

    /// <summary>A header eyebrow: the Ark wording only while the Ark route is really present, else the Holodex one.</summary>
    internal static string HeaderEyebrow(bool arkRoute, string arkEyebrow, string holodexEyebrow) => arkRoute ? arkEyebrow : holodexEyebrow;

    private void SetSyndicateTarget(string id)
    {
        CloseHivemindSession();
        var agent = _roster.FirstOrDefault(a => a.Id == id) ?? _roster[0];
        _selectedSyndicateTarget = agent.Id;
        // Signed out, the chip names the model that will answer, once ("Nemotron 3 Nano", never "Nemotron // Nemotron 3 Nano").
        bool modelOnly = agent == SignedOutRoster[0];
        _rosterChip.Label = modelOnly ? ModelChip.ShortName(_ark.SelectedModel) : agent.Name;
        _rosterChip.Detail = agent.Id == "all" || modelOnly ? "" : agent.Pod;
        _rosterChip.Accent = agent.Accent;
        _rosterChip.Confident = true;
        _rosterChip.Fit();
        string who = agent.Id == "all" ? "all" : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(agent.Name.ToLowerInvariant());
        _sessionSendBtn.Text = agent == SignedOutRoster[0] ? "Send" : $"Send to {who}";
        ShowTargetLine();
    }

    /// <summary>
    /// The line under the chip: first whether Hivemind is reached (the session thread holds only what was said), then
    /// who the message reaches. The chip names the agent and its pod; the line adds its role, or for All where it
    /// goes. The offline roster lists everyone; Hivemind's live roster (36 agents) would bury the line, so All says
    /// where it goes and the chip's menu lists the agents.
    /// </summary>
    private void ShowTargetLine()
    {
        if (_activeRosterLabel == null || _openSession != null) return; // not built yet, or an open Hivemind session has its own line (ShowSessionLine)
        var agent = _roster.FirstOrDefault(a => a.Id == _selectedSyndicateTarget) ?? _roster[0];
        string error = _ark.Hivemind.LastError;
        (Color, string) status = _ark.Hivemind.State switch
        {
            HivemindState.Connected => _roster != SignedOutRoster ? (PhosphorTheme.Moss, "Hivemind live")
                // A Hivemind account without agents (a judge's) still answers: Nemotron through model_invoke.
                : _rosterError != null && !ArkStationClient.ArkOnThisPc ? (PhosphorTheme.Moss, "Hivemind connected: Nemotron answers through it")
                : _rosterError != null ? (PhosphorTheme.Rust, _rosterError)
                : (PhosphorTheme.Moss, "Hivemind connected: loading your agents"),
            HivemindState.SigningIn => (PhosphorTheme.Seal, "Signing in: finish in the browser"),
            HivemindState.Connecting => (PhosphorTheme.Ghost, "Connecting to Hivemind"),
            HivemindState.Unreachable => (PhosphorTheme.Rust, $"Hivemind unreachable{(error.Length > 0 ? $" ({(error.Length > 48 ? error[..47] + "\u2026" : error)})" : "")}: Nemotron answers"),
            _ => (PhosphorTheme.Ember, error.Length > 0 ? error
                : _ark.LastGatewayOnline || HasNebiusKey ? "Signed out of Hivemind: Nemotron answers, sign in for work orders"
                : "Sign in with Hivemind to get answers: no key needed")
        };
        var leds = new List<(Color, string)> { status };
        // Connected with the roster not in yet: the signed-out stand-in's role would contradict the status, so it goes.
        bool rosterPending = _ark.Hivemind.State == HivemindState.Connected && _roster == SignedOutRoster;
        if (!rosterPending)
            leds.Add(agent.Id != "all"
                ? (agent.Accent, agent.Role)
                : (PhosphorTheme.Patina, $"Goes to {MissionTarget(agent)}, who orchestrates the group"));
        _activeRosterLabel.Set(leds.ToArray());
    }

    private void ShowTokenBar(bool show)
    {
        _tokenBarOpen = show;
        _tokenBar.Visible = _tokenGap.Visible = show;
        // Without a working connection the key row is the way in, so it has no Replace toggle.
        _replaceKeyBtn.Visible = _connected;
        _replaceKeyBtn.Text = show ? "Cancel" : HasNebiusKey ? "Replace key" : "Use a key";
        if (show) ShowProfile(true);
        FitProfile();
        if (show) _tokenField.Box.Focus();
    }

    /// <summary>The avatar's click, for pipe holodex:profile:open|close.</summary>
    public void SetProfileOpen(bool open) => ShowProfile(open);

    /// <summary>Opens or closes the profile panel beside the rail; the avatar is lit while it is open.</summary>
    private void ShowProfile(bool open)
    {
        _profileOpen = open;
        _profilePanel.Visible = open;
        // The panel floats over the session's foot; the composer under it would show only its top edge.
        foreach (var part in _sessionComposerParts) part.Visible = !open;
        _profileBtn.Open = open;
        _profileBtn.Invalidate();
        FitProfile();
        if (open) _profilePanel.BringToFront();
    }

    /// <summary>Sizes the profile panel to its visible rows, then places it.</summary>
    private void FitProfile()
    {
        if (_profilePanel == null) return;
        // Width first (it follows the drawer), so the docked rows have their real width before measuring.
        PlaceProfile();
        _profilePanel.PerformLayout();
        // The gateway and key line wraps in the panel's width; its row (and the Replace key beside it) grows to fit.
        if (_connectionTelemetry.Parent is Control connectionRow && _connectionTelemetry.Width > 0)
        {
            int want = Math.Max((int)Math.Round(36 * PhosphorPaint.Scale(this)), _connectionTelemetry.HeightFor(_connectionTelemetry.Width));
            if (connectionRow.Height != want) connectionRow.Height = want;
        }
        int h = _profilePanel.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height) + _profilePanel.Padding.Vertical;
        if (_profilePanel.Height != h) _profilePanel.Height = h;
        PlaceProfile();
    }

    /// <summary>Beside the rail, its bottom level with the avatar's, as wide as the content to the right.</summary>
    private void PlaceProfile()
    {
        if (_rail == null) return;
        int gap = (int)Math.Round(8 * PhosphorPaint.Scale(this));
        int x = _rail.Right + gap;
        int bottom = _rail.Top + _profileBtn.Bottom;
        _profilePanel.Width = Math.Max(_profilePanel.Width / 2, ClientSize.Width - Padding.Right - x);
        _profilePanel.Location = new Point(x, Math.Max(_rail.Top, bottom - _profilePanel.Height));
    }

    private static Color PodAccent(string token) => token switch
    {
        "coral" => PhosphorTheme.Coral,
        "seal" => PhosphorTheme.Seal,
        _ => PhosphorTheme.Patina
    };

    /// <summary>A list of cards that stays at its top, for the tabs that show records rather than a conversation.</summary>
    private static ThreadView CardList(string name) => new(name) { Dock = DockStyle.Fill, FollowNewest = false, ReserveScrollbar = false };

    private void BuildAInboxTab()
    {
        _ainboxPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.AInboxPanel" };

        var openFullBtn = new PhosphorButton("Open AInbox (Ctrl+Alt+I)")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.OpenAInboxButton"
        };
        openFullBtn.Click += (_, _) => _openAInbox(Location);

        // A card per pod inbox in its zone accent: what the pod handles, then its channel and conductor as LEDs.
        var podBox = CardList("Holodex.PodInboxList");
        bool ark = ArkStationClient.ArkOnThisPc;
        podBox.AddSection(ark ? "Pod inboxes // Handoff Scroll routing" : "Destinations // Where AInbox sends a Scroll");
        if (!ark)
        {
            // Public install: the destination table, built from what is connected (Holodex chat first).
            foreach (var d in HandoffDestinations.Current(_ark))
            {
                var card = podBox.Add(ThreadTone.Action, d.Label, d.Detail, leds: new[]
                {
                    d.Kind == DestinationKind.HolodexChat ? (PhosphorTheme.Patina, "Default") : (PhosphorTheme.Ghost, d.Kind switch
                    {
                        DestinationKind.SaveOnPc => @"Documents\Holodex\Scrolls",
                        DestinationKind.Hivemind => HivemindClient.EndpointHost,
                        _ => "Connected in the Vault"
                    })
                });
                card.Name = $"Holodex.Destination.{d.Id}";
            }
        }
        foreach (var p in ark ? ArkStationClient.PodInboxes : Array.Empty<ArkPodInboxInfo>())
        {
            Color accent = PodAccent(p.AccentToken);
            var card = podBox.Add(ThreadTone.Action, $"{p.Label} // {p.Zone}", p.Description, leds: new[]
            {
                (accent, p.InboxChannel),
                ark ? (PhosphorTheme.Patina, $"Conductor {p.OrchestratorLabel}") : (PhosphorTheme.Patina, "Documents\\Holodex\\Scrolls")
            });
            card.EdgeOverride = accent;
        }

        _ainboxPanel.Controls.Add(podBox);
        _ainboxPanel.Controls.Add(WidgetChrome.Spacer(Gutter));
        _ainboxPanel.Controls.Add(openFullBtn);
        _contentHost.Controls.Add(_ainboxPanel);
    }

    private void BuildCodexTab()
    {
        _codexPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.CodexPanel" };

        var btnRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = PhosphorTheme.Surface };
        var lassoBtn = new PhosphorButton("Lasso (Ctrl+Alt+S)")
        {
            Dock = DockStyle.Left,
            Width = 216,
            Name = "Holodex.LassoButton"
        };
        lassoBtn.Click += (_, _) => _openSnipOrPicker(Location, WandCaptureTool.FreeformLasso);

        var elemBtn = new PhosphorButton("Point at UI (Ctrl+Alt+E)")
        {
            Dock = DockStyle.Right,
            Width = 216,
            Name = "Holodex.PointButton"
        };
        elemBtn.Click += (_, _) => _openSnipOrPicker(Location, WandCaptureTool.ElementPicker);
        btnRow.Controls.Add(elemBtn);
        btnRow.Controls.Add(lassoBtn);

        _codexList = CardList("Holodex.CodexListBox");
        RefreshCodexEntries();

        _codexPanel.Controls.Add(_codexList);
        _codexPanel.Controls.Add(WidgetChrome.Spacer(Gutter));
        _codexPanel.Controls.Add(btnRow);
        _contentHost.Controls.Add(_codexPanel);
    }

    private void BuildGrimXTab()
    {
        _grimxPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.GrimXPanel" };

        var wizBtnRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = PhosphorTheme.Surface };
        var openWizBtn = new PhosphorButton("Open Wiz")
        {
            Dock = DockStyle.Left,
            Width = 224,
            Name = "Holodex.OpenWizButton"
        };
        openWizBtn.Click += (_, _) => _openWiz(Location);

        var autoMacroBtn = new PhosphorButton("Reset my buttons")
        {
            Dock = DockStyle.Right,
            Width = 210,
            Name = "Holodex.AutoBindButton"
        };
        autoMacroBtn.Click += (_, _) =>
        {
            var hw = ConnectedMouse;
            var defaults = MouseHardwareAndWandEngine.GetDefaultMacroBindings(hw);
            try
            {
                MouseHardwareAndWandEngine.SaveMacroBindings(defaults);
            }
            catch (Exception ex)
            {
                PhosphorToast.Show(
                    "Buttons not saved",
                    $"Your buttons could not be saved: {ex.Message}",
                    "MOUSE // BUTTONS",
                    4200,
                    ToastTone.Warning);
                return;
            }
            RefreshGrimXMacroReadout();
            PhosphorToast.Show(
                "Buttons reset",
                $"Your {hw.Name} has its starting buttons back.",
                "MOUSE // BUTTONS",
                3400,
                ToastTone.Success);
        };
        wizBtnRow.Controls.Add(autoMacroBtn);
        wizBtnRow.Controls.Add(openWizBtn);

        _macroList = CardList("Holodex.GrimXList");
        RefreshGrimXMacroReadout();

        _grimxPanel.Controls.Add(_macroList);
        _grimxPanel.Controls.Add(WidgetChrome.Spacer(Gutter));
        _grimxPanel.Controls.Add(wizBtnRow);
        _contentHost.Controls.Add(_grimxPanel);
    }

    private void BuildModsTab()
    {
        _modsPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.ModsPanel" };

        // Wiz Automated Onboarding & Animated Tooltip Tutorial button
        var onboardBtn = new PhosphorButton("Show me around")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.RunOnboardingBtn"
        };
        onboardBtn.Click += (_, _) => _runWizOnboardingTutorial();

        // Every Augment by Shell: the one inventory page from Ark, in its own window.
        var inventoryBtn = new PhosphorButton("Augment Inventory")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.AugmentInventoryBtn"
        };
        inventoryBtn.Click += (_, _) => OpenAugmentInventory?.Invoke();

        // The mouse, its speed, the pointer look and Input Studio live in the Mouse Mod Station, in its own window.
        var stationBtn = new PhosphorButton("Mouse Mod Station")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.MouseModStationBtn"
        };
        stationBtn.Click += (_, _) => OpenMouseModStation?.Invoke();
        // The Vault: apps, services and keys, on this PC with or without Ark or Hivemind.
        var vaultBtn = new PhosphorButton("Vault and adapters")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.VaultBtn"
        };
        vaultBtn.Click += (_, _) => OpenVault?.Invoke();

        // What is equipped: the spellbook's gesture casts, then the mods, a card each.
        var modsInfoBox = CardList("Holodex.ModsInfoBox");
        modsInfoBox.AddSection("Spells you draw");
        // Name only the arming keys that are switched on (tray: Spells // hold a key, then draw).
        string arm = (OmniwandSpellGestureEngine.HoldCtrlToArm, OmniwandSpellGestureEngine.HoldRightMouseToArm) switch
        {
            (true, true) => "Hold Ctrl or right-click",
            (false, true) => "Hold right-click",
            _ => "Hold Ctrl"
        };
        foreach (var (spell, gesture, does) in new[]
        {
            ("Time Splitter", $"{arm}, then shake side to side.", "Asks Wiz about it"),
            ("Copycat", $"{arm}, then shake up and down.", "Cuts out a picture"),
            ("Mutation", $"{arm}, then draw a circle.", "Casts a spell"),
            ("Synthesize", $"{arm}, then sweep to the right.", "Opens Holodex")
        })
            modsInfoBox.Add(ThreadTone.Action, $"Spell // {spell}", gesture, leds: new[] { (PhosphorTheme.Patina, does) });
        modsInfoBox.AddSection("What is turned on");
        foreach (var (mod, what, leds) in new (string, string, (Color, string)[])[]
        {
            ("Shape-changing pointer", "It turns into a line when you can type, glows on links, and spins while it waits.", new[] { (PhosphorTheme.Moss, "Smooth") }),
            ("Knows your mouse", "Finds the mouse you are moving and uses its own speed and buttons.", new[] { (PhosphorTheme.Moss, "Switches by itself") }),
            ("Safe key keeper", "Keeps your model key locked in Windows, never in a file.", new[] { (PhosphorTheme.Patina, "Windows vault") }),
            ("Hivemind session bridge", "Messages and work orders become real Hivemind missions over MCP. Signed out, Nemotron answers and every card names its model.", new[] { (PhosphorTheme.Patina, HivemindClient.EndpointHost), (PhosphorTheme.Ghost, "Signed out: Nemotron") })
        })
            modsInfoBox.Add(ThreadTone.Step, $"Mod // {mod}", what, leds: leds);

        _modsPanel.Controls.Add(modsInfoBox);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(Gutter));
        _modsPanel.Controls.Add(onboardBtn);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(6));
        _modsPanel.Controls.Add(vaultBtn);
        // The Augment Inventory reads the owner's Ark API's Shell registry; offered only when that feed answers.
        if (AugmentInventoryForm.FeedAvailable(_ark))
        {
            _modsPanel.Controls.Add(WidgetChrome.Spacer(6));
            _modsPanel.Controls.Add(inventoryBtn);
        }
        if (MouseModStationForm.Available(_ark))
        {
            _modsPanel.Controls.Add(WidgetChrome.Spacer(6));
            _modsPanel.Controls.Add(stationBtn);
        }

        _contentHost.Controls.Add(_modsPanel);
    }

    private Panel PanelFor(string tabId) => tabId switch
    {
        "ainbox" => _ainboxPanel,
        "codex" => _codexPanel,
        "grimx" => _grimxPanel,
        "mods" => _modsPanel,
        "stats" when _statsPanel != null => _statsPanel,
        _ => _sessionPanel
    };

    /// <summary>
    /// Shows <paramref name="tabId"/>'s panel. The swap is one frame: the content host holds the old tab on screen
    /// while the new one is shown (before the old one hides, so the host is never empty) and its list catches up,
    /// then paints once. Codex and GrimX rebuild their cards only when what they list has changed.
    /// </summary>
    public void SwitchTab(string tabId)
    {
        // Unknown tab names (for example holodex:tab:bogus) keep the current tab.
        tabId = (tabId ?? string.Empty).Trim().ToLowerInvariant();
        if (!_tabButtons.ContainsKey(tabId)) return;
        _activeTab = tabId;
        foreach (var (id, btn) in _tabButtons)
        {
            if (btn.Selected == (id == tabId)) continue;
            btn.Selected = id == tabId;
            btn.Invalidate();
        }

        var show = PanelFor(tabId);
        using (PhosphorPaint.FreezeRedraw(_contentHost))
        {
            _contentHost.SuspendLayout();
            show.Visible = true;
            foreach (string id in TabOrder)
                if (PanelFor(id) != show) PanelFor(id).Visible = false;
            _contentHost.ResumeLayout(true);
            if (tabId == "codex") RefreshCodexEntries();
            if (tabId == "grimx") RefreshGrimXMacroReadout();
            if (tabId == "stats") _statsPanel?.Refresh();
        }
    }

    /// <summary>Steps the rail one tab down (<paramref name="step"/> 1) or up (-1), wrapping at either end.</summary>
    private void StepTab(int step)
    {
        int at = Array.IndexOf(TabOrder, _activeTab);
        SwitchTab(TabOrder[((at < 0 ? 0 : at) + step + TabOrder.Length) % TabOrder.Length]);
    }

    /// <summary>Ctrl+1 to Ctrl+5 open a tab; Ctrl+Tab and Ctrl+PageDown step down the rail, with Shift or PageUp up it.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.PageDown:
                StepTab(1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
            case Keys.Control | Keys.PageUp:
                StepTab(-1);
                return true;
        }
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            int n = (keyData & Keys.KeyCode) - Keys.D1;
            if (n < 0 || n >= TabOrder.Length) n = (keyData & Keys.KeyCode) - Keys.NumPad1;
            if (n >= 0 && n < TabOrder.Length)
            {
                SwitchTab(TabOrder[n]);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// For pipe holodex:tabs:time: each tab's switch through its first paint, in ms, over <paramref name="rounds"/>
    /// passes of the rail from top to bottom. The drawer must be on screen for the paint to count.
    /// </summary>
    internal string TimeTabSwitches(int rounds)
    {
        var times = TabOrder.ToDictionary(id => id, _ => new List<long>());
        for (int r = 0; r < rounds; r++)
            foreach (string id in TabOrder)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SwitchTab(id);
                Update();
                times[id].Add(sw.ElapsedMilliseconds);
            }
        SwitchTab(TabOrder[0]);
        return string.Join(",", TabOrder.Select(id => $"{id}={string.Join("/", times[id])}ms"));
    }

    /// <summary>The captures the Codex list shows now; null forces the next refresh to rebuild.</summary>
    private IReadOnlyList<CodexCaptureEntry>? _codexShown;

    /// <summary>
    /// For pipe holodex:roster:shoot: opens the Send-to picker, presses <paramref name="keys"/> (comma-separated, for
    /// example Down,Down), waits <paramref name="settleMs"/>, captures the chip and its drop to <paramref name="file"/>,
    /// then closes it.
    /// </summary>
    internal string ShootRoster(string file, string keys, int settleMs)
    {
        if (!Visible) return "error:holodex:roster:drawer is not open";
        SwitchTab("session");
        void Pump(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }
        // A drop still running its exit (the last shot's) finishes first.
        while (_rosterChip.IsOpen) Pump(50);
        Pump(300);
        _rosterChip.ShowChoices(RosterChoices().ToList());
        if (_rosterChip.Drop is not { } drop) return "error:holodex:roster:no drop";
        Pump(350);
        foreach (string k in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<Keys>(k, true, out var key)) { drop.Press(key); Pump(60); }
        Pump(settleMs);
        var area = Rectangle.Union(drop.Bounds, _rosterChip.RectangleToScreen(_rosterChip.ClientRectangle));
        area.Inflate(24, 24);
        area.Intersect(Screen.FromRectangle(drop.Bounds).Bounds);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        using (var bmp = new Bitmap(area.Width, area.Height))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        string at = $"drop={drop.Bounds.X},{drop.Bounds.Y},{drop.Width}x{drop.Height}";
        drop.Close(ToolStripDropDownCloseReason.CloseCalled);
        return $"ok:holodex:roster:{file}:{at}";
    }

    public void RefreshCodexEntries()
    {
        if (_codexList == null) return;
        var entries = ScreenSnipAndElementPickerForm.LoadCodexEntries();
        if (_codexShown != null && _codexShown.SequenceEqual(entries)) return;
        ShowCodexEntries(entries);
    }

    private void ShowCodexEntries(IReadOnlyList<CodexCaptureEntry> entries)
    {
        _codexShown = entries;
        using var batch = _codexList.Batch();
        _codexList.Clear();
        _codexList.AddSection("Codex // lasso captures and element picks");
        if (entries.Count == 0) _codexList.Add(ThreadTone.Intro, "None yet", ScreenSnipAndElementPickerForm.CodexEmptyStateText);
        foreach (var e in entries)
        {
            var glyph = e.Kind.EndsWith("element", StringComparison.OrdinalIgnoreCase) ? PhosphorGlyph.Pick
                : e.Kind.EndsWith("lasso", StringComparison.OrdinalIgnoreCase) ? PhosphorGlyph.Lasso
                : PhosphorGlyph.Rect;
            // The palette LED is drawn in the captured color itself.
            Color swatch = PhosphorTheme.Ghost;
            try { if (e.HexColor.StartsWith('#')) swatch = ColorTranslator.FromHtml(e.HexColor); }
            catch (Exception) { }
            _codexList.Add(ThreadTone.Action, $"{e.Kind} // {ModelChip.ShortName(e.ModelUsed)}", $"{e.Title}\n{e.Summary}", glyph, new[]
            {
                (swatch, e.HexColor),
                (PhosphorTheme.Patina, e.ElementClass),
                (PhosphorTheme.Ghost, e.Geometry)
            });
        }
    }

    /// <summary>The GrimX buttons for the mouse in use now.</summary>
    public void ShowActiveMouse() => RefreshGrimXMacroReadout();

    /// <summary>What the GrimX list shows now (the mouse and each binding's words), so a switch back skips an unchanged rebuild.</summary>
    private string? _grimxShown;

    public void RefreshGrimXMacroReadout()
    {
        if (_macroList == null) return;
        var hw = ConnectedMouse;
        var macros = MouseHardwareAndWandEngine.LoadOrCreateMacroBindings(hw);
        string shown = hw.Name + "\u0001" + string.Join("\u0001", macros.Select(m => $"{m.TriggerLabel}\u0002{m.ActionLabel}\u0002{m.Parameter}"));
        if (shown == _grimxShown) return;
        _grimxShown = shown;
        using var batch = _macroList.Batch();
        _macroList.Clear();
        // The full model is on the Mods tab; each binding names the device it reads.
        _macroList.AddSection($"Buttons // {hw.Name}");
        foreach (var m in macros)
            _macroList.Add(ThreadTone.Action, m.TriggerLabel, m.ActionLabel,
                leds: string.IsNullOrWhiteSpace(m.Parameter) ? null : new[] { (PhosphorTheme.Patina, m.Parameter) });

        _macroList.AddSection("Spell orb // 8 slots // middle-hold or F15");
        foreach (var s in GrimXFlowOrb.Slots)
        {
            string slotLabel = string.Equals(s.Verb, s.Title, StringComparison.OrdinalIgnoreCase) ? s.Title : $"{s.Verb} {s.Title}";
            var card = _macroList.Add(ThreadTone.Step, $"Slot {s.Index + 1} // {slotLabel}", s.Subtitle,
                leds: new[] { (s.AccentColor, $"{s.ZoneToken} zone") });
            card.EdgeOverride = s.AccentColor;
        }
    }

    /// <summary>For the as-built render: "codex" shows one saved element pick as a card; "reset" reloads the real list.</summary>
    internal void PreviewState(string state)
    {
        if (state is "session-empty" or "session-conversation")
        {
            PreviewSession(state == "session-conversation");
            return;
        }
        if (state != "codex")
        {
            RestoreSession();
            RefreshCodexEntries();
            return;
        }
        ShowCodexEntries(new[]
        {
            new CodexCaptureEntry("codex:preview", "wand.element", "ArkStation: ArkStation // Holodex Drawer",
                "A docked drawer with a tab for each part of Holodex. The active tab uses the coral aura; the Session composer sits on the Phosphor surface.",
                "Chrome_WidgetWin_1", "270x184 @ (450,92)", "#79C4B7", "nvidia/Nemotron-3.5-Super-VL", null, DateTime.UtcNow.ToString("O"))
        });
    }

    private List<Control>? _stashedSession;

    /// <summary>
    /// For the as-built render: the session as a new user sees it (the first steps) or with a short conversation.
    /// The live session's cards are set aside and come back on reset.
    /// </summary>
    private void PreviewSession(bool conversation)
    {
        if (_stashedSession == null)
        {
            _stashedSession = _sessionThread.Controls.Cast<Control>().Where(c => c is not ThreadCard card || !_firstSteps.Contains(card)).ToList();
            foreach (var c in _stashedSession) _sessionThread.Controls.Remove(c);
        }
        ClearFirstSteps();
        foreach (var c in _sessionThread.Controls.Cast<Control>().ToList()) { _sessionThread.Controls.Remove(c); c.Dispose(); }
        if (!conversation)
        {
            ShowFirstSteps();
            return;
        }
        _sessionThread.Add(ThreadTone.You, "YOU // TO NEMOTRON", "What can Holodex do on this PC?");
        _sessionThread.Add(ThreadTone.Agent, "NEMOTRON 3 NANO // VIA HIVEMIND",
            "Ask about anything on your screen, cut out a picture and ask about it, and send captures to AInbox. Your ArkStation lists the tools on this PC Holodex can connect.");
    }

    private void RestoreSession()
    {
        if (_stashedSession == null) return;
        ClearFirstSteps();
        foreach (var c in _sessionThread.Controls.Cast<Control>().ToList()) { _sessionThread.Controls.Remove(c); c.Dispose(); }
        foreach (var c in _stashedSession) _sessionThread.Controls.Add(c);
        _stashedSession = null;
        _sessionThread.Relayout();
        if (!_sessionThread.Cards.Any()) ShowFirstSteps();
    }

    private void RefreshWorkOrdersText()
    {
        // The panel stays: one empty-state card, or a card per order, two tall before it scrolls.
        using var batch = _workOrdersList.Batch();
        _workOrdersList.Clear();
        // Work orders are Hivemind missions: signed out there is nothing to send them to, so the block and + go,
        // and /order says to sign in.
        bool live = _ark.Hivemind.State == HivemindState.Connected && HivemindClient.Granted("runtime.execute");
        _workOrdersHost.Visible = _workOrdersGap.Visible = live || _workOrders.Count > 0;
        _addOrderBtn.Visible = live;
        _sessionInputBox.Prompt = live ? "Send a message. /order queues a work order." : "Send a message.";
        if (_workOrders.Count == 0)
            _workOrdersList.Add(ThreadTone.Intro, "NONE YET", "Add one with + or /order. Each one becomes a Hivemind mission.");
        foreach (var wo in _workOrders)
            _workOrdersList.Add(ThreadTone.Action, $"{wo.OrderId} // {wo.Status} // {wo.OwnerAgent} // {wo.CreatedAt}", wo.Title, PhosphorGlyph.Holodex,
                wo.ExecutionId.Length > 0 ? new[] { (PhosphorTheme.Patina, $"Mission {ShortId(wo.ExecutionId)}") } : null);
        SizeWorkOrders();
    }

    /// <summary>Fits the list to two cards, then it scrolls; reruns when its width or content changes.</summary>
    private void SizeWorkOrders()
    {
        // Measured at the full width, as the cards are laid out while two or fewer fit (ReserveScrollbar off).
        int width = Math.Max(80, _workOrdersList.ClientSize.Width - 2);
        int content = _workOrdersList.ContentHeight(width) + 2;
        int cap = _workOrdersList.Cards.Take(2).Sum(c => c.HeightFor(width) + c.Margin.Vertical) + 2;
        int want = Math.Min(content, cap);
        if (_workOrdersList.Height == want) return;
        _workOrdersList.Height = want;
        _workOrdersHost.Height = 18 + 4 + want;
    }

    /// <summary>The connection row's LEDs: which route answers, the key, and whether the key is in the vault.</summary>
    private void ShowConnection(bool gatewayOnline, bool keyed, bool bundle)
    {
        var leds = new List<(Color, string)>();
        if (gatewayOnline) leds.Add((PhosphorTheme.Moss, "ARKSTATION GATEWAY ONLINE"));
        if (keyed)
        {
            leds.Add((PhosphorTheme.Moss, "NEBIUS KEY LINKED"));
            leds.Add((PhosphorTheme.Patina, "NEMOTRON READY"));
            if (bundle) leds.Add((PhosphorTheme.Moss, "WINDOWS VAULT"));
        }
        else if (!gatewayOnline)
            // No key is needed: a Hivemind sign-in answers with Nemotron. The key is the optional advanced route.
            leds.Add(HivemindClient.HasSavedSignIn
                ? (PhosphorTheme.Patina, "NEMOTRON VIA HIVEMIND // NO KEY NEEDED")
                : (PhosphorTheme.Ember, "SIGN IN WITH HIVEMIND ABOVE // KEY OPTIONAL"));
        _connectionTelemetry.Set(leds.ToArray());
    }

    private void CreateWorkOrderFromComposer()
    {
        string text = _sessionInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            PhosphorToast.Show("Work Order", "Type the task in the message box first.", "HOLODEX // WORK ORDERS", 2800, ToastTone.Warning);
            return;
        }
        if (AddWorkOrder(_selectedSyndicateTarget, text) != null) _sessionInputBox.Clear();
    }

    /// <summary>
    /// Queues a work order for a Syndicate agent (roster id or name; "all" goes to Wiz), the same way the session
    /// composer's + does. Returns the order id, or null with the reason when the agent or task is not valid.
    /// </summary>
    public string? QueueWorkOrder(string agent, string task, out string error)
    {
        string text = task.Trim();
        string who = agent.Trim();
        var match = _roster.FirstOrDefault(a =>
            string.Equals(a.Id, who, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, who, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            error = $"Unknown agent '{agent}'. Use one of: {string.Join(", ", _roster.Select(a => a.Id))}.";
            return null;
        }
        if (string.IsNullOrEmpty(text))
        {
            error = "The task is empty.";
            return null;
        }
        if (_ark.Hivemind.State != HivemindState.Connected)
        {
            error = SignInForOrders;
            return null;
        }
        error = string.Empty;
        return AddWorkOrder(match.Id, text)?.OrderId;
    }

    private const string SignInForOrders = "Sign in to Hivemind to send work orders.";

    /// <summary>
    /// Adds a work order and dispatches it to Hivemind as a real mission; its status follows the mission's. Signed
    /// out there is nowhere to send it, so nothing is added and the toast says to sign in.
    /// </summary>
    private SyndicateWorkOrder? AddWorkOrder(string targetId, string text)
    {
        if (_ark.Hivemind.State != HivemindState.Connected)
        {
            PhosphorToast.Show("Work orders need Hivemind", SignInForOrders, "HOLODEX // WORK ORDERS", 3200, ToastTone.Warning);
            return null;
        }
        if (!HivemindClient.Granted("runtime.execute"))
        {
            PhosphorToast.Show("Work orders need agent access", HivemindClient.NoAgentsOnThisSignIn, "HOLODEX // WORK ORDERS", 4200, ToastTone.Warning);
            return null;
        }
        var agent = _roster.FirstOrDefault(a => a.Id == targetId) ?? _roster[0];
        string missionTarget = MissionTarget(agent);
        string owner = missionTarget.ToUpperInvariant();
        string orderId = $"WO-{100 + _workOrders.Count + 1}";
        var order = new SyndicateWorkOrder(orderId, text, owner, "DISPATCHING", "Just now");
        _workOrders.Insert(0, order);
        RefreshWorkOrdersText();
        _ = RunMissionAsync(missionTarget, text, $"WORK ORDER {orderId}", mission => UpdateWorkOrder(orderId, mission));
        return order;
    }

    private void UpdateWorkOrder(string orderId, HivemindMission mission)
    {
        int i = _workOrders.FindIndex(w => w.OrderId == orderId);
        if (i < 0) return;
        _workOrders[i] = _workOrders[i] with { Status = mission.Status.ToUpperInvariant(), ExecutionId = mission.ExecutionId, Result = mission.Result };
        RefreshWorkOrdersText();
    }

    /// <summary>The Hivemind agent a roster entry dispatches to: "all" goes to Gala, the co-op orchestrator.</summary>
    private string MissionTarget(SyndicateAgentInfo agent)
    {
        if (agent.Id != "all") return agent.Id;
        return _roster.FirstOrDefault(a => a.Id == "gala")?.Id ?? _roster.FirstOrDefault(a => a.Id != "all")?.Id ?? "gala";
    }

    private static string ShortId(string executionId) => executionId.Length > 8 ? executionId[..8] : executionId;

    private static string Clip(string text) => text.Length > 140 ? text[..140] + "..." : text;

    /// <summary>
    /// Dispatches a real Hivemind mission and follows it: one action card whose result line tracks the mission's
    /// status, then the agent's answer as its own card, named by agent and model. <paramref name="onStatus"/> hears
    /// every status change (work orders use it).
    /// </summary>
    private async Task RunMissionAsync(string agentId, string objective, string what, Action<HivemindMission>? onStatus)
    {
        _setThinkingState(true);
        try
        {
            var card = _sessionThread.Add(ThreadTone.Action, $"{what} // HIVEMIND MISSION // TO {agentId.ToUpperInvariant()}", objective, PhosphorGlyph.Holodex);
            var mission = await _ark.Hivemind.DispatchAsync(agentId, objective);
            if (mission == null)
            {
                card.SetResult($"Not dispatched: {_ark.Hivemind.LastError}", false);
                return;
            }
            onStatus?.Invoke(mission);
            card.SetResult($"Mission {ShortId(mission.ExecutionId)} // {mission.Status}", true);

            // Hivemind's missions time out at 10 minutes by default (30 at most); five failed polls in a row stop it.
            int misses = 0;
            var deadline = DateTime.UtcNow.AddMinutes(31);
            while (!mission.Finished && DateTime.UtcNow < deadline && misses < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                var next = await _ark.Hivemind.MissionStatusAsync(mission.ExecutionId);
                if (next == null) { misses++; continue; }
                misses = 0;
                next = next with { AgentId = mission.AgentId };
                if (next.Status != mission.Status)
                {
                    onStatus?.Invoke(next);
                    card.SetResult($"Mission {ShortId(next.ExecutionId)} // {next.Status}", next.Status != "failed");
                }
                mission = next;
            }

            // The model exactly as Hivemind's receipt names it, in the body (kickers are drawn uppercase), so the id can
            // be checked against Hivemind; it is the receipt's claim, not Omniwand's.
            string model = $"\n\nModel (Hivemind receipt): {(mission.Model.Length > 0 ? mission.Model : "not reported")}";
            if (mission.Status == "completed")
            {
                // The answer is the body; the receipt's verdict and constraints are one LED each under it.
                var reply = mission.Reply();
                var leds = new List<(Color, string)>();
                if (reply.Validation.Length > 0)
                    leds.Add((reply.Validation.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? PhosphorTheme.Moss
                        : reply.Validation.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ? PhosphorTheme.Rust : PhosphorTheme.Ghost,
                        $"Validation // {reply.Validation}"));
                if (reply.Constraints.Length > 0) leds.Add((PhosphorTheme.Ghost, reply.Constraints));
                _sessionThread.Add(ThreadTone.Agent, $"{mission.AgentId.ToUpperInvariant()} // HIVEMIND",
                    (reply.Answer.Length > 0 ? reply.Answer : "Completed with no text result.") + model, leds: leds.Count > 0 ? leds : null);
            }
            else if (mission.Finished)
                _sessionThread.Add(ThreadTone.Fail, $"{mission.AgentId.ToUpperInvariant()} // {mission.Status.ToUpperInvariant()} // HIVEMIND",
                    (mission.Error.Length > 0 ? mission.Error : $"The mission ended {mission.Status}.") + model);
            else
                card.SetResult($"Mission {ShortId(mission.ExecutionId)} // still {mission.Status}; stopped following it ({(misses >= 5 ? _ark.Hivemind.LastError : "after 31 minutes")})", false);
            if (!Visible || !_sessionPanel.Visible)
                PhosphorToast.Show($"{mission.AgentId} // {mission.Status}", Clip(mission.Status == "completed" ? mission.Result : mission.Error),
                    "HOLODEX // HIVEMIND // CTRL+ALT+H", 4200, mission.Status == "completed" ? ToastTone.Success : ToastTone.Warning);
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    /// <summary>The Hivemind row's LED: whether the session reaches the real agents (the intro card says the rest).</summary>
    private (Color, string) HivemindLed() => _ark.Hivemind.State switch
    {
        HivemindState.Connected => _roster != SignedOutRoster ? (PhosphorTheme.Moss, $"HIVEMIND // LIVE // {_roster.Count - 1} AGENTS")
            : _rosterError != null ? (PhosphorTheme.Rust, "HIVEMIND // CONNECTED // AGENTS NOT LOADED")
            : (PhosphorTheme.Moss, "HIVEMIND // CONNECTED // LOADING AGENTS"),
        HivemindState.SigningIn => (PhosphorTheme.Seal, "HIVEMIND // FINISH IN THE BROWSER"),
        HivemindState.Connecting => (PhosphorTheme.Ghost, "HIVEMIND // CONNECTING"),
        HivemindState.Unreachable => (PhosphorTheme.Rust, "HIVEMIND // UNREACHABLE"),
        _ => (PhosphorTheme.Ember, "HIVEMIND // SIGNED OUT")
    };

    /// <summary>
    /// Reflects the Hivemind state: the sign-in button, the connection LED, the roster, and the line under the chip
    /// saying what a message reaches now. The session thread stays empty until something is sent.
    /// </summary>
    private void ShowHivemindState()
    {
        var state = _ark.Hivemind.State;
        _hivemindBtn.Visible = state is HivemindState.NotSignedIn or HivemindState.Unreachable;
        _hivemindBtn.Text = state == HivemindState.Unreachable ? "Reconnect" : "Sign in";
        _hivemindBtn.Enabled = state != HivemindState.SigningIn;
        _signOutBtn.Visible = state is HivemindState.Connected or HivemindState.Unreachable;
        if (state != HivemindState.Connected && _roster != SignedOutRoster) SetRoster(SignedOutRoster);
        // Every arrival at Connected loads the user's agents, whichever path connected (sign-in, reconnect, a call).
        if (state == HivemindState.Connected && _roster == SignedOutRoster) _ = LoadLiveRosterAsync();
        var led = HivemindLed();
        _hivemindLine.Set(led);
        _profileBtn.Status = led.Item1;
        ShowTargetLine();
        // Connecting is the moment before the saved sign-in is tried: nothing opens until it succeeds or fails.
        if (state == _shownHivemindState || state == HivemindState.Connecting) return;
        _shownHivemindState = state;
        // A lost connection opens the profile to reconnect. Signed out is a normal state when something else answers:
        // the session's status line says so and Sign in is one click away on the avatar. With nothing to answer (a
        // fresh install with no key and no Ark), the profile opens on Sign in, the one step answers need.
        if (state == HivemindState.Unreachable) ShowProfile(true);
        else if (state == HivemindState.NotSignedIn && !_ark.LastGatewayOnline && !HasNebiusKey) ShowProfile(true);
        RefreshWorkOrdersText();
    }

    private void SetRoster(IReadOnlyList<SyndicateAgentInfo> roster)
    {
        _roster = roster;
        SetSyndicateTarget(_roster.Any(a => a.Id == _selectedSyndicateTarget) ? _selectedSyndicateTarget : _roster[0].Id);
    }

    /// <summary>
    /// Replaces the roster with Hivemind's live ark_agents (dispatch-ready agents first). A null or empty list says
    /// so with Hivemind's error, and the next RefreshConnectionHeaderAsync tries again.
    /// </summary>
    private async Task LoadLiveRosterAsync()
    {
        if (_rosterLoading) return;
        // A sign-in without runtime.execute (consent narrowed it) cannot ask agents: no roster to pick from, and the
        // session answers with Nemotron through model_invoke.
        if (!HivemindClient.Granted("runtime.execute"))
        {
            _rosterError = HivemindClient.NoAgentsOnThisSignIn;
            _hivemindLine.Set(HivemindLed());
            ShowTargetLine();
            return;
        }
        _rosterLoading = true;
        IReadOnlyList<HivemindAgent>? agents;
        try { agents = await _ark.Hivemind.ListAgentsAsync(); }
        finally { _rosterLoading = false; }
        if (agents == null || agents.Count == 0)
        {
            string why = _ark.Hivemind.LastError;
            _rosterError = agents == null
                ? $"Hivemind: could not load agents{(why.Length > 0 ? $" ({(why.Length > 60 ? why[..59] + "\u2026" : why)})" : "")}"
                : "Hivemind: could not load agents (the roster is empty)";
            _hivemindLine.Set(HivemindLed());
            ShowTargetLine();
            return;
        }
        _rosterError = null;
        var live = new List<SyndicateAgentInfo> { new("all", "ALL SYNDICATE", "Gala orchestrates the group", "Hivemind", PhosphorTheme.Patina) };
        live.AddRange(agents.OrderByDescending(a => a.DispatchReady).ThenBy(a => a.Id).Select(a => new SyndicateAgentInfo(
            a.Id,
            a.Name.ToUpperInvariant(),
            a.Role.Length > 0 ? a.Role : (a.DispatchReady ? "Ready for missions" : "Not ready for missions"),
            a.Provider.Length > 0 ? a.Provider : "Hivemind",
            a.DispatchReady ? PhosphorTheme.Patina : PhosphorTheme.Ghost)));
        SetRoster(live);
        _hivemindLine.Set(HivemindLed());
        await RefreshRecentSessionsAsync();
    }

    private async Task SignOutOfHivemindAsync()
    {
        await _ark.Hivemind.SignOutAsync();
        PhosphorToast.Show("Signed out of Hivemind", "The saved sign-in was removed from Windows Credential Manager.", "HOLODEX // HIVEMIND", 3200, ToastTone.Info);
        await RefreshConnectionHeaderAsync();
    }

    private async Task SignInToHivemindAsync()
    {
        bool ok = await _ark.Hivemind.SignInAsync();
        if (!ok)
            PhosphorToast.Show("Hivemind sign-in did not finish", _ark.Hivemind.LastError, "HOLODEX // HIVEMIND", 5200, ToastTone.Warning);
        await RefreshConnectionHeaderAsync();
        // Signed in: the profile has done its job, so the session it covered comes back.
        if (ok) ShowProfile(false);
    }

    /// <summary>The work orders with each one's full Hivemind execution id, for pipe holodex:orders.</summary>
    public string DescribeWorkOrders() => string.Join(" || ", _workOrders.Select(w =>
        $"{w.OrderId}|{w.Status}|{w.OwnerAgent}|{w.ExecutionId}|raw={w.Result.Replace("\r", "").Replace("\n", "\\n")}"));

    /// <summary>The session's newest cards as they read (kicker, body, result), newest last, for pipe holodex:thread.</summary>
    public string DescribeSessionThread(int count) => string.Join(" || ", _sessionThread.Cards.TakeLast(Math.Max(1, count))
        .Select(c => $"[{c.Kicker}] {c.Body}{(c.Result != null ? $" => {c.Result}" : "")}{(c.Leds.Count > 0 ? " leds=" + string.Join(" | ", c.Leds.Select(l => l.Text)) : "")}".Replace("\r", " ").Replace("\n", " ")));

    /// <summary>Sends <paramref name="message"/> to <paramref name="agentId"/> exactly as the session's Send button does.</summary>
    /// <summary>
    /// A Handoff Scroll from AInbox on a public install: it lands in the Session chat as the user's card, the
    /// destination answers in a card under it, and the drawer opens on Session.
    /// </summary>
    internal async Task ReceiveHandoffAsync(HandoffDestination to, string title, string scroll)
    {
        SummonDrawer(Cursor.Position, "session");
        _sessionThread.Add(ThreadTone.You, $"YOU // HANDOFF TO {to.Label.ToUpperInvariant()}", scroll.Length > 1200 ? scroll[..1200] + "..." : scroll);
        _setThinkingState(true);
        var pending = _sessionThread.AddPending($"{to.Label.ToUpperInvariant()} // ANSWERING");
        try
        {
            var (ok, text, by) = await HandoffDestinations.DeliverAsync(to, title, scroll, _ark);
            pending.Resolve(ok ? ThreadTone.Agent : ThreadTone.Fail, ok ? by : $"NOT ANSWERED // {by}", text);
            if (!Visible || !_sessionPanel.Visible)
                PhosphorToast.Show(ok ? $"{to.Label} replied" : "Not answered", Clip(text), "HOLODEX // HANDOFF", 4200, ok ? ToastTone.Info : ToastTone.Warning);
        }
        catch (Exception ex)
        {
            if (pending.Pending) pending.Resolve(ThreadTone.Fail, $"{to.Label.ToUpperInvariant()} // NOT ANSWERED", ex.Message);
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    public async void SendSessionMessage(string agentId, string message)
    {
        if (_roster.Any(a => a.Id == agentId.Trim())) SetSyndicateTarget(agentId.Trim());
        _sessionInputBox.Text = message;
        await SendSyndicateSessionMessageAsync();
    }

    private async Task SendSyndicateSessionMessageAsync()
    {
        string msg = _sessionInputBox.Text.Trim();
        if (string.IsNullOrEmpty(msg)) return;
        _sessionInputBox.Clear();

        if (msg.StartsWith("/order ", StringComparison.OrdinalIgnoreCase))
        {
            _sessionInputBox.Text = msg[7..].Trim();
            CreateWorkOrderFromComposer();
            return;
        }

        if (_openSession != null)
        {
            await ReplyInHivemindSessionAsync(msg);
            return;
        }

        var agent = _roster.FirstOrDefault(a => a.Id == _selectedSyndicateTarget) ?? _roster[0];
        _sessionThread.Add(ThreadTone.You, $"YOU // TO {agent.Name}", msg);

        // Missions go to the user's Hivemind agents once their roster is in. A Hivemind with no agents for this account
        // (a judge's) answers below: Nemotron through model_invoke, named on the card.
        if (_ark.Hivemind.State == HivemindState.Connected && _roster != SignedOutRoster && HivemindClient.Granted("runtime.execute"))
        {
            await RunMissionAsync(MissionTarget(agent), msg, "MESSAGE", null);
            return;
        }

        // Nemotron answers as itself (Ark API, Nebius key or Hivemind), and the card names the model and the route.
        _setThinkingState(true);
        var pending = _sessionThread.AddPending($"{ModelChip.ShortName(_ark.SelectedModel).ToUpperInvariant()} // ANSWERING");
        try
        {
            var answer = await _ark.AskAsync(msg);
            string label = $"{ModelChip.ShortName(answer.Model).ToUpperInvariant()} // {answer.RouteLabel}";
            pending.Resolve(answer.Ok ? ThreadTone.Agent : ThreadTone.Fail, answer.Ok ? label : $"NOT ANSWERED // {label}", answer.Output);
            if (!Visible || !_sessionPanel.Visible)
                PhosphorToast.Show(answer.Ok ? $"{ModelChip.ShortName(answer.Model)} replied" : "Not answered", Clip(answer.Output),
                    "HOLODEX // SESSION // CTRL+ALT+H", 4200, answer.Ok ? ToastTone.Info : ToastTone.Warning);
        }
        catch (Exception ex)
        {
            if (pending.Pending) pending.Resolve(ThreadTone.Fail, $"{agent.Name.ToUpperInvariant()} // NOT ANSWERED", ex.Message);
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    private async Task SaveTokenLoginAsync()
    {
        string token = _tokenField.Box.Text.Trim();
        if (string.IsNullOrEmpty(token))
        {
            PhosphorToast.Show("No key to link", "Paste a Nebius Token Factory key first.", "HOLODEX // VAULT", 2800, ToastTone.Warning);
            return;
        }

        bool saved = _ark.SaveTokenToWindowsVault(token);
        _tokenField.Box.Clear();
        ShowTokenBar(false);
        await RefreshConnectionHeaderAsync();

        if (saved)
        {
            PhosphorToast.Show(
                "Token Saved to Windows Vault",
                "Stored in Windows Credential Manager (wincred://) and applied to this session.",
                "KEYMASTER // WINCRED VAULT",
                3600,
                ToastTone.Success);
        }
        else
        {
            PhosphorToast.Show(
                "Token Applied for Session Only",
                "Could not write to Windows Credential Manager. The token works until Holodex closes.",
                "KEYMASTER // WINCRED VAULT",
                4200,
                ToastTone.Warning);
        }
    }

    public async Task RefreshConnectionHeaderAsync()
    {
        // The saved Hivemind sign-in only: this never opens the browser.
        if (await _ark.Hivemind.EnsureConnectedAsync()) await LoadLiveRosterAsync();
        var st = await _ark.CheckStatusAsync();
        bool bundle = ArkStationClient.HasWinCredVaultBundle();
        // The key row never forces itself open: Sign in with Hivemind is the way in, and Use a key opens the row.
        _connected = true;
        ShowConnection(st.GatewayOnline, st.NebiusKeyLoaded, st.NebiusKeyLoaded && bundle);
        ShowTokenBar(_tokenBarOpen);
        ShowHivemindState();
    }

    public async void SummonDrawer(Point cursorPos, string? tab = null)
    {
        if (!string.IsNullOrEmpty(tab))
        {
            SwitchTab(tab);
        }

        // The drawer's 560 x 820 is logical: at 150% it is 840 x 1230 device pixels, capped by the work area.
        var work = Screen.FromPoint(cursorPos).WorkingArea;
        float s = DeviceDpi / 96f; // the monitor's DPI even before the first Show (PhosphorPaint.Scale is 1 until then)
        int h = Math.Min((int)Math.Round(820 * s), work.Height - (int)Math.Round(24 * s));
        Size = new Size((int)Math.Round(560 * s), h);
        Location = new Point(work.Right - Width - 12, work.Top + (work.Height - h) / 2);

        // The drawer lives on the right edge, so it slides in from there.
        SurfaceMotion.Present(this, fromRight: true);
        Activate();
        await RefreshConnectionHeaderAsync();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Patina);
    }
}
