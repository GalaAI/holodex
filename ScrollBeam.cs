using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The scroll indicator for every Holodex scroll region, after the Augment Inventory scroll beam
/// (packages/ui augment-inventory.html .scroll-beam, .sb-core, .sb-bloom) and the global scrollbar rule: no track, no
/// box, no arrows. The native bar is hidden without giving up scrolling; a cream light (patina on agent panels) sits on
/// the right edge in a <see cref="LanePx"/> lane and follows the scroll on the same frame. It fades in over 180 ms when
/// scrolling starts and out over 420 ms once 1 s passes with no wheel, drag or scroll message; at rest it is hidden.
/// Dragging the beam's lane scrolls. Reduced motion shows and hides it without a fade.
/// </summary>
internal sealed class ScrollBeam : IDisposable
{
    /// <summary>The lane the beam draws in, at the right edge; content stops short of it (unscaled px).</summary>
    public const int LanePx = 5;
    public const int IdleMs = 1000;
    public const int FadeInMs = 180;
    public const int FadeOutMs = 420;

    /// <summary>--beam-rgb: the Augment Inventory cream.</summary>
    public static readonly Color Cream = Color.FromArgb(228, 217, 192);

    private readonly ScrollableControl _host;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private readonly Stopwatch _since = new();
    private float _opacity;
    private float _fadeFrom;
    private bool _dragging;
    private int _dragOffset;

    public ScrollBeam(ScrollableControl host, Color? color = null)
    {
        _host = host;
        Color = color ?? Cream;
        _clock.Tick += (_, _) => Step();
        host.Paint += (_, e) => Draw(e.Graphics);
        host.Disposed += (_, _) => Dispose();
    }

    public Color Color { get; set; }

    /// <summary>0 hidden to 1 shown: the fade the next paint uses.</summary>
    public float Opacity => _opacity;

    /// <summary>The lane width at the host's DPI.</summary>
    public static int Lane(Control c) => (int)Math.Round(LanePx * PhosphorPaint.Scale(c));

    /// <summary>Shows the beam (scrolling just happened) and restarts the 1 s idle count.</summary>
    public void Wake()
    {
        _fadeFrom = _opacity;
        _since.Restart();
        if (PhosphorMotion.ReducedMotion) _opacity = 1f;
        _clock.Start();
        InvalidateLane();
    }

    /// <summary>
    /// One frame: hold while scrolling is recent, then fade out. Pure in time so a test can drive it with
    /// <paramref name="elapsedMs"/> since the last scroll.
    /// </summary>
    internal static float OpacityAt(double elapsedMs, float from, bool reducedMotion)
    {
        if (elapsedMs < IdleMs)
        {
            if (reducedMotion) return 1f;
            float t = (float)Math.Clamp(elapsedMs / FadeInMs, 0, 1);
            return from + (1f - from) * Ease(t);
        }
        if (reducedMotion) return 0f;
        float u = (float)Math.Clamp((elapsedMs - IdleMs) / FadeOutMs, 0, 1);
        return 1f - Ease(u);
    }

    /// <summary>cubic-bezier(.45,.05,.35,1) closely enough: a smootherstep.</summary>
    private static float Ease(float t) => t * t * t * (t * (6f * t - 15f) + 10f);

    private void Step()
    {
        float next = OpacityAt(_since.Elapsed.TotalMilliseconds, _fadeFrom, PhosphorMotion.ReducedMotion);
        if (Math.Abs(next - _opacity) > 0.002f)
        {
            _opacity = next;
            InvalidateLane();
        }
        if (_opacity <= 0f && _since.ElapsedMilliseconds >= IdleMs) _clock.Stop();
    }

    private bool Overflows => _host.DisplayRectangle.Height > _host.ClientSize.Height + 1;

    /// <summary>How far down the content is scrolled, 0 at the top to 1 at the bottom.</summary>
    internal float Ratio
    {
        get
        {
            int travel = _host.DisplayRectangle.Height - _host.ClientSize.Height;
            return travel <= 0 ? 0f : Math.Clamp(-_host.DisplayRectangle.Y / (float)travel, 0f, 1f);
        }
    }

    private Rectangle LaneRect => new(_host.ClientSize.Width - Lane(_host), 0, Lane(_host), _host.ClientSize.Height);

    private void InvalidateLane()
    {
        if (_host.IsHandleCreated && !_host.IsDisposed) _host.Invalidate(LaneRect, false);
    }

