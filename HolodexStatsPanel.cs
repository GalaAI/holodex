using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The skill tree: the overall level in the middle, the ten skills on a ring around it. Each node shows its level,
/// an arc for progress to the next level, and its band; a notch marks the next Crucible checkpoint.
/// </summary>
internal sealed class SkillTreeView : Control
{
    private IReadOnlyList<SkillStanding> _standings = Array.Empty<SkillStanding>();
    private int _overall = 1;
    private string _center = "YOU";
    private int _hover = -1;

    public event Action<Skill>? SkillClicked;

    public SkillTreeView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = PhosphorTheme.Surface;
        Height = 360;
        Dock = DockStyle.Top;
        Name = "Holodex.SkillTree";
        Cursor = Cursors.Hand;
    }

    public void Show(IReadOnlyList<SkillStanding> standings, int overall, string center)
    {
        _standings = standings;
        _overall = overall;
        _center = center;
        Invalidate();
    }

    private (PointF Center, float Ring, float Node) Geometry()
    {
        float s = PhosphorPaint.Scale(this);
        var c = new PointF(Width / 2f, Height / 2f);
        float node = 26 * s;
        float ring = Math.Min(Width, Height) / 2f - node - 22 * s;
        return (c, ring, node);
    }

    private PointF NodeAt(int i, int count, PointF c, float ring)
    {
        double a = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, count);
        return new PointF(c.X + (float)(Math.Cos(a) * ring), c.Y + (float)(Math.Sin(a) * ring));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (c, ring, node) = Geometry();
        int hit = -1;
        for (int i = 0; i < _standings.Count; i++)
        {
            var p = NodeAt(i, _standings.Count, c, ring);
            if (Math.Pow(e.X - p.X, 2) + Math.Pow(e.Y - p.Y, 2) <= node * node) hit = i;
        }
        if (hit == _hover) return;
        _hover = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_hover >= 0 && _hover < _standings.Count) SkillClicked?.Invoke(_standings[_hover].Skill);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(PhosphorTheme.Surface);
        float s = PhosphorPaint.Scale(this);
        var (c, ring, node) = Geometry();
        int n = _standings.Count;

        using var spoke = new Pen(PhosphorTheme.Line, 1 * s);
        using var ringPen = new Pen(PhosphorTheme.Line, 1 * s) { DashStyle = DashStyle.Dot };
        g.DrawEllipse(ringPen, c.X - ring, c.Y - ring, ring * 2, ring * 2);
        for (int i = 0; i < n; i++) g.DrawLine(spoke, c, NodeAt(i, n, c, ring));

        // Center: the overall level (display only, capped at 15 over the lowest skill, as in Ark).
        float core = 44 * s;
        using (var coreFill = new SolidBrush(PhosphorTheme.Surface2)) g.FillEllipse(coreFill, c.X - core, c.Y - core, core * 2, core * 2);
        using (var coreLine = new Pen(PhosphorTheme.Patina, 2 * s)) g.DrawEllipse(coreLine, c.X - core, c.Y - core, core * 2, core * 2);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var big = PhosphorFonts.Display(26)) using (var bone = new SolidBrush(PhosphorTheme.Bone))
            g.DrawString(_overall.ToString(), big, bone, new RectangleF(c.X - core, c.Y - core - 6 * s, core * 2, core * 2), center);
        using (var eyebrow = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx)) using (var dim = new SolidBrush(PhosphorTheme.Dim))
            g.DrawString(_center, eyebrow, dim, new RectangleF(c.X - core, c.Y + 12 * s, core * 2, 16 * s), center);

        using var levelFont = PhosphorFonts.Ui(15, heavy: true);
        using var labelFont = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx);
        for (int i = 0; i < n; i++)
        {
            var st = _standings[i];
            var p = NodeAt(i, n, c, ring);
            var rect = new RectangleF(p.X - node, p.Y - node, node * 2, node * 2);
            bool hot = i == _hover;
            using (var fill = new SolidBrush(hot ? PhosphorTheme.Line2 : PhosphorTheme.Surface2)) g.FillEllipse(fill, rect);
            using (var track = new Pen(PhosphorTheme.Line, 4 * s)) g.DrawEllipse(track, rect);
            if (st.Progress > 0)
                using (var arc = new Pen(BandColor(st.Band), 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, rect, -90, (float)(360 * st.Progress));
            using (var bone = new SolidBrush(PhosphorTheme.Bone)) g.DrawString(st.Level.ToString(), levelFont, bone, rect, center);
            using (var dim = new SolidBrush(hot ? PhosphorTheme.Bone : PhosphorTheme.Dim))
                g.DrawString(st.Skill.ToString().ToUpperInvariant(), labelFont, dim, new RectangleF(p.X - 60 * s, p.Y + node + 3 * s, 120 * s, 14 * s), center);
        }

        if (_hover >= 0 && _hover < n)
        {
            var st = _standings[_hover];
            string tip = $"{st.Skill} // level {st.Level} // {st.Band.Replace('_', ' ')} // {st.XpIntoLevel:0} of {st.XpForNext:0} XP" +
                         (st.NextCrucible is int cp ? $" // Crucible at {cp}" : "");
            using var dim = new SolidBrush(PhosphorTheme.Bone);
            g.DrawString(tip, labelFont, dim, new RectangleF(0, Height - 18 * s, Width, 16 * s), center);
        }
    }

    private static Color BandColor(string band) => band switch
    {
        "mastery" or "advanced" => PhosphorTheme.Coral,
        "proficient" or "bounded_verified" => PhosphorTheme.Moss,
        _ => PhosphorTheme.Patina
    };
}

