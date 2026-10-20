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

    internal static GraphicsPath ParseSvgSubpathCentered(string d, float pivotX, float pivotY, float preScale)
    {
        var raw = ParsePath(d);
        using var m = new Matrix();
        m.Translate(-pivotX, -pivotY);
        m.Scale(preScale, preScale, MatrixOrder.Append);
        raw.Transform(m);
        return raw;
    }
}

public enum ApertureMorphMode
{
    AutoContextual = 0,
    HoloscopeIris = 1,
    SyndicateCompass = 2,
    WizWandStar = 3,
    AegisShield = 4
}

/// <summary>
/// 240Hz real-time shape-morphing engine for the Ark Aperture cursor.
/// Decomposes the Aperture emblem into its 3 independent Cream chevron wings + 4th emergence wing
/// + state-reactive Center Globe + auxiliary Phosphor geometry, smoothly morphing the Aperture into
/// all 14 Windows cursor states and custom Wiz/Syndicate formations.
/// </summary>
internal static class ArkApertureMorphEngine
{
    private const float BaseSvgScale = 0.9238f;
    private const float LeftNaturalDirDeg = -132f;
    private const float RightNaturalDirDeg = 132f;

    private static GraphicsPath? _topWingPath;
    private static GraphicsPath? _leftWingPath;
    private static GraphicsPath? _rightWingPath;

    public static ApertureMorphMode ActiveMorphMode { get; set; } = ApertureMorphMode.AutoContextual;

    // Live Showcase state (cycles through all morph formations automatically when triggered)
    private static bool _showcaseActive;
    private static double _showcaseElapsedSec;
    private static string _showcaseStageLabel = "";

    public static bool IsShowcaseActive => _showcaseActive;
    public static string ShowcaseStageLabel => _showcaseStageLabel;

    private struct WingPose
    {
        public float X;
        public float Y;
        public float AngleDeg;
        public float ScaleX;
        public float ScaleY;
        public float Alpha;

        public static WingPose Create(float x, float y, float angleDeg, float scaleX = 1f, float scaleY = 1f, float alpha = 1f) =>
            new() { X = x, Y = y, AngleDeg = angleDeg, ScaleX = scaleX, ScaleY = scaleY, Alpha = alpha };
    }

    private struct MorphState
    {
        public WingPose Top;
        public WingPose Left;
        public WingPose Right;
        public WingPose Fourth;

        public float GlobeX;
        public float GlobeY;
        public float GlobeRx;
        public float GlobeRy;
        public float GlobeR;
        public float GlobeG;
        public float GlobeB;
        public float BloomStrength;

        public float CaretStemAlpha;
        public float ReticleRingAlpha;
        public float SlashAlpha;
        public float QuestionAlpha;
        public float ResizeShaftAlpha;
        public float ResizeShaftAngleDeg;
        public float FourWayShaftAlpha;
        public float OrbitalSpinWeight;
    }

    private static MorphState _current;
    private static MorphState _target;
    private static bool _initialized;
    private static bool _isAnimating;

    public static bool IsAnimating => _isAnimating || _showcaseActive || ActiveMorphMode != ApertureMorphMode.AutoContextual;

    private static void EnsureWingPaths()
    {
        if (_topWingPath != null) return;
        lock (typeof(ArkApertureMorphEngine))
        {
            if (_topWingPath != null) return;
            string[] parts = ArkEmblemGeometry.Aperture32Path.Split("ZM", StringSplitOptions.RemoveEmptyEntries);
            string rightSvg = parts[0] + "Z";
            string topSvg = "M" + parts[1] + "Z";
            string leftSvg = "M" + parts[2];

            _rightWingPath = ArkEmblem.ParseSvgSubpathCentered(rightSvg, 25.0f, 23.0f, BaseSvgScale);
            _topWingPath = ArkEmblem.ParseSvgSubpathCentered(topSvg, 16.0f, 8.85f, BaseSvgScale);
            _leftWingPath = ArkEmblem.ParseSvgSubpathCentered(leftSvg, 7.0f, 23.0f, BaseSvgScale);
        }
    }

    public static void StartMorphShowcase()
    {
        _showcaseActive = true;
        _showcaseElapsedSec = 0;
        _showcaseStageLabel = "Arrow -> Hand (Coral Bloom)";
    }

