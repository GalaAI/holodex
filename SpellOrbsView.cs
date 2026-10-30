using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The Spells tab inventory: an orb per Spell kind, built from the Augment Inventory FolderPocket (CP-36) made round.
/// Each orb is three layers: a back with a token contour field, the Spells as sheets, and a smoked-glass front on a
/// bottom hinge with the kind's orb glyph. Hover leans the front and fans the sheets (staggered, ease-spring);
/// pointer parallax shifts the layers and drags a specular across the glass.
/// Clicking an orb opens it modally (PocketCard CP-37): the tab behind dims to 62% and blurs, the orb comes to the
/// centre and its Spells spring out on an orbit as round nodes with labels (<see cref="SpellOrbit"/>). Clicking a
/// node turns the orbit to bring it to the top, grows it 1.5x, pulses the Spells that share its route and opens a
/// detail card; the rest dim. Clicking off closes the card, then the orb; Esc closes the orb. An empty kind does not
/// open: it asks the composer below for one (<see cref="EmptyKindPicked"/>). Reduced motion settles every state in
/// place and holds the pulse still.
/// </summary>
internal sealed class SpellOrbsView : Control
{
    private const float HeaderPx = 28f;
    private const float OrbDiameterPx = 88f;
    private const float FanRoomPx = 16f;
    private const float LabelPx = 44f;
    private const int MaxSheets = 5;
    private const float HoverMs = 260f;
    private const float OpenMs = 460f;
    private const float TurnMs = 520f;
    private const float GrowMs = 320f;
    private const float CardMs = 180f;
    private const float AddMs = 620f;
    private const float StaggerMs = 35f;
    private const float PulseMs = 2600f;

    private IReadOnlyList<SpellKindInfo> _kinds = SpellKindInfo.All;
    private Dictionary<string, List<OrbSpell>> _spells = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private (Color Led, string Text)[] _sourceLeds = { (PhosphorTheme.Ghost, "READING INPUT SPINE") };

    // Grid
    private readonly List<RectangleF> _cells = new();
    private readonly float[] _hover = new float[16];
    private int _hoverIndex = -1;
    private int _focusIndex = -1;
    private bool _keyboardFocus;
    private PointF _pointer = new(-1, -1);
    private string? _addingKind;
    private long _addingAt;

    // Open orb
    private string? _openKind;
    private int _openCell = -1;
    /// <summary>Where the opened orb sat in the grid, in this view's open-state coordinates.</summary>
    private RectangleF _openFrom;
    private bool _closing;
    private long _openAt;
    private int _selected = -1;
    private long _selectedAt;
    private float _rotFrom = SpellOrbit.RestRotation, _rotTo = SpellOrbit.RestRotation;
    private long _rotAt;
    private int _hoverNode = -1;
    private Bitmap? _backdrop, _backdropBlur;
    private Control? _coverHidden;
    private DockStyle _restDock = DockStyle.Fill;
    private readonly List<(PointF Center, float Radius)> _nodes = new();
    private RectangleF _cardRect = RectangleF.Empty;
    private RectangleF _centerOrb = RectangleF.Empty;
    private bool _framing;
    private long _lastTick;

