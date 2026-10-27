using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Holodex as a native Hivemind client: the owner's saved Hivemind Sessions, the same rooms the Hivemind web app
/// shows, are picked from the Session tab's target chip. An open Session shows its transcript as cards, follows new
/// messages while it is open, and the composer replies into it as Holodex (Hivemind attributes the post to this
/// client, and a post starts no agent). The two stay separate apps: Hivemind keeps the rooms, Holodex reads and
/// writes them through Hivemind's own tools, and "Open in Hivemind" hands over to the web app.
/// </summary>
public sealed partial class HolodexLiteDrawerForm
{
    /// <summary>How many Sessions the chip offers, newest first; the rest stay one click away in Hivemind.</summary>
    private const int ChipSessionCount = 6;

    /// <summary>An open Session shows its newest messages; older ones stay in Hivemind.</summary>
    private const int TranscriptCards = 40;

    private ThreadView _hivemindThread = null!;
    private IconToolButton _openInHivemindBtn = null!;
    private readonly System.Windows.Forms.Timer _sessionFollow = new() { Interval = 10_000 };
    private IReadOnlyList<HivemindSessionSummary> _recentSessions = Array.Empty<HivemindSessionSummary>();
    private HivemindSessionSummary? _openSession;
    private string _shownTranscriptKey = "";
    private bool _followingSession;

    /// <summary>The Session thread and its Open in Hivemind button, added by BuildSessionTab.</summary>
    private void BuildHivemindSessions(Panel sessionPanel, Panel composerBar)
    {
        _hivemindThread = new ThreadView("Holodex.HivemindSessionThread") { Dock = DockStyle.Fill, ReserveScrollbar = false, Visible = false };
        sessionPanel.Controls.Add(_hivemindThread);

        _openInHivemindBtn = new IconToolButton("Holodex.OpenInHivemindBtn", PhosphorGlyph.ExternalLink, "Open in Hivemind") { Dock = DockStyle.Right, Visible = false };
        _openInHivemindBtn.Click += (_, _) => HivemindClient.OpenWebSessions();
        composerBar.Controls.Add(_openInHivemindBtn);

        _sessionFollow.Tick += async (_, _) =>
        {
            if (_openSession == null || !Visible || !_sessionPanel.Visible) return;
            await RefreshOpenSessionAsync();
        };
    }

    /// <summary>The chip's menu: every agent, then the newest Hivemind Sessions under their own heading.</summary>
    private IEnumerable<ChipChoice> RosterChoices()
    {
        foreach (var ag in _roster)
            yield return new ChipChoice($"{ag.Name}  ·  {ag.Role}", ag.Pod, _openSession == null && ag.Id == _selectedSyndicateTarget, () => SetSyndicateTarget(ag.Id));
        if (_ark.Hivemind.State != HivemindState.Connected) yield break;
        // Refreshed in the background, so the next open lists any Session started in the web app meanwhile.
        _ = RefreshRecentSessionsAsync();
        if (_recentSessions.Count == 0) yield break;
        yield return ChipChoice.Header("Hivemind sessions");
        foreach (var s in _recentSessions.Take(ChipSessionCount))
        {
            var session = s;
            yield return new ChipChoice(Clip(s.Title, 44), s.Status.ToUpperInvariant(), _openSession?.Id == s.Id, () => _ = OpenHivemindSessionAsync(session),
                $"{string.Join(", ", s.Participants)} // updated {s.UpdatedAt.ToLocalTime():MMM d, HH:mm}");
        }
    }

    /// <summary>Reloads the newest Sessions for the chip (list_sessions).</summary>
    private async Task RefreshRecentSessionsAsync()
    {
        var sessions = await _ark.Hivemind.ListSessionsAsync();
        if (sessions != null) _recentSessions = sessions;
    }

