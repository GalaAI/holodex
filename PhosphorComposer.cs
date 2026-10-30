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
    Rect, Pen, Arrow, Box, Holodex, Wiz, AInbox, Codex, Window, Vault,
    // ArkStation's own icons (lucide-react, as the web console's Holodex uses them), drawn from their path data.
    Chat, Inbox, BookOpen, Sparkles, WandSparkles, ExternalLink, Stats,
    // lucide Hammer: 4GE, as the studio's Build rail uses it.
    Hammer,

    // Answer-card actions (lucide Copy, RotateCcw, Square, ThumbsUp, ThumbsDown).

    Copy, Retry, Stop, ThumbUp, ThumbDown
}

internal static class PhosphorIcons
{
    /// <summary>
    /// Lucide 1.55.0 path data (24 viewBox, stroke 2, round caps and joins), copied from the web console's
    /// node_modules/lucide-react icons: MessageSquareText, Inbox, BookOpen, Sparkles, WandSparkles.
    /// </summary>
    private static readonly Dictionary<PhosphorGlyph, string[]> Lucide = new()
    {
        [PhosphorGlyph.Chat] = new[] { "M22 17a2 2 0 0 1-2 2H6.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 2 21.286V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2z", "M7 11h10", "M7 15h6", "M7 7h8" },
        [PhosphorGlyph.Inbox] = new[] { "M22 12h-6l-2 3h-4l-2-3H2", "M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" },
        [PhosphorGlyph.BookOpen] = new[] { "M12 5v16", "M20.001 19A2 2 0 0022 17V5a2 2 0 00-1.999-2L16 3.002A5 5 0 0012 5a5 5 0 00-4-2H4a2 2 0 00-2 2v12a2 2 0 001.999 2H8a5 5 0 014 2 5 5 0 014-2z" },
        [PhosphorGlyph.Sparkles] = new[] { "M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z", "M20 2v4", "M22 4h-4", "M6 20a2 2 0 1 1-4 0a2 2 0 1 1 4 0" },
        // lucide chart-no-axes-column (three bars), for the Stats tab.
        [PhosphorGlyph.Stats] = new[] { "M18 20V10", "M12 20V4", "M6 20v-6" },
        // lucide-react 1.65.0 hammer (forge/node_modules), for the 4GE entry and My projects.
        [PhosphorGlyph.Hammer] = new[] { "m15 12-9.373 9.373a1 1 0 0 1-3.001-3L12 9", "m18 15 4-4", "m21.5 11.5-1.914-1.914A2 2 0 0 1 19 8.172v-.344a2 2 0 0 0-.586-1.414l-1.657-1.657A6 6 0 0 0 12.516 3H9l1.243 1.243A6 6 0 0 1 12 8.485V10l2 2h1.172a2 2 0 0 1 1.414.586L18.5 14.5" },
        [PhosphorGlyph.ExternalLink] = new[] { "M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" },
        // Copy and Square are lucide rects (rx 2), written as the same rounded-rect paths.
        [PhosphorGlyph.Copy] = new[] { "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z", "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2" },
        [PhosphorGlyph.Retry] = new[] { "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5" },
        [PhosphorGlyph.Stop] = new[] { "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z" },
        [PhosphorGlyph.ThumbUp] = new[] { "M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z", "M7 10v12" },
        [PhosphorGlyph.ThumbDown] = new[] { "M9 18.12 10 14H4.17a2 2 0 0 1-1.92-2.56l2.33-8A2 2 0 0 1 6.5 2H20a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-2.76a2 2 0 0 0-1.79 1.11L12 22a3.13 3.13 0 0 1-3-3.88Z", "M17 14V2" },
        [PhosphorGlyph.WandSparkles] = new[] { "m21.64 3.64-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72", "m14 7 3 3", "M5 6v4", "M19 14v4", "M10 2v2", "M7 8H3", "M21 16h-4", "M11 3H9" },
    };