    /// <summary>Paints the beam into the lane: the core band on the edge and its bloom toward the content.</summary>
    internal void Draw(Graphics g)
    {
        if (_opacity <= 0.01f || !Overflows) return;
        float s = PhosphorPaint.Scale(_host);
        var lane = LaneRect;
        float h = lane.Height;
        float center = Ratio * h;

        var state = g.Save();
        g.SetClip(lane);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // .sb-bloom: a soft glow 120 px tall reaching in from the edge.
        float bloomH = 120f * s;
        var bloomRect = new RectangleF(lane.Left, center - bloomH / 2f, lane.Width, bloomH);
        using (var bloom = new LinearGradientBrush(new RectangleF(bloomRect.Left - 1, bloomRect.Top, bloomRect.Width + 2, bloomRect.Height),
            Color.FromArgb(0, Color), Color.FromArgb(Alpha(0.16f), Color), LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipX })
        using (var fadeEnds = new GraphicsPath())
        {
            fadeEnds.AddEllipse(bloomRect.Left, bloomRect.Top, bloomRect.Width * 2f, bloomRect.Height);
            g.SetClip(fadeEnds, CombineMode.Intersect);
            g.FillRectangle(bloom, bloomRect);
            g.SetClip(lane);
        }

        // .sb-core: a 144 px band on the edge, transparent at its ends and solid at its middle.
        float coreH = 144f * s, coreW = Math.Max(2f, 2.5f * s);
        var coreRect = new RectangleF(lane.Right - coreW, center - coreH / 2f, coreW, coreH);
        using (var core = new LinearGradientBrush(new RectangleF(coreRect.Left, coreRect.Top - 1, coreRect.Width, coreRect.Height + 2),
            Color.Transparent, Color.Transparent, LinearGradientMode.Vertical) { WrapMode = WrapMode.TileFlipY })
        {
            core.InterpolationColors = new ColorBlend(7)
            {
                Colors = new[] { 0f, 0.16f, 0.55f, 1f, 0.55f, 0.16f, 0f }.Select(a => Color.FromArgb(Alpha(a), Color)).ToArray(),
                Positions = new[] { 0f, 0.2f, 0.36f, 0.5f, 0.64f, 0.8f, 1f }
            };
            g.FillRectangle(core, coreRect);
        }
        g.Restore(state);
    }

    private int Alpha(float a) => (int)Math.Clamp(a * _opacity * 255f, 0, 255);

    /// <summary>
    /// The host's window procedure calls this first (<see cref="BeamFlowPanel"/>); true means the beam handled the
    /// message and the host must not pass it on. No subclassing, so closing the window never unhooks out of order.
    /// </summary>
    public bool PreProcess(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE:
                // The client area is the whole window: the native bar keeps its scroll state (wheel, SetScrollInfo,
                // AutoScroll) but gets no frame to draw in, so it shows nothing and reserves no width. The styles are
                // left alone; changing them makes AutoScroll show the bar again and relayout without end.
                m.Result = IntPtr.Zero;
                return true;
            case WM_LBUTTONDOWN when InLane(m.LParam) && Overflows:
                _dragging = true;
                int y = Y(m.LParam);
                float center = Ratio * _host.ClientSize.Height;
                // A press on the beam drags it from where it was taken; a press elsewhere in the lane jumps there.
                _dragOffset = Math.Abs(y - center) < 72f * PhosphorPaint.Scale(_host) ? (int)(y - center) : 0;
                _host.Capture = true;
                ScrollToLaneY(y);
                return true;
            case WM_MOUSEMOVE when _dragging:
                ScrollToLaneY(Y(m.LParam));
                return true;
            case WM_LBUTTONUP when _dragging:
            case WM_CAPTURECHANGED when _dragging:
                _dragging = false;
                _host.Capture = false;
                Wake();
                break;
        }
        return false;
    }

    /// <summary>The host calls this after its own handling: scrolling just happened, so the beam shows.</summary>
    public void PostProcess(in Message m)
    {
        if (m.Msg is WM_MOUSEWHEEL or WM_VSCROLL or WM_GESTURE) Wake();
    }

    private bool InLane(IntPtr lParam) => X(lParam) >= _host.ClientSize.Width - Lane(_host);

    private void ScrollToLaneY(int y)
    {
        int travel = _host.DisplayRectangle.Height - _host.ClientSize.Height;
        if (travel <= 0) return;
        float ratio = Math.Clamp((y - _dragOffset) / (float)Math.Max(1, _host.ClientSize.Height), 0f, 1f);
        _host.AutoScrollPosition = new Point(-_host.DisplayRectangle.X, (int)Math.Round(ratio * travel));
        Wake();
        _host.Update();
    }

    private static int X(IntPtr lParam) => unchecked((short)(long)lParam);
    private static int Y(IntPtr lParam) => unchecked((short)((long)lParam >> 16));

    public void Dispose() => _clock.Dispose();

    private const int WM_NCCALCSIZE = 0x0083, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_MOUSEWHEEL = 0x020A, WM_VSCROLL = 0x0115, WM_GESTURE = 0x0119, WM_CAPTURECHANGED = 0x0215;
}

/// <summary>A FlowLayoutPanel that scrolls with a <see cref="ScrollBeam"/> instead of the native bar.</summary>
internal class BeamFlowPanel : FlowLayoutPanel
{
    public BeamFlowPanel() => Beam = new ScrollBeam(this);

    public ScrollBeam Beam { get; }

    protected override void WndProc(ref Message m)
    {
        if (Beam.PreProcess(ref m)) return;
        base.WndProc(ref m);
        Beam.PostProcess(m);
    }
}
