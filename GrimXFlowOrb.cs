using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Omniwand;

public sealed record GrimXOrbSlot(
    int Index,
    string Id,
    string Title,
    string Verb,
    string Subtitle,
    string ZoneToken,
    Color AccentColor);

/// <summary>
/// 8-slot Spell Orb rendered in ArkStation Phosphor Shell tokens.
/// Zero Ev3-Orb or Omnidex references: strictly GrimX Spell kinds (Slots 1-6),
/// Wiz Crafting Chatbot (Slot 7), and AInbox Pod Intake + Handoff Scroll (Slot 8).
/// </summary>
public sealed class GrimXFlowOrb
{
    public static readonly GrimXOrbSlot[] Slots =
    {
        new(0, "spell",    "QUICK CAST", "CAST",  "Runs a Spell once",   "operate",    PhosphorTheme.Patina),
        new(1, "ritual",   "RITUAL",   "PERFORM", "Runs on a schedule",  "operate",    PhosphorTheme.Patina),
        new(2, "symphony", "SYMPHONY", "CONDUCT", "Spells in sequence",  "create",     PhosphorTheme.Coral),
        new(3, "ultima",   "ULTIMA",   "PURSUE",  "Works toward a goal", "distribute", PhosphorTheme.Seal),
        new(4, "charm",    "CHARM",    "BIND",    "Ties a Spell to a trigger","create",     PhosphorTheme.Coral),
        new(5, "summon",   "SUMMON",   "SUMMON",  "Calls one of your agents","operate",    PhosphorTheme.Patina),
        new(6, "crafting", "CRAFTING", "WIZ",     "Wiz on Nemotron",     "create",     PhosphorTheme.Coral),
        new(7, "ainbox",   "AINBOX",   "INTAKE",  "Sends captures as a handoff","distribute", PhosphorTheme.Seal)
    };

    public bool IsActive { get; private set; }
    public Point AnchorScreenPt { get; private set; }
    public int ActiveSlotIndex { get; private set; } = -1;
    public int LastDetentSlotIndex { get; private set; } = -1;

    private readonly CriticallyDampedSpring _openProgress = new(0.0, stiffness: 520);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    public void OpenAt(Point cursorScreenPt)
    {
        AnchorScreenPt = ClampToWorkArea(cursorScreenPt);
        if (AnchorScreenPt != cursorScreenPt)
        {
            // Keep the pointer on the orb center so releasing without moving
            // never resolves a slot, even when the orb was pushed off an edge.
            SetCursorPos(AnchorScreenPt.X, AnchorScreenPt.Y);
        }
        ActiveSlotIndex = -1;
        LastDetentSlotIndex = -1;
        IsActive = true;
        _openProgress.Target = 1.0;
    }

    /// <summary>
    /// Keeps the whole radial (ring plus the wide label tiles) inside the
    /// working area of the monitor under the cursor, clear of the taskbar.
    /// </summary>
    private static Point ClampToWorkArea(Point pt)
    {
        float scale = 1f;
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            scale = g.DpiX / 96f;
        }
        catch
        {
        }

        var area = System.Windows.Forms.Screen.FromPoint(pt).WorkingArea;
        int marginX = (int)Math.Ceiling((114f + 130f) * scale);
        int marginY = (int)Math.Ceiling((114f + 44f) * scale);

        int minX = area.Left + marginX;
        int maxX = area.Right - marginX;
        int minY = area.Top + marginY;
        int maxY = area.Bottom - marginY;
        if (minX > maxX) minX = maxX = area.Left + area.Width / 2;
        if (minY > maxY) minY = maxY = area.Top + area.Height / 2;

