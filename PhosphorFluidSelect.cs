using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Sizes and beats of FluidSelect (Augment Inventory CP-32: packages/ui FluidSelect.tsx and fluid-select.css), in
/// logical pixels and milliseconds. The menu radii are packages/tokens spacing.css --radius-menu-closed/-open, which
/// the generated PhosphorTokens predates.
/// </summary>
internal static class FluidSelectSpec
{
    public const float U = 4f;
    public const float RadiusClosed = 8f, RadiusOpen = 12f;
    /// <summary>The drop sits --s2 from the trigger.</summary>
    public const float Gap = 8f;
    /// <summary>The list scrolls past --u * 72.</summary>
    public const float MaxListHeight = U * 72f;
    public const float RowHeight = 44f;
    public const int ChevronMs = 120, StaggerMs = 12, StaggerCap = 10;
    public static int PanelMs => PhosphorMotion.DurDisclosure;
    public static int ItemMs => PhosphorMotion.DurDisclosureItem;
}

/// <summary>A list whose rows tip on hover (the FluidSelect drop): the row under the pointer, its screen box and its copy.</summary>
internal interface IRowTips
{
    (object Key, Rectangle Anchor, PhosphorTipInfo Info)? TipAt(Point screen);
    bool TipsAlive { get; }
}

/// <summary>
/// The drop of the one single-choice dropdown (CP-32 FluidSelect), opened from a trigger by
/// <see cref="SuggestionChip.ShowChoices"/>: a surface panel 8px from the trigger whose height springs open on the
/// disclosure beat, options that enter on a 12ms stagger, dim labels lit to bone on the active or chosen row, and the
/// coral Highlight Aura gliding between rows. It is a listbox: Up, Down, Home, End, Enter, Space and Esc work, and a
/// click outside closes it. Reduced motion shows every end state at once.
/// </summary>
internal sealed class FluidSelectDrop : ToolStripDropDown
{
    private readonly FluidSelectList _list;
    private readonly ToolStripControlHost _host;
    private readonly bool _above;
    private readonly long _openedAt = SurfaceMotion.Now;
    private long _exitAt = -1;
    private ToolStripDropDownCloseReason _exitReason;
    private bool _exited;
    private bool _animating;

    private FluidSelectDrop(Control trigger, IReadOnlyList<ChipChoice> choices, bool preferAbove)
    {
        AutoSize = false;
        Padding = Padding.Empty;
        Margin = Padding.Empty;
        DropShadowEnabled = false;
        BackColor = PhosphorTheme.Surface;
        Renderer = new PhosphorMenuRenderer();
        float s = PhosphorPaint.Scale(trigger);
        _list = new FluidSelectList(this, choices, s, _openedAt) { Margin = Padding.Empty };
        var size = _list.Measure(Math.Max(trigger.Width, (int)(220 * s)));
        _list.Size = size;
        _host = new ToolStripControlHost(_list) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = size };
        Items.Add(_host);
        Size = size;

