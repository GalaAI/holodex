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
internal enum CaptureKind { File, Screen, Window, Pick, Lasso, Record, Whisper, Snippet, Link, More }

internal sealed record CaptureKindInfo(CaptureKind Kind, string Label, bool Planned);

internal static class CaptureKinds
{
    /// <summary>All nine, in capture-bar order (design target "Capture tools"). Record and Whisper are planned: the bar shows them dimmed.</summary>
    public static readonly CaptureKindInfo[] All =
    {
        new(CaptureKind.File, "FILE", false),
        new(CaptureKind.Screen, "SCREEN", false),
        new(CaptureKind.Window, "WINDOW", false),
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
        CaptureKind.Window => "Window",
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
            return IsLink(t) ? CaptureKind.Link : CaptureKind.Snippet;
        }
        return CaptureKind.File;
    }

    public static bool IsLink(string text)
    {
        string s = text.Trim();
        return !s.Contains(' ') && !s.Contains('\n') &&
               (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    public static PhosphorGlyph Glyph(CaptureKind k) => k switch
    {
        CaptureKind.File => PhosphorGlyph.File,
        CaptureKind.Screen => PhosphorGlyph.Screen,
        CaptureKind.Window => PhosphorGlyph.Window,
        CaptureKind.Pick => PhosphorGlyph.Pick,
        CaptureKind.Lasso => PhosphorGlyph.Lasso,
        CaptureKind.Record => PhosphorGlyph.Record,
        CaptureKind.Whisper => PhosphorGlyph.Whisper,
        CaptureKind.Snippet => PhosphorGlyph.Snippet,
        CaptureKind.Link => PhosphorGlyph.Link,
        _ => PhosphorGlyph.Plus
    };

    public static void DrawIcon(Graphics g, CaptureKind kind, RectangleF box, Color ink, float stroke) =>
        PhosphorIcons.Draw(g, Glyph(kind), box, ink, stroke);
}

internal enum CaptureWellMode { Idle, DragOver, Captured }

/// <summary>
/// AInboxUpload CP-27 as the composer surface. At rest it is the shared <see cref="ComposerSurface"/> hosting the
/// composer (attachments, the intent box and the capture bar). While something is dragged over the widget the
/// content steps aside and the well takes the surface: the fan of capture-kind tiles spreads on ease-spring
/// (CP-36), the kind the drag carries lifts, and the Augment Inventory's TraceBeam FX-10 runs around it: a 2px
/// ring 3px outside, coral at 15% with one bright band (coral into #f6dccd) orbiting every 2.2s, over a 6% coral
/// fill. No name tag. Reduced motion keeps the fan still, outlines the detected tile and holds a static outline.
/// </summary>
internal sealed class CaptureWellControl : ComposerSurface
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
    private float? _frozenOrbit;

    public CaptureWellControl()
    {
        Content.Name = "AInbox.ComposerContent";
        _tick.Tick += (_, _) => Step();
    }

    public CaptureWellMode Mode => _mode;

    /// <summary>The mode a drag returns to when it leaves: Idle, or Captured once something is attached.</summary>
    public CaptureWellMode RestMode { get; set; } = CaptureWellMode.Idle;

    public void SetMode(CaptureWellMode mode, CaptureKind lifted = CaptureKind.File)
    {
        _mode = mode;
        _lifted = lifted;
        Content.Visible = mode != CaptureWellMode.DragOver;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_mode != CaptureWellMode.DragOver) { base.OnPaint(e); return; }
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var well = WellRect;
        float r = 12f * s;
        PhosphorPaint.FillRounded(g, well, r, PhosphorTheme.Surface2);

        var fanCenter = new PointF(well.X + well.Width / 2f, well.Y + well.Height * 0.4f);
        DrawFan(g, fanCenter, s);

        float ty = fanCenter.Y + 38f * s;
        using (var titleFont = PhosphorFonts.Ui(14f, heavy: true))
        using (var bodyFont = PhosphorFonts.Body(12f))
        using (var bone = new SolidBrush(PhosphorTheme.Bone))
        using (var dim = new SolidBrush(PhosphorTheme.Dim))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString("Release to capture", titleFont, bone, new RectangleF(well.X, ty, well.Width, 20f * s), fmt);
            g.DrawString($"{CaptureKinds.Name(_lifted)} detected, so the {CaptureKinds.Name(_lifted)} tile lifts.", bodyFont, dim, new RectangleF(well.X, ty + 22f * s, well.Width, 18f * s), fmt);
        }

        DrawTraceBeam(g, well, r, s);
    }

    private void DrawFan(Graphics g, PointF center, float s)
    {
        bool reduced = PhosphorMotion.ReducedMotion;
        float spread = reduced ? 0f : _spreadNow;
        int liftedIndex = Array.IndexOf(CaptureKinds.Fan, _lifted);
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

            var st = g.Save();
            g.TranslateTransform(center.X + dx, center.Y + dy);
            g.RotateTransform(rot);
            g.ScaleTransform(scale, scale);
            var rect = new RectangleF(-tile / 2, -tile / 2, tile, tile);
            float rr = 11f * s;
            using (var shadow = PhosphorTheme.CreateRoundedRect(new RectangleF(rect.X, rect.Y + (lifted ? 6f : 3f) * s, rect.Width, rect.Height), rr))
            using (var sb = new SolidBrush(Color.FromArgb(lifted ? 120 : 80, 0, 0, 0)))
                g.FillPath(sb, shadow);
            PhosphorPaint.FillRounded(g, rect, rr, lifted ? Color.FromArgb(0x1B, 0x1B, 0x1A) : PhosphorTheme.Surface);
            Color border = lifted ? PhosphorTheme.Coral : more ? PhosphorTheme.Line : PhosphorTheme.Line2;
            PhosphorPaint.StrokeRounded(g, rect, rr, border, lifted ? 1.5f * s : 1f);
            if (lifted && reduced)
                PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, 3f * s, 3f * s), rr + 3f * s, PhosphorTheme.Coral, 2f * s);
            Color ink = lifted ? PhosphorTheme.Cream : more ? PhosphorTheme.Ghost : PhosphorTheme.Dim;
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