    private static readonly Dictionary<PhosphorGlyph, GraphicsPath[]> LucidePaths = new();

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
        if (Lucide.TryGetValue(glyph, out var data))
        {
            GraphicsPath[] paths;
            lock (LucidePaths)
            {
                if (!LucidePaths.TryGetValue(glyph, out paths!))
                    LucidePaths[glyph] = paths = data.Select(d => SvgPaths.Parse(d)).ToArray();
            }
            foreach (var path in paths) g.DrawPath(pen, path);
            g.Restore(state);
            return;
        }
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
            case PhosphorGlyph.Window:
                using (var frame = PhosphorTheme.CreateRoundedRect(new RectangleF(3, 4.5f, 18, 15), 2)) g.DrawPath(pen, frame);
                g.DrawLine(pen, 3, 9, 21, 9);
                break;
            case PhosphorGlyph.Vault:
                using (var body = PhosphorTheme.CreateRoundedRect(new RectangleF(4, 10, 16, 11), 2)) g.DrawPath(pen, body);
                g.DrawArc(pen, 7.5f, 3, 9, 12, 180, 180);
                g.DrawLine(pen, 12, 14, 12, 17);
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
    internal const float InsetPx = 5f; // room for an outer ring (the TraceBeam) inside the control bounds
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
    public int HeightFor(int contentHeight) => contentHeight + (int)Math.Round(2 * (InsetPx + PadYPx) * PhosphorPaint.Scale(this));

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

    /// <summary>A capability that is designed but not built: drawn dimmed, still focusable and tooltipped.</summary>
    public bool Planned { get; set; }

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
        float r = PhosphorTheme.RadiusBadge * s;
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
        Color ink = Planned ? Color.FromArgb(_hover ? 150 : 90, PhosphorTheme.Dim) : _selected ? PhosphorTheme.Patina : _hover ? PhosphorTheme.Bone : PhosphorTheme.Dim;
        _drawIcon(g, new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon), ink, 1.7f * s);
        if (Planned)
        {
            // A small dashed ring marks planned tools.
            using var dash = new Pen(Color.FromArgb(110, PhosphorTheme.Ghost), 1f) { DashPattern = new[] { 2f, 2f } };
            using var ring = PhosphorTheme.CreateRoundedRect(rect, r);
            g.DrawPath(dash, ring);
        }
    }
}

/// <summary>
/// One item of an ArkStation rail (packages/station-shell rail.css, collapsed): a 44px row with the lucide glyph at
/// 1.5 x 18px, idle in dim, hover in bone (color only, the geometry never moves), and the active item as the neutral
/// pill (surface-2 fill, line-2 inset, 14px corners, bone glyph). Keyboard focus is the patina outline. The tooltip
/// (PhosphorTips, by Name) is the rail's label, as the collapsed rail's AnimatedTooltip is.
/// </summary>
internal sealed class RailButton : Control
{
    private readonly PhosphorGlyph _glyph;
    private bool _hover;
    private bool _selected;

    public RailButton(string name, PhosphorGlyph glyph, string label)
    {
        _glyph = glyph;
        Name = name;
        Text = label;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(52, 44);
        AccessibleName = label;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
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
        // Half: geometry lands on pixel edges, so a shape centred in the control is centred on screen. The default
        // (None) draws everything half a pixel right and down, which read as the rail icons sitting off its axis.
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
        float r = PhosphorTheme.RadiusButton * s;
        if (_selected)
        {
            PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
            PhosphorPaint.StrokeRounded(g, rect, r, PhosphorTheme.Line2);
        }
        if (Focused) PhosphorPaint.StrokeRounded(g, rect, r, PhosphorTheme.Patina, 2f * s);
        float icon = 27f * s;
        Color ink = _selected || _hover ? PhosphorTheme.Bone : PhosphorTheme.Dim;
        PhosphorIcons.Draw(g, _glyph, new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon), ink, 2f * icon / 24f);
    }
}

/// <summary>
/// The rail's first item: the ArkMark at 32px, centred on the rail's axis like every item under it (the
/// station-shell Rail header carries the mark the same way). It names the place, so it is not a button.
/// </summary>
internal sealed class RailMark : Control
{
    public RailMark(string name)
    {
        Name = name;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        TabStop = false;
        Size = new Size(52, 52);
        AccessibleName = "Holodex";
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half; // on the rail's axis, as RailButton
        float s = PhosphorPaint.Scale(this);
        float d = 32f * s;
        PhosphorTheme.DrawArkMark(e.Graphics, new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d));
    }
}

/// <summary>
/// The profile button: a 32px circle holding the aperture emblem (the brand's profile picture, DESIGN.md), on the
/// surface fill with a line ring like the Holodex vitals avatar in drawer mode. The ring takes
/// <see cref="Status"/>, so whether Hivemind is live shows without opening the profile.
/// </summary>
internal sealed class AvatarButton : Control
{
    private bool _hover;
    private Color _status = PhosphorTheme.Line;

