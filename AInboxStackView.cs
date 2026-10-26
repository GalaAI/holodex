using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>Shared painting for the AInbox stack cards: the card panel with an optional accent edge.</summary>
internal static class StackPaint
{
    public static void Card(Graphics g, Control c, Color? edge, bool active = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(c);
        var rect = new RectangleF(0.5f, 0.5f, c.Width - 1f, c.Height - 1f);
        float r = 14f * s;
        PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, active ? PhosphorTheme.Ghost : PhosphorTheme.Line);
        if (edge is Color e)
        {
            var st = g.Save();
            using (var clip = PhosphorTheme.CreateRoundedRect(rect, r)) g.SetClip(clip);
            using (var b = new SolidBrush(Color.FromArgb(200, e))) g.FillRectangle(b, 0, 0, 3f * s, c.Height);
            using (var glow = new LinearGradientBrush(new RectangleF(0, 0, 16f * s, c.Height), Color.FromArgb(46, e), Color.FromArgb(0, e), LinearGradientMode.Horizontal))
                g.FillRectangle(glow, 0, 0, 16f * s, c.Height);
            g.Restore(st);
        }
    }

    public static float Tracked(Graphics g, string text, float px, Color color, float x, float y, Control c, float tracking = 1.1f)
    {
        float s = PhosphorPaint.Scale(c);
        using var font = PhosphorFonts.Mono(px * s);
        using var b = new SolidBrush(color);
        return PhosphorPaint.DrawTracked(g, text.ToUpperInvariant(), font, b, x, y, tracking * s);
    }

    public static float MeasureTracked(Graphics g, string text, float px, Control c, float tracking = 1.1f)
    {
        float s = PhosphorPaint.Scale(c);
        using var font = PhosphorFonts.Mono(px * s);
        return PhosphorPaint.MeasureTracked(g, text.ToUpperInvariant(), font, tracking * s);
    }

    /// <summary>Wrapped body text in a box; returns the height used.</summary>
    public static float Wrapped(Graphics g, string text, float px, Color color, RectangleF box, Control c)
    {
        float s = PhosphorPaint.Scale(c);
        using var font = PhosphorFonts.Body(px * s);
        using var b = new SolidBrush(color);
        var size = g.MeasureString(text, font, (int)box.Width);
        g.DrawString(text, font, b, box);
        return size.Height;
    }

    public static float MeasureWrapped(string text, float px, float width, Control c)
    {
        float s = PhosphorComposerScale.Of(c);
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        using var font = PhosphorFonts.Body(px * s);
        return g.MeasureString(text, font, (int)Math.Max(1, width)).Height;
    }

    public static void Dots(Graphics g, float x, float y, Control c, int phase)
    {
        float s = PhosphorPaint.Scale(c);
        for (int i = 0; i < 3; i++)
        {
            int a = (phase + i) % 3 == 0 ? 255 : 90;
            using var b = new SolidBrush(Color.FromArgb(a, PhosphorTheme.Patina));
            g.FillEllipse(b, x + i * 9f * s, y, 5f * s, 5f * s);
        }
    }
}

/// <summary>
/// One context group in the stack: a lettered header (A // FROM CHROME // PR 1068 // 3) over its captures, each a
/// <see cref="CapturedRow"/> with its own remove button. Clicking the header makes it the active group.
/// </summary>
internal sealed class StackGroupView : Panel
{
    private const int HeaderPx = 26;
    private const int RowPx = 48;
    private const int GapPx = 4;
    private readonly HeaderStrip _header;

