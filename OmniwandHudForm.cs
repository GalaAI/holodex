using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Summonable Phosphor Shell HUD (Alt+Space, Actions Ring "Summon HUD", pipe hud:summon). One composer: ask about
/// what the wand points at, with the screen around the tip attached by a toggle. The question goes to Gala in the
/// operator's Hivemind when Gala can take the turn (a Hivemind Session, opened once and continued, with Open in
/// Hivemind beside it); otherwise Nemotron answers directly and the answer card says why Gala did not. The header badge is the wand
/// status (Online, Ready, No key) beside the icons that open Holodex, Lasso, Wiz and AInbox; the model chip says
/// which model answers. Under the answer: one telemetry line (gateway, Input Spine 8842, the connected mouse) and
/// the wand shortcuts behind a collapsed SHORTCUTS row (the answer area is not a help panel, operator 2026-10-18).
/// </summary>
public sealed class OmniwandHudForm : Form
{
    /// <summary>The answer box grows with the answer up to this (logical px, scaled by DPI) or half the work area.</summary>
    private const int AnswerMaxHeight = 520;

    private const string ShortcutsText =
        "Actions Ring casts the eight GrimX Spell slots.\n" +
        "F15 or Ctrl+Shift+Space opens the fallback orb.\n" +
        "F16 streams the GhostTrailer ribbon (Alt+V for it is planned, not bound yet).\n" +
        "Alt+W opens Wiz. Alt+I opens AInbox.";

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly ComposerSurface _composer;
    private readonly PromptTextBox _promptBox;
    private readonly IconToolButton _visionToggle;
    private readonly DataLabel _visionHint;
    private readonly ThreadView _answers;
    private readonly Panel _responseGap;
    private readonly LedRow _telemetryLine;
    private static bool _gatewayNoticeShown;
    private readonly DisclosureRow _shortcutsRow;
    private readonly Label _shortcutsBody;
    private readonly ModelChip _modelChip;
    private readonly DataLabel _statusLine;
    private readonly PhosphorButton _askBtn;
    private readonly IconToolButton _openInHivemindBtn;
    /// <summary>The Hivemind Session Gala answers in: opened by the first ask, continued by the rest.</summary>
    private string? _galaSessionId;
    /// <summary>Whether Gala can take a turn, as the telemetry line's first LED says it.</summary>
    private (Color Led, string Text) _galaLed = (PhosphorTheme.Ghost, "GALA");
    private static readonly TimeSpan GalaReplyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GalaPollInterval = TimeSpan.FromSeconds(2);
    /// <summary>The ask under way, cancelled by a pending card's Stop.</summary>
    private System.Threading.CancellationTokenSource? _askCancel;
    private Point _summonCursorPos;
    private bool _working;
    private bool _gatewayOnline;
    private bool? _spineOnline;
    private string? _mouseModel;
    private Task? _mouseScan;