    public SpellOrbsView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = PhosphorTheme.Surface;
        TabStop = true;
        Name = "Holodex.SpellOrbs";
        AccessibleName = "Spell orbs";
        AccessibleRole = AccessibleRole.List;
        Cursor = Cursors.Default;
    }

    /// <summary>The controls this view covers while an orb is open (the composer below it): drawn dimmed, hidden.</summary>
    public Control? CoverWhenOpen { get; set; }

    /// <summary>Raised when an empty orb is clicked: the composer takes that kind and the focus.</summary>
    public event Action<SpellKindInfo>? EmptyKindPicked;

    public IReadOnlyList<SpellKindInfo> Kinds => _kinds;
    public bool Loaded => _loaded;
    public string? OpenKind => _closing ? null : _openKind;
    public int SelectedIndex => _selected;
    public IReadOnlyList<OrbSpell> SpellsOf(string kind) => _spells.TryGetValue(kind, out var l) ? l : Array.Empty<OrbSpell>();

    /// <summary>The orbit as drawn now (centre, radius), for tests and the as-built render.</summary>
    internal IReadOnlyList<(PointF Center, float Radius)> NodesDrawn => _nodes;

    /// <summary>Shows <paramref name="spells"/>; <paramref name="sourceLeds"/> says where they came from.</summary>
    public void SetSpells(Dictionary<string, List<OrbSpell>> spells, (Color Led, string Text)[] sourceLeds)
    {
        _spells = spells;
        _loaded = true;
        _sourceLeds = sourceLeds;
        if (_openKind != null && SpellsOf(_openKind).Count == 0) CloseNow();
        if (_selected >= 0 && (_openKind == null || _selected >= SpellsOf(_openKind).Count)) _selected = -1;
        Invalidate();
    }

    /// <summary>A Spell just landed in <paramref name="kind"/>: its orb drops a sheet in, edged ember while it lands.</summary>
    public void MarkAdded(string kind)
    {
        _addingKind = kind;
        _addingAt = SurfaceMotion.Now;
        Kick();
    }

    // ---- Motion -----------------------------------------------------------------------------------------------------

    private bool Animates => FindForm() is Form f && SurfaceMotion.Animates(f);

    private float Progress(long since, float ms) => !Animates ? 1f : Math.Clamp((SurfaceMotion.Now - since) / ms, 0f, 1f);

    private float OpenProgress => _openKind == null ? 0f : _closing ? 1f - Progress(_openAt, OpenMs * 0.7f) : Progress(_openAt, OpenMs);

    private float Rotation => _rotFrom + SpellOrbit.ShortestTurn(_rotFrom, _rotTo) * PhosphorMotion.EaseAnalog(Progress(_rotAt, TurnMs));

    /// <summary>Starts the frame clock while anything moves; it stops itself when everything has settled.</summary>
    private void Kick()
    {
        Invalidate();
        if (!Animates)
        {
            for (int i = 0; i < _hover.Length; i++) _hover[i] = i == _hoverIndex && _openKind == null ? 1f : 0f;
            if (_closing) CloseNow();
            return;
        }
        if (_framing) return;
        _framing = true;
        _lastTick = SurfaceMotion.Now;
        SurfaceMotion.Frame += OnFrame;
    }

    private void OnFrame()
    {
        if (IsDisposed) { StopFrames(); return; }
        long now = SurfaceMotion.Now;
        float dt = Math.Clamp((now - _lastTick) / HoverMs, 0f, 1f);
        _lastTick = now;
        bool moving = false;
        for (int i = 0; i < _kinds.Count && i < _hover.Length; i++)
        {
            float target = i == _hoverIndex && _openKind == null ? 1f : 0f;
            if (Math.Abs(_hover[i] - target) < 0.001f) { _hover[i] = target; continue; }
            _hover[i] = target > _hover[i] ? Math.Min(target, _hover[i] + dt) : Math.Max(target, _hover[i] - dt);
            moving = true;
        }
        if (_openKind != null)
        {
            if (_closing && OpenProgress <= 0f) CloseNow();
        }
        bool pulseOnly = false;
        if (_openKind != null)
        {
            // The orbit settles; then only the related pulse keeps the clock while a node is open (frozen under reduced
            // motion), and it is slow, so it repaints at a third of the frame rate.
            bool settling = OpenProgress < 1f || Progress(_rotAt, TurnMs) < 1f || Progress(_selectedAt, Math.Max(GrowMs, CardMs)) < 1f
                || SurfaceMotion.Now - _openAt < OpenMs + StaggerMs * 12;
            pulseOnly = !settling && !moving && _selected >= 0 && SpellOrbit.Related(SpellsOf(_openKind), _selected).Count > 0;
            moving |= settling || pulseOnly;
        }
        if (_addingKind != null)
        {
            if (Progress(_addingAt, AddMs) >= 1f) _addingKind = null;
            else moving = true;
        }
        if (!pulseOnly || ++_pulseFrame % 3 == 0) Invalidate();
        if (!moving) StopFrames();
    }

    private int _pulseFrame;

    private void StopFrames()
    {
        if (!_framing) return;
        _framing = false;
        SurfaceMotion.Frame -= OnFrame;
    }

    /// <summary>For the as-built render: every tween at its end state.</summary>
    internal void Settle()
    {
        for (int i = 0; i < _hover.Length; i++) _hover[i] = i == _hoverIndex && _openKind == null ? 1f : 0f;
        _openAt = _selectedAt = _rotAt = _addingAt = SurfaceMotion.Now - 100_000;
        _addingKind = null;
        if (_closing) CloseNow();
        Invalidate();
    }

    /// <summary>For the as-built render: the orb at <paramref name="index"/> as hovered, the pointer at its glass.</summary>
    internal void PreviewHover(int index)
    {
        _hoverIndex = index;
        if (index >= 0 && index < _cells.Count)
        {
            var c = OrbCircle(_cells[index]);
            _pointer = new PointF(c.X + c.Width * 0.68f, c.Y + c.Height * 0.62f);
        }
        Settle();
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------

    private float S => PhosphorPaint.Scale(this);

    private void LayoutCells()
    {
        _cells.Clear();
        float s = S;
        var area = new RectangleF(Padding.Left, Padding.Top + HeaderPx * s, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical - HeaderPx * s));
        int n = _kinds.Count;
        int cols = area.Width >= 4 * 104f * s ? 4 : 3;
        int rows = (n + cols - 1) / cols;
        float cellW = area.Width / cols;
        float cellH = (Math.Min(OrbDiameterPx * s, cellW - 18f * s) + FanRoomPx * s + LabelPx * s);
        float gridH = rows * cellH + (rows - 1) * 8f * s;
        // Centred with the hint line under it.
        float top = area.Top + Math.Max(0, (area.Height - gridH - 32f * s) / 2f);
        for (int r = 0; r < rows; r++)
        {
            int inRow = Math.Min(cols, n - r * cols);
            float left = area.Left + (area.Width - inRow * cellW) / 2f;
            for (int c = 0; c < inRow; c++)
                _cells.Add(new RectangleF(left + c * cellW, top + r * (cellH + 8f * s), cellW, cellH));
        }
    }

    /// <summary>The orb's circle inside its cell: under the fan room, over the labels.</summary>
    private RectangleF OrbCircle(RectangleF cell)
    {
        float s = S;
        float d = Math.Min(OrbDiameterPx * s, cell.Width - 18f * s);
        return new RectangleF(cell.X + (cell.Width - d) / 2f, cell.Y + FanRoomPx * s, d, d);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutCells();
    }

    // ---- Opening and closing ------------------------------------------------------------------------------------------

    /// <summary>Opens <paramref name="kind"/>'s orb. False for an unknown or empty kind (an empty orb never opens).</summary>
    public bool Open(string kind)
    {
        int index = _kinds.ToList().FindIndex(k => k.Id == kind);
        if (index < 0 || SpellsOf(kind).Count == 0) return false;
        if (_cells.Count == 0) LayoutCells();
        CaptureBackdrop();
        var from = OrbCircle(_cells[index]);
        var parentOrigin = Parent?.ClientRectangle.Location ?? Point.Empty;
        _openFrom = new RectangleF(from.X + Left - parentOrigin.X, from.Y + Top - parentOrigin.Y, from.Width, from.Height);
        _openKind = kind;
        _openCell = index;
        _closing = false;
        _selected = -1;
        _hoverNode = -1;
        _rotFrom = _rotTo = SpellOrbit.RestRotation;
        _openAt = _rotAt = SurfaceMotion.Now;
        Cover(true);
        // Esc and the arrows reach the open orb; focus is taken only inside the active window, never activating one.
        if (SpellsTab.InActiveForm(this)) Focus();
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        Kick();
        return true;
    }

    /// <summary>Closes the open orb, easing back unless motion is off.</summary>
    public void Close()
    {
        if (_openKind == null || _closing) return;
        if (!Animates) { CloseNow(); return; }
        // Reverse from wherever the opening is.
        float p = OpenProgress;
        _closing = true;
        _openAt = SurfaceMotion.Now - (long)((1f - p) * OpenMs * 0.7f);
        Kick();
    }

    private void CloseNow()
    {
        _openKind = null;
        _openCell = -1;
        _closing = false;
        _selected = -1;
        _nodes.Clear();
        _cardRect = _centerOrb = RectangleF.Empty;
        Cover(false);
        _backdrop?.Dispose();
        _backdropBlur?.Dispose();
        _backdrop = _backdropBlur = null;
        Invalidate();
    }

    /// <summary>Selects node <paramref name="index"/> of the open orb (-1 closes the card), turning it to the top.</summary>
    public void Select(int index)
    {
        if (_openKind == null) return;
        var spells = SpellsOf(_openKind);
        if (index >= spells.Count) return;
        float now = Rotation;
        _selected = index;
        _selectedAt = SurfaceMotion.Now;
        _rotFrom = now;
        _rotTo = index >= 0 ? SpellOrbit.CenterOn(index, spells.Count) : SpellOrbit.Normalize(now);
        _rotAt = SurfaceMotion.Now;
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        Kick();
    }

    /// <summary>
    /// Covers the whole tab while an orb is open: this view leaves its dock and fills its parent, and the composer it
    /// covers is hidden (its picture is in the dimmed backdrop), so nothing under the modal takes a click or focus.
    /// </summary>
    private void Cover(bool on)
    {
        if (Parent == null) return;
        if (on)
        {
            if (Dock != DockStyle.None) _restDock = Dock;
            if (CoverWhenOpen is { Visible: true } c) { c.Visible = false; _coverHidden = c; }
            Dock = DockStyle.None;
            Bounds = Parent.ClientRectangle;
            BringToFront();
        }
        else
        {
            Dock = _restDock;
            if (_coverHidden != null) { _coverHidden.Visible = true; _coverHidden = null; }
        }
    }

    /// <summary>The tab as it looks now, sharp and blurred (7px), for the open orb's backdrop.</summary>
    private void CaptureBackdrop()
    {
        _backdrop?.Dispose();
        _backdropBlur?.Dispose();
        var full = Parent?.ClientRectangle ?? ClientRectangle;
        int w = Math.Max(1, full.Width), h = Math.Max(1, full.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(PhosphorTheme.Surface);
            var st = g.Save();
            g.TranslateTransform(Left - full.Left, Top - full.Top);
            PaintGrid(g, ClientRectangle, still: true);
            g.Restore(st);
            if (CoverWhenOpen is { Visible: true, Width: > 0, Height: > 0 } cover)
            {
                try
                {
                    using var part = new Bitmap(cover.Width, cover.Height);
                    cover.DrawToBitmap(part, new Rectangle(0, 0, cover.Width, cover.Height));
                    g.DrawImageUnscaled(part, cover.Left - full.Left, cover.Top - full.Top);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
                {
                    // The composer stays plain surface in the backdrop.
                }
            }
        }
        _backdrop = bmp;
        // A 7px blur, as a downscale and back up: smooth enough under a 62% dim, and cheap enough to make on click.
        int f = Math.Max(3, (int)Math.Round(7f * S));
        using var small = new Bitmap(Math.Max(1, w / f), Math.Max(1, h / f), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, new Rectangle(0, 0, small.Width, small.Height));
        }
        var blur = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(blur))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var wrap = new ImageAttributes();
            wrap.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, wrap);
        }
        _backdropBlur = blur;
    }

    // ---- Input ------------------------------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.Location;
        if (_openKind != null)
        {
            int node = NodeAt(e.Location);
            if (node != _hoverNode) { _hoverNode = node; Invalidate(); }
            Cursor = node >= 0 || _centerOrb.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            return;
        }
        int hit = CellAt(e.Location);
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit;
            _keyboardFocus = false;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }
        // Parallax and the specular follow the pointer across a hovered orb.
        if (hit >= 0) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(_cells[hit], 8, 8)));
        Kick();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _hoverNode = -1;
        _pointer = new PointF(-1, -1);
        Kick();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        ClickAt(e.Location);
    }

    /// <summary>A left click at <paramref name="p"/>: an orb opens, a node selects, a click off closes the card, then the orb.</summary>
    internal void ClickAt(Point p)
    {
        if (_openKind != null)
        {
            if (_closing) return;
            int node = NodeAt(p);
            if (node >= 0) { Select(node == _selected ? -1 : node); return; }
            if (_cardRect.Contains(p)) return;
            if (_centerOrb.Contains(p)) { Close(); return; }
            // Off the orb: the card closes first, then the orb.
            if (_selected >= 0) Select(-1);
            else Close();
            return;
        }
        int hit = CellAt(p);
        if (hit >= 0) Activate(hit);
    }

    private void Activate(int cell)
    {
        var kind = _kinds[cell];
        if (SpellsOf(kind.Id).Count == 0) { EmptyKindPicked?.Invoke(kind); return; }
        Open(kind.Id);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _openKind != null && !_closing) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = e.KeyCode switch { Keys.Right or Keys.Down => 1, Keys.Left or Keys.Up => -1, _ => 0 };
        if (_openKind != null)
        {
            int n = SpellsOf(_openKind).Count;
            if (step != 0 && n > 0) { Select(((_selected < 0 ? (step > 0 ? -1 : 0) : _selected) + step + n) % n); e.Handled = true; }
            return;
        }
        if (step != 0)
        {
            _keyboardFocus = true;
            _focusIndex = ((_focusIndex < 0 ? (step > 0 ? -1 : 0) : _focusIndex) + step + _kinds.Count) % _kinds.Count;
            e.Handled = true;
            Invalidate();
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space && _focusIndex >= 0)
        {
            Activate(_focusIndex);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _keyboardFocus = false; Invalidate(); }

    private int CellAt(Point p)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            var orb = OrbCircle(_cells[i]);
            var hit = RectangleF.FromLTRB(orb.Left, _cells[i].Top, orb.Right, _cells[i].Bottom);
            if (hit.Contains(p)) return i;
        }
        return -1;
    }

    private int NodeAt(Point p)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            var (c, r) = _nodes[i];
            float dx = p.X - c.X, dy = p.Y - c.Y;
            float reach = r + 6f * S;
            if (dx * dx + dy * dy <= reach * reach) return i;
        }
        return -1;
    }

    // ---- Paint ------------------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (_cells.Count != _kinds.Count) LayoutCells();
        if (_openKind == null) { PaintGrid(g, ClientRectangle, still: false); return; }
        PaintOpen(g);
    }

    private void PaintGrid(Graphics g, Rectangle bounds, bool still)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var bg = new SolidBrush(PhosphorTheme.Surface)) g.FillRectangle(bg, bounds);
        float s = S;
        // Header: what the orbs hold and where it came from.
        int total = _spells.Values.Sum(l => l.Count);
        PhosphorPaint.DrawEyebrow(g, _loaded ? $"Spells // {_kinds.Count} kinds // {total} {(total == 1 ? "Spell" : "Spells")}" : $"Spells // {_kinds.Count} kinds",
            PhosphorTheme.Dim, Padding.Left, Padding.Top + 6f * s, s);
        float ledW = Math.Min(LedWidth(g, _sourceLeds, s), Width * 0.6f);
        LedPaint.Draw(g, _sourceLeds, new RectangleF(bounds.Right - Padding.Right - ledW, Padding.Top + 2f * s, ledW + 1f, LedPaint.LinePx * s), s, wrap: false);

        for (int i = 0; i < _cells.Count && i < _kinds.Count; i++)
        {
            var kind = _kinds[i];
            var cell = _cells[i];
            float hover = still ? 0f : _hover[i];
            bool focus = !still && Focused && _keyboardFocus && i == _focusIndex;
            float add = !still && _addingKind == kind.Id ? Progress(_addingAt, AddMs) : 1f;
            var spells = SpellsOf(kind.Id);
            DrawOrb(g, OrbCircle(cell), kind, spells, hover, still || i != _hoverIndex ? PointF.Empty : Parallax(OrbCircle(cell)), focus, add, sheetsOut: 1f);
            DrawOrbLabel(g, kind, spells.Count, cell, OrbCircle(cell).Bottom + 8f * s, hover);
        }
        if (_loaded && _cells.Count > 0)
        {
            float y = _cells[^1].Bottom + 14f * s;
            using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var body = PhosphorFonts.Body(11f);
            using var ghost = new SolidBrush(PhosphorTheme.Ghost);
            g.DrawString("Click an orb to open it. An empty orb asks Wiz for its first Spell.", body, ghost,
                new RectangleF(Padding.Left, y, Width - Padding.Horizontal, 18f * s), fmt);
        }
    }

    /// <summary>The width <see cref="LedPaint"/> lays <paramref name="leds"/> out in on one line (dot, gap, tracked mono, separator).</summary>
    private static float LedWidth(Graphics g, (Color Led, string Text)[] leds, float s)
    {
        using var font = PhosphorFonts.Mono(9f);
        float w = 0;
        foreach (var (_, text) in leds) w += 6f * s + 7f * s + PhosphorPaint.MeasureTracked(g, text.ToUpperInvariant(), font, 0.9f * s);
        return w + Math.Max(0, leds.Length - 1) * 16f * s;
    }

    /// <summary>The pointer over a hovered orb as -1..1 on each axis.</summary>
    private PointF Parallax(RectangleF orb)
    {
        if (_pointer.X < 0) return PointF.Empty;
        float px = Math.Clamp((_pointer.X - (orb.X + orb.Width / 2f)) / (orb.Width / 2f), -1f, 1f);
        float py = Math.Clamp((_pointer.Y - (orb.Y + orb.Height / 2f)) / (orb.Height / 2f), -1f, 1f);
        return new PointF(px, py);
    }

    private void DrawOrbLabel(Graphics g, SpellKindInfo kind, int count, RectangleF cell, float top, float hover)
    {
        float s = S;
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var title = PhosphorFonts.Ui(13f, heavy: true);
        using var titleInk = new SolidBrush(count > 0 || hover > 0.5f ? PhosphorTheme.Bone : PhosphorTheme.Dim);
        g.DrawString(kind.Plural, title, titleInk, new RectangleF(cell.X, top, cell.Width, 18f * s), fmt);
        if (!_loaded)
        {
            using var mono = PhosphorFonts.Mono(PhosphorTheme.TextData);
            using var ghost = new SolidBrush(PhosphorTheme.Ghost);
            g.DrawString("READING", mono, ghost, new RectangleF(cell.X, top + 19f * s, cell.Width, 16f * s), fmt);
            return;
        }
        if (count == 0)
        {
            using var body = PhosphorFonts.Body(11f);
            using var ghost = new SolidBrush(PhosphorTheme.Ghost);
            g.DrawString(kind.EmptyText, body, ghost, new RectangleF(cell.X, top + 19f * s, cell.Width, 16f * s), fmt);
            return;
        }
        using var data = PhosphorFonts.Mono(PhosphorTheme.TextData);
        using var accent = new SolidBrush(Color.FromArgb(hover > 0.5f ? 255 : 210, kind.Accent));
        g.DrawString($"{count} {(count == 1 ? "SPELL" : "SPELLS")}", data, accent, new RectangleF(cell.X, top + 20f * s, cell.Width, 16f * s), fmt);
    }

    /// <summary>
    /// One orb: back (contour field and a folder tab), the Spells as sheets (fanned by <paramref name="hover"/>), the
    /// smoked-glass front leaning on its bottom hinge, the kind glyph on the glass, and the rim.
    /// <paramref name="sheetsOut"/> 0 draws the pocket empty (its Spells are out on the orbit).
    /// </summary>
    private void DrawOrb(Graphics g, RectangleF orb, SpellKindInfo kind, IReadOnlyList<OrbSpell> spells, float hover, PointF parallax, bool focus, float add, float sheetsOut)
    {
        float s = S;
        float R = orb.Width / 2f;
        var c = new PointF(orb.X + R, orb.Y + R);
        bool empty = spells.Count == 0;
        Color accent = kind.Accent;
        float lean = PhosphorMotion.EaseSpring(hover);
        float adding = add < 1f ? 1f - add : 0f;
        // The pocket squashes as a Spell lands (CP-36 adding): down, then back with the spring.
        float squash = add < 1f ? (float)Math.Sin(Math.Clamp(add * 1.6f, 0f, 1f) * Math.PI) * 0.05f : 0f;

        var st = g.Save();
        g.TranslateTransform(c.X, orb.Bottom);
        g.ScaleTransform(1f + squash * 0.6f, 1f - squash);
        g.TranslateTransform(-c.X, -orb.Bottom);

        // Shadow under the orb.
        using (var shadow = new GraphicsPath())
        {
            shadow.AddEllipse(c.X - R * 0.78f, orb.Bottom - R * 0.12f, R * 1.56f, R * 0.3f);
            using var pb = new PathGradientBrush(shadow) { CenterColor = Color.FromArgb(110, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } };
            g.FillPath(pb, shadow);
        }

        // Back: the folder tab, then the sphere with its contour field. Parallax moves it against the pointer.
        var back = new RectangleF(orb.X - parallax.X * 2f * s, orb.Y - parallax.Y * 1.5f * s, orb.Width, orb.Height);
        var tab = new RectangleF(back.X + R * 0.16f, back.Y - R * 0.04f, R * 0.62f, R * 0.32f);
        PhosphorPaint.FillRounded(g, tab, R * 0.12f, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, tab, R * 0.12f, PhosphorTheme.Line2);
        using (var sphere = new GraphicsPath())
        {
            sphere.AddEllipse(back);
            using (var fill = new SolidBrush(PhosphorTheme.Surface)) g.FillPath(fill, sphere);
            var prev = g.Clip;
            g.SetClip(sphere, CombineMode.Intersect);
            DrawContourField(g, back, accent, empty);
            g.Clip = prev;
            using var rim = new Pen(empty ? PhosphorTheme.Line2 : PhosphorTheme.Line2, 1f);
            g.DrawPath(rim, sphere);
        }

        // Sheets: the newest up to five, tops above the glass; hover fans them 14px apart at 8deg, staggered.
        int shown = empty ? 0 : (int)Math.Round(Math.Min(spells.Count, MaxSheets) * Math.Clamp(sheetsOut, 0f, 1f));
        float sheetW = R * 0.92f, sheetH = R * 0.78f;
        for (int k = 0; k < shown; k++)
        {
            int idx = spells.Count - shown + k;
            float mid = (shown - 1) / 2f;
            float stagger = Math.Clamp(hover * (1f + 0.12f * (shown - 1)) - 0.12f * k, 0f, 1f);
            float fan = PhosphorMotion.EaseSpring(stagger);
            float dx = (k - mid) * 14f * s * fan + (k - mid) * 2.2f * s;
            float dy = -(4f + 8f * fan) * s - k * 1.6f * s * (1f - fan);
            float rot = (k - mid) * 8f * fan;
            bool landing = adding > 0 && k == shown - 1;
            if (landing) dy -= 34f * s * (1f - PhosphorMotion.EaseSpring(add));
            var gs = g.Save();
            g.TranslateTransform(c.X + dx - parallax.X * 1f * s, c.Y - R * 0.18f + dy);
            g.RotateTransform(rot);
            var sheet = new RectangleF(-sheetW / 2f, -sheetH / 2f, sheetW, sheetH);
            PhosphorPaint.FillRounded(g, sheet, 5f * s, PhosphorTheme.Surface2);
            PhosphorPaint.StrokeRounded(g, sheet, 5f * s, landing ? PhosphorTheme.Ember : k == shown - 1 && hover > 0.4f ? Color.FromArgb(160, accent) : PhosphorTheme.Line2);
            using (var head = new SolidBrush(Color.FromArgb(spells[idx].Source == SpellSource.WizDraft ? 150 : 210, accent)))
                g.FillRectangle(head, sheet.X + 5f * s, sheet.Y + 5f * s, sheetW * 0.34f, 2.2f * s);
            using (var line = new SolidBrush(Color.FromArgb(70, PhosphorTheme.Bone)))
            {
                g.FillRectangle(line, sheet.X + 5f * s, sheet.Y + 10f * s, sheetW * 0.62f, 1.6f * s);
                g.FillRectangle(line, sheet.X + 5f * s, sheet.Y + 14f * s, sheetW * 0.48f, 1.6f * s);
            }
            g.Restore(gs);
        }

        // Front: smoked glass over the lower part of the sphere, hinged at the bottom. Hover leans it (its top drops
        // toward the hinge and it widens a little), uncovering the sheets.
        float hingeY = orb.Bottom;
        float frontTop = c.Y - R * 0.2f;
        float leanScale = 1f - 0.2f * lean;
        var fst = g.Save();
        g.TranslateTransform(c.X + parallax.X * 2f * s, hingeY + parallax.Y * 1.5f * s);
        g.ScaleTransform(1f + 0.03f * lean, leanScale);
        g.TranslateTransform(-c.X, -hingeY);
        using (var sphere = new GraphicsPath())
        {
            sphere.AddEllipse(orb);
            using var glass = new Region(sphere);
            glass.Intersect(new RectangleF(orb.X - 2, frontTop, orb.Width + 4, orb.Bottom - frontTop + 2));
            using (var tint = new LinearGradientBrush(new RectangleF(orb.X, frontTop - 1, orb.Width, orb.Bottom - frontTop + 2),
                Color.FromArgb(232, PhosphorTheme.Surface2), Color.FromArgb(246, PhosphorTheme.Ground), LinearGradientMode.Vertical))
                g.FillRegion(tint, glass);
            var prev = g.Clip;
            g.SetClip(glass, CombineMode.Intersect);
            // Smoke: a faint bone wash at the top of the glass.
            using (var smoke = new LinearGradientBrush(new RectangleF(orb.X, frontTop, orb.Width, R * 0.7f), Color.FromArgb(22, PhosphorTheme.Bone), Color.FromArgb(0, PhosphorTheme.Bone), LinearGradientMode.Vertical))
                g.FillRectangle(smoke, orb.X, frontTop, orb.Width, R * 0.7f);
            // LW-01 left edge: the kind's side light on the leading arc of the glass.
            using (var edge = new Pen(Color.FromArgb(empty ? 90 : 230, empty ? PhosphorTheme.Ghost : accent), 2f * s))
                g.DrawArc(edge, orb.X + 1f * s, orb.Y + 1f * s, orb.Width - 2f * s, orb.Height - 2f * s, 112, 64);
            // Specular: follows the pointer across a hovered orb, rests upper left otherwise.
            float sx = parallax == PointF.Empty ? c.X - R * 0.35f : c.X + parallax.X * R * 0.6f;
            float sy = parallax == PointF.Empty ? frontTop + R * 0.28f : frontTop + R * 0.25f + parallax.Y * R * 0.25f;
            using (var spec = new GraphicsPath())
            {
                spec.AddEllipse(sx - R * 0.55f, sy - R * 0.28f, R * 1.1f, R * 0.56f);
                using var sb = new PathGradientBrush(spec) { CenterColor = Color.FromArgb(parallax == PointF.Empty ? 18 : 44, PhosphorTheme.Bone), SurroundColors = new[] { Color.FromArgb(0, PhosphorTheme.Bone) } };
                g.FillPath(sb, spec);
            }
            g.Clip = prev;
            // Bone rim along the glass's top edge, brightest in the middle.
            float chord = (float)Math.Sqrt(Math.Max(0, R * R - (frontTop - c.Y) * (frontTop - c.Y)));
            using (var rimBrush = new LinearGradientBrush(new RectangleF(c.X - chord, frontTop - 1, chord * 2f, 2), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                rimBrush.InterpolationColors = new ColorBlend(3)
                {
                    Colors = new[] { Color.FromArgb(0, PhosphorTheme.Bone), Color.FromArgb(empty ? 60 : 130, PhosphorTheme.Bone), Color.FromArgb(0, PhosphorTheme.Bone) },
                    Positions = new[] { 0f, 0.5f, 1f }
                };
                using var rimPen = new Pen(rimBrush, 1.2f * s);
                g.DrawLine(rimPen, c.X - chord + 1, frontTop, c.X + chord - 1, frontTop);
            }
        }
        // The kind's orb glyph on the glass.
        float glyph = R * 0.6f;
        SpellOrbGlyphs.Draw(g, kind.Id, new RectangleF(c.X - glyph / 2f, c.Y + R * 0.3f - glyph / 2f, glyph, glyph),
            empty ? PhosphorTheme.Ghost : Color.FromArgb(hover > 0.4f ? 255 : 220, accent), 1.6f * s);
        g.Restore(fst);

        // Rim: the kind's accent while hovered, patina for keyboard focus, ember while a Spell lands.
        using (var outline = new GraphicsPath())
        {
            outline.AddEllipse(RectangleF.Inflate(orb, 3f * s, 3f * s));
            Color ring = adding > 0 ? Color.FromArgb((int)(200 * adding), PhosphorTheme.Ember)
                : focus ? PhosphorTheme.Patina
                : Color.FromArgb((int)(150 * hover), accent);
            if (ring.A > 4)
            {
                using var pen = new Pen(ring, (focus ? 2f : 1.4f) * s);
                g.DrawPath(pen, outline);
            }
        }
        g.Restore(st);
    }

    /// <summary>The back's art: a token contour field, patina and bone lines with a faint ember corner.</summary>
    private void DrawContourField(Graphics g, RectangleF back, Color accent, bool empty)
    {
        float s = S;
        float R = back.Width / 2f;
        using (var corner = new GraphicsPath())
        {
            corner.AddEllipse(back.Right - R * 0.9f, back.Y - R * 0.5f, R * 1.4f, R * 1.2f);
            using var cb = new PathGradientBrush(corner) { CenterColor = Color.FromArgb(empty ? 14 : 34, PhosphorTheme.Ember), SurroundColors = new[] { Color.FromArgb(0, PhosphorTheme.Ember) } };
            g.FillPath(cb, corner);
        }
        for (int i = 0; i < 7; i++)
        {
            float y0 = back.Y + R * (0.18f + i * 0.2f);
            var pts = new PointF[13];
            for (int k = 0; k < pts.Length; k++)
            {
                float x = back.X + back.Width * k / (pts.Length - 1);
                float wave = (float)Math.Sin(k * 0.62 + i * 0.9) * R * 0.06f + (float)Math.Cos(k * 0.31 - i * 0.4) * R * 0.04f;
                pts[k] = new PointF(x, y0 + wave);
            }
            Color ink = i % 3 == 1 ? PhosphorTheme.Bone : empty ? PhosphorTheme.Ghost : accent;
            using var pen = new Pen(Color.FromArgb(i % 3 == 1 ? 22 : empty ? 26 : 46, ink), 1f * s);
            g.DrawCurve(pen, pts, 0.5f);
        }
    }

    private void PaintOpen(Graphics g)
    {
        float s = S;
        var kind = _kinds[_openCell];
        var spells = SpellsOf(kind.Id);
        float open = OpenProgress;
        float fade = PhosphorMotion.EaseAnalog(open);

        // Backdrop: the tab as it was, cross-faded to its blurred picture, under ground at 62%.
        if (_backdrop != null && (_backdropBlur == null || fade < 0.99f)) g.DrawImageUnscaled(_backdrop, 0, 0);
        if (_backdropBlur != null) DrawFaded(g, _backdropBlur, fade);
        using (var dim = new SolidBrush(Color.FromArgb((int)(158 * fade), PhosphorTheme.Ground))) g.FillRectangle(dim, ClientRectangle);

        // The orb travels from its cell to the centre and grows.
        var from = _openFrom;
        var center = new PointF(Width / 2f, Height / 2f - 6f * s);
        float radius = SpellOrbit.Radius(Width, Height, s);
        float centerR = 34f * s;
        float travel = PhosphorMotion.EaseSpring(open);
        float r = from.Width / 2f + (centerR - from.Width / 2f) * travel;
        var at = new PointF(from.X + from.Width / 2f + (center.X - from.X - from.Width / 2f) * travel, from.Y + from.Height / 2f + (center.Y - from.Y - from.Height / 2f) * travel);
        _centerOrb = new RectangleF(at.X - r, at.Y - r, r * 2f, r * 2f);

        // Orbit ring.
        using (var ringPen = new Pen(Color.FromArgb((int)(110 * fade), PhosphorTheme.Line2), 1f * s) { DashPattern = new[] { 2f, 3f } })
            g.DrawEllipse(ringPen, center.X - radius * fade, center.Y - radius * fade, radius * 2f * fade, radius * 2f * fade);

        bool selecting = _selected >= 0;
        DrawOrb(g, _centerOrb, kind, spells, 1f, PointF.Empty, false, 1f, sheetsOut: 1f - open);
        // Under the orb: the kind and how many Spells it holds.
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        using (var title = PhosphorFonts.Ui(13f, heavy: true))
        using (var mono = PhosphorFonts.Mono(PhosphorTheme.TextData))
        using (var bone = new SolidBrush(Color.FromArgb((int)(255 * fade * (selecting ? 0.35f : 1f)), PhosphorTheme.Bone)))
        using (var ink = new SolidBrush(Color.FromArgb((int)(220 * fade * (selecting ? 0.35f : 1f)), kind.Accent)))
        {
            g.DrawString(kind.Plural, title, bone, new RectangleF(center.X - 90f * s, _centerOrb.Bottom + 8f * s, 180f * s, 18f * s), fmt);
            g.DrawString($"{spells.Count} {(spells.Count == 1 ? "SPELL" : "SPELLS")} // ESC CLOSES", mono, ink, new RectangleF(center.X - 120f * s, _centerOrb.Bottom + 27f * s, 240f * s, 16f * s), fmt);
        }

        // Nodes: out on the orbit, staggered on ease-spring; depth fades the far side; the selected one grows 1.5x.
        _nodes.Clear();
        float rot = Rotation;
        var related = SpellOrbit.Related(spells, _selected);
        float grow = PhosphorMotion.EaseSpring(Progress(_selectedAt, GrowMs));
        float pulse = PhosphorMotion.ReducedMotion || !Animates ? 0.7f : 0.45f + 0.55f * (0.5f + 0.5f * (float)Math.Cos(SurfaceMotion.Now % (long)PulseMs / PulseMs * Math.PI * 2));
        var order = Enumerable.Range(0, spells.Count)
            .OrderBy(i => i == _selected ? 2f : SpellOrbit.DepthOpacity(SpellOrbit.AngleDeg(i, spells.Count, rot))).ToArray();
        var nodes = new (PointF C, float R)[spells.Count];
        for (int i = 0; i < spells.Count; i++)
        {
            float t = _closing ? open : Animates ? Math.Clamp((SurfaceMotion.Now - _openAt - i * StaggerMs) / OpenMs, 0f, 1f) : 1f;
            float out1 = PhosphorMotion.EaseSpring(t);
            float angle = SpellOrbit.AngleDeg(i, spells.Count, rot);
            var p = SpellOrbit.Position(center, radius * out1, angle);
            float nodeR = 17f * s * (i == _selected ? 1f + (SpellOrbit.ExpandedScale - 1f) * grow : 1f);
            nodes[i] = (p, nodeR);
        }
        _nodes.AddRange(nodes);
        foreach (int i in order)
        {
            var spell = spells[i];
            var (p, nodeR) = nodes[i];
            float angle = SpellOrbit.AngleDeg(i, spells.Count, rot);
            float depth = i == _selected ? 1f : SpellOrbit.DepthOpacity(angle);
            float alpha = fade * depth * (selecting && i != _selected && !related.Contains(i) ? 0.45f : 1f);
            bool hot = i == _hoverNode || i == _selected;
            DrawNode(g, p, nodeR, kind, spell, alpha, hot, related.Contains(i) ? pulse : 0f);
            if (i != _selected) DrawNodeLabel(g, p, nodeR, spell, alpha, center);
        }

        // Detail card for the selected Spell, under its node at the top of the orbit.
        _cardRect = RectangleF.Empty;
        if (_selected >= 0 && _selected < spells.Count && !_closing)
            DrawCard(g, spells[_selected], kind, nodes[_selected].C, nodes[_selected].R, PhosphorMotion.EaseAnalog(Progress(_selectedAt, CardMs)) * fade, related.Count);
    }

    private static void DrawFaded(Graphics g, Image img, float alpha)
    {
        if (alpha <= 0.01f) return;
        if (alpha >= 0.99f) { g.DrawImageUnscaled(img, 0, 0); return; }
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>A Spell node: a round icon in the kind's accent; the related ring pulses (rp-pulse, 1 to .45).</summary>
    private void DrawNode(Graphics g, PointF c, float r, SpellKindInfo kind, OrbSpell spell, float alpha, bool hot, float relatedPulse)
    {
        float s = S;
        int A(float a) => (int)Math.Clamp(a * 255f * alpha, 0, 255);
        var rect = new RectangleF(c.X - r, c.Y - r, r * 2f, r * 2f);
        if (relatedPulse > 0)
        {
            using var halo = new Pen(Color.FromArgb(A(relatedPulse * 0.9f), kind.Accent), 1.6f * s);
            g.DrawEllipse(halo, RectangleF.Inflate(rect, 5f * s, 5f * s));
        }
        using (var fill = new SolidBrush(Color.FromArgb(A(1f), hot ? PhosphorTheme.Surface2 : PhosphorTheme.Surface)))
            g.FillEllipse(fill, rect);
        if (hot)
        {
            using var bloom = new GraphicsPath();
            bloom.AddEllipse(RectangleF.Inflate(rect, r * 0.5f, r * 0.5f));
            using var pb = new PathGradientBrush(bloom) { CenterColor = Color.FromArgb(A(0.22f), kind.Accent), SurroundColors = new[] { Color.FromArgb(0, kind.Accent) } };
            g.FillPath(pb, bloom);
        }
        Color stroke = spell.Source == SpellSource.WizDraft ? PhosphorTheme.Dim : kind.Accent;
        using (var pen = new Pen(Color.FromArgb(A(hot ? 1f : 0.8f), stroke), (hot ? 1.8f : 1.2f) * s))
        {
            if (spell.Source == SpellSource.WizDraft) pen.DashPattern = new[] { 3f, 2f };
            g.DrawEllipse(pen, rect);
        }
        float icon = r * 1.1f;
        SpellOrbGlyphs.Draw(g, kind.Id, new RectangleF(c.X - icon / 2f, c.Y - icon / 2f, icon, icon), Color.FromArgb(A(1f), hot ? PhosphorTheme.Cream : kind.Accent), 1.5f * s, ring: false);
    }

    /// <summary>The name beside a node, outside the orbit: under it in the lower half, over it in the upper.</summary>
    private void DrawNodeLabel(Graphics g, PointF c, float r, OrbSpell spell, float alpha, PointF center)
    {
        float s = S;
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var font = PhosphorFonts.Ui(11f);
        using var ink = new SolidBrush(Color.FromArgb((int)Math.Clamp(255 * alpha, 0, 255), PhosphorTheme.Bone));
        float w = 124f * s, h = 16f * s;
        bool below = c.Y >= center.Y - r;
        float y = below ? c.Y + r + 5f * s : c.Y - r - 5f * s - h;
        float x = Math.Clamp(c.X - w / 2f, 2f * s, Width - w - 2f * s);
        g.DrawString(spell.Name, font, ink, new RectangleF(x, y, w, h), fmt);
    }

    /// <summary>The Spell's card: name, type, binding and what it does, in the Highlight Aura of its kind.</summary>
    private void DrawCard(Graphics g, OrbSpell spell, SpellKindInfo kind, PointF node, float nodeR, float appear, int relatedCount)
    {
        if (appear <= 0.01f) return;
        float s = S;
        float w = Math.Min(340f * s, Width - 24f * s);
        float lead = 2f * s + PhosphorTheme.AuraLeadPx * s;
        float textW = w - lead - 14f * s;
        using var eyebrowFont = PhosphorFonts.Eyebrow();
        using var nameFont = PhosphorFonts.Ui(15f, heavy: true);
        using var keyFont = PhosphorFonts.Mono(PhosphorTheme.TextData);
        using var bodyFont = PhosphorFonts.Body(12f);
        var rows = new List<(string Key, string Value, Font Font, Color Ink)>
        {
            ("TYPE", $"{kind.Title} // {kind.Description}", bodyFont, PhosphorTheme.Bone),
            ("BINDING", spell.Binding + (spell.Source == SpellSource.InputSpine && !spell.Enabled ? " (turned off)" : ""), keyFont, spell.Source == SpellSource.InputSpine ? PhosphorTheme.Patina : PhosphorTheme.Dim),
            ("DOES", spell.Does, bodyFont, PhosphorTheme.Dim),
        };
        if (relatedCount > 0) rows.Add(("ROUTE", $"{spell.Route} // {relatedCount} more {(relatedCount == 1 ? "Spell casts" : "Spells cast")} this way and {(relatedCount == 1 ? "pulses" : "pulse")}", keyFont, PhosphorTheme.Ghost));
        float keyW = 0;
        foreach (var row in rows) keyW = Math.Max(keyW, g.MeasureString(row.Key, keyFont, PointF.Empty, PhosphorPaint.Typographic).Width);
        keyW += 10f * s;
        float valueW = textW - keyW;
        float nameH = g.MeasureString(spell.Name, nameFont, (int)textW).Height;
        float h = 12f * s + 14f * s + nameH + 8f * s;
        var heights = rows.Select(row => Math.Max(16f * s, g.MeasureString(row.Value, row.Font, (int)valueW).Height)).ToArray();
        h += heights.Sum() + (rows.Count - 1) * 6f * s + 12f * s;

        float top = node.Y + nodeR + 10f * s;
        top = Math.Clamp(top, 6f * s, Height - h - 6f * s);
        float left = Math.Clamp(node.X - w / 2f, 8f * s, Width - w - 8f * s);
        float rise = (1f - appear) * 8f * s;
        var rect = new RectangleF(left, top + rise, w, h);
        _cardRect = rect;

        var st = g.Save();
        using (var shadow = PhosphorTheme.CreateRoundedRect(new RectangleF(rect.X, rect.Y + 6f * s, rect.Width, rect.Height), PhosphorTheme.RadiusTile * s))
        using (var sb = new SolidBrush(Color.FromArgb((int)(120 * appear), 0, 0, 0)))
            g.FillPath(sb, shadow);
        var bounds = Rectangle.Round(rect);
        PhosphorTheme.DrawPhosphorPanel(g, bounds, (int)(PhosphorTheme.RadiusTile * s), Color.FromArgb((int)(255 * appear), PhosphorTheme.Surface), Color.FromArgb((int)(255 * appear), PhosphorTheme.Line2));
        PhosphorTheme.DrawHighlightAura(g, bounds, kind.Accent, appear, (int)(PhosphorTheme.RadiusTile * s));

        int A(Color c, float k = 1f) => (int)Math.Clamp(c.A * appear * k, 0, 255);
        float x = rect.X + lead, y = rect.Y + 12f * s;
        PhosphorPaint.DrawEyebrow(g, $"{kind.Title} // {spell.SourceLabel}", Color.FromArgb(A(kind.Accent), kind.Accent), x, y, s);
        y += 14f * s;
        using (var bone = new SolidBrush(Color.FromArgb(A(PhosphorTheme.Bone), PhosphorTheme.Bone)))
            g.DrawString(spell.Name, nameFont, bone, new RectangleF(x, y, textW, nameH));
        y += nameH + 8f * s;
        using var keyInk = new SolidBrush(Color.FromArgb(A(PhosphorTheme.Ghost), PhosphorTheme.Ghost));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            g.DrawString(row.Key, keyFont, keyInk, x, y + 1f * s, PhosphorPaint.Typographic);
            using var ink = new SolidBrush(Color.FromArgb(A(row.Ink), row.Ink));
            g.DrawString(row.Value, row.Font, ink, new RectangleF(x + keyW, y, valueW, heights[i] + 2f * s));
            y += heights[i] + 6f * s;
        }
        g.Restore(st);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopFrames();
            _backdrop?.Dispose();
            _backdropBlur?.Dispose();
        }
        base.Dispose(disposing);
    }
}
