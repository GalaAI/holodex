using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Omniwand;

/// <summary>
/// Port of the ArkStation GhostTrailer Flow cubic Bezier ribbon engine into Omniwand's
/// VSync-locked GDI+ renderer, styled in ArkStation Phosphor Shell tokens.
/// Context-Aware Dual Mode:
/// - Over IBeam (text fields): Dictation mode (Patina #79C4B7) — types/pastes text on release.
/// - Over any other cursor role: Voice-Wand mode (Coral #D98A63) — sends spoken command + cursor context to ArkStation.
/// </summary>
public sealed class GhostTrailerEngine
{
    private const int MaxChars = 34;
    private const double MaxTiltDeg = 62.0;
    private static readonly float[] GhostLayerAlphas = { 0.18f, 0.11f, 0.06f, 0.03f };

    private readonly List<PointF> _ribbonPoints = new(64);
    private readonly List<PointF[]> _ghostLayers = new(4);

    public bool IsStreaming { get; private set; }
    public bool IsDictationMode { get; private set; }
    public string LiveTranscript { get; private set; } = string.Empty;
    public Point StartCursorPos { get; private set; }
    public string StartCursorRole { get; private set; } = "Arrow";

    private double _streamStartSec;
    private double _smoothedTangentDeg;

    public void BeginStream(Point cursorPos, string cursorRole, double nowSec)
    {
        IsStreaming = true;
        StartCursorPos = cursorPos;
        StartCursorRole = cursorRole;
        IsDictationMode = string.Equals(cursorRole, "IBeam", StringComparison.OrdinalIgnoreCase);
        _streamStartSec = nowSec;
        _smoothedTangentDeg = 0.0;

        _ribbonPoints.Clear();
        for (int i = 0; i < 24; i++)
        {
            _ribbonPoints.Add(new PointF(cursorPos.X - i * 8f, cursorPos.Y));
        }

        _ghostLayers.Clear();
        for (int g = 0; g < GhostLayerAlphas.Length; g++)
        {
            _ghostLayers.Add(_ribbonPoints.ToArray());
        }

        LiveTranscript = IsDictationMode
            ? "Listening for dictation..."
            : "Listening for Voice-Wand command...";
    }