/// <summary>A labelled 0 to 100 bar (focus, consistency, curiosity).</summary>
internal sealed class TraitBars : Control
{
    private IReadOnlyDictionary<string, int> _traits = new Dictionary<string, int>();

    public TraitBars()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 64;
        Name = "Holodex.TraitBars";
    }

    public void Show(IReadOnlyDictionary<string, int> traits) { _traits = traits; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(PhosphorTheme.Surface);
        float s = PhosphorPaint.Scale(this);
        using var font = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx);
        using var dim = new SolidBrush(PhosphorTheme.Dim);
        using var track = new SolidBrush(PhosphorTheme.Line);
        using var fill = new SolidBrush(PhosphorTheme.Patina);
        float row = 20 * s, labelW = 110 * s;
        int i = 0;
        foreach (var (name, value) in _traits.OrderBy(kv => kv.Key))
        {
            float y = i++ * row + 2 * s;
            g.DrawString($"{name.ToUpperInvariant()} {value}", font, dim, 0, y);
            float w = Width - labelW - 4 * s;
            g.FillRectangle(track, labelW, y + 6 * s, w, 4 * s);
            g.FillRectangle(fill, labelW, y + 6 * s, w * Math.Clamp(value, 0, 100) / 100f, 4 * s);
        }
    }
}

/// <summary>
/// The Stats tab: You, Agents and Apex tracks on one skill tree, traits, the quest board with Start and Send proof,
/// and the Apex evaluation, daily recap and ArkHub share.
/// </summary>
internal sealed class HolodexStatsPanel : Panel
{
    private readonly HolodexProgressionService _svc;
    private readonly SkillTreeView _tree = new();
    private readonly TraitBars _traits = new();
    private readonly Panel _quests = new() { Dock = DockStyle.Top, Height = 10, BackColor = PhosphorTheme.Surface, Name = "Holodex.QuestList" };
    private readonly EyebrowLabel _trackEyebrow = new("STATS // YOU") { Dock = DockStyle.Top };
    private readonly DataLabel _status = new("") { Dock = DockStyle.Top };
    private ProgressTrack _track = ProgressTrack.Human;

    public HolodexStatsPanel(HolodexProgressionService svc)
    {
        _svc = svc;
        Dock = DockStyle.Fill;
        BackColor = PhosphorTheme.Surface;
        Visible = false;
        Name = "Holodex.StatsPanel";
        AutoScroll = true;

        var trackRow = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = PhosphorTheme.Surface };
        foreach (var (track, label) in new[] { (ProgressTrack.Apex, "Apex"), (ProgressTrack.Agent, "Agents"), (ProgressTrack.Human, "You") })
        {
            var b = new PhosphorButton(label) { Dock = DockStyle.Left, Width = 120, Name = $"Holodex.Stats.Track.{track}" };
            b.Click += (_, _) => { _track = track; Refresh(); };
            trackRow.Controls.Add(b);
            trackRow.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        }

