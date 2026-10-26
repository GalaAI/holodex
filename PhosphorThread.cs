using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>What a thread card is, which sets its edge and kicker ink.</summary>
internal enum ThreadTone { Intro, You, Agent, Step, Action, Approval, Ok, Fail }

/// <summary>
/// One card in a conversation thread, in the AInbox card language: the <see cref="StackPaint.Card"/> panel with an
/// accent edge, a tracked mono kicker (WHO // MODEL), and wrapped body text. An action card carries the tool's
/// icon tile like a <see cref="CapturedRow"/> and takes its result in place, with a Moss or Ember dot. An approval
/// card carries Skip and Approve, like the Aegis card carries Stow in Vault, and settles to the operator's answer.
/// </summary>
internal sealed class ThreadCard : Control
{
    private const float Pad = 14f;
    private const float Tile = 32f;
    private readonly PhosphorButton? _approve;
    private readonly PhosphorButton? _skip;
    // Its own flag: Control.Visible reads false while the form is hidden, which would drop the buttons' room.
    private bool _waiting;

    public ThreadCard(ThreadTone tone, string kicker, string? body, PhosphorGlyph? glyph = null)
    {
        Tone = tone;
        Kicker = kicker;
        Body = body?.Trim() ?? "";
        Glyph = glyph;
        Name = $"Thread.{tone}Card";
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 6);
        if (tone == ThreadTone.Approval)
        {
            _approve = new PhosphorButton("Approve") { Name = "Thread.ApproveButton" };
            _skip = new PhosphorButton("Skip") { Name = "Thread.SkipButton" };
            _approve.Click += (_, _) => Answered?.Invoke(true);
            _skip.Click += (_, _) => Answered?.Invoke(false);
            _waiting = true;
            Controls.Add(_approve);
            Controls.Add(_skip);
        }
    }

    public ThreadTone Tone { get; private set; }
    public string Kicker { get; private set; }
    public string Body { get; private set; }
    public PhosphorGlyph? Glyph { get; }
    public string? Result { get; private set; }
    public bool ResultOk { get; private set; }

    /// <summary>Raised by an approval card's buttons: true for Approve.</summary>
    public event Action<bool>? Answered;

    /// <summary>An action card's outcome, drawn under its call.</summary>
    public void SetResult(string text, bool ok)
    {
        Result = text.Trim();
        ResultOk = ok;
        (Parent as ThreadView)?.Relayout();
        Invalidate();
    }

    /// <summary>Settles an approval card to the operator's answer and removes its buttons.</summary>
    public void Settle(bool approved)
    {
        if (_approve == null || _skip == null) return;
        _waiting = false;
        _approve.Visible = _skip.Visible = false;
        Tone = approved ? ThreadTone.Ok : ThreadTone.Intro;
        Kicker = approved ? $"YOU APPROVED // {Kicker}" : $"YOU SKIPPED // {Kicker}";
        (Parent as ThreadView)?.Relayout();
        Invalidate();
    }

    private bool Waiting => _waiting;

    private float TextLeft(float s) => Glyph != null ? Pad * s + Tile * s + 10f * s : Pad * s;

    private float ButtonsHeight(float s) => Waiting ? 52f * s : 0f;

    public int HeightFor(int width)
    {
        float s = PhosphorComposerScale.Of(this);
        float textW = Math.Max(40f, width - TextLeft(s) - Pad * s);
        float h = 12f * s + 16f * s; // kicker line
        if (Body.Length > 0) h += 4f * s + StackPaint.MeasureWrapped(Body, BodyPx, textW, this);
        if (Result is { Length: > 0 }) h += 8f * s + 16f * s + StackPaint.MeasureWrapped(Result, 12f, textW - 14f * s, this);
        h += ButtonsHeight(s) + 12f * s;
        if (Glyph != null) h = Math.Max(h, Tile * s + 24f * s);
        return (int)Math.Ceiling(h);
    }

    private float BodyPx => Tone == ThreadTone.Intro ? 12.5f : 13f;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_approve == null || _skip == null) return;
        float s = PhosphorPaint.Scale(this);
        int bh = (int)Math.Round(40f * s), gap = (int)Math.Round(8f * s), pad = (int)Math.Round(Pad * s);
        _approve.Size = new Size((int)Math.Round(120f * s), bh);
        _skip.Size = new Size((int)Math.Round(96f * s), bh);
        _approve.Location = new Point(Width - pad - _approve.Width, Height - pad + (int)Math.Round(4f * s) - bh);
        _skip.Location = new Point(_approve.Left - gap - _skip.Width, _approve.Top);
    }

    private Color Edge => Tone switch
    {
        ThreadTone.You => PhosphorTheme.Line2,
        ThreadTone.Agent => PhosphorTheme.Coral,
        ThreadTone.Step => PhosphorTheme.Ghost,
        ThreadTone.Action => PhosphorTheme.Patina,
        ThreadTone.Approval => PhosphorTheme.Ember,
        ThreadTone.Ok => PhosphorTheme.Moss,
        ThreadTone.Fail => PhosphorTheme.Ember,
        _ => PhosphorTheme.Line
    };

    private Color KickerInk => Tone switch
    {
        ThreadTone.You => PhosphorTheme.Dim,
        ThreadTone.Agent => PhosphorTheme.Coral,
        ThreadTone.Action => PhosphorTheme.Patina,
        ThreadTone.Approval => PhosphorTheme.Ember,
        ThreadTone.Ok => PhosphorTheme.Moss,
        ThreadTone.Fail => PhosphorTheme.Ember,
        _ => PhosphorTheme.Ghost
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = PhosphorPaint.Scale(this);
        StackPaint.Card(g, this, Tone == ThreadTone.Intro ? null : Edge, active: Waiting);

        if (Glyph is PhosphorGlyph glyph)
        {
            float bx = Pad * s, by = 12f * s, box = Tile * s;
            var boxRect = new RectangleF(bx, by, box, box);
            PhosphorPaint.FillRounded(g, boxRect, 9f * s, PhosphorTheme.Ground);
            PhosphorPaint.StrokeRounded(g, boxRect, 9f * s, PhosphorTheme.Line2);
            float icon = 15f * s;
            PhosphorIcons.Draw(g, glyph, new RectangleF(bx + (box - icon) / 2f, by + (box - icon) / 2f, icon, icon), PhosphorTheme.Cream, 1.7f * s);
        }

        float x = TextLeft(s), w = Width - x - Pad * s, y = 12f * s;
        var st = g.Save();
        g.SetClip(new RectangleF(x, 0, w, Height));
        StackPaint.Tracked(g, Kicker, 9.5f, KickerInk, x, y, this);
        g.Restore(st);
        y += 16f * s;

        if (Body.Length > 0)
        {
            y += 4f * s;
            Color ink = Tone == ThreadTone.Intro ? PhosphorTheme.Dim : PhosphorTheme.Bone;
            y += StackPaint.Wrapped(g, Body, BodyPx, ink, new RectangleF(x, y, w, Height - y), this);
        }

        if (Result is { Length: > 0 } result)
        {
            y += 8f * s;
            float dot = 7f * s;
            Color ink = ResultOk ? PhosphorTheme.Moss : PhosphorTheme.Ember;
            using (var b = new SolidBrush(ink)) g.FillEllipse(b, x, y + 4f * s, dot, dot);
            StackPaint.Tracked(g, ResultOk ? "RESULT" : "RESULT // NOT DONE", 9f, ink, x + 14f * s, y, this);
            y += 16f * s;
            StackPaint.Wrapped(g, result, 12f, PhosphorTheme.Dim, new RectangleF(x + 14f * s, y, w - 14f * s, Height - y), this);
        }
    }
}

