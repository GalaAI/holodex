using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Phosphor Shell primitives for the summoned widgets. Every color, radius, spacing step and type size resolves
/// through <see cref="PhosphorTheme"/>, which reads PhosphorTokens.cs (generated from packages/tokens). Component
/// shapes follow packages/ui (Button, Badge, TextField, DropdownMenu, Eyebrow) and the Augment Inventory catalog
/// (cat-panel, CP-20 SpecCard). Fonts come from packages/tokens/assets/fonts (JetBrains Mono, Archivo Black);
/// Inter is used when installed, Segoe UI otherwise.
/// </summary>
internal static class PhosphorFonts
{
    private static readonly PrivateFontCollection Collection = new();
    private static FontFamily? _mono;
    private static FontFamily? _display;
    private static FontFamily? _ui;       // Inter SemiBold (600): nav, labels, menu rows
    private static FontFamily? _uiHeavy;  // Inter ExtraBold (800): titles, buttons
    private static FontFamily? _body;     // Space Grotesk 400: prose
    private static bool _loaded;

    public static string UiFamily { get; private set; } = "Segoe UI";
    public static bool MonoLoaded => _mono != null;
    public static bool DisplayLoaded => _display != null;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // Next to Omniwand.dll (dist or obj\stage), so a host that loads the assembly finds them too.
        string dir = Path.Combine(Path.GetDirectoryName(typeof(PhosphorFonts).Assembly.Location) ?? AppContext.BaseDirectory, "fonts");
        TryAdd(Path.Combine(dir, "JetBrainsMono-Regular.ttf"));
        TryAdd(Path.Combine(dir, "ArchivoBlack-Regular.ttf"));
        // The design system's UI and body faces (Google Fonts static instances, OFL), bundled so the app
        // never falls back to Segoe UI. GDI+ names each static weight as its own family.
        TryAdd(Path.Combine(dir, "Inter-SemiBold.ttf"));
        TryAdd(Path.Combine(dir, "Inter-ExtraBold.ttf"));
        TryAdd(Path.Combine(dir, "SpaceGrotesk-Regular.ttf"));
        foreach (var fam in Collection.Families)
        {
            if (fam.Name.StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase)) _mono = fam;
            else if (fam.Name.StartsWith("Archivo", StringComparison.OrdinalIgnoreCase)) _display = fam;
            else if (fam.Name.Equals("Inter SemiBold", StringComparison.OrdinalIgnoreCase)) _ui = fam;
            else if (fam.Name.Equals("Inter ExtraBold", StringComparison.OrdinalIgnoreCase)) _uiHeavy = fam;
            else if (fam.Name.Equals("Space Grotesk", StringComparison.OrdinalIgnoreCase)) _body = fam;
        }
        if (_ui != null) UiFamily = _ui.Name;
    }

    private static void TryAdd(string path)
    {
        try { if (File.Exists(path)) Collection.AddFontFile(path); } catch { }
    }

    /// <summary>CSS px at 96 dpi to points; GDI+ scales points with the control's DPI.</summary>
    public static float Pt(float px) => Math.Max(1f, px) * 0.75f;

    private static Font FromFamily(FontFamily family, float pt, params FontStyle[] styles)
    {
        foreach (var style in styles)
        {
            if (family.IsStyleAvailable(style)) return new Font(family, pt, style, GraphicsUnit.Point);
        }
        return new Font(family, pt, GraphicsUnit.Point);
    }

    public static Font Mono(float px)
    {
        EnsureLoaded();
        return _mono != null ? FromFamily(_mono, Pt(px), FontStyle.Regular) : new Font("Consolas", Pt(px), GraphicsUnit.Point);
    }

    public static Font Display(float px)
    {
        EnsureLoaded();
        return _display != null
            ? FromFamily(_display, Pt(px), FontStyle.Regular, FontStyle.Bold)
            : new Font("Segoe UI Black", Pt(px), GraphicsUnit.Point);
    }

    /// <summary>--font-ui: Inter 600, or 800 when <paramref name="heavy"/>. Segoe UI only if the files are missing.</summary>
    public static Font Ui(float px, bool heavy = false)
    {
        EnsureLoaded();
        var family = heavy ? _uiHeavy : _ui;
        return family != null
            ? FromFamily(family, Pt(px), FontStyle.Regular)
            : new Font("Segoe UI", Pt(px), heavy ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>--font-body: Space Grotesk 400 for prose.</summary>
    public static Font Body(float px)
    {
        EnsureLoaded();
        return _body != null ? FromFamily(_body, Pt(px), FontStyle.Regular) : new Font("Segoe UI", Pt(px), GraphicsUnit.Point);
    }

    /// <summary>The eyebrow face: mono at the Omniwand eyebrow size (CLAUDE.md rule 6).</summary>
    public static Font Eyebrow() => Mono(PhosphorTheme.EyebrowPx);
}

internal static class PhosphorPaint
{
    public static readonly StringFormat Typographic = (StringFormat)StringFormat.GenericTypographic.Clone();

    public static float Scale(Control c) => c.DeviceDpi / 96f;

    /// <summary>Draws text with letter spacing (CSS letter-spacing has no WinForms equivalent).</summary>
    public static float DrawTracked(Graphics g, string text, Font font, Brush brush, float x, float y, float trackingPx)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float cx = x;
        float space = SpaceWidth(g, font);
        foreach (char ch in text)
        {
            if (ch == ' ') { cx += space + trackingPx; continue; }
            string s = ch.ToString();
            var size = g.MeasureString(s, font, PointF.Empty, Typographic);
            g.DrawString(s, font, brush, cx, y, Typographic);
            cx += size.Width + trackingPx;
        }
        return cx - x;
    }

    public static float MeasureTracked(Graphics g, string text, Font font, float trackingPx)
    {
        float w = 0;
        float space = SpaceWidth(g, font);
        foreach (char ch in text)
        {
            w += (ch == ' ' ? space : g.MeasureString(ch.ToString(), font, PointF.Empty, Typographic).Width) + trackingPx;
        }
        return w;
    }

    /// <summary>Eyebrow: uppercase, eyebrow face, tracked, at (x, y). Returns the drawn width.</summary>
    public static float DrawEyebrow(Graphics g, string text, Color color, float x, float y, float scale)
    {
        using var font = PhosphorFonts.Eyebrow();
        using var brush = new SolidBrush(color);
        return DrawTracked(g, text.ToUpperInvariant(), font, brush, x, y, PhosphorTheme.EyebrowTracking(scale));
    }

    /// <summary>GenericTypographic measures a lone space as zero; derive the advance from a padded pair.</summary>
    private static float SpaceWidth(Graphics g, Font font)
    {
        float two = g.MeasureString("n n", font, PointF.Empty, Typographic).Width;
        float one = g.MeasureString("nn", font, PointF.Empty, Typographic).Width;
        float w = two - one;
        return w > 0.5f ? w : font.Size * 0.45f;
    }

    /// <summary>cat-panel: surface fill, 1px line border, panel-edge 3px inset bar on the left.</summary>
    public static void DrawPanel(Graphics g, Rectangle bounds, float radius, Color fill, Color border, Color edge, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1f, bounds.Height - 1f);
        using var path = PhosphorTheme.CreateRoundedRect(rect, radius);
        using var fillBrush = new SolidBrush(fill);
        g.FillPath(fillBrush, path);

        var prev = g.Clip;
        g.SetClip(path, CombineMode.Intersect);
        using (var edgeBrush = new SolidBrush(edge))
        {
            g.FillRectangle(edgeBrush, bounds.X, bounds.Y, 3f * scale, bounds.Height);
        }
        using (var hairline = new Pen(Color.FromArgb(9, 255, 255, 255), 1f))
        {
            g.DrawLine(hairline, bounds.X + radius, bounds.Y + 1, bounds.Right - radius, bounds.Y + 1);
        }
        g.Clip = prev;

        using var borderPen = new Pen(border, 1f);
        g.DrawPath(borderPen, path);
    }

    public static void FillRounded(Graphics g, RectangleF rect, float radius, Color fill)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = PhosphorTheme.CreateRoundedRect(rect, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
    }

    public static void StrokeRounded(Graphics g, RectangleF rect, float radius, Color stroke, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = PhosphorTheme.CreateRoundedRect(rect, radius);
        using var p = new Pen(stroke, width);
        g.DrawPath(p, path);
    }

    /// <summary>The first opaque background up the parent chain, for controls that paint their own corners.</summary>
    public static Color ResolveBackColor(Control? c)
    {
        for (var p = c; p != null; p = p.Parent)
        {
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return PhosphorTheme.Surface;
    }

    /// <summary>
    /// Clips a window to a rounded rectangle so no square corners or light ears show around a rounded panel
    /// (the toast and the tray menu do the same). Re-run on resize and DPI change.
    /// </summary>
    public static void ClipRounded(Control c, float radiusPx)
    {
        if (c.Width <= 0 || c.Height <= 0) return;
        using var path = PhosphorTheme.CreateRoundedRect(new RectangleF(0, 0, c.Width, c.Height), radiusPx);
        var old = c.Region;
        c.Region = new Region(path);
        old?.Dispose();
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>Uses the system dark theme for native scrollbars (Windows' own DarkMode_Explorer), not grey ones.</summary>
    public static void UseDarkScrollbars(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    public static void UseDarkScrollbars(IntPtr hwnd) => SetWindowTheme(hwnd, "DarkMode_Explorer", null);
}

/// <summary>Eyebrow: uppercase JetBrains Mono, Omniwand eyebrow size and tracking, ghost. Route copy uses //.</summary>
internal sealed class EyebrowLabel : Control
{
    private Color? _accent;

    public EyebrowLabel(string text)
    {
        Text = text;
        Height = 18;
        Dock = DockStyle.Top;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
    }

    public Color? Accent
    {
        get => _accent;
        set { _accent = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = PhosphorPaint.Scale(this);
        using var font = PhosphorFonts.Eyebrow();
        PhosphorPaint.DrawEyebrow(e.Graphics, Text, _accent ?? PhosphorTheme.Ghost, 0, (Height - font.GetHeight(e.Graphics)) / 2f, s);
    }
}

/// <summary>Mono data label (text-size-data, dim) for telemetry lines.</summary>
internal sealed class DataLabel : Control
{
    public DataLabel(string text)
    {
        Text = text;
        Height = 18;
        Dock = DockStyle.Top;
        ForeColor = PhosphorTheme.Dim;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var font = PhosphorFonts.Mono(PhosphorTheme.TextData);
        using var brush = new SolidBrush(ForeColor);
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var rect = new RectangleF(0, 0, Width, Height);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        e.Graphics.DrawString(Text, font, brush, rect, fmt);
    }
}

internal enum PhosphorButtonVariant { Default, Primary, Danger }

/// <summary>Button contract (packages/ui button.css): uppercase Inter 800 at text-size-btn, 0.16em, touch-min tall, radius-btn. One primary per region, filled bone, never ember.</summary>
internal sealed class PhosphorButton : Control
{
    private const float TrackingEm = 0.16f; // button.css letter-spacing
    private bool _hover;
    private bool _down;

    public PhosphorButton(string text, PhosphorButtonVariant variant = PhosphorButtonVariant.Default)
    {
        Text = text;
        Variant = variant;
        Height = PhosphorTheme.TouchMin;
        Width = 140;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
    }

    public PhosphorButtonVariant Variant { get; set; }

    /// <summary>Glyph mode draws the text as a single large glyph (the close ×) with no tracking.</summary>
    public bool Glyph { get; set; }

    private bool _selected;
    /// <summary>Selected tab or toggle: Default variant with bone ink and the highlight aura sidelight, never a bone fill.</summary>
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); Focus(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = PhosphorPaint.Scale(this);
        var g = e.Graphics;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = PhosphorTheme.RadiusButton * s;

        Color fill, border, ink;
        switch (Variant)
        {
            case PhosphorButtonVariant.Primary:
                fill = _down ? PhosphorTheme.Plate : PhosphorTheme.Bone;
                border = PhosphorTheme.Bone;
                ink = PhosphorTheme.Ground;
                break;
            case PhosphorButtonVariant.Danger:
                fill = _down ? Color.FromArgb(40, PhosphorTheme.Rust) : PhosphorTheme.Surface2;
                border = PhosphorTheme.Rust;
                ink = PhosphorTheme.Rust;
                break;
            default:
                fill = _down ? PhosphorTheme.Line : PhosphorTheme.Surface2;
                border = _hover || Selected ? PhosphorTheme.Ghost : PhosphorTheme.Line2;
                ink = _hover || Selected ? PhosphorTheme.Bone : PhosphorTheme.Dim;
                break;
        }
        if (!Enabled)
        {
            fill = PhosphorTheme.Surface2;
            border = PhosphorTheme.Line;
            ink = PhosphorTheme.Ghost;
        }

        if (_hover && Enabled && Variant != PhosphorButtonVariant.Default)
        {
            // bloom-bone or bloom-rust on hover (Law 05: bloom is for interaction)
            var bloom = Variant == PhosphorButtonVariant.Primary ? PhosphorTheme.Bone : PhosphorTheme.Rust;
            for (int i = 3; i >= 1; i--)
            {
                var halo = RectangleF.Inflate(rect, i * 2f * s, i * 2f * s);
                PhosphorPaint.StrokeRounded(g, halo, radius + i * 2f * s, Color.FromArgb(18 / i, bloom), 1.5f);
            }
        }

        PhosphorPaint.FillRounded(g, rect, radius, fill);
        if (Selected && Variant == PhosphorButtonVariant.Default)
            PhosphorTheme.DrawHighlightAura(g, Rectangle.Round(rect), PhosphorTheme.Coral, 1f, (int)Math.Round(radius));
        PhosphorPaint.StrokeRounded(g, rect, radius, border);

        if (Focused && !_hover)
        {
            PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), Math.Max(1f, radius - 2f), Color.FromArgb(160, PhosphorTheme.Patina));
        }

        using var inkBrush = new SolidBrush(ink);
        if (Glyph)
        {
            using var gf = new Font("Segoe UI", PhosphorFonts.Pt(20f), FontStyle.Regular, GraphicsUnit.Point);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.DrawString(Text, gf, inkBrush, rect, fmt);
            return;
        }

        float px = PhosphorTheme.TextButton;
        using var font = PhosphorFonts.Ui(px, heavy: true);
        string label = Text.ToUpperInvariant();
        float tracking = px * TrackingEm * s;
        float w = PhosphorPaint.MeasureTracked(g, label, font, tracking) - tracking;
        float h = font.GetHeight(g);
        PhosphorPaint.DrawTracked(g, label, font, inkBrush, (Width - w) / 2f, (Height - h) / 2f, tracking);
    }
}

internal enum PhosphorBadgeState { Unstated, Validated, Proven, Blocked, Accent }

/// <summary>Badge contract (packages/ui badge.css): mono text-size-badge, 0.12em, radius-badge outline. Unstated ghost, validated patina outline, proven filled patina, blocked rust.</summary>
internal sealed class PhosphorBadge : Control
{
    private const float TrackingEm = 0.12f; // badge.css letter-spacing
    private PhosphorBadgeState _state;
    private Color? _accent;

    public PhosphorBadge(string text, PhosphorBadgeState state = PhosphorBadgeState.Unstated)
    {
        Text = text;
        _state = state;
        Height = 22;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Resize += (_, _) => Invalidate();
    }

    public PhosphorBadgeState State
    {
        get => _state;
        set { _state = value; Invalidate(); }
    }

    public Color AccentColor
    {
        get => _accent ?? PhosphorTheme.Coral;
        set { _accent = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        FitToText();
        Invalidate();
    }

    public void FitToText()
    {
        float s = PhosphorPaint.Scale(this);
        using var g = CreateGraphics();
        float px = PhosphorTheme.TextBadge;
        using var font = PhosphorFonts.Mono(px);
        float w = PhosphorPaint.MeasureTracked(g, Text.ToUpperInvariant(), font, px * TrackingEm * s);
        Width = (int)Math.Ceiling(w + 2f * PhosphorTheme.S2 * s);
        Height = (int)Math.Ceiling(22f * s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = PhosphorPaint.Scale(this);
        var g = e.Graphics;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = PhosphorTheme.RadiusBadge * s;
        Color line = _state switch
        {
            PhosphorBadgeState.Validated => Color.FromArgb(102, PhosphorTheme.Patina),
            PhosphorBadgeState.Proven => PhosphorTheme.Patina,
            PhosphorBadgeState.Blocked => Color.FromArgb(115, PhosphorTheme.Rust),
            PhosphorBadgeState.Accent => AccentColor,
            _ => PhosphorTheme.Line2
        };
        Color ink = _state switch
        {
            PhosphorBadgeState.Proven => PhosphorTheme.Ground,
            PhosphorBadgeState.Validated => PhosphorTheme.Patina,
            PhosphorBadgeState.Blocked => PhosphorTheme.Rust,
            PhosphorBadgeState.Accent => AccentColor,
            _ => PhosphorTheme.Ghost
        };
        if (_state == PhosphorBadgeState.Proven)
        {
            PhosphorPaint.FillRounded(g, rect, radius, PhosphorTheme.Patina);
        }
        PhosphorPaint.StrokeRounded(g, rect, radius, line);
        float px = PhosphorTheme.TextBadge;
        using var font = PhosphorFonts.Mono(px);
        using var brush = new SolidBrush(ink);
        float h = font.GetHeight(g);
        PhosphorPaint.DrawTracked(g, Text.ToUpperInvariant(), font, brush, PhosphorTheme.S2 * s, (Height - h) / 2f, px * TrackingEm * s);
    }
}

/// <summary>
/// TextField (packages/ui text-field.css): surface-2 box inside a line-2 border at radius-btn, ghost on hover,
/// patina while focused. The host paints its own rounded fill on a transparent background, so no square corners
/// show outside the border. A borderless TextBox sits inside.
/// </summary>
internal sealed class PhosphorField : Panel
{
    private readonly bool _mono;
    private readonly bool _multiline;
    private bool _hover;

    public TextBox Box { get; }

    public PhosphorField(string placeholder, bool multiline, int heightPx, bool mono = false)
    {
        _mono = mono;
        _multiline = multiline;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Height = heightPx;
        BackColor = Color.Transparent;
        Padding = new Padding(10, multiline ? 8 : 0, 10, multiline ? 8 : 0);

        Box = new TextBox
        {
            Multiline = multiline,
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Dock = multiline ? DockStyle.Fill : DockStyle.None,
            ScrollBars = ScrollBars.None
        };
        ApplyStyle();
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        Box.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        Controls.Add(Box);
        if (!multiline)
        {
            Box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            Layout += (_, _) =>
            {
                Box.Width = ClientSize.Width - Padding.Horizontal;
                Box.Left = Padding.Left;
                Box.Top = Math.Max(0, (ClientSize.Height - Box.Height) / 2);
            };
        }
        PhosphorPaint.UseDarkScrollbars(Box);
        Click += (_, _) => Box.Focus();
    }

    private void ApplyStyle()
    {
        Box.BackColor = PhosphorTheme.Surface2;
        Box.ForeColor = PhosphorTheme.Bone;
        Box.Font = _mono ? PhosphorFonts.Mono(PhosphorTheme.TextData) : PhosphorFonts.Body(PhosphorTheme.TextBody);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Math.Min(PhosphorTheme.RadiusButton * s, Height / 2f);
        PhosphorPaint.FillRounded(e.Graphics, rect, radius, PhosphorTheme.Surface2);
        var border = Box.Focused ? PhosphorTheme.Patina : (_hover ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
        PhosphorPaint.StrokeRounded(e.Graphics, rect, radius, border);
    }
}

/// <summary>
/// Phosphor select (cat-tab styling; DropdownMenu colors from packages/ui): the closed box is painted entirely by
/// us, a surface-2 rounded field with a line-2 border, mono value and a drawn chevron, so no system drop button
/// shows. The drop list keeps owner-drawn rows, a line-2 frame and the system dark scrollbar.
/// </summary>
internal class PhosphorComboBox : ComboBox
{
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PRINTCLIENT = 0x0318;
    private const int WM_NCPAINT = 0x0085;

    private bool _hover;
    private DropListFrame? _listFrame;

    public PhosphorComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = PhosphorTheme.Surface2;
        ForeColor = PhosphorTheme.Bone;
        Font = PhosphorFonts.Mono(PhosphorTheme.TextData);
        ItemHeight = (int)Math.Ceiling(24f * PhosphorPaint.Scale(this));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
    protected override void OnDropDownClosed(EventArgs e) { Invalidate(); base.OnDropDownClosed(e); }

    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        if (GetComboBoxInfo(Handle, ref info) && info.hwndList != IntPtr.Zero)
        {
            if (_listFrame == null || _listFrame.Handle != info.hwndList)
            {
                _listFrame?.ReleaseHandle();
                _listFrame = new DropListFrame();
                _listFrame.AssignHandle(info.hwndList);
                PhosphorPaint.UseDarkScrollbars(info.hwndList);
            }
        }
        Invalidate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _listFrame?.ReleaseHandle();
        _listFrame = null;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_PAINT:
                {
                    var ps = new PAINTSTRUCT();
                    IntPtr hdc = BeginPaint(Handle, ref ps);
                    try
                    {
                        using var g = Graphics.FromHdc(hdc);
                        PaintBuffered(g);
                    }
                    finally
                    {
                        EndPaint(Handle, ref ps);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
            case WM_PRINTCLIENT:
                using (var g = Graphics.FromHdc(m.WParam)) PaintBuffered(g);
                m.Result = IntPtr.Zero;
                return;
            case WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;
        }
        base.WndProc(ref m);
    }

    private void PaintBuffered(Graphics target)
    {
        if (Width <= 0 || Height <= 0) return;
        using var bmp = new Bitmap(Width, Height);
        using (var g = Graphics.FromImage(bmp)) PaintClosed(g);
        target.DrawImageUnscaled(bmp, 0, 0);
    }

    /// <summary>The closed select: parent background, rounded box, value, chevron.</summary>
    protected virtual void PaintClosed(Graphics g)
    {
        float s = PhosphorPaint.Scale(this);
        g.Clear(PhosphorPaint.ResolveBackColor(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Math.Min(PhosphorTheme.RadiusButton * s, Height / 2f);
        PhosphorPaint.FillRounded(g, rect, radius, PhosphorTheme.Surface2);
        var border = !Enabled ? PhosphorTheme.Line
            : (DroppedDown || Focused) ? PhosphorTheme.Patina
            : _hover ? PhosphorTheme.Ghost
            : PhosphorTheme.Line2;
        PhosphorPaint.StrokeRounded(g, rect, radius, border);

        float chevronW = 10f * s;
        float right = Width - PhosphorTheme.S3 * s;
        float cx = right - chevronW / 2f;
        float cy = Height / 2f;
        var ink = !Enabled ? PhosphorTheme.Ghost : (_hover || DroppedDown ? PhosphorTheme.Bone : PhosphorTheme.Dim);
        using (var pen = new Pen(ink, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float dy = DroppedDown ? -1f : 1f;
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4f * s, cy - 2f * s * dy),
                new PointF(cx, cy + 2f * s * dy),
                new PointF(cx + 4f * s, cy - 2f * s * dy)
            });
        }

        string text = SelectedIndex >= 0 ? (Items[SelectedIndex]?.ToString() ?? string.Empty) : string.Empty;
        float left = PhosphorTheme.S3 * s;
        var textRect = new RectangleF(left, 0, Math.Max(0, right - chevronW - PhosphorTheme.S2 * s - left), Height);
        using var brush = new SolidBrush(Enabled ? PhosphorTheme.Bone : PhosphorTheme.Ghost);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.DrawString(text.ToUpperInvariant(), Font, brush, textRect, fmt);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? PhosphorTheme.Line : PhosphorTheme.Surface2);
        e.Graphics.FillRectangle(bg, e.Bounds);
        if (selected)
        {
            using var edge = new SolidBrush(PhosphorTheme.Coral);
            e.Graphics.FillRectangle(edge, e.Bounds.X, e.Bounds.Y, 2, e.Bounds.Height);
        }
        string text = Items[e.Index]?.ToString() ?? string.Empty;
        using var brush = new SolidBrush(selected ? PhosphorTheme.Bone : PhosphorTheme.Dim);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float pad = PhosphorTheme.S3 * PhosphorPaint.Scale(this);
        e.Graphics.DrawString(text.ToUpperInvariant(), Font, brush, new RectangleF(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad - 2, e.Bounds.Height), fmt);
    }

    /// <summary>Paints the drop list's 1px frame in line-2 instead of the system border.</summary>
    private sealed class DropListFrame : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCPAINT) return;
            IntPtr hdc = GetWindowDC(Handle);
            if (hdc == IntPtr.Zero) return;
            try
            {
                GetWindowRect(Handle, out RECT r);
                using var g = Graphics.FromHdc(hdc);
                using var pen = new Pen(PhosphorTheme.Line2, 1f);
                g.DrawRectangle(pen, 0, 0, r.Right - r.Left - 1, r.Bottom - r.Top - 1);
            }
            finally
            {
                ReleaseDC(Handle, hdc);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndItem;
        public IntPtr hwndList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO pcbi);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}

/// <summary>
/// Boolean entry (packages/ui text-field.css .rp-field--checkbox): an s4 box at radius u in line-2, filled bone
/// with a ground check when on (accent-color bone), ghost border on hover, patina focus ring. Label in the UI face
/// at text-size-data, dim. Fully owner-drawn, so no native grey glyph shows.
/// </summary>
internal sealed class PhosphorCheckBox : CheckBox
{
    private bool _hover;

    public PhosphorCheckBox(string text)
    {
        Text = text;
        Height = 26;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // Opaque on the panel surface it always sits on. A transparent owner-drawn checkbox made WinForms
        // repaint the parent behind it and left stale pixels (a ghost Craft Flow button in Wiz).
        BackColor = PhosphorTheme.Surface;
        Font = PhosphorFonts.Ui(PhosphorTheme.TextData);
        ForeColor = PhosphorTheme.Dim;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Paint the whole row first. Without this the double buffer keeps stale pixels: a black strip, or a
        // ghost of whatever the parent last drew there (the Wiz starter buttons on hover).
        g.Clear(BackColor.A == 255 ? BackColor : (Parent?.BackColor is { A: 255 } pb ? pb : PhosphorTheme.Surface));
        float s = PhosphorPaint.Scale(this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float box = PhosphorTheme.S4 * s;
        var r = new RectangleF(1f * s, (Height - box) / 2f, box, box);
        float radius = PhosphorTheme.U * s;
        bool on = Checked;

        if (Focused && ShowFocusCues)
        {
            PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(r, 2.5f * s, 2.5f * s), radius + 2.5f * s, PhosphorTheme.Patina, 1.5f * s);
        }
        // Disabled reads at reduced strength (text-field.css: opacity 0.4).
        var onFill = Enabled ? PhosphorTheme.Bone : PhosphorTheme.Ghost;
        PhosphorPaint.FillRounded(g, r, radius, on ? onFill : PhosphorTheme.Surface2);
        var border = on ? onFill : (_hover ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
        PhosphorPaint.StrokeRounded(g, r, radius, Enabled || on ? border : PhosphorTheme.Line);
        if (on)
        {
            using var pen = new Pen(PhosphorTheme.Ground, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4f * s, cy),
                new PointF(cx - 1.2f * s, cy + 3f * s),
                new PointF(cx + 4f * s, cy - 3f * s)
            });
        }

        float tx = r.Right + PhosphorTheme.S2 * s;
        using var brush = new SolidBrush(!Enabled ? PhosphorTheme.Ghost : (_hover ? PhosphorTheme.Bone : ForeColor));
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.DrawString(Text, Font, brush, new RectangleF(tx, 0, Math.Max(0, Width - tx), Height), fmt);
    }
}

/// <summary>
/// Shared chrome for a summoned widget: panel with edge, header (ArkMark, eyebrow, title, trailing badge),
/// a real close button, Esc on every path, drag by passive surfaces, and the window clipped to the rounded
/// panel.
/// </summary>
internal static class WidgetChrome
{
    /// <summary>Frame inset: s5 sides and bottom, s3 top (header sits close to the edge).</summary>
    public static Padding FramePadding(float scale = 1f)
    {
        int side = (int)Math.Round(PhosphorTheme.S5 * scale);
        return new Padding(side, (int)Math.Round(PhosphorTheme.S3 * scale), side, side);
    }

    private static Icon? _appIcon;

    /// <summary>The aperture app icon (assets\omniwand.ico, copied beside the exe), loaded once, all sizes.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIcon != null) return _appIcon;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "omniwand.ico");
                _appIcon = File.Exists(path) ? new Icon(path) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { _appIcon = null; }
            return _appIcon;
        }
    }

    public static Panel Attach(Form form, string eyebrow, string title, Color accent, Control? trailing)
    {
        float s = PhosphorPaint.Scale(form);
        // Taskbar, Alt+Tab and the title-bar icon use the aperture app icon, not the WinForms default.
        if (AppIcon != null) form.Icon = AppIcon;
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)Math.Round(60 * s),
            BackColor = Color.Transparent,
            Name = $"{form.Name}.Header"
        };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            float sc = PhosphorPaint.Scale(header);
            PhosphorTheme.DrawArkMark(g, new RectangleF(0, 6 * sc, 32 * sc, 32 * sc));
            using var titleFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
            using var bone = new SolidBrush(PhosphorTheme.Bone);
            float x = 44 * sc;
            PhosphorPaint.DrawEyebrow(g, eyebrow, PhosphorTheme.Ghost, x, 4 * sc, sc);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.DrawString(title, titleFont, bone, x - 1, 20 * sc, PhosphorPaint.Typographic);
        };

        var close = new PhosphorButton("×") { Glyph = true, Name = $"{form.Name}.CloseButton", TabStop = false };
        close.Size = new Size((int)Math.Round(36 * s), (int)Math.Round(36 * s));
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => form.Hide();
        header.Controls.Add(close);

        if (trailing != null)
        {
            trailing.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(trailing);
        }

        void Place()
        {
            close.Location = new Point(header.ClientSize.Width - close.Width, (int)Math.Round(4 * s));
            if (trailing != null)
            {
                trailing.Location = new Point(close.Left - trailing.Width - (int)Math.Round(10 * s), (int)Math.Round(11 * s));
            }
        }
        header.Resize += (_, _) => Place();
        header.HandleCreated += (_, _) => Place();
        if (trailing != null) trailing.SizeChanged += (_, _) => Place();
        Place();

        form.Controls.Add(header);
        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                form.Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        ClipToFrame(form);
        foreach (Control c in Descendants(form))
        {
            if (c is TextBoxBase) PhosphorPaint.UseDarkScrollbars(c);
        }
        return header;
    }

    /// <summary>Math.Clamp that tolerates min greater than max (a widget taller than the screen) instead of throwing.</summary>
    public static int SafeClamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);

    /// <summary>
    /// Places a widget near the cursor inside the working area. A widget taller or wider than the screen
    /// (768p laptops, 125 percent scaling) is shrunk to fit first, so placement never throws.
    /// </summary>
    public static Point PlaceNear(Form form, Rectangle work, int x, int y)
    {
        if (form.Height > work.Height - 24) form.Height = Math.Max(200, work.Height - 24);
        if (form.Width > work.Width - 24) form.Width = Math.Max(200, work.Width - 24);
        return new Point(
            SafeClamp(x, work.Left + 12, work.Right - form.Width - 12),
            SafeClamp(y, work.Top + 12, work.Bottom - form.Height - 12));
    }

    /// <summary>Keeps a borderless widget clipped to its rounded frame so its square corners never show.</summary>
    public static void ClipToFrame(Form form)
    {
        form.SizeChanged += (_, _) => ClipRegion(form);
        form.HandleCreated += (_, _) => ClipRegion(form);
        form.DpiChanged += (_, _) => ClipRegion(form);
        ClipRegion(form);
    }

    private static void ClipRegion(Form form) => PhosphorPaint.ClipRounded(form, PhosphorTheme.RadiusWidget * PhosphorPaint.Scale(form));

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    public static void PaintFrame(Form form, Graphics g, Color edge)
    {
        float s = PhosphorPaint.Scale(form);
        PhosphorPaint.DrawPanel(g, new Rectangle(0, 0, form.Width, form.Height), PhosphorTheme.RadiusWidget * s, PhosphorTheme.Surface, PhosphorTheme.Line, edge, s);
    }

    /// <summary>Eyebrow above a control, stacked with Dock.Top.</summary>
    public static Panel Labeled(string eyebrow, Control body, int bodyHeightPx, int gapPx = 4)
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = Color.Transparent };
        var label = new EyebrowLabel(eyebrow) { Dock = DockStyle.Top, Height = 18 };
        body.Dock = DockStyle.Top;
        body.Height = bodyHeightPx;
        host.Height = 18 + gapPx + bodyHeightPx;
        host.Controls.Add(body);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = gapPx, BackColor = Color.Transparent });
        host.Controls.Add(label);
        host.Tag = label;
        return host;
    }

    public static Panel Spacer(int px) => new() { Dock = DockStyle.Top, Height = px, BackColor = Color.Transparent };
}

