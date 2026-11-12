using System.Diagnostics;

namespace Holodex;

/// <summary>
/// Horizon, the daily missions log, calendar and checklist mod, in its own window off the drawer's Mods tab. One
/// day at a time: the calendar, then the checklist, then the missions log, each drawn from the
/// <see cref="IHorizonProvider"/> the account's mode chooses. A part that is signed out, not in Ark yet or
/// unreachable is a card that says so in that part's place; the rest of the day still shows. Native Phosphor: the
/// kit's cards, checkbox, field and buttons, tokens only.
/// </summary>
internal sealed class HorizonForm : Form
{
    private readonly Func<IHorizonProvider> _choose;
    private readonly HorizonList _list;
    private readonly EyebrowLabel _dayLabel;
    private readonly DataLabel _status;
    private readonly PhosphorField _entry;
    private readonly PhosphorButton _addCheck;
    private readonly PhosphorButton _addLog;
    private IHorizonProvider _provider;
    private DateOnly _day;
    private int _load;

    /// <summary>Today on this PC; renders and tests pin it.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Opens the Vault (Program owns it), for a calendar that needs the user's Google client first.</summary>
    public Action? OpenVault { get; set; }

    /// <summary>Runs the calendar's browser sign-in; renders and tests swap it.</summary>
    public Func<CancellationToken, Task> ConnectCalendar { get; set; } = GoogleCalendarSource.SignInAsync;

