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
    string ExecutionId = "");

/// <summary>
/// Holodex Lite Dashboard Drawer (Alt+H, Mouse5, HUD button, pipe holodex:summon).
/// Docked right-edge Relic Phosphor drawer giving users a live taste of ArkStation &amp; Hivemind:
/// - Token Login bar (connects Hivemind / Arkhive token or Nebius Token Factory key into Windows Vault)
/// - 5 Lite Tabs: SESSION (Syndicate Group Chat + Work Orders + Activity), AINBOX, CODEX, GRIMX, MODS.
/// </summary>
public sealed class HolodexLiteDrawerForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly Action<Point> _openWiz;
    private readonly Action<Point> _openAInbox;
    private readonly Action<Point, WandCaptureTool> _openSnipOrPicker;
    private readonly Action _runWizOnboardingTutorial;
    private readonly Action<CursorIconStyle> _onCursorStyleChanged;

    private string _activeTab = "session";
    private string _selectedSyndicateTarget = "all";

    private readonly PhosphorField _tokenField;
    private readonly Panel _tokenBar;
    private readonly Panel _tokenGap;
    private readonly LedRow _connectionTelemetry;
    private readonly PhosphorButton _replaceKeyBtn;
    private readonly PhosphorButton _hivemindBtn;
    private readonly LedRow _hivemindLine;
    private bool _connected;
    private bool _tokenBarOpen;

    private readonly Dictionary<string, PhosphorButton> _tabButtons = new();
    private readonly Panel _contentHost;

    // Tab panels
    private Panel _sessionPanel = null!;
    private Panel _ainboxPanel = null!;
    private Panel _codexPanel = null!;
    private Panel _grimxPanel = null!;
    private Panel _modsPanel = null!;

    // Session controls
    private ThreadView _sessionThread = null!;
    private ThreadView _workOrdersList = null!;
    private Panel _workOrdersHost = null!;
    private PromptTextBox _sessionInputBox = null!;
    private SuggestionChip _rosterChip = null!;
    private PhosphorButton _sessionSendBtn = null!;
    private LedRow _activeRosterLabel = null!;

    // Codex: a card per saved capture.
    private ThreadView _codexList = null!;

    // GrimX: a card per mouse binding, then a card per Spell orb slot.
    private ThreadView _macroList = null!;

    // Mods / Hardware controls
    private LedRow _mouseHwLabel = null!;
    private LedRow _dpiValueLabel = null!;

    private readonly List<SyndicateWorkOrder> _workOrders = new();

    // Who the session reaches: Hivemind's live roster once signed in, the offline roster until then.
    private IReadOnlyList<SyndicateAgentInfo> _roster = OfflineRoster;
    private HivemindState? _shownHivemindState;

    // Mouse detection launches PowerShell, so it runs once and the result is reused.
    private MouseHardwareReport? _cachedMouse;
    private MouseHardwareReport ConnectedMouse => _cachedMouse ??= MouseHardwareAndWandEngine.DetectConnectedMouse();

    private bool HasNebiusKey => !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());

    /// <summary>
    /// The roster shown while Hivemind is not reachable. Replies to it are Nemotron role-play and say so
    /// (<see cref="ArkStationClient.AskOfflineSyndicateAsync"/>); once signed in, <see cref="LoadLiveRosterAsync"/>
    /// replaces it with Hivemind's ark_agents.
    /// </summary>
    public static readonly SyndicateAgentInfo[] OfflineRoster =
    {
        new("all",     "ALL SYNDICATE", "Co-op Group Session",          "Nexus",    PhosphorTheme.Patina),
        new("gala",    "GALA",          "Co-op Orchestrator",           "Nexus",    PhosphorTheme.Patina),
        new("wiz",     "WIZ",           "GrimX & Mouse Macro Conductor","4GE",      PhosphorTheme.Coral),
        new("smith",   "SMITH",         "Architecture & Code Authority","4GE",      PhosphorTheme.Coral),
        new("arbiter", "ARBITER",       "Eden & SolaTree Conductor",    "Eden",     PhosphorTheme.Patina),
        new("scholar", "SCHOLAR",       "Research & Codex Conductor",   "Cortex",   PhosphorTheme.Patina),
        new("muse",    "MUSE",          "Visual & Motion Conductor",    "MagiXLab", PhosphorTheme.Coral),
        new("fabrix",  "FABRIX",        "Hardware & Gear Coordinator",  "Anvl",     PhosphorTheme.Seal),
        new("herald",  "HERALD",        "Bazaar & Pack Distribution",   "Bazaar",   PhosphorTheme.Seal)
    };

    public HolodexLiteDrawerForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<Point> openWiz,
        Action<Point> openAInbox,
        Action<Point, WandCaptureTool> openSnipOrPicker,
        Action runWizOnboardingTutorial,
        Action<CursorIconStyle> onCursorStyleChanged)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;
        _openWiz = openWiz;
        _openAInbox = openAInbox;
        _openSnipOrPicker = openSnipOrPicker;
        _runWizOnboardingTutorial = runWizOnboardingTutorial;
        _onCursorStyleChanged = onCursorStyleChanged;

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
        Size = new Size(500, 760);
        Padding = WidgetChrome.FramePadding();

        bool hasNebiusKey = HasNebiusKey;
        // The connection is said once, by the telemetry line under the header. With a key linked the key row stays
        // hidden, and Replace key brings it back.

        _tokenBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = PhosphorTheme.Surface, Name = "Holodex.TokenBar" };
        _tokenField = new PhosphorField("Paste a Nebius Token Factory key. It is saved to Windows Credential Manager.", false, 40, mono: true)
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
        // One LED per fact, the AInbox footer's status style.
        _connectionTelemetry = new LedRow { Dock = DockStyle.Fill, Height = 36, Name = "Holodex.ConnectionLine" };
        ShowConnection(gatewayOnline: false, hasNebiusKey, bundle);
        _replaceKeyBtn = new PhosphorButton("Replace key") { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.ReplaceKeyButton" };
        _replaceKeyBtn.Click += (_, _) => ShowTokenBar(!_tokenBarOpen);
        connectionRow.Controls.Add(_connectionTelemetry);
        connectionRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        connectionRow.Controls.Add(_replaceKeyBtn);

        // Hivemind: whether the session reaches the real agents, and the way in (the browser sign-in).
        var hivemindRow = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = PhosphorTheme.Surface, Name = "Holodex.HivemindRow" };
        _hivemindLine = new LedRow { Dock = DockStyle.Fill, Height = 36, Name = "Holodex.HivemindLine" };
        _hivemindBtn = new PhosphorButton("Sign in", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.HivemindSignInButton" };
        _hivemindBtn.Click += async (_, _) => await SignInToHivemindAsync();
        hivemindRow.Controls.Add(_hivemindLine);
        hivemindRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        hivemindRow.Controls.Add(_hivemindBtn);
        _ark.Hivemind.StateChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(ShowHivemindState)); };
        _connected = hasNebiusKey;

        // 2. 5-Tab Bar: SESSION | AINBOX | CODEX | GRIMX | MODS
        var tabBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = PhosphorTheme.Surface, Name = "Holodex.TabBar" };
        var tabs = new (string Id, string Label)[]
        {
            ("session", "Session"),
            ("ainbox",  "AInbox"),
            ("codex",   "Codex"),
            ("grimx",   "GrimX"),
            ("mods",    "Mods")
        };
        int tabX = 0;
        int tabW = 86;
        foreach (var (id, label) in tabs)
        {
            string capturedId = id;
            var btn = new PhosphorButton(label)
            {
                Selected = id == _activeTab,
                Location = new Point(tabX, 2),
                Size = new Size(tabW, 34),
                Name = $"Holodex.Tab.{id}"
            };
            btn.Click += (_, _) => SwitchTab(capturedId);
            _tabButtons[id] = btn;
            tabBar.Controls.Add(btn);
            tabX += tabW + 6;
        }

        // 3. Content Host for the 5 tabs
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.ContentHost" };
        BuildSessionTab();
        BuildAInboxTab();
        BuildCodexTab();
        BuildGrimXTab();
        BuildModsTab();

        Controls.Add(_contentHost);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(tabBar);
        Controls.Add(_tokenGap);
        Controls.Add(_tokenBar);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(hivemindRow);
        Controls.Add(WidgetChrome.Spacer(4));
        Controls.Add(connectionRow);
        Controls.Add(WidgetChrome.Spacer(4));
        WidgetChrome.Attach(this, "ARK // HOLODEX", "Holodex", PhosphorTheme.Patina, null);
        WidgetDrag.Enable(this);

        ShowTokenBar(!hasNebiusKey);
        SwitchTab("session");
        ResumeLayout(false);
    }

    private void BuildSessionTab()
    {
        _sessionPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionPanel" };

        // Work orders: always visible, a card per order (its empty state is a card too), like the AInbox stack.
        _workOrdersList = new ThreadView("Holodex.WorkOrdersBox") { Dock = DockStyle.Top, Height = 60 };
        _workOrdersHost = WidgetChrome.Labeled("Work orders", _workOrdersList, 60);
        _workOrdersList.ContentChanged += (_, _) => SizeWorkOrders();
        var ordersGap = WidgetChrome.Spacer(8);

        // The session: a card per message, in the same card language as Wiz and the AInbox stack.
        // What the session reaches is said by an intro card each time the Hivemind state changes (ShowHivemindState).
        _sessionThread = new ThreadView("Holodex.SessionChatBox") { Dock = DockStyle.Fill };

        // Composer: the message, a bar with + work order, then who it goes to and Send.
        var composer = new ComposerSurface { Dock = DockStyle.Bottom, Name = "Holodex.SessionComposer" };
        _sessionInputBox = new PromptTextBox { Dock = DockStyle.Top, Height = 44, Prompt = "Message the Syndicate. /order queues a work order.", Name = "Holodex.SessionInput" };
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
        var addOrderBtn = new IconToolButton("Holodex.AddWorkOrderBtn", PhosphorGlyph.Plus, "Add work order") { Dock = DockStyle.Left };
        addOrderBtn.Click += (_, _) => CreateWorkOrderFromComposer();
        bar.Controls.Add(addOrderBtn);
        composer.Content.Controls.Add(bar);
        composer.Content.Controls.Add(_sessionInputBox);
        composer.Height = composer.HeightFor(_sessionInputBox.Height + bar.Height);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionFooter" };
        _rosterChip = new SuggestionChip { Dock = DockStyle.Left, Name = "Holodex.RosterSelect" };
        _rosterChip.Click += (_, _) => _rosterChip.ShowChoices(_roster.Select(ag => new ChipChoice(
            $"{ag.Name}  ·  {ag.Role}", ag.Pod, ag.Id == _selectedSyndicateTarget, () => SetSyndicateTarget(ag.Id))));
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
        _sessionPanel.Controls.Add(ordersGap);
        _sessionPanel.Controls.Add(_workOrdersHost);
        _sessionPanel.Controls.Add(composer);
        _sessionPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = PhosphorTheme.Surface });
        _sessionPanel.Controls.Add(footer);
        _sessionPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = PhosphorTheme.Surface });
        _sessionPanel.Controls.Add(_activeRosterLabel);

        SetSyndicateTarget("all");
        RefreshWorkOrdersText();
        ShowHivemindState();
        _contentHost.Controls.Add(_sessionPanel);
    }

    private void SetSyndicateTarget(string id)
    {
        var agent = _roster.FirstOrDefault(a => a.Id == id) ?? _roster[0];
        _selectedSyndicateTarget = agent.Id;
        _rosterChip.Label = agent.Name;
        _rosterChip.Detail = agent.Id == "all" ? "" : agent.Pod;
        _rosterChip.Accent = agent.Accent;
        _rosterChip.Confident = true;
        _rosterChip.Fit();
        string who = agent.Id == "all" ? "all" : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(agent.Name.ToLowerInvariant());
        _sessionSendBtn.Text = $"Send to {who}";
        // The chip names the agent and its pod; the line says who else that reaches or the agent's role. The offline
        // roster lists everyone; Hivemind's live roster (36 agents) would bury the session, so All says where it goes
        // and the chip's menu lists the agents.
        _activeRosterLabel.Set(agent.Id != "all"
            ? new[] { (agent.Accent, agent.Role) }
            : _roster == OfflineRoster
                ? _roster.Where(a => a.Id != "all").Select(a => (a.Accent, a.Name)).ToArray()
                : new[] { (PhosphorTheme.Patina, $"Sent to {MissionTarget(agent)}, who orchestrates the group. The chip lists every agent.") });
    }

    private void ShowTokenBar(bool show)
    {
        _tokenBarOpen = show;
        _tokenBar.Visible = _tokenGap.Visible = show;
        // Without a working connection the key row is the way in, so it has no Replace toggle.
        _replaceKeyBtn.Visible = _connected;
        _replaceKeyBtn.Text = show ? "Cancel" : "Replace key";
        if (show) _tokenField.Box.Focus();
    }

    private static Color PodAccent(string token) => token switch
    {
        "coral" => PhosphorTheme.Coral,
        "seal" => PhosphorTheme.Seal,
        _ => PhosphorTheme.Patina
    };

    /// <summary>A list of cards that stays at its top, for the tabs that show records rather than a conversation.</summary>
    private static ThreadView CardList(string name) => new(name) { Dock = DockStyle.Fill, FollowNewest = false };

    private void BuildAInboxTab()
    {
        _ainboxPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.AInboxPanel" };

        var openFullBtn = new PhosphorButton("Open AInbox (Alt+I)")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.OpenAInboxButton"
        };
        openFullBtn.Click += (_, _) => _openAInbox(Location);

        // A card per pod inbox in its zone accent: what the pod handles, then its channel and conductor as LEDs.
        var podBox = CardList("Holodex.PodInboxList");
        podBox.AddSection("Pod inboxes // Handoff Scroll routing");
        foreach (var p in ArkStationClient.PodInboxes)
        {
            Color accent = PodAccent(p.AccentToken);
            var card = podBox.Add(ThreadTone.Action, $"{p.Label} // {p.Zone}", p.Description, leds: new[]
            {
                (accent, p.InboxChannel),
                (PhosphorTheme.Patina, $"Conductor {p.OrchestratorLabel}")
            });
            card.EdgeOverride = accent;
        }

        _ainboxPanel.Controls.Add(podBox);
        _ainboxPanel.Controls.Add(WidgetChrome.Spacer(10));
        _ainboxPanel.Controls.Add(openFullBtn);
        _contentHost.Controls.Add(_ainboxPanel);
    }

    private void BuildCodexTab()
    {
        _codexPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.CodexPanel" };

        var btnRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = PhosphorTheme.Surface };
        var lassoBtn = new PhosphorButton("Lasso (Alt+S)")
        {
            Dock = DockStyle.Left,
            Width = 216,
            Name = "Holodex.LassoButton"
        };
        lassoBtn.Click += (_, _) => _openSnipOrPicker(Location, WandCaptureTool.FreeformLasso);

        var elemBtn = new PhosphorButton("Point at UI (Alt+E)")
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
        _codexPanel.Controls.Add(WidgetChrome.Spacer(10));
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

        var autoMacroBtn = new PhosphorButton("Auto-bind mouse")
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
                    "Mouse Macros Not Saved",
                    $"Could not save macro bindings: {ex.Message}",
                    "WIZ // GRIMX MACROS",
                    4200,
                    ToastTone.Warning);
                return;
            }
            RefreshGrimXMacroReadout();
            PhosphorToast.Show(
                "Wiz Mouse Macros Bound",
                $"Configured Mouse4, Mouse5 & Right-Hold Gesture Flicks for {hw.Brand} {hw.Model}.",
                "WIZ // GRIMX MACROS",
                3400,
                ToastTone.Success);
        };
        wizBtnRow.Controls.Add(autoMacroBtn);
        wizBtnRow.Controls.Add(openWizBtn);

        _macroList = CardList("Holodex.GrimXList");
        RefreshGrimXMacroReadout();

        _grimxPanel.Controls.Add(_macroList);
        _grimxPanel.Controls.Add(WidgetChrome.Spacer(10));
        _grimxPanel.Controls.Add(wizBtnRow);
        _contentHost.Controls.Add(_grimxPanel);
    }

    private void BuildModsTab()
    {
        _modsPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.ModsPanel" };

        var hw = ConnectedMouse;
        // The mouse (brand, model, VID/PID), then the pointer: speed, DPI, the slider's range and VSync.
        // One LED per fact; the slider's eyebrow below says its 100 to 4000 DPI range.
        _mouseHwLabel = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "Holodex.MouseLine" };
        _mouseHwLabel.Set((PhosphorTheme.Patina, $"Mouse // {hw.Brand} {hw.Model}"), (PhosphorTheme.Moss, $"VID/PID {hw.VidPid}"));
        _dpiValueLabel = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "Holodex.PointerLine" };
        void PointerLine(int dpi, int speed) => _dpiValueLabel.Set(
            (PhosphorTheme.Patina, $"Pointer speed {speed}/20"),
            (PhosphorTheme.Coral, $"{dpi} DPI"),
            (PhosphorTheme.Moss, "VSync morph"));
        PointerLine(MouseHardwareAndWandEngine.GetCurrentDpi(), hw.WindowsPointerSpeed);

        // Live DPI Slider (100 DPI to 4000 DPI)
        var dpiSlider = new PhosphorDpiSlider
        {
            Dock = DockStyle.Top,
            Height = 46,
            Name = "Holodex.DpiSlider"
        };
        MouseHardwareAndWandEngine.DpiChanged += (dpi, speed) =>
        {
            if (IsDisposed) return;
            void UpdateText() => PointerLine(dpi, speed);
            if (InvokeRequired) BeginInvoke(new Action(UpdateText));
            else UpdateText();
        };
        var dpiLabeled = WidgetChrome.Labeled("Pointer speed // 100 to 4000 DPI", dpiSlider, 46);

        // On-Brand Cursor Icon Style Picker
        var styleRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = PhosphorTheme.Surface };
        var styles = new (string Label, CursorIconStyle Style, int Width)[]
        {
            ("Aperture",   CursorIconStyle.ArkAperture, 108),
            ("Relic Wand", CursorIconStyle.RelicWand, 110),
            ("Arrow",      CursorIconStyle.PhosphorArrow, 96),
            ("Reticle",    CursorIconStyle.ViewfinderReticle, 114)
        };
        int sx = 0;
        foreach (var (lbl, st, w) in styles)
        {
            var captured = st;
            var btn = new PhosphorButton(lbl)
            {
                Location = new Point(sx, 2),
                Size = new Size(w, 36),
                Tag = st,
                Selected = MouseHardwareAndWandEngine.ActiveCursorStyle == st
            };
            btn.Click += (_, _) =>
            {
                MouseHardwareAndWandEngine.ActiveCursorStyle = captured;
                _onCursorStyleChanged(captured);
                foreach (var c in styleRow.Controls.OfType<PhosphorButton>())
                {
                    if (c.Tag is CursorIconStyle cs)
                    {
                        c.Selected = cs == captured;
                        c.Invalidate();
                    }
                }
            };
            styleRow.Controls.Add(btn);
            sx += w + 6;
        }
        var styleLabeled = WidgetChrome.Labeled("Cursor // VSync vector morph", styleRow, 40);

        // Wiz Automated Onboarding & Animated Tooltip Tutorial button
        var onboardBtn = new PhosphorButton("Replay the tour")
        {
            Dock = DockStyle.Top,
            Height = 42,
            Name = "Holodex.RunOnboardingBtn"
        };
        onboardBtn.Click += (_, _) => _runWizOnboardingTutorial();

        // What is equipped: the spellbook's gesture casts, then the mods, a card each.
        var modsInfoBox = CardList("Holodex.ModsInfoBox");
        modsInfoBox.AddSection("Equipped // Spellbook");
        foreach (var (spell, gesture, opens) in new[]
        {
            ("Time Splitter", "Hold Ctrl or right-click, then shake left and right.", "Wiz Inspect"),
            ("Copycat", "Hold Ctrl or right-click, then shake up and down.", "Free-form lasso"),
            ("Mutation", "Hold Ctrl or right-click, then draw a circle.", "GrimX Spell cast"),
            ("Synthesize", "Hold Ctrl or right-click, then sweep right.", "Holodex session")
        })
            modsInfoBox.Add(ThreadTone.Action, $"Spell // {spell}", gesture, leds: new[] { (PhosphorTheme.Patina, $"Opens {opens}") });
        modsInfoBox.AddSection("Equipped // Mods");
        foreach (var (mod, what, leds) in new (string, string, (Color, string)[])[]
        {
            ("Ark Aperture morph engine", "Solid patina I-beam, coral hover and the Wiz globe.", new[] { (PhosphorTheme.Moss, "240 Hz") }),
            ("Mouse hardware intelligence", "Reads the brand and VID/PID over USB/HID and drives the DPI slider.", new[] { (PhosphorTheme.Moss, "100 to 4000 DPI") }),
            ("KeyMaster local vault sync", "Keeps the model key in Windows Credential Manager, synced with ArkHive.", new[] { (PhosphorTheme.Patina, "wincred:// to ArkHive") }),
            ("Hivemind session bridge", "Messages and work orders become real Hivemind missions over MCP. Signed out or offline, replies are Nemotron role-play and are labelled so.", new[] { (PhosphorTheme.Patina, "hivemind.arkomnitek.com/mcp"), (PhosphorTheme.Ghost, "Offline: Nemotron 3 Nano") })
        })
            modsInfoBox.Add(ThreadTone.Step, $"Mod // {mod}", what, leds: leds);

        _modsPanel.Controls.Add(modsInfoBox);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(10));
        _modsPanel.Controls.Add(onboardBtn);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(10));
        _modsPanel.Controls.Add(styleLabeled);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(10));
        _modsPanel.Controls.Add(dpiLabeled);
        _modsPanel.Controls.Add(WidgetChrome.Spacer(6));
        _modsPanel.Controls.Add(_dpiValueLabel);
        _modsPanel.Controls.Add(_mouseHwLabel);

        _contentHost.Controls.Add(_modsPanel);
    }

    public void SwitchTab(string tabId)
    {
        // Unknown tab names (for example holodex:tab:bogus) keep the current tab.
        tabId = (tabId ?? string.Empty).Trim().ToLowerInvariant();
        if (!_tabButtons.ContainsKey(tabId)) return;
        _activeTab = tabId;
        foreach (var (id, btn) in _tabButtons)
        {
            btn.Selected = id == tabId;
            btn.Invalidate();
        }
        _sessionPanel.Visible = tabId == "session";
        _ainboxPanel.Visible = tabId == "ainbox";
        _codexPanel.Visible = tabId == "codex";
        _grimxPanel.Visible = tabId == "grimx";
        _modsPanel.Visible = tabId == "mods";

        if (tabId == "codex") RefreshCodexEntries();
        if (tabId == "grimx") RefreshGrimXMacroReadout();
    }

    public void RefreshCodexEntries()
    {
        if (_codexList == null) return;
        ShowCodexEntries(ScreenSnipAndElementPickerForm.LoadCodexEntries());
    }

    private void ShowCodexEntries(IReadOnlyList<CodexCaptureEntry> entries)
    {
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

    public void RefreshGrimXMacroReadout()
    {
        if (_macroList == null) return;
        var hw = ConnectedMouse;
        var macros = MouseHardwareAndWandEngine.LoadOrCreateMacroBindings(hw);
        _macroList.Clear();
        // The full model is on the Mods tab; each binding names the device it reads.
        _macroList.AddSection($"Wiz mouse bindings // {hw.Brand}");
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
        if (state != "codex")
        {
            RefreshCodexEntries();
            return;
        }
        ShowCodexEntries(new[]
        {
            new CodexCaptureEntry("codex:preview", "wand.element", "ArkStation: ArkStation // Holodex Drawer",
                "A docked drawer with five tabs. The active tab uses the coral aura; the Session composer sits on the Phosphor surface.",
                "Chrome_WidgetWin_1", "270x184 @ (450,92)", "#79C4B7", "nvidia/Nemotron-3.5-Super-VL", null, DateTime.UtcNow.ToString("O"))
        });
    }

    private void RefreshWorkOrdersText()
    {
        // The panel stays: one empty-state card, or a card per order, two tall before it scrolls.
        _workOrdersList.Clear();
        if (_workOrders.Count == 0)
            _workOrdersList.Add(ThreadTone.Intro, "NONE YET", _ark.Hivemind.State == HivemindState.Connected
                ? "Add one with + or /order. Each one becomes a Hivemind mission."
                : "Add one with + or /order. Signed out, orders stay here and are not sent.");
        foreach (var wo in _workOrders)
            _workOrdersList.Add(ThreadTone.Action, $"{wo.OrderId} // {wo.Status} // {wo.OwnerAgent} // {wo.CreatedAt}", wo.Title, PhosphorGlyph.Holodex,
                wo.ExecutionId.Length > 0 ? new[] { (PhosphorTheme.Patina, $"Mission {ShortId(wo.ExecutionId)}") } : null);
        SizeWorkOrders();
    }

    /// <summary>Fits the list to two cards, then it scrolls; reruns when its width or content changes.</summary>
    private void SizeWorkOrders()
    {
        int width = _workOrdersList.CardWidth;
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
        else if (!gatewayOnline) leds.Add((PhosphorTheme.Ember, "NO MODEL KEY // PASTE ONE BELOW"));
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
        AddWorkOrder(_selectedSyndicateTarget, text);
        _sessionInputBox.Clear();
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
        error = string.Empty;
        return AddWorkOrder(match.Id, text).OrderId;
    }

    /// <summary>
    /// Adds a work order. Signed in to Hivemind it is dispatched as a real mission and its status follows the
    /// mission's; signed out it stays in this list, marked as not sent.
    /// </summary>
    private SyndicateWorkOrder AddWorkOrder(string targetId, string text)
    {
        bool live = _ark.Hivemind.State == HivemindState.Connected;
        var agent = _roster.FirstOrDefault(a => a.Id == targetId) ?? _roster[0];
        string missionTarget = MissionTarget(agent);
        string owner = missionTarget.ToUpperInvariant();
        string orderId = $"WO-{100 + _workOrders.Count + 1}";
        var order = new SyndicateWorkOrder(orderId, text, owner, live ? "DISPATCHING" : "NOT SENT // SIGNED OUT", "Just now");
        _workOrders.Insert(0, order);
        RefreshWorkOrdersText();
        if (live)
        {
            _ = RunMissionAsync(missionTarget, text, $"WORK ORDER {orderId}", mission => UpdateWorkOrder(orderId, mission));
        }
        else
        {
            _sessionThread.Add(ThreadTone.Action, $"WORK ORDER // {orderId} // TO {owner} // NOT SENT", text, PhosphorGlyph.Holodex)
                .SetResult("Signed out of Hivemind, so this order was not sent. Sign in to Hivemind to dispatch work orders.", false);
            PhosphorToast.Show($"Work order {orderId} kept here", "Not sent: Omniwand is not signed in to Hivemind.", "HOLODEX // WORK ORDERS", 3200, ToastTone.Warning);
        }
        return order;
    }

    private void UpdateWorkOrder(string orderId, HivemindMission mission)
    {
        int i = _workOrders.FindIndex(w => w.OrderId == orderId);
        if (i < 0) return;
        _workOrders[i] = _workOrders[i] with { Status = mission.Status.ToUpperInvariant(), ExecutionId = mission.ExecutionId };
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
                _sessionThread.Add(ThreadTone.Agent, $"{mission.AgentId.ToUpperInvariant()} // HIVEMIND",
                    (mission.Result.Length > 0 ? mission.Result : "Completed with no text result.") + model);
            else if (mission.Finished)
                _sessionThread.Add(ThreadTone.Fail, $"{mission.AgentId.ToUpperInvariant()} // {mission.Status.ToUpperInvariant()} // HIVEMIND",
                    (mission.Error.Length > 0 ? mission.Error : $"The mission ended {mission.Status}.") + model);
            else
                card.SetResult($"Mission {ShortId(mission.ExecutionId)} // still {mission.Status}; stopped following it ({(misses >= 5 ? _ark.Hivemind.LastError : "after 31 minutes")})", false);
            if (!Visible || !_sessionPanel.Visible)
                PhosphorToast.Show($"{mission.AgentId} // {mission.Status}", Clip(mission.Status == "completed" ? mission.Result : mission.Error),
                    "HOLODEX // HIVEMIND // ALT+H", 4200, mission.Status == "completed" ? ToastTone.Success : ToastTone.Warning);
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    /// <summary>The Hivemind row's LED: whether the session reaches the real agents (the intro card says the rest).</summary>
    private (Color, string) HivemindLed() => _ark.Hivemind.State switch
    {
        HivemindState.Connected => (PhosphorTheme.Moss, _roster == OfflineRoster ? "HIVEMIND // LIVE" : $"HIVEMIND // LIVE // {_roster.Count - 1} AGENTS"),
        HivemindState.SigningIn => (PhosphorTheme.Seal, "HIVEMIND // FINISH IN THE BROWSER"),
        HivemindState.Connecting => (PhosphorTheme.Ghost, "HIVEMIND // CONNECTING"),
        HivemindState.Unreachable => (PhosphorTheme.Rust, "HIVEMIND // UNREACHABLE"),
        _ => (PhosphorTheme.Ember, "HIVEMIND // SIGNED OUT")
    };

    /// <summary>
    /// Reflects the Hivemind state: the sign-in button, the connection LED, the roster, and one intro card in the
    /// session saying what a message reaches now.
    /// </summary>
    private void ShowHivemindState()
    {
        var state = _ark.Hivemind.State;
        _hivemindBtn.Visible = state is HivemindState.NotSignedIn or HivemindState.Unreachable;
        _hivemindBtn.Text = state == HivemindState.Unreachable ? "Reconnect" : "Sign in";
        _hivemindBtn.Enabled = state != HivemindState.SigningIn;
        if (state != HivemindState.Connected && _roster != OfflineRoster) SetRoster(OfflineRoster);
        _hivemindLine.Set(HivemindLed());
        // Connecting is the moment before the saved sign-in is tried: no card until it succeeds or fails.
        if (state == _shownHivemindState || state == HivemindState.Connecting) return;
        _shownHivemindState = state;
        string error = _ark.Hivemind.LastError;
        _sessionThread.Add(ThreadTone.Intro, state switch
        {
            HivemindState.Connected => "HIVEMIND // LIVE",
            HivemindState.SigningIn => "HIVEMIND // SIGNING IN",
            HivemindState.Unreachable => "HIVEMIND // UNREACHABLE",
            _ => "HIVEMIND // SIGNED OUT"
        }, state switch
        {
            HivemindState.Connected => "Messages and work orders go to the agents as real Hivemind missions. Each reply names its agent and model.",
            HivemindState.SigningIn => "Finish in the browser: sign in with the Hivemind owner key, then Authorize. Omniwand picks it up on its own.",
            HivemindState.Unreachable => $"Hivemind did not answer ({(error.Length > 0 ? error : "no reason given")}). Until it does, replies are Nemotron role-play and work orders are not sent.",
            _ => "Sign in (above) to reach the real Hivemind agents. Until then replies are Nemotron role-play, labelled as such, and work orders are not sent."
        });
        RefreshWorkOrdersText();
    }

    private void SetRoster(IReadOnlyList<SyndicateAgentInfo> roster)
    {
        _roster = roster;
        SetSyndicateTarget(_roster.Any(a => a.Id == _selectedSyndicateTarget) ? _selectedSyndicateTarget : _roster[0].Id);
    }

    /// <summary>Replaces the roster with Hivemind's live ark_agents (dispatch-ready agents first).</summary>
    private async Task LoadLiveRosterAsync()
    {
        var agents = await _ark.Hivemind.ListAgentsAsync();
        if (agents == null || agents.Count == 0) return;
        var live = new List<SyndicateAgentInfo> { new("all", "ALL SYNDICATE", "Gala orchestrates the group", "Hivemind", PhosphorTheme.Patina) };
        live.AddRange(agents.OrderByDescending(a => a.DispatchReady).ThenBy(a => a.Id).Select(a => new SyndicateAgentInfo(
            a.Id,
            a.Name.ToUpperInvariant(),
            a.Role.Length > 0 ? a.Role : (a.DispatchReady ? "Ready for missions" : "Not ready for missions"),
            a.Provider.Length > 0 ? a.Provider : "Hivemind",
            a.DispatchReady ? PhosphorTheme.Patina : PhosphorTheme.Ghost)));
        SetRoster(live);
        _hivemindLine.Set(HivemindLed());
    }

    private async Task SignInToHivemindAsync()
    {
        bool ok = await _ark.Hivemind.SignInAsync();
        if (!ok)
            PhosphorToast.Show("Hivemind sign-in did not finish", _ark.Hivemind.LastError, "HOLODEX // HIVEMIND", 5200, ToastTone.Warning);
        await RefreshConnectionHeaderAsync();
    }

    /// <summary>The work orders with each one's full Hivemind execution id, for pipe holodex:orders.</summary>
    public string DescribeWorkOrders() => string.Join(" || ", _workOrders.Select(w => $"{w.OrderId}|{w.Status}|{w.OwnerAgent}|{w.ExecutionId}"));

    /// <summary>The session's newest cards as they read (kicker, body, result), newest last, for pipe holodex:thread.</summary>
    public string DescribeSessionThread(int count) => string.Join(" || ", _sessionThread.Cards.TakeLast(Math.Max(1, count))
        .Select(c => $"[{c.Kicker}] {c.Body}{(c.Result != null ? $" => {c.Result}" : "")}".Replace("\r", " ").Replace("\n", " ")));

    /// <summary>Sends <paramref name="message"/> to <paramref name="agentId"/> exactly as the session's Send button does.</summary>
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

        var agent = _roster.FirstOrDefault(a => a.Id == _selectedSyndicateTarget) ?? _roster[0];
        _sessionThread.Add(ThreadTone.You, $"YOU // TO {agent.Name}", msg);

        if (_ark.Hivemind.State == HivemindState.Connected)
        {
            await RunMissionAsync(MissionTarget(agent), msg, "MESSAGE", null);
            return;
        }

        // Offline: Nemotron answers in the Syndicate's voice, and every card says it is role-play and which model.
        _setThinkingState(true);
        try
        {
            var (ok, reply, model) = await _ark.AskOfflineSyndicateAsync(msg, agent.Id, agent.Name, agent.Role);
            string label = $"OFFLINE ROLE-PLAY // {ModelChip.ShortName(model).ToUpperInvariant()}";
            if (!ok) _sessionThread.Add(ThreadTone.Fail, $"{agent.Name.ToUpperInvariant()} // NOT ANSWERED // {label}", reply);
            else foreach (var (who, text) in SplitTurns(reply, agent.Name)) _sessionThread.Add(ThreadTone.Agent, $"{who} // {label}", text);
            if (!Visible || !_sessionPanel.Visible)
                PhosphorToast.Show(ok ? $"{agent.Name} replied (offline role-play)" : $"{agent.Name} did not answer", Clip(reply),
                    "HOLODEX // SESSION // ALT+H", 4200, ok ? ToastTone.Info : ToastTone.Warning);
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    /// <summary>
    /// The session asks agents to answer as "NAME // ROLE:" lines; each turn becomes its own card. A reply with no
    /// such line is one turn from the agent it was sent to.
    /// </summary>
    internal static List<(string Who, string Text)> SplitTurns(string reply, string fallbackName)
    {
        var turns = new List<(string Who, string Text)>();
        string? who = null;
        var body = new System.Text.StringBuilder();
        void Flush()
        {
            if (who == null && body.Length == 0) return;
            turns.Add((who ?? fallbackName.ToUpperInvariant(), body.ToString().Trim()));
            body.Clear();
        }
        foreach (var raw in reply.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim().Trim('*').Trim();
            var head = System.Text.RegularExpressions.Regex.Match(line, @"^([A-Za-z][A-Za-z0-9 .]{0,40}) // ([^:]{1,60}):\s*(.*)$");
            if (head.Success)
            {
                Flush();
                who = $"{head.Groups[1].Value.Trim().ToUpperInvariant()} // {head.Groups[2].Value.Trim().ToUpperInvariant()}";
                if (head.Groups[3].Value.Length > 0) body.AppendLine(head.Groups[3].Value);
                continue;
            }
            body.AppendLine(raw);
        }
        Flush();
        return turns.Where(t => t.Text.Length > 0 || turns.Count == 1).ToList();
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
                "Could not write to Windows Credential Manager. The token works until Omniwand closes.",
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
        _connected = st.GatewayOnline || st.NebiusKeyLoaded;
        if (st.GatewayOnline)
        {
            ShowConnection(gatewayOnline: true, st.NebiusKeyLoaded, bundle);
            ShowTokenBar(_tokenBarOpen);
        }
        else if (st.NebiusKeyLoaded)
        {
            ShowConnection(gatewayOnline: false, keyed: true, bundle);
            ShowTokenBar(_tokenBarOpen);
        }
        else
        {
            ShowConnection(gatewayOnline: false, keyed: false, bundle: false);
            ShowTokenBar(true);
        }
        ShowHivemindState();
    }

    public async void SummonDrawer(Point cursorPos, string? tab = null)
    {
        if (!string.IsNullOrEmpty(tab))
        {
            SwitchTab(tab);
        }

        // The drawer's 500 x 820 is logical: at 150% it is 750 x 1230 device pixels, capped by the work area.
        var work = Screen.FromPoint(cursorPos).WorkingArea;
        float s = DeviceDpi / 96f; // the monitor's DPI even before the first Show (PhosphorPaint.Scale is 1 until then)
        int h = Math.Min((int)Math.Round(820 * s), work.Height - (int)Math.Round(24 * s));
        Size = new Size((int)Math.Round(500 * s), h);
        Location = new Point(work.Right - Width - 12, work.Top + (work.Height - h) / 2);

        Show();
        Activate();
        await RefreshConnectionHeaderAsync();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Patina);
    }
}
