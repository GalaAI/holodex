using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>The capture kinds AInbox accepts (Augment Inventory CP-27, AInboxUpload).</summary>
internal enum CaptureKind { File, Screen, Pick, Lasso, Record, Whisper, Snippet, Link, More }

internal sealed record CaptureKindInfo(CaptureKind Kind, string Label, bool Planned);

internal static class CaptureKinds
{
    /// <summary>All eight, in the order the chips show them. Record and Whisper are not built yet.</summary>
    public static readonly CaptureKindInfo[] All =
    {
        new(CaptureKind.File, "FILE", false),
        new(CaptureKind.Screen, "SCREEN", false),
        new(CaptureKind.Pick, "PICK", false),
        new(CaptureKind.Lasso, "LASSO", false),
        new(CaptureKind.Record, "RECORD", true),
        new(CaptureKind.Whisper, "WHISPER", true),
        new(CaptureKind.Snippet, "SNIPPET", false),
        new(CaptureKind.Link, "LINK", false),
    };

    /// <summary>The four kinds a drag can carry fan out in the well; "+4" stands for the rest.</summary>
    public static readonly CaptureKind[] Fan = { CaptureKind.File, CaptureKind.Screen, CaptureKind.Snippet, CaptureKind.Link, CaptureKind.More };

    public static string Name(CaptureKind k) => k switch
    {
        CaptureKind.File => "File",
        CaptureKind.Screen => "Screen",
        CaptureKind.Pick => "Pick",
        CaptureKind.Lasso => "Lasso",
        CaptureKind.Record => "Record",
        CaptureKind.Whisper => "Whisper",
        CaptureKind.Snippet => "Snippet",
        CaptureKind.Link => "Link",
        _ => "More"
    };

    /// <summary>Which kind a drag carries, so its tile lifts out of the fan.</summary>
    public static CaptureKind FromDrag(IDataObject? data)
    {
        if (data == null) return CaptureKind.File;
        if (data.GetDataPresent(DataFormats.FileDrop)) return CaptureKind.File;
        if (data.GetDataPresent(DataFormats.Bitmap)) return CaptureKind.Screen;
        if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string t)
        {
            string s = t.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? CaptureKind.Link
                : CaptureKind.Snippet;
        }
        return CaptureKind.File;
    }

    /// <summary>
    /// Stroke icons on a 24-unit grid, drawn into <paramref name="box"/>. They follow the canvas
    /// glyphs (Omniwand UI, AInbox upload board) with GDI shapes in place of SVG paths.
    /// </summary>
    public static void DrawIcon(Graphics g, CaptureKind kind, RectangleF box, Color ink, float stroke)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        float u = box.Width / 24f;
        g.ScaleTransform(u, u);
        using var pen = new Pen(ink, stroke / u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case CaptureKind.File:
                g.DrawLines(pen, new[] { new PointF(14, 3), new PointF(6.5f, 3), new PointF(5, 4.5f), new PointF(5, 19.5f), new PointF(6.5f, 21), new PointF(17.5f, 21), new PointF(19, 19.5f), new PointF(19, 8), new PointF(14, 3) });
                g.DrawLines(pen, new[] { new PointF(14, 3), new PointF(14, 8), new PointF(19, 8) });
                break;
            case CaptureKind.Screen:
                g.DrawLines(pen, new[] { new PointF(4, 8), new PointF(4, 4), new PointF(8, 4) });
                g.DrawLines(pen, new[] { new PointF(16, 4), new PointF(20, 4), new PointF(20, 8) });
                g.DrawLines(pen, new[] { new PointF(20, 16), new PointF(20, 20), new PointF(16, 20) });
                g.DrawLines(pen, new[] { new PointF(8, 20), new PointF(4, 20), new PointF(4, 16) });
                g.DrawEllipse(pen, 9, 9, 6, 6);
                break;
            case CaptureKind.Pick:
                g.DrawPolygon(pen, new[] { new PointF(5, 3), new PointF(11, 19), new PointF(13, 13), new PointF(19, 11) });
                g.DrawLine(pen, 14.5f, 14.5f, 19.5f, 19.5f);
                break;
            case CaptureKind.Lasso:
                g.DrawEllipse(pen, 4, 4, 16, 10);
                g.DrawBezier(pen, new PointF(7.5f, 13), new PointF(5.5f, 15.5f), new PointF(8.5f, 17.5f), new PointF(7, 21));
                break;
            case CaptureKind.Record:
                g.DrawEllipse(pen, 3, 3, 18, 18);
                using (var dot = new SolidBrush(ink)) g.FillEllipse(dot, 8.5f, 8.5f, 7, 7);
                break;
            case CaptureKind.Whisper:
                using (var mic = PhosphorTheme.CreateRoundedRect(new RectangleF(9, 3, 6, 12), 3)) g.DrawPath(pen, mic);
                g.DrawArc(pen, 5, 4, 14, 14, 0, 180);
                g.DrawLine(pen, 12, 18, 12, 21);
                break;
            case CaptureKind.Snippet:
                g.DrawLines(pen, new[] { new PointF(9, 7), new PointF(4, 12), new PointF(9, 17) });
                g.DrawLines(pen, new[] { new PointF(15, 7), new PointF(20, 12), new PointF(15, 17) });
                break;
            case CaptureKind.Link:
                foreach (var (cx, cy) in new[] { (9.2f, 14.8f), (14.8f, 9.2f) })
                {
                    var st = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-45);
                    using (var pill = PhosphorTheme.CreateRoundedRect(new RectangleF(-5.5f, -2.8f, 11, 5.6f), 2.8f)) g.DrawPath(pen, pill);
                    g.Restore(st);
                }
                break;
            default:
                g.DrawLine(pen, 12, 5, 12, 19);
                g.DrawLine(pen, 5, 12, 19, 12);
                break;
        }
        g.Restore(state);
    }
}