    /// <param name="choose">Picks the provider for the account as it is now. Asked again on every refresh, so signing in to or out of Hivemind changes the mode the next time the day is read.</param>
    public HorizonForm(Func<IHorizonProvider> choose)
    {
        _choose = choose;
        _provider = choose();
        _day = Today();
        PhosphorFonts.EnsureLoaded();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "Horizon";
        Text = "Horizon";
        Size = new Size(620, 780);
        Padding = WidgetChrome.FramePadding();

        _list = new HorizonList("Horizon.List") { Dock = DockStyle.Fill };

        // Day strip: earlier and later either side of the day, Today on the right.
        var earlier = new PhosphorButton("Earlier") { Name = "Horizon.EarlierButton", Dock = DockStyle.Left, Width = 104 };
        var later = new PhosphorButton("Later") { Name = "Horizon.LaterButton", Dock = DockStyle.Left, Width = 104 };
        var today = new PhosphorButton("Today") { Name = "Horizon.TodayButton", Dock = DockStyle.Right, Width = 104 };
        earlier.Click += (_, _) => ShowDay(_day.AddDays(-1));
        later.Click += (_, _) => ShowDay(_day.AddDays(1));
        today.Click += (_, _) => ShowDay(Today());
        _dayLabel = new EyebrowLabel("") { Name = "Horizon.Day", Dock = DockStyle.Fill, Accent = PhosphorTheme.Bone };
        var strip = new Panel { Name = "Horizon.DayStrip", Dock = DockStyle.Top, Height = PhosphorTheme.TouchMin, BackColor = PhosphorTheme.Surface };
        strip.Controls.Add(_dayLabel);
        strip.Controls.Add(new Panel { Dock = DockStyle.Left, Width = PhosphorTheme.S3, BackColor = PhosphorTheme.Surface });
        strip.Controls.Add(later);
        strip.Controls.Add(new Panel { Dock = DockStyle.Left, Width = PhosphorTheme.S2, BackColor = PhosphorTheme.Surface });
        strip.Controls.Add(earlier);
        strip.Controls.Add(today);

        _status = new DataLabel("") { Name = "Horizon.Status", Height = 22 };

        // One line in, two places it can go: the checklist or the missions log.
        _entry = new PhosphorField("A mission for the day, or a note on what happened", multiline: false, heightPx: PhosphorTheme.TouchMin) { Name = "Horizon.Entry", Dock = DockStyle.Fill };
        _addCheck = new PhosphorButton("Add to checklist") { Name = "Horizon.AddCheckButton", Dock = DockStyle.Right, Width = 168 };
        _addLog = new PhosphorButton("Log it") { Name = "Horizon.AddLogButton", Dock = DockStyle.Right, Width = 96 };
        _addCheck.Click += async (_, _) => await AddAsync(toLog: false);
        _addLog.Click += async (_, _) => await AddAsync(toLog: true);
        _entry.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            // Enter adds a checklist item; Shift+Enter writes the line to the log.
            await AddAsync(toLog: e.Shift);
        };
        var composer = new Panel { Name = "Horizon.Composer", Dock = DockStyle.Bottom, Height = PhosphorTheme.TouchMin, BackColor = PhosphorTheme.Surface };
        composer.Controls.Add(_entry);
        composer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = PhosphorTheme.S2, BackColor = PhosphorTheme.Surface });
        composer.Controls.Add(_addCheck);
        composer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = PhosphorTheme.S2, BackColor = PhosphorTheme.Surface });
        composer.Controls.Add(_addLog);

        Controls.Add(_list);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = PhosphorTheme.S2, BackColor = PhosphorTheme.Surface });
        Controls.Add(composer);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(_status);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S1));
        Controls.Add(strip);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "HOLODEX // HORIZON", "Horizon", PhosphorTheme.Patina, null, liveEyebrow: () => $"HOLODEX // HORIZON // {_provider.Label}");
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { SurfaceMotion.Dismiss(this); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }

    /// <summary>Shows Horizon on the cursor's screen at today, and reads the day.</summary>
    public void Summon(Point cursorPos)
    {
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Height = Math.Min(Height, screen.Height - 40);
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + Math.Max(20, (screen.Height - Height) / 2));
        if (!Visible) SurfaceMotion.Present(this);
        Activate();
        ShowDay(Today());
    }

    private void ShowDay(DateOnly day)
    {
        _day = day;
        _ = RefreshAsync();
    }

    /// <summary>
    /// Reads the three parts for the day on screen and draws them. A newer read (another day, another refresh)
    /// supersedes this one: its answers are dropped rather than drawn over the newer day.
    /// </summary>
    public async Task RefreshAsync()
    {
        int load = ++_load;
        _provider = _choose();
        var day = _day;
        _dayLabel.Text = Horizon.DayHeader(day, Today());
        SetStatus(_provider.Mode == HorizonMode.Ark
            ? "Calendar from Ark, through Hivemind. Checklist and log stay on this PC until Ark has them."
            : "Your calendar comes from your own sign-in. The checklist and log stay on this PC.");
        Invalidate();

        var calendarTask = Guard(_provider.ReadCalendarAsync(day));
        var checklistTask = Guard(_provider.ReadChecklistAsync(day));
        var logTask = Guard(_provider.ReadLogAsync(day));
        var calendar = await calendarTask;
        var checklist = await checklistTask;
        var log = await logTask;
        if (load != _load || IsDisposed) return;
        Render(day, calendar, checklist, log);
    }

    /// <summary>A provider that throws is still one part that did not answer, not a dead window.</summary>
    private static async Task<HorizonPart<T>> Guard<T>(Task<HorizonPart<T>> read)
    {
        try { return await read; }
        catch (Exception ex) { return HorizonPart<T>.Not(HorizonPartState.Unavailable, PlainFailure.For(ex.Message, "Horizon")); }
    }

    /// <summary>Draws one day. Internal so a render can show any state without a provider behind it.</summary>
    internal void Render(DateOnly day, HorizonPart<HorizonCalendarDay> calendar, HorizonPart<IReadOnlyList<HorizonCheckItem>> checklist, HorizonPart<IReadOnlyList<HorizonLogEntry>> log)
    {
        _dayLabel.Text = Horizon.DayHeader(day, Today());
        bool canWrite = checklist.Ready || log.Ready;
        _entry.Enabled = canWrite;
        _addCheck.Enabled = checklist.Ready;
        _addLog.Enabled = log.Ready;

        using (_list.Batch())
        {
            _list.Clear();

            _list.AddSection("CALENDAR");
            if (calendar is { Ready: true, Value: { } cal })
            {
                if (cal.Events.Count == 0) _list.AddCard(ThreadTone.Intro, "Nothing on the calendar", "No events on this day.");
                foreach (var ev in cal.Events)
                {
                    var card = _list.AddCard(ThreadTone.Step, $"{ev.When()} // {ev.Source}".ToUpperInvariant(), ev.Title);
                    if (ev.Url.Length > 0)
                        card.SetFooter("", new CardAction("Horizon.OpenEvent", PhosphorGlyph.ExternalLink, "Open in your calendar", (_, _) => OpenLink(ev.Url)));
                }
                if (!cal.Complete || cal.Issues.Count > 0)
                    _list.AddCard(ThreadTone.Approval, "CALENDAR // NOT COMPLETE", cal.Issues.Count > 0 ? string.Join("\n", cal.Issues) : "Not every connected calendar answered, so this day may be missing events.");
            }
            else AddNotReady("CALENDAR", calendar.State, calendar.Note, calendarPart: true);

            _list.AddSection(checklist.Ready && checklist.Note.Length > 0 ? "CHECKLIST // ON THIS PC" : "CHECKLIST");
            if (checklist is { Ready: true, Value: { } items })
            {
                if (items.Count == 0) _list.AddCard(ThreadTone.Intro, "No missions yet", "Add the things this day is for. Tick them off as they land.");
                foreach (var item in items)
                {
                    var row = _list.AddCheckRow(item);
                    row.Toggled += async (_, done) => ShowChecklist(await Guard(_provider.SetCheckItemAsync(_day, item.Id, done)));
                    row.Removed += async (_, _) => ShowChecklist(await Guard(_provider.RemoveCheckItemAsync(_day, item.Id)));
                }
            }
            else AddNotReady("CHECKLIST", checklist.State, checklist.Note, calendarPart: false);

            _list.AddSection(log.Ready && log.Note.Length > 0 ? "MISSIONS LOG // ON THIS PC" : "MISSIONS LOG");
            if (log is { Ready: true, Value: { } entries })
            {
                if (entries.Count == 0) _list.AddCard(ThreadTone.Intro, "Nothing logged", "Write what happened as the day goes. Each line keeps its time.");
                foreach (var entry in entries)
                    _list.AddCard(ThreadTone.You, entry.At.ToLocalTime().ToString("HH:mm"), entry.Text);
            }
            else AddNotReady("MISSIONS LOG", log.State, log.Note, calendarPart: false);
        }
    }

    /// <summary>A part with nothing to show says which state it is in, in that part's place, with the way forward when there is one.</summary>
    private void AddNotReady(string part, HorizonPartState state, string note, bool calendarPart)
    {
        switch (state)
        {
            case HorizonPartState.NeedsArkChange:
                _list.AddCard(ThreadTone.Approval, $"{part} // NEEDS AN ARK CHANGE", note);
                break;
            case HorizonPartState.NeedsSignIn:
            {
                var card = _list.AddCard(ThreadTone.Step, $"{part} // NOT CONNECTED", note);
                if (!calendarPart) break;
                if (GoogleAdapter.HasClient)
                    card.SetFooter("", new CardAction("Horizon.ConnectCalendar", PhosphorGlyph.Link, "Connect Google Calendar", (_, _) => _ = ConnectCalendarAsync()));
                else if (OpenVault != null)
                    card.SetFooter("", new CardAction("Horizon.OpenVault", PhosphorGlyph.Vault, "Open the Vault", (_, _) => OpenVault()));
                break;
            }
            default:
            {
                var card = _list.AddCard(ThreadTone.Fail, $"{part} // NOT AVAILABLE", note);
                card.SetFooter("", new CardAction("Thread.Retry", PhosphorGlyph.Retry, "Try again", (_, _) => _ = RefreshAsync()));
                break;
            }
        }
    }

    private async Task ConnectCalendarAsync()
    {
        SetStatus("Finish the Google sign-in in your browser.");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await ConnectCalendar(limit.Token);
        }
        catch (Exception ex)
        {
            SetStatus(PlainFailure.For(ex.Message, "Google Calendar"));
            return;
        }
        if (!IsDisposed) await RefreshAsync();
    }

    private async Task AddAsync(bool toLog)
    {
        string text = Horizon.CleanText(_entry.Box.Text);
        if (text.Length == 0 || !(toLog ? _addLog.Enabled : _addCheck.Enabled)) return;
        var day = _day;
        string? failed;
        if (toLog)
        {
            var result = await Guard(_provider.AppendLogAsync(day, text));
            failed = result.Ready ? null : result.Note;
        }
        else
        {
            var result = await Guard(_provider.AddCheckItemAsync(day, text));
            failed = result.Ready ? null : result.Note;
        }
        if (IsDisposed) return;
        if (failed != null) { SetStatus(failed); return; }
        // The line is kept only once it is saved, so a failed write leaves it in the box to try again.
        _entry.Box.Clear();
        await RefreshAsync();
    }

    /// <summary>After a tick or a remove: the day is read again so the list is what was saved, or the reason is shown.</summary>
    private void ShowChecklist(HorizonPart<IReadOnlyList<HorizonCheckItem>> result)
    {
        if (IsDisposed) return;
        if (!result.Ready) SetStatus(result.Note);
        _ = RefreshAsync();
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>Opens an event in the browser. Only https links reach here (<see cref="HorizonJson.Event"/>), checked again before the shell sees it.</summary>
    private void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"The link did not open: {ex.Message}"); }
    }

    /// <summary>Draws the window offscreen to a PNG for a day already in hand (renders and tests).</summary>
    public string CaptureTo(string path, DateOnly day, HorizonPart<HorizonCalendarDay> calendar, HorizonPart<IReadOnlyList<HorizonCheckItem>> checklist, HorizonPart<IReadOnlyList<HorizonLogEntry>> log)
    {
        _day = day;
        Render(day, calendar, checklist, log);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        SurfaceCapture.SaveForm(this, path);
        return path;
    }
}

