using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Omniwand;

/// <summary>
/// Renders the Ark Omnitek emblem (ArkMark) from the approved path geometry: cube and Y stubs in
/// emblem cream, the 3/4-view globe in emblem green at 40% over the surface. Mirrors
/// packages/ui/src/brand/ArkMark.tsx. The paths use M, L, A and Z only, so a small parser suffices.
/// </summary>
internal static class ArkEmblem
{
    private static Color Cream => PhosphorTheme.EmblemCream;                                   // --ark-emblem-cream
    private static Color GlobeGreen => Color.FromArgb((int)Math.Round(255 * Math.Clamp(PhosphorTheme.EmblemGlobeOpacity, 0f, 1f)), PhosphorTheme.EmblemGlobe); // --ark-emblem-globe at --ark-emblem-globe-opacity

    private static GraphicsPath? _cube;
    private static GraphicsPath? _globe;
    private static GraphicsPath? _aperture16;
    private static GraphicsPath? _aperture32;

    /// <summary>
    /// Aperture favicon, untiled: the cream A with the green globe at 40%, as the untiled aperture emblem
    /// draws it. With the tile dropped its padding has no purpose, so the mark is fitted to its own bounds
    /// and centered in <paramref name="rect"/>. Uses the 16px drawing for small marks and the 32px drawing
    /// for larger ones, never the 512 artwork scaled. <paramref name="solidGlobe"/> draws the globe at full
    /// opacity for the 16px tray, where the 40% globe disappears into the taskbar (operator call, 2026-10-17).
    /// </summary>
    public static void DrawAperture(
        Graphics g,
        RectangleF rect,
        bool solidGlobe = false,
        Color? globeColorOverride = null,
        bool asCursor = false,
        bool bloomGlobe = false,
        float bloomPulse = 1f)
    {
        bool small = rect.Width < 20f;
        GraphicsPath path;
        float[] globe;
        lock (typeof(ArkEmblem))
        {
            if (small)
            {
                _aperture16 ??= ParsePath(ArkEmblemGeometry.Aperture16Path);
                path = _aperture16;
                globe = ArkEmblemGeometry.Aperture16Globe;
            }
            else
            {
                _aperture32 ??= ParsePath(ArkEmblemGeometry.Aperture32Path);
                path = _aperture32;
                globe = ArkEmblemGeometry.Aperture32Globe;
            }
        }

        var globeBounds = new RectangleF(globe[0] - globe[2], globe[1] - globe[2], globe[2] * 2f, globe[2] * 2f);
        var bounds = RectangleF.Union(path.GetBounds(), globeBounds);
        float scale = Math.Min(rect.Width / bounds.Width, rect.Height / bounds.Height);

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(rect.X + (rect.Width - bounds.Width * scale) / 2f, rect.Y + (rect.Height - bounds.Height * scale) / 2f);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-bounds.X, -bounds.Y);

        Color effectiveGlobe = globeColorOverride ?? (solidGlobe ? Color.FromArgb(255, GlobeGreen) : GlobeGreen);

        if (bloomGlobe)
        {
            // Multi-ring radial Coral bloom behind and around the globe
            float gx = globe[0];
            float gy = globe[1];
            float gr = globe[2] * Math.Clamp(bloomPulse, 0.85f, 1.35f);
            using (var outerBloom = new SolidBrush(Color.FromArgb(46, effectiveGlobe)))
            using (var midBloom = new SolidBrush(Color.FromArgb(92, effectiveGlobe)))
            using (var innerBloom = new SolidBrush(Color.FromArgb(148, effectiveGlobe)))
            {
                float r3 = gr * 2.45f;
                float r2 = gr * 1.82f;
                float r1 = gr * 1.34f;
                g.FillEllipse(outerBloom, gx - r3, gy - r3, r3 * 2f, r3 * 2f);
                g.FillEllipse(midBloom, gx - r2, gy - r2, r2 * 2f, r2 * 2f);
                g.FillEllipse(innerBloom, gx - r1, gy - r1, r1 * 2f, r1 * 2f);
            }
        }

