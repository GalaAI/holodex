using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Phosphor Shell tray menu, composed like <see cref="PhosphorToast"/>: a rounded surface panel with a grey
/// state edge, an ArkMark header (<see cref="PhosphorMenuHeader"/>), tracked mono section eyebrows
/// (<see cref="PhosphorMenuEyebrow"/>), bone item text, shortcuts and details in a dim mono column, and the
/// coral Highlight Aura on hover. Rows tagged <see cref="DangerTag"/> light in rust. Applies to every submenu.
/// </summary>
internal sealed class PhosphorMenuRenderer : ToolStripProfessionalRenderer
{
    /// <summary>Tag for destructive rows (exit): rust text on hover.</summary>
    public const string DangerTag = "danger";

    /// <summary>Menu radius: --menu-radius is --radius-btn (packages/ui dropdown-menu.css).</summary>
    private static float RadiusPx => PhosphorTheme.RadiusButton;

    public PhosphorMenuRenderer() : base(new PhosphorColorTable())
    {
        RoundedEdges = false;
    }

    private static float ScaleOf(ToolStrip? strip) => (strip?.DeviceDpi ?? 96) / 96f;


    private sealed class PhosphorColorTable : ProfessionalColorTable
    {
        public PhosphorColorTable() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => PhosphorTheme.Surface;
        public override Color MenuBorder => PhosphorTheme.Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => PhosphorTheme.Surface2;
        public override Color ImageMarginGradientBegin => PhosphorTheme.Surface;
        public override Color ImageMarginGradientMiddle => PhosphorTheme.Surface;
        public override Color ImageMarginGradientEnd => PhosphorTheme.Surface;
        public override Color SeparatorDark => PhosphorTheme.Line;
        public override Color SeparatorLight => PhosphorTheme.Surface;
    }

