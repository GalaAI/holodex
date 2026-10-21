using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>What a toast reports; sets the panel-edge color (cat-panel / sb-agent states).</summary>
internal enum ToastTone
{
    /// <summary>Neutral notice: grey edge (panels rest grey).</summary>
    Info,
    /// <summary>Done and verified: moss edge, unlit (Law 05).</summary>
    Success,
    /// <summary>Pending, gated or not done: ember edge.</summary>
    Warning,
    /// <summary>Failed or interrupted: rust edge.</summary>
    Error,
}

/// <summary>
/// Phosphor Shell toast, composed from PhosphorKit: surface panel with a state edge, ArkMark,
/// tracked mono eyebrow, heavy title, dim body. The window is clipped to the rounded panel (WidgetChrome.ClipToFrame) so no square corners show.
/// Shown without stealing focus at the bottom-right of the primary work area; fades out on its own;
/// click to dismiss.
/// </summary>
internal sealed class PhosphorToast : Form
{
    private const int WidthPx = 400;
    private const int TextLeft = 60;
    private const int PadRight = 20;
    private const int PadTop = 16;
    private const int PadBottom = 18;

    private static PhosphorToast? _current;

    // Typographic layout so the body starts on the same left edge as the title (GDI pads by default).
    private static readonly StringFormat BodyFormat = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.Word };

    private readonly string _eyebrow;
    private readonly string _title;
    private readonly string _body;
    private readonly ToastTone _tone;
    private readonly System.Windows.Forms.Timer _life = new();
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 30 };
    private bool _fadingOut;

    private PhosphorToast(string eyebrow, string title, string body, int durationMs, ToastTone tone)
    {
        _eyebrow = eyebrow;
        _title = title;
        _body = body;
        _tone = tone;

        PhosphorFonts.EnsureLoaded();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "Toast";
        Size = new Size(WidthPx, MeasureHeight());
        WidgetChrome.ClipToFrame(this);
        Opacity = 0.0;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        _rest = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        Location = new Point(_rest.X, _rest.Y + RisePx);

        Click += (_, _) => Close();

        _life.Interval = Math.Max(1200, durationMs);
        _life.Tick += (_, _) =>
        {
            _life.Stop();
            _fadingOut = true;
            _clock.Restart();
            _fade.Start();
        };
        // Clock-driven disclosure (dur-disclosure, ease-analog in, ease-out away), so timing holds under load.
        _fade.Tick += (_, _) =>
        {
            if (IsDisposed || Disposing) { _fade.Stop(); return; }
            float t =PhosphorMotion.ReducedMotion ? 1f : Math.Min(1f, _clock.ElapsedMilliseconds / (float)PhosphorMotion.DurDisclosure);
            float e = _fadingOut ? 1f - PhosphorMotion.EaseOut(t) : PhosphorMotion.EaseAnalog(t);
            Opacity = e;
            Location = new Point(_rest.X, _rest.Y + (int)Math.Round((1f - e) * RisePx));
            if (t < 1f) return;
            _fade.Stop();
            if (_fadingOut) Close(); else _life.Start();
        };
    }

    private const int RisePx = 8;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Point _rest;

    protected override bool ShowWithoutActivation => true;

    // A toast replaced or clicked mid-fade must take its timers with it; a timer left running set Opacity
    // on the disposed form about six times a second.
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _fade.Stop();
        _life.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade.Dispose();
            _life.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    /// <summary>Draws a toast offscreen without showing it (as-built captures, <see cref="SurfaceCapture"/>).</summary>
    public static Bitmap RenderPreview(string title, string body, string eyebrow, ToastTone tone)
    {
        using var toast = new PhosphorToast(eyebrow, title, body, 4200, tone) { Opacity = 1.0 };
        return SurfaceCapture.Render(toast);
    }

    public static void Show(string title, string body, string eyebrow = "OMNIWAND", int durationMs = 4200, ToastTone tone = ToastTone.Info)
    {
        void Create()
        {
            _current?.Close();
            var toast = new PhosphorToast(eyebrow, title, body, durationMs, tone);
            _current = toast;
            toast.FormClosed += (_, _) => { if (ReferenceEquals(_current, toast)) _current = null; toast.Dispose(); };
            toast.Show();
            toast._clock.Restart();
            toast._fade.Start();
        }

        if (UiContext != null) UiContext.Post(_ => Create(), null);
        else Create();
    }

    /// <summary>UI-thread context set at startup so toasts can be raised from any thread.</summary>
    public static System.Threading.SynchronizationContext? UiContext { get; set; }

    private Color EdgeColor => _tone switch
    {
        ToastTone.Success => PhosphorTheme.Moss,
        ToastTone.Warning => PhosphorTheme.Ember,
        ToastTone.Error => PhosphorTheme.Rust,
        _ => PhosphorTheme.Line2,
    };

    private int MeasureHeight()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var eyebrowFont = PhosphorFonts.Eyebrow();
        using var titleFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
        using var bodyFont = PhosphorFonts.Body(PhosphorTheme.TextBody);
        float bodyH = string.IsNullOrEmpty(_body)
            ? 0
            : g.MeasureString(_body, bodyFont, WidthPx - TextLeft - PadRight, BodyFormat).Height;
        float h = PadTop + eyebrowFont.GetHeight(g) + 4 + titleFont.GetHeight(g) + (bodyH > 0 ? 6 + bodyH : 0) + PadBottom;
        return Math.Max(84, (int)Math.Ceiling(h));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        PhosphorPaint.DrawPanel(g, new Rectangle(0, 0, Width, Height), PhosphorTheme.RadiusWidget * s, PhosphorTheme.Surface, PhosphorTheme.Line, EdgeColor, s);
        PhosphorTheme.DrawArkMark(g, new RectangleF(18 * s, PadTop * s, 28 * s, 28 * s));

        using var eyebrowFont = PhosphorFonts.Eyebrow();
        using var titleFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
        using var bodyFont = PhosphorFonts.Body(PhosphorTheme.TextBody);
        using var bone = new SolidBrush(PhosphorTheme.Bone);
        using var dim = new SolidBrush(PhosphorTheme.Dim);

        float x = TextLeft * s;
        float y = PadTop * s - 1;
        PhosphorPaint.DrawEyebrow(g, _eyebrow, PhosphorTheme.Ghost, x, y, s);
        y += eyebrowFont.GetHeight(g) + 4 * s;
        g.DrawString(_title, titleFont, bone, x - 1, y, PhosphorPaint.Typographic);
        y += titleFont.GetHeight(g) + 6 * s;
        if (!string.IsNullOrEmpty(_body))
        {
            g.DrawString(_body, bodyFont, dim, new RectangleF(x, y, Width - x - PadRight * s, Height - y - PadBottom * s), BodyFormat);
        }
    }
}
