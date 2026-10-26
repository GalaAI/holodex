using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The simplified-surface kit (skill simplify-ui, first shipped as the AInbox composer 2026-10-26): one composer
/// surface with a prompt that stays visible, icon-only tools, suggestion chips for anything the system can infer,
/// and a one-line disclosure for machine detail. Every summoned widget composes these instead of dropdowns,
/// label rows and status lines.
/// </summary>
internal enum PhosphorGlyph
{
    File, Screen, Pick, Lasso, Record, Whisper, Snippet, Link, Plus,
    Rect, Pen, Arrow, Box, Holodex, Wiz, AInbox, Codex
}

internal static class PhosphorIcons
{
    /// <summary>
    /// Stroke icons on a 24-unit grid, drawn into <paramref name="box"/>. They follow the canvas glyphs (Omniwand
    /// UI, AInbox upload board) with GDI shapes in place of SVG paths.
    /// </summary>
    public static void Draw(Graphics g, PhosphorGlyph glyph, RectangleF box, Color ink, float stroke)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        float u = box.Width / 24f;
        g.ScaleTransform(u, u);
        using var pen = new Pen(ink, stroke / u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (glyph)
        {
            case PhosphorGlyph.File:
                g.DrawLines(pen, new[] { new PointF(14, 3), new PointF(6.5f, 3), new PointF(5, 4.5f), new PointF(5, 19.5f), new PointF(6.5f, 21), new PointF(17.5f, 21), new PointF(19, 19.5f), new PointF(19, 8), new PointF(14, 3) });
                g.DrawLines(pen, new[] { new PointF(14, 3), new PointF(14, 8), new PointF(19, 8) });
                break;
            case PhosphorGlyph.Screen:
                g.DrawLines(pen, new[] { new PointF(4, 8), new PointF(4, 4), new PointF(8, 4) });
                g.DrawLines(pen, new[] { new PointF(16, 4), new PointF(20, 4), new PointF(20, 8) });
                g.DrawLines(pen, new[] { new PointF(20, 16), new PointF(20, 20), new PointF(16, 20) });
                g.DrawLines(pen, new[] { new PointF(8, 20), new PointF(4, 20), new PointF(4, 16) });
                g.DrawEllipse(pen, 9, 9, 6, 6);
                break;
            case PhosphorGlyph.Pick:
                g.DrawPolygon(pen, new[] { new PointF(5, 3), new PointF(11, 19), new PointF(13, 13), new PointF(19, 11) });
                g.DrawLine(pen, 14.5f, 14.5f, 19.5f, 19.5f);
                break;
            case PhosphorGlyph.Lasso:
                g.DrawEllipse(pen, 4, 4, 16, 10);
                g.DrawBezier(pen, new PointF(7.5f, 13), new PointF(5.5f, 15.5f), new PointF(8.5f, 17.5f), new PointF(7, 21));
                break;
            case PhosphorGlyph.Record:
                g.DrawEllipse(pen, 3, 3, 18, 18);
                using (var dot = new SolidBrush(ink)) g.FillEllipse(dot, 8.5f, 8.5f, 7, 7);
                break;
            case PhosphorGlyph.Whisper:
                using (var mic = PhosphorTheme.CreateRoundedRect(new RectangleF(9, 3, 6, 12), 3)) g.DrawPath(pen, mic);
                g.DrawArc(pen, 5, 4, 14, 14, 0, 180);
                g.DrawLine(pen, 12, 18, 12, 21);
                break;
            case PhosphorGlyph.Snippet:
                g.DrawLines(pen, new[] { new PointF(9, 7), new PointF(4, 12), new PointF(9, 17) });
                g.DrawLines(pen, new[] { new PointF(15, 7), new PointF(20, 12), new PointF(15, 17) });
                break;
            case PhosphorGlyph.Link:
                foreach (var (cx, cy) in new[] { (9.2f, 14.8f), (14.8f, 9.2f) })
                {
                    var st = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-45);
                    using (var pill = PhosphorTheme.CreateRoundedRect(new RectangleF(-5.5f, -2.8f, 11, 5.6f), 2.8f)) g.DrawPath(pen, pill);
                    g.Restore(st);
                }
                break;
            case PhosphorGlyph.Rect:
                using (var dash = new Pen(ink, stroke / u) { DashPattern = new[] { 2f, 1.6f } })
                    g.DrawRectangle(dash, 4, 6, 16, 12);
                break;
            case PhosphorGlyph.Pen:
                g.DrawPolygon(pen, new[] { new PointF(4, 20), new PointF(5, 15.5f), new PointF(15.5f, 5), new PointF(19, 8.5f), new PointF(8.5f, 19) });
                g.DrawLine(pen, 13.5f, 7, 17, 10.5f);
                break;
            case PhosphorGlyph.Arrow:
                g.DrawLine(pen, 5, 19, 19, 5);
                g.DrawLines(pen, new[] { new PointF(11, 5), new PointF(19, 5), new PointF(19, 13) });
                break;
            case PhosphorGlyph.Box:
                using (var sq = PhosphorTheme.CreateRoundedRect(new RectangleF(4, 4, 16, 16), 3)) g.DrawPath(pen, sq);
                break;
            case PhosphorGlyph.Holodex:
                using (var body = PhosphorTheme.CreateRoundedRect(new RectangleF(3.5f, 5, 17, 14), 2.5f)) g.DrawPath(pen, body);
                g.DrawLine(pen, 3.5f, 10, 20.5f, 10);
                g.DrawLine(pen, 10, 14, 14, 14);
                break;
            case PhosphorGlyph.Wiz:
                g.DrawPolygon(pen, new[] { new PointF(10, 3), new PointF(12, 9), new PointF(18, 11), new PointF(12, 13), new PointF(10, 19), new PointF(8, 13), new PointF(2, 11), new PointF(8, 9) });
                g.DrawLine(pen, 18, 16, 18, 21);
                g.DrawLine(pen, 15.5f, 18.5f, 20.5f, 18.5f);
                break;
            case PhosphorGlyph.AInbox:
                using (var tray = PhosphorTheme.CreateRoundedRect(new RectangleF(3.5f, 5, 17, 14), 2.5f)) g.DrawPath(pen, tray);
                g.DrawLines(pen, new[] { new PointF(3.5f, 13), new PointF(8, 13), new PointF(9.5f, 15.5f), new PointF(14.5f, 15.5f), new PointF(16, 13), new PointF(20.5f, 13) });
                break;
            case PhosphorGlyph.Codex:
                g.DrawPolygon(pen, new[] { new PointF(6, 3), new PointF(18, 3), new PointF(18, 21), new PointF(12, 16.5f), new PointF(6, 21) });
                break;
            default:
                g.DrawLine(pen, 12, 5, 12, 19);
                g.DrawLine(pen, 5, 12, 19, 12);
                break;
        }
        g.Restore(state);
    }
}