        var actionRow = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = PhosphorTheme.Surface };
        // ArkHub lives only on the owner's Hivemind; elsewhere the share goes to a Discord webhook the user saves.
        var share = new PhosphorButton(RealmConnectors.HivemindServesArkHub(HivemindClient.Endpoint) ? "Share to ArkHub" : "Share progress")
            { Dock = DockStyle.Right, Width = 170, Name = "Holodex.Stats.Share" };
        share.Click += async (_, _) => await ShareAsync();
        var recap = new PhosphorButton("Daily recap") { Dock = DockStyle.Left, Width = 150, Name = "Holodex.Stats.Recap" };
        recap.Click += async (_, _) => await RecapAsync();
        var apex = new PhosphorButton("Apex evaluation", PhosphorButtonVariant.Primary) { Dock = DockStyle.Left, Width = 180, Name = "Holodex.Stats.Apex" };
        apex.Click += (_, _) => new ApexEvaluationForm(_svc, Refresh).Show(FindForm());
        actionRow.Controls.Add(share);
        actionRow.Controls.Add(recap);
        actionRow.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        actionRow.Controls.Add(apex);

        _tree.SkillClicked += skill =>
        {
            var st = _svc.Ledger.Standing(SubjectFor(_track), skill);
            _status.Text = $"{skill.ToString().ToUpperInvariant()} // LEVEL {st.Level} // {st.TotalXp:0} XP // {st.Band.Replace('_', ' ').ToUpperInvariant()}";
        };

        // Docking runs from the last control added, so these go bottom first.
        Controls.Add(_quests);
        Controls.Add(new EyebrowLabel("QUESTS // MISSIONS // GAUNTLETS") { Dock = DockStyle.Top });
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_traits);
        Controls.Add(new EyebrowLabel("TRAITS // FROM YOUR HABITS AND APEX") { Dock = DockStyle.Top });
        Controls.Add(_status);
        Controls.Add(_tree);
        Controls.Add(_trackEyebrow);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(actionRow);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(trackRow);

        _svc.Ledger.Changed += RefreshSoon;
        _svc.Board.Changed += RefreshSoon;
    }

    private void RefreshSoon()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(new Action(Refresh));
    }

    private string SubjectFor(ProgressTrack track) => track switch
    {
        ProgressTrack.Apex => "apex",
        ProgressTrack.Agent => _svc.Ledger.Subjects(ProgressTrack.Agent).FirstOrDefault() ?? "gala",
        _ => "user"
    };

    public override void Refresh()
    {
        string subject = SubjectFor(_track);
        var standings = _svc.Ledger.Standings(subject);
        _tree.Show(standings, _svc.Ledger.OverallLevel(subject), subject == "user" ? "YOU" : subject.ToUpperInvariant());
        var apex = _svc.Ledger.Apex;
        _trackEyebrow.Text = _track switch
        {
            ProgressTrack.Human => apex is null ? "STATS // YOU // TAKE THE APEX EVALUATION TO SET YOUR START" : $"STATS // YOU // APEX BASELINE {apex.TakenAt:yyyy-MM-dd}",
            ProgressTrack.Agent => "STATS // AGENTS // " + string.Join(" ", _svc.Ledger.Subjects(ProgressTrack.Agent).Select(s => s.ToUpperInvariant())),
            _ => "STATS // APEX EVOLUTION"
        };
        _traits.Show(_svc.Ledger.Traits(subject));
        BuildQuestRows();
        base.Refresh();
    }

    private void BuildQuestRows()
    {
        _quests.SuspendLayout();
        _quests.Controls.Clear();
        var open = _svc.Board.Open.Take(5).ToList();
        foreach (var q in open.AsEnumerable().Reverse())
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = PhosphorTheme.Surface, Name = "Holodex.Quest." + q.Id };
            var title = new DataLabel($"{q.Kind.ToString().ToUpperInvariant()} // {q.Skill.ToString().ToUpperInvariant()} // {q.Title}") { Dock = DockStyle.Top };
            var detail = new DataLabel(q.State == QuestState.Submitted ? "With the Watcher. " + q.Feedback : q.Feedback.Length > 0 ? q.Feedback : Trim(q.Briefing, 90)) { Dock = DockStyle.Top };
            var go = new PhosphorButton(q.Kind == QuestKind.Gauntlet ? "Start" : "Send proof") { Dock = DockStyle.Right, Width = 140, Name = "Holodex.Quest.Go." + q.Id };
            go.Enabled = q.State != QuestState.Submitted;
            go.Click += (_, _) => StartQuest(q);
            row.Controls.Add(detail);
            row.Controls.Add(title);
            row.Controls.Add(go);
            _quests.Controls.Add(row);
        }
        _quests.Height = Math.Max(24, open.Count * 64);
        _quests.ResumeLayout(true);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "...";

    private void StartQuest(Quest q)
    {
        if (q.Kind == QuestKind.Gauntlet)
        {
            _svc.Board.Update(q.Id, x => x with { State = QuestState.Accepted });
            new GauntletForm(_svc, q).Show(FindForm());
            return;
        }
        using var pick = new OpenFileDialog { Title = $"Proof for {q.Title}", Multiselect = true, Filter = "Images and files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf;*.md;*.txt|All files|*.*" };
        if (pick.ShowDialog(FindForm()) != DialogResult.OK || pick.FileNames.Length == 0) return;
        _status.Text = "SENDING PROOF // AINBOX // WATCHER REVIEWING";
        _ = _svc.SubmitProofAsync(q, pick.FileNames, "submitted from the Stats page");
    }

    private async Task RecapAsync()
    {
        _status.Text = "GALA // WRITING YOUR DAILY RECAP";
        var (ok, text, events) = await _svc.Recap().ComposeAsync(Environment.UserName, HolodexProgressionService.Habits());
        _status.Text = ok ? $"RECAP // {events.Count} EVENTS" : "RECAP // NOT WRITTEN";
        PhosphorToast.Show("Your daily recap", text, "GALA // RECAP", 14000, ok ? ToastTone.Info : ToastTone.Warning);
        _svc.Activity(Skill.Learning, "Read the daily recap");
    }

    private async Task ShareAsync()
    {
        var dest = _svc.Herald.Destinations();
        if (dest.Count == 0)
        {
            if (RealmConnectors.HivemindServesArkHub(HivemindClient.Endpoint))
                PhosphorToast.Show("ArkHub is not set up", "Save a Discord webhook as Omniwand:DiscordWebhook in Credential Manager, or turn on the ArkHub slot in connectors.json.", "HERALD // ARKHUB", 6000, ToastTone.Warning);
            else
                PhosphorToast.Show("Nowhere to share yet", "ArkHub is not on the public Hivemind. To share to Discord, save a Discord webhook as Omniwand:DiscordWebhook in Credential Manager.", "HOLODEX // SHARE", 6000, ToastTone.Info);
            return;
        }
        var standings = _svc.Ledger.Standings("user");
        int overall = _svc.Ledger.OverallLevel("user");
        var top = standings.OrderByDescending(x => x.Level).First();
        var share = new ArkHubShare($"{Environment.UserName} reached overall level {overall}",
            $"Top skill: {top.Skill} at level {top.Level}. Shared from Holodex.", standings, overall);
        if (MessageBox.Show(FindForm(), $"Post your levels to {string.Join(", ", dest)}?", "Share to ArkHub", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var (result, posted) = await _svc.Herald.PublishAsync(share);
        PhosphorToast.Show(result.Ok ? "Shared to ArkHub" : "Not shared", result.Output, "HERALD // ARKHUB", 4200, result.Ok ? ToastTone.Success : ToastTone.Warning);
        if (result.Ok) _svc.Activity(Skill.Social, "Shared progress to ArkHub");
    }
}

/// <summary>
/// The Apex evaluation: a 0 to 10 rating and a "what can you do" line per skill, goals, and an opt-in for the
/// reasoning and emotional baselines. Nemotron Super turns it into starting levels and traits.
/// </summary>
internal sealed class ApexEvaluationForm : Form
{
    private readonly HolodexProgressionService _svc;
    private readonly Action _done;
    private readonly Dictionary<Skill, PhosphorField> _ratings = new();
    private readonly Dictionary<Skill, PhosphorField> _canDo = new();
    private readonly PhosphorField _goals = new("What do you want to get better at this year?", multiline: true, heightPx: 64);
    private readonly PhosphorCheckBox _baselines = new("Also take the reasoning and emotional baselines later (optional)") { Dock = DockStyle.Top, Height = 32 };
    private readonly PhosphorButton _submit = new("Evaluate", PhosphorButtonVariant.Primary) { Dock = DockStyle.Bottom, Name = "Apex.Submit" };

    public ApexEvaluationForm(HolodexProgressionService svc, Action done)
    {
        _svc = svc;
        _done = done;
        Text = "Apex evaluation";
        Name = "Apex.EvaluationForm";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PhosphorTheme.Surface;
        Size = new Size(620, 760);
        TopMost = true;

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PhosphorTheme.Surface, Padding = new Padding(16, 8, 16, 8) };
        var rows = new List<Control>();
        foreach (Skill s in Enum.GetValues<Skill>())
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = PhosphorTheme.Surface };
            var rating = new PhosphorField("0-10", multiline: false, heightPx: 36, mono: true) { Dock = DockStyle.Left, Width = 70 };
            var can = new PhosphorField($"{s}: what can you do today?", multiline: false, heightPx: 36) { Dock = DockStyle.Fill };
            row.Controls.Add(can);
            row.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
            row.Controls.Add(rating);
            _ratings[s] = rating;
            _canDo[s] = can;
            rows.Add(new EyebrowLabel(s.ToString().ToUpperInvariant()) { Dock = DockStyle.Top });
            rows.Add(row);
        }
        rows.Add(new EyebrowLabel("GOALS") { Dock = DockStyle.Top });
        rows.Add(_goals);
        rows.Add(_baselines);
        rows.Add(new DataLabel("Apex sets where each skill starts. It is separate from the Genesis profile Gala keeps about you.") { Dock = DockStyle.Top });
        foreach (var c in Enumerable.Reverse(rows)) body.Controls.Add(c);

        _submit.Click += async (_, _) => await SubmitAsync();
        Controls.Add(body);
        Controls.Add(_submit);
        WidgetChrome.Attach(this, "APEX // EVALUATION", "Where you start", PhosphorTheme.Patina, null);
        WidgetDrag.Enable(this);
    }

    private async Task SubmitAsync()
    {
        _submit.Enabled = false;
        _submit.Text = "Evaluating";
        var ratings = _ratings.ToDictionary(kv => kv.Key, kv => int.TryParse(kv.Value.Box.Text.Trim(), out int r) ? Math.Clamp(r, 0, 10) : 0);
        var canDo = _canDo.ToDictionary(kv => kv.Key, kv => kv.Value.Box.Text.Trim());
        var evaluation = await _svc.Apex.EvaluateAsync(new ApexEvaluator.Answers(ratings, canDo, _goals.Box.Text.Trim()));
        _svc.Ledger.SetApexEvaluation(evaluation);
        // Apex itself grows each time it evaluates someone (the Apex track on the Stats page).
        try
        {
            _svc.Ledger.Award("apex", ProgressTrack.Apex, Skill.Learning, new XpAwardInput(2, QualityComponents.Neutral, 0.6, 1),
                EvidenceKind.Activity, "apex-evaluation:" + evaluation.TakenAt.ToUnixTimeSeconds(), "apex", "Evaluated a new user");
        }
        catch (Exception) { }
        PhosphorToast.Show("Apex evaluation saved", evaluation.Summary.Length > 0 ? evaluation.Summary : "Your starting levels are on the Stats page.",
            "APEX // EVALUATION", 6000, ToastTone.Success);
        _done();
        Close();
    }
}

