using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
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

    /// <summary>Row width set by <see cref="PhosphorMenuLayout"/> (the panel's label column); null sizes to content.</summary>
    public int? ColumnWidth { get; set; }

    private float Scale => (Owner?.DeviceDpi ?? 96) / 96f;

    /// <summary>
    /// Sized by the eyebrow and title only. The subline (the detected mouse name, which can run long) never
    /// widens the menu: inside the column <see cref="PhosphorMenuLayout"/> gives the panel it breaks onto a
    /// second line (at its " · " separator, else between words), and ellipsizes only what still does not fit.
    /// </summary>
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
            g.MeasureString(_title, titleFont, PointF.Empty, PhosphorPaint.Typographic).Width);
        int width = ColumnWidth ?? (int)Math.Ceiling(44 * s + text + 12 * s);
        int lines = SublineLines(g, subFont, width - 56 * s).Length;
        return new Size(width, (int)(68 * s + (lines - 1) * subFont.GetHeight(g)));
    }

    /// <summary>
    /// The subline on one line when it fits in <paramref name="room"/>, else split at its last " · ", else at the
    /// last space that keeps the first line whole.
    /// </summary>
    private string[] SublineLines(Graphics g, Font font, float room)
    {
        bool Fits(string text) => g.MeasureString(text, font, PointF.Empty, PhosphorPaint.Typographic).Width <= room;
        if (Fits(_subline)) return new[] { _subline };
        int cut = _subline.LastIndexOf(" · ", StringComparison.Ordinal);
        if (cut > 0) return new[] { _subline[..cut], _subline[(cut + 3)..] };
        for (int space = _subline.LastIndexOf(' '); space > 0; space = _subline.LastIndexOf(' ', space - 1))
            if (Fits(_subline[..space])) return new[] { _subline[..space], _subline[(space + 1)..] };
        return new[] { _subline };
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
        using var trim = new StringFormat(PhosphorPaint.Typographic) { Trimming = StringTrimming.EllipsisCharacter };
        trim.FormatFlags = (trim.FormatFlags | StringFormatFlags.NoWrap) & ~StringFormatFlags.NoClip;
        float room = Math.Max(0, Width - x - 12 * s);
        foreach (string line in SublineLines(g, subFont, room))
        {
            g.DrawString(line, subFont, dim, new RectangleF(x, y, room, subFont.GetHeight(g) + 2 * s), trim);
            y += subFont.GetHeight(g);
        }
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

/// <summary>
/// Bottom margin row for a menu panel (see <see cref="PhosphorMenuFrame"/>). It also carries the panel's column
/// width: a dropdown sizes its label column to its widest non-menu row, so <see cref="PhosphorMenuLayout"/> sets
/// <see cref="ColumnWidth"/> to land each panel on its content width with the header fitted to it.
/// </summary>
internal sealed class PhosphorMenuSpacer : ToolStripItem
{
    /// <summary>Row width in pixels set by <see cref="PhosphorMenuLayout"/>; null keeps the default 40px.</summary>
    public int? ColumnWidth { get; set; }

    public override bool CanSelect => false;

    public override Size GetPreferredSize(Size constrainingSize)
    {
        float s = (Owner?.DeviceDpi ?? 96) / 96f;
        return new Size(ColumnWidth ?? (int)(40 * s), (int)(6 * s));
    }
}

/// <summary>
/// Tray menu geometry, one rule for the main menu and every submenu under it:
/// <list type="number">
/// <item>Width: every panel is as wide as its own content and no wider, because the root has to sit left of the
/// tray by the whole width of its deepest submenu chain: one shared column made every panel in that chain as wide
/// as the widest. The header never widens a panel; it breaks and ellipsizes its subline instead.</item>
/// <item>Direction: every submenu opens to the right (<see cref="ToolStripDropDownDirection.Right"/>). WinForms'
/// default direction flips a submenu left whenever the screen edge pushes it over its parent row.</item>
/// <item>Placement: a root menu opened near the right screen edge (the tray) moves left until its deepest
/// submenu chain fits on the right (<see cref="PlaceRootLeft"/>), so no submenu is pushed back over the menu.</item>
/// </list>
/// </summary>
internal static class PhosphorMenuLayout
{
    private const int ProbeWidth = 4096;

    /// <summary>Each fixed-size hosted control's own width, before the column stretched it.</summary>
    private static readonly ConditionalWeakTable<ToolStripControlHost, StrongBox<int>> HostWidths = new();

    /// <summary>What each tree looked like when it was last sized (see <see cref="Signature"/>).</summary>
    private static readonly ConditionalWeakTable<ToolStripDropDown, string> Applied = new();

    /// <summary>
    /// Applies the width and direction rules to <paramref name="root"/> and every submenu under it. Sizing lays
    /// every panel out several times, so a tree whose rows have not changed since the last call is left as is.
    /// </summary>
    public static void Apply(ToolStripDropDown root)
    {
        var panels = new List<ToolStripDropDown>();
        Collect(root, panels);

        foreach (var panel in panels)
        {
            // Submenus inherit only the manager renderer, which never initializes them (fonts, clip); give them
            // the root's renderer before measuring, as PhosphorMenuRenderer.InitializeItem does when it runs.
            if (panel != root && panel.RenderMode != ToolStripRenderMode.Custom && root.RenderMode == ToolStripRenderMode.Custom)
                panel.Renderer = root.Renderer;
            foreach (var item in panel.Items.OfType<ToolStripDropDownItem>())
                if (item.HasDropDownItems) item.DropDownDirection = ToolStripDropDownDirection.Right;
        }

        string signature = Signature(panels);
        if (Applied.TryGetValue(root, out var last) && last == signature) return;

        // Measure with AutoSize off: the passes below read preferred sizes only, and letting each pass resize
        // the panel windows (and re-clip their regions) cost 20-30 ms per panel. One resize happens at the end.
        var autoSized = panels.Where(p => p.AutoSize).ToList();
        foreach (var panel in autoSized) panel.AutoSize = false;
        try
        {
            foreach (var panel in panels)
            {
                // The panel's width is its own content's (its spacer at zero, so content alone decides).
                SetStrut(panel, 0, fitHeaders: false);
                int width = PreferredWidth(panel);

                // A panel is its label column plus fixed chrome (check gutter, paddings, arrow column). Probe the
                // chrome with a wide spacer, then size the spacer, and any header, so the panel lands on its width.
                SetStrut(panel, ProbeWidth, fitHeaders: false);
                int chrome = PreferredWidth(panel) - ProbeWidth;
                SetStrut(panel, Math.Max(0, width - chrome), fitHeaders: true);
            }
        }
        finally
        {
            foreach (var panel in autoSized) panel.AutoSize = true;
        }
        Applied.AddOrUpdate(root, signature);
    }

    /// <summary>Everything that changes a panel's width: its DPI and font, and each row's text, shortcut and check.</summary>
    private static string Signature(List<ToolStripDropDown> panels)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var panel in panels)
        {
            sb.Append(panel.DeviceDpi).Append('|').Append(panel.Font.Size).Append('|').Append(panel.Items.Count).Append('{');
            foreach (ToolStripItem item in panel.Items)
            {
                sb.Append(item.Available ? '1' : '0').Append(item.Text).Append('\u0001');
                if (item is ToolStripMenuItem mi) sb.Append(mi.ShortcutKeyDisplayString).Append(mi.Checked ? '+' : '-').Append(mi.CheckOnClick ? 'c' : ' ');
                sb.Append('\u0002');
            }
            sb.Append('}');
        }
        return sb.ToString();
    }

    /// <summary>
    /// How far right of <paramref name="panel"/>'s left edge its deepest submenu chain reaches, with every submenu
    /// opening to the right of its row (a submenu's left edge is its row's right edge).
    /// </summary>
    public static int Reach(ToolStripDropDown panel, int? panelWidth = null)
    {
        int reach = panelWidth ?? PreferredWidth(panel);
        foreach (var item in panel.Items.OfType<ToolStripDropDownItem>())
        {
            if (!item.Available || !item.HasDropDownItems) continue;
            reach = Math.Max(reach, item.Bounds.Right + Reach(item.DropDown));
        }
        return reach;
    }

    /// <summary>
    /// Left edge for a root menu that wants <paramref name="desiredLeft"/>: unchanged when its submenu chain
    /// (<paramref name="reach"/>) fits inside <paramref name="workArea"/>, otherwise moved left just far enough.
    /// </summary>
    public static int PlaceRootLeft(int desiredLeft, int reach, Rectangle workArea) =>
        Math.Max(workArea.Left, Math.Min(desiredLeft, workArea.Right - reach));

    private static void Collect(ToolStripDropDown panel, List<ToolStripDropDown> into)
    {
        into.Add(panel);
        foreach (var item in panel.Items.OfType<ToolStripDropDownItem>())
            if (item.HasDropDownItems) Collect(item.DropDown, into);
    }

    private static int PreferredWidth(ToolStripDropDown panel) => panel.GetPreferredSize(Size.Empty).Width;

    private static void SetStrut(ToolStripDropDown panel, int width, bool fitHeaders)
    {
        var strut = panel.Items.OfType<PhosphorMenuSpacer>().LastOrDefault();
        if (strut == null)
        {
            strut = new PhosphorMenuSpacer();
            panel.Items.Add(strut);
        }
        strut.ColumnWidth = width;
        // A header spans the label column so its subline can use it, and a fixed-size hosted control (the DPI
        // slider) stretches across it; while measuring, both keep their own content width.
        foreach (var header in panel.Items.OfType<PhosphorMenuHeader>())
            header.ColumnWidth = fitHeaders ? width : null;
        foreach (var host in panel.Items.OfType<ToolStripControlHost>().Where(h => !h.AutoSize))
        {
            int own = HostWidths.GetValue(host, h => new StrongBox<int>(h.Width)).Value;
            host.Width = fitHeaders ? Math.Max(own, width - host.Margin.Horizontal) : own;
        }
        // A dropdown reads its non-menu rows' widths from their bounds of the previous layout, so lay out twice:
        // once to size the spacer row, once to size the label column from it.
        panel.PerformLayout();
        panel.PerformLayout();
    }
}