    /// <summary>Makes <paramref name="session"/> the composer's target and shows its transcript.</summary>
    private async Task OpenHivemindSessionAsync(HivemindSessionSummary session)
    {
        _openSession = session;
        _shownTranscriptKey = "";
        _rosterChip.Label = "SESSION";
        // Short enough that the chip and Reply in session share the footer; the full title is in the transcript and the menu.
        _rosterChip.Detail = Clip(session.Title, 16);
        _rosterChip.Accent = PhosphorTheme.Seal;
        _rosterChip.Confident = true;
        _rosterChip.Fit();
        _sessionSendBtn.Text = "Reply in session";
        _sessionInputBox.Prompt = "Reply in this Hivemind session. It shows in Hivemind too.";
        ShowSessionLine(session);
        _sessionThread.Visible = false;
        _hivemindThread.Visible = true;
        _hivemindThread.BringToFront();
        _openInHivemindBtn.Visible = true;
        _hivemindThread.Clear();
        _hivemindThread.Add(ThreadTone.Intro, "HIVEMIND // OPENING", $"Reading \"{session.Title}\" from Hivemind...");
        _sessionFollow.Start();
        await RefreshOpenSessionAsync();
    }

    /// <summary>Back to the Syndicate thread (choosing an agent on the chip does this).</summary>
    private void CloseHivemindSession()
    {
        if (_openSession == null) return;
        _openSession = null;
        _sessionFollow.Stop();
        _hivemindThread.Visible = false;
        _sessionThread.Visible = true;
        _openInHivemindBtn.Visible = false;
        _sessionInputBox.Prompt = "Message the Syndicate. /order queues a work order.";
    }

    /// <summary>Reads the open Session and redraws its cards when anything changed (new message, new status).</summary>
    private async Task RefreshOpenSessionAsync()
    {
        var open = _openSession;
        if (open == null || _followingSession) return;
        _followingSession = true;
        try
        {
            var session = await _ark.Hivemind.ReadSessionAsync(open.Id);
            if (_openSession?.Id != open.Id) return; // another Session or the Syndicate was picked meanwhile
            if (session == null)
            {
                if (_shownTranscriptKey.Length == 0)
                {
                    _hivemindThread.Clear();
                    _hivemindThread.Add(ThreadTone.Fail, "HIVEMIND // NOT READ", $"The session could not be read: {_ark.Hivemind.LastError}");
                }
                return;
            }
            _openSession = session.Summary;
            ShowSessionLine(session.Summary);
            string key = $"{session.Summary.Status}|{session.Messages.Count}|{session.Messages.LastOrDefault()?.Id}";
            if (key == _shownTranscriptKey) return;
            _shownTranscriptKey = key;
            ShowTranscript(session);
        }
        finally
        {
            _followingSession = false;
        }
    }

    private void ShowTranscript(HivemindSession session)
    {
        _hivemindThread.SuspendLayout();
        _hivemindThread.Clear();
        var shown = session.Messages.TakeLast(TranscriptCards).ToList();
        if (session.Messages.Count > shown.Count)
            _hivemindThread.Add(ThreadTone.Intro, "HIVEMIND // EARLIER MESSAGES", $"{session.Messages.Count - shown.Count} earlier messages are in Hivemind.");
        if (shown.Count == 0)
            _hivemindThread.Add(ThreadTone.Intro, "HIVEMIND // NO MESSAGES YET", "Nothing has been said in this session yet. A reply from here is the first message.");
        foreach (var m in shown)
        {
            string when = m.CreatedAt == DateTimeOffset.MinValue ? "" : $" // {m.CreatedAt.ToLocalTime():MMM d, HH:mm}";
            switch (m.Role)
            {
                case "user":
                    _hivemindThread.Add(ThreadTone.You, $"YOU{when}", m.Content);
                    break;
                case "agent":
                    string model = m.Model.Length > 0 ? $" // {ModelChip.ShortName(m.Model).ToUpperInvariant()}" : "";
                    _hivemindThread.Add(ThreadTone.Agent, $"{(m.AgentId.Length > 0 ? m.AgentId : "AGENT").ToUpperInvariant()}{model}{when}", m.Content);
                    break;
                default:
                    string client = m.ClientName.Length > 0 ? m.ClientName : "A CONNECTED CLIENT";
                    _hivemindThread.Add(ThreadTone.Step, $"{client.ToUpperInvariant()} // CLIENT{when}", m.Content);
                    break;
            }
        }
        if (session.Summary.Error.Length > 0)
            _hivemindThread.Add(ThreadTone.Fail, $"HIVEMIND // {session.Summary.Status.ToUpperInvariant()}", session.Summary.Error);
        _hivemindThread.ResumeLayout(true);
        _hivemindThread.Relayout();
    }