/// <summary>
/// A composer surface: a filled rounded panel that hosts the composer stack in <see cref="Content"/> and brightens
/// its border while focus is inside. <see cref="HeightFor"/> lets the owning widget size it to what it holds.
/// </summary>
internal class ComposerSurface : Control
{
    protected const float InsetPx = 5f; // room for an outer ring (the TraceBeam) inside the control bounds
    private const float PadXPx = 10f;
    private const float PadYPx = 8f;

    public ComposerSurface()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Content = new Panel { BackColor = Color.Transparent, Name = "Composer.Content" };
        Controls.Add(Content);
    }

    /// <summary>The composer stack (children dock Top inside it).</summary>
    public Panel Content { get; }

    /// <summary>Control height that fits <paramref name="contentHeight"/> of composer inside the insets.</summary>
    public int HeightFor(int contentHeight) => contentHeight + (int)Math.Round(2 * (InsetPx + PadYPx) * PhosphorComposerScale.Of(this));

    protected RectangleF WellRect
    {
        get
        {
            float inset = InsetPx * PhosphorPaint.Scale(this);
            return new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        float s = PhosphorPaint.Scale(this);
        int x = (int)Math.Round((InsetPx + PadXPx) * s), y = (int)Math.Round((InsetPx + PadYPx) * s);
        Content.SetBounds(x, y, Math.Max(0, Width - 2 * x), Math.Max(0, Height - 2 * y));
    }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = PhosphorPaint.Scale(this);
        var well = WellRect;
        PhosphorPaint.FillRounded(g, well, 12f * s, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, well, 12f * s, ContainsFocus ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
    }
}

