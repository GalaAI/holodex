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

        // If user selected a dedicated custom morph formation, apply it when in normal Arrow state
        if (customMode != ApertureMorphMode.AutoContextual && role == WindowsCursorRole.Arrow)
        {
            switch (customMode)
            {
                case ApertureMorphMode.HoloscopeIris:
                {
                    float r = 10.4f;
                    s.Top = WingPose.Create(0f, -r, 180f, 0.82f, 0.82f, 1f);
                    s.Fourth = WingPose.Create(0f, r, 0f, 0.82f, 0.82f, 1f);
                    s.Left = WingPose.Create(-r, 0f, 90f - LeftNaturalDirDeg, 0.82f, 0.82f, 1f);
                    s.Right = WingPose.Create(r, 0f, -90f - RightNaturalDirDeg, 0.82f, 0.82f, 1f);
                    s.GlobeX = 0f;
                    s.GlobeY = 0f;
                    s.GlobeRx = 4.2f;
                    s.GlobeRy = 4.2f;
                    SetGlobeColor(ref s, isAiThinking ? PhosphorTheme.Coral : PhosphorTheme.Patina, 0.55f);
                    s.ReticleRingAlpha = 0.65f;
                    return s;
                }
                case ApertureMorphMode.SyndicateCompass:
                {
                    float r = 11.8f;
                    s.Top = WingPose.Create(0f, -r, 0f, 0.82f, 0.82f, 1f);
                    s.Fourth = WingPose.Create(0f, r, 180f, 0.82f, 0.82f, 1f);
                    s.Left = WingPose.Create(-r, 0f, -90f - LeftNaturalDirDeg, 0.82f, 0.82f, 1f);
                    s.Right = WingPose.Create(r, 0f, 90f - RightNaturalDirDeg, 0.82f, 0.82f, 1f);
                    s.GlobeX = 0f;
                    s.GlobeY = 0f;
                    s.GlobeRx = 4.2f;
                    s.GlobeRy = 4.2f;
                    SetGlobeColor(ref s, PhosphorTheme.Coral, 0.65f);
                    s.FourWayShaftAlpha = 0.65f;
                    return s;
                }
                case ApertureMorphMode.WizWandStar:
                {
                    s.Top = WingPose.Create(0f, 3.1f, 0f, 1.04f, 1.04f, 1f);
                    s.Left = WingPose.Create(-9.4f, 14.5f, -95f - LeftNaturalDirDeg, 0.88f, 0.88f, 1f);
                    s.Right = WingPose.Create(9.4f, 14.5f, 95f - RightNaturalDirDeg, 0.88f, 0.88f, 1f);
                    s.Fourth = WingPose.Create(0f, 21.6f, 180f, 0.76f, 0.76f, 0.85f);
                    s.GlobeX = 0f;
                    s.GlobeY = 13.0f;
                    s.GlobeRx = 5.8f;
                    s.GlobeRy = 5.8f;
                    SetGlobeColor(ref s, PhosphorTheme.Coral, 0.72f);
                    return s;
                }
                case ApertureMorphMode.AegisShield:
                {
                    float r = 9.2f;
                    s.Top = WingPose.Create(0f, 3.4f, 0f, 1.0f, 1.0f, 1f);
                    s.Left = WingPose.Create(-r * 0.86f, 14.8f, 60f - LeftNaturalDirDeg, 0.88f, 0.88f, 1f);
                    s.Right = WingPose.Create(r * 0.86f, 14.8f, -60f - RightNaturalDirDeg, 0.88f, 0.88f, 1f);
                    s.Fourth = WingPose.Create(0f, 20.5f, 0f, 0.4f, 0.4f, 0f);
                    s.GlobeX = 0f;
                    s.GlobeY = 13.2f;
                    s.GlobeRx = 5.6f;
                    s.GlobeRy = 5.6f;
                    SetGlobeColor(ref s, PhosphorTheme.Seal, 0.65f);
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
                // Subtle, noticeable Aperture opening + steady Coral globe glow (no extra 4th wing or ring)
                s.Top = WingPose.Create(0f, 3.25f, 0f, 1.04f, 1.04f, 1f);
                s.Left = WingPose.Create(-9.05f, 17.2f, -6f, 1.02f, 1.02f, 1f);
                s.Right = WingPose.Create(9.05f, 17.2f, 6f, 1.02f, 1.02f, 1f);
                s.Fourth = WingPose.Create(0f, 13.0f, 180f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 13.1f;
                s.GlobeRx = 5.95f;
                s.GlobeRy = 5.95f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 0.68f);
                return s;
            }

            case WindowsCursorRole.IBeam:
            {
                // Solid, basic vertical beam bar glowing Patina (steady, non-pulsing).
                // The 3 Aperture wings collapse smoothly into the vertical centerline and fade out.
                s.Top = WingPose.Create(0f, -6.5f, 0f, 0.16f, 0.55f, 0f);
                s.Left = WingPose.Create(0f, 6.5f, -LeftNaturalDirDeg, 0.16f, 0.55f, 0f);
                s.Right = WingPose.Create(0f, 6.5f, -RightNaturalDirDeg, 0.16f, 0.55f, 0f);
                s.Fourth = WingPose.Create(0f, 0f, 180f, 0.16f, 0.45f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 1.35f;
                s.GlobeRy = 9.8f;
                SetGlobeColor(ref s, PhosphorTheme.Patina, 0.62f);
                s.CaretStemAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.Crosshair:
            {
                // Clean, subtle Patina crosshair with small steady center dot
                float dist = 9.8f;
                s.Top = WingPose.Create(0f, -dist, 180f, 0.58f, 0.58f, 0.9f);
                s.Fourth = WingPose.Create(0f, dist, 0f, 0.58f, 0.58f, 0.9f);
                s.Left = WingPose.Create(-dist, 0f, 90f - LeftNaturalDirDeg, 0.58f, 0.58f, 0.9f);
                s.Right = WingPose.Create(dist, 0f, -90f - RightNaturalDirDeg, 0.58f, 0.58f, 0.9f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 2.1f;
                s.GlobeRy = 2.1f;
                SetGlobeColor(ref s, PhosphorTheme.Patina, 0.55f);
                s.FourWayShaftAlpha = 0.75f;
                return s;
            }

            case WindowsCursorRole.Wait:
            {
                // Compact 3-Wing Triad orbiting smoothly around a steady Coral Globe
                float r = 9.6f;
                s.Top = WingPose.Create(0f, -r, 0f, 0.80f, 0.80f, 1f);
                s.Left = WingPose.Create(-r * 0.866f, r * 0.5f, 0f, 0.80f, 0.80f, 1f);
                s.Right = WingPose.Create(r * 0.866f, r * 0.5f, 0f, 0.80f, 0.80f, 1f);
                s.Fourth = WingPose.Create(0f, 0f, 180f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 4.8f;
                s.GlobeRy = 4.8f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 0.68f);
                s.OrbitalSpinWeight = 1.0f;
                return s;
            }

            case WindowsCursorRole.AppStarting:
            {
                // Pure Ark Aperture with steady Coral Bloom Globe while Wiz / background work runs
                s.Top = WingPose.Create(0f, 3.65f, 0f, 1.0f, 1.0f, 1f);
                s.Left = WingPose.Create(-8.55f, 16.95f, -2f, 1.0f, 1.0f, 1f);
                s.Right = WingPose.Create(8.55f, 16.95f, 2f, 1.0f, 1.0f, 1f);
                s.Fourth = WingPose.Create(0f, 13.16f, 180f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 13.16f;
                s.GlobeRx = 5.75f;
                s.GlobeRy = 5.75f;
                SetGlobeColor(ref s, PhosphorTheme.Coral, 0.72f);
                return s;
            }

            case WindowsCursorRole.SizeWE:
            case WindowsCursorRole.SizeNS:
            case WindowsCursorRole.SizeNWSE:
            case WindowsCursorRole.SizeNESW:
            {
                // Clean, subtle 2-way resize caliper along the resize axis (side wings tuck away cleanly)
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
                float dist = 10.6f;

                s.Top = WingPose.Create(ux * dist, uy * dist, axisDeg, 0.72f, 0.72f, 1f);
                s.Fourth = WingPose.Create(-ux * dist, -uy * dist, axisDeg + 180f, 0.72f, 0.72f, 1f);
                s.Left = WingPose.Create(0f, 0f, axisDeg - LeftNaturalDirDeg, 0.2f, 0.2f, 0f);
                s.Right = WingPose.Create(0f, 0f, axisDeg - RightNaturalDirDeg, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 2.8f;
                s.GlobeRy = 2.8f;
                SetGlobeColor(ref s, PhosphorTheme.Patina, 0.52f);
                s.ResizeShaftAlpha = 1.0f;
                s.ResizeShaftAngleDeg = axisDeg;
                return s;
            }

            case WindowsCursorRole.SizeAll:
            {
                // Clean 4-Way Move Cross with steady Patina center dot
                float dist = 10.8f;
                s.Top = WingPose.Create(0f, -dist, 0f, 0.68f, 0.68f, 1f);
                s.Fourth = WingPose.Create(0f, dist, 180f, 0.68f, 0.68f, 1f);
                s.Left = WingPose.Create(-dist, 0f, -90f - LeftNaturalDirDeg, 0.68f, 0.68f, 1f);
                s.Right = WingPose.Create(dist, 0f, 90f - RightNaturalDirDeg, 0.68f, 0.68f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 3.0f;
                s.GlobeRy = 3.0f;
                SetGlobeColor(ref s, PhosphorTheme.Patina, 0.55f);
                s.FourWayShaftAlpha = 0.85f;
                return s;
            }

            case WindowsCursorRole.No:
            {
                // Subtle Rust prohibition ring & slash with wings folded cleanly inward
                float r = 8.4f;
                s.Top = WingPose.Create(0f, -r, 180f, 0.65f, 0.65f, 0.85f);
                s.Left = WingPose.Create(-r * 0.866f, r * 0.5f, 60f - LeftNaturalDirDeg, 0.65f, 0.65f, 0.85f);
                s.Right = WingPose.Create(r * 0.866f, r * 0.5f, -60f - RightNaturalDirDeg, 0.65f, 0.65f, 0.85f);
                s.Fourth = WingPose.Create(0f, 0f, 0f, 0.2f, 0.2f, 0f);
                s.GlobeX = 0f;
                s.GlobeY = 0f;
                s.GlobeRx = 4.2f;
                s.GlobeRy = 4.2f;
                SetGlobeColor(ref s, PhosphorTheme.Rust, 0.58f);
                s.ReticleRingAlpha = 0.9f;
                s.SlashAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.Help:
            {
                // Pure Ark Aperture with steady Seal Amber Globe + subtle '?'
                s.Top = WingPose.Create(0f, 3.6f, 0f, 1.0f, 1.0f, 1f);
                s.Left = WingPose.Create(-8.4f, 16.9f, -2f, 1.0f, 1.0f, 1f);
                s.Right = WingPose.Create(8.4f, 16.9f, 2f, 1.0f, 1.0f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 13.16f;
                s.GlobeRx = 5.8f;
                s.GlobeRy = 5.8f;
                SetGlobeColor(ref s, PhosphorTheme.Seal, 0.58f);
                s.QuestionAlpha = 1.0f;
                return s;
            }

            case WindowsCursorRole.UpArrow:
            {
                // Sleek vertical Aperture with steady Seal Amber Globe
                s.Top = WingPose.Create(0f, 3.2f, 0f, 1.02f, 1.02f, 1f);
                s.Left = WingPose.Create(-6.4f, 15.6f, 12f, 0.92f, 0.92f, 1f);
                s.Right = WingPose.Create(6.4f, 15.6f, -12f, 0.92f, 0.92f, 1f);
                s.GlobeX = 0f;
                s.GlobeY = 11.4f;
                s.GlobeRx = 4.6f;
                s.GlobeRy = 4.6f;
                SetGlobeColor(ref s, PhosphorTheme.Seal, 0.55f);
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

        float gx = _current.GlobeX;
        float gy = _current.GlobeY;
        float grx = Math.Max(1.1f, _current.GlobeRx);
        float gry = Math.Max(1.1f, _current.GlobeRy);

        // 1. Solid basic glowing Patina Beam Bar for IBeam (plus subtle morph connectors for Resize/Crosshair)
        if (_current.CaretStemAlpha > 0.02f)
        {
            float stemA = Math.Clamp(_current.CaretStemAlpha, 0f, 1f);
            int a = (int)(255 * stemA);
            float halfH = 10.2f * stemA;
            using var glowPen = new Pen(Color.FromArgb((int)(82 * stemA), PhosphorTheme.Patina), 5.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var shadowPen = new Pen(Color.FromArgb((int)(175 * stemA), PhosphorTheme.Ground), 4.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var beamPen = new Pen(Color.FromArgb(a, PhosphorTheme.Patina), 2.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var corePen = new Pen(Color.FromArgb((int)(150 * stemA), PhosphorTheme.EmblemCream), 1.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(glowPen, gx, -halfH, gx, halfH);
            g.DrawLine(shadowPen, gx, -halfH, gx, halfH);
            g.DrawLine(beamPen, gx, -halfH, gx, halfH);
            g.DrawLine(corePen, gx, -halfH + 1.2f, gx, halfH - 1.2f);
        }

        if (_current.ResizeShaftAlpha > 0.02f)
        {
            var st = g.Save();
            g.TranslateTransform(gx, gy);
            g.RotateTransform(_current.ResizeShaftAngleDeg - 90f);
            int a = (int)(220 * Math.Clamp(_current.ResizeShaftAlpha, 0f, 1f));
            using var shadowPen = new Pen(Color.FromArgb((int)(145 * _current.ResizeShaftAlpha), PhosphorTheme.Ground), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var shaftPen = new Pen(Color.FromArgb(a, PhosphorTheme.EmblemCream), 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, -8.2f, 0f, 8.2f, 0f);
            g.DrawLine(shaftPen, -8.2f, 0f, 8.2f, 0f);
            g.Restore(st);
        }

        if (_current.FourWayShaftAlpha > 0.02f)
        {
            int a = (int)(210 * Math.Clamp(_current.FourWayShaftAlpha, 0f, 1f));
            using var shadowPen = new Pen(Color.FromArgb((int)(140 * _current.FourWayShaftAlpha), PhosphorTheme.Ground), 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var shaftPen = new Pen(Color.FromArgb(a, PhosphorTheme.EmblemCream), 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(shadowPen, gx - 8.4f, gy, gx + 8.4f, gy);
            g.DrawLine(shadowPen, gx, gy - 8.4f, gx, gy + 8.4f);
            g.DrawLine(shaftPen, gx - 8.4f, gy, gx + 8.4f, gy);
            g.DrawLine(shaftPen, gx, gy - 8.4f, gx, gy + 8.4f);
        }

        if (_current.ReticleRingAlpha > 0.02f)
        {
            float ringR = Math.Max(grx, gry) + 4.0f;
            int a = (int)(195 * Math.Clamp(_current.ReticleRingAlpha, 0f, 1f));
            using var ringShadow = new Pen(Color.FromArgb((int)(130 * _current.ReticleRingAlpha), PhosphorTheme.Ground), 2.8f);
            using var ringPen = new Pen(Color.FromArgb(a, globeColor), 1.5f);
            g.DrawEllipse(ringShadow, gx - ringR, gy - ringR, ringR * 2f, ringR * 2f);
            g.DrawEllipse(ringPen, gx - ringR, gy - ringR, ringR * 2f, ringR * 2f);
        }

        // 3. Center Globe (fades gracefully into the solid Patina beam bar when CaretStemAlpha -> 1)
        float globeVisAlpha = 1.0f - Math.Clamp(_current.CaretStemAlpha * 0.92f, 0f, 0.92f);
        if (globeVisAlpha > 0.05f)
        {
            int ga = (int)(255 * globeVisAlpha);
            using (var shadowBrush = new SolidBrush(Color.FromArgb((int)(115 * globeVisAlpha), 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, gx - grx - 0.5f, gy - gry + 0.8f, (grx + 0.5f) * 2f, (gry + 0.5f) * 2f);
            }
            using (var outlinePen = new Pen(Color.FromArgb(ga, PhosphorTheme.Ground), 1.7f) { LineJoin = LineJoin.Round })
            {
                g.DrawEllipse(outlinePen, gx - grx, gy - gry, grx * 2f, gry * 2f);
            }
            using (var globeBrush = new SolidBrush(Color.FromArgb(ga, globeColor)))
            {
                g.FillEllipse(globeBrush, gx - grx, gy - gry, grx * 2f, gry * 2f);
            }
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

public enum WandSpellGestureKind
{
    ShakeHorizontal = 0, // TIME SPLITTER -> Prompt Wiz / Contextual HUD
    ShakeVertical = 1,   // COPYCAT       -> Free-Form Lasso / Snip Capture
    CircleLoop = 2,      // MUTATION      -> Cast GrimX Flow / Syndicate Order
    SweepRight = 3       // SYNTHESIZE    -> Open Holodex Drawer / Bridge
}

/// <summary>
/// Ported directly from the original Omniwand concept in MagiXLab Video Bench (ark/magixlab/video.html):
/// Holding Ctrl (or Right-Click Hold) arms the Omniwand gesture recognizer, rendering a 0.9s tapering
/// Bone/White ghost ribbon + stochastic 4-point twinkling star dust keyed to pointer acceleration,
/// and recognizing 4 loose-wristed spell gestures:
/// - Horizontal Shake (TIME SPLITTER)
/// - Vertical Shake (COPYCAT)
/// - Circle Loop (MUTATION)
/// - Unbroken Rightward Sweep (SYNTHESIZE)
/// On cast, fires a 48-spark omnidirectional starburst that chroma-shifts to the spell's Relic Phosphor color.
/// </summary>
internal static class OmniwandSpellGestureEngine
{
    private const float SpellSwingPx = 44f;
    private const int SpellSwingsRequired = 4;
    private const double SpellWindowSec = 0.90;
    private const double SpellCooldownSec = 0.75;
    private const double SpellCircleWindowSec = 1.40;
    private const double SpellTurnRadRequired = 5.2; // ~300 deg forgiving loop
    private const double SpellCirclePxRequired = 210.0;
    private const float SpellSwipePxRequired = 280f;

    public static bool Enabled { get; set; } = true;
    public static bool HoldCtrlToArm { get; set; } = true;
    public static bool HoldRightMouseToArm { get; set; } = false; // right-drag is the context menu; opt in from Settings

    private struct TrailNode
    {
        public float ScreenX;
        public float ScreenY;
        public double TimeSec;
    }

    private struct StarSpark
    {
        public float ScreenX;
        public float ScreenY;
        public float Vx;
        public float Vy;
        public float Radius;
        public float Ttl;
        public float Life;
        public float Rot;
        public float Spin;
        public float TwinkleFreq;
        public float TwinklePhase;
        public Color BaseColor;
        public Color? TargetChroma;
    }

    private struct SwingSample
    {
        public int Dir;
        public double TimeSec;
    }

    private struct TurnSample
    {
        public double TimeSec;
        public double DeltaAngleRad;
        public double SegmentPx;
    }

    private static readonly List<TrailNode> _trail = new(128);
    private static readonly List<StarSpark> _sparks = new(256);
    private static readonly List<SwingSample> _hSwings = new(16);
    private static readonly List<SwingSample> _vSwings = new(16);
    private static readonly List<TurnSample> _turns = new(96);
    private static readonly Random _rng = new();

    private static bool _isArmed;
    private static float _fade;
    private static float _lastX = float.NaN;
    private static float _lastY = float.NaN;
    private static float _vx;
    private static float _vy;
    private static float _speed;
    private static float _prevSpeed;
    private static double _cooldownUntilSec;
    private static double _quietSparksUntilSec;

    private static int _hDir;
    private static float _hRun;
    private static int _vDir;
    private static float _vRun;
    private static float _swipeRightAccum;
    private static double? _lastHeadingRad;

    public static bool HasVisuals => _fade > 0.02f || _trail.Count > 1 || _sparks.Count > 0;
    public static bool IsArmed => _isArmed;

    public static bool Step(
        Point cursorScreenPt,
        double nowSec,
        double dtSec,
        bool ctrlDown,
        bool rightMouseDown,
        out WandSpellGestureKind triggeredSpell)
    {
        triggeredSpell = default;
        dtSec = Math.Clamp(dtSec, 0.001, 0.05);

        bool armedNow = Enabled && ((HoldCtrlToArm && ctrlDown) || (HoldRightMouseToArm && rightMouseDown));
        if (armedNow && !_isArmed)
        {
            ResetGestureAccumulators();
        }
        else if (!armedNow && _isArmed)
        {
            ResetGestureAccumulators();
        }
        _isArmed = armedNow;

        float fadeRate = _isArmed ? 14f : 8f;
        _fade += ((_isArmed ? 1f : 0f) - _fade) * Math.Min(1f, (float)dtSec * fadeRate);

        float cx = cursorScreenPt.X;
        float cy = cursorScreenPt.Y;
        if (float.IsNaN(_lastX))
        {
            _lastX = cx;
            _lastY = cy;
        }

        float dx = cx - _lastX;
        float dy = cy - _lastY;
        _lastX = cx;
        _lastY = cy;

        float instVx = (float)(dx / dtSec);
        float instVy = (float)(dy / dtSec);
        _vx = _vx * 0.6f + instVx * 0.4f;
        _vy = _vy * 0.6f + instVy * 0.4f;
        _speed = (float)Math.Sqrt(_vx * _vx + _vy * _vy);
        float acc = (float)((_speed - _prevSpeed) / dtSec);
        _prevSpeed = _speed;

        // 1. Update 0.9s tapering Ghost Ribbon
        double cutoff = nowSec - 0.85;
        while (_trail.Count > 0 && _trail[0].TimeSec < cutoff)
        {
            _trail.RemoveAt(0);
        }

        if (_isArmed)
        {
            if (_trail.Count == 0 ||
                Math.Sqrt(Math.Pow(cx - _trail[^1].ScreenX, 2) + Math.Pow(cy - _trail[^1].ScreenY, 2)) > 1.8)
            {
                _trail.Add(new TrailNode { ScreenX = cx, ScreenY = cy, TimeSec = nowSec });
                if (_trail.Count > 96) _trail.RemoveAt(0);
            }

            // Stochastic star dust emission keyed to speed + acceleration
            if (nowSec > _quietSparksUntilSec && _speed > 160f && acc > -450f)
            {
                double odds = Math.Min(0.75, _speed / 1600.0) + (acc > 250f ? Math.Min(0.45, acc / 5500.0) : 0.0);
                if (_rng.NextDouble() < odds * dtSec * 55.0)
                {
                    SpawnTrailSparks(cx, cy, _rng.NextDouble() < 0.25 ? 2 : 1);
                }
            }
        }

        // 2. Step existing star dust & cast starburst sparks
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var p = _sparks[i];
            bool isBurst = p.TargetChroma.HasValue;
            p.Vy += (isBurst ? 320f : 95f) * (float)dtSec;
            float dragX = (float)Math.Pow(isBurst ? 0.14 : 0.05, dtSec);
            float dragY = (float)Math.Pow(isBurst ? 0.28 : 0.09, dtSec);
            p.Vx *= dragX;
            p.Vy *= dragY;
            p.ScreenX += p.Vx * (float)dtSec;
            p.ScreenY += p.Vy * (float)dtSec;
            p.Life -= (float)dtSec / p.Ttl;
            p.Rot += p.Spin * (float)dtSec;
            if (p.Life <= 0f)
            {
                _sparks.RemoveAt(i);
            }
            else
            {
                _sparks[i] = p;
            }
        }

        // 3. Evaluate spell gesture recognizers while armed
        if (!_isArmed || nowSec < _cooldownUntilSec || (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f))
        {
            return false;
        }

        // A. Shake recognizer (dominant axis per move so diagonal moves don't double-count)
        bool horizDominant = Math.Abs(dx) >= Math.Abs(dy);
        if (horizDominant)
        {
            int dir = dx > 0 ? 1 : -1;
            if (dir == _hDir || _hDir == 0) { _hRun += Math.Abs(dx); _hDir = dir; }
            else { _hRun = Math.Abs(dx); _hDir = dir; }

            if (_hRun >= SpellSwingPx && (_hSwings.Count == 0 || _hSwings[^1].Dir != dir))
            {
                _hSwings.Add(new SwingSample { Dir = dir, TimeSec = nowSec });
                _hRun = 0f;
                _hSwings.RemoveAll(s => nowSec - s.TimeSec > SpellWindowSec);
                if (_hSwings.Count >= SpellSwingsRequired)
                {
                    TriggerSpellCast(cx, cy, nowSec, WandSpellGestureKind.ShakeHorizontal, PhosphorTheme.Coral, out triggeredSpell);
                    return true;
                }
            }
        }
        else
        {
            int dir = dy > 0 ? 1 : -1;
            if (dir == _vDir || _vDir == 0) { _vRun += Math.Abs(dy); _vDir = dir; }
            else { _vRun = Math.Abs(dy); _vDir = dir; }

            if (_vRun >= SpellSwingPx && (_vSwings.Count == 0 || _vSwings[^1].Dir != dir))
            {
                _vSwings.Add(new SwingSample { Dir = dir, TimeSec = nowSec });
                _vRun = 0f;
                _vSwings.RemoveAll(s => nowSec - s.TimeSec > SpellWindowSec);
                if (_vSwings.Count >= SpellSwingsRequired)
                {
                    TriggerSpellCast(cx, cy, nowSec, WandSpellGestureKind.ShakeVertical, PhosphorTheme.Seal, out triggeredSpell);
                    return true;
                }
            }
        }

        // B. Circle loop recognizer (sum signed heading change over 1.4s window)
        double seg = Math.Sqrt(dx * dx + dy * dy);
        if (seg >= 3.5)
        {
            double hd = Math.Atan2(dy, dx);
            if (_lastHeadingRad.HasValue)
            {
                double dh = hd - _lastHeadingRad.Value;
                while (dh > Math.PI) dh -= Math.PI * 2.0;
                while (dh < -Math.PI) dh += Math.PI * 2.0;
                _turns.Add(new TurnSample { TimeSec = nowSec, DeltaAngleRad = dh, SegmentPx = seg });
            }
            _lastHeadingRad = hd;
            _turns.RemoveAll(t => nowSec - t.TimeSec > SpellCircleWindowSec);

            double sumRad = 0.0;
            double sumTravel = 0.0;
            for (int i = 0; i < _turns.Count; i++)
            {
                sumRad += _turns[i].DeltaAngleRad;
                sumTravel += _turns[i].SegmentPx;
            }

            if (Math.Abs(sumRad) >= SpellTurnRadRequired && sumTravel >= SpellCirclePxRequired)
            {
                TriggerSpellCast(cx, cy, nowSec, WandSpellGestureKind.CircleLoop, PhosphorTheme.Patina, out triggeredSpell);
                return true;
            }
        }

        // C. Unbroken Rightward Sweep (when Shift is also held or fast pure horizontal sweep)
        if (dx < -4f || Math.Abs(dy) > Math.Abs(dx) * 0.85f)
        {
            _swipeRightAccum = 0f;
        }
        else if (dx > 0f)
        {
            _swipeRightAccum += dx;
            if (_swipeRightAccum >= SpellSwipePxRequired && _hSwings.Count <= 1)
            {
                TriggerSpellCast(cx, cy, nowSec, WandSpellGestureKind.SweepRight, PhosphorTheme.Coral, out triggeredSpell);
                return true;
            }
        }

        return false;
    }

    private static void TriggerSpellCast(
        float cx,
        float cy,
        double nowSec,
        WandSpellGestureKind kind,
        Color chroma,
        out WandSpellGestureKind triggeredSpell)
    {
        triggeredSpell = kind;
        _cooldownUntilSec = nowSec + SpellCooldownSec;
        _quietSparksUntilSec = nowSec + 1.8;
        ResetGestureAccumulators();
        CastBurst(cx, cy, chroma);
    }

    private static void ResetGestureAccumulators()
    {
        _hSwings.Clear();
        _vSwings.Clear();
        _turns.Clear();
        _hDir = 0;
        _hRun = 0f;
        _vDir = 0;
        _vRun = 0f;
        _swipeRightAccum = 0f;
        _lastHeadingRad = null;
    }

    private static void SpawnTrailSparks(float sx, float sy, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double ke = Math.Min(_speed, 1800f) * (0.04 + _rng.NextDouble() * 0.05);
            double ang = Math.Atan2(_vy, _vx) + (_rng.NextDouble() - 0.5) * 0.9;
            Color hue = _rng.NextDouble() < 0.65
                ? Color.White
                : (_rng.NextDouble() < 0.5 ? PhosphorTheme.EmblemCream : PhosphorTheme.Patina);

            _sparks.Add(new StarSpark
            {
                ScreenX = sx + (float)(_rng.NextDouble() - 0.5) * 5f,
                ScreenY = sy + (float)(_rng.NextDouble() - 0.5) * 5f,
                Vx = (float)(-Math.Cos(ang) * ke + (_rng.NextDouble() - 0.5) * 32.0),
                Vy = (float)(-Math.Sin(ang) * ke + (_rng.NextDouble() - 0.5) * 32.0 - 14.0),
                Radius = _rng.NextDouble() < 0.8 ? (float)(0.7 + _rng.NextDouble() * 0.8) : (float)(1.3 + _rng.NextDouble() * 1.0),
                Ttl = (float)(0.65 + _rng.NextDouble() * 0.75),
                Life = 1f,
                Rot = (float)(_rng.NextDouble() * Math.PI * 2.0),
                Spin = (float)((_rng.NextDouble() - 0.5) * 3.0),
                TwinkleFreq = (float)(4.0 + _rng.NextDouble() * 8.0),
                TwinklePhase = (float)_rng.NextDouble(),
                BaseColor = hue,
                TargetChroma = null
            });
        }
        if (_sparks.Count > 180)
        {
            _sparks.RemoveRange(0, _sparks.Count - 180);
        }
    }

    public static void CastBurst(float sx, float sy, Color chroma)
    {
        _sparks.Clear();
        for (int i = 0; i < 42; i++)
        {
            double a = _rng.NextDouble() * Math.PI * 2.0;
            double v = 95.0 + _rng.NextDouble() * 240.0;
            _sparks.Add(new StarSpark
            {
                ScreenX = sx,
                ScreenY = sy,
                Vx = (float)(Math.Cos(a) * v),
                Vy = (float)(Math.Sin(a) * v - 48.0),
                Radius = (float)(1.2 + _rng.NextDouble() * 2.0),
                Ttl = (float)(0.65 + _rng.NextDouble() * 0.9),
                Life = 1f,
                Rot = (float)(_rng.NextDouble() * Math.PI * 2.0),
                Spin = (float)((_rng.NextDouble() - 0.5) * 4.5),
                TwinkleFreq = (float)(5.0 + _rng.NextDouble() * 7.0),
                TwinklePhase = (float)_rng.NextDouble(),
                BaseColor = _rng.NextDouble() < 0.7 ? Color.White : PhosphorTheme.EmblemCream,
                TargetChroma = chroma
            });
        }
    }

    public static void DrawInOverlay(
        Graphics g,
        PointF cursorScreenPt,
        float overlayCenterX,
        float overlayCenterY,
        double nowSec,
        float dpiScale)
    {
        if (!HasVisuals) return;

        float ox = overlayCenterX - cursorScreenPt.X;
        float oy = overlayCenterY - cursorScreenPt.Y;

        // 1. Tapering 0.85s Ghost Ribbon
        if (_trail.Count > 1)
        {
            for (int i = 1; i < _trail.Count; i++)
            {
                var p0 = _trail[i - 1];
                var p1 = _trail[i];
                float age = (float)(1.0 - (nowSec - p1.TimeSec) / 0.85);
                if (age <= 0.02f) continue;

                int alpha = Math.Clamp((int)Math.Round(age * age * 120f), 0, 255);
                Color col = age > 0.6f ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, PhosphorTheme.EmblemCream);
                float width = (0.6f + age * 2.6f) * dpiScale;
                using var pen = new Pen(col, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, p0.ScreenX + ox, p0.ScreenY + oy, p1.ScreenX + ox, p1.ScreenY + oy);
            }
        }

        // 2. 4-Point Twinkling Star Dust & Cast Starbursts
        for (int i = 0; i < _sparks.Count; i++)
        {
            var p = _sparks[i];
            float sx = p.ScreenX + ox;
            float sy = p.ScreenY + oy;
            if (sx < 4f || sy < 4f || sx > overlayCenterX * 2f - 4f || sy > overlayCenterY * 2f - 4f)
                continue;

            float tw = 0.55f + 0.45f * (float)Math.Sin(nowSec * p.TwinkleFreq + p.TwinklePhase * 6.2832f);
            float a = Math.Clamp(p.Life * p.Life * tw, 0f, 1f);
            if (a <= 0.02f) continue;

            Color col = p.BaseColor;
            if (p.TargetChroma.HasValue)
            {
                var tc = p.TargetChroma.Value;
                float t = 1f - Math.Clamp(p.Life, 0f, 1f);
                col = Color.FromArgb(
                    255,
                    (int)Math.Round(p.BaseColor.R + (tc.R - p.BaseColor.R) * t),
                    (int)Math.Round(p.BaseColor.G + (tc.G - p.BaseColor.G) * t),
                    (int)Math.Round(p.BaseColor.B + (tc.B - p.BaseColor.B) * t));
            }

            float r = p.Radius * (0.35f + 0.65f * p.Life) * dpiScale;
            var st = g.Save();
            g.TranslateTransform(sx, sy);
            g.RotateTransform(p.Rot * (180f / (float)Math.PI));

            int mainA = Math.Clamp((int)(a * 215f), 0, 255);
            int diagA = Math.Clamp((int)(a * 105f), 0, 255);
            int coreA = Math.Clamp((int)(a * 245f), 0, 255);

            using (var mainPen = new Pen(Color.FromArgb(mainA, col), Math.Max(0.6f, r * 0.42f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(mainPen, -r * 2.3f, 0f, r * 2.3f, 0f);
                g.DrawLine(mainPen, 0f, -r * 2.3f, 0f, r * 2.3f);
            }
            using (var diagPen = new Pen(Color.FromArgb(diagA, col), Math.Max(0.5f, r * 0.26f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(diagPen, -r * 0.9f, -r * 0.9f, r * 0.9f, r * 0.9f);
                g.DrawLine(diagPen, -r * 0.9f, r * 0.9f, r * 0.9f, -r * 0.9f);
            }
            using (var coreBrush = new SolidBrush(Color.FromArgb(coreA, Color.White)))
            {
                float cr = Math.Max(0.55f, r * 0.48f);
                g.FillEllipse(coreBrush, -cr, -cr, cr * 2f, cr * 2f);
            }

            g.Restore(st);
        }
    }
}