    public void AppendTranscript(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            LiveTranscript = text.Trim();
        }
    }

    public void AppendCharacter(char c)
    {
        if (!IsStreaming) return;
        if (LiveTranscript.StartsWith("Listening ", StringComparison.Ordinal))
        {
            LiveTranscript = string.Empty;
        }
        if (c == '\b')
        {
            if (LiveTranscript.Length > 0)
                LiveTranscript = LiveTranscript[..^1];
        }
        else if (!char.IsControl(c))
        {
            LiveTranscript += c;
        }
    }

    public (bool WasDictation, string Transcript, Point Origin, string Role) EndStream()
    {
        IsStreaming = false;
        string final = LiveTranscript.StartsWith("Listening ", StringComparison.Ordinal)
            ? string.Empty
            : LiveTranscript.Trim();
        return (IsDictationMode, final, StartCursorPos, StartCursorRole);
    }

    public void StepRibbon(PointF currentTip, double dtSec)
    {
        if (!IsStreaming || _ribbonPoints.Count == 0) return;

        // Propagate lagged ghost layers from back to front (GhostTrailer _draw_flow_trail)
        for (int g = _ghostLayers.Count - 1; g >= 1; g--)
        {
            _ghostLayers[g] = (PointF[])_ghostLayers[g - 1].Clone();
        }
        _ghostLayers[0] = _ribbonPoints.ToArray();

        // Advance leading point and relax trailing spine along cubic pursuit curve
        _ribbonPoints[0] = currentTip;
        const float targetSpacing = 9.5f;
        double pull = 1.0 - Math.Exp(-dtSec * 28.0);

        for (int i = 1; i < _ribbonPoints.Count; i++)
        {
            PointF prev = _ribbonPoints[i - 1];
            PointF cur = _ribbonPoints[i];
            float dx = cur.X - prev.X;
            float dy = cur.Y - prev.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 0.001f)
            {
                float targetX = prev.X + (dx / dist) * targetSpacing;
                float targetY = prev.Y + (dy / dist) * targetSpacing;
                _ribbonPoints[i] = new PointF(
                    cur.X + (float)((targetX - cur.X) * pull),
                    cur.Y + (float)((targetY - cur.Y) * pull));
            }
        }
    }

    /// <summary>
    /// Renders the GhostTrailer cubic Bezier ribbon and character stream inside the local overlay canvas.
    /// </summary>
    public void DrawInOverlay(Graphics g, PointF tipScreen, float overlayCenterX, float overlayCenterY, float dpiScale)
    {
        if (!IsStreaming || _ribbonPoints.Count < 4) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color modeAccent = IsDictationMode ? PhosphorTheme.Patina : PhosphorTheme.Coral;

        // Transform screen ribbon coordinates to overlay-local coordinates
        PointF ToLocal(PointF screenPt) => new(
            overlayCenterX + (screenPt.X - tipScreen.X),
            overlayCenterY + (screenPt.Y - tipScreen.Y));

        // 1. Draw 4 lagged Ghostlayers with Phosphor accent glow
        for (int layerIdx = _ghostLayers.Count - 1; layerIdx >= 0; layerIdx--)
        {
            var pts = _ghostLayers[layerIdx];
            if (pts.Length < 4) continue;
            PointF[] localPts = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++) localPts[i] = ToLocal(pts[i]);

            int alpha = (int)Math.Clamp(GhostLayerAlphas[layerIdx] * 255f, 4, 90);
            using var ribbonPen = new Pen(Color.FromArgb(alpha, modeAccent), (4.5f - layerIdx * 0.7f) * dpiScale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawCurve(ribbonPen, localPts, 0.45f);
        }

        // 2. Draw primary Phosphor ribbon spine
        PointF[] mainLocal = new PointF[_ribbonPoints.Count];
        for (int i = 0; i < _ribbonPoints.Count; i++) mainLocal[i] = ToLocal(_ribbonPoints[i]);
        using (var corePen = new Pen(Color.FromArgb(145, modeAccent), 1.8f * dpiScale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawCurve(corePen, mainLocal, 0.45f);
        }

        // 3. Draw mode badge pill + trailing characters with quadratic tail fade (1 - i/N)^1.35
        string display = LiveTranscript.Length > MaxChars
            ? LiveTranscript[^MaxChars..]
            : LiveTranscript;

        using var badgeFont = new Font("Consolas", 7.5f * dpiScale, FontStyle.Bold);
        using var charFont = new Font("Segoe UI Semibold", 9.5f * dpiScale, FontStyle.Bold);

        string modeTag = IsDictationMode ? "GHOSTTRAILER · DICTATE [IBEAM]" : $"GHOSTTRAILER · WAND [{StartCursorRole.ToUpperInvariant()}]";
        var tagSize = g.MeasureString(modeTag, badgeFont);
        var tagRect = new Rectangle(
            (int)(overlayCenterX + 16 * dpiScale),
            (int)(overlayCenterY - 28 * dpiScale),
            (int)(tagSize.Width + 18),
            (int)(tagSize.Height + 6));

        PhosphorTheme.DrawPhosphorPanel(g, tagRect, PhosphorTheme.RadiusBadge, PhosphorTheme.Surface, modeAccent);
        PhosphorTheme.DrawHighlightAura(g, tagRect, modeAccent, 0.9f, PhosphorTheme.RadiusBadge);
        using (var tagBrush = new SolidBrush(PhosphorTheme.Bone))
        {
            g.DrawString(modeTag, badgeFont, tagBrush, tagRect.Left + 10, tagRect.Top + 3);
        }

        // Render characters along ribbon with clamped tilt (MAX_TILT = 62 deg) and (1 - i/N)^1.35 fade
        int count = Math.Min(display.Length, _ribbonPoints.Count - 2);
        for (int i = 0; i < count; i++)
        {
            char ch = display[display.Length - 1 - i];
            PointF p0 = mainLocal[Math.Min(i + 1, mainLocal.Length - 1)];
            PointF p1 = mainLocal[Math.Max(0, i)];

            double rawDeg = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * (180.0 / Math.PI);
            if (rawDeg > 90) rawDeg -= 180;
            if (rawDeg < -90) rawDeg += 180;
            double clampedTilt = Math.Clamp(rawDeg, -MaxTiltDeg, MaxTiltDeg);
            _smoothedTangentDeg = _smoothedTangentDeg * 0.7 + clampedTilt * 0.3;

            double fade = Math.Pow(1.0 - (double)i / Math.Max(1, MaxChars), 1.35);
            int charAlpha = (int)Math.Clamp(fade * 245.0, 20, 255);

            var state = g.Save();
            g.TranslateTransform(p0.X, p0.Y - 12f * dpiScale);
            g.RotateTransform((float)_smoothedTangentDeg);
            using var shadowBrush = new SolidBrush(Color.FromArgb((int)(charAlpha * 0.75), PhosphorTheme.Ground));
            using var boneBrush = new SolidBrush(Color.FromArgb(charAlpha, PhosphorTheme.Bone));
            string s = ch.ToString();
            g.DrawString(s, charFont, shadowBrush, 1f, 1f);
            g.DrawString(s, charFont, boneBrush, 0f, 0f);
            g.Restore(state);
        }
    }
}