/// <summary>
/// CP-20 SpecCard (plain name: Ticket) from the Augment Inventory catalog: code headline, tear line with
/// real notches, spec grid, barcode band, receipt line; the body below is the editable form. Painted on a
/// transparent background so the rounded corners and the clipped ear show the surface behind, not a square.
/// </summary>
internal sealed class SpecCardPanel : Panel
{
    private const int HeadPx = 184;

    public string Eyebrow { get; set; } = "Augment // Scroll // Handoff";
    public string Code { get; set; } = "SC01";
    public string Title { get; set; } = "Handoff Scroll";
    public string Alt { get; set; } = "Plain alternative // Work order";
    public string Status { get; set; } = "Draft";
    public PhosphorBadgeState StatusState { get; set; } = PhosphorBadgeState.Unstated;
    public string Board { get; set; } = "Board // Scribe";
    public (string Label, string Value)[] Specs { get; set; } = Array.Empty<(string, string)>();
    public string Receipt { get; set; } = "RCPT // pending // written on submit";
    public Color? EdgeColor { get; set; }
    public Color Edge { get => EdgeColor ?? PhosphorTheme.Patina; set => EdgeColor = value; }

    public SpecCardPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        float s = PhosphorPaint.Scale(this);
        Padding = new Padding((int)Math.Round(PhosphorTheme.S5 * s), (int)Math.Round(HeadPx * s), (int)Math.Round(PhosphorTheme.S5 * s), (int)Math.Round(PhosphorTheme.S4 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float radius = PhosphorTheme.RadiusButton * s;
        float ear = 15f * s;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        // Card silhouette with the clipped top-right ear (cat-card mask).
        using var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddLine(rect.X + radius, rect.Y, rect.Right - ear, rect.Y);
        path.AddLine(rect.Right - ear, rect.Y, rect.Right, rect.Y + ear);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        using (var fill = new SolidBrush(PhosphorTheme.Surface2)) g.FillPath(fill, path);

        var prevClip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);
        using (var edgeBrush = new SolidBrush(Color.FromArgb(230, Edge))) g.FillRectangle(edgeBrush, rect.X, rect.Y, 3f * s, rect.Height);
        using (var glow = new LinearGradientBrush(new RectangleF(rect.X, rect.Y, 18f * s, rect.Height), Color.FromArgb(50, Edge), Color.FromArgb(0, Edge), LinearGradientMode.Horizontal))
            g.FillRectangle(glow, rect.X, rect.Y, 18f * s, rect.Height);

        float x = PhosphorTheme.S6 * s;
        float y = PhosphorTheme.S4 * s;
        float dtPx = PhosphorTheme.EyebrowPx;
        using var codeFont = PhosphorFonts.Display(32f);
        using var nameFont = PhosphorFonts.Ui(PhosphorTheme.TextSection, heavy: true);
        using var dtFont = PhosphorFonts.Mono(dtPx);
        using var ddFont = PhosphorFonts.Mono(PhosphorTheme.TextData);
        using var ghost = new SolidBrush(PhosphorTheme.Ghost);
        using var bone = new SolidBrush(PhosphorTheme.Bone);
        float trackWide = PhosphorTheme.EyebrowTracking(s);
        float trackTight = dtPx * 0.12f * s;

        PhosphorPaint.DrawEyebrow(g, Eyebrow, PhosphorTheme.Ghost, x, y, s);
        y += 16f * s;
        g.DrawString(Code, codeFont, bone, x - 2f * s, y - 6f * s, PhosphorPaint.Typographic);
        float codeH = codeFont.GetHeight(g) * 0.86f;
        float nameX = x + g.MeasureString(Code, codeFont, PointF.Empty, PhosphorPaint.Typographic).Width + 14f * s;
        g.DrawString(Title, nameFont, bone, nameX, y + 4f * s, PhosphorPaint.Typographic);
        PhosphorPaint.DrawTracked(g, Alt.ToUpperInvariant(), dtFont, ghost, nameX, y + 22f * s, trackTight);

        // Right column: status tag and board.
        float tagW = PhosphorPaint.MeasureTracked(g, Status.ToUpperInvariant(), dtFont, trackTight) + 14f * s;
        var tagRect = new RectangleF(rect.Right - x - tagW, PhosphorTheme.S4 * s, tagW, 20f * s);
        Color tagLine = StatusState switch
        {
            PhosphorBadgeState.Proven => PhosphorTheme.Patina,
            PhosphorBadgeState.Validated => PhosphorTheme.Patina,
            PhosphorBadgeState.Blocked => PhosphorTheme.Rust,
            _ => PhosphorTheme.Line2
        };
        float tagRadius = Math.Min(PhosphorTheme.RadiusBadge * s, tagRect.Height / 2f);
        if (StatusState == PhosphorBadgeState.Proven) PhosphorPaint.FillRounded(g, tagRect, tagRadius, PhosphorTheme.Patina);
        PhosphorPaint.StrokeRounded(g, tagRect, tagRadius, tagLine);
        using (var tagInk = new SolidBrush(StatusState == PhosphorBadgeState.Proven ? PhosphorTheme.Ground : (StatusState == PhosphorBadgeState.Blocked ? PhosphorTheme.Rust : PhosphorTheme.Dim)))
            PhosphorPaint.DrawTracked(g, Status.ToUpperInvariant(), dtFont, tagInk, tagRect.X + 7f * s, tagRect.Y + (tagRect.Height - dtFont.GetHeight(g)) / 2f, trackTight);
        float boardW = PhosphorPaint.MeasureTracked(g, Board.ToUpperInvariant(), dtFont, trackTight);
        PhosphorPaint.DrawTracked(g, Board.ToUpperInvariant(), dtFont, ghost, rect.Right - x - boardW, 42f * s, trackTight);

        // Tear line with real notches.
        float tearY = y + codeH + 10f * s;
        using (var tear = new Pen(Color.FromArgb(80, PhosphorTheme.Bone), 1f) { DashStyle = DashStyle.Dash })
            g.DrawLine(tear, rect.X + 18f * s, tearY, rect.Right - 18f * s, tearY);
        float hole = 9f * s;
        using (var holeFill = new SolidBrush(PhosphorPaint.ResolveBackColor(Parent)))
        using (var holeRing = new Pen(PhosphorTheme.Line2, 1f))
        {
            g.FillEllipse(holeFill, rect.X - hole, tearY - hole, hole * 2f, hole * 2f);
            g.DrawEllipse(holeRing, rect.X - hole, tearY - hole, hole * 2f, hole * 2f);
            g.FillEllipse(holeFill, rect.Right - hole, tearY - hole, hole * 2f, hole * 2f);
            g.DrawEllipse(holeRing, rect.Right - hole, tearY - hole, hole * 2f, hole * 2f);
        }

        // Spec grid.
        float gy = tearY + PhosphorTheme.S3 * s;
        int cols = Math.Max(1, Specs.Length);
        float colW = (rect.Width - 2f * x) / cols;
        for (int i = 0; i < Specs.Length; i++)
        {
            float cx = x + i * colW;
            PhosphorPaint.DrawTracked(g, Specs[i].Label.ToUpperInvariant(), dtFont, ghost, cx, gy, trackTight);
            using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(Specs[i].Value, ddFont, bone, new RectangleF(cx, gy + 15f * s, colW - 10f * s, 18f * s), fmt);
        }

        // Barcode band.
        float by = gy + 38f * s;
        float bh = 12f * s;
        using (var bar = new SolidBrush(PhosphorTheme.Ghost))
        {
            float bx = x;
            float[] pattern = { 2, 2, 1, 4, 3, 2, 1, 4 };
            int k = 0;
            while (bx < rect.Right - x)
            {
                float w = pattern[k % pattern.Length] * s;
                if (k % 2 == 0) g.FillRectangle(bar, bx, by, w, bh);
                bx += w;
                k++;
            }
        }

        PhosphorPaint.DrawTracked(g, Receipt.ToUpperInvariant(), dtFont, ghost, x, by + bh + 8f * s, trackWide * 0.75f);

        using (var sep = new Pen(PhosphorTheme.Line, 1f))
            g.DrawLine(sep, rect.X, Padding.Top - 6f * s, rect.Right, Padding.Top - 6f * s);

        g.Clip = prevClip;
        using var border = new Pen(PhosphorTheme.Line2, 1f);
        g.DrawPath(border, path);
    }
}
