using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Motion helpers bound to the packages/tokens motion contract: dur-disclosure (240ms),
/// dur-disclosure-item (180ms) and ease-analog cubic-bezier(0.45,0.05,0.35,1).
/// Reduced motion follows the Windows "Show animations" setting.
/// </summary>
internal static class PhosphorMotion
{
    public static int DurDisclosure => Math.Max(1, PhosphorTheme.Px("dur-disclosure"));
    public static int DurDisclosureItem => Math.Max(1, PhosphorTheme.Px("dur-disclosure-item"));

    public static bool ReducedMotion
    {
        get
        {
            try { return !SystemInformation.UIEffectsEnabled; }
            catch { return false; }
        }
    }

    /// <summary>cubic-bezier(0.45,0.05,0.35,1) solved for y at progress t.</summary>
    public static float EaseAnalog(float t) => CubicBezier(Math.Clamp(t, 0f, 1f), 0.45f, 0.05f, 0.35f, 1f);

    /// <summary>
    /// ease-spring cubic-bezier(0.34,1.36,0.5,1) (CP-36, approved 2026-10-20): the one overshoot easing, for
    /// things that lift, drop, land or snap back. Overshoots past 1 before settling.
    /// </summary>
    public static float EaseSpring(float t) => CubicBezier(Math.Clamp(t, 0f, 1f), 0.34f, 1.36f, 0.5f, 1f);

    /// <summary>Ease-out cubic for entries that should settle, not swing.</summary>
    public static float EaseOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float u = 1f - t;
        return 1f - u * u * u;
    }

    private static float CubicBezier(float x, float x1, float y1, float x2, float y2)
    {
        float t = x;
        for (int i = 0; i < 6; i++)
        {
            float cx = Bez(t, x1, x2) - x;
            float d = BezD(t, x1, x2);
            if (Math.Abs(d) < 1e-5f) break;
            t = Math.Clamp(t - cx / d, 0f, 1f);
        }
        return Bez(t, y1, y2);
    }

    private static float Bez(float t, float p1, float p2) => 3f * (1 - t) * (1 - t) * t * p1 + 3f * (1 - t) * t * t * p2 + t * t * t;
    private static float BezD(float t, float p1, float p2) => 3f * (1 - t) * (1 - t) * p1 + 6f * (1 - t) * t * (p2 - p1) + 3f * t * t * (1 - p2);
}

internal sealed record PhosphorTipInfo(string Title, string Body, string? Keys = null);

/// <summary>
/// One hover tooltip for every Omniwand surface. Controls opt in by Name (the catalog below), so no form
/// carries its own tooltip code. A message filter watches mouse movement, waits a short dwell, then shows a
/// non-activating tip that rises 6px and fades in on the disclosure timing, with the highlight aura on its lead edge.
/// </summary>
internal static class PhosphorTips
{
    private const int DwellMs = 420;

    private static readonly Dictionary<string, PhosphorTipInfo> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hud.PromptField"] = new("Ask", "Ask about what the wand points at. Shift+Enter breaks a line.", "Enter"),
        ["Hud.VisionToggle"] = new("Attach the screen", "On: the screen around the wand tip goes with the question so a vision model sees it."),
        ["Hud.ModelPicker"] = new("Model", "Which model answers and where it runs. The dot is ready (patina) or no key (ember). Click to switch."),
        ["Hud.AskButton"] = new("Ask", "Sends the question to the model on the chip.", "Ctrl+Enter"),
        ["Hud.SnipButton"] = new("Lasso", "Draw around anything on screen, annotate it, then ask about it.", "Alt+S"),
        ["Hud.WizButton"] = new("Wiz", "Turns a goal into a Spell, an ordered chain of steps.", "Alt+W"),
        ["Hud.AInboxButton"] = new("AInbox", "Drop a file, snippet or capture and hand it off with a goal.", "Alt+I"),
        ["Hud.HolodexButton"] = new("Holodex", "Session chat, captures, Codex, Spells and mouse mods.", "Alt+H"),
        ["Hud.StatusBadge"] = new("Wand status", "Online: the ArkStation gateway answers. Ready: Nebius direct with a linked key. No key: add one in Holodex.", "Alt+H"),
        ["Hud.Telemetry"] = new("Telemetry", "Gateway state, Input Spine on 8842 up or down, and the connected mouse."),
        ["Hud.Shortcuts"] = new("Shortcuts", "The wand's keys and ring: Actions Ring slots, the fallback orb, GhostTrailer, Wiz and AInbox. Click to show or hide."),