internal enum CaptureWellMode { Idle, DragOver, Captured }

/// <summary>
/// AInboxUpload CP-27, the capture well. A dashed well with a fan of capture-kind tiles. Dragging something
/// over spreads the fan on ease-spring (CP-36), lifts the kind it carries, and runs the Augment Inventory's
/// TraceBeam FX-10 around the well: a 2px ring 3px outside it, coral at 15% with one bright band (coral into
/// #f6dccd) orbiting every 2.2s, over a 6% coral fill. No name tag. Reduced motion keeps the fan still,
/// outlines the detected tile and holds a static coral outline.
/// </summary>
internal sealed class CaptureWellControl : Control
{
    private static readonly float[,] Idle = { { -52, 5, -12 }, { -26, 1, -5 }, { 0, 0, 0 }, { 26, 1, 5 }, { 52, 5, 12 } };
    private static readonly float[,] Open = { { -76, -7, -14 }, { -38, 2, -6 }, { 0, 3, 0 }, { 38, 2, 6 }, { 76, -2, 14 } };
    private static readonly Color Hot = Color.FromArgb(0xF6, 0xDC, 0xCD);
    private const float SpreadMs = 500f;
    private const float OrbitMs = 2200f;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CaptureWellMode _mode = CaptureWellMode.Idle;
    private CaptureKind _lifted = CaptureKind.File;
    private float _spreadFrom, _spreadTo, _spreadNow;
    private long _spreadStartMs;
    private bool _hover;
    private float? _frozenOrbit;

    public string Title { get; set; } = "Drop anything into AInbox";
    public string Body { get; set; } = "Files, the screen at the tip, picks, lassos, snippets or links.";
    public string Line { get; set; } = "OMNITEK // waiting for a capture";
    public Color LineColor { get; set; } = PhosphorTheme.Ghost;

    public event EventHandler? Activated;
    public event DragEventHandler? Dropped;