        var at = trigger.RectangleToScreen(trigger.ClientRectangle);
        var work = Screen.FromRectangle(at).WorkingArea;
        int gap = (int)Math.Round(FluidSelectSpec.Gap * s);
        bool fitsBelow = at.Bottom + gap + size.Height <= work.Bottom;
        bool fitsAbove = at.Top - gap - size.Height >= work.Top;
        _above = preferAbove ? fitsAbove || !fitsBelow : !fitsBelow && fitsAbove;
        int y = _above ? at.Top - gap - size.Height : at.Bottom + gap;
        int x = Math.Clamp(at.Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - size.Width - 8));
        Location = new Point(x, Math.Clamp(y, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - size.Height - 8)));
        Reveal(PhosphorMotion.ReducedMotion ? 1f : 0f);
    }

    /// <summary>Opens the drop for <paramref name="trigger"/>; <paramref name="preferAbove"/> flips when there is no room.</summary>
    public static FluidSelectDrop Open(Control trigger, IReadOnlyList<ChipChoice> choices, bool preferAbove)
    {
        var drop = new FluidSelectDrop(trigger, choices, preferAbove) { OwnerControl = trigger };
        drop.Show(drop.Location);
        return drop;
    }

    /// <summary>Picks <paramref name="choice"/>: the drop closes and the pick runs once the click has finished.</summary>
    internal void Choose(ChipChoice choice)
    {
        Close(ToolStripDropDownCloseReason.ItemClicked);
        var pick = choice.Pick;
        if (OwnerControl is { IsHandleCreated: true } owner) owner.BeginInvoke(pick);
        else pick();
    }

    /// <summary>The trigger the drop belongs to, for running the pick on its thread.</summary>
    internal Control? OwnerControl { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PhosphorTips.Suppressed = true;
        _list.Focus();
        Animate();
    }

    protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
    {
        // WinForms closes a dropdown whenever focus changes. Omniwand's own windows (a tooltip, a toast, a widget a
        // gesture summoned) can take focus while it is open; keep it open for those. Any other app, a click outside,
        // Esc or a pick still close it.
        if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange && !_exited && SuggestionChip.OwnsForeground())
        {
            e.Cancel = true;
            // The window that took focus (the topmost drawer it opened from, a toast) would cover the drop it kept.
            BeginInvoke(new Action(() => { if (!IsDisposed && Visible) BringToFront(); }));
        }
        SuggestionChip.LogMenuClose(OwnerControl?.Name ?? "FluidSelect", e.CloseReason, e.Cancel);
        base.OnClosing(e);
        if (e.Cancel || _exited) return;
        // The exit is the open run backwards (height and opacity to 0 on the panel beat); the drop closes at its end.
        if (PhosphorMotion.ReducedMotion || !Visible) return;
        e.Cancel = true;
        if (_exitAt < 0)
        {
            _exitAt = SurfaceMotion.Now;
            _exitReason = e.CloseReason;
            Animate();
        }
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        base.OnClosed(e);
        StopAnimating();
        PhosphorTips.Suppressed = false;
        BeginInvoke(new Action(Dispose));
    }

    protected override bool ProcessDialogKey(Keys keyData) => _list.HandleKey(keyData) || base.ProcessDialogKey(keyData);

    /// <summary>Presses <paramref name="key"/> in the list, as the keyboard does (for the as-built capture).</summary>
    internal void Press(Keys key) => _list.HandleKey(key);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => _list.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private void Animate()
    {
        if (_animating || PhosphorMotion.ReducedMotion) { Tick(); return; }
        _animating = true;
        SurfaceMotion.Frame += Tick;
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        SurfaceMotion.Frame -= Tick;
    }

    private void Tick()
    {
        if (IsDisposed) { StopAnimating(); return; }
        long now = SurfaceMotion.Now;
        if (_exitAt >= 0)
        {
            float p = PhosphorMotion.ReducedMotion ? 1f : Math.Clamp((now - _exitAt) / (float)FluidSelectSpec.PanelMs, 0f, 1f);
            float e = PhosphorMotion.EaseDisclosure(p);
            Opacity = 1f - e;
            Reveal(1f - e);
            if (p >= 1f)
            {
                _exited = true;
                StopAnimating();
                Close(_exitReason);
            }
            return;
        }
        float open = PhosphorMotion.ReducedMotion ? 1f : Math.Clamp((now - _openedAt) / (float)FluidSelectSpec.PanelMs, 0f, 1f);
        Reveal(PhosphorMotion.EaseDisclosure(open));
        bool listMoving = _list.Animating(now);
        _list.Invalidate();
        if (open >= 1f && !listMoving) StopAnimating();
    }

    /// <summary>Wakes the shared clock for the list's highlight glide.</summary>
    internal void Wake() => Animate();

    /// <summary>Shows the top (or, opened above, the bottom) <paramref name="e"/> of the panel: the height reveal.</summary>
    private void Reveal(float e)
    {
        int h = Math.Max(1, (int)Math.Round(Height * Math.Clamp(e, 0f, 1f)));
        float s = _list.ScaleFactor;
        var box = _above ? new RectangleF(0, Height - h, Width, h) : new RectangleF(0, 0, Width, h);
        using var path = PhosphorTheme.CreateRoundedRect(box, FluidSelectSpec.RadiusOpen * s);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }
}

/// <summary>The drop's panel and listbox: groups, options, rules, the gliding aura and the scroll.</summary>
internal sealed class FluidSelectList : Control, IRowTips
{
    private enum Kind { Group, Option, Rule }

    private sealed class Row
    {
        public required Kind Kind;
        public ChipChoice? Choice;
        public string Text = "";
        public int Index;
        public float Top, Height;
    }