/// <summary>
/// The composer's text box, borderless on the composer surface. Its prompt (often a suggestion Tab takes) stays
/// visible while the box is empty, focused or not, and in offscreen renders; the stock placeholder hides on focus.
/// </summary>
internal sealed class PromptTextBox : TextBox
{
    private const int WmPaint = 0x000F;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;
    private string _prompt = "";

    public PromptTextBox()
    {
        Multiline = true;
        BorderStyle = BorderStyle.None;
        ScrollBars = ScrollBars.None;
        BackColor = PhosphorTheme.Surface2;
        ForeColor = PhosphorTheme.Bone;
        Font = PhosphorFonts.Body(PhosphorTheme.TextBody);
    }

    public string Prompt
    {
        get => _prompt;
        set { if (_prompt == value) return; _prompt = value; Invalidate(); }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Filter on the message first: TextLength sends WM_GETTEXTLENGTH, which comes back through here.
        if (m.Msg is not (WmPaint or WmPrint or WmPrintClient) || _prompt.Length == 0 || TextLength > 0) return;
        if (m.Msg == WmPaint)
        {
            using var g = Graphics.FromHwnd(Handle);
            DrawPrompt(g);
        }
        else if (m.WParam != IntPtr.Zero)
        {
            using var g = Graphics.FromHdc(m.WParam);
            DrawPrompt(g);
        }
    }

    private void DrawPrompt(Graphics g) => TextRenderer.DrawText(g, _prompt, Font, new Rectangle(1, 0, ClientSize.Width - 2, ClientSize.Height),
        PhosphorTheme.Ghost, BackColor, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
}

/// <summary>An icon-only tool, 44px square, named for its PhosphorTips entry. A toggle shows its on state in patina.</summary>
internal sealed class IconToolButton : Control
{
    private readonly Action<Graphics, RectangleF, Color, float> _drawIcon;
    private bool _hover;
    private bool _selected;

    public IconToolButton(string name, PhosphorGlyph glyph, string accessibleName)
        : this(name, (g, r, c, w) => PhosphorIcons.Draw(g, glyph, r, c, w), accessibleName) { }

    public IconToolButton(string name, Action<Graphics, RectangleF, Color, float> drawIcon, string accessibleName)
    {
        _drawIcon = drawIcon;
        Name = name;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(44, 44);
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>On state for toggles (attach screen, the active snip tool).</summary>
    public bool Selected
    {
        get => _selected;
        set { _selected = value; AccessibleRole = AccessibleRole.CheckButton; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(2.5f * s, 2.5f * s, Width - 5f * s, Height - 5f * s);
        float r = 10f * s;
        if (_selected)
        {
            PhosphorPaint.FillRounded(g, rect, r, Color.FromArgb(28, PhosphorTheme.Patina));
            PhosphorPaint.StrokeRounded(g, rect, r, Color.FromArgb(150, PhosphorTheme.Patina));
        }
        else if (_hover)
        {
            PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface);
            PhosphorPaint.StrokeRounded(g, rect, r, PhosphorTheme.Line2);
        }
        if (Focused) PhosphorPaint.StrokeRounded(g, rect, r, PhosphorTheme.Patina, 2f * s);
        float icon = 18f * s;
        Color ink = _selected ? PhosphorTheme.Patina : _hover ? PhosphorTheme.Bone : PhosphorTheme.Dim;
        _drawIcon(g, new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon), ink, 1.7f * s);
    }
}

/// <summary>One choice in a suggestion chip's menu.</summary>
internal sealed record ChipChoice(string Text, string? Note, bool Selected, Action Pick, string? Tip = null);

/// <summary>
/// Something the system proposed (a pod, a model, a spell type), as one chip: an optional arrow, LABEL · DETAIL,
/// a confidence dot and a caret. The dot lights in <see cref="Accent"/> when the system is sure; a hand-picked or
/// unsure value shows a ghost dot. Clicking opens the choices.
/// </summary>
internal class SuggestionChip : Control
{
    private bool _hover;