    public CaptureWellControl()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        AllowDrop = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        _tick.Tick += (_, _) => Step();
    }

    public CaptureWellMode Mode => _mode;

    /// <summary>The mode a drag returns to when it leaves: Idle, or Captured once something is in the well.</summary>
    public CaptureWellMode RestMode { get; set; } = CaptureWellMode.Idle;

    public void SetMode(CaptureWellMode mode, CaptureKind lifted = CaptureKind.File)
    {
        _mode = mode;
        _lifted = lifted;
        float target = mode == CaptureWellMode.DragOver ? 1f : 0f;
        if (PhosphorMotion.ReducedMotion) { _spreadNow = _spreadFrom = _spreadTo = 0f; }
        else if (Math.Abs(target - _spreadTo) > 0.001f)
        {
            _spreadFrom = _spreadNow;
            _spreadTo = target;
            _spreadStartMs = _clock.ElapsedMilliseconds;
        }
        if (mode == CaptureWellMode.DragOver || Math.Abs(_spreadNow - _spreadTo) > 0.001f) _tick.Start();
        Invalidate();
    }

    /// <summary>For the as-built capture: settle the fan and hold the beam at a fixed point of its orbit.</summary>
    public void FreezeForCapture(float orbitTurn)
    {
        _spreadNow = _spreadFrom = _spreadTo;
        _frozenOrbit = orbitTurn;
        _tick.Stop();
        Invalidate();
    }

    public void Unfreeze() => _frozenOrbit = null;

    private void Step()
    {
        float t = Math.Clamp((_clock.ElapsedMilliseconds - _spreadStartMs) / SpreadMs, 0f, 1f);
        _spreadNow = _spreadFrom + (_spreadTo - _spreadFrom) * PhosphorMotion.EaseSpring(t);
        if (t >= 1f) _spreadNow = _spreadTo;
        if (_mode != CaptureWellMode.DragOver && t >= 1f) _tick.Stop();
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap)))
        {
            e.Effect = DragDropEffects.Copy;
            SetMode(CaptureWellMode.DragOver, CaptureKinds.FromDrag(e.Data));
        }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (_mode == CaptureWellMode.DragOver) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        if (_mode == CaptureWellMode.DragOver) SetMode(RestMode);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        Dropped?.Invoke(this, e);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; Activated?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        float inset = 5f * s; // TraceBeam gap (3px) plus ring width (2px) sit inside the control bounds.
        var well = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
        float r = 12f * s;
        bool drag = _mode == CaptureWellMode.DragOver;

        if (drag) PhosphorPaint.FillRounded(g, well, r, PhosphorTheme.Surface2);
        else
        {
            using var path = PhosphorTheme.CreateRoundedRect(well, r);
            using var dash = new Pen(_hover || Focused ? PhosphorTheme.Ghost : PhosphorTheme.Line2, 1f) { DashPattern = new[] { 6f, 6f } };
            g.DrawPath(dash, path);
        }

        var fanCenter = new PointF(well.X + 116f * s, well.Y + well.Height / 2f);
        DrawFan(g, fanCenter, s, drag);

        // Copy, to the right of the fan.
        float tx = well.X + 236f * s, tw = well.Right - tx - 16f * s;
        float cy = well.Y + well.Height / 2f;
        using (var titleFont = PhosphorFonts.Ui(14f * s, heavy: true))
        using (var bodyFont = PhosphorFonts.Body(12f * s))
        using (var bone = new SolidBrush(PhosphorTheme.Bone))
        using (var dim = new SolidBrush(PhosphorTheme.Dim))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            string title = drag ? "Release to capture" : Title;
            string body = drag ? $"{CaptureKinds.Name(_lifted)} detected, so the {CaptureKinds.Name(_lifted)} tile lifts." : Body;
            g.DrawString(title, titleFont, bone, new RectangleF(tx, cy - 30f * s, tw, 20f * s), fmt);
            g.DrawString(body, bodyFont, dim, new RectangleF(tx, cy - 7f * s, tw, 18f * s), fmt);
        }
        using (var monoFont = PhosphorFonts.Mono(10f * s))
        using (var lineBrush = new SolidBrush(LineColor))
        {
            var st = g.Save();
            g.SetClip(new RectangleF(tx, cy + 13f * s, tw, 16f * s));
            PhosphorPaint.DrawTracked(g, Line.ToUpperInvariant(), monoFont, lineBrush, tx, cy + 14f * s, 1.2f * s);
            g.Restore(st);
        }

        if (drag) DrawTraceBeam(g, well, r, s);
        if (Focused && !drag)
        {
            var ring = RectangleF.Inflate(well, 2f * s, 2f * s);
            PhosphorPaint.StrokeRounded(g, ring, r + 2f * s, PhosphorTheme.Patina, 2f * s);
        }
    }

    private void DrawFan(Graphics g, PointF center, float s, bool drag)
    {
        bool reduced = PhosphorMotion.ReducedMotion;
        float spread = reduced ? 0f : _spreadNow;
        bool captured = _mode == CaptureWellMode.Captured;
        int liftedIndex = drag ? Array.IndexOf(CaptureKinds.Fan, _lifted) : -1;
        var order = Enumerable.Range(0, 5).OrderBy(i => i == liftedIndex ? 9 : i == 2 ? 3 : i is 1 or 3 ? 2 : 1).ToArray();
        float tile = 40f * s;
        foreach (int i in order)
        {
            var kind = CaptureKinds.Fan[i];
            bool lifted = i == liftedIndex;
            bool more = kind == CaptureKind.More;
            float dx = Lerp(Idle[i, 0], Open[i, 0], spread) * s;
            float dy = Lerp(Idle[i, 1], Open[i, 1], spread) * s;
            float rot = Lerp(Idle[i, 2], Open[i, 2], spread);
            float scale = lifted && !reduced ? Lerp(1f, 1.18f, spread) : Lerp(1f, 1.04f, spread);
            if (captured) scale *= 0.9f;

            var st = g.Save();
            g.TranslateTransform(center.X + dx, center.Y + dy);
            g.RotateTransform(rot);
            g.ScaleTransform(scale, scale);
            var rect = new RectangleF(-tile / 2, -tile / 2, tile, tile);
            float rr = 11f * s;
            using (var shadow = PhosphorTheme.CreateRoundedRect(new RectangleF(rect.X, rect.Y + (lifted ? 6f : 3f) * s, rect.Width, rect.Height), rr))
            using (var sb = new SolidBrush(Color.FromArgb(lifted ? 120 : 80, 0, 0, 0)))
                g.FillPath(sb, shadow);
            PhosphorPaint.FillRounded(g, rect, rr, lifted ? Color.FromArgb(0x1B, 0x1B, 0x1A) : PhosphorTheme.Surface2);
            Color border = lifted ? PhosphorTheme.Coral : more ? PhosphorTheme.Line : PhosphorTheme.Line2;
            PhosphorPaint.StrokeRounded(g, rect, rr, border, lifted ? 1.5f * s : 1f);
            if (lifted && reduced)
                PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, 3f * s, 3f * s), rr + 3f * s, PhosphorTheme.Coral, 2f * s);
            Color ink = lifted ? PhosphorTheme.Cream : more ? PhosphorTheme.Ghost : drag ? PhosphorTheme.Dim : PhosphorTheme.Cream;
            float icon = 18f * s;
            CaptureKinds.DrawIcon(g, kind, new RectangleF(-icon / 2, -icon / 2, icon, icon), ink, 1.7f * s);
            g.Restore(st);
        }
    }

    /// <summary>TraceBeam FX-10: the ring, the orbiting band, and the 6% fill, without the name tag.</summary>
    private void DrawTraceBeam(Graphics g, RectangleF well, float r, float s)
    {
        float gap = 3f * s, width = 2f * s;
        var ringRect = RectangleF.Inflate(well, gap + width / 2f, gap + width / 2f);
        float ringRadius = r + gap + width / 2f;
        Color coral = PhosphorTheme.Coral;

        using (var fill = PhosphorTheme.CreateRoundedRect(RectangleF.Inflate(well, gap, gap), r + gap))
        using (var fb = new SolidBrush(Color.FromArgb(15, coral)))
            g.FillPath(fb, fill);

        using var ring = PhosphorTheme.CreateRoundedRect(ringRect, ringRadius);
        if (PhosphorMotion.ReducedMotion && _frozenOrbit == null)
        {
            using var solid = new Pen(coral, width);
            g.DrawPath(solid, ring);
            return;
        }
        using (var faint = new Pen(Color.FromArgb(38, coral), width))
            g.DrawPath(faint, ring);

        var pts = Resample(ring, 2f * s, out float total);
        if (pts.Count < 2 || total <= 0) return;
        float turn = _frozenOrbit ?? (_clock.ElapsedMilliseconds % (long)OrbitMs) / OrbitMs;
        float head = turn * total;
        float arc = 0.2f * total;   // --tb-arc: 20%
        float tail = 0.04f * total; // fade back to the ring just past the hot point
        float step = total / pts.Count;
        for (int i = 0; i < pts.Count; i++)
        {
            float at = i * step;
            float behind = Wrap(head - at, total); // distance behind the head along the orbit
            float ahead = Wrap(at - head, total);
            Color c;
            if (behind <= arc)
            {
                float u = 1f - behind / arc; // 0 at the band's start, 1 at the hot point
                c = u < 0.8f ? Mix(Color.FromArgb(38, coral), coral, u / 0.8f) : Mix(coral, Hot, (u - 0.8f) / 0.2f);
            }
            else if (ahead <= tail) c = Mix(Hot, Color.FromArgb(38, coral), ahead / tail);
            else continue;
            using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            g.DrawLine(pen, a, b);
        }
    }

    private static List<PointF> Resample(GraphicsPath path, float spacing, out float total)
    {
        using var flat = (GraphicsPath)path.Clone();
        flat.Flatten(null, 0.2f);
        var src = flat.PathPoints;
        var list = new List<PointF>();
        total = 0;
        if (src.Length < 2) return list;
        var closed = src.Concat(new[] { src[0] }).ToArray();
        float carry = 0;
        for (int i = 0; i < closed.Length - 1; i++)
        {
            var a = closed[i];
            var b = closed[i + 1];
            float len = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            float d = carry;
            while (d < len)
            {
                float t = len == 0 ? 0 : d / len;
                list.Add(new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                d += spacing;
            }
            carry = d - len;
            total += len;
        }
        return list;
    }

    private static float Wrap(float v, float m) => ((v % m) + m) % m;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Stop(); _tick.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>One capture-kind chip: icon over its label, 44px or taller, with a tooltip by Name.</summary>
internal sealed class KindChip : Control
{
    private bool _hover;

    public KindChip(CaptureKindInfo info)
    {
        Info = info;
        Name = "AInbox.Kind." + CaptureKinds.Name(info.Kind);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = info.Planned ? Cursors.Default : Cursors.Hand;
        AccessibleName = CaptureKinds.Name(info.Kind) + (info.Planned ? " (planned)" : "");
        AccessibleRole = AccessibleRole.PushButton;
    }

    public CaptureKindInfo Info { get; }

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
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float r = PhosphorTheme.RadiusButton * s;
        bool live = !Info.Planned;
        PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, live && _hover ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f * s, -2f * s), r - 2f * s, PhosphorTheme.Patina, 2f * s);

        Color ink = live ? (_hover ? PhosphorTheme.Bone : PhosphorTheme.Cream) : PhosphorTheme.Ghost;
        float icon = 16f * s;
        CaptureKinds.DrawIcon(g, Info.Kind, new RectangleF((Width - icon) / 2f, Height / 2f - icon - 1f * s, icon, icon), ink, 1.7f * s);

        using var font = PhosphorFonts.Mono(9.5f * s);
        using var brush = new SolidBrush(live ? (_hover ? PhosphorTheme.Bone : PhosphorTheme.Dim) : PhosphorTheme.Ghost);
        float tracking = 0.9f * s;
        float w = PhosphorPaint.MeasureTracked(g, Info.Label, font, tracking);
        PhosphorPaint.DrawTracked(g, Info.Label, font, brush, (Width - w) / 2f, Height / 2f + 3f * s, tracking);
    }
}