    private readonly FluidSelectDrop _drop;
    private readonly List<Row> _rows = new();
    private readonly List<Row> _options = new();
    private readonly long _openedAt;
    private readonly float _s;
    private int _active = -1;
    private int _selected = -1;
    private float _scroll;
    private float _contentHeight;
    // The aura's glide: from where it stood to the active (or chosen) row, on the panel beat.
    private float _hlFrom, _hlTo, _hlHeight;
    private long _hlAt = long.MinValue / 2;

    public FluidSelectList(FluidSelectDrop drop, IReadOnlyList<ChipChoice> choices, float s, long openedAt)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _drop = drop;
        _s = s;
        _openedAt = openedAt;
        BackColor = PhosphorTheme.Surface;
        Name = "FluidSelect.List";
        AccessibleRole = AccessibleRole.List;
        string? group = null;
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            if (c.IsHeader)
            {
                // A heading after options draws the group label; a rule between groups reads as the "All" divider.
                group = c.Text;
                _rows.Add(new Row { Kind = Kind.Group, Text = group });
                continue;
            }
            var row = new Row { Kind = Kind.Option, Choice = c, Index = _options.Count };
            _rows.Add(row);
            _options.Add(row);
            if (c.Selected) _selected = row.Index;
            if (c.SeparatorAfter) _rows.Add(new Row { Kind = Kind.Rule });
        }
        _active = -1;
    }

    public float ScaleFactor => _s;

    private float PanelPad => FluidSelectSpec.U * _s;
    private float ListPad => 8f * _s;

    /// <summary>Lays the rows out and returns the panel size: at least <paramref name="minWidth"/>, the list capped at --u * 72.</summary>
    public Size Measure(int minWidth)
    {
        float y = 0, width = minWidth;
        using var g = PhosphorPaint.MeasureGraphics(_s);
        using var label = PhosphorFonts.Ui(PhosphorTheme.TextBody);
        using var mono = PhosphorFonts.Mono(PhosphorTheme.TextData);
        foreach (var row in _rows)
        {
            row.Top = y;
            row.Height = row.Kind switch
            {
                Kind.Group => 8f * _s + mono.GetHeight(g) + FluidSelectSpec.U * _s,
                Kind.Rule => (FluidSelectSpec.U * 2.5f * 2f + 1f) * _s,
                _ => FluidSelectSpec.RowHeight * _s
            };
            y += row.Height;
            if (row.Choice is { } c)
            {
                float w = TextLeft + g.MeasureString(c.Text, label).Width + 16f * _s + PanelPad * 2;
                if (!string.IsNullOrEmpty(c.Note)) w += 24f * _s + NoteWidth(g, mono, c.Note!);
                width = Math.Max(width, w);
            }
        }
        _contentHeight = y;
        float list = Math.Min(y, FluidSelectSpec.MaxListHeight * _s);
        if (_selected >= 0) Glide(_selected, instant: true);
        width = Math.Min(width, 480f * _s);
        return new Size((int)Math.Ceiling(width), (int)Math.Ceiling(list + (PanelPad + ListPad) * 2));
    }

    /// <summary>The label's left edge: --u inside the list, then the aura lead, then the 16px tone slot and its --s2.</summary>
    private float TextLeft => PanelPad + (FluidSelectSpec.U + PhosphorTheme.AuraLeadPx) * _s + 24f * _s;

    private static float NoteWidth(Graphics g, Font mono, string note)
    {
        float tracking = mono.Size * 0.12f * g.DpiX / 72f;
        return PhosphorPaint.MeasureTracked(g, note.ToUpperInvariant(), mono, tracking);
    }

    private RectangleF ListBox => new(PanelPad, PanelPad + ListPad, Width - PanelPad * 2, Height - (PanelPad + ListPad) * 2);
    private float MaxScroll => Math.Max(0, _contentHeight - ListBox.Height);

    /// <summary>The row's box on screen-of-the-control coordinates, after the scroll.</summary>
    private RectangleF Box(Row row) => new(ListBox.X, ListBox.Y + row.Top - _scroll, ListBox.Width, row.Height);

    private int OptionAt(Point p)
    {
        foreach (var row in _options)
            if (Box(row).Contains(p) && ListBox.Contains(p)) return row.Index;
        return -1;
    }

    /// <summary>Whether a stagger, the aura glide or the reveal still moves at <paramref name="now"/>.</summary>
    public bool Animating(long now)
    {
        if (PhosphorMotion.ReducedMotion) return false;
        long staggerEnd = _openedAt + FluidSelectSpec.StaggerCap * FluidSelectSpec.StaggerMs + FluidSelectSpec.ItemMs;
        return now < staggerEnd || now < _hlAt + FluidSelectSpec.PanelMs;
    }

    private void Glide(int option, bool instant = false)
    {
        if (option < 0 || option >= _options.Count) return;
        var row = _options[option];
        float now = CurrentHighlightY();
        _hlFrom = instant || _hlHeight == 0 ? row.Top : now;
        _hlTo = row.Top;
        _hlHeight = row.Height;
        _hlAt = instant ? long.MinValue / 2 : SurfaceMotion.Now;
        if (!instant) _drop.Wake();
    }

    private float CurrentHighlightY()
    {
        if (PhosphorMotion.ReducedMotion) return _hlTo;
        float p = Math.Clamp((SurfaceMotion.Now - _hlAt) / (float)FluidSelectSpec.PanelMs, 0f, 1f);
        return _hlFrom + (_hlTo - _hlFrom) * PhosphorMotion.EaseDisclosure(p);
    }

    /// <summary>The active row, or the chosen one when nothing is active (as onHoverEnd falls back to the value).</summary>
    private int Shown => _active >= 0 ? _active : _selected;

    private void SetActive(int option)
    {
        if (option == _active) return;
        _active = option;
        if (Shown >= 0) Glide(Shown);
        KeepInView(Shown);
        Invalidate();
    }

    private void KeepInView(int option)
    {
        if (option < 0) return;
        var row = _options[option];
        float view = ListBox.Height;
        if (row.Top < _scroll) _scroll = row.Top;
        else if (row.Top + row.Height > _scroll + view) _scroll = row.Top + row.Height - view;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    /// <summary>The listbox keys; true when handled.</summary>
    public bool HandleKey(Keys key)
    {
        if (_options.Count == 0) return false;
        int from = Shown < 0 ? -1 : Shown;
        switch (key)
        {
            case Keys.Down: SetActive(Math.Min(_options.Count - 1, from + 1)); return true;
            case Keys.Up: SetActive(Math.Max(0, from < 0 ? 0 : from - 1)); return true;
            case Keys.Home: SetActive(0); return true;
            case Keys.End: SetActive(_options.Count - 1); return true;
            case Keys.Enter:
            case Keys.Space:
                if (Shown >= 0 && _options[Shown].Choice is { } c) _drop.Choose(c);
                return true;
            case Keys.Escape:
                _drop.Close(ToolStripDropDownCloseReason.Keyboard);
                return true;
        }
        return false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter or Keys.Space or Keys.Escape || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.KeyData)) e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetActive(OptionAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetActive(-1);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int at = OptionAt(e.Location);
        if (at >= 0 && _options[at].Choice is { } c) _drop.Choose(c);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * FluidSelectSpec.RowHeight * _s, 0, MaxScroll);
        SetActive(OptionAt(e.Location));
        Invalidate();
    }

    /// <summary>Entry progress of row <paramref name="index"/> on the 12ms stagger (capped), 0 to 1.</summary>
    private float Entry(int index)
    {
        if (PhosphorMotion.ReducedMotion) return 1f;
        long start = _openedAt + Math.Min(index, FluidSelectSpec.StaggerCap) * FluidSelectSpec.StaggerMs;
        return PhosphorMotion.EaseDisclosure(Math.Clamp((SurfaceMotion.Now - start) / (float)FluidSelectSpec.ItemMs, 0f, 1f));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var panel = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = FluidSelectSpec.RadiusOpen * _s;
        PhosphorPaint.FillRounded(g, panel, radius, PhosphorTheme.Surface);
        PhosphorPaint.StrokeRounded(g, panel, radius, PhosphorTheme.Line2);

        var list = ListBox;
        var clip = g.Save();
        g.SetClip(list);

        // The gliding aura: an inset cell --u inside the list, lit from its leading edge.
        if (Shown >= 0 && _hlHeight > 0)
        {
            float y = list.Y + CurrentHighlightY() - _scroll;
            var cell = Rectangle.Round(new RectangleF(list.X + FluidSelectSpec.U * _s, y, list.Width - FluidSelectSpec.U * 2 * _s, _hlHeight));
            PhosphorTheme.DrawHighlightAura(g, cell, PhosphorTheme.Coral, 1f, (int)Math.Round(PhosphorTheme.RadiusBadge * _s));
        }

        using var label = PhosphorFonts.Ui(PhosphorTheme.TextBody);
        using var mono = PhosphorFonts.Mono(PhosphorTheme.TextData);
        int entryIndex = 0;
        foreach (var row in _rows)
        {
            var box = Box(row);
            if (box.Bottom < list.Top || box.Top > list.Bottom) { entryIndex++; continue; }
            float a = Entry(entryIndex++);
            int alpha = (int)Math.Round(255 * a);
            float lift = (1f - a) * FluidSelectSpec.U * _s;
            switch (row.Kind)
            {
                case Kind.Group:
                    PhosphorPaint.DrawEyebrow(g, row.Text, Color.FromArgb(alpha, PhosphorTheme.Ghost), list.X + 16f * _s,
                        box.Top + 8f * _s + lift, _s);
                    break;
                case Kind.Rule:
                {
                    using var pen = new Pen(Color.FromArgb(alpha, PhosphorTheme.Line2), Math.Max(1f, _s));
                    float y = box.Top + box.Height / 2f;
                    g.DrawLine(pen, list.X + 16f * _s, y, list.Right - 16f * _s, y);
                    break;
                }
                default:
                {
                    var c = row.Choice!;
                    bool lit = row.Index == _active || row.Index == _selected;
                    float cy = box.Top + box.Height / 2f + lift;
                    // The tone slot: dim at rest; the active row's lifts (x1.2) and takes its tone.
                    float dot = 6f * _s * (row.Index == _active ? 1.2f : 1f);
                    float slot = TextLeft - 24f * _s + 8f * _s;
                    var tone = row.Index == _active ? (c.Tone ?? PhosphorTheme.Coral) : (c.Tone is { } t && row.Index == _selected ? t : PhosphorTheme.Ghost);
                    using (var b = new SolidBrush(Color.FromArgb(alpha, tone))) g.FillEllipse(b, slot - dot / 2f, cy - dot / 2f, dot, dot);
                    float right = list.Right - 16f * _s;
                    if (!string.IsNullOrEmpty(c.Note))
                    {
                        string note = c.Note!.ToUpperInvariant();
                        float tracking = mono.Size * 0.12f * g.DpiX / 72f;
                        float w = PhosphorPaint.MeasureTracked(g, note, mono, tracking) - tracking;
                        using var ink = new SolidBrush(Color.FromArgb(alpha, lit ? PhosphorTheme.Dim : PhosphorTheme.Ghost));
                        PhosphorPaint.DrawTracked(g, note, mono, ink, right - w, cy - mono.GetHeight(g) / 2f, tracking);
                        right -= w + 24f * _s;
                    }
                    using var text = new SolidBrush(Color.FromArgb(alpha, lit ? PhosphorTheme.Bone : PhosphorTheme.Dim));
                    using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
                    g.DrawString(c.Text, label, text, new RectangleF(TextLeft, cy - box.Height / 2f, Math.Max(10f, right - TextLeft), box.Height), fmt);
                    break;
                }
            }
        }
        g.Restore(clip);

        // A thin thumb when the list scrolls, in the dim of the dark scrollbars.
        if (MaxScroll > 0)
        {
            float track = list.Height, thumb = Math.Max(24f * _s, track * list.Height / _contentHeight);
            float y = list.Y + (track - thumb) * (_scroll / MaxScroll);
            PhosphorPaint.FillRounded(g, new RectangleF(list.Right - 4f * _s, y, 3f * _s, thumb), 1.5f * _s, PhosphorTheme.Line2);
        }
    }

    public (object Key, Rectangle Anchor, PhosphorTipInfo Info)? TipAt(Point screen)
    {
        int at = OptionAt(PointToClient(screen));
        if (at < 0 || _options[at].Choice is not { Tip.Length: > 0 } c) return null;
        return (c, RectangleToScreen(Rectangle.Round(Box(_options[at]))), new PhosphorTipInfo(c.Text, c.Tip!));
    }

    public bool TipsAlive => !IsDisposed && Visible;
}