    public static void Step(double dtSec, WindowsCursorRole role, bool isAiThinking, float spinAngleDeg)
    {
        EnsureWingPaths();

        WindowsCursorRole effectiveRole = role;
        ApertureMorphMode effectiveMode = ActiveMorphMode;
        bool effectiveThinking = isAiThinking;

        if (_showcaseActive)
        {
            _showcaseElapsedSec += dtSec;
            int stage = (int)(_showcaseElapsedSec / 1.15);
            switch (stage)
            {
                case 0:
                    effectiveRole = WindowsCursorRole.Hand;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "Hand Hover (Iris Expansion + Coral Bloom)";
                    break;
                case 1:
                    effectiveRole = WindowsCursorRole.IBeam;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "IBeam Text Caret (Aperture Serif Caliper)";
                    break;
                case 2:
                    effectiveRole = WindowsCursorRole.Crosshair;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "Crosshair Reticle (4-Wing Holoscope Pinpoint)";
                    break;
                case 3:
                    effectiveRole = WindowsCursorRole.SizeWE;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "Resize WE (Bi-Directional Aperture Caliper)";
                    break;
                case 4:
                    effectiveRole = WindowsCursorRole.SizeAll;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "SizeAll Move (4-Way Outward Compass Rose)";
                    break;
                case 5:
                    effectiveRole = WindowsCursorRole.Wait;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "Wait Busy (Spinning 3-Wing Coral Turbine)";
                    break;
                case 6:
                    effectiveRole = WindowsCursorRole.Arrow;
                    effectiveMode = ApertureMorphMode.WizWandStar;
                    effectiveThinking = true;
                    _showcaseStageLabel = "Wiz Wand Star (True Path + Coral Bloom)";
                    break;
                case 7:
                    effectiveRole = WindowsCursorRole.No;
                    effectiveMode = ApertureMorphMode.AutoContextual;
                    _showcaseStageLabel = "Aegis Rust Lock (Forbidden Target)";
                    break;
                default:
                    _showcaseActive = false;
                    _showcaseStageLabel = "";
                    break;
            }
        }

        _target = BuildTargetPose(effectiveRole, effectiveMode, effectiveThinking, spinAngleDeg);

        if (!_initialized)
        {
            _current = _target;
            _initialized = true;
            _isAnimating = false;
            return;
        }

        float alpha = (float)(1.0 - Math.Exp(-Math.Clamp(dtSec, 0.0005, 0.05) * 20.0));
        float diff = 0f;

        StepWing(ref _current.Top, in _target.Top, alpha, ref diff);
        StepWing(ref _current.Left, in _target.Left, alpha, ref diff);
        StepWing(ref _current.Right, in _target.Right, alpha, ref diff);
        StepWing(ref _current.Fourth, in _target.Fourth, alpha, ref diff);

        StepFloat(ref _current.GlobeX, _target.GlobeX, alpha, ref diff);
        StepFloat(ref _current.GlobeY, _target.GlobeY, alpha, ref diff);
        StepFloat(ref _current.GlobeRx, _target.GlobeRx, alpha, ref diff);
        StepFloat(ref _current.GlobeRy, _target.GlobeRy, alpha, ref diff);
        StepFloat(ref _current.GlobeR, _target.GlobeR, alpha * 0.85f, ref diff);
        StepFloat(ref _current.GlobeG, _target.GlobeG, alpha * 0.85f, ref diff);
        StepFloat(ref _current.GlobeB, _target.GlobeB, alpha * 0.85f, ref diff);
        StepFloat(ref _current.BloomStrength, _target.BloomStrength, alpha, ref diff);

        StepFloat(ref _current.CaretStemAlpha, _target.CaretStemAlpha, alpha, ref diff);
        StepFloat(ref _current.ReticleRingAlpha, _target.ReticleRingAlpha, alpha, ref diff);
        StepFloat(ref _current.SlashAlpha, _target.SlashAlpha, alpha, ref diff);
        StepFloat(ref _current.QuestionAlpha, _target.QuestionAlpha, alpha, ref diff);
        StepFloat(ref _current.ResizeShaftAlpha, _target.ResizeShaftAlpha, alpha, ref diff);
        StepAngle(ref _current.ResizeShaftAngleDeg, _target.ResizeShaftAngleDeg, alpha, ref diff);
        StepFloat(ref _current.FourWayShaftAlpha, _target.FourWayShaftAlpha, alpha, ref diff);
        StepFloat(ref _current.OrbitalSpinWeight, _target.OrbitalSpinWeight, alpha, ref diff);

        _isAnimating = diff > 0.015f || _current.OrbitalSpinWeight > 0.02f || _current.BloomStrength > 0.05f;
    }