        if (asCursor)
        {
            // Subtle drop shadow + dark ground outline so the cream Aperture and globe pop over light and dark surfaces
            var shadowState = g.Save();
            g.TranslateTransform(0f, 1.3f);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, globe[0] - globe[2] - 0.6f, globe[1] - globe[2] - 0.6f, (globe[2] + 0.6f) * 2f, (globe[2] + 0.6f) * 2f);
                g.FillPath(shadowBrush, path);
            }
            g.Restore(shadowState);

            using var outlinePen = new Pen(PhosphorTheme.Ground, 1.8f) { LineJoin = LineJoin.Round };
            g.DrawEllipse(outlinePen, globe[0] - globe[2], globe[1] - globe[2], globe[2] * 2f, globe[2] * 2f);
            g.DrawPath(outlinePen, path);
        }

        using (var globeBrush = new SolidBrush(effectiveGlobe))
        {
            g.FillEllipse(globeBrush, globe[0] - globe[2], globe[1] - globe[2], globe[2] * 2f, globe[2] * 2f);
        }
        if (bloomGlobe)
        {
            // Hot inner bloom specular core on the Coral globe
            float coreR = globe[2] * 0.52f;
            using var coreBrush = new SolidBrush(Color.FromArgb(170, 255, 226, 212));
            g.FillEllipse(coreBrush, globe[0] - coreR, globe[1] - coreR, coreR * 2f, coreR * 2f);
        }
        using (var cream = new SolidBrush(Cream))
        {
            g.FillPath(cream, path);
        }
        g.Restore(state);
    }

    private static void EnsurePaths()
    {
        if (_cube != null) return;
        var cube = ParsePath(ArkEmblemGeometry.CubePath);
        foreach (var stub in ArkEmblemGeometry.StubPaths)
        {
            using var sp = ParsePath(stub);
            cube.AddPath(sp, false);
        }
        _cube = cube;
        _globe = ParsePath(ArkEmblemGeometry.GlobePath);
    }

    /// <summary>Green variant: cream cube, green globe at 40%.</summary>
    public static void Draw(Graphics g, RectangleF rect)
    {
        EnsurePaths();
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = rect.Width / ArkEmblemGeometry.ViewBox;
        g.TranslateTransform(rect.X, rect.Y);
        g.ScaleTransform(scale, rect.Height / ArkEmblemGeometry.ViewBox);
        using (var globeBrush = new SolidBrush(GlobeGreen)) g.FillPath(globeBrush, _globe!);
        using (var cubeBrush = new SolidBrush(Cream)) g.FillPath(cubeBrush, _cube!);
        g.Restore(state);
    }

    /// <summary>Parses absolute M, L, A, Z commands (the only ones the emblem uses) into an even-odd path.</summary>
    private static GraphicsPath ParsePath(string d)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        var tokens = Tokenize(d);
        int i = 0;
        PointF current = PointF.Empty;
        PointF start = PointF.Empty;
        char cmd = 'M';
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Length == 1 && char.IsLetter(t[0]))
            {
                cmd = t[0];
                i++;
                if (cmd == 'Z' || cmd == 'z')
                {
                    path.CloseFigure();
                    current = start;
                    continue;
                }
            }
            switch (cmd)
            {
                case 'M':
                    current = new PointF(F(tokens[i]), F(tokens[i + 1]));
                    start = current;
                    path.StartFigure();
                    i += 2;
                    cmd = 'L';
                    break;
                case 'L':
                    {
                        var next = new PointF(F(tokens[i]), F(tokens[i + 1]));
                        path.AddLine(current, next);
                        current = next;
                        i += 2;
                        break;
                    }
                case 'A':
                    {
                        float rx = F(tokens[i]);
                        float ry = F(tokens[i + 1]);
                        float rot = F(tokens[i + 2]);
                        bool large = F(tokens[i + 3]) != 0;
                        bool sweep = F(tokens[i + 4]) != 0;
                        var end = new PointF(F(tokens[i + 5]), F(tokens[i + 6]));
                        AddSvgArc(path, current, end, rx, ry, rot, large, sweep);
                        current = end;
                        i += 7;
                        break;
                    }
                default:
                    throw new FormatException($"Unsupported SVG path command '{cmd}' in emblem geometry.");
            }
        }
        return path;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static List<string> Tokenize(string d)
    {
        var list = new List<string>();
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsLetter(c)) { list.Add(c.ToString()); i++; continue; }
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            int j = i;
            if (d[j] == '-' || d[j] == '+') j++;
            while (j < d.Length && (char.IsDigit(d[j]) || d[j] == '.')) j++;
            list.Add(d.Substring(i, j - i));
            i = j;
        }
        return list;
    }

    /// <summary>SVG endpoint arc to GDI+ AddArc (center parameterization, W3C implementation notes F.6.5).</summary>
    private static void AddSvgArc(GraphicsPath path, PointF p1, PointF p2, float rx, float ry, float rotDeg, bool largeArc, bool sweep)
    {
        if (rx == 0 || ry == 0 || p1 == p2)
        {
            path.AddLine(p1, p2);
            return;
        }
        double phi = rotDeg * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        double dx = (p1.X - p2.X) / 2.0, dy = (p1.Y - p2.Y) / 2.0;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;
        double rxd = Math.Abs(rx), ryd = Math.Abs(ry);
        double lambda = (x1p * x1p) / (rxd * rxd) + (y1p * y1p) / (ryd * ryd);
        if (lambda > 1) { rxd *= Math.Sqrt(lambda); ryd *= Math.Sqrt(lambda); }
        double num = rxd * rxd * ryd * ryd - rxd * rxd * y1p * y1p - ryd * ryd * x1p * x1p;
        double den = rxd * rxd * y1p * y1p + ryd * ryd * x1p * x1p;
        double coef = (largeArc != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * (rxd * y1p / ryd);
        double cyp = coef * (-ryd * x1p / rxd);
        double cx = cosPhi * cxp - sinPhi * cyp + (p1.X + p2.X) / 2.0;
        double cy = sinPhi * cxp + cosPhi * cyp + (p1.Y + p2.Y) / 2.0;
        double theta1 = Angle(1, 0, (x1p - cxp) / rxd, (y1p - cyp) / ryd);
        double dtheta = Angle((x1p - cxp) / rxd, (y1p - cyp) / ryd, (-x1p - cxp) / rxd, (-y1p - cyp) / ryd);
        if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
        else if (sweep && dtheta < 0) dtheta += 2 * Math.PI;

        // GDI+ AddArc takes the unrotated ellipse; the emblem arcs carry no rotation.
        var rect = new RectangleF((float)(cx - rxd), (float)(cy - ryd), (float)(rxd * 2), (float)(ryd * 2));
        path.AddArc(rect, (float)(theta1 * 180.0 / Math.PI), (float)(dtheta * 180.0 / Math.PI));
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
        double ang = Math.Acos(Math.Clamp(dot / len, -1, 1));
        return (ux * vy - uy * vx) < 0 ? -ang : ang;
    }
}