    public OmniwandHudForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<Point> openWizCrafting,
        Action<Point> openAInboxIntake,
        Action<Point>? openHolodex = null,
        Action<Point>? openSnipLasso = null)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;

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
        Name = "Hud";
        Size = new Size(560, 300);
        Padding = WidgetChrome.FramePadding();

        // Header: the launchers as icons. Wand status is the gateway LED in the telemetry line, said once.
        var headerTools = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Name = "Hud.HeaderTools"
        };
        void AddLauncher(string name, PhosphorGlyph glyph, string label, Action<Point>? open)
        {
            if (open == null) return;
            var b = new IconToolButton(name, glyph, label) { Margin = Padding.Empty };
            b.Click += (_, _) => { SurfaceMotion.Dismiss(this); open(_summonCursorPos); };
            headerTools.Controls.Add(b);
        }
        AddLauncher("Hud.HolodexButton", PhosphorGlyph.Holodex, "Holodex", openHolodex);
        AddLauncher("Hud.SnipButton", PhosphorGlyph.Lasso, "Lasso", openSnipLasso);
        AddLauncher("Hud.WizButton", PhosphorGlyph.Wiz, "Wiz", openWizCrafting);
        AddLauncher("Hud.AInboxButton", PhosphorGlyph.AInbox, "AInbox", openAInboxIntake);
        // Shown once Gala has a Session to open.
        _openInHivemindBtn = new IconToolButton("Hud.OpenInHivemind", PhosphorGlyph.ExternalLink, "Open in Hivemind") { Margin = Padding.Empty, Visible = false };
        _openInHivemindBtn.Click += (_, _) => HivemindClient.OpenWebSessions();
        headerTools.Controls.Add(_openInHivemindBtn);

        // Composer: the question, and a bar with the screen toggle.
        _composer = new ComposerSurface { Dock = DockStyle.Top, Name = "Hud.Composer" };
        _promptBox = new PromptTextBox { Dock = DockStyle.Top, Height = 44, Prompt = "Ask about what the wand points at.", Name = "Hud.PromptField" };
        _promptBox.KeyDown += OnPromptKeyDown;
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Hud.ComposerBar" };
        _visionToggle = new IconToolButton("Hud.VisionToggle", PhosphorGlyph.Screen, "Attach the screen") { Dock = DockStyle.Left, Selected = true };
        _visionToggle.Click += (_, _) => { _visionToggle.Selected = !_visionToggle.Selected; SyncVisionHint(); };
        _visionHint = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Hud.VisionHint" };
        bar.Controls.Add(_visionHint);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        bar.Controls.Add(_visionToggle);
        _composer.Content.Controls.Add(bar);
        _composer.Content.Controls.Add(_promptBox);
        _composer.Height = _composer.HeightFor(_promptBox.Height + bar.Height);

        // The answers appear under the question once there are any: a card per question and per answer, in the
        // AInbox card language, fitting their content up to a cap and then scrolling.
        // Full width until the answers reach their cap and scroll: the cards line up with the composer's edges.
        _answers = new ThreadView("Hud.ResponseBox") { Dock = DockStyle.Top, Height = 0, ReserveScrollbar = false };
        _answers.ContentChanged += (_, _) => SizeAnswers();
        _responseGap = WidgetChrome.Spacer(10);

        // Telemetry: gateway state, Input Spine 8842 and the connected mouse, one LED each on one line.
        _telemetryLine = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "Hud.Telemetry" };

        // Wand shortcuts: one click away, collapsed by default.
        _shortcutsRow = new DisclosureRow
        {
            Dock = DockStyle.Top,
            Height = 44,
            Kicker = "SHORTCUTS",
            Sentence = "Actions Ring, fallback orb, GhostTrailer, Wiz, AInbox",
            OpenWord = "SHOW",
            Name = "Hud.Shortcuts"
        };
        _shortcutsRow.Expanded = false;
        _shortcutsRow.Toggled += (_, _) => { _shortcutsRow.Expanded = !_shortcutsRow.Expanded; FitToAnswer(); };
        _shortcutsBody = new Label
        {
            Dock = DockStyle.Top,
            Height = 92,
            AutoSize = false,
            UseMnemonic = false,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Dim,
            Font = PhosphorFonts.Body(12.5f),
            Padding = new Padding(14, 8, 0, 0),
            Text = ShortcutsText,
            Name = "Hud.ShortcutsBody"
        };

        // Footer: which model answers, what is happening, and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "Hud.Footer" };
        _modelChip = new ModelChip(_ark) { Dock = DockStyle.Left, Name = "Hud.ModelPicker" };
        _modelChip.ModelChanged += (_, _) => { SyncAskLabel(); SyncVisionHint(); };
        _statusLine = new DataLabel("ENTER ASKS") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Hud.StatusLine" };
        _askBtn = new PhosphorButton("Ask", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Hud.AskButton" };
        _askBtn.Click += async (_, _) => await AskAsync();
        footer.Controls.Add(_statusLine);
        footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_modelChip);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_askBtn);

        // Top stack (the first added sits lowest): composer, answer, telemetry, shortcuts.
        Controls.Add(_shortcutsBody);
        Controls.Add(_shortcutsRow);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(_telemetryLine);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_answers);
        Controls.Add(_responseGap);
        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "ARK // WAND", "Holodex", PhosphorTheme.Patina, headerTools);
        WidgetDrag.Enable(this);
        ShowWandStatus(gatewayOnline: false);
        SyncVisionHint();
        SyncAskLabel();
        FitToAnswer();
        ResumeLayout(false);
    }

    private bool HasAnswer => _answers.Cards.Any();

    private void SizeAnswers()
    {
        // Card heights are device pixels, so the cap is too; a 96-DPI 260 px cap cut most answers at 150%.
        float s = DeviceDpi / 96f;
        int work = Screen.FromControl(this).WorkingArea.Height;
        int cap = Math.Min((int)Math.Round(AnswerMaxHeight * s), work / 2);
        int want = HasAnswer ? Math.Min(_answers.ContentHeight(_answers.FitWidth) + 2, cap) : 0;
        if (_answers.Height == want) return;
        _answers.Height = want;
        if (IsHandleCreated) FitToAnswer();
    }

    /// <summary>No answer yet: no answer box. An answer (or the asking state) opens it under the question.</summary>
    private void FitToAnswer() => FitToContent.Apply(this, c =>
        c == _answers || c == _responseGap ? HasAnswer
        : c == _shortcutsBody ? _shortcutsRow.Expanded
        : true);

    /// <summary>
    /// The model that actually answers: on the Nebius path a text-only Nemotron hands an attached screen to
    /// Nemotron 3.5 Super VL (ArkStationClient.AskNebiusTokenFactoryAsync), so say that rather than the chip's model.
    /// </summary>
    private string AnsweringModel(bool withScreen)
    {
        string model = _ark.SelectedModel;
        bool nebiusPath = !_ark.LastGatewayOnline || ArkStationClient.IsNemotronModel(model);
        bool vision = model.Contains("vl", StringComparison.OrdinalIgnoreCase) || model.Contains("omni", StringComparison.OrdinalIgnoreCase) || model.Contains("vision", StringComparison.OrdinalIgnoreCase);
        return withScreen && nebiusPath && !vision ? "nvidia/Nemotron-3.5-Super-VL" : model;
    }

    private void SyncVisionHint() => _visionHint.Text = _visionToggle.Selected
        ? $"SCREEN GOES WITH IT // {ModelChip.ShortName(AnsweringModel(true)).ToUpperInvariant()} READS IT"
        : "TEXT ONLY";

    private void SyncAskLabel()
    {
        if (_working) return;
        string first = _modelChip.Label.Split(' ')[0];
        _askBtn.Text = _galaLed.Text == "GALA IN HIVEMIND" ? "Ask Gala" : first.Length > 0 ? $"Ask {first}" : "Ask";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { SurfaceMotion.Dismiss(this); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { _ = AskAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public async void SummonAt(Point cursorPos)
    {
        _summonCursorPos = cursorPos;

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 18, cursorPos.Y + 18);

        SurfaceMotion.Present(this);
        Activate();
        _promptBox.Focus();
        _promptBox.SelectAll();

        // The local key state is instant, so the gateway LED never waits on the probe; the probe below upgrades it.
        if (!_ark.LastGatewayOnline) ShowWandStatus(false);
        _ = DetectMouseOnceAsync();

        var status = await _ark.CheckStatusAsync();
        _spineOnline = status.InputSpineOnline;
        ShowWandStatus(status.GatewayOnline);
        // Only an owner's Ark has a gateway to miss; a public install asks Nemotron directly and the LED says so.
        if (!_gatewayNoticeShown && !status.GatewayOnline && !_ark.HasGatewayInboundKey && _ark.ArkInstalled)
        {
            _gatewayNoticeShown = true;
            PhosphorToast.Show("Gateway unavailable",
                status.NebiusKeyLoaded
                    ? "No inbound key in Keymaster, so the HUD asks Nemotron directly through Token Factory. Wand and ring still work."
                    : "The HUD cannot ask the Ark gateway: no inbound key in Keymaster. Wand and ring still work; add a Nebius key in Holodex (Alt+H).",
                "GATEWAY // KEYMASTER", 4200, status.NebiusKeyLoaded ? ToastTone.Info : ToastTone.Warning);
        }
        await _modelChip.RefreshAsync();
        ShowKeyState();
        // Who answers the next ask: Gala when Hivemind has it ready, said by the LED and the button.
        if (!_working) await GalaUnavailableReasonAsync();
        SyncTelemetry();
        SyncAskLabel();
    }

    /// <summary>The mouse scan shells out to PowerShell for a second or two, so it runs off the UI thread, once.</summary>
    private Task DetectMouseOnceAsync() => _mouseScan ??= DetectMouseAsync();

    private async Task DetectMouseAsync()
    {
        try
        {
            var mouse = await Task.Run(MouseHardwareAndWandEngine.DetectConnectedMouse);
            _mouseModel = mouse.Model;
        }
        catch
        {
            _mouseModel = "";
        }
        if (!IsDisposed) SyncTelemetry();
    }

    /// <summary>Gateway online, Nebius direct with a key, or no key: the gateway LED carries it.</summary>
    private void ShowWandStatus(bool gatewayOnline)
    {
        _gatewayOnline = gatewayOnline;
        SyncTelemetry();
    }

    private void SyncTelemetry()
    {
        bool keyed = _gatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());
        var gateway = _gatewayOnline ? (PhosphorTheme.Moss, "GATEWAY ONLINE")
            : keyed ? (PhosphorTheme.Patina, "NEMOTRON // TOKEN FACTORY")
            : (PhosphorTheme.Ember, "NO MODEL KEY // ALT+H ADDS ONE");
        var mouse = _mouseModel == null ? (PhosphorTheme.Ghost, "MOUSE")
            : _mouseModel.Length == 0 ? (PhosphorTheme.Ember, "NO MOUSE")
            : (PhosphorTheme.Moss, ShortMouse(_mouseModel));
        // Input Spine exists only on an owner's Ark; a public install shows no LED for it.
        if (!_ark.ArkInstalled)
        {
            _telemetryLine.Set(_galaLed, gateway, mouse);
            return;
        }
        var spine = _spineOnline switch
        {
            true => (PhosphorTheme.Moss, "SPINE 8842"),
            false => (PhosphorTheme.Ember, "SPINE 8842 DOWN"),
            null => (PhosphorTheme.Ghost, "SPINE 8842")
        };
        _telemetryLine.Set(_galaLed, gateway, spine, mouse);
    }

    /// <summary>"Logitech G PRO X Superlight 2 (Lightspeed)" as "G PRO X SUPERLIGHT 2": the LED row has one line.</summary>
    private static string ShortMouse(string model)
    {
        string m = model.Split('(')[0].Trim();
        foreach (var brand in new[] { "Logitech ", "Razer ", "SteelSeries ", "Corsair " })
            if (m.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) m = m[brand.Length..];
        return m.Length > 24 ? m[..24].TrimEnd() : m;
    }

    /// <summary>
    /// The footer status is only what is happening now, in words that fit beside the chip: the answer card names
    /// the model that answered and the gateway LED names a missing key, so neither is repeated here.
    /// </summary>
    private void ShowKeyState()
    {
        if (!_working) SetStatus("ENTER ASKS", PhosphorTheme.Ghost);
        SyncTelemetry();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLine.ForeColor = color;
        _statusLine.Text = text;
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        await AskAsync();
    }

    private async Task AskAsync()
    {
        string prompt = _promptBox.Text.Trim();
        if (_working) return;
        if (string.IsNullOrEmpty(prompt)) { _promptBox.Focus(); return; }

        string? snapshot = _visionToggle.Selected
            ? ArkStationClient.CaptureRegionAroundCursor(_summonCursorPos.X, _summonCursorPos.Y)
            : null;

        _working = true;
        _promptBox.Enabled = false;
        _askBtn.Enabled = false;
        _answers.Add(ThreadTone.You, snapshot != null ? "YOU // WITH THE SCREEN" : "YOU", prompt, snapshot != null ? PhosphorGlyph.Screen : null);
        // The pending card says who is answering, so the status line is quiet until the answer lands.
        SetStatus("", PhosphorTheme.Ghost);
        FitToAnswer();
        Invalidate();
        _setThinkingState(true);
        using var cancel = new System.Threading.CancellationTokenSource();
        _askCancel = cancel;

        (bool Ok, string Text, string Kicker) answer;
        try
        {
            // Gala in the operator's Hivemind answers when it can take the turn; otherwise Nemotron answers directly
            // and its card says why Gala did not. A Gala card is only ever Gala's own reply.
            string? galaReason = await GalaUnavailableReasonAsync();
            SyncTelemetry();
            answer = galaReason == null ? await AskGalaAsync(prompt, snapshot, cancel.Token) : (false, "", galaReason);
            if (!answer.Ok && answer.Text.Length == 0 && !cancel.IsCancellationRequested)
                answer = await AskNemotronAsync(prompt, snapshot, because: answer.Kicker, linked: galaReason == null, cancel.Token);
        }
        catch (Exception ex)
        {
            answer = (false, ex.Message, "NOT ANSWERED");
            FailFooter(_answers.Add(ThreadTone.Fail, answer.Kicker, answer.Text), prompt);
        }
        try
        {
            _promptBox.Clear();
            SetStatus("ENTER ASKS", PhosphorTheme.Ghost);
            SyncTelemetry();
            SyncAskLabel();
            if (!Visible && !cancel.IsCancellationRequested)
            {
                // Esc hid the HUD mid-ask: the answer still arrives, so say where it is.
                string first = answer.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                PhosphorToast.Show(answer.Ok ? TitleCase(answer.Kicker) : "Not answered", first.Length > 140 ? first[..140] + "..." : first,
                    "WAND // ALT+SPACE OPENS IT", 4200, answer.Ok ? ToastTone.Success : ToastTone.Warning);
            }
            FitToAnswer();
        }
        finally
        {
            _askCancel = null;
            _working = false;
            _setThinkingState(false);
            // After _working clears: who answers the next ask, now that this one has settled.
            SyncAskLabel();
            _promptBox.Enabled = true;
            _askBtn.Enabled = true;
            _promptBox.Focus();
            Invalidate();
        }
    }

    /// <summary>"GALA // QWEN3-8B // HIVEMIND" as a toast title: "Gala // Qwen3-8b // Hivemind".</summary>
    private static string TitleCase(string kicker) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kicker.ToLowerInvariant());

    /// <summary>
    /// Why Gala cannot be asked, in the words its LED and the answer card carry, or null when it can: Hivemind is
    /// signed out or did not answer, or it lists no Gala. The roster's dispatchReady is not a gate: it reads false for
    /// every agent while Sessions still run their turns, so Gala is asked and the Session says whether it answered.
    /// </summary>
    private async Task<string?> GalaUnavailableReasonAsync()
    {
        (Color Led, string Text)? state = null;
        if (!HivemindClient.HasSavedSignIn) state = (PhosphorTheme.Ghost, "HIVEMIND SIGNED OUT");
        else if (await _ark.Hivemind.ListAgentsAsync() is not { } agents) state = (PhosphorTheme.Rust, "HIVEMIND UNREACHABLE");
        else if (!agents.Any(a => a.Id == "gala")) state = (PhosphorTheme.Ember, "NO GALA IN HIVEMIND");
        _galaLed = state ?? (PhosphorTheme.Moss, "GALA IN HIVEMIND");
        return state?.Text;
    }

    /// <summary>
    /// Asks Gala in the operator's Hivemind (ask_agent): the first ask opens a Session with Gala, later ones continue
    /// it, and Hivemind runs Gala's turn with its receipt. An attached screen is read first by the vision model and its
    /// words go with the question, so Gala answers about the screen without receiving pixels. The reply card names the
    /// model Gala's receipt reports and carries the receipt. When Gala does not answer, the result carries the reason
    /// and no text, so the caller answers on Nemotron instead.
    /// </summary>
    private async Task<(bool Ok, string Text, string Kicker)> AskGalaAsync(string prompt, string? snapshot, System.Threading.CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string content = prompt;
        if (snapshot != null)
        {
            string readerId = AnsweringModel(true);
            string reader = ModelChip.ShortName(readerId).ToUpperInvariant();
            var reading = _answers.AddPending($"READING THE SCREEN // {reader}");
            reading.SetFooter("", StopAction());
            try
            {
                var (seen, description) = await _ark.AskArkGatewayAsync(
                    $"Describe, in a few plain sentences, what on this screen bears on the question below. Do not answer it.\n\nQuestion: {prompt}", snapshot).WaitAsync(ct);
                reading.Resolve(seen ? ThreadTone.Action : ThreadTone.Fail, seen ? $"SCREEN READ BY {reader}" : $"SCREEN NOT READ // {reader}", description);
                if (seen) content = $"{prompt}\n\nWhat is on my screen, as {ModelChip.ShortName(readerId)} read it:\n{description}";
            }
            catch (OperationCanceledException)
            {
                reading.Resolve(ThreadTone.Fail, "STOPPED BY YOU", "The screen was not read and nothing was asked.");
                return (false, "STOPPED", "STOPPED BY YOU");
            }
        }

        var pending = _answers.AddPending("ASKING GALA // HIVEMIND");
        pending.SetFooter("", StopAction());
        var ask = await _ark.Hivemind.AskAgentAsync(_galaSessionId, "gala", content, Guid.NewGuid(),
            title: $"Holodex: {(prompt.Length > 60 ? prompt[..60] : prompt)}");
        if (ask == null)
        {
            pending.Resolve(ThreadTone.Fail, "GALA // NOT ASKED // HIVEMIND", _ark.Hivemind.LastError);
            return (false, "", "GALA NOT ASKED");
        }
        _galaSessionId = ask.SessionId;
        _openInHivemindBtn.Visible = true;

        // Hivemind runs the turn on its own schedule; the Session is read until Gala's reply to this ask lands.
        var deadline = DateTime.UtcNow + GalaReplyTimeout;
        string failure = "";
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(GalaPollInterval, ct);
                var session = await _ark.Hivemind.ReadSessionAsync(ask.SessionId, ct);
                if (session == null) continue;
                int asked = session.Messages.ToList().FindIndex(m => m.Id == ask.MessageId);
                var reply = asked < 0 ? null : session.Messages.Skip(asked + 1).FirstOrDefault(m => m.Role == "agent" && m.AgentId == "gala");
                if (reply != null)
                {
                    string model = reply.Model.Length > 0 ? ModelChip.ShortName(reply.Model).ToUpperInvariant() : "MODEL NOT REPORTED";
                    string kicker = $"GALA // {model} // HIVEMIND";
                    pending.Resolve(ThreadTone.Agent, kicker, reply.Content);
                    string receipt = reply.ExecutionId.Length > 8 ? $"RECEIPT {reply.ExecutionId[..8]} // " : "";
                    AnswerFooter(pending, prompt, $"{receipt}{Seconds(clock)}", gala: true);
                    return (true, reply.Content, kicker);
                }
                if (session.Summary.Status is "failed" or "completed" || (session.Summary.Status == "paused" && session.Summary.Error.Length > 0))
                {
                    failure = session.Summary.Error.Length > 0 ? session.Summary.Error : $"The Session is {session.Summary.Status} with no reply from Gala.";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            pending.Resolve(ThreadTone.Fail, "STOPPED BY YOU // GALA", "Holodex stopped waiting. The ask stays in its Hivemind Session, where Gala may still answer.");
            pending.SetFooter("", OpenInHivemindAction());
            return (false, "STOPPED", "STOPPED BY YOU");
        }
        if (failure.Length == 0) failure = $"Gala did not reply within {GalaReplyTimeout.TotalMinutes:0} minutes. The Session stays open in Hivemind.";
        pending.Resolve(ThreadTone.Fail, "GALA // NOT ANSWERED // HIVEMIND", failure);
        FailFooterGala(pending);
        return (false, "", "GALA DID NOT ANSWER");
    }

    /// <summary>
    /// Nemotron through Token Factory (or the Ark gateway), as the HUD always asked. <paramref name="because"/> is why
    /// Gala is not answering, and the card's kicker carries it beside the model that did. <paramref name="linked"/>
    /// joins this card to Gala's failure card above it, so the two read as one exchange.
    /// </summary>
    private async Task<(bool Ok, string Text, string Kicker)> AskNemotronAsync(string prompt, string? snapshot, string because, bool linked, System.Threading.CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string answeredBy = ModelChip.ShortName(AnsweringModel(snapshot != null)).ToUpperInvariant();
        var pending = _answers.AddPending($"ASKING {answeredBy} // {because}");
        pending.LinkedAbove = linked;
        pending.SetFooter("", StopAction());
        bool ok = false;
        string output;
        try { (ok, output) = await _ark.AskArkGatewayAsync(prompt, snapshot).WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            pending.Resolve(ThreadTone.Fail, "STOPPED BY YOU", $"Holodex stopped waiting for {answeredBy}.");
            return (false, "STOPPED", "STOPPED BY YOU");
        }
        catch (Exception ex) { output = ex.Message; }
        string kicker = ok ? $"ANSWERED BY {answeredBy} // {because}" : $"NOT ANSWERED // {answeredBy}";
        pending.Resolve(ok ? ThreadTone.Agent : ThreadTone.Fail, kicker, output);
        if (ok) AnswerFooter(pending, prompt, Seconds(clock), gala: false);
        else FailFooter(pending, prompt);
        return (ok, output, kicker);
    }

    private static string Seconds(System.Diagnostics.Stopwatch clock) => $"{clock.Elapsed.TotalSeconds:0.0} S";

    // ---- Card actions: the kit's footer on every answer and failure, and Stop on every wait ----------------------

    private CardAction StopAction() => new("Thread.Stop", PhosphorGlyph.Stop, "Stop", (_, _) => _askCancel?.Cancel());

    private static CardAction CopyAction() => new("Thread.Copy", PhosphorGlyph.Copy, "Copy", (card, _) =>
    {
        try { Clipboard.SetText(card.Body); } catch { return; }
        string meta = card.Meta;
        card.SetMeta("COPIED");
        var restore = new System.Windows.Forms.Timer { Interval = 1400 };
        restore.Tick += (_, _) => { restore.Dispose(); if (!card.IsDisposed) card.SetMeta(meta); };
        restore.Start();
    });

    private CardAction RetryAction(string prompt) => new("Thread.Retry", PhosphorGlyph.Retry, "Ask again", (_, _) =>
    {
        if (_working) return;
        _promptBox.Text = prompt;
        _ = AskAsync();
    });

    private static CardAction OpenInHivemindAction() =>
        new("Thread.OpenInHivemind", PhosphorGlyph.ExternalLink, "Open in Hivemind", (_, _) => HivemindClient.OpenWebSessions());

    /// <summary>A rating of one answer, kept on this PC (feedback.jsonl beside the other Holodex data): nothing is sent.</summary>
    private static CardAction FeedbackAction(bool up) => new(up ? "Thread.ThumbUp" : "Thread.ThumbDown", up ? PhosphorGlyph.ThumbUp : PhosphorGlyph.ThumbDown,
        up ? "Good answer" : "Bad answer", (card, button) =>
        {
            // The two ratings are one choice: picking one clears the other.
            foreach (var other in card.Controls.OfType<IconToolButton>())
                if (other != button && other.Name is "Thread.ThumbUp" or "Thread.ThumbDown") other.Selected = false;
            if (!button.Selected) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "feedback.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow,
                    rating = up ? "up" : "down",
                    kicker = card.Kicker,
                    answerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(card.Body))).ToLowerInvariant()
                }) + Environment.NewLine);
            }
            catch { }
        }, Toggle: true);

    private void AnswerFooter(ThreadCard card, string prompt, string meta, bool gala)
    {
        var actions = new List<CardAction> { CopyAction(), RetryAction(prompt), FeedbackAction(true), FeedbackAction(false) };
        if (gala) actions.Add(OpenInHivemindAction());
        card.SetFooter(meta, actions.ToArray());
    }

    /// <summary>Gala's own failure card: copy its real cause, or open the Session where the ask still waits.</summary>
    private static void FailFooterGala(ThreadCard card) => card.SetFooter("", CopyAction(), OpenInHivemindAction());

    /// <summary>A failure keeps its real cause as its body; its footer offers copying that cause and asking again.</summary>
    private void FailFooter(ThreadCard card, string prompt) => card.SetFooter("", CopyAction(), RetryAction(prompt));

    /// <summary>
    /// Live capture (the pipe's hud:submit): asks <paramref name="question"/> through the real composer and Ask path,
    /// with the screen toggle off so no screen pixels leave the machine for a capture.
    /// </summary>
    internal async Task AskForCaptureAsync(string question)
    {
        bool screen = _visionToggle.Selected;
        _visionToggle.Selected = false;
        _promptBox.Text = question;
        try { await AskAsync(); }
        finally { _visionToggle.Selected = screen; }
    }

    /// <summary>The answer box's sizes and scroll state, for the pipe's hud:state.</summary>
    internal string DescribeAnswers() =>
        $"dpi={DeviceDpi}:box={_answers.Width}x{_answers.Height}:client={_answers.ClientSize.Width}x{_answers.ClientSize.Height}" +
        $":content={_answers.ContentHeight(_answers.FitWidth)}:display={_answers.DisplayRectangle.Height}" +
        $":vscroll={_answers.VerticalScroll.Visible}:cards={_answers.Cards.Count()}:form={Width}x{Height}";

    /// <summary>
    /// As-built render states (SurfaceCapture): "asking", "answered" and "failed" show a question with the screen and
    /// the card a live ask holds at that moment (pending, answer, failure) on the path a live ask takes today, Nemotron
    /// when Gala did not answer; "reset" clears them. No state draws a Gala reply: only Gala's own reply is ever shown as one.
    /// </summary>
    internal void PreviewState(string state)
    {
        if (_working) return; // never draw over a live ask
        _answers.Clear();
        string model = ModelChip.ShortName(AnsweringModel(true)).ToUpperInvariant();
        if (state is "asking" or "answered" or "failed")
            _answers.Add(ThreadTone.You, "YOU // WITH THE SCREEN", "Why would this hold the PR?", PhosphorGlyph.Screen);
        const string question = "Why would this hold the PR?";
        if (state == "asking")
            _answers.AddPending("ASKING GALA // HIVEMIND").SetFooter("", StopAction());
        else if (state == "answered")
        {
            // The honest fallback as a live ask draws it: Gala's real failure, linked to the model that answered.
            FailFooterGala(_answers.Add(ThreadTone.Fail, "GALA // NOT ANSWERED // HIVEMIND",
                "Native mission worker failed: Missing environment variable: ARK_MODEL_GATEWAY_INBOUND_KEY."));
            var answer = _answers.Add(ThreadTone.Agent, $"ANSWERED BY {model} // GALA DID NOT ANSWER",
                "Navigate holds it because the Verify lane is paused, not because of the diff: the check reads \"could not execute this validation lane\". Resume the release, then re-request the check.");
            answer.LinkedAbove = true;
            AnswerFooter(answer, question, "", gala: false); // a render has no latency to report
        }
        else if (state == "failed")
            FailFooter(_answers.Add(ThreadTone.Fail, $"NOT ANSWERED // {model}", "Vision request to nvidia/Nemotron-3.5-Super-VL returned no answer. Check the Nebius key and try again."), question);
        ShowKeyState();
        if (state == "asking") SetStatus("", PhosphorTheme.Ghost);
        SizeAnswers();
        FitToAnswer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