    private static void StepWing(ref WingPose cur, in WingPose tgt, float alpha, ref float diff)
    {
        StepFloat(ref cur.X, tgt.X, alpha, ref diff);
        StepFloat(ref cur.Y, tgt.Y, alpha, ref diff);
        StepAngle(ref cur.AngleDeg, tgt.AngleDeg, alpha, ref diff);
        StepFloat(ref cur.ScaleX, tgt.ScaleX, alpha, ref diff);
        StepFloat(ref cur.ScaleY, tgt.ScaleY, alpha, ref diff);
        StepFloat(ref cur.Alpha, tgt.Alpha, alpha, ref diff);
    }

    private static void StepFloat(ref float cur, float tgt, float alpha, ref float diff)
    {
        float d = tgt - cur;
        diff = Math.Max(diff, Math.Abs(d));
        cur += d * alpha;
    }

    private static void StepAngle(ref float curDeg, float tgtDeg, float alpha, ref float diff)
    {
        float delta = (tgtDeg - curDeg) % 360f;
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        diff = Math.Max(diff, Math.Abs(delta) * 0.08f);
        curDeg = (curDeg + delta * alpha) % 360f;
    }

    private static MorphState CreateDefaultArrowState(bool isAiThinking)
    {
        Color globeCol = isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.EmblemGlobe;
        return new MorphState
        {
            Top = WingPose.Create(0f, 3.79f, 0f, 1f, 1f, 1f),
            Left = WingPose.Create(-8.31f, 16.86f, 0f, 1f, 1f, 1f),
            Right = WingPose.Create(8.31f, 16.86f, 0f, 1f, 1f, 1f),
            Fourth = WingPose.Create(0f, 10.0f, 180f, 0.25f, 0.25f, 0f),
            GlobeX = 0f,
            GlobeY = 13.16f,
            GlobeRx = 5.54f,
            GlobeRy = 5.54f,
            GlobeR = globeCol.R,
            GlobeG = globeCol.G,
            GlobeB = globeCol.B,
            BloomStrength = isAiThinking ? 1.0f : 0f
        };
    }

