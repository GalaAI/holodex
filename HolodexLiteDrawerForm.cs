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
    string CreatedAt);

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

    private readonly PhosphorBadge _vaultBadge;
    private readonly PhosphorField _tokenField;
    private readonly Panel _tokenBar;
    private readonly Panel _tokenGap;
    private readonly Label _connectionTelemetry;
    private readonly PhosphorButton _replaceKeyBtn;
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
    private RichTextBox _sessionChatBox = null!;
    private RichTextBox _workOrdersBox = null!;
    private Panel _workOrdersHost = null!;
    private PromptTextBox _sessionInputBox = null!;
    private SuggestionChip _rosterChip = null!;
    private PhosphorButton _sessionSendBtn = null!;
    private DataLabel _activeRosterLabel = null!;

    // Codex controls
    private RichTextBox _codexListBox = null!;

    // GrimX / Macro controls
    private RichTextBox _macroReadoutBox = null!;

    // Mods / Hardware controls
    private DataLabel _mouseHwLabel = null!;
    private DataLabel _dpiValueLabel = null!;

    private readonly List<SyndicateWorkOrder> _workOrders = new();

    // Mouse detection launches PowerShell, so it runs once and the result is reused.
    private MouseHardwareReport? _cachedMouse;
    private MouseHardwareReport ConnectedMouse => _cachedMouse ??= MouseHardwareAndWandEngine.DetectConnectedMouse();

    private bool HasNebiusKey => !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());

    public static readonly SyndicateAgentInfo[] SyndicateRoster =
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
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
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
        // The header badge sums up the connection; the telemetry line under the header says it in full. With a key
        // linked the key row stays hidden, and Replace key (or the badge) brings it back.
        _vaultBadge = new PhosphorBadge(hasNebiusKey ? "Nemotron ready" : "No key", hasNebiusKey ? PhosphorBadgeState.Validated : PhosphorBadgeState.Unstated)
        {
            Name = "Holodex.VaultBadge",
            Cursor = Cursors.Hand
        };
        _vaultBadge.FitToText();
        _vaultBadge.Click += (_, _) => ShowTokenBar(!_tokenBarOpen);

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
        // A wrapping mono label, so the full telemetry fits beside the button on two lines.
        _connectionTelemetry = new Label
        {
            Text = hasNebiusKey
                ? $"NEBIUS KEY LINKED // NEMOTRON READY{(bundle ? " // SAVED IN WINDOWS VAULT" : "")}"
                : "NO MODEL KEY // PASTE A NEBIUS KEY BELOW",
            Dock = DockStyle.Fill,
            AutoSize = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = PhosphorTheme.Surface,
            Font = PhosphorFonts.Mono(PhosphorTheme.TextData),
            Height = 36,
            ForeColor = hasNebiusKey ? PhosphorTheme.Patina : PhosphorTheme.Ember,
            Name = "Holodex.ConnectionLine"
        };
        _replaceKeyBtn = new PhosphorButton("Replace key") { Dock = DockStyle.Right, Width = 128, Height = 36, Name = "Holodex.ReplaceKeyButton" };
        _replaceKeyBtn.Click += (_, _) => ShowTokenBar(!_tokenBarOpen);
        connectionRow.Controls.Add(_connectionTelemetry);
        connectionRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        connectionRow.Controls.Add(_replaceKeyBtn);
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
        BuildSessionTab(hasNebiusKey);
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
        Controls.Add(connectionRow);
        Controls.Add(WidgetChrome.Spacer(4));
        WidgetChrome.Attach(this, "ARK // HOLODEX", "Holodex", PhosphorTheme.Patina, _vaultBadge);
        WidgetDrag.Enable(this);

        ShowTokenBar(!hasNebiusKey);
        SwitchTab("session");
    }

    private void BuildSessionTab(bool hasNebiusKey)
    {
        _sessionPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionPanel" };

        // Work orders: always visible, with its empty state.
        _workOrdersBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 96,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Patina,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(10.5f),
            Name = "Holodex.WorkOrdersBox"
        };
        PhosphorPaint.UseDarkScrollbars(_workOrdersBox);
        _workOrdersHost = WidgetChrome.Labeled("Work orders", _workOrdersBox, 96);
        var ordersGap = WidgetChrome.Spacer(8);

        _sessionChatBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(12.5f),
            Name = "Holodex.SessionChatBox",
            Text =
                "GALA // NEXUS ORCHESTRATOR\n" +
                (hasNebiusKey
                    ? "Welcome. Your Nebius key is linked and Nemotron is ready to chat.\n\n"
                    : "Welcome. Paste a Nebius Token Factory key above to turn on Nemotron.\n\n") +
                "WIZ // 4GE CONDUCTOR\n" +
                "Your mouse hardware profile, DPI controls, Free-Form Lasso (Alt+S), and Element Picker (Alt+E) are online. Message the Syndicate below or prefix with /order to dispatch a Work Order."
        };
        PhosphorPaint.UseDarkScrollbars(_sessionChatBox);

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
        _rosterChip.Click += (_, _) => _rosterChip.ShowChoices(SyndicateRoster.Select(ag => new ChipChoice(
            $"{ag.Name}  ·  {ag.Role}", ag.Pod, ag.Id == _selectedSyndicateTarget, () => SetSyndicateTarget(ag.Id))));
        _sessionSendBtn = new PhosphorButton("Send", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 150, Name = "Holodex.SessionSendBtn" };
        _sessionSendBtn.Click += async (_, _) => await SendSyndicateSessionMessageAsync();
        footer.Controls.Add(_rosterChip);
        footer.Controls.Add(_sessionSendBtn);

        // Roster: who the message reaches, under the chip.
        _activeRosterLabel = new DataLabel("") { Dock = DockStyle.Bottom, Height = 20, Name = "Holodex.RosterLine" };

        _sessionPanel.Controls.Add(_sessionChatBox);
        _sessionPanel.Controls.Add(ordersGap);
        _sessionPanel.Controls.Add(_workOrdersHost);
        _sessionPanel.Controls.Add(composer);
        _sessionPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = PhosphorTheme.Surface });
        _sessionPanel.Controls.Add(footer);
        _sessionPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = PhosphorTheme.Surface });
        _sessionPanel.Controls.Add(_activeRosterLabel);

        SetSyndicateTarget("all");
        RefreshWorkOrdersText();
        _contentHost.Controls.Add(_sessionPanel);
    }

    private void SetSyndicateTarget(string id)
    {
        var agent = SyndicateRoster.FirstOrDefault(a => a.Id == id) ?? SyndicateRoster[0];
        _selectedSyndicateTarget = agent.Id;
        _rosterChip.Label = agent.Name;
        _rosterChip.Detail = agent.Id == "all" ? "" : agent.Pod;
        _rosterChip.Accent = agent.Accent;
        _rosterChip.Confident = true;
        _rosterChip.Fit();
        string who = agent.Id == "all" ? "all" : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(agent.Name.ToLowerInvariant());
        _sessionSendBtn.Text = $"Send to {who}";
        _activeRosterLabel.Text = agent.Id == "all"
            ? "ROSTER // ALL SYNDICATE · GALA + WIZ + SMITH + ARBITER + SCHOLAR + MUSE"
            : $"ROSTER // {agent.Name} · {agent.Pod.ToUpperInvariant()} POD";
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

    private void BuildAInboxTab()
    {
        _ainboxPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.AInboxPanel" };

        var openFullBtn = new PhosphorButton("Open Full AInbox Intake + Scroll (Alt+I)")
        {
            Dock = DockStyle.Top,
            Height = 42
        };
        openFullBtn.Click += (_, _) => _openAInbox(Location);

        var podBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(11f)
        };
        PhosphorPaint.UseDarkScrollbars(podBox);

        var lines = new List<string> { "ARKSTATION POD INBOXES // SCRIBE HANDOFF SCROLL ROUTING\n" };
        foreach (var p in ArkStationClient.PodInboxes)
        {
            lines.Add($"[{p.Label.ToUpperInvariant()}]  {p.InboxChannel}  ·  Conductor: {p.OrchestratorLabel}");
            lines.Add($"  {p.Description}\n");
        }
        podBox.Text = string.Join("\n", lines);

        _ainboxPanel.Controls.Add(podBox);
        _ainboxPanel.Controls.Add(WidgetChrome.Spacer(10));
        _ainboxPanel.Controls.Add(openFullBtn);
        _contentHost.Controls.Add(_ainboxPanel);
    }

    private void BuildCodexTab()
    {
        _codexPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.CodexPanel" };

        var btnRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = PhosphorTheme.Surface };
        var lassoBtn = new PhosphorButton("Lasso")
        {
            Dock = DockStyle.Left,
            Width = 216,
            Name = "Holodex.LassoButton"
        };
        lassoBtn.Click += (_, _) => _openSnipOrPicker(Location, WandCaptureTool.FreeformLasso);

        var elemBtn = new PhosphorButton("Element Picker (Alt+E)")
        {
            Dock = DockStyle.Right,
            Width = 216
        };
        elemBtn.Click += (_, _) => _openSnipOrPicker(Location, WandCaptureTool.ElementPicker);
        btnRow.Controls.Add(elemBtn);
        btnRow.Controls.Add(lassoBtn);

        _codexListBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(11f),
            Name = "Holodex.CodexListBox"
        };
        PhosphorPaint.UseDarkScrollbars(_codexListBox);
        RefreshCodexEntries();

        _codexPanel.Controls.Add(_codexListBox);
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

        var autoMacroBtn = new PhosphorButton("Wiz Auto-Bind Mouse")
        {
            Dock = DockStyle.Right,
            Width = 210
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

        _macroReadoutBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(11f)
        };
        PhosphorPaint.UseDarkScrollbars(_macroReadoutBox);
        RefreshGrimXMacroReadout();

        _grimxPanel.Controls.Add(_macroReadoutBox);
        _grimxPanel.Controls.Add(WidgetChrome.Spacer(10));
        _grimxPanel.Controls.Add(wizBtnRow);
        _contentHost.Controls.Add(_grimxPanel);
    }

    private void BuildModsTab()
    {
        _modsPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Visible = false, Name = "Holodex.ModsPanel" };

        var hw = ConnectedMouse;
        // The mouse (brand, model, VID/PID), then the pointer: speed, DPI, the slider's range and VSync.
        _mouseHwLabel = new DataLabel($"MOUSE // {hw.Brand.ToUpperInvariant()} {hw.Model.ToUpperInvariant()} ({hw.VidPid})")
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = PhosphorTheme.Patina,
            Name = "Holodex.MouseLine"
        };
        static string PointerLine(int dpi, int speed) => $"POINTER SPEED // {speed}/20 · {dpi} DPI (100 to 4000 SLIDER) · VSYNC";
        _dpiValueLabel = new DataLabel(PointerLine(MouseHardwareAndWandEngine.GetCurrentDpi(), hw.WindowsPointerSpeed))
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = PhosphorTheme.Bone,
            Name = "Holodex.PointerLine"
        };

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
            void UpdateText() => _dpiValueLabel.Text = PointerLine(dpi, speed);
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

        var modsInfoBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(10.5f),
            Name = "Holodex.ModsInfoBox",
            Text =
                "EQUIPPED OMNIWAND AUGMENTS, SPELLBOOK & MODS\n" +
                "• [SPELL] Hold Ctrl or Right-Click + Shake L-R  ->  TIME SPLITTER (Wiz Inspect)\n" +
                "• [SPELL] Hold Ctrl or Right-Click + Shake U-D  ->  COPYCAT (Free-Form Lasso)\n" +
                "• [SPELL] Hold Ctrl or Right-Click + Circle     ->  MUTATION (GrimX Spell Cast)\n" +
                "• [SPELL] Hold Ctrl or Right-Click + Sweep R    ->  SYNTHESIZE (Holodex Session)\n" +
                "• [MOD] Ark Aperture 240Hz Morph Engine (Solid Patina IBeam, Coral Hover/Wiz Globe)\n" +
                "• [MOD] Mouse Hardware Intelligence (USB/HID VID/PID Brand & 100-4000 DPI Slider)\n" +
                "• [MOD] KeyMaster Local Vault Sync (Windows Credential Manager wincred:// <-> ArkHive)\n" +
                "• [MOD] Hivemind Lite Session Bridge (Nemotron 3 Nano/Super + Syndicate Group Chat)"
        };
        PhosphorPaint.UseDarkScrollbars(modsInfoBox);

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
        if (_codexListBox == null) return;
        var entries = ScreenSnipAndElementPickerForm.LoadCodexEntries();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("HOLODEX CODEX // CAPTURED LASSO ARTIFACTS & ELEMENT INSPECTIONS\n");
        if (entries.Count == 0) sb.AppendLine(ScreenSnipAndElementPickerForm.CodexEmptyStateText);
        foreach (var e in entries)
        {
            sb.AppendLine($"[{e.Kind.ToUpperInvariant()}] {e.Title}");
            sb.AppendLine($"  CLASS: {e.ElementClass}  ·  GEOMETRY: {e.Geometry}  ·  PALETTE: {e.HexColor}");
            sb.AppendLine($"  MODEL: {e.ModelUsed}");
            sb.AppendLine($"  {e.Summary}\n");
        }
        _codexListBox.Text = sb.ToString();
    }

    public void RefreshGrimXMacroReadout()
    {
        if (_macroReadoutBox == null) return;
        var hw = ConnectedMouse;
        var macros = MouseHardwareAndWandEngine.LoadOrCreateMacroBindings(hw);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"WIZ MOUSE MACRO & GESTURE BINDER // {hw.Brand.ToUpperInvariant()} {hw.Model.ToUpperInvariant()}\n");
        foreach (var m in macros)
        {
            sb.AppendLine($"• {m.TriggerLabel}");
            sb.AppendLine($"  -> {m.ActionLabel} ({m.Parameter})\n");
        }
        sb.AppendLine("SPELL ORB (8 SLOTS // MIDDLE-HOLD OR F15)");
        foreach (var s in GrimXFlowOrb.Slots)
        {
            string slotLabel = string.Equals(s.Verb, s.Title, StringComparison.OrdinalIgnoreCase) ? s.Title : $"{s.Verb} {s.Title}";
            sb.AppendLine($"  [{s.Index + 1}] {slotLabel} // {s.Subtitle}");
        }
        _macroReadoutBox.Text = sb.ToString();
    }

    private void RefreshWorkOrdersText()
    {
        // The panel stays; it is short while empty and opens to its full height with orders in it.
        bool empty = _workOrders.Count == 0;
        _workOrdersBox.Height = empty ? 28 : 96;
        _workOrdersHost.Height = 18 + 4 + _workOrdersBox.Height;
        _workOrdersBox.ForeColor = empty ? PhosphorTheme.Ghost : PhosphorTheme.Patina;
        if (empty)
        {
            _workOrdersBox.Text = "No work orders yet. Add one or ask in the session.";
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var wo in _workOrders)
        {
            sb.AppendLine($"[{wo.Status}] {wo.OrderId} · {wo.OwnerAgent}: {wo.Title} ({wo.CreatedAt})");
        }
        _workOrdersBox.Text = sb.ToString().TrimEnd();
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
        var match = SyndicateRoster.FirstOrDefault(a =>
            string.Equals(a.Id, who, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, who, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            error = $"Unknown agent '{agent}'. Use one of: {string.Join(", ", SyndicateRoster.Select(a => a.Id))}.";
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

    private SyndicateWorkOrder AddWorkOrder(string targetId, string text)
    {
        string owner = targetId == "all" ? "WIZ" : targetId.ToUpperInvariant();
        string orderId = $"WO-{100 + _workOrders.Count + 1}";
        var order = new SyndicateWorkOrder(orderId, text, owner, "QUEUED", "Just now");
        _workOrders.Insert(0, order);
        RefreshWorkOrdersText();
        _sessionChatBox.AppendText($"\n\n[WORK ORDER CREATED // {orderId} -> {owner}]\n{text}");
        _sessionChatBox.SelectionStart = _sessionChatBox.TextLength;
        _sessionChatBox.ScrollToCaret();
        return order;
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

        var agent = SyndicateRoster.FirstOrDefault(a => a.Id == _selectedSyndicateTarget) ?? SyndicateRoster[0];
        _sessionChatBox.AppendText($"\n\nYOU -> {agent.Name}\n{msg}");
        _sessionChatBox.SelectionStart = _sessionChatBox.TextLength;
        _sessionChatBox.ScrollToCaret();

        _setThinkingState(true);
        try
        {
            var (ok, reply) = await _ark.AskSyndicateSessionAsync(msg, agent.Id, agent.Name, agent.Role);
            _sessionChatBox.AppendText($"\n\n{reply}");
            _sessionChatBox.SelectionStart = _sessionChatBox.TextLength;
            _sessionChatBox.ScrollToCaret();
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
                "Could not write to Windows Credential Manager. The token works until Omniwand closes.",
                "KEYMASTER // WINCRED VAULT",
                4200,
                ToastTone.Warning);
        }
    }

    public async Task RefreshConnectionHeaderAsync()
    {
        var st = await _ark.CheckStatusAsync();
        bool bundle = ArkStationClient.HasWinCredVaultBundle();
        _connected = st.GatewayOnline || st.NebiusKeyLoaded;
        if (st.GatewayOnline)
        {
            _vaultBadge.Text = "Gateway online";
            _vaultBadge.State = PhosphorBadgeState.Validated;
            _connectionTelemetry.Text = "ARKSTATION GATEWAY ONLINE";
            _connectionTelemetry.ForeColor = PhosphorTheme.Patina;
            ShowTokenBar(_tokenBarOpen);
        }
        else if (st.NebiusKeyLoaded)
        {
            _vaultBadge.Text = "Nemotron ready";
            _vaultBadge.State = PhosphorBadgeState.Validated;
            _connectionTelemetry.Text = $"NEBIUS KEY LINKED // NEMOTRON READY{(bundle ? " // SAVED IN WINDOWS VAULT" : "")}";
            _connectionTelemetry.ForeColor = PhosphorTheme.Patina;
            ShowTokenBar(_tokenBarOpen);
        }
        else
        {
            _vaultBadge.Text = "No key";
            _vaultBadge.State = PhosphorBadgeState.Unstated;
            _connectionTelemetry.Text = "NO MODEL KEY // PASTE A NEBIUS KEY BELOW";
            _connectionTelemetry.ForeColor = PhosphorTheme.Ember;
            ShowTokenBar(true);
        }
        _vaultBadge.FitToText();
    }

    public async void SummonDrawer(Point cursorPos, string? tab = null)
    {
        if (!string.IsNullOrEmpty(tab))
        {
            SwitchTab(tab);
        }

        var work = Screen.FromPoint(cursorPos).WorkingArea;
        int h = Math.Min(820, work.Height - 24);
        Size = new Size(500, h);
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