    public SuggestionChip()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        Size = new Size(170, 44);
    }

    public string Label { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool ShowArrow { get; set; } = true;
    public Color Accent { get; set; } = PhosphorTheme.Patina;
    public bool Confident { get; set; }
    /// <summary>Overrides the dot (for example ember when no model key is linked).</summary>
    public Color? DotColor { get; set; }

    private string Text2 => (Detail.Length > 0 ? $"{Label} · {Detail}" : Label).ToUpperInvariant();

    /// <summary>Sizes the chip to its text and repaints.</summary>
    public void Fit()
    {
        AccessibleName = Detail.Length > 0 ? $"{Label}, {Detail}" : Label;
        float s = PhosphorComposerScale.Of(this);
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        using var font = PhosphorFonts.Mono(10.5f * s);
        float text = PhosphorPaint.MeasureTracked(g, Text2, font, 1f * s);
        Width = (int)Math.Ceiling(text + (ShowArrow ? 88f : 66f) * s);
        Invalidate();
    }

    /// <summary>Opens the choices above or below the chip with the Phosphor menu renderer.</summary>
    public void ShowChoices(IEnumerable<ChipChoice> choices, bool above = true)
    {
        var menu = new ContextMenuStrip { Renderer = new PhosphorMenuRenderer(), ShowImageMargin = false };
        foreach (var c in choices)
        {
            var item = new ToolStripMenuItem(c.Text) { Checked = c.Selected, ToolTipText = c.Tip };
            if (!string.IsNullOrEmpty(c.Note)) item.ShortcutKeyDisplayString = c.Note;
            var pick = c.Pick;
            item.Click += (_, _) => pick();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        if (above) menu.Show(this, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        else menu.Show(this, new Point(0, Height), ToolStripDropDownDirection.BelowRight);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float r = PhosphorTheme.RadiusButton * s;
        PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), Math.Max(1f, r - 2f), Color.FromArgb(160, PhosphorTheme.Patina));

        float cy = Height / 2f;
        float x = 14f * s;
        if (ShowArrow)
        {
            using var pen = new Pen(Accent, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, x, cy, x + 12f * s, cy);
            g.DrawLines(pen, new[] { new PointF(x + 7.5f * s, cy - 4.5f * s), new PointF(x + 12f * s, cy), new PointF(x + 7.5f * s, cy + 4.5f * s) });
            x += 22f * s;
        }

        using (var font = PhosphorFonts.Mono(10.5f * s))
        using (var ink = new SolidBrush(_hover ? PhosphorTheme.Bone : PhosphorTheme.Cream))
            x += PhosphorPaint.DrawTracked(g, Text2, font, ink, x, cy - font.GetHeight(g) / 2f, 1f * s);

        float dot = 6f * s;
        x += 8f * s;
        using (var b = new SolidBrush(DotColor ?? (Confident ? Accent : PhosphorTheme.Ghost))) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);

        float cx = Width - 16f * s;
        using var caret = new Pen(PhosphorTheme.Dim, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(caret, new[] { new PointF(cx - 4f * s, cy + 2f * s), new PointF(cx, cy - 2f * s), new PointF(cx + 4f * s, cy + 2f * s) });
    }
}

/// <summary>
/// The model as a suggestion chip, the one place a surface shows which model answers, where it runs and whether it
/// is ready (the dot: patina ready, ember when no key is linked). Replaces the model dropdown, the runtime badge and
/// the RUNS ON line. By default it reads and writes the shared <see cref="ArkStationClient.SelectedModel"/>.
/// </summary>
internal sealed class ModelChip : SuggestionChip
{
    private readonly ArkStationClient _ark;
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly IReadOnlyList<string>? _fixedModels;
    private List<string> _models = new();
    private string? _runtime;
    private string? _lastLabel;

