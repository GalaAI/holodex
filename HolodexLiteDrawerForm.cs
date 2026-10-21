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
    private readonly DataLabel _connectionTelemetry;

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
    private PhosphorField _sessionInputField = null!;
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
        _vaultBadge = hasNebiusKey
            ? new PhosphorBadge("Nebius Key Linked", PhosphorBadgeState.Validated) { Name = "Holodex.VaultBadge" }
            : new PhosphorBadge("Token Login", PhosphorBadgeState.Unstated) { Name = "Holodex.VaultBadge" };
        _vaultBadge.FitToText();

        // 1. Token Login / ArkHive Hivemind Connection Bar
        var tokenBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = PhosphorTheme.Surface, Name = "Holodex.TokenBar" };
        _connectionTelemetry = new DataLabel(hasNebiusKey
            ? "HIVEMIND LITE // NEBIUS KEY LINKED // CHECKING GATEWAY"
            : "HIVEMIND LITE // NO NEBIUS KEY // PASTE ONE BELOW")
        {
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = PhosphorTheme.Patina
        };
        var tokenInputRow = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface };
        _tokenField = new PhosphorField("Paste Hivemind / ArkHive Token or Nebius Key to sync to Windows Vault...", false, 36, mono: true)
        {
            Dock = DockStyle.Fill,
            Name = "Holodex.TokenField"
        };
        var saveTokenBtn = new PhosphorButton("Link Token", PhosphorButtonVariant.Primary)
        {
            Dock = DockStyle.Right,
            Width = 124,
            Height = 36,
            Name = "Holodex.LinkTokenButton"
        };
        saveTokenBtn.Click += async (_, _) => await SaveTokenLoginAsync();
        tokenInputRow.Controls.Add(_tokenField);
        tokenInputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        tokenInputRow.Controls.Add(saveTokenBtn);
        tokenBar.Controls.Add(tokenInputRow);
        tokenBar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4, BackColor = PhosphorTheme.Surface });
        tokenBar.Controls.Add(_connectionTelemetry);

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
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(tokenBar);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "ARK://HOLODEX LITE", "Holodex", PhosphorTheme.Patina, _vaultBadge);
        WidgetDrag.Enable(this);

        SwitchTab("session");
    }

    private void BuildSessionTab(bool hasNebiusKey)
    {
        _sessionPanel = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Surface, Name = "Holodex.SessionPanel" };

        // Syndicate roster selector row
        var rosterRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = PhosphorTheme.Surface };
        var rosterCombo = new PhosphorComboBox { Dock = DockStyle.Left, Width = 260, Name = "Holodex.RosterSelect" };
        foreach (var ag in SyndicateRoster)
            rosterCombo.Items.Add($"{ag.Name} // {ag.Role} ({ag.Pod})");
        rosterCombo.SelectedIndex = 0;
        rosterCombo.SelectedIndexChanged += (_, _) =>
        {
            int idx = Math.Clamp(rosterCombo.SelectedIndex, 0, SyndicateRoster.Length - 1);
            _selectedSyndicateTarget = SyndicateRoster[idx].Id;
            _activeRosterLabel.Text = $"ROSTER // {SyndicateRoster[idx].Name} · {SyndicateRoster[idx].Pod.ToUpperInvariant()} POD";
        };

        var addOrderBtn = new PhosphorButton("+ Work Order")
        {
            Dock = DockStyle.Right,
            Width = 148,
            Height = 30,
            Name = "Holodex.AddWorkOrderBtn"
        };
        addOrderBtn.Click += (_, _) => CreateWorkOrderFromComposer();
        rosterRow.Controls.Add(addOrderBtn);
        rosterRow.Controls.Add(rosterCombo);

        _activeRosterLabel = new DataLabel("ROSTER // ALL SYNDICATE · GALA + WIZ + SMITH + ARBITER + SCHOLAR + MUSE")
        {
            Dock = DockStyle.Top,
            Height = 18
        };

        // Work Orders + Activity mini-board
        _workOrdersBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 104,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Patina,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Mono(10.5f),
            Name = "Holodex.WorkOrdersBox"
        };
        PhosphorPaint.UseDarkScrollbars(_workOrdersBox);
        RefreshWorkOrdersText();
        var ordersLabeled = WidgetChrome.Labeled("Syndicate Work Orders // Live Activity", _workOrdersBox, 104);

        // Group chat thread box
        _sessionChatBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(12.5f),
            Name = "Holodex.SessionChatBox",
            Text =
                "GALA // NEXUS ORCHESTRATOR\n" +
                (hasNebiusKey
                    ? "Welcome to the Hivemind Lite Session. Your Nebius key is linked and Nemotron is ready.\n\n"
                    : "Welcome to the Hivemind Lite Session. Paste a Nebius Token Factory key above to enable Nemotron.\n\n") +
                "WIZ // 4GE CONDUCTOR\n" +
                "Your mouse hardware profile, DPI controls, Free-Form Lasso (Alt+S), and Element Picker (Alt+E) are online. Message the Syndicate below or prefix with /order to dispatch a Work Order."
        };
        PhosphorPaint.UseDarkScrollbars(_sessionChatBox);

        // Bottom composer
        var composer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = PhosphorTheme.Surface };
        _sessionInputField = new PhosphorField("Message the Syndicate (@all, @wiz, @smith, @gala) or /order <task>...", false, 40)
        {
            Dock = DockStyle.Fill,
            Name = "Holodex.SessionInput"
        };
        _sessionInputField.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                await SendSyndicateSessionMessageAsync();
            }
        };
        var sendBtn = new PhosphorButton("Send", PhosphorButtonVariant.Primary)
        {
            Dock = DockStyle.Right,
            Width = 96,
            Height = 40,
            Name = "Holodex.SessionSendBtn"
        };
        sendBtn.Click += async (_, _) => await SendSyndicateSessionMessageAsync();
        composer.Controls.Add(_sessionInputField);
        composer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = PhosphorTheme.Surface });
        composer.Controls.Add(sendBtn);

        _sessionPanel.Controls.Add(_sessionChatBox);
        _sessionPanel.Controls.Add(WidgetChrome.Spacer(8));
        _sessionPanel.Controls.Add(ordersLabeled);
        _sessionPanel.Controls.Add(WidgetChrome.Spacer(6));
        _sessionPanel.Controls.Add(_activeRosterLabel);
        _sessionPanel.Controls.Add(WidgetChrome.Spacer(4));
        _sessionPanel.Controls.Add(rosterRow);
        _sessionPanel.Controls.Add(composer);

        _contentHost.Controls.Add(_sessionPanel);
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
        _mouseHwLabel = new DataLabel($"MOUSE // {hw.Brand.ToUpperInvariant()} {hw.Model.ToUpperInvariant()} ({hw.VidPid})")
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = PhosphorTheme.Patina
        };

        _dpiValueLabel = new DataLabel($"POINTER SPEED // {hw.WindowsPointerSpeed}/20 · {MouseHardwareAndWandEngine.GetCurrentDpi()} DPI (100 to 4000 SLIDER) · VSYNC")
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = PhosphorTheme.Bone
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
            void UpdateText() => _dpiValueLabel.Text = $"POINTER SPEED // {speed}/20 · {dpi} DPI (100 to 4000 SLIDER) · VSYNC";
            if (InvokeRequired) BeginInvoke(new Action(UpdateText));
            else UpdateText();
        };
        var dpiLabeled = WidgetChrome.Labeled("Live Mouse Sensitivity // DPI Slider (100 to 4000 DPI)", dpiSlider, 46);

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
        var styleLabeled = WidgetChrome.Labeled("On-Brand Cursor Icon // VSync Vector Morph", styleRow, 40);

        // Wiz Automated Onboarding & Animated Tooltip Tutorial button
        var onboardBtn = new PhosphorButton("Run Wiz Onboarding & Animated Tooltips")
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
        sb.AppendLine("GRIMX FLOW ONTOLOGY (8-SLOT ORB // MIDDLE-HOLD OR F15)");
        foreach (var s in GrimXFlowOrb.Slots)
        {
            string slotLabel = string.Equals(s.Verb, s.Title, StringComparison.OrdinalIgnoreCase) ? s.Title : $"{s.Verb} {s.Title}";
            sb.AppendLine($"  [{s.Index + 1}] {slotLabel} // {s.Subtitle}");
        }
        _macroReadoutBox.Text = sb.ToString();
    }

    private void RefreshWorkOrdersText()
    {
        if (_workOrders.Count == 0)
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
        string text = _sessionInputField.Box.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            PhosphorToast.Show("Work Order", "Type the task in the message box first.", "HOLODEX // WORK ORDERS", 2800, ToastTone.Warning);
            return;
        }
        string owner = _selectedSyndicateTarget == "all" ? "WIZ" : _selectedSyndicateTarget.ToUpperInvariant();
        string orderId = $"WO-{100 + _workOrders.Count + 1}";
        _workOrders.Insert(0, new SyndicateWorkOrder(orderId, text, owner, "QUEUED", "Just now"));
        RefreshWorkOrdersText();
        _sessionChatBox.AppendText($"\n\n[WORK ORDER CREATED // {orderId} -> {owner}]\n{text}");
        _sessionChatBox.SelectionStart = _sessionChatBox.TextLength;
        _sessionChatBox.ScrollToCaret();
        _sessionInputField.Box.Clear();
    }

    private async Task SendSyndicateSessionMessageAsync()
    {
        string msg = _sessionInputField.Box.Text.Trim();
        if (string.IsNullOrEmpty(msg)) return;
        _sessionInputField.Box.Clear();

        if (msg.StartsWith("/order ", StringComparison.OrdinalIgnoreCase))
        {
            _sessionInputField.Box.Text = msg[7..].Trim();
            CreateWorkOrderFromComposer();
            return;
        }

        var agent = SyndicateRoster.FirstOrDefault(a => a.Id == _selectedSyndicateTarget) ?? SyndicateRoster[0];
        _sessionChatBox.AppendText($"\n\nOPERATOR -> {agent.Name}\n{msg}");
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
            PhosphorToast.Show("Token Login", "Paste a Hivemind / ArkHive token or Nebius API key first.", "HOLODEX // VAULT", 2800, ToastTone.Warning);
            return;
        }

        bool saved = _ark.SaveTokenToWindowsVault(token);
        _tokenField.Box.Clear();
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
        if (st.GatewayOnline)
        {
            _vaultBadge.Text = "Online";
            _vaultBadge.State = PhosphorBadgeState.Validated;
            _connectionTelemetry.Text = $"HIVEMIND ONLINE // ARKHIVE GATEWAY :8788 // WINCRED VAULT {(bundle ? "LINKED" : "NOT LINKED")}";
        }
        else if (st.NebiusKeyLoaded)
        {
            _vaultBadge.Text = "Nebius Key Linked";
            _vaultBadge.State = PhosphorBadgeState.Validated;
            _connectionTelemetry.Text = $"NEBIUS KEY LINKED // NEMOTRON READY // WINCRED VAULT {(bundle ? "LINKED" : "NOT LINKED")}";
        }
        else
        {
            _vaultBadge.Text = "Token Login";
            _vaultBadge.State = PhosphorBadgeState.Unstated;
            _connectionTelemetry.Text = "PASTE HIVEMIND OR NEBIUS TOKEN BELOW TO LINK WINDOWS VAULT";
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