    public AvatarButton(string name, string accessibleName)
    {
        Name = name;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(36, 36);
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public Color Status
    {
        get => _status;
        set { _status = value; Invalidate(); }
    }

    /// <summary>On while the profile panel is open.</summary>
    public bool Open { get; set; }

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
        g.PixelOffsetMode = PixelOffsetMode.Half; // on the rail's axis, as RailButton
        float s = PhosphorPaint.Scale(this);
        // 32px like the Holodex vitals avatar in drawer mode, smaller only if the control is.
        float d = Math.Min(32f * s, Math.Min(Width, Height) - 4f * s);
        var circle = new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d);
        using (var fill = new SolidBrush(Open || _hover ? PhosphorTheme.Surface2 : PhosphorTheme.Surface)) g.FillEllipse(fill, circle);
        using (var ring = new Pen(_status, 1.5f * s)) g.DrawEllipse(ring, circle);
        if (Focused) using (var focus = new Pen(PhosphorTheme.Patina, 2f * s)) g.DrawEllipse(focus, RectangleF.Inflate(circle, 2f * s, 2f * s));
        float m = d * 0.62f;
        ArkEmblem.DrawAperture(g, new RectangleF(circle.X + (d - m) / 2f, circle.Y + (d - m) / 2f, m, m));
    }
}

/// <summary>One choice in a suggestion chip's menu.</summary>
/// <summary>
/// One option of a value picker (FluidSelect, CP-32): its label, a mono note on the right, whether it is the current
/// value, what picking it does, its tip, the accent <paramref name="Tone"/> its dot takes when lit (accent tokens only,
/// never ember or rust), and a rule under it (the "All" divider).
/// </summary>
internal sealed record ChipChoice(string Text, string? Note, bool Selected, Action Pick, string? Tip = null, Color? Tone = null, bool SeparatorAfter = false)
{
    /// <summary>A heading between groups of choices (it picks nothing).</summary>
    public static ChipChoice Header(string text) => new(text, null, false, null!);

    public bool IsHeader => Pick == null;
}