    /// <summary>The roster line for an open Session: its status, who is in it, and that a reply posts as Holodex.</summary>
    private void ShowSessionLine(HivemindSessionSummary s)
    {
        Color status = s.Status switch
        {
            "completed" => PhosphorTheme.Moss,
            "failed" => PhosphorTheme.Rust,
            "paused" => PhosphorTheme.Ember,
            _ => PhosphorTheme.Patina
        };
        var leds = new List<(Color, string)> { (status, s.Status.Length > 0 ? s.Status : "open") };
        if (s.Participants.Count > 0) leds.Add((PhosphorTheme.Patina, string.Join(", ", s.Participants)));
        leds.Add((PhosphorTheme.Ghost, "Replies post as Holodex"));
        _activeRosterLabel.Set(leds.ToArray());
    }

    /// <summary>Posts the composer's text into the open Session, then shows the transcript with it.</summary>
    private async Task ReplyInHivemindSessionAsync(string msg)
    {
        var open = _openSession;
        if (open == null) return;
        if (_ark.Hivemind.State != HivemindState.Connected && !await _ark.Hivemind.EnsureConnectedAsync())
        {
            _hivemindThread.Add(ThreadTone.Fail, "NOT SENT // HIVEMIND", $"Holodex is not connected to Hivemind: {_ark.Hivemind.LastError}");
            _sessionInputBox.Text = msg;
            return;
        }
        var pending = _hivemindThread.Add(ThreadTone.You, "YOU // SENDING TO HIVEMIND", msg);
        _sessionSendBtn.Enabled = false;
        try
        {
            bool ok = await _ark.Hivemind.PostSessionMessageAsync(open.Id, msg, Guid.NewGuid());
            if (!ok)
            {
                pending.Dispose();
                _hivemindThread.Add(ThreadTone.Fail, "NOT SENT // HIVEMIND", _ark.Hivemind.LastError.Length > 0 ? _ark.Hivemind.LastError : "Hivemind did not accept the message.");
                _sessionInputBox.Text = msg;
                _hivemindThread.Relayout();
                return;
            }
            _shownTranscriptKey = "";
            await RefreshOpenSessionAsync();
        }
        finally
        {
            _sessionSendBtn.Enabled = true;
        }
    }

    /// <summary>Hands over to the Hivemind web app's Sessions view in the default browser.</summary>
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // ---- Pipe (holodex:sessions, holodex:session:open:, holodex:session:reply:, holodex:session:state) ----------

    /// <summary>The newest Sessions as one line each, refreshed from Hivemind first.</summary>
    public async Task<string> DescribeSessionsAsync()
    {
        await RefreshRecentSessionsAsync();
        if (_recentSessions.Count == 0) return $"none:{_ark.Hivemind.State}:{_ark.Hivemind.LastError}";
        return string.Join(" | ", _recentSessions.Take(ChipSessionCount).Select(s => $"{s.Id}={s.Status}:{s.Title}"));
    }

    /// <summary>Opens a Session by id (as picking it on the chip does) and waits for its transcript.</summary>
    public async Task<string> OpenSessionForPipeAsync(string id)
    {
        if (_recentSessions.Count == 0) await RefreshRecentSessionsAsync();
        var s = _recentSessions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (s == null) return $"error:unknown session {id}";
        SwitchTab("session");
        await OpenHivemindSessionAsync(s);
        return DescribeOpenSession();
    }

    /// <summary>Replies in the open Session as the composer's Send does.</summary>
    public async Task<string> ReplyForPipeAsync(string text)
    {
        if (_openSession == null) return "error:no session open";
        await ReplyInHivemindSessionAsync(text);
        return DescribeOpenSession();
    }

    public string DescribeOpenSession() => _openSession == null
        ? "syndicate"
        : $"open={_openSession.Id}:status={_openSession.Status}:cards={_hivemindThread.Cards.Count()}:send={_sessionSendBtn.Text}:chip={_rosterChip.Label} {_rosterChip.Detail}:last={_hivemindThread.Cards.LastOrDefault()?.Kicker}";
}