/// <summary>The captured item: kind icon, name, meta and the Omnitek prediction, with a 44px remove button.</summary>
internal sealed class CapturedRow : Control
{
    private bool _hoverRemove;

    public CapturedRow()
    {
        Name = "AInbox.CapturedRow";
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    public CaptureKind Kind { get; set; } = CaptureKind.File;
    public string ItemName { get; set; } = "";
    public string Meta { get; set; } = "";
    public string Prediction { get; set; } = "";

    public event EventHandler? RemoveClicked;

    private RectangleF RemoveRect
    {
        get
        {
            float s = PhosphorPaint.Scale(this);
            float b = 44f * s;
            return new RectangleF(Width - b - 2f * s, (Height - b) / 2f, b, b);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = RemoveRect.Contains(e.Location);
        if (over != _hoverRemove) { _hoverRemove = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverRemove = false; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && RemoveRect.Contains(e.Location)) RemoveClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        PhosphorPaint.FillRounded(g, rect, 12f * s, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, 12f * s, PhosphorTheme.Line);

        float box = 32f * s, bx = 8f * s, by = (Height - box) / 2f;
        var boxRect = new RectangleF(bx, by, box, box);
        PhosphorPaint.FillRounded(g, boxRect, 9f * s, PhosphorTheme.Ground);
        PhosphorPaint.StrokeRounded(g, boxRect, 9f * s, PhosphorTheme.Line2);
        float icon = 15f * s;
        CaptureKinds.DrawIcon(g, Kind, new RectangleF(bx + (box - icon) / 2f, by + (box - icon) / 2f, icon, icon), PhosphorTheme.Cream, 1.7f * s);

        float tx = bx + box + 10f * s;
        float tw = RemoveRect.X - tx - 8f * s;
        using (var nameFont = PhosphorFonts.Ui(12f * s, heavy: true))
        using (var bone = new SolidBrush(PhosphorTheme.Bone))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(ItemName, nameFont, bone, new RectangleF(tx, Height / 2f - 17f * s, tw, 17f * s), fmt);

        using (var mono = PhosphorFonts.Mono(9.5f * s))
        using (var ghost = new SolidBrush(PhosphorTheme.Ghost))
        using (var patina = new SolidBrush(PhosphorTheme.Patina))
        {
            var st = g.Save();
            g.SetClip(new RectangleF(tx, Height / 2f + 1f * s, tw, 16f * s));
            float w = PhosphorPaint.DrawTracked(g, Meta.ToUpperInvariant() + " // ", mono, ghost, tx, Height / 2f + 3f * s, 0.8f * s);
            PhosphorPaint.DrawTracked(g, Prediction.ToUpperInvariant(), mono, patina, tx + w, Height / 2f + 3f * s, 0.8f * s);
            g.Restore(st);
        }

        var rr = RemoveRect;
        if (_hoverRemove) PhosphorPaint.FillRounded(g, rr, 10f * s, PhosphorTheme.Surface);
        using var pen = new Pen(_hoverRemove ? PhosphorTheme.Bone : PhosphorTheme.Dim, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = 5f * s;
        var mid = new PointF(rr.X + rr.Width / 2f, rr.Y + rr.Height / 2f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLine(pen, mid.X - c, mid.Y - c, mid.X + c, mid.Y + c);
        g.DrawLine(pen, mid.X + c, mid.Y - c, mid.X - c, mid.Y + c);
    }
}