/// <summary>
/// Something the system proposed (a pod, a model, a spell type), as one chip: an optional arrow, LABEL · DETAIL,
/// a confidence dot and a chevron. The dot lights in <see cref="Accent"/> when the system is sure; a hand-picked or
/// unsure value shows a ghost dot. It is FluidSelect's trigger (CP-32): clicking opens the choices in
/// <see cref="FluidSelectDrop"/>, the chevron turns over in 120ms and the corner morphs from --radius-menu-closed to
/// --radius-menu-open on the disclosure beat.
/// </summary>
internal class SuggestionChip : Control
{
    private bool _hover;
    private bool _open;
    private long _openChangedAt = long.MinValue / 2;
    private bool _ticking;
    private DateTime _dropClosedAtUtc;

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
        float s = PhosphorPaint.Scale(this);
        using var g = PhosphorPaint.MeasureGraphics(s);
        // Measured in the face and size the chip paints in, so a label that fits is never cut with an ellipsis.
        using var font = PhosphorFonts.Mono(ChipTextPx);
        float text = PhosphorPaint.MeasureTracked(g, Text2, font, 1f * s);
        int want = (int)Math.Ceiling(text + Chrome(s));
        // A row can cap the chip (MaximumSize); the text then ends in an ellipsis inside it, chevron kept.
        Width = MaximumSize.Width > 0 ? Math.Min(want, MaximumSize.Width) : want;
        Invalidate();
    }

    /// <summary>The chip's text size: Fit measures and OnPaint draws at the same size.</summary>
    private const float ChipTextPx = 10.5f;

    /// <summary>Everything on the chip but its text: padding, the dot and the chevron.</summary>
    private float Chrome(float s) => (ShowArrow ? 88f : 66f) * s;

    /// <summary>The chip's text, cut with an ellipsis when the chip is narrower than the whole of it.</summary>
    private string FittedText(Graphics g, Font font, float s)
    {
        string text = Text2;
        float room = Width - Chrome(s);
        if (PhosphorPaint.MeasureTracked(g, text, font, 1f * s) <= room) return text;
        while (text.Length > 1 && PhosphorPaint.MeasureTracked(g, text + "…", font, 1f * s) > room) text = text[..^1];
        return text.TrimEnd(' ', '·') + "…";
    }

    /// <summary>
    /// Opens the choices in the FluidSelect drop, above the chip by preference (it flips below when there is no
    /// room). A click on the chip while the drop is open closes it: the outside click closes the drop first, so a
    /// Click that lands just after it does not open it again.
    /// </summary>
    public void ShowChoices(IEnumerable<ChipChoice> choices, bool above = true)
    {
        if (_open || DateTime.UtcNow - _dropClosedAtUtc < TimeSpan.FromMilliseconds(250)) return;
        PhosphorTips.Hide();
        var drop = FluidSelectDrop.Open(this, choices.ToList(), above);
        Drop = drop;
        SetOpen(true);
        drop.Closed += (_, _) =>
        {
            _dropClosedAtUtc = DateTime.UtcNow;
            Drop = null;
            SetOpen(false);
        };
    }

    /// <summary>The open drop, for the as-built capture.</summary>
    internal FluidSelectDrop? Drop { get; private set; }

    /// <summary>Whether the drop is open; the chevron and corner ease to the new state.</summary>
    public bool IsOpen => _open;

    private void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        _openChangedAt = SurfaceMotion.Now;
        if (!_ticking && !PhosphorMotion.ReducedMotion)
        {
            _ticking = true;
            SurfaceMotion.Frame += OnFrame;
        }
        Invalidate();
    }

    private void OnFrame()
    {
        if (IsDisposed || SurfaceMotion.Now - _openChangedAt > FluidSelectSpec.PanelMs)
        {
            _ticking = false;
            SurfaceMotion.Frame -= OnFrame;
        }
        if (!IsDisposed) Invalidate();
    }

    /// <summary>How open the trigger looks, 0 to 1, over <paramref name="ms"/> from the last change.</summary>
    private float Openness(int ms)
    {
        float p = PhosphorMotion.ReducedMotion ? 1f : Math.Clamp((SurfaceMotion.Now - _openChangedAt) / (float)ms, 0f, 1f);
        float e = PhosphorMotion.EaseDisclosure(p);
        return _open ? e : 1f - e;
    }

    /// <summary>Whether the foreground window is one of Omniwand's own.</summary>
    internal static bool OwnsForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out uint pid) != 0 && pid == (uint)Environment.ProcessId;

    /// <summary>One line per chip-menu close (why, and whether it was kept open) in %TEMP%\omniwand-menu.log.</summary>
    internal static void LogMenuClose(string chip, ToolStripDropDownCloseReason reason, bool kept)
    {
        try
        {
            var fg = GetForegroundWindow();
            var cls = new System.Text.StringBuilder(128);
            GetClassName(fg, cls, cls.Capacity);
            GetWindowThreadProcessId(fg, out uint pid);
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omniwand-menu.log"),
                $"{DateTime.Now:O} {chip} {reason}{(kept ? " KEPT-OPEN" : "")} foreground={cls} pid={pid}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

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
        float morph = Openness(FluidSelectSpec.PanelMs);
        float r = (FluidSelectSpec.RadiusClosed + (FluidSelectSpec.RadiusOpen - FluidSelectSpec.RadiusClosed) * morph) * s;
        PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover || _open ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
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

        using (var font = PhosphorFonts.Mono(ChipTextPx))
        using (var ink = new SolidBrush(_hover || _open ? PhosphorTheme.Bone : PhosphorTheme.Cream))
            x += PhosphorPaint.DrawTracked(g, FittedText(g, font, s), font, ink, x, cy - font.GetHeight(g) / 2f, 1f * s);

        float dot = 6f * s;
        x += 8f * s;
        using (var b = new SolidBrush(DotColor ?? (Confident ? Accent : PhosphorTheme.Ghost))) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);

        // ChevronDown, turning over (180 degrees) in 120ms while the drop is open.
        float cx = Width - 16f * s;
        using var caret = new Pen(_hover || _open ? PhosphorTheme.Bone : PhosphorTheme.Dim, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var turn = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(180f * Openness(FluidSelectSpec.ChevronMs));
        g.DrawLines(caret, new[] { new PointF(-4f * s, -2f * s), new PointF(0, 2f * s), new PointF(4f * s, -2f * s) });
        g.Restore(turn);
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

    /// <summary>Offers the chat models LM Studio and Ollama serve on this PC (true for ask pickers; Wiz's agent loop runs on Nemotron).</summary>
    public bool IncludeLocalModels { get; set; } = true;

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
        _ark.ArkApiStateChanged += OnArkApiStateChanged;
        // Signing in or out of Hivemind changes where a keyless ask goes, so the chip relabels then too.
        _ark.Hivemind.StateChanged += OnHivemindStateChanged;
        Sync();
    }

    private void OnHivemindStateChanged() => OnArkApiStateChanged(this, EventArgs.Empty);

    /// <summary>The Ark API came up or went down: the next ask takes the other route, so the chip stops naming the last one.</summary>
    private void OnArkApiStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        void Resync() { if (IsDisposed) return; _runtime = null; Sync(); }
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(Resync); }
        else Resync();
    }

    /// <summary>A route change before the chip had a handle could not be marshalled; read the current route now.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Sync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ark.ArkApiStateChanged -= OnArkApiStateChanged;
            _ark.Hivemind.StateChanged -= OnHivemindStateChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>Reloads the model list (gateway models when reachable, unless the list is fixed) and the ready state.</summary>
    public async Task RefreshAsync()
    {
        var models = (_fixedModels ?? await _ark.GetAvailableModelsAsync(IncludeLocalModels)).ToList();
        // ark/ models only answer through the ArkStation gateway; offer them only while it is up.
        if (!_ark.LastGatewayOnline) models.RemoveAll(m => m.StartsWith("ark/", StringComparison.OrdinalIgnoreCase));
        if (!models.Contains(_get(), StringComparer.OrdinalIgnoreCase)) models.Insert(0, _get());
        _models = models;
        Sync();
    }

    /// <summary>After a run, the runtime that actually answered (for example ANTIGRAVITY CLI) replaces the guess.</summary>
    public void ShowRuntime(string runtime)
    {
        // Runtimes come back as route ids ("ark-api", "nebius-direct", "hivemind", "none") or CLI names; the chip names the host.
        string r = runtime.ToLowerInvariant();
        _runtime = r.Contains("gemini") ? "Gemini"
            : r == ArkStationClient.RouteArkApi ? "Ark API"
            : r == ArkStationClient.RouteHivemind ? "Hivemind"
            : r == ArkStationClient.RouteLocal ? _ark.LocalProviderLabel(_get()) ?? "This PC"
            : r == ArkStationClient.RouteNone ? "Signed out"
            : r.Contains("nebius") ? "Nebius direct"
            : runtime.Split(' ', '(')[0];
        Sync();
    }

    /// <summary>
    /// An agent that answers before the chip's model (Gala in the owner's Hivemind): the chip names it first and
    /// its model as the fallback, so the chip and the ask button name the same answerer. Null names the model.
    /// </summary>
    public void FrontedBy(string? agent)
    {
        if (_agent == agent) return;
        _agent = agent;
        Sync();
    }

    private string? _agent;

    private void Sync()
    {
        string model = _get();
        bool keyed = _ark.HasAnswerRoute;
        Label = _agent ?? ShortName(model);
        Detail = _agent != null ? $"Hivemind ({ShortName(model).Split(' ')[0]} fallback)" : _runtime ?? Host(model);
        Confident = keyed;
        DotColor = keyed ? null : PhosphorTheme.Ember;
        Fit();
        if (_lastLabel != model)
        {
            _lastLabel = model;
            ModelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Where the next ask with this model goes: the Ark API, Token Factory direct, the user's Hivemind, or nowhere yet.</summary>
    private string Host(string model) => _ark.NextTextRoute(model) switch
    {
        ArkStationClient.RouteArkApi => "Ark API",
        ArkStationClient.RouteNebiusDirect => "Nebius direct",
        ArkStationClient.RouteHivemind => "Hivemind",
        ArkStationClient.RouteLocal => _ark.LocalProviderLabel(model) ?? "This PC",
        _ => "Signed out"
    };

    /// <summary>"nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B" reads "Nemotron 3 Nano": vendor, sizes and tuning tags dropped.
    /// A local picker id names only its model ("local:lmstudio:qwen/qwen3-8b" reads "qwen3").</summary>
    public static string ShortName(string model)
    {
        if (ArkStationClient.TryParseLocalModel(model, out _, out string localModel)) model = localModel;
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
        float r = PhosphorTheme.RadiusBadge * s;
        if (_hover) PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover ? PhosphorTheme.Line2 : PhosphorTheme.Line);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), r - 2f, Color.FromArgb(160, PhosphorTheme.Patina));
        using (var edge = new SolidBrush(Muted ? PhosphorTheme.Line2 : Accent))
            g.FillRectangle(edge, 0.5f, 10f * s, 2f * s, Height - 20f * s);

        float cy = Height / 2f;
        float x = 14f * s;
        using (var mono = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx))
        using (var ghost = new SolidBrush(PhosphorTheme.Ghost))
            x += PhosphorPaint.DrawTracked(g, Kicker, mono, ghost, x, cy - mono.GetHeight(g) / 2f, 1.1f * s) + 12f * s;

        string action = Expanded ? "CLOSE" : OpenWord;
        float actionW;
        using (var mono = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx))
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
        using var body = PhosphorFonts.Body(12f);
        using var text = new SolidBrush(Muted ? PhosphorTheme.Ghost : PhosphorTheme.Bone);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(Sentence, body, text, new RectangleF(x, 0, Math.Max(0, tw), Height), fmt);
    }
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
        // On screen the height eases (an answer arriving, a disclosure opening); hidden, it is set at once.
        SurfaceMotion.Height(form, Math.Min(height, area.Height - 24));
    }
}