        return new Point(Math.Clamp(pt.X, minX, maxX), Math.Clamp(pt.Y, minY, maxY));
    }

    public GrimXOrbSlot? CloseAndResolve()
    {
        if (!IsActive) return null;
        IsActive = false;
        _openProgress.Target = 0.0;
        int idx = ActiveSlotIndex;
        ActiveSlotIndex = -1;
        return idx >= 0 && idx < Slots.Length ? Slots[idx] : null;
    }

    public void Cancel()
    {
        IsActive = false;
        ActiveSlotIndex = -1;
        _openProgress.Target = 0.0;
    }

    public bool UpdateCursor(Point currentScreenPt, double dtSec, out bool crossedWedgeDetent)
    {
        crossedWedgeDetent = false;
        _openProgress.Step(dtSec);

        if (!IsActive)
        {
            return _openProgress.Current > 0.01;
        }

        double dx = currentScreenPt.X - AnchorScreenPt.X;
        double dy = currentScreenPt.Y - AnchorScreenPt.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // Inner deadzone (center ArkMark core = 38px radius)
        if (dist < 38.0)
        {
            ActiveSlotIndex = -1;
            return true;
        }

        // Angle where 0 deg = North (up), clockwise in 45-degree wedges
        double deg = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
        if (deg < 0) deg += 360.0;

        int slot = (int)Math.Floor((deg + 22.5) / 45.0) % 8;
        ActiveSlotIndex = slot;

        if (slot != LastDetentSlotIndex)
        {
            LastDetentSlotIndex = slot;
            crossedWedgeDetent = true;
        }

        return true;
    }

    public bool IsVisible => IsActive || _openProgress.Current > 0.02;

    /// <summary>
    /// Draws the 8-slot Spell Orb in Phosphor Shell styling centered at (centerX, centerY).
    /// </summary>
    public void Draw(Graphics g, float centerX, float centerY, float dpiScale)
    {
        float p = (float)Math.Clamp(_openProgress.Current, 0.0, 1.0);
        if (p <= 0.02f) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        float ringRadius = 114f * dpiScale * (0.82f + 0.18f * p);
        float cardWidth = 96f * dpiScale;
        float cardHeight = 46f * dpiScale;
        int alpha = (int)Math.Clamp(p * 255f, 0, 255);

        // Outer subtle Phosphor orbit ring
        using (var orbitPen = new Pen(Color.FromArgb((int)(alpha * 0.45f), PhosphorTheme.Line2), 1.2f))
        {
            g.DrawEllipse(orbitPen, centerX - ringRadius, centerY - ringRadius, ringRadius * 2f, ringRadius * 2f);
        }

        using var verbFont = PhosphorFonts.Mono(7.5f / 0.75f * dpiScale);
        using var titleFont = PhosphorFonts.Ui(8.5f / 0.75f * dpiScale, heavy: true);
        using var subFont = PhosphorFonts.Body(6.8f / 0.75f * dpiScale);

        for (int i = 0; i < Slots.Length; i++)
        {
            var slot = Slots[i];
            bool selected = i == ActiveSlotIndex;

            // 0 = North, clockwise in 45-degree steps
            double rad = (i * 45.0 - 90.0) * (Math.PI / 180.0);
            float sx = centerX + (float)(Math.Cos(rad) * ringRadius);
            float sy = centerY + (float)(Math.Sin(rad) * ringRadius);

            float scale = selected ? 1.08f : 1.0f;
            float cw = cardWidth * scale;
            float ch = cardHeight * scale;
            var cardRect = new Rectangle((int)(sx - cw * 0.5f), (int)(sy - ch * 0.5f), (int)cw, (int)ch);

            // Spoke line from center to selected slot
            if (selected)
            {
                using var spokePen = new Pen(Color.FromArgb((int)(alpha * 0.75f), slot.AccentColor), 1.8f);
                g.DrawLine(spokePen, centerX, centerY, sx, sy);
            }

            // Phosphor tile surface (#121212 / #161616 when active)
            PhosphorTheme.DrawPhosphorPanel(
                g,
                cardRect,
                PhosphorTheme.RadiusTile,
                selected ? PhosphorTheme.Surface2 : PhosphorTheme.Surface,
                selected ? slot.AccentColor : PhosphorTheme.Line);

            // Veritas highlight-aura-rule: leading-edge 2px bar + 12px gap + right-fading bloom
            PhosphorTheme.DrawHighlightAura(
                g,
                cardRect,
                slot.AccentColor,
                selected ? 1.0f : 0.32f,
                PhosphorTheme.RadiusTile);

            int textLeft = cardRect.Left + PhosphorTheme.AuraLeadPx;
            using var verbBrush = new SolidBrush(slot.AccentColor);
            using var titleBrush = new SolidBrush(PhosphorTheme.Bone);
            using var subBrush = new SolidBrush(selected ? PhosphorTheme.Dim : PhosphorTheme.Ghost);

            g.DrawString($"{i + 1} · {slot.Verb}", verbFont, verbBrush, textLeft, cardRect.Top + 4 * dpiScale);
            g.DrawString(slot.Title, titleFont, titleBrush, textLeft, cardRect.Top + 15 * dpiScale);
            // The picked Spell names what this slot will cast; with no pick the slot keeps its kind's description.
            g.DrawString(SpellPicks.Get(slot.Id)?.Name ?? slot.Subtitle, subFont, subBrush, textLeft, cardRect.Top + 30 * dpiScale);
        }

        // Center Phosphor Core with ratified ArkMark emblem
        float coreSize = 54f * dpiScale;
        var coreRect = new RectangleF(centerX - coreSize * 0.5f, centerY - coreSize * 0.5f, coreSize, coreSize);
        PhosphorTheme.DrawArkMark(g, coreRect);

        if (ActiveSlotIndex >= 0 && ActiveSlotIndex < Slots.Length)
        {
            var active = Slots[ActiveSlotIndex];
            string badgeText = $"{active.Verb} · {active.Title}";
            var badgeSize = g.MeasureString(badgeText, verbFont);
            var badgeRect = new Rectangle(
                (int)(centerX - badgeSize.Width * 0.5f - 8),
                (int)(centerY + coreSize * 0.5f + 6),
                (int)(badgeSize.Width + 16),
                (int)(badgeSize.Height + 6));
            PhosphorTheme.DrawPhosphorPanel(g, badgeRect, PhosphorTheme.RadiusBadge, PhosphorTheme.Ground, active.AccentColor);
            using var activeBrush = new SolidBrush(PhosphorTheme.Bone);
            g.DrawString(badgeText, verbFont, activeBrush, badgeRect.Left + 8, badgeRect.Top + 3);
        }
    }
}