/// <summary>
/// A scrolling conversation of <see cref="ThreadCard"/>s, newest at the bottom. Wiz, Holodex and the HUD share it,
/// so every surface that talks shows the same cards as the AInbox stack.
/// </summary>
internal sealed class ThreadView : FlowLayoutPanel
{
    public ThreadView(string name)
    {
        Name = name;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = PhosphorTheme.Surface;
        Padding = new Padding(0);
        PhosphorPaint.UseDarkScrollbars(this);
        Resize += (_, _) => Relayout();
    }

    public IEnumerable<ThreadCard> Cards => Controls.OfType<ThreadCard>();

    public ThreadCard Add(ThreadTone tone, string kicker, string? body = null, PhosphorGlyph? glyph = null)
    {
        var card = new ThreadCard(tone, kicker, body, glyph);
        Controls.Add(card);
        Relayout();
        ScrollControlIntoView(card);
        return card;
    }

    public void Clear()
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        Controls.Clear();
    }

    /// <summary>Total content height at a width, for forms that size to their thread.</summary>
    public int ContentHeight(int width) => Cards.Sum(c => c.HeightFor(width) + c.Margin.Vertical);

    /// <summary>Raised after cards are added, resized or settled, so a form can fit the thread.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Card width: the client width less a scrollbar's, always reserved so text never rewraps under it.</summary>
    public int CardWidth => Math.Max(80, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);

    public void Relayout()
    {
        int w = CardWidth;
        SuspendLayout();
        foreach (var card in Cards) card.Size = new Size(w, card.HeightFor(w));
        ResumeLayout(true);
        var last = Cards.LastOrDefault();
        if (last != null) ScrollControlIntoView(last);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A row of status LEDs: a dot and a tracked line each, in the AInbox footer's SYNCED and QUEUED style. Lines that
/// do not fit are dropped from the end rather than cut mid-word, so the row never runs off the card.
/// </summary>
internal class LedRow : Control
{
    public LedRow()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    public IReadOnlyList<(Color Led, string Text)> Leds { get; set; } = Array.Empty<(Color, string)>();

    public void Set(params (Color Led, string Text)[] leds)
    {
        Leds = leds;
        Invalidate();
    }

    /// <summary>The whole status as plain text, for tooltips and as-built checks.</summary>
    public string Summary => string.Join(" // ", Leds.Select(l => l.Text));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = PhosphorPaint.Scale(this);
        float dot = 6f * s, cy = Height / 2f, x = 0;
        foreach (var (led, text) in Leds)
        {
            float need = dot + 7f * s + StackPaint.MeasureTracked(g, text, 9f, this, 0.9f);
            if (x > 0 && x + need > Width) break;
            using (var b = new SolidBrush(led)) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);
            x += dot + 7f * s;
            x += StackPaint.Tracked(g, text, 9f, PhosphorTheme.Dim, x, cy - 6f * s, this, 0.9f) + 16f * s;
        }
    }
}