/// <summary>
/// Horizon's day list: section eyebrows, the thread's cards, and checklist rows, top to bottom, scrolling with the
/// scroll beam. The thread view lays out cards and eyebrows only, so the list that also holds tick rows is this one.
/// </summary>
internal sealed class HorizonList : BeamFlowPanel
{
    private int _batch;

    public HorizonList(string name)
    {
        Name = name;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = PhosphorTheme.Surface;
        Padding = new Padding(0);
        DoubleBuffered = true;
        Resize += (_, _) => Relayout();
    }

    public IEnumerable<ThreadCard> Cards => Controls.OfType<ThreadCard>();
    public IEnumerable<HorizonCheckRow> CheckRows => Controls.OfType<HorizonCheckRow>();

    /// <summary>Rebuilds in one pass: rows added while the scope is open are laid out once when it closes.</summary>
    public IDisposable Batch()
    {
        _batch++;
        SuspendLayout();
        return new Scope(this);
    }

    private sealed class Scope(HorizonList list) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            list._batch--;
            list.ResumeLayout(false);
            list.Relayout();
        }
    }

    public void Clear()
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        Controls.Clear();
    }

    public void AddSection(string eyebrow)
    {
        Controls.Add(new EyebrowLabel(eyebrow) { Dock = DockStyle.None, Margin = new Padding(0, PhosphorTheme.S2, 0, PhosphorTheme.U), Name = "Horizon.Section" });
        Relayout();
    }

    public ThreadCard AddCard(ThreadTone tone, string kicker, string? body = null)
    {
        var card = new ThreadCard(tone, kicker, body);
        Controls.Add(card);
        Relayout();
        return card;
    }

    public HorizonCheckRow AddCheckRow(HorizonCheckItem item)
    {
        var row = new HorizonCheckRow(item);
        Controls.Add(row);
        Relayout();
        return row;
    }

    private int FitWidth => Math.Max(80, ClientSize.Width - Padding.Horizontal - ScrollBeam.Lane(this));

    public void Relayout()
    {
        if (_batch > 0) return;
        int w = FitWidth;
        SuspendLayout();
        foreach (Control c in Controls)
        {
            switch (c)
            {
                case ThreadCard card: card.Size = new Size(w, card.HeightFor(w)); break;
                default: c.Width = w; break;
            }
        }
        ResumeLayout(true);
        Invalidate();
    }
}

