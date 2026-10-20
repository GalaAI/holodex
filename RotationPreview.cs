using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Rotation submenu row with a live preview in the right column: the cursor runs a small figure-eight and
/// turns the way that <see cref="RotationMode"/> turns the real wand (Program.RenderLoop):
/// TruePathTrailer faces its direction of travel, SubtleBank holds the classic -22.5 degree angle and tilts
/// up to 28 degrees with horizontal speed, LockedAngle keeps the classic angle. Animates only while the
/// submenu is open.
/// </summary>
internal sealed class RotationPreviewItem : ToolStripMenuItem
{
    private const float StageW = 60f;
    private const float StageH = 24f;
    private const double PeriodSec = 2.6;
    private const double ClassicDeg = -22.5 - 90.0; // the wand's resting tip direction (up and a little left)
    private const double MaxBankDeg = 28.0;

    private static readonly System.Windows.Forms.Timer Clock = new() { Interval = 33 };
    private static int _openMenus;

    public RotationMode Mode { get; }

    public RotationPreviewItem(string text, RotationMode mode) : base(text)
    {
        Mode = mode;
        // Dropdown menus size and paint rows through the renderer, not item overrides, so the preview takes
        // the shortcut column: blank figure spaces reserve its width, and PhosphorMenuRenderer draws the
        // stage there instead of the text (DrawPreview).
        ShortcutKeyDisplayString = new string(' ', 11);
    }

    /// <summary>Runs the shared animation clock while <paramref name="parent"/>'s submenu is open.</summary>
    public static void Animate(ToolStripMenuItem parent)
    {
        void Tick(object? s, EventArgs e)
        {
            foreach (ToolStripItem child in parent.DropDownItems)
                if (child is RotationPreviewItem) child.Invalidate();
        }
        parent.DropDownOpened += (_, _) =>
        {
            Clock.Tick += Tick;
            if (_openMenus++ == 0) Clock.Start();
        };
        parent.DropDownClosed += (_, _) =>
        {
            Clock.Tick -= Tick;
            if (--_openMenus <= 0) { _openMenus = 0; Clock.Stop(); }
        };
    }

    private float Scale => (Owner?.DeviceDpi ?? 96) / 96f;

    /// <summary>Draws the animated stage, right-aligned and vertically centered in <paramref name="column"/>.</summary>
    public void DrawPreview(Graphics g, Rectangle column)
    {
        float s = Scale;
        float w = Math.Min(StageW * s, column.Width);
        var stage = new RectangleF(column.Right - w, (Height - StageH * s) / 2f, w, StageH * s);
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.SetClip(stage);

        double t = Environment.TickCount64 / 1000.0;
        // Fading trail of where the tip has been: the motion reads even in a still frame.
        for (int i = 7; i >= 1; i--)
        {
            var p = PathPoint(t - i * 0.05, stage, s);
            int alpha = (int)(70 * (1 - i / 8.0));
            using var dot = new SolidBrush(Color.FromArgb(alpha, PhosphorTheme.Dim));
            float r = 1.1f * s;
            g.FillEllipse(dot, p.X - r, p.Y - r, r * 2, r * 2);
        }

        var tip = PathPoint(t, stage, s);
        var ahead = PathPoint(t + 0.02, stage, s);
        var behind = PathPoint(t - 0.02, stage, s);
        double deg = Mode switch
        {
            // Faces its direction of travel, like the tethered trailer.
            RotationMode.TruePathTrailer => Math.Atan2(ahead.Y - behind.Y, ahead.X - behind.X) * 180.0 / Math.PI,
            // Classic angle, tilted by horizontal speed (cos(wt) is x speed over its maximum).
            RotationMode.SubtleBank => ClassicDeg + MaxBankDeg * Math.Cos(W * t),
            _ => ClassicDeg,
        };
        DrawCursor(g, tip, (float)deg, s);
        g.Restore(saved);
    }

    private static double W => 2 * Math.PI / PeriodSec;

    /// <summary>Figure-eight across the stage: x = A sin(wt), y = B sin(2wt).</summary>
    private static PointF PathPoint(double t, RectangleF stage, float s)
    {
        // Inset by the arrow's half length so it never crosses the stage edge.
        float ax = stage.Width / 2f - 9 * s;
        float ay = stage.Height / 2f - 7 * s;
        float x = stage.X + stage.Width / 2f + ax * (float)Math.Sin(W * t);
        float y = stage.Y + stage.Height / 2f + ay * (float)Math.Sin(2 * W * t);
        return new PointF(x, y);
    }

    /// <summary>A small wand arrow centered on <paramref name="at"/>, its tip pointing along <paramref name="deg"/>.</summary>
    private static void DrawCursor(Graphics g, PointF at, float deg, float s)
    {
        var state = g.Save();
        g.TranslateTransform(at.X, at.Y);
        g.RotateTransform(deg);
        g.ScaleTransform(s, s);
        // Tip at +x, notch behind: 13px long, centered on the origin.
        PointF[] arrow =
        {
            new(6.5f, 0f),
            new(-6.5f, -5.2f),
            new(-3.4f, 0f),
            new(-6.5f, 5.2f),
        };
        using (var fill = new SolidBrush(PhosphorTheme.Cream))
            g.FillPolygon(fill, arrow);
        using (var outline = new Pen(PhosphorTheme.Ground, 1.1f) { LineJoin = LineJoin.Round })
            g.DrawPolygon(outline, arrow);
        g.Restore(state);
    }
}