    protected override void Initialize(ToolStrip toolStrip)
    {
        base.Initialize(toolStrip);
        if (toolStrip is not ToolStripDropDown dropDown) return;

        PhosphorFonts.EnsureLoaded();
        float s = ScaleOf(dropDown);
        // Submenus run at text-size-data so they stay narrow; the top menu keeps text-size-body.
        dropDown.Font = PhosphorFonts.Ui(dropDown.OwnerItem != null ? PhosphorTheme.TextData : PhosphorTheme.TextBody);
        dropDown.BackColor = PhosphorTheme.Surface;
        dropDown.ForeColor = PhosphorTheme.Bone;
        dropDown.DropShadowEnabled = false;
        if (dropDown is ToolStripDropDownMenu menu && dropDown.OwnerItem != null)
        {
            // WinForms locks a dropdown's padding, so the side margins come from its gutters: a 33px check
            // gutter only where some row can be checked; otherwise labels start 12px in (--aura-lead).
            void Fit(object? _, EventArgs __)
            {
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Checked || i.CheckOnClick);
            }
            menu.ItemAdded -= FitHandler(menu);
            menu.ItemAdded += FitHandler(menu, Fit);
            Fit(null, EventArgs.Empty);
        }
        dropDown.SizeChanged -= ClipToPanel;
        dropDown.SizeChanged += ClipToPanel;
        ClipToPanel(dropDown, EventArgs.Empty);
    }

    protected override void InitializeItem(ToolStripItem item)
    {
        base.InitializeItem(item);
        if (item is ToolStripMenuItem menuItem)
        {
            float s = ScaleOf(item.Owner);
            item.Padding = new Padding(0, (int)(5 * s), 0, (int)(5 * s));
            if (menuItem is not RotationPreviewItem && !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString))
                menuItem.ShortcutKeyDisplayString = ReserveShortcutColumn(menuItem);
        }
        // Submenus only inherit the manager renderer, which never runs Initialize on them, so they
        // missed the rounded clip and font. Assigning the renderer directly runs it.
        if (item is ToolStripDropDownItem parent && parent.DropDown.RenderMode != ToolStripRenderMode.Custom)
        {
            parent.DropDown.Renderer = this;
        }
    }

    /// <summary>
    /// Top of a text line whose visible letters (baseline to cap height) are centered in a row of
    /// <paramref name="rowHeight"/>, from the font's own metrics.
    /// </summary>
    private static float CenteredLineTop(Graphics g, Font font, int rowHeight)
    {
        var family = font.FontFamily;
        float linePx = font.GetHeight(g);
        float lineUnits = family.GetLineSpacing(font.Style);
        float ascentPx = linePx * family.GetCellAscent(font.Style) / lineUnits;
        float emPx = linePx * family.GetEmHeight(font.Style) / lineUnits;
        float glyphCenterFromTop = ascentPx - 0.36f * emPx;
        return rowHeight / 2f - glyphCenterFromTop;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDownMenu, ToolStripItemEventHandler> FitHandlers = new();

    private static ToolStripItemEventHandler FitHandler(ToolStripDropDownMenu menu, EventHandler? fit = null)
    {
        if (fit != null)
        {
            ToolStripItemEventHandler h = (o, e) => fit(o, e);
            FitHandlers.AddOrUpdate(menu, h);
            return h;
        }
        return FitHandlers.TryGetValue(menu, out var existing) ? existing : (_, _) => { };
    }

    private const char ColumnPad = ' '; // figure space

    /// <summary>
    /// WinForms sizes the shortcut column from the shortcut string in the row's label font, but the column is
    /// drawn in tracked uppercase mono, which runs wider and then overlapped the label. Pad the string with
    /// figure spaces until the measured column fits the tracked text; drawing trims the padding back off.
    /// </summary>
    private static string ReserveShortcutColumn(ToolStripMenuItem item)
    {
        string text = (item.ShortcutKeyDisplayString ?? string.Empty).TrimEnd(ColumnPad).ToUpperInvariant();
        var owner = item.Owner as ToolStripDropDown;
        using var rowFont = PhosphorFonts.Ui(owner?.OwnerItem != null ? PhosphorTheme.TextData : PhosphorTheme.TextBody);
        using var mono = PhosphorFonts.Mono(PhosphorTheme.TextData);
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        float tracking = mono.Size * 0.12f * g.DpiX / 72f;
        float need = PhosphorPaint.MeasureTracked(g, text, mono, tracking) + 4;
        if (owner?.OwnerItem == null && (item.Checked || item.CheckOnClick)) need += 24; // room for the check
        string reserved = text;
        while (TextRenderer.MeasureText(g, reserved, rowFont).Width < need && reserved.Length < text.Length + 24)
            reserved += ColumnPad;
        return reserved;
    }

    /// <summary>Clips the window to the panel shape so no square corners show (same as the toast).</summary>
    private static void ClipToPanel(object? sender, EventArgs e)
    {
        if (sender is not ToolStripDropDown dd || dd.Width <= 0 || dd.Height <= 0) return;
        using var path = PhosphorTheme.CreateRoundedRect(new RectangleF(0, 0, dd.Width, dd.Height), RadiusPx * ScaleOf(dd));
        var old = dd.Region;
        dd.Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        float s = ScaleOf(e.ToolStrip);
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width, e.ToolStrip.Height);
        // .rp-menu: surface fill, 1px line-2 border, --radius-btn corners, no edge strip (the strip is a toast and
        // card signal; on a menu it fused with the row side light).
        PhosphorPaint.DrawPanel(e.Graphics, bounds, RadiusPx * s, PhosphorTheme.Surface, PhosphorTheme.Line2, PhosphorTheme.Surface, s);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No separate icon gutter: the panel runs edge to edge.
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // DrawPanel strokes the rounded border with the background.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        // Highlight Aura Rule (packages/ui aura.css via PhosphorTheme.DrawHighlightAura): hovered rows, and a
        // parent row while its submenu is open, are lit from their leading edge (coral, rust on destructive rows).
        // Never a grey fill, an outline or a pill.
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;

        // The aura fills the whole row (aura.css), its side light on the row's leading edge just inside the
        // menu's 1px border. Labels are centered on the row in OnRenderItemText, so the two line up.
        int left = Math.Max(0, 1 - e.Item.Bounds.X);
        int right = e.Item.Width - 1;
        var bounds = new Rectangle(left, 1, Math.Max(8, right - left), Math.Max(4, e.Item.Height - 2));
        var sidelight = Equals(e.Item.Tag, DangerTag) ? PhosphorTheme.Rust : PhosphorTheme.Coral;
        PhosphorTheme.DrawHighlightAura(e.Graphics, bounds, sidelight, radius: PhosphorTheme.RadiusBadge);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        bool isShortcut = e.Item is ToolStripMenuItem mi
            && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
            && e.Text == mi.ShortcutKeyDisplayString;

        if (!isShortcut && e.Item is ToolStripMenuItem)
        {
            // WinForms places menu labels about 5px above the row's middle (its text box keeps descender room
            // below the baseline). Center the letters themselves on the row so labels sit level with the
            // aura, check mark and arrow.
            var r = e.TextRectangle;
            float top = CenteredLineTop(e.Graphics, e.TextFont ?? e.Item.Font, e.Item.Height);
            float lineTop = r.Top + (r.Height - (e.TextFont ?? e.Item.Font).GetHeight(e.Graphics)) / 2f;
            e.TextRectangle = new Rectangle(r.X, r.Y + (int)Math.Round(top - lineTop), r.Width, r.Height);
        }
        else if (e.Item is RotationPreviewItem preview)
        {
            // The rotation rows use the shortcut column for their animated cursor preview.
            preview.DrawPreview(e.Graphics, e.TextRectangle);
            return;
        }

        if (isShortcut)
        {
            // label-data style: uppercase JetBrains Mono at text-size-data, 0.12em tracking, right-aligned in the
            // column; ghost at rest, dim on the lit row. Drawn here because WinForms has no letter spacing.
            float s = ScaleOf(e.Item.Owner);
            using var mono = PhosphorFonts.Mono(PhosphorTheme.TextData);
            string text = (e.Text ?? string.Empty).TrimEnd(ColumnPad).ToUpperInvariant();
            float tracking = mono.Size * 0.12f * e.Graphics.DpiX / 72f;
            float width = PhosphorPaint.MeasureTracked(e.Graphics, text, mono, tracking) - tracking;
            // Flush with the submenu arrows' right edge, not short of the arrow column.
            float right = Math.Max(e.TextRectangle.Right - 2 * s, e.Item.Width - 14 * s);
            float x = right - width;
            float y = CenteredLineTop(e.Graphics, mono, e.Item.Height);
            using var ink = new SolidBrush(e.Item.Selected ? PhosphorTheme.Dim : PhosphorTheme.Ghost);
            PhosphorPaint.DrawTracked(e.Graphics, text, mono, ink, x, y, tracking);
            if (e.Item is ToolStripMenuItem toggle && toggle.Checked && HasNoCheckGutter(toggle))
                DrawCheck(e.Graphics, new RectangleF(x - 22 * s, 0, 16 * s, e.Item.Height), s);
            return;
        }
        else if (!e.Item.Enabled)
        {
            e.TextColor = PhosphorTheme.Ghost;
        }
        else if (e.Item.Selected && Equals(e.Item.Tag, DangerTag))
        {
            e.TextColor = PhosphorTheme.Rust;
        }
        else
        {
            e.TextColor = PhosphorTheme.Bone;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = ScaleOf(e.Item?.Owner);
        var r = e.ArrowRectangle;
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        var color = e.Item?.Selected == true ? PhosphorTheme.Bone : PhosphorTheme.Ghost;
        using var pen = new Pen(color, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 2f * s, cy - 4f * s), new PointF(cx + 2f * s, cy), new PointF(cx - 2f * s, cy + 4f * s) });
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var g = e.Graphics;
        DrawCheck(g, r, ScaleOf(e.Item?.Owner));
    }

    private static bool HasNoCheckGutter(ToolStripMenuItem item) =>
        item.Owner is ToolStripDropDownMenu m && !m.ShowCheckMargin && !m.ShowImageMargin;

    /// <summary>.rp-menu__indicator: a patina check centered in <paramref name="r"/>.</summary>
    private static void DrawCheck(Graphics g, RectangleF r, float s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(PhosphorTheme.Patina, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        g.DrawLines(pen, new[]
        {
            new PointF(cx - 4f * s, cy),
            new PointF(cx - 1f * s, cy + 3.5f * s),
            new PointF(cx + 4.5f * s, cy - 3.5f * s)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = ScaleOf(e.Item.Owner);
        using var pen = new Pen(PhosphorTheme.Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10 * s, y, e.Item.Width - 10 * s, y);
    }
}

/// <summary>Menu header in the toast's layout: ArkMark, tracked mono eyebrow, heavy title, dim subline.</summary>
internal sealed class PhosphorMenuHeader : ToolStripItem
{
    private readonly string _eyebrow;
    private readonly string _title;
    private readonly string _subline;

    public PhosphorMenuHeader(string eyebrow, string title, string subline)
    {
        _eyebrow = eyebrow;
        _title = title;
        _subline = subline;
    }

    public override bool CanSelect => false;

    private float Scale => (Owner?.DeviceDpi ?? 96) / 96f;

    public override Size GetPreferredSize(Size constrainingSize)
    {
        float s = Scale;
        using var eyebrowFont = PhosphorFonts.Eyebrow();
        using var titleFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
        using var subFont = PhosphorFonts.Body(PhosphorTheme.TextData);
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        float tracking = eyebrowFont.Size * 0.16f * g.DpiX / 72f;
        float text = Math.Max(PhosphorPaint.MeasureTracked(g, _eyebrow.ToUpperInvariant(), eyebrowFont, tracking),
            Math.Max(g.MeasureString(_title, titleFont, PointF.Empty, PhosphorPaint.Typographic).Width,
                     g.MeasureString(_subline, subFont, PointF.Empty, PhosphorPaint.Typographic).Width));
        return new Size((int)Math.Ceiling(44 * s + text + 12 * s), (int)(68 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = Scale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Custom rows start 4px left of where WinForms draws menu labels; indent to the label column.
        PhosphorTheme.DrawArkMark(g, new RectangleF(4 * s, 10 * s, 28 * s, 28 * s));

        using var eyebrowFont = PhosphorFonts.Eyebrow();
        using var titleFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
        using var subFont = PhosphorFonts.Body(PhosphorTheme.TextData);
        using var bone = new SolidBrush(PhosphorTheme.Bone);
        using var dim = new SolidBrush(PhosphorTheme.Dim);

        float x = 44 * s;
        float y = 8 * s;
        PhosphorPaint.DrawEyebrow(g, _eyebrow, PhosphorTheme.Ghost, x, y, s);
        y += eyebrowFont.GetHeight(g) + 3 * s;
        g.DrawString(_title, titleFont, bone, x - 1, y, PhosphorPaint.Typographic);
        y += titleFont.GetHeight(g) + 1 * s;
        g.DrawString(_subline, subFont, dim, x, y, PhosphorPaint.Typographic);
    }
}

/// <summary>
/// Frames a submenu: an eyebrow header on top and a spacer row at the bottom. ToolStripDropDownMenu locks
/// its own padding at 2px (assigning Padding is ignored), so the panel's top and bottom margins have to come
/// from items, or the first and last rows sit against the edge.
/// </summary>
internal static class PhosphorMenuFrame
{
    public static void Apply(ToolStripMenuItem parent, string eyebrow)
    {
        parent.DropDownItems.Insert(0, new PhosphorMenuEyebrow(eyebrow));
        parent.DropDownItems.Add(new PhosphorMenuSpacer());
    }
}

/// <summary>Bottom margin row for a menu panel (see <see cref="PhosphorMenuFrame"/>).</summary>
internal sealed class PhosphorMenuSpacer : ToolStripItem
{
    public override bool CanSelect => false;

    public override Size GetPreferredSize(Size constrainingSize)
    {
        float s = (Owner?.DeviceDpi ?? 96) / 96f;
        return new Size((int)(40 * s), (int)(6 * s));
    }
}

/// <summary>Section eyebrow: uppercase JetBrains Mono at the Omniwand eyebrow size and tracking, ghost.</summary>
internal sealed class PhosphorMenuEyebrow : ToolStripItem
{
    public PhosphorMenuEyebrow(string text) : base(text, null, null)
    {
    }

    public override bool CanSelect => false;

    private float Scale => (Owner?.DeviceDpi ?? 96) / 96f;

    public override Size GetPreferredSize(Size constrainingSize)
    {
        float s = Scale;
        // Wide enough for the tracked label, so a long header ("GRIMX FLOW // RING SLOTS") is never clipped.
        using var font = PhosphorFonts.Eyebrow();
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        string text = (Text ?? string.Empty).ToUpperInvariant();
        float tracking = font.Size * 0.16f * g.DpiX / 72f;
        int width = (int)Math.Ceiling(PhosphorPaint.MeasureTracked(g, text, font, tracking) + 12 * s);
        return new Size(Math.Max((int)(120 * s), width), (int)(26 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = Scale;
        using var font = PhosphorFonts.Eyebrow();
        float y = Height - font.GetHeight(e.Graphics) - 5 * s;
        PhosphorPaint.DrawEyebrow(e.Graphics, Text ?? string.Empty, PhosphorTheme.Ghost, 4 * s, y, s);
    }
}