    private static MorphState BuildTargetPose(
        WindowsCursorRole role,
        ApertureMorphMode customMode,
        bool isAiThinking,
        float spinAngleDeg)
    {
        var s = CreateDefaultArrowState(isAiThinking);
        float pulse = (float)Math.Sin(spinAngleDeg * Math.PI / 90.0);

        // If user selected a dedicated custom morph formation, apply it when in normal Arrow state
        if (customMode != ApertureMorphMode.AutoContextual && role == WindowsCursorRole.Arrow)
        {
            switch (customMode)
            {
                case ApertureMorphMode.HoloscopeIris:
                {
                    float r = 10.8f + 1.1f * pulse;
                    s.Top = WingPose.Create(0f, -r, 180f, 0.85f, 0.85f, 1f);
                    s.Fourth = WingPose.Create(0f, r, 0f, 0.85f, 0.85f, 1f);
                    s.Left = WingPose.Create(-r, 0f, 90f - LeftNaturalDirDeg, 0.85f, 0.85f, 1f);
                    s.Right = WingPose.Create(r, 0f, -90f - RightNaturalDirDeg, 0.85f, 0.85f, 1f);
                    s.GlobeX = 0f;
                    s.GlobeY = 0f;
                    s.GlobeRx = 4.6f;
                    s.GlobeRy = 4.6f;
                    SetGlobeColor(ref s, isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 0.9f);
                    s.ReticleRingAlpha = 0.85f;
                    return s;
                }
                case ApertureMorphMode.SyndicateCompass:
                {
                    float r = 12.6f;
                    s.Top = WingPose.Create(0f, -r, 0f, 0.86f, 0.86f, 1f);
                    s.Fourth = WingPose.Create(0f, r, 180f, 0.86f, 0.86f, 1f);
                    s.Left = WingPose.Create(-r, 0f, -90f - LeftNaturalDirDeg, 0.86f, 0.86f, 1f);
                    s.Right = WingPose.Create(r, 0f, 90f - RightNaturalDirDeg, 0.86f, 0.86f, 1f);
                    s.GlobeX = 0f;
                    s.GlobeY = 0f;
                    s.GlobeRx = 4.8f;
                    s.GlobeRy = 4.8f;
                    SetGlobeColor(ref s, PhosphorTheme.Coral, 1.0f);
                    s.FourWayShaftAlpha = 0.75f;
                    return s;
                }
                case ApertureMorphMode.WizWandStar:
                {
                    s.Top = WingPose.Create(0f, 2.8f, 0f, 1.08f, 1.08f, 1f);
                    s.Left = WingPose.Create(-10.2f, 13.5f, -95f - LeftNaturalDirDeg + 6f * pulse, 0.92f, 0.92f, 1f);
                    s.Right = WingPose.Create(10.2f, 13.5f, 95f - RightNaturalDirDeg - 6f * pulse, 0.92f, 0.92f, 1f);
                    s.Fourth = WingPose.Create(0f, 22.8f, 180f, 0.82f, 0.82f, 0.95f);
                    s.GlobeX = 0f;
                    s.GlobeY = 13.0f;
                    s.GlobeRx = 6.2f;
                    s.GlobeRy = 6.2f;
                    SetGlobeColor(ref s, PhosphorTheme.Coral, 1.0f);
                    s.ReticleRingAlpha = 0.55f;
                    return s;
                }
                case ApertureMorphMode.AegisShield:
                {
                    float r = 9.6f;
                    s.Top = WingPose.Create(0f, 3.2f, 0f, 1.02f, 1.02f, 1f);
                    s.Left = WingPose.Create(-r * 0.86f, 14.5f, 60f - LeftNaturalDirDeg, 0.92f, 0.92f, 1f);
                    s.Right = WingPose.Create(r * 0.86f, 14.5f, -60f - RightNaturalDirDeg, 0.92f, 0.92f, 1f);
                    s.Fourth = WingPose.Create(0f, 21.2f, 0f, 0.78f, 0.78f, 0.9f);
                    s.GlobeX = 0f;
                    s.GlobeY = 13.2f;
                    s.GlobeRx = 5.8f;
                    s.GlobeRy = 5.8f;
                    SetGlobeColor(ref s, PhosphorTheme.Seal, 0.95f);
                    s.ReticleRingAlpha = 0.65f;
                    return s;
                }
            }
        }

        switch (role)
        {
            case WindowsCursorRole.Arrow:
                return s;

            case WindowsCursorRole.Hand:
            {
                // Iris expansion: wings flare outward around a Coral blooming globe
                s.Top = WingPose.Create(0f, 2.75f, 0f, 1.08f, 1.08f, 1f);
                s.Left = WingPose.Create(-9.85f, 17.6f, -12f, 1.04f, 1.04f, 1f);
                s.Right = WingPose.Create(9.85f, 17.6f, 12f, 1.04f, 1.04f, 1f);
                s.Fourth = WingPose.Create(0f, 22.4f, 180f, 0.62f, 0.55f, 0.55f);
                s.GlobeX = 0f;
                s.GlobeY = 13.0f;
                s.GlobeRx = 6.35f;
                s.GlobeRy = 6.35f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 1.0f);
                s.ReticleRingAlpha = 0.38f;
                return s;
            }

            case WindowsCursorRole.IBeam:
            {
                // Aperture Caret Caliper: Top & Fourth wings form top/bottom serifs; Globe becomes vertical lens at (0,0)
                s.Top = WingPose.Create(0f, -13.2f, 180f, 0.76f, 0.56f, 1f);
                s.Fourth = WingPose.Create(0f, 13.2f, 0f, 0.76f, 0.56f, 1f);
                s.Left = WingPose.Create(-3.2f, 0f, 90f - LeftNaturalDirDeg, 0.42f, 0.42f, 0.72f);
                s.Right = WingPose.Create(3.2f, 0f, -90f - RightNaturalDirDeg, 0.42f, 0.42f, 0.72f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 2.65f;
                s.GlobeRy = 4.5f;
                SetGlobeColor(ref s, isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 0.85f);
                s.CaretStemAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.Crosshair:
            {
                // 4-Wing Holoscope Reticle: all 4 wings point inward at the central pinpoint globe
                float dist = 11.8f;
                s.Top = WingPose.Create(0f, -dist, 180f, 0.82f, 0.82f, 1f);
                s.Fourth = WingPose.Create(0f, dist, 0f, 0.82f, 0.82f, 1f);
                s.Left = WingPose.Create(-dist, 0f, 90f - LeftNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.Right = WingPose.Create(dist, 0f, -90f - RightNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 2.7f;
                s.GlobeRy = 2.7f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 0.95f);
                s.ReticleRingAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.Wait:
            {
                // Spinning 3-Wing Coral Turbine around the central Coral Bloom Globe
                float r = 10.6f + 0.9f * pulse;
                s.Top = WingPose.Create(0f, -r, 0f, 0.88f, 0.88f, 1f);
                s.Left = WingPose.Create(-r * 0.866f, r * 0.5f, 0f, 0.88f, 0.88f, 1f);
                s.Right = WingPose.Create(r * 0.866f, r * 0.5f, 0f, 0.88f, 0.88f, 1f);
                s.Fourth = WingPose.Create(0f, 0f, 180f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 5.4f;
                s.GlobeRy = 5.4f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 1.0f);
                s.ReticleRingAlpha = 0.72f;
                s.OrbitalSpinWeight = 1.0f;
                return s;
            }

            case WindowsCursorRole.AppStarting:
            {
                // Active Pointer + Wiz Coral Bloom + pulsing lower wings
                float wingWave = 8f * pulse;
                s.Top = WingPose.Create(0f, 3.5f, 0f, 1.04f, 1.04f, 1f);
                s.Left = WingPose.Create(-9.2f - 0.8f * pulse, 16.4f, wingWave, 0.96f, 0.96f, 1f);
                s.Right = WingPose.Create(9.2f + 0.8f * pulse, 16.4f, -wingWave, 0.96f, 0.96f, 1f);
                s.Fourth = WingPose.Create(0f, 22.0f, 180f, 0.58f, 0.58f, 0.65f);
                s.GlobeX = 0f;
                s.GlobeY = 13.0f;
                s.GlobeRx = 6.1f;
                s.GlobeRy = 6.1f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 1.0f);
                s.ReticleRingAlpha = 0.48f;
                return s;
            }

            case WindowsCursorRole.SizeWE:
            case WindowsCursorRole.SizeNS:
            case WindowsCursorRole.SizeNWSE:
            case WindowsCursorRole.SizeNESW:
            {
                // Bi-Directional Aperture Caliper along the resize axis
                float axisDeg = role switch
                {
                    WindowsCursorRole.SizeWE => 90f,     // East-West
                    WindowsCursorRole.SizeNS => 0f,      // North-South
                    WindowsCursorRole.SizeNWSE => 135f,  // NW-SE
                    _ => 45f                             // NE-SW
                };
                double rad = (axisDeg - 90.0) * Math.PI / 180.0;
                float ux = (float)Math.Cos(rad);
                float uy = (float)Math.Sin(rad);
                float perpX = -uy;
                float perpY = ux;
                float dist = 12.6f;

                s.Top = WingPose.Create(ux * dist, uy * dist, axisDeg, 0.86f, 0.86f, 1f);
                s.Fourth = WingPose.Create(-ux * dist, -uy * dist, axisDeg + 180f, 0.86f, 0.86f, 1f);
                s.Left = WingPose.Create(perpX * 4.2f, perpY * 4.2f, axisDeg - 90f - LeftNaturalDirDeg, 0.45f, 0.45f, 0.68f);
                s.Right = WingPose.Create(-perpX * 4.2f, -perpY * 4.2f, axisDeg + 90f - RightNaturalDirDeg, 0.45f, 0.45f, 0.68f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 4.2f;
                s.GlobeRy = 4.2f;
                SetGlobeColor(ref s, isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 0.78f);
                s.ResizeShaftAlpha = 1.0f;
                s.ResizeShaftAngleDeg = axisDeg;
                return s;
            }

            case WindowsCursorRole.SizeAll:
            {
                // 4-Way Outward Aperture Compass Rose
                float dist = 13.0f;
                s.Top = WingPose.Create(0f, -dist, 0f, 0.82f, 0.82f, 1f);
                s.Fourth = WingPose.Create(0f, dist, 180f, 0.82f, 0.82f, 1f);
                s.Left = WingPose.Create(-dist, 0f, -90f - LeftNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.Right = WingPose.Create(dist, 0f, 90f - RightNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 4.5f;
                s.GlobeRy = 4.5f;
                SetGlobeColor(ref s, isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 0.88f);
                s.FourWayShaftAlpha = 0.9f;
                return s;
            }

            case WindowsCursorRole.No:
            {
                // Aegis Rust Shield & Lock
                float r = 9.8f;
                s.Top = WingPose.Create(0f, -r, 180f, 0.82f, 0.82f, 1f);
                s.Left = WingPose.Create(-r * 0.866f, r * 0.5f, 60f - LeftNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.Right = WingPose.Create(r * 0.866f, r * 0.5f, -60f - RightNaturalDirDeg, 0.82f, 0.82f, 1f);
                s.Fourth = WingPose.Create(0f, 0f, 0f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 5.0f;
                s.GlobeRy = 5.0f;
                SetGlobeColor(ref s, PhosphorTheme.Rust, 0.95f);
                s.ReticleRingAlpha = 1.0f;
                s.SlashAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.Help:
            {
                // Oracle Seal Amber Globe + Question Glyph inside Globe
                s.Top = WingPose.Create(0f, 3.4f, 0f, 1.04f, 1.04f, 1f);
                s.Left = WingPose.Create(-8.8f, 17.0f, -5f, 1.0f, 1.0f, 1f);
                s.Right = WingPose.Create(8.8f, 17.0f, 5f, 1.0f, 1.0f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 13.16f;
                s.GlobeRx = 6.2f;
                s.GlobeRy = 6.2f;
                SetGlobeColor(ref s, PhosphorTheme.Seal, 0.92f);
                s.QuestionAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.UpArrow:
            {
                // Vertical Spearhead Aperture with Seal Amber Globe
                s.Top = WingPose.Create(0f, 2.9f, 0f, 1.08f, 1.08f, 1f);
                s.Left = WingPose.Create(-5.8f, 15.2f, 18f, 0.92f, 0.92f, 1f);
                s.Right = WingPose.Create(5.8f, 15.2f, -18f, 0.92f, 0.92f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 10.8f;
                s.GlobeRx = 4.6f;
                s.GlobeRy = 4.6f;
                SetGlobeColor(ref s, PhosphorTheme.Seal, 0.85f);
                return s;
            }

            default:
                return s;
        }
    }

    private static void SetGlobeColor(ref MorphState s, Color c, float bloom)
    {
        s.GlobeR = c.R;
        s.GlobeG = c.G;
        s.GlobeB = c.B;
        s.BloomStrength = bloom;
    }

    public static void DrawMorphedApertureCursor(
        Graphics g,
        float cx,
        float cy,
        float angleDeg,
        float scale,
        bool isAiThinking,
        WindowsCursorRole role,
        float spinAngleDeg)
    {
        EnsureWingPaths();
        if (!_initialized)
        {
            Step(0.016, role, isAiThinking, spinAngleDeg);
        }

        var rootState = g.Save();
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TranslateTransform(cx, cy);

        // Apply subtle banking / true path rotation to pointer-like states; keep symmetric centered states upright
        float roleBankWeight = (role is WindowsCursorRole.Arrow or WindowsCursorRole.Hand or WindowsCursorRole.AppStarting or WindowsCursorRole.Help)
            ? 1.0f - Math.Clamp(_current.OrbitalSpinWeight + _current.CaretStemAlpha + _current.ResizeShaftAlpha + _current.FourWayShaftAlpha, 0f, 1f)
            : 0f;
        if (Math.Abs(angleDeg) > 0.01f && roleBankWeight > 0.01f)
        {
            g.RotateTransform(angleDeg * roleBankWeight);
        }

        g.ScaleTransform(scale, scale);

        Color globeColor = Color.FromArgb(
            255,
            Math.Clamp((int)Math.Round(_current.GlobeR), 0, 255),
            Math.Clamp((int)Math.Round(_current.GlobeG), 0, 255),
            Math.Clamp((int)Math.Round(_current.GlobeB), 0, 255));

        float pulse = 1.0f + 0.16f * (float)Math.Sin(spinAngleDeg * Math.PI / 60.0);
        float gx = _current.GlobeX;
        float gy = _current.GlobeY;
        float grx = Math.Max(1.2f, _current.GlobeRx);
        float gry = Math.Max(1.2f, _current.GlobeRy);

        // 1. Multi-ring radial Bloom behind the Globe when active
        if (_current.BloomStrength > 0.02f)
        {
            float bAlpha = Math.Clamp(_current.BloomStrength, 0f, 1f);
            float brx = grx * pulse;
            float bry = gry * pulse;
            using var outerBloom = new SolidBrush(Color.FromArgb((int)(48 * bAlpha), globeColor));
            using var midBloom = new SolidBrush(Color.FromArgb((int)(96 * bAlpha), globeColor));
            using var innerBloom = new SolidBrush(Color.FromArgb((int)(152 * bAlpha), globeColor));

            g.FillEllipse(outerBloom, gx - brx * 2.5f, gy - bry * 2.5f, brx * 5.0f, bry * 5.0f);
            g.FillEllipse(midBloom, gx - brx * 1.85f, gy - bry * 1.85f, brx * 3.7f, bry * 3.7f);
            g.FillEllipse(innerBloom, gx - brx * 1.35f, gy - bry * 1.35f, brx * 2.7f, bry * 2.7f);
        }

        // 2. Auxiliary morph connectors (IBeam Caret Stem, Holoscope Ring, Resize Shafts, 4-Way Compass Shafts)
        if (_current.CaretStemAlpha > 0.02f)
        {
            int a = (int)(235 * Math.Clamp(_current.CaretStemAlpha, 0f, 1f));
            using var shadowPen = new Pen(Color.FromArgb((int)(150 * _current.CaretStemAlpha), PhosphorTheme.Ground), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var stemPen = new Pen(Color.FromArgb(a, PhosphorTheme.EmblemCream), 1.85f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, gx, -11.5f, gx, 11.5f);
            g.DrawLine(stemPen, gx, -11.5f, gx, 11.5f);
        }

        if (_current.ResizeShaftAlpha > 0.02f)
        {
            var st = g.Save();
            g.TranslateTransform(gx, gy);
            g.RotateTransform(_current.ResizeShaftAngleDeg - 90f);
            int a = (int)(220 * Math.Clamp(_current.ResizeShaftAlpha, 0f, 1f));
            using var shadowPen = new Pen(Color.FromArgb((int)(145 * _current.ResizeShaftAlpha), PhosphorTheme.Ground), 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var shaftPen = new Pen(Color.FromArgb(a, PhosphorTheme.EmblemCream), 1.75f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, -9.2f, 0f, 9.2f, 0f);
            g.DrawLine(shaftPen, -9.2f, 0f, 9.2f, 0f);
            g.Restore(st);
        }

        if (_current.FourWayShaftAlpha > 0.02f)
        {
            int a = (int)(210 * Math.Clamp(_current.FourWayShaftAlpha, 0f, 1f));
            using var shadowPen = new Pen(Color.FromArgb((int)(140 * _current.FourWayShaftAlpha), PhosphorTheme.Ground), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var shaftPen = new Pen(Color.FromArgb(a, PhosphorTheme.EmblemCream), 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, gx - 9.5f, gy, gx + 9.5f, gy);
            g.DrawLine(shadowPen, gx, gy - 9.5f, gx, gy + 9.5f);
            g.DrawLine(shaftPen, gx - 9.5f, gy, gx + 9.5f, gy);
            g.DrawLine(shaftPen, gx, gy - 9.5f, gx, gy + 9.5f);
        }

        if (_current.ReticleRingAlpha > 0.02f)
        {
            float ringR = Math.Max(grx, gry) + 4.4f;
            int a = (int)(195 * Math.Clamp(_current.ReticleRingAlpha, 0f, 1f));
            using var ringShadow = new Pen(Color.FromArgb((int)(130 * _current.ReticleRingAlpha), PhosphorTheme.Ground), 3.0f);
            using var ringPen = new Pen(Color.FromArgb(a, globeColor), 1.55f);
            g.DrawEllipse(ringShadow, gx - ringR, gy - ringR, ringR * 2f, ringR * 2f);
            g.DrawEllipse(ringPen, gx - ringR, gy - ringR, ringR * 2f, ringR * 2f);
        }

        // 3. Center Globe with Ground outline + Specular Bloom Core
        using (var shadowBrush = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
        {
            g.FillEllipse(shadowBrush, gx - grx - 0.5f, gy - gry + 0.8f, (grx + 0.5f) * 2f, (gry + 0.5f) * 2f);
        }
        using (var outlinePen = new Pen(PhosphorTheme.Ground, 1.7f) { LineJoin = LineJoin.Round })
        {
            g.DrawEllipse(outlinePen, gx - grx, gy - gry, grx * 2f, gry * 2f);
        }
        using (var globeBrush = new SolidBrush(globeColor))
        {
            g.FillEllipse(globeBrush, gx - grx, gy - gry, grx * 2f, gry * 2f);
        }
        if (_current.BloomStrength > 0.05f)
        {
            int coreAlpha = (int)(175 * Math.Clamp(_current.BloomStrength, 0f, 1f));
            using var coreBrush = new SolidBrush(Color.FromArgb(coreAlpha, 255, 232, 218));
            g.FillEllipse(coreBrush, gx - grx * 0.5f, gy - gry * 0.5f, grx, gry);
        }

        // Prohibition Slash for 'No' state
        if (_current.SlashAlpha > 0.02f)
        {
            float sr = Math.Max(grx, gry) + 3.8f;
            float d = sr * 0.707f;
            int a = (int)(245 * Math.Clamp(_current.SlashAlpha, 0f, 1f));
            using var slashShadow = new Pen(Color.FromArgb((int)(160 * _current.SlashAlpha), PhosphorTheme.Ground), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var slashPen = new Pen(Color.FromArgb(a, PhosphorTheme.Rust), 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(slashShadow, gx - d, gy - d, gx + d, gy + d);
            g.DrawLine(slashPen, gx - d, gy - d, gx + d, gy + d);
        }

        // Question glyph for 'Help' state
        if (_current.QuestionAlpha > 0.02f)
        {
            int a = (int)(250 * Math.Clamp(_current.QuestionAlpha, 0f, 1f));
            using var font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(a, PhosphorTheme.Ground));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("?", font, brush, new PointF(gx, gy + 0.5f), sf);
        }

        // 4. Draw the 4 independent morphing Cream Aperture wings (with orbital spin when in Wait turbine state)
        var wingSpinState = g.Save();
        if (_current.OrbitalSpinWeight > 0.01f)
        {
            g.TranslateTransform(gx, gy);
            g.RotateTransform(spinAngleDeg * _current.OrbitalSpinWeight);
            g.TranslateTransform(-gx, -gy);
        }

        DrawSingleWing(g, _topWingPath!, in _current.Top);
        DrawSingleWing(g, _leftWingPath!, in _current.Left);
        DrawSingleWing(g, _rightWingPath!, in _current.Right);
        DrawSingleWing(g, _topWingPath!, in _current.Fourth);

        g.Restore(wingSpinState);
        g.Restore(rootState);
    }

    private static void DrawSingleWing(Graphics g, GraphicsPath wingPath, in WingPose pose)
    {
        if (pose.Alpha <= 0.02f || pose.ScaleX <= 0.05f || pose.ScaleY <= 0.05f) return;

        var st = g.Save();
        g.TranslateTransform(pose.X, pose.Y);
        if (Math.Abs(pose.AngleDeg) > 0.01f)
        {
            g.RotateTransform(pose.AngleDeg);
        }
        g.ScaleTransform(pose.ScaleX, pose.ScaleY);

        int alpha = Math.Clamp((int)Math.Round(255 * pose.Alpha), 0, 255);
        int shadowAlpha = Math.Clamp((int)Math.Round(115 * pose.Alpha), 0, 255);

        var shadowSt = g.Save();
        g.TranslateTransform(0f, 1.15f);
        using (var shadowBrush = new SolidBrush(Color.FromArgb(shadowAlpha, 0, 0, 0)))
        {
            g.FillPath(shadowBrush, wingPath);
        }
        g.Restore(shadowSt);

        using (var outlinePen = new Pen(Color.FromArgb(alpha, PhosphorTheme.Ground), 1.65f) { LineJoin = LineJoin.Round })
        {
            g.DrawPath(outlinePen, wingPath);
        }
        using (var creamBrush = new SolidBrush(Color.FromArgb(alpha, PhosphorTheme.EmblemCream)))
        {
            g.FillPath(creamBrush, wingPath);
        }

        g.Restore(st);
    }
}