        ["Wiz.PromptField"] = new("Describe the goal", "Say what you want done. Shift+Enter breaks a line.", "Enter"),
        ["Wiz.CraftButton"] = new("Send", "Sends the goal to Wiz. With a Nemotron model it acts through tools for up to 6 steps, then returns a Spell draft of the type on the chip. While Wiz works, this button stops it.", "Ctrl+Enter"),
        ["Wiz.AttachScreenToggle"] = new("Attach the screen", "On: Nemotron 3.5 Super VL describes what is open around the wand tip before Wiz plans."),
        ["Wiz.FlowLensSelect"] = new("Spell type", "What kind of Spell, with what it does beside it. Omnitek picks it from your words (ritual, combo, summon...); the dot lights when it is sure. Click to choose."),
        ["Wiz.ModelPicker"] = new("Model", "Wiz's own reasoning model and where it ran (Nemotron 3 Super by default). Nemotron models can act through tools; others draft a plan only. Click to switch."),
        ["Wiz.ApprovalRow"] = new("Approval", "Wiz wants to save something. Nothing is written until you approve."),
        ["Wiz.ApprovalLine"] = new("Pending action", "The tool call waiting for you, with its arguments."),
        ["Wiz.ApproveButton"] = new("Approve", "Lets Wiz run this action: save to Codex or queue a Holodex work order."),
        ["Wiz.SkipButton"] = new("Skip", "Declines this action. Nothing is saved and Wiz carries on without it."),
        ["Wiz.RuntimeBadge"] = new("Runtime model", "The model Wiz runs on; after a craft, the one that actually answered."),
        ["Wiz.RuntimeLine"] = new("Host", "Where Wiz runs (Nebius Token Factory, Antigravity CLI or the Ark model gateway) and the model, updated with the one that answered."),
        ["Wiz.StarterExplain"] = new("Explain my screen", "Fills in a question about what is open, with the screen attached."),
        ["Wiz.StarterChecklist"] = new("Checklist", "Starts a goal that comes back as a step-by-step checklist."),
        ["Wiz.StarterAutomate"] = new("Automate", "Starts a goal that comes back as a rerunnable Combo."),