    /// <summary>Raised when the model the chip names changes (owners relabel their primary from it).</summary>
    public event EventHandler? ModelChanged;

    public ModelChip(ArkStationClient ark, IReadOnlyList<string>? fixedModels = null, Func<string>? get = null, Action<string>? set = null)
    {
        _ark = ark;
        _fixedModels = fixedModels;
        _get = get ?? (() => _ark.SelectedModel);
        _set = set ?? (m => _ark.SelectedModel = m);
        ShowArrow = false;
        Accent = PhosphorTheme.Patina;
        Click += (_, _) => ShowChoices(_models.Select(m => new ChipChoice(ShortName(m), Host(m), string.Equals(m, _get(), StringComparison.OrdinalIgnoreCase), () => { _set(m); _runtime = null; Sync(); }, m)));
        _models.Add(_get());
        Sync();
    }

    /// <summary>Reloads the model list (gateway models when reachable, unless the list is fixed) and the ready state.</summary>
    public async Task RefreshAsync()
    {
        var models = (_fixedModels ?? await _ark.GetAvailableModelsAsync()).ToList();
        // ark/ models only answer through the ArkStation gateway; offer them only while it is up.
        if (!_ark.LastGatewayOnline) models.RemoveAll(m => m.StartsWith("ark/", StringComparison.OrdinalIgnoreCase));
        if (!models.Contains(_get(), StringComparer.OrdinalIgnoreCase)) models.Insert(0, _get());
        _models = models;
        Sync();
    }

    /// <summary>After a run, the runtime that actually answered (for example ANTIGRAVITY CLI) replaces the guess.</summary>
    public void ShowRuntime(string runtime)
    {
        // Runtimes come back as ids like "ark-model-gateway (nvidia/...)"; the chip names the host.
        string r = runtime.ToLowerInvariant();
        _runtime = r.Contains("gemini") ? "Gemini"
            : r.Contains("gateway") ? "Ark gateway"
            : r.Contains("nebius") || r.Contains("token") ? "Nebius"
            : runtime.Split(' ', '(')[0];
        Sync();
    }

    private void Sync()
    {
        string model = _get();
        bool keyed = _ark.LastGatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey());
        Label = ShortName(model);
        Detail = _runtime ?? Host(model);
        Confident = keyed;
        DotColor = keyed ? null : PhosphorTheme.Ember;
        Fit();
        if (_lastLabel != model)
        {
            _lastLabel = model;
            ModelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string Host(string model) => model.StartsWith("ark/", StringComparison.OrdinalIgnoreCase) || (_ark.LastGatewayOnline && !ArkStationClient.IsNemotronModel(model))
        ? "Ark gateway"
        : "Nebius";

    /// <summary>"nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B" reads "Nemotron 3 Nano": vendor, sizes and tuning tags dropped.</summary>
    public static string ShortName(string model)
    {
        string name = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        if (name.StartsWith("NVIDIA-", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        var kept = new List<string>();
        foreach (string part in name.Split('-', '_'))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^(A?\d+(\.\d+)?[BbMm]|it|Instruct|Chat)$")) break;
            kept.Add(part);
        }
        return kept.Count > 0 ? string.Join(" ", kept) : name;
    }
}

/// <summary>A one-line summary with a kicker, an accent edge and Review/Close, standing in for detail it opens.</summary>
internal sealed class DisclosureRow : Control
{
    private bool _hover;
    private bool _expanded;

    public DisclosureRow()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Height = 44;
    }

    public string Kicker { get; set; } = "";
    public string Sentence { get; set; } = "";
    public bool Muted { get; set; }
    public Color Accent { get; set; } = PhosphorTheme.Patina;
    public string OpenWord { get; set; } = "REVIEW";

    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; AccessibleName = $"{(value ? "Close" : "Open")} {Kicker.ToLowerInvariant()}"; Invalidate(); }
    }