    public StackGroupView(StackGroup group, bool active, Action<StackItem> remove, Action activate)
    {
        Group = group;
        BackColor = Color.Transparent;
        Name = "AInbox.StackGroup";
        _header = new HeaderStrip(this) { Dock = DockStyle.Top, Height = HeaderPx, Name = "AInbox.StackGroup" };
        _header.Click += (_, _) => activate();
        var rows = new List<Control>();
        foreach (var it in group.Items)
        {
            var row = new CapturedRow("AInbox.CapturedRow")
            {
                Dock = DockStyle.Top,
                Height = RowPx,
                Kind = it.Kind,
                ItemName = it.Name,
                Meta = it.Meta + (it.Secrets.Count > 0 ? $" // AEGIS {it.Secrets.Count}" : ""),
                Prediction = it.Origin.Element is { Length: > 0 } el && it.Kind is CaptureKind.Pick ? el : ""
            };
            var captured = it;
            row.RemoveClicked += (_, _) => remove(captured);
            rows.Add(new Panel { Dock = DockStyle.Top, Height = GapPx, BackColor = Color.Transparent });
            rows.Add(row);
        }
        rows.Reverse();
        foreach (var r in rows) Controls.Add(r);
        Controls.Add(_header);
        Active = active;
        Height = HeaderPx + group.Items.Count * (RowPx + GapPx);
    }

    public StackGroup Group { get; }
    public bool Active { get; set; }

    private sealed class HeaderStrip : Control
    {
        private readonly StackGroupView _owner;

        public HeaderStrip(StackGroupView owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = PhosphorPaint.Scale(this);
            var box = new RectangleF(0.5f, 3f * s, 18f * s, 18f * s);
            Color ink = _owner.Active ? PhosphorTheme.Coral : PhosphorTheme.Ghost;
            PhosphorPaint.StrokeRounded(g, box, 5f * s, ink, _owner.Active ? 1.5f * s : 1f);
            using (var f = PhosphorFonts.Mono(9.5f * s))
            using (var b = new SolidBrush(_owner.Active ? PhosphorTheme.Cream : PhosphorTheme.Bone))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(_owner.Group.Letter, f, b, box, fmt);
            var st = g.Save();
            g.SetClip(new RectangleF(26f * s, 0, Width - 26f * s, Height));
            StackPaint.Tracked(g, $"{_owner.Group.Label} // {_owner.Group.Items.Count}", 9.5f, _owner.Active ? PhosphorTheme.Dim : PhosphorTheme.Ghost, 26f * s, 7f * s, this);
            g.Restore(st);
        }
    }
}

/// <summary>
/// WHAT SHOULD A BECOME: Omnitek's ranked intents for one group, each with its target pod and orchestrator, a
/// confidence bar and percentage; the chosen row is lit. Then WHY, the synthesis (Nemotron's, when it has run),
/// and the PULSE line naming the inbox that will be pulsed. Click a row to steer.
/// </summary>
internal sealed class PredictionCard : Control
{
    private const float RowPx = 32f;
    private int _hoverRow = -1;
    private int _phase;
    private readonly System.Windows.Forms.Timer _dots = new() { Interval = 300 };

    public PredictionCard(StackGroup group, string omnitekLine, Action<string> choose, Action activate)
    {
        Group = group;
        OmnitekLine = omnitekLine;
        _choose = choose;
        _activate = activate;
        Name = "AInbox.Prediction";
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        _dots.Tick += (_, _) => { _phase++; Invalidate(); };
        if (group.Refining) _dots.Start();
    }

    private readonly Action<string> _choose;
    private readonly Action _activate;

    public StackGroup Group { get; }
    public string OmnitekLine { get; }
    public bool Active { get; set; }

    private float HeadPx => 52f;
    private float RowsTop => HeadPx;

    /// <summary>Height for this card at <paramref name="width"/> in logical pixels: head, rows, why, synthesis, pulse.</summary>
    public int HeightFor(int width)
    {
        float s = PhosphorComposerScale.Of(this);
        float syn = Group.Synthesis.Length > 0 || Group.Refining ? 20f * s + StackPaint.MeasureWrapped(Group.Synthesis.Length > 0 ? Group.Synthesis : "x", 12f, width - 36f * s, this) : 0;
        return (int)Math.Ceiling((HeadPx + Group.Ranking.Count * RowPx + 30f + 26f + 14f) * s + syn);
    }