        ["AInbox.Well"] = new("Composer", "Drop, paste or pick anything; it stacks by where it came from. Say what should happen below.", "Ctrl+Enter"),
        ["AInbox.IntentField"] = new("What should happen", "The ask for the active stack. Tab takes Omnitek's suggestion.", "Ctrl+Enter"),
        ["AInbox.Kind.File"] = new("File", "Choose files, or drop or paste them on the composer. Each joins the stack."),
        ["AInbox.Kind.Screen"] = new("Screen", "Grabs 640x420 around the wand tip."),
        ["AInbox.Kind.Window"] = new("Window", "Captures the whole window under the wand tip, with its app and title as the origin."),
        ["AInbox.Kind.Pick"] = new("Pick", "Point at a window or element, then Send to AInbox from the picker.", "Alt+E"),
        ["AInbox.Kind.Lasso"] = new("Lasso", "Draw a free-form region on any screen, then Send to AInbox.", "Alt+S"),
        ["AInbox.Kind.Record"] = new("Record (planned)", "Region recording with step marks, landing in the stack. Designed, not built yet."),
        ["AInbox.Kind.Whisper"] = new("Whisper (planned)", "A Ghost Whisper voice note pinned to a capture. Designed, not wired yet."),
        ["AInbox.Kind.Snippet"] = new("Snippet", "Adds text, code or notes from the clipboard."),
        ["AInbox.Kind.Link"] = new("Link", "Adds a URL from the clipboard; GitHub links carry their repository."),
        ["AInbox.Stack"] = new("Stack", "Every capture, grouped by where it came from: app and window, repository, folder or site."),
        ["AInbox.StackGroup"] = new("Context group", "Captures that share an origin. Click to make this group the one you are sending."),
        ["AInbox.CapturedRow"] = new("Capture", "One capture: its kind, name and origin detail. The x takes it out of the stack."),
        ["AInbox.Prediction"] = new("What should this become", "Omnitek ranks the intents for this group; Nemotron 3 Nano re-ranks, explains and synthesizes. Click a row to steer.", "Up/Down"),
        ["AInbox.Aegis"] = new("Aegis", "Secrets found in text captures. They are redacted before anything is sent. Images are not scanned."),
        ["AInbox.StowButton"] = new("Stow in Vault", "Keeps each found secret in Windows Credential Manager under Omniwand:Aegis."),
        ["AInbox.SyncStatus"] = new("Delivery", "Handoffs go to the AInbox service (:8840). Queued ones retry every 30 seconds until it takes them."),
        ["AInbox.ScrollSummary"] = new("Handoff scroll", "The active group's scroll in one line. Review opens it to read and edit."),
        ["AInbox.ScrollField"] = new("Scroll field", "Fields follow the chosen intent. The tag says where the value came from: predicted, from the stack, Nemotron, or you."),
        ["AInbox.SubmitButton"] = new("Send", "Waits five seconds so you can undo (or press Esc), then queues the active group's scroll and captures for AInbox.", "Ctrl+Enter"),
        ["AInbox.HandoffScroll"] = new("Handoff scroll", "What the receiving agent reads. Edit any field; the SHA-256 seal follows your edits."),

        ["Holodex.TabBar"] = new("Holodex tabs", "Session, AInbox, Codex, GrimX and Mods. Each tab keeps its state while the drawer is closed."),
        ["Holodex.VaultBadge"] = new("Connection", "Nemotron ready or Gateway online when a key works. Click to link or replace the Nebius key."),
        ["Holodex.ConnectionLine"] = new("Connection", "The gateway or Nebius key state in full, and whether the key is saved in the Windows vault."),
        ["Holodex.ReplaceKeyButton"] = new("Replace key", "Opens the key row to link a different Nebius Token Factory key."),
        ["Holodex.RosterLine"] = new("Roster", "Who the message reaches: every Syndicate agent in its accent, or the chosen conductor's role."),
        ["Holodex.TokenField"] = new("Nebius key", "Paste a Nebius Token Factory key. It is stored in Windows Credential Manager, never in a file."),
        ["Holodex.LinkTokenButton"] = new("Link key", "Saves the key to the Windows vault and checks the endpoint."),
        ["Holodex.SessionInput"] = new("Session chat", "Message the Syndicate. /order queues a work order. Shift+Enter breaks a line.", "Enter"),
        ["Holodex.RosterSelect"] = new("Send to", "Who gets the message: the whole Syndicate or one conductor. Click to choose."),
        ["Holodex.SessionSendBtn"] = new("Send", "Sends the message to the agent on the chip.", "Enter"),
        ["Holodex.AddWorkOrderBtn"] = new("Add work order", "Queues the message as a task for the agent on the chip."),
        ["Holodex.MouseLine"] = new("Mouse", "The connected mouse: brand, model and USB/HID VID/PID."),
        ["Holodex.PointerLine"] = new("Pointer", "Windows pointer speed, the current DPI and VSync cursor morph."),
        ["Holodex.DpiSlider"] = new("Pointer DPI", "Drag to set how fast the wand moves. Applies to the Windows pointer speed."),
        ["Holodex.RunOnboardingBtn"] = new("Replay the tour", "Walks through every Omniwand tool again.", "Alt+T"),
        ["Holodex.LassoButton"] = new("Free-form lasso", "Draw around anything on screen, annotate it, then ask about it.", "Alt+S"),
        ["Holodex.OpenWizButton"] = new("Open Wiz", "Turns a goal into a Spell, an ordered chain of steps.", "Alt+W"),
        ["Holodex.CodexListBox"] = new("Codex", "Saved captures, palettes and element picks."),
        ["Holodex.PointButton"] = new("Point at UI", "Hover any window or element and click to lock it, then ask about it.", "Alt+E"),
        ["Holodex.OpenAInboxButton"] = new("Open AInbox", "The full intake: stack captures and send a Handoff Scroll to a pod.", "Alt+I"),
        ["Holodex.PodInboxList"] = new("Pod inboxes", "Where a Handoff Scroll can go: each pod, its channel and the conductor that reads it."),
        ["Holodex.AutoBindButton"] = new("Auto-bind mouse", "Wiz binds Mouse4, Mouse5 and right-hold flicks to defaults for the connected mouse."),
        ["Holodex.GrimXList"] = new("Bindings and orb", "Each mouse binding and what it runs, then the eight Spell orb slots."),
        ["Holodex.ModsInfoBox"] = new("Equipped", "The spellbook's gesture casts and the mods Omniwand runs."),