/// <summary>
/// A running Gauntlet: the briefing, a 3 second count in, then the timer and the Watcher's notes as they come.
/// Frames come from the Windows Camera window when it is open (point the webcam at the desk), else the screen
/// under this window.
/// </summary>
internal sealed class GauntletForm : Form
{
    private readonly HolodexProgressionService _svc;
    private readonly Quest _quest;
    private readonly Label _clock = new() { Dock = DockStyle.Top, Height = 72, TextAlign = ContentAlignment.MiddleCenter, ForeColor = PhosphorTheme.Bone, BackColor = PhosphorTheme.Surface };
    private readonly DataLabel _source = new("") { Dock = DockStyle.Top };
    private readonly Label _notes = new() { Dock = DockStyle.Fill, ForeColor = PhosphorTheme.Dim, BackColor = PhosphorTheme.Surface };
    private readonly PhosphorButton _go = new("Ready", PhosphorButtonVariant.Primary) { Dock = DockStyle.Bottom, Name = "Gauntlet.Go" };
    private CancellationTokenSource? _cts;

    public GauntletForm(HolodexProgressionService svc, Quest quest)
    {
        _svc = svc;
        _quest = quest;
        Name = "Gauntlet.Form";
        Text = quest.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PhosphorTheme.Surface;
        Size = new Size(520, 460);
        TopMost = true;
        _clock.Font = PhosphorFonts.Display(40);
        _notes.Font = PhosphorFonts.Body(14);
        _notes.Text = quest.Briefing;
        _clock.Text = TimeSpan.FromSeconds(quest.TimeLimitSeconds).ToString(@"m\:ss");
        _source.Text = CameraWindow() is null ? "CAMERA // OPEN THE CAMERA APP AND POINT IT AT YOUR DESK" : "CAMERA // WINDOWS CAMERA FOUND";
        _go.Click += async (_, _) => await GoAsync();
        Controls.Add(_notes);
        Controls.Add(_source);
        Controls.Add(_clock);
        Controls.Add(_go);
        WidgetChrome.Attach(this, $"GAUNTLET // {quest.Skill.ToString().ToUpperInvariant()}", quest.Title, PhosphorTheme.Ember, null);
        WidgetDrag.Enable(this);
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private async Task GoAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _go.Text = "Stop";
        for (int i = 3; i > 0; i--) { _clock.Text = i.ToString(); await Task.Delay(1000); }
        _cts = new CancellationTokenSource();
        var session = new GauntletSession(_quest, _svc.Model, _svc.Watcher, _svc.Ledger, CaptureFrame)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_quest.TimeLimitSeconds / 8, 20, 90))
        };
        session.Tick += left => BeginInvoke(() => _clock.Text = left.ToString(@"m\:ss"));
        session.Observed += note => BeginInvoke(() => _notes.Text = "WATCHER // " + note);
        _notes.Text = "Go. The Watcher is looking.";
        var (verdict, award) = await session.RunAsync(_cts.Token);
        _clock.Text = award is null ? "HELD" : $"+{award.VerifiedXp:0} XP";
        _notes.Text = verdict.Note + $"\n\n{session.Observations.Count} observations.";
        _go.Text = "Close";
        _go.Click += (_, _) => Close();
        _svc.Board.Update(_quest.Id, q => q with { State = award is null ? QuestState.Failed : QuestState.Completed, Feedback = verdict.Note });
        PhosphorToast.Show(award is null ? "Gauntlet held" : $"Gauntlet passed // +{award.VerifiedXp:0} {_quest.Skill} XP", verdict.Note,
            "WATCHER // GAUNTLET", 7000, award is null ? ToastTone.Warning : ToastTone.Success);
    }

    private string? CaptureFrame()
    {
        Rectangle area = CameraWindow() ?? Screen.FromControl(this).Bounds;
        string? url = null;
        Invoke(() =>
        {
            // Step out of the frame while it is taken when it overlaps.
            bool hide = area.IntersectsWith(Bounds);
            if (hide) Opacity = 0;
            url = ArkStationClient.CaptureRectangleRegion(area);
            if (hide) Opacity = 1;
        });
        return url;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rectangle? CameraWindow()
    {
        foreach (var p in Process.GetProcessesByName("WindowsCamera"))
        {
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero || IsIconic(h) || !GetWindowRect(h, out var r)) continue;
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width > 100 && rect.Height > 100) return rect;
        }
        // The Camera app is packaged, so its window usually belongs to ApplicationFrameHost under the title "Camera".
        IntPtr frame = FindWindow("ApplicationFrameWindow", "Camera");
        if (frame != IntPtr.Zero && !IsIconic(frame) && GetWindowRect(frame, out var f))
        {
            var rect = Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom);
            if (rect.Width > 100 && rect.Height > 100) return rect;
        }
        return null;
    }
}