    private int RowAt(Point p)
    {
        float s = PhosphorPaint.Scale(this);
        float y = p.Y - RowsTop * s;
        int i = (int)Math.Floor(y / (RowPx * s));
        return y >= 0 && i < Group.Ranking.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int r = RowAt(e.Location);
        if (r != _hoverRow) { _hoverRow = r; Cursor = r >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverRow = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        _activate();
        int r = RowAt(e.Location);
        if (r >= 0) _choose(Group.Ranking[r].Intent.Id);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = Group.Ranking.FindIndex(o => o.Intent.Id == Group.ChosenIntent);
        if (e.KeyCode == Keys.Down && i < Group.Ranking.Count - 1) { _choose(Group.Ranking[i + 1].Intent.Id); e.Handled = true; }
        if (e.KeyCode == Keys.Up && i > 0) { _choose(Group.Ranking[i - 1].Intent.Id); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        StackPaint.Card(g, this, Active ? PhosphorTheme.Coral : null, Active);
        if (Focused) PhosphorPaint.StrokeRounded(g, new RectangleF(2, 2, Width - 4, Height - 4), 12f * s, Color.FromArgb(160, PhosphorTheme.Patina));
        float x = 16f * s, w = Width - 32f * s;

        // Head: the question, who answered it, and Omnitek's read of the item type.
        StackPaint.Tracked(g, $"WHAT SHOULD {Group.Letter} BECOME // PREDICTED, STEER ANY TIME", 9.5f, PhosphorTheme.Ghost, x, 12f * s, this);
        string who = Group.Refining ? "NEMOTRON 3 NANO // RE-RANKING" : Group.WhySource == "NEMOTRON" ? "RANKED BY NEMOTRON 3 NANO" : "RANKED BY OMNITEK";
        float ww = StackPaint.MeasureTracked(g, who, 9f, this);
        StackPaint.Tracked(g, who, 9f, Group.WhySource == "NEMOTRON" || Group.Refining ? PhosphorTheme.Patina : PhosphorTheme.Ghost, Width - x - ww, 12f * s, this);
        StackPaint.Tracked(g, OmnitekLine, 9f, PhosphorTheme.Dim, x, 30f * s, this, 0.9f);

        // Ranked rows: radio, label, target pod and orchestrator, bar, percentage.
        float y = RowsTop * s;
        for (int i = 0; i < Group.Ranking.Count; i++)
        {
            var o = Group.Ranking[i];
            bool on = o.Intent.Id == Group.ChosenIntent;
            var row = new RectangleF(8f * s, y + 1f * s, Width - 16f * s, RowPx * s - 2f * s);
            if (on || i == _hoverRow)
            {
                PhosphorPaint.FillRounded(g, row, 8f * s, on ? PhosphorTheme.Surface : Color.FromArgb(120, PhosphorTheme.Surface));
                if (on)
                {
                    using var edge = new SolidBrush(PhosphorTheme.Coral);
                    g.FillRectangle(edge, row.X, row.Y + 4f * s, 2f * s, row.Height - 8f * s);
                }
            }
            float cy = y + RowPx * s / 2f;
            var ring = new RectangleF(x, cy - 7f * s, 14f * s, 14f * s);
            using (var p = new Pen(on ? PhosphorTheme.Bone : PhosphorTheme.Line2, 1f)) g.DrawEllipse(p, ring);
            if (on) using (var b = new SolidBrush(PhosphorTheme.Bone)) g.FillEllipse(b, RectangleF.Inflate(ring, -4f * s, -4f * s));
            using (var f = PhosphorFonts.Ui(12f * s, heavy: true))
            using (var b = new SolidBrush(on ? PhosphorTheme.Bone : PhosphorTheme.Dim))
                g.DrawString(o.Intent.Label, f, b, x + 24f * s, cy - 8f * s, PhosphorPaint.Typographic);
            string pct = $"{Math.Round(o.Score * 100)}%";
            float barW = 70f * s, pctW = 36f * s;
            float barX = Width - x - pctW - barW - 6f * s;
            string target = $"{o.Pod.Label} · {ShortName(o.Pod.OrchestratorLabel)}";
            float tw = StackPaint.MeasureTracked(g, target, 9f, this, 0.8f);
            StackPaint.Tracked(g, target, 9f, PhosphorTheme.Ghost, barX - 12f * s - tw, cy - 5f * s, this, 0.8f);
            PhosphorPaint.FillRounded(g, new RectangleF(barX, cy - 2f * s, barW, 4f * s), 2f * s, PhosphorTheme.Line);
            if (o.Score > 0.005f) PhosphorPaint.FillRounded(g, new RectangleF(barX, cy - 2f * s, Math.Max(3f * s, barW * o.Score), 4f * s), 2f * s, on ? PhosphorTheme.Bone : PhosphorTheme.Ghost);
            float pw = StackPaint.MeasureTracked(g, pct, 9.5f, this, 0.6f);
            StackPaint.Tracked(g, pct, 9.5f, PhosphorTheme.Dim, Width - x - pw, cy - 5f * s, this, 0.6f);
            y += RowPx * s;
        }

        // WHY, the synthesis, and the inbox that will be pulsed.
        y += 6f * s;
        var st = g.Save();
        g.SetClip(new RectangleF(x, y, w, 20f * s));
        StackPaint.Tracked(g, $"WHY // {Group.Why}", 9f, PhosphorTheme.Dim, x, y + 2f * s, this, 0.7f);
        g.Restore(st);
        y += 24f * s;
        if (Group.Synthesis.Length > 0 || Group.Refining)
        {
            StackPaint.Tracked(g, "OMNITEK // SYNTHESIS", 9f, PhosphorTheme.Ghost, x, y, this);
            if (Group.Refining) StackPaint.Dots(g, x + StackPaint.MeasureTracked(g, "OMNITEK // SYNTHESIS", 9f, this) + 8f * s, y + 3f * s, this, _phase);
            y += 16f * s;
            if (Group.Synthesis.Length > 0) y += StackPaint.Wrapped(g, Group.Synthesis, 12f, PhosphorTheme.Bone, new RectangleF(x, y, w, Height - y), this) + 4f * s;
        }
        var pod = Group.Chosen.Pod;
        StackPaint.Tracked(g, $"PULSE // {ShortName(pod.OrchestratorLabel)} @ {pod.InboxChannel}", 9f, PhosphorTheme.ResolveTokenColor(pod.AccentToken), x, Height - 22f * s, this, 0.9f);
    }

    private static string ShortName(string label)
    {
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? label[..paren] : label;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dots.Stop(); _dots.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The Aegis gate: how many secrets the stack carries, where, masked, and that they are redacted before anything is
/// sent. Stow in Vault keeps each one in Windows Credential Manager.
/// </summary>
internal sealed class AegisCard : Control
{
    private readonly PhosphorButton _stow;

    public AegisCard(IReadOnlyList<(string Where, AegisFinding Finding)> findings, bool imagesUnscanned, Action stow)
    {
        Findings = findings;
        ImagesUnscanned = imagesUnscanned;
        Name = "AInbox.Aegis";
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        _stow = new PhosphorButton("Stow in Vault") { Width = 150, Name = "AInbox.StowButton", Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _stow.Click += (_, _) => stow();
        _stow.Visible = findings.Count > 0;
        Controls.Add(_stow);
    }

    public IReadOnlyList<(string Where, AegisFinding Finding)> Findings { get; }
    public bool ImagesUnscanned { get; }

    public string Sentence => Findings.Count == 0
        ? "No secrets in the text captures. Images and recordings are not scanned."
        : string.Join("; ", Findings.Take(3).Select(f => $"{f.Finding.Kind} in {f.Where} ({f.Finding.Masked})"))
          + (Findings.Count > 3 ? $"; {Findings.Count - 3} more" : "")
          + ". Redacted before anything is sent." + (ImagesUnscanned ? " Images are not scanned." : "");

    public int HeightFor(int width)
    {
        float s = PhosphorComposerScale.Of(this);
        return (int)Math.Ceiling(40f * s + StackPaint.MeasureWrapped(Sentence, 12f, width - 32f * s - (Findings.Count > 0 ? 166f * s : 0), this) + 14f * s);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        float s = PhosphorPaint.Scale(this);
        _stow.Height = (int)Math.Round(40f * s);
        _stow.Width = (int)Math.Round(150f * s);
        _stow.Location = new Point(Width - _stow.Width - (int)Math.Round(14f * s), (Height - _stow.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        bool hit = Findings.Count > 0;
        StackPaint.Card(g, this, hit ? PhosphorTheme.Ember : PhosphorTheme.Line2);
        float x = 16f * s;
        float dot = 7f * s;
        using (var b = new SolidBrush(hit ? PhosphorTheme.Ember : PhosphorTheme.Moss)) g.FillEllipse(b, x, 15f * s, dot, dot);
        StackPaint.Tracked(g, hit ? $"AEGIS // {Findings.Count} SECRET{(Findings.Count == 1 ? "" : "S")} FOUND" : "AEGIS // CLEAR", 9.5f, hit ? PhosphorTheme.Ember : PhosphorTheme.Moss, x + 14f * s, 12f * s, this);
        float right = hit ? 166f * s : 0;
        StackPaint.Wrapped(g, Sentence, 12f, PhosphorTheme.Dim, new RectangleF(x, 32f * s, Width - 2 * x - right, Height - 34f * s), this);
    }
}

/// <summary>
/// One adaptive Scroll field: "3 // REPRO" on the left, where the value came from on the right (PREDICTED,
/// FROM STACK A, NEMOTRON, YOU EDITED), and the editable value under it.
/// </summary>
internal sealed class ScrollFieldRow : Panel
{
    private readonly FieldHead _head;
    private bool _setting;

    public ScrollFieldRow(int index, ScrollField field, Action<string> edited)
    {
        BackColor = Color.Transparent;
        Name = "AInbox.ScrollField";
        Field = new PhosphorField("", true, 44, mono: true) { Dock = DockStyle.Top, Height = 44, Name = "AInbox.ScrollFieldBox" };
        _head = new FieldHead { Dock = DockStyle.Top, Height = 18, Label = $"{index} // {field.Label}", Source = field.Source };
        Controls.Add(Field);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Color.Transparent });
        Controls.Add(_head);
        Height = 18 + 4 + 44;
        Set(field);
        Field.Box.TextChanged += (_, _) => { if (!_setting) { _head.Source = "YOU EDITED"; _head.Invalidate(); edited(Field.Box.Text); } };
    }

    public PhosphorField Field { get; }

    public void Set(ScrollField f)
    {
        _setting = true;
        if (Field.Box.Text != f.Value) Field.Box.Text = f.Value;
        _setting = false;
        _head.Source = f.Source;
        _head.Invalidate();
    }

    private sealed class FieldHead : Control
    {
        public string Label { get; set; } = "";
        public string Source { get; set; } = "";

        public FieldHead()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = PhosphorPaint.Scale(this);
            StackPaint.Tracked(g, Label, 9.5f, PhosphorTheme.Ghost, 0, 3f * s, this, 1.2f);
            Color ink = Source switch
            {
                "YOU EDITED" => PhosphorTheme.Bone,
                "NEMOTRON" => PhosphorTheme.Patina,
                _ when Source.StartsWith("FROM") => PhosphorTheme.Cream,
                _ => PhosphorTheme.Patina
            };
            float w = StackPaint.MeasureTracked(g, Source, 8.5f, this, 1f);
            StackPaint.Tracked(g, Source, 8.5f, Source == "PREDICTED" ? PhosphorTheme.Ghost : ink, Width - w, 4f * s, this, 1f);
        }
    }
}

/// <summary>
/// Delivery status, the design target's SYNCED and QUEUED lines: a dot and a line each, from the outbound queue.
/// </summary>
internal sealed class SyncStatus : Control
{
    public SyncStatus()
    {
        Name = "AInbox.SyncStatus";
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    public int Pending { get; set; }
    public int SentToday { get; set; }
    public bool? Online { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = PhosphorPaint.Scale(this);
        float dot = 6f * s, cy = Height / 2f;
        float x = 0;
        void Led(Color c, string text)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);
            x += dot + 7f * s;
            x += StackPaint.Tracked(g, text, 9f, PhosphorTheme.Dim, x, cy - 6f * s, this, 0.9f) + 16f * s;
        }
        Led(Online == true ? PhosphorTheme.Moss : Online == false ? PhosphorTheme.Ember : PhosphorTheme.Ghost,
            Online == true ? $"SYNCED // {SentToday} TO AINBOX TODAY" : Online == false ? "AINBOX :8840 OFFLINE" : "AINBOX // CHECKING");
        if (Pending > 0) Led(PhosphorTheme.Seal, $"QUEUED {Pending} // RETRYING");
    }
}