        ["Snip.Tool.Lasso"] = new("Lasso", "Drag around any shape on screen.", "Alt+S"),
        ["Snip.Tool.Rect"] = new("Rectangle", "Drag a rectangular frame."),
        ["Snip.Tool.Point"] = new("Point at UI", "Hover any window or element; click to lock it.", "Alt+E"),
        ["Snip.Tool.Draw"] = new("Draw", "Freehand callouts on the capture."),
        ["Snip.Tool.Arrow"] = new("Arrow", "Drag to point at a target."),
        ["Snip.Tool.Box"] = new("Box", "Drag to highlight a target."),
        ["Snip.InkButton.Coral"] = new("Ink: coral", "Annotations draw in coral. Click for patina, then seal."),
        ["Snip.InkButton.Patina"] = new("Ink: patina", "Annotations draw in patina. Click for seal, then coral."),
        ["Snip.InkButton.Seal"] = new("Ink: seal", "Annotations draw in seal. Click for coral, then patina."),
        ["Snip.Telemetry"] = new("Telemetry", "What the wand holds: class, palette hex, size, position and process."),
        ["Snip.VisionPromptField"] = new("Ask about the capture", "Empty asks for a description and next steps.", "Enter"),
        ["Snip.VisionModelSelect"] = new("Vision model", "Which model reads the capture. Click to switch."),
        ["Snip.AnalyzeButton"] = new("Ask", "Sends the capture, its annotations and your question to the vision model on the chip."),
        ["Snip.ToAInboxButton"] = new("Send to AInbox", "Attaches this capture to the AInbox composer."),
        ["Snip.SaveCodexButton"] = new("Save to Codex", "Keeps this capture and its answer in Holodex."),