/// <summary>
/// One checklist item: the kit's checkbox across a touch-height row, and Remove at its right edge. Ticking and
/// removing raise events; the row does not change the day itself, so what shows is always what was saved.
/// </summary>
internal sealed class HorizonCheckRow : Panel
{
    private readonly PhosphorCheckBox _box;
    private bool _quiet;

    public HorizonCheckItem Item { get; }
    public event EventHandler<bool>? Toggled;
    public event EventHandler? Removed;

    public HorizonCheckRow(HorizonCheckItem item)
    {
        Item = item;
        Name = "Horizon.CheckRow";
        Height = PhosphorTheme.TouchMin;
        Margin = new Padding(0, 0, 0, PhosphorTheme.U);
        BackColor = PhosphorTheme.Surface;

        _quiet = true;
        _box = new PhosphorCheckBox(item.Text) { Name = "Horizon.CheckBox", Dock = DockStyle.Fill, Checked = item.Done };
        _quiet = false;
        // A done item reads quieter than one still open.
        _box.ForeColor = item.Done ? PhosphorTheme.Ghost : PhosphorTheme.Bone;
        _box.CheckedChanged += (_, _) => { if (!_quiet) Toggled?.Invoke(this, _box.Checked); };

        var remove = new PhosphorButton("Remove") { Name = "Horizon.RemoveCheckButton", Dock = DockStyle.Right, Width = 96 };
        remove.Click += (_, _) => Removed?.Invoke(this, EventArgs.Empty);

        Controls.Add(_box);
        Controls.Add(new Panel { Dock = DockStyle.Right, Width = PhosphorTheme.S2, BackColor = PhosphorTheme.Surface });
        Controls.Add(remove);
    }

    public bool Checked => _box.Checked;
}