    public event EventHandler? Toggled;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnClick(EventArgs e) { base.OnClick(e); Toggled?.Invoke(this, EventArgs.Empty); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float r = 10f * s;
        if (_hover) PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover ? PhosphorTheme.Line2 : PhosphorTheme.Line);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), r - 2f, Color.FromArgb(160, PhosphorTheme.Patina));
        using (var edge = new SolidBrush(Muted ? PhosphorTheme.Line2 : Accent))
            g.FillRectangle(edge, 0.5f, 10f * s, 2f * s, Height - 20f * s);

        float cy = Height / 2f;
        float x = 14f * s;
        using (var mono = PhosphorFonts.Mono(9.5f * s))
        using (var ghost = new SolidBrush(PhosphorTheme.Ghost))
            x += PhosphorPaint.DrawTracked(g, Kicker, mono, ghost, x, cy - mono.GetHeight(g) / 2f, 1.1f * s) + 12f * s;

        string action = Expanded ? "CLOSE" : OpenWord;
        float actionW;
        using (var mono = PhosphorFonts.Mono(9.5f * s))
        using (var ink = new SolidBrush(_hover ? PhosphorTheme.Bone : PhosphorTheme.Dim))
        {
            actionW = PhosphorPaint.MeasureTracked(g, action, mono, 1.1f * s);
            float ax = Width - 30f * s - actionW;
            PhosphorPaint.DrawTracked(g, action, mono, ink, ax, cy - mono.GetHeight(g) / 2f, 1.1f * s);
            float cx = Width - 18f * s;
            using var caret = new Pen(_hover ? PhosphorTheme.Bone : PhosphorTheme.Dim, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float d = Expanded ? -1f : 1f;
            g.DrawLines(caret, new[] { new PointF(cx - 4f * s, cy - 2f * s * d), new PointF(cx, cy + 2f * s * d), new PointF(cx + 4f * s, cy - 2f * s * d) });
        }

        float tw = Width - 30f * s - actionW - 12f * s - x;
        using var body = PhosphorFonts.Body(12f * s);
        using var text = new SolidBrush(Muted ? PhosphorTheme.Ghost : PhosphorTheme.Bone);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(Sentence, body, text, new RectangleF(x, 0, Math.Max(0, tw), Height), fmt);
    }
}

/// <summary>
/// Scale for sizes the kit computes. Before a control has a window its sizes are logical 96-DPI pixels that the
/// form's DPI autoscale multiplies later, so they must not be pre-scaled; once it has a window they are device pixels.
/// </summary>
internal static class PhosphorComposerScale
{
    public static float Of(Control c) => c.IsHandleCreated ? PhosphorPaint.Scale(c) : 1f;
}

/// <summary>Sizes a widget to its docked children, keeping its top edge and staying on screen.</summary>
internal static class FitToContent
{
    /// <summary>
    /// Shows exactly the Top or Bottom docked children that are <paramref name="shown"/> (callers say, since Visible
    /// reads false while the form is hidden) and sets <paramref name="form"/>'s height to its padding plus theirs.
    /// </summary>
    public static void Apply(Form form, Func<Control, bool> shown)
    {
        int height = form.Padding.Vertical;
        foreach (Control c in form.Controls)
        {
            if (c.Dock is not (DockStyle.Top or DockStyle.Bottom)) continue;
            bool on = shown(c);
            c.Visible = on;
            if (on) height += c.Height;
        }
        var area = (form.Visible ? Screen.FromControl(form) : Screen.FromPoint(form.Location)).WorkingArea;
        height = Math.Min(height, area.Height - 24);
        if (height == form.Height) return;
        var top = form.Location;
        form.Height = height;
        if (form.Visible)
        {
            var work = Screen.FromControl(form).WorkingArea;
            form.Location = new Point(top.X, WidgetChrome.SafeClamp(top.Y, work.Top + 12, work.Bottom - form.Height - 12));
        }
        form.Invalidate();
    }
}