        ["Tutorial.TryButton"] = new("Try it now", "Opens the tool for this step. The tour waits for you."),
        ["Tutorial.NextButton"] = new("Next", "Go to the next step.", "Right"),
        ["Tutorial.PrevButton"] = new("Back", "Go to the previous step.", "Left"),
        ["Tutorial.CloseButton"] = new("Skip the tour", "Replay any time from Holodex or the tray.", "Esc"),
    };

    private static TipForm? _form;
    private static Filter? _filter;
    private static readonly System.Windows.Forms.Timer Dwell = new() { Interval = DwellMs };
    private static Control? _pending;
    private static Control? _shownFor;

    public static void Install()
    {
        if (_filter != null) return;
        _filter = new Filter();
        Application.AddMessageFilter(_filter);
        Dwell.Tick += (_, _) =>
        {
            Dwell.Stop();
            if (Suppressed) return;
            if (_pending is { IsDisposed: false, Visible: true } c && Catalog.TryGetValue(c.Name, out var info)) Show(c, info);
        };
    }

    /// <summary>True while a chip menu is open: no tooltip may appear over or beside it.</summary>
    public static bool Suppressed { get; set; }

    public static bool TryGet(string name, out PhosphorTipInfo info) => Catalog.TryGetValue(name, out info!);

    private static Control? ResolveTipTarget(Control? c)
    {
        while (c != null)
        {
            if (!string.IsNullOrEmpty(c.Name) && Catalog.ContainsKey(c.Name)) return c;
            c = c.Parent;
        }
        return null;
    }

    private static void OnHover(Control? target)
    {
        if (target == _shownFor) return;
        if (target != _pending)
        {
            _pending = target;
            Dwell.Stop();
            Hide();
            if (target != null) Dwell.Start();
        }
    }

    private static void Show(Control target, PhosphorTipInfo info)
    {
        _form ??= new TipForm();
        _shownFor = target;
        var screenRect = target.RectangleToScreen(target.ClientRectangle);
        _form.Present(info, screenRect);
    }

    public static void Hide()
    {
        _shownFor = null;
        _form?.Retract();
    }

    private sealed class Filter : IMessageFilter
    {
        private const int WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_KEYDOWN = 0x0100, WM_NCMOUSEMOVE = 0x00A0;

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                    OnHover(ResolveTipTarget(Control.FromChildHandle(m.HWnd)));
                    break;
                case WM_NCMOUSEMOVE:
                case WM_MOUSELEAVE:
                    if (Control.FromChildHandle(m.HWnd) is { } c && ResolveTipTarget(c) == null) OnHover(null);
                    break;
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_KEYDOWN:
                    _pending = null;
                    Dwell.Stop();
                    Hide();
                    break;
            }
            return false;
        }
    }

    /// <summary>Non-activating, click-through tip window. Rises 6px and fades in over dur-disclosure-item.</summary>
    private sealed class TipForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020;
        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
        private readonly Stopwatch _clock = new();
        private PhosphorTipInfo _info = new("", "");
        private Point _restLocation;
        private bool _showing;

        public TipForm()
        {
            PhosphorFonts.EnsureLoaded();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = PhosphorTheme.Surface;
            Opacity = 0;
            _anim.Tick += (_, _) => Step();
        }

        protected override bool ShowWithoutActivation => true;

        private void ClipRounded()
        {
            float s = DeviceDpi / 96f;
            using var path = PhosphorTheme.CreateRoundedRect(new RectangleF(0, 0, Width, Height), PhosphorTheme.RadiusButton * s);
            Region = new Region(path);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void Present(PhosphorTipInfo info, Rectangle anchor)
        {
            _info = info;
            float s = DeviceDpi / 96f;
            using var titleFont = PhosphorFonts.Ui(13f, heavy: true);
            using var bodyFont = PhosphorFonts.Body(12f);
            int maxW = (int)(300 * s);
            using (var g = CreateGraphics())
            {
                var bodySize = g.MeasureString(info.Body, bodyFont, maxW - (int)(36 * s));
                var titleSize = g.MeasureString(info.Title, titleFont);
                int keysW = info.Keys is null ? 0 : (int)(KeycapRow.Measure(g, info.Keys, s) + 12 * s);
                int w = (int)Math.Min(maxW, Math.Max(titleSize.Width + keysW, bodySize.Width) + 36 * s);
                int h = (int)(titleSize.Height + bodySize.Height + 30 * s);
                Size = new Size(Math.Max((int)(160 * s), w), h);
            }
            ClipRounded();

            var work = Screen.FromRectangle(anchor).WorkingArea;
            int x = Math.Clamp(anchor.Left + anchor.Width / 2 - Width / 2, work.Left + 8, work.Right - Width - 8);
            int y = anchor.Bottom + (int)(10 * s);
            if (y + Height > work.Bottom - 8) y = anchor.Top - Height - (int)(10 * s);
            _restLocation = new Point(x, y);

            _showing = true;
            _clock.Restart();
            if (PhosphorMotion.ReducedMotion)
            {
                Location = _restLocation;
                Opacity = 1.0;
            }
            else
            {
                Location = new Point(x, y + (int)(6 * s));
                Opacity = 0;
            }
            if (!Visible) Show();
            Invalidate();
            _anim.Start();
        }

        public void Retract()
        {
            if (!Visible) return;
            if (PhosphorMotion.ReducedMotion) { Hide(); return; }
            _showing = false;
            _clock.Restart();
            _anim.Start();
        }

        private void Step()
        {
            float dur = PhosphorMotion.DurDisclosureItem;
            float t = Math.Min(1f, _clock.ElapsedMilliseconds / dur);
            float e = _showing ? PhosphorMotion.EaseAnalog(t) : 1f - PhosphorMotion.EaseOut(t);
            float s = DeviceDpi / 96f;
            Opacity = Math.Clamp(e, 0, 1.0);
            Location = new Point(_restLocation.X, _restLocation.Y + (int)Math.Round((1f - e) * 6f * s));
            if (t >= 1f)
            {
                _anim.Stop();
                if (!_showing) Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var path = PhosphorTheme.CreateRoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), PhosphorTheme.RadiusButton * s))
            using (var fill = new SolidBrush(PhosphorTheme.Surface))
            using (var edge = new Pen(PhosphorTheme.Line2, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            using var titleFont = PhosphorFonts.Ui(13f, heavy: true);
            using var bodyFont = PhosphorFonts.Body(12f);
            using var bone = new SolidBrush(PhosphorTheme.Bone);
            using var dim = new SolidBrush(PhosphorTheme.Dim);
            float x = 18 * s, y = 12 * s;
            g.DrawString(_info.Title, titleFont, bone, x, y);
            float titleH = g.MeasureString(_info.Title, titleFont).Height;
            if (_info.Keys is { } keys)
            {
                float kw = KeycapRow.Measure(g, keys, s);
                KeycapRow.Draw(g, keys, Width - 14 * s - kw, y + 1 * s, s);
            }
            g.DrawString(_info.Body, bodyFont, dim, new RectangleF(x, y + titleH + 4 * s, Width - 36 * s, Height));
        }
    }
}