/// <summary>
/// One attachment in the composer: kind icon, name, meta and, on the primary attachment, the Omnitek prediction
/// (the only place the classification shows), with a 44px remove button.
/// </summary>
internal sealed class CapturedRow : Control
{
    private bool _hoverRemove;

    public CapturedRow(string name)
    {
        Name = name;
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
        PhosphorPaint.FillRounded(g, rect, 10f * s, PhosphorTheme.Surface);
        PhosphorPaint.StrokeRounded(g, rect, 10f * s, PhosphorTheme.Line);

        float box = 32f * s, bx = 8f * s, by = (Height - box) / 2f;
        var boxRect = new RectangleF(bx, by, box, box);
        PhosphorPaint.FillRounded(g, boxRect, 9f * s, PhosphorTheme.Ground);
        PhosphorPaint.StrokeRounded(g, boxRect, 9f * s, PhosphorTheme.Line2);
        float icon = 15f * s;
        CaptureKinds.DrawIcon(g, Kind, new RectangleF(bx + (box - icon) / 2f, by + (box - icon) / 2f, icon, icon), PhosphorTheme.Cream, 1.7f * s);

        float tx = bx + box + 10f * s;
        float tw = RemoveRect.X - tx - 8f * s;
        using (var nameFont = PhosphorFonts.Ui(12f, heavy: true))
        using (var bone = new SolidBrush(PhosphorTheme.Bone))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(ItemName, nameFont, bone, new RectangleF(tx, Height / 2f - 17f * s, tw, 17f * s), fmt);

        using (var mono = PhosphorFonts.Mono(9.5f))
        using (var ghost = new SolidBrush(PhosphorTheme.Ghost))
        using (var patina = new SolidBrush(PhosphorTheme.Patina))
        {
            var st = g.Save();
            g.SetClip(new RectangleF(tx, Height / 2f + 1f * s, tw, 16f * s));
            string meta = Meta.ToUpperInvariant() + (Prediction.Length > 0 ? " // " : "");
            float w = PhosphorPaint.DrawTracked(g, meta, mono, ghost, tx, Height / 2f + 3f * s, 0.8f * s);
            if (Prediction.Length > 0) PhosphorPaint.DrawTracked(g, Prediction.ToUpperInvariant(), mono, patina, tx + w, Height / 2f + 3f * s, 0.8f * s);
            g.Restore(st);
        }

        var rr = RemoveRect;
        if (_hoverRemove) PhosphorPaint.FillRounded(g, rr, 10f * s, PhosphorTheme.Surface2);
        using var pen = new Pen(_hoverRemove ? PhosphorTheme.Bone : PhosphorTheme.Dim, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = 5f * s;
        var mid = new PointF(rr.X + rr.Width / 2f, rr.Y + rr.Height / 2f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLine(pen, mid.X - c, mid.Y - c, mid.X + c, mid.Y + c);
        g.DrawLine(pen, mid.X + c, mid.Y - c, mid.X - c, mid.Y + c);
    }
}
