using System.Text.Json;
using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// Horizon, one mod with two modes: a signed-in Hivemind account reads Ark's Horizon and keeps nothing on this PC;
/// everyone else reads their own calendar and keeps the missions log and checklist here. These run with no network,
/// no sign-in and no window.
/// </summary>
public class HorizonTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 11);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "holodex-horizon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private HorizonLocalStore Store(Func<DateTimeOffset>? now = null) => new(_root, now);

    // ---- Mode ----

    [Fact]
    public void A_saved_hivemind_sign_in_is_ark_mode_and_none_is_the_public_mode()
    {
        Assert.Equal(HorizonMode.Ark, Horizon.ModeFor(hivemindSignedIn: true));
        Assert.Equal(HorizonMode.ConnectedServices, Horizon.ModeFor(hivemindSignedIn: false));
    }

    [Fact]
    public void Text_is_one_trimmed_line_cut_to_the_limit()
    {
        Assert.Equal("ship the mod today", Horizon.CleanText("  ship the mod\r\n today \t"));
        Assert.Equal("", Horizon.CleanText(" \r\n "));
        Assert.Equal("", Horizon.CleanText(null));
        Assert.Equal(Horizon.MaxTextLength, Horizon.CleanText(new string('x', Horizon.MaxTextLength + 40)).Length);
    }

    [Fact]
    public void The_day_header_names_today_and_its_neighbours()
    {
        Assert.Equal("WED 11 NOV 2026 // TODAY", Horizon.DayHeader(Day, Day));
        Assert.Equal("THU 12 NOV 2026 // TOMORROW", Horizon.DayHeader(Day.AddDays(1), Day));
        Assert.Equal("TUE 10 NOV 2026 // YESTERDAY", Horizon.DayHeader(Day.AddDays(-1), Day));
        Assert.Equal("SAT 14 NOV 2026", Horizon.DayHeader(Day.AddDays(3), Day));
        Assert.Equal("2026-11-11", Horizon.DayKey(Day));
    }

    // ---- The local missions log and checklist ----

    [Fact]
    public void The_log_and_checklist_survive_a_new_store_on_the_same_folder()
    {
        var at = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(-5));
        var store = Store(() => at);
        store.AppendLog(Day, "Stood up the Horizon mod");
        var items = store.AddCheckItem(Day, "Open the draft PR");
        store.SetCheckItem(Day, items[0].Id, done: true);

        var again = Store();
        var log = Assert.Single(again.ReadLog(Day));
        Assert.Equal("Stood up the Horizon mod", log.Text);
        Assert.Equal(at, log.At);
        var item = Assert.Single(again.ReadChecklist(Day));
        Assert.Equal("Open the draft PR", item.Text);
        Assert.True(item.Done);
        Assert.True(File.Exists(Path.Combine(_root, "2026-11-11.json")));
    }

    [Fact]
    public void Each_day_is_its_own_file()
    {
        var store = Store();
        store.AddCheckItem(Day, "Today's mission");
        store.AppendLog(Day.AddDays(1), "Tomorrow's note");

        Assert.Single(store.ReadChecklist(Day));
        Assert.Empty(store.ReadLog(Day));
        Assert.Empty(store.ReadChecklist(Day.AddDays(1)));
        Assert.Single(store.ReadLog(Day.AddDays(1)));
    }

    [Fact]
    public void Items_tick_untick_and_remove_by_id()
    {
        var store = Store();
        store.AddCheckItem(Day, "First");
        var two = store.AddCheckItem(Day, "Second");
        string first = two[0].Id, second = two[1].Id;
        Assert.NotEqual(first, second);

        Assert.True(store.SetCheckItem(Day, second, true).Single(i => i.Id == second).Done);
        Assert.False(store.SetCheckItem(Day, second, false).Single(i => i.Id == second).Done);
        var left = store.RemoveCheckItem(Day, first);
        Assert.Equal("Second", Assert.Single(left).Text);
        // An id that is not there changes nothing.
        Assert.Single(store.SetCheckItem(Day, "missing", true));
        Assert.Single(store.RemoveCheckItem(Day, "missing"));
    }

    [Fact]
    public void Empty_text_adds_nothing_and_writes_no_file()
    {
        var store = Store();
        Assert.Empty(store.AppendLog(Day, "   "));
        Assert.Empty(store.AddCheckItem(Day, "\r\n"));
        Assert.False(File.Exists(store.PathFor(Day)));
    }

    [Fact]
    public void An_unreadable_day_is_set_aside_not_overwritten()
    {
        var store = Store();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.PathFor(Day), "{ this is not a day");

        Assert.Empty(store.ReadChecklist(Day));
        store.AddCheckItem(Day, "Carry on");

        Assert.Equal("{ this is not a day", File.ReadAllText(store.PathFor(Day) + ".unreadable"));
        Assert.Equal("Carry on", Assert.Single(Store().ReadChecklist(Day)).Text);
    }

    // ---- The public mode's provider ----

    private sealed class FakeCalendar(bool connected, Func<DateOnly, HorizonCalendarDay>? read = null) : IHorizonCalendarSource
    {
        public int Reads;
        public string Name => "Test Calendar";
        public bool Connected => connected;
        public string ConnectHint => "Connect the test calendar.";
        public Task<HorizonCalendarDay> ReadDayAsync(DateOnly day, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult((read ?? (_ => new HorizonCalendarDay(Array.Empty<HorizonEvent>(), true, Array.Empty<string>())))(day));
        }
    }

    [Fact]
    public async Task Without_a_calendar_sign_in_the_log_and_checklist_still_work()
    {
        var calendar = new FakeCalendar(connected: false);
        var provider = new HorizonConnectedProvider(Store(), calendar);

        var cal = await provider.ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.NeedsSignIn, cal.State);
        Assert.Equal("Connect the test calendar.", cal.Note);
        Assert.Equal(0, calendar.Reads);

        Assert.True((await provider.AddCheckItemAsync(Day, "Works signed out")).Ready);
        Assert.True((await provider.AppendLogAsync(Day, "So does the log")).Ready);
        Assert.Single((await provider.ReadChecklistAsync(Day)).Value!);
        Assert.Single((await provider.ReadLogAsync(Day)).Value!);
        Assert.Equal(HorizonMode.ConnectedServices, provider.Mode);
        Assert.Equal("THIS PC", provider.Label);
    }

    [Fact]
    public async Task A_connected_calendar_is_read_and_a_failing_one_is_a_state_not_a_crash()
    {
        var ev = new HorizonEvent("1", "Standup", false, DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(15), "", "Test Calendar");
        var good = new HorizonConnectedProvider(Store(), new FakeCalendar(true, _ => new HorizonCalendarDay(new[] { ev }, true, Array.Empty<string>())));
        var read = await good.ReadCalendarAsync(Day);
        Assert.True(read.Ready);
        Assert.Equal("Standup", Assert.Single(read.Value!.Events).Title);

        var bad = new HorizonConnectedProvider(Store(), new FakeCalendar(true, _ => throw new InvalidOperationException("Google Calendar answered HTTP 403.")));
        var failed = await bad.ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.Unavailable, failed.State);
        Assert.Contains("HTTP 403", failed.Note);

        Assert.Equal(HorizonPartState.NeedsSignIn, (await new HorizonConnectedProvider(Store(), null).ReadCalendarAsync(Day)).State);
    }

    // ---- Google Calendar, the user's own sign-in ----

    [Fact]
    public void The_calendar_grant_is_read_only_and_separate_from_drive()
    {
        Assert.Equal("https://www.googleapis.com/auth/calendar.events.readonly", GoogleCalendarSource.Scope);
        Assert.NotEqual(GoogleAdapter.TokenTarget, GoogleCalendarSource.TokenTarget);
        Assert.NotEqual(GoogleAdapter.Scope, GoogleCalendarSource.Scope);
        // The sign-in comes back to this PC only.
        Assert.True(GoogleCalendarSource.Redirect.IsLoopback);
        Assert.Equal("http", GoogleCalendarSource.Redirect.Scheme);
    }

    [Fact]
    public void The_day_request_covers_local_midnight_to_midnight_and_asks_only_for_what_is_shown()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test-5", "test-5");
        var url = new Uri(GoogleCalendarSource.DayUrl(Day, zone, "next page"));
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("www.googleapis.com", url.Host);
        Assert.Equal("/calendar/v3/calendars/primary/events", url.AbsolutePath);
        Assert.Equal("2026-11-11T00:00:00-05:00", q["timeMin"]);
        Assert.Equal("2026-11-12T00:00:00-05:00", q["timeMax"]);
        Assert.Equal("true", q["singleEvents"]);
        Assert.Equal("next page", q["pageToken"]);
        Assert.DoesNotContain("attendees", q["fields"]);
        Assert.DoesNotContain("description", q["fields"]);
    }

    [Fact]
    public void Google_events_are_read_timed_and_all_day_and_cancelled_ones_are_left_out()
    {
        const string body = """
        {
          "nextPageToken": "more",
          "items": [
            { "id": "a", "summary": "Standup", "status": "confirmed", "htmlLink": "https://www.google.com/calendar/event?eid=a",
              "start": { "dateTime": "2026-11-11T09:30:00-05:00" }, "end": { "dateTime": "2026-11-11T09:45:00-05:00" } },
            { "id": "b", "summary": "Shop closed", "start": { "date": "2026-11-11" }, "end": { "date": "2026-11-12" } },
            { "id": "c", "summary": "Called off", "status": "cancelled", "start": { "dateTime": "2026-11-11T12:00:00-05:00" } },
            { "id": "d", "htmlLink": "http://not-https.example/x", "start": { "dateTime": "2026-11-11T14:00:00-05:00" }, "end": { "dateTime": "2026-11-11T15:00:00-05:00" } },
            { "id": "e", "summary": "No start" }
          ]
        }
        """;
        var (events, next) = GoogleCalendarSource.ParseEvents(body);

        Assert.Equal("more", next);
        Assert.Equal(new[] { "a", "b", "d" }, events.Select(e => e.Id));
        var standup = events[0];
        Assert.False(standup.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(-5)), standup.Start);
        Assert.Equal("https://www.google.com/calendar/event?eid=a", standup.Url);
        Assert.True(events[1].AllDay);
        Assert.Equal("All day", events[1].When());
        // No title reads as busy time, and a link that is not https is dropped.
        Assert.Equal("Busy", events[2].Title);
        Assert.Equal("", events[2].Url);

        Assert.Throws<InvalidOperationException>(() => GoogleCalendarSource.ParseEvents("<html>sign in</html>"));
    }

    [Fact]
    public async Task A_day_is_read_across_pages_with_all_day_events_first()
    {
        var urls = new List<string>();
        var source = new GoogleCalendarSource(
            get: (url, _) =>
            {
                urls.Add(url);
                return Task.FromResult(urls.Count == 1
                    ? """{ "nextPageToken": "p2", "items": [ { "id": "late", "summary": "Late", "start": { "dateTime": "2026-11-11T18:00:00Z" }, "end": { "dateTime": "2026-11-11T19:00:00Z" } } ] }"""
                    : """{ "items": [ { "id": "day", "summary": "All day", "start": { "date": "2026-11-11" }, "end": { "date": "2026-11-12" } } ] }""");
            },
            connected: () => true, hasClient: () => true, zone: TimeZoneInfo.Utc);

        var day = await source.ReadDayAsync(Day);

        Assert.Equal(2, urls.Count);
        Assert.Contains("pageToken=p2", urls[1]);
        Assert.True(day.Complete);
        Assert.Equal(new[] { "day", "late" }, day.Events.Select(e => e.Id));
    }

    [Fact]
    public void The_connect_hint_never_offers_a_shared_key()
    {
        var noClient = new GoogleCalendarSource((_, _) => Task.FromResult("{}"), () => false, () => false);
        var hasClient = new GoogleCalendarSource((_, _) => Task.FromResult("{}"), () => false, () => true);
        Assert.Contains("your own Google OAuth client", noClient.ConnectHint);
        Assert.Contains("your own sign-in", hasClient.ConnectHint);
        Assert.False(noClient.Connected);
    }

    // ---- The Ark provider ----

    private const string ArkDay = """
    {
      "day": "2026-11-11", "complete": false, "profileCount": 2,
      "windowStart": "2026-11-11T05:00:00.000Z", "windowEnd": "2026-11-12T05:00:00.000Z",
      "events": [
        { "id": "t1", "title": "Tattoo session", "start": "2026-11-11T13:00:00-05:00", "end": "2026-11-11T16:00:00-05:00", "allDay": false,
          "timeZone": "America/New_York", "status": "confirmed", "url": "https://calendar.google.com/calendar/event?eid=t1",
          "sources": [ { "profile": "p", "accountEmail": "owner@example.com", "calendarId": "primary", "calendarName": "Owner", "timeZone": "America/New_York", "status": "connected" } ] },
        { "id": "d1", "title": "Convention", "start": "2026-11-11", "end": "2026-11-12", "allDay": true, "timeZone": "America/New_York", "status": "confirmed",
          "sources": [ { "profile": "p", "accountEmail": "owner@example.com", "calendarId": "primary", "calendarName": "Owner", "timeZone": "America/New_York", "status": "connected" } ] }
      ],
      "sources": [
        { "profile": "p", "accountEmail": "owner@example.com", "calendarId": "primary", "calendarName": "Owner", "timeZone": "America/New_York", "status": "connected" },
        { "profile": "q", "accountEmail": "shop@example.com", "calendarId": "primary", "calendarName": "Shop", "timeZone": "America/New_York", "status": "failed", "issue": "Reconnect this Google account in Ark." }
      ]
    }
    """;

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static HorizonArkProvider Ark(bool offers, Func<DateOnly, (JsonElement?, string)>? read = null, Action? onRead = null) => new(
        (tool, _) => Task.FromResult(offers && tool == HorizonArkProvider.CalendarTool),
        (day, _) =>
        {
            onRead?.Invoke();
            return Task.FromResult(read?.Invoke(day) ?? (null, "no reader"));
        });

    [Fact]
    public async Task When_hivemind_does_not_offer_horizon_the_calendar_needs_an_ark_change_and_nothing_is_called()
    {
        int calls = 0;
        var provider = Ark(offers: false, onRead: () => calls++);

        var cal = await provider.ReadCalendarAsync(Day);

        Assert.Equal(HorizonPartState.NeedsArkChange, cal.State);
        Assert.Equal(HorizonArkProvider.CalendarNeedsArk, cal.Note);
        Assert.Equal(0, calls);
        Assert.Equal(HorizonMode.Ark, provider.Mode);
        Assert.Equal("ARK", provider.Label);
    }

    [Fact]
    public async Task Arks_day_is_read_with_its_sources_and_its_gaps()
    {
        var provider = Ark(offers: true, _ => (Json(ArkDay), ""));

        var cal = await provider.ReadCalendarAsync(Day);

        Assert.True(cal.Ready);
        var day = cal.Value!;
        Assert.Equal(new[] { "d1", "t1" }, day.Events.Select(e => e.Id));
        Assert.True(day.Events[0].AllDay);
        Assert.Equal("owner@example.com", day.Events[1].Source);
        Assert.Equal("https://calendar.google.com/calendar/event?eid=t1", day.Events[1].Url);
        Assert.False(day.Complete);
        Assert.Equal("shop@example.com: Reconnect this Google account in Ark.", Assert.Single(day.Issues));
    }

    [Fact]
    public void Arks_day_is_read_bare_or_inside_the_api_envelope_and_a_different_day_is_refused()
    {
        Assert.NotNull(HorizonArkProvider.ParseDay(Json(ArkDay), Day));
        Assert.NotNull(HorizonArkProvider.ParseDay(Json($$"""{ "ok": true, "data": {{ArkDay}} }"""), Day));
        Assert.Null(HorizonArkProvider.ParseDay(Json(ArkDay), Day.AddDays(1)));
        Assert.Null(HorizonArkProvider.ParseDay(Json("""{ "month": "2026-11", "eventDays": [] }"""), Day));
        Assert.Null(HorizonArkProvider.ParseDay(Json("""[1, 2]"""), Day));
    }

    [Fact]
    public async Task A_pc_on_an_older_ark_release_needs_the_release_and_any_other_failure_is_unavailable()
    {
        var stale = await Ark(true, _ => (null, "unknown-runtime-operation: the PC node does not relay /api/horizon, so its build is older than this tool.")).ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.NeedsArkChange, stale.State);
        Assert.Equal(HorizonArkProvider.CalendarNeedsRelease, stale.Note);

        var olderApi = await Ark(true, _ => (null, "ArkStation operator bridge authentication is required.")).ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.NeedsArkChange, olderApi.State);
        Assert.Equal(HorizonArkProvider.CalendarNeedsRelease, olderApi.Note);

        var down = await Ark(true, _ => (null, "The PC node did not answer")).ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.Unavailable, down.State);
        Assert.Contains("The PC node did not answer", down.Note);

        var odd = await Ark(true, _ => (Json("""{ "hello": "world" }"""), "")).ReadCalendarAsync(Day);
        Assert.Equal(HorizonPartState.Unavailable, odd.State);
    }

    [Fact]
    public async Task With_no_stopgap_the_log_and_checklist_need_ark_and_nothing_is_kept_on_this_pc()
    {
        var provider = Ark(offers: true, _ => (Json(ArkDay), ""));

        var states = new[]
        {
            (await provider.ReadLogAsync(Day)).State,
            (await provider.AppendLogAsync(Day, "would be a second copy")).State,
            (await provider.ReadChecklistAsync(Day)).State,
            (await provider.AddCheckItemAsync(Day, "would be a second copy")).State,
            (await provider.SetCheckItemAsync(Day, "x", true)).State,
            (await provider.RemoveCheckItemAsync(Day, "x")).State
        };

        Assert.All(states, s => Assert.Equal(HorizonPartState.NeedsArkChange, s));
        Assert.Equal(HorizonArkProvider.LogNeedsArk, (await provider.ReadLogAsync(Day)).Note);
        Assert.Equal(HorizonArkProvider.ChecklistNeedsArk, (await provider.ReadChecklistAsync(Day)).Note);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Until_ark_has_missions_a_signed_in_account_keeps_its_log_and_checklist_here_and_says_so()
    {
        var provider = new HorizonArkProvider(
            (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<(JsonElement?, string)>((null, "")),
            untilArk: Store());

        var added = await provider.AddCheckItemAsync(Day, "Works while Ark catches up");
        var logged = await provider.AppendLogAsync(Day, "Logged signed in");

        Assert.True(added.Ready);
        Assert.Equal(HorizonArkProvider.KeptHereUntilArk, added.Note);
        Assert.Equal(HorizonArkProvider.KeptHereUntilArk, logged.Note);
        Assert.Single((await provider.ReadChecklistAsync(Day)).Value!);
        // The same days the public mode reads: one store on this PC, not a second one per mode.
        Assert.Equal("Works while Ark catches up", Assert.Single(Store().ReadChecklist(Day)).Text);
        // The calendar is still Ark's alone.
        Assert.Equal(HorizonPartState.NeedsArkChange, (await provider.ReadCalendarAsync(Day)).State);
    }

    // ---- Missions: Ark's progression math and quest states, unchanged ----

    [Fact]
    public void Xp_is_arks_formula_and_tops_out_at_500()
    {
        Assert.Equal(1.0, HorizonXp.QualityScore(1, 1, 1, 1, 1), 6);
        Assert.Equal(0.4, HorizonXp.QualityScore(1, 0, 0, 0, 0), 6);
        Assert.Equal(500, HorizonXp.VerifiedXp(5, 1, 1, 1), 6);
        Assert.Equal(100 * 3 * 0.8 * 0.9 * 0.5, HorizonXp.VerifiedXp(3, 0.8, 0.9, 0.5), 6);
        // Out-of-range inputs are clamped to Ark's ranges, never extrapolated.
        Assert.Equal(500, HorizonXp.VerifiedXp(9, 2, 2, 2), 6);
        Assert.Equal(100 * 1 * 1 * 1 * 0.25, HorizonXp.VerifiedXp(0, 1, 1, 0), 6);
        Assert.Equal("HAXES-XP-1.0.0", HorizonXp.PolicyVersion);
    }

    [Fact]
    public void Levels_and_rarity_follow_arks_thresholds()
    {
        Assert.Equal(1, HorizonXp.LevelFromXp(0));
        Assert.Equal(2, HorizonXp.LevelFromXp(10));
        Assert.Equal(11, HorizonXp.LevelFromXp(1000));
        Assert.Equal(100, HorizonXp.LevelFromXp(98_010));
        Assert.Equal(100, HorizonXp.LevelFromXp(5_000_000));
        Assert.Equal(1000, HorizonXp.XpForLevel(11), 6);

        Assert.Equal(1.0, HorizonXp.RarityScore(1, 1, 1, 1, 1), 3);
        Assert.Equal("mythic", HorizonXp.RarityTier(0.92));
        Assert.Equal("legendary", HorizonXp.RarityTier(0.82));
        Assert.Equal("epic", HorizonXp.RarityTier(0.7));
        Assert.Equal("rare", HorizonXp.RarityTier(0.55));
        Assert.Equal("uncommon", HorizonXp.RarityTier(0.38));
        Assert.Equal("common", HorizonXp.RarityTier(0.379));
    }

    [Fact]
    public void A_self_reported_tick_earns_a_fraction_and_no_proof_earns_nothing()
    {
        var at = DateTimeOffset.UnixEpoch;
        var merged = new HorizonEvidence("github", "pull-request-merged", "GalaAI/holodex#22", "github:owner", at, 0.95);
        var tick = new HorizonEvidence("capture", "self-report", "tick", "user", at, 1.0, SelfReported: true);

        Assert.Equal(0, HorizonXp.EvidenceConfidence(Array.Empty<HorizonEvidence>()));
        Assert.Equal(HorizonXp.SelfReportConfidence, HorizonXp.EvidenceConfidence(new[] { tick }));
        // A tick cannot raise what the connected evidence says.
        Assert.Equal(0.95, HorizonXp.EvidenceConfidence(new[] { tick, merged }));
        Assert.Equal(0, HorizonXp.VerifiedXp(3, 1, HorizonXp.EvidenceConfidence(Array.Empty<HorizonEvidence>()), 1));
    }

    [Fact]
    public void Quests_move_only_along_arks_transitions()
    {
        Assert.True(HorizonQuests.CanMove(HorizonQuestState.Offered, HorizonQuestState.Accepted));
        Assert.True(HorizonQuests.CanMove(HorizonQuestState.InProgress, HorizonQuestState.Submitted));
        Assert.True(HorizonQuests.CanMove(HorizonQuestState.EvaluatorReview, HorizonQuestState.Completed));
        Assert.True(HorizonQuests.CanMove(HorizonQuestState.Failed, HorizonQuestState.Offered));
        // Nothing jumps to done without the evaluator gate, and done stays done.
        Assert.False(HorizonQuests.CanMove(HorizonQuestState.InProgress, HorizonQuestState.Completed));
        Assert.False(HorizonQuests.CanMove(HorizonQuestState.Accepted, HorizonQuestState.Completed));
        Assert.False(HorizonQuests.CanMove(HorizonQuestState.Completed, HorizonQuestState.Offered));
        Assert.False(HorizonQuests.CanMove(HorizonQuestState.Abandoned, HorizonQuestState.Accepted));
    }

    [Fact]
    public void Today_shows_at_most_three_quests_and_never_counts_dailies()
    {
        var soon = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        HorizonQuest Q(string id, HorizonQuestKind kind, HorizonQuestState state, int difficulty, DateTimeOffset? due = null) =>
            new(id, id, kind, state, difficulty, "capture", due);
        var quests = new[]
        {
            Q("offered", HorizonQuestKind.Main, HorizonQuestState.Offered, 5),
            Q("daily", HorizonQuestKind.Daily, HorizonQuestState.InProgress, 1),
            Q("easy", HorizonQuestKind.Side, HorizonQuestState.Accepted, 1),
            Q("hard", HorizonQuestKind.Main, HorizonQuestState.Accepted, 4),
            Q("boss", HorizonQuestKind.Boss, HorizonQuestState.Accepted, 3, soon),
            Q("doing", HorizonQuestKind.Side, HorizonQuestState.InProgress, 2),
            Q("done", HorizonQuestKind.Main, HorizonQuestState.Completed, 5)
        };

        Assert.Equal(new[] { "doing", "boss", "hard" }, HorizonQuests.Today(quests).Select(q => q.Id));
        Assert.Equal(3, HorizonQuests.TodayLimit);
    }

    // ---- Mission sources: ready stubs, all off, never a key ----

    [Fact]
    public void Every_source_stub_starts_off_names_a_connection_and_holds_no_secret()
    {
        var stubs = HorizonSourceConfigs.Stubs();

        Assert.Contains(stubs, s => s.Id == "github");
        Assert.Contains(stubs, s => s.Id == "calendar");
        Assert.Contains(stubs, s => s.Id == "ark-agent-log");
        Assert.All(stubs, s =>
        {
            Assert.False(s.Enabled);
            Assert.NotEmpty(s.Connection);
            Assert.Contains(s.Mode, new[] { "ark", "public", "both" });
            Assert.False(HorizonSourceConfigs.HoldsSecret(s));
        });
        Assert.Equal(stubs.Count, stubs.Select(s => s.Id).Distinct().Count());
        // With everything off, neither mode has a source until the user turns one on.
        Assert.Empty(HorizonSourceConfigs.For(HorizonMode.Ark, stubs));
        Assert.Empty(HorizonSourceConfigs.For(HorizonMode.ConnectedServices, stubs));
    }

    [Fact]
    public void The_users_file_turns_sources_on_by_id_and_each_mode_sees_only_its_own()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "sources.json");
        var mine = HorizonSourceConfigs.Stubs().Where(s => s.Id is "github" or "ark-agent-log").ToList();
        foreach (var s in mine) s.Enabled = true;
        mine[0].Settings["repositories"] = "GalaAI/holodex";
        File.WriteAllText(path, HorizonSourceConfigs.ToJson(mine));

        var loaded = HorizonSourceConfigs.Load(path);

        Assert.Equal(HorizonSourceConfigs.Stubs().Count, loaded.Count);
        Assert.Equal("GalaAI/holodex", loaded.Single(s => s.Id == "github").Settings["repositories"]);
        Assert.Equal(new[] { "github" }, HorizonSourceConfigs.For(HorizonMode.ConnectedServices, loaded).Select(s => s.Id));
        Assert.Equal(new[] { "ark-agent-log" }, HorizonSourceConfigs.For(HorizonMode.Ark, loaded).Select(s => s.Id));
    }

    [Fact]
    public void A_sources_file_with_a_key_in_it_or_that_cannot_be_read_changes_nothing()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "sources.json");
        var withKey = HorizonSourceConfigs.Stubs().Where(s => s.Id == "github").ToList();
        withKey[0].Enabled = true;
        withKey[0].Settings["apiKey"] = "not-a-real-key";
        File.WriteAllText(path, HorizonSourceConfigs.ToJson(withKey));
        Assert.All(HorizonSourceConfigs.Load(path), s => Assert.False(s.Enabled));

        File.WriteAllText(path, "{ not json");
        Assert.All(HorizonSourceConfigs.Load(path), s => Assert.False(s.Enabled));
        Assert.All(HorizonSourceConfigs.Load(Path.Combine(_root, "missing.json")), s => Assert.False(s.Enabled));
    }

    [Fact]
    public void Horizon_adds_no_scope_to_the_hivemind_sign_in()
    {
        // horizon_read rides on runtime.read, which Holodex already asks for.
        Assert.Contains("runtime.read", HivemindClient.RequestedScopes);
        Assert.DoesNotContain(HivemindClient.RequestedScopes, s => s.Contains("horizon", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The surface ----

    /// <summary>Draws Horizon offscreen in both modes (HOLODEX_RENDER=folder): a full local day, and Ark mode with the parts Ark does not have yet.</summary>
    [Fact]
    public void Horizon_renders_in_both_modes()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (string.IsNullOrEmpty(folder)) return;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var calendar = HorizonArkProvider.ParseDay(Json(ArkDay), Day)!;
                IReadOnlyList<HorizonCheckItem> items = new[]
                {
                    new HorizonCheckItem("1", "Open the Horizon draft PR", true),
                    new HorizonCheckItem("2", "Decide which calendar providers come first", false)
                };
                IReadOnlyList<HorizonLogEntry> log = new[] { new HorizonLogEntry("1", new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero), "Stood up the Horizon mod.") };

                var local = new HorizonConnectedProvider(Store(), new FakeCalendar(true));
                using (var form = new HorizonForm(() => local) { Location = new Point(-4000, -4000), Today = () => Day })
                {
                    form.CaptureTo(Path.Combine(folder, "horizon-this-pc.png"), Day,
                        HorizonPart<HorizonCalendarDay>.Of(calendar),
                        HorizonPart<IReadOnlyList<HorizonCheckItem>>.Of(items),
                        HorizonPart<IReadOnlyList<HorizonLogEntry>>.Of(log));
                    form.CaptureTo(Path.Combine(folder, "horizon-this-pc-not-connected.png"), Day,
                        HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsSignIn, new GoogleCalendarSource((_, _) => Task.FromResult("{}"), () => false, () => false).ConnectHint),
                        HorizonPart<IReadOnlyList<HorizonCheckItem>>.Of(Array.Empty<HorizonCheckItem>()),
                        HorizonPart<IReadOnlyList<HorizonLogEntry>>.Of(Array.Empty<HorizonLogEntry>()));
                }

                var ark = Ark(offers: true, _ => (Json(ArkDay), ""));
                using (var form = new HorizonForm(() => ark) { Location = new Point(-4000, -4000), Today = () => Day })
                {
                    form.CaptureTo(Path.Combine(folder, "horizon-ark.png"), Day,
                        HorizonPart<HorizonCalendarDay>.Of(calendar),
                        HorizonPart<IReadOnlyList<HorizonCheckItem>>.Of(items, HorizonArkProvider.KeptHereUntilArk),
                        HorizonPart<IReadOnlyList<HorizonLogEntry>>.Of(log, HorizonArkProvider.KeptHereUntilArk));
                    form.CaptureTo(Path.Combine(folder, "horizon-ark-needs-change.png"), Day,
                        HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsArkChange, HorizonArkProvider.CalendarNeedsArk),
                        HorizonPart<IReadOnlyList<HorizonCheckItem>>.Not(HorizonPartState.NeedsArkChange, HorizonArkProvider.ChecklistNeedsArk),
                        HorizonPart<IReadOnlyList<HorizonLogEntry>>.Not(HorizonPartState.NeedsArkChange, HorizonArkProvider.LogNeedsArk));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