/// <summary>Keycap chips (packages/ui kbd): mono caps on surface-2 with a line-2 border and badge radius.</summary>
internal static class KeycapRow
{
    public static float Measure(Graphics g, string combo, float s)
    {
        using var font = PhosphorFonts.Mono(10.5f);
        float w = 0;
        foreach (string part in Split(combo))
            w += g.MeasureString(part, font).Width + 10 * s + 4 * s;
        return Math.Max(0, w - 4 * s);
    }

    public static float Draw(Graphics g, string combo, float x, float y, float s)
    {
        using var font = PhosphorFonts.Mono(10.5f);
        using var ink = new SolidBrush(PhosphorTheme.Bone);
        using var pen = new Pen(PhosphorTheme.Line2, 1f);
        foreach (string part in Split(combo))
        {
            var sz = g.MeasureString(part, font);
            var r = new RectangleF(x, y, sz.Width + 10 * s, sz.Height + 4 * s);
            using (var path = PhosphorTheme.CreateRoundedRect(r, 6 * s))
            {
                using var fill = new SolidBrush(PhosphorTheme.Surface2);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            g.DrawString(part, font, ink, x + 5 * s, y + 2 * s);
            x = r.Right + 4 * s;
        }
        return x;
    }

    private static IEnumerable<string> Split(string combo)
    {
        foreach (string p in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return p.ToUpperInvariant();
    }
}