/// <summary>
/// The tray's context menu. Applies <see cref="PhosphorMenuLayout"/> each time it opens (row text and checks
/// change), and keeps itself far enough left of the screen edge that every submenu opens to its right.
/// </summary>
internal sealed class PhosphorTrayMenu : ContextMenuStrip
{
    /// <summary>
    /// The bottom-right corner the menu was last asked to open at (NotifyIcon opens it above and left of the
    /// pointer), so a width change while opening keeps the menu on the pointer.
    /// </summary>
    private Point? _anchor;
    private bool _applying;

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        _applying = true;
        try { PhosphorMenuLayout.Apply(this); }
        finally { _applying = false; }
        if (_anchor is Point anchor && Location != new Point(anchor.X - Width, anchor.Y - Height))
            Location = new Point(anchor.X - Width, anchor.Y - Height);
        base.OnOpening(e);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (OwnerItem == null && Items.Count > 0 && !_applying)
        {
            if ((specified & BoundsSpecified.Location) != 0) _anchor = new Point(x + width, y + height);
            var workArea = Screen.FromRectangle(new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height))).WorkingArea;
            x = PhosphorMenuLayout.PlaceRootLeft(x, PhosphorMenuLayout.Reach(this, width), workArea);
        }
        base.SetBoundsCore(x, y, width, height, specified);
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
        // Wide enough for the tracked label, so a long header ("SPELL ORB // RING SLOTS") is never clipped.
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
