using System.Globalization;
using System.Text.Json;

namespace Holodex;

/// <summary>
/// A calendar the public mode can read with the user's own sign-in. One implementation today (Google Calendar);
/// the next provider implements this and nothing else changes. Reads throw InvalidOperationException or
/// HttpRequestException with a plain message; the provider turns that into a state.
/// </summary>
internal interface IHorizonCalendarSource
{
    /// <summary>The provider's name as the surface says it ("Google Calendar").</summary>
    string Name { get; }

    /// <summary>Whether a sign-in for this calendar is saved on this PC.</summary>
    bool Connected { get; }

    /// <summary>What to do when not connected, as one sentence.</summary>
    string ConnectHint { get; }

    Task<HorizonCalendarDay> ReadDayAsync(DateOnly day, CancellationToken ct = default);
}

/// <summary>
/// Google Calendar, read-only, through the user's own Google OAuth client (the one they pasted into the Vault) and
/// their own consent. Holodex ships no Google client, no key and no shared account. The grant is separate from the
/// Vault's Drive sign-in and asks for one scope, calendar.events.readonly, so connecting the calendar never widens
/// what Drive was given. Reads the primary calendar for the PC's local day.
/// </summary>
internal sealed class GoogleCalendarSource : IHorizonCalendarSource
{
    public const string Scope = "https://www.googleapis.com/auth/calendar.events.readonly";
    public const string TokenTarget = "Holodex:Vault:google-calendar";
    public static readonly Uri Redirect = new("http://127.0.0.1:47613/horizon/google/");

    private readonly Func<string, CancellationToken, Task<string>> _get;
    private readonly Func<bool> _connected;
    private readonly Func<bool> _hasClient;
    private readonly TimeZoneInfo _zone;

    /// <summary>The live source. Tests pass their own <paramref name="get"/> (url to response body) and switches.</summary>
    public GoogleCalendarSource(Func<string, CancellationToken, Task<string>>? get = null, Func<bool>? connected = null, Func<bool>? hasClient = null, TimeZoneInfo? zone = null)
    {
        _get = get ?? ((url, ct) => GoogleAdapter.GetTextAsync(url, TokenTarget, "Google Calendar", ct));
        _connected = connected ?? (() => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(TokenTarget)));
        _hasClient = hasClient ?? (() => GoogleAdapter.HasClient);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public string Name => "Google Calendar";
    public bool Connected => _connected();
    public bool HasClient => _hasClient();

    public string ConnectHint => HasClient
        ? "Connect Google Calendar to see your day here. Holodex asks Google for read-only access to your events, with your own sign-in."
        : "Open the Vault and paste your own Google OAuth client file, then connect Google Calendar here. Holodex ships no Google key.";

    /// <summary>The browser sign-in for the calendar scope, with the Vault's Google client. Tokens stay in Credential Manager.</summary>
    public static Task SignInAsync(CancellationToken ct = default) => GoogleAdapter.SignInForAsync(Scope, TokenTarget, Redirect, ct);

    /// <summary>Forgets the calendar grant on this PC. The Vault's Google client and Drive sign-in are left alone.</summary>
    public static void SignOut() => ArkStationClient.TryDeleteWindowsCredential(TokenTarget);

    public async Task<HorizonCalendarDay> ReadDayAsync(DateOnly day, CancellationToken ct = default)
    {
        var events = new List<HorizonEvent>();
        string? page = null;
        // A day rarely needs a second page; the cap keeps a misbehaving answer from looping.
        for (int i = 0; i < 4; i++)
        {
            var (items, next) = ParseEvents(await _get(DayUrl(day, _zone, page), ct));
            events.AddRange(items);
            if (string.IsNullOrEmpty(next)) return new HorizonCalendarDay(Sorted(events), true, Array.Empty<string>());
            page = next;
        }
        return new HorizonCalendarDay(Sorted(events), false, new[] { "This day has more events than Holodex reads at once." });
    }

    /// <summary>All-day events first, then by start time.</summary>
    internal static IReadOnlyList<HorizonEvent> Sorted(IEnumerable<HorizonEvent> events) =>
        events.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start ?? DateTimeOffset.MinValue).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The events.list call for one local day: local midnight to the next local midnight, recurring events expanded
    /// (singleEvents), only the fields Horizon shows.
    /// </summary>
    internal static string DayUrl(DateOnly day, TimeZoneInfo zone, string? pageToken = null)
    {
        static string Stamp(DateOnly d, TimeZoneInfo z)
        {
            var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // A midnight a clock change skips does not exist; the first valid instant after it starts the day.
            while (z.IsInvalidTime(local)) local = local.AddMinutes(30);
            return new DateTimeOffset(local, z.GetUtcOffset(local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }
        var query = new List<string>
        {
            "timeMin=" + Uri.EscapeDataString(Stamp(day, zone)),
            "timeMax=" + Uri.EscapeDataString(Stamp(day.AddDays(1), zone)),
            "singleEvents=true",
            "orderBy=startTime",
            "maxResults=250",
            "fields=" + Uri.EscapeDataString("nextPageToken,items(id,summary,status,htmlLink,start,end)")
        };
        if (!string.IsNullOrEmpty(pageToken)) query.Add("pageToken=" + Uri.EscapeDataString(pageToken));
        return "https://www.googleapis.com/calendar/v3/calendars/primary/events?" + string.Join("&", query);
    }

    /// <summary>Reads an events.list answer. Cancelled events and events with no start are left out.</summary>
    internal static (IReadOnlyList<HorizonEvent> Events, string? NextPage) ParseEvents(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException("Google Calendar answered with something that is not a calendar."); }
        using (doc)
        {
            var root = doc.RootElement;
            var events = new List<HorizonEvent>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (HorizonJson.Str(item, "status") == "cancelled") continue;
                    if (!item.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object) continue;
                    item.TryGetProperty("end", out var end);
                    var parsed = HorizonJson.Event(
                        id: HorizonJson.Str(item, "id"),
                        title: HorizonJson.Str(item, "summary"),
                        startText: FirstOf(start, "dateTime", "date"),
                        endText: end.ValueKind == JsonValueKind.Object ? FirstOf(end, "dateTime", "date") : "",
                        url: HorizonJson.Str(item, "htmlLink"),
                        source: "Google Calendar");
                    if (parsed != null) events.Add(parsed);
                }
            }
            string next = root.ValueKind == JsonValueKind.Object ? HorizonJson.Str(root, "nextPageToken") : "";
            return (events, next.Length > 0 ? next : null);
        }
    }

    private static string FirstOf(JsonElement obj, params string[] names) =>
        names.Select(n => HorizonJson.Str(obj, n)).FirstOrDefault(v => v.Length > 0) ?? "";
}

/// <summary>The JSON reading both calendar shapes share (Ark's Horizon and Google's events).</summary>
internal static class HorizonJson
{
    public static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// One event from its text bounds: a YYYY-MM-DD start is an all-day event, anything else must be an instant with
    /// an offset. Null when the start cannot be read. An untitled event is shown as busy time, and a link is kept
    /// only when it is https.
    /// </summary>
    public static HorizonEvent? Event(string id, string title, string startText, string endText, string url, string source)
    {
        bool allDay = Horizon.TryParseDay(startText, out var startDay);
        DateTimeOffset? start, end = null;
        if (allDay)
        {
            start = new DateTimeOffset(startDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            if (Horizon.TryParseDay(endText, out var endDay)) end = new DateTimeOffset(endDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
        else
        {
            if (!TryInstant(startText, out var s)) return null;
            start = s;
            if (TryInstant(endText, out var e)) end = e;
        }
        bool https = Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps;
        return new HorizonEvent(
            id.Length > 0 ? id : $"{startText}|{title}",
            string.IsNullOrWhiteSpace(title) ? "Busy" : title.Trim(),
            allDay, start, end, https ? url : "", source);
    }

    private static bool TryInstant(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
}

/// <summary>
/// The public mode: the user's own calendar provider, and a missions log and checklist kept on this PC for this
/// Windows user. No Hivemind, no Ark, no Omnitek account.
/// </summary>
internal sealed class HorizonConnectedProvider : IHorizonProvider
{
    private readonly HorizonLocalStore _store;
    private readonly IHorizonCalendarSource? _calendar;

    public HorizonConnectedProvider(HorizonLocalStore store, IHorizonCalendarSource? calendar)
    {
        _store = store;
        _calendar = calendar;
    }

    public HorizonMode Mode => HorizonMode.ConnectedServices;
    public string Label => "THIS PC";

    public async Task<HorizonPart<HorizonCalendarDay>> ReadCalendarAsync(DateOnly day, CancellationToken ct = default)
    {
        if (_calendar == null)
            return HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsSignIn, "No calendar is connected.");
        if (!_calendar.Connected)
            return HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsSignIn, _calendar.ConnectHint);
        try
        {
            return HorizonPart<HorizonCalendarDay>.Of(await _calendar.ReadDayAsync(day, ct), _calendar.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            return HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.Unavailable, $"{_calendar.Name} did not answer: {ex.Message}");
        }
    }

    public Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> ReadLogAsync(DateOnly day, CancellationToken ct = default) =>
        Local(() => _store.ReadLog(day));

    public Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> AppendLogAsync(DateOnly day, string text, CancellationToken ct = default) =>
        Local(() => _store.AppendLog(day, text));

    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> ReadChecklistAsync(DateOnly day, CancellationToken ct = default) =>
        Local(() => _store.ReadChecklist(day));

    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> AddCheckItemAsync(DateOnly day, string text, CancellationToken ct = default) =>
        Local(() => _store.AddCheckItem(day, text));

    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> SetCheckItemAsync(DateOnly day, string id, bool done, CancellationToken ct = default) =>
        Local(() => _store.SetCheckItem(day, id, done));

    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> RemoveCheckItemAsync(DateOnly day, string id, CancellationToken ct = default) =>
        Local(() => _store.RemoveCheckItem(day, id));

    /// <summary>A disk that refuses the write is a state the surface shows, with the reason; the day on screen stays as it was.</summary>
    private static Task<HorizonPart<T>> Local<T>(Func<T> work)
    {
        try { return Task.FromResult(HorizonPart<T>.Of(work())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HorizonPart<T>.Not(HorizonPartState.Unavailable, $"This PC could not save that: {ex.Message}"));
        }
    }
}

/// <summary>
/// The Omnitek mode: Horizon is Ark's, reached through the signed-in Hivemind. Holodex is a client and keeps no
/// copy. What Ark exposes to an owner's client is found by asking Hivemind which tools it offers, so this provider
/// lights up part by part as Ark ships them and says "needs an Ark change" for the rest. Today that is:
/// the calendar through <c>horizon_read</c> (scope runtime.read) once Ark carries it, and nothing for the missions
/// log and checklist, which Ark does not have yet. It never falls back to the local store.
/// </summary>
internal sealed class HorizonArkProvider : IHorizonProvider
{
    /// <summary>Ark's read-only calendar projection as a Hivemind tool: {day?: YYYY-MM-DD, month?: YYYY-MM}.</summary>
    public const string CalendarTool = "horizon_read";

    /// <summary>
    /// What an owner's PC on an older Ark release answers. The PC node from before the route says
    /// unknown-runtime-operation; a newer node in front of an older Ark API gets that API's operator bridge refusal,
    /// because the older route does not accept the node's capability assertion.
    /// </summary>
    internal static readonly string[] OlderReleaseMarks = { "unknown-runtime-operation", "operator bridge authentication" };

    public const string CalendarNeedsArk =
        "Ark does not offer Horizon to Holodex yet. It needs the Ark change that adds the horizon_read Hivemind tool. Your calendar is still in ArkStation.";
    public const string CalendarNeedsRelease =
        "Your Hivemind offers Horizon, but the Ark on your PC has not adopted the release that carries it. Update Ark on your PC, then try again.";
    public const string LogNeedsArk =
        "Ark has no missions log for Horizon yet, so there is nothing to read or write. It appears here when Ark adds it. Holodex keeps no second copy.";
    public const string ChecklistNeedsArk =
        "Ark has no Horizon checklist yet, so there is nothing to read or write. It appears here when Ark adds it. Holodex keeps no second copy.";

    private readonly Func<string, CancellationToken, Task<bool>> _offersTool;
    private readonly Func<DateOnly, CancellationToken, Task<(JsonElement? Data, string Error)>> _readDay;

    /// <param name="offersTool">Whether the signed-in Hivemind lists a tool.</param>
    /// <param name="readDay">Calls <see cref="CalendarTool"/> for a day: the envelope's data, or null with why.</param>
    public HorizonArkProvider(Func<string, CancellationToken, Task<bool>> offersTool, Func<DateOnly, CancellationToken, Task<(JsonElement? Data, string Error)>> readDay)
    {
        _offersTool = offersTool;
        _readDay = readDay;
    }

    public HorizonMode Mode => HorizonMode.Ark;
    public string Label => "ARK";

    public async Task<HorizonPart<HorizonCalendarDay>> ReadCalendarAsync(DateOnly day, CancellationToken ct = default)
    {
        if (!await _offersTool(CalendarTool, ct))
            return HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsArkChange, CalendarNeedsArk);
        var (data, error) = await _readDay(day, ct);
        if (data is not { } root)
        {
            return OlderReleaseMarks.Any(mark => error.Contains(mark, StringComparison.OrdinalIgnoreCase))
                ? HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.NeedsArkChange, CalendarNeedsRelease)
                : HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.Unavailable, error.Length > 0 ? $"Ark's Horizon did not answer: {error}" : "Ark's Horizon did not answer.");
        }
        return ParseDay(root, day) is { } parsed
            ? HorizonPart<HorizonCalendarDay>.Of(parsed, "Ark")
            : HorizonPart<HorizonCalendarDay>.Not(HorizonPartState.Unavailable, "Ark's Horizon answered with something Holodex does not read as a day.");
    }

    public Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> ReadLogAsync(DateOnly day, CancellationToken ct = default) => NoLog();
    public Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> AppendLogAsync(DateOnly day, string text, CancellationToken ct = default) => NoLog();
    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> ReadChecklistAsync(DateOnly day, CancellationToken ct = default) => NoChecklist();
    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> AddCheckItemAsync(DateOnly day, string text, CancellationToken ct = default) => NoChecklist();
    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> SetCheckItemAsync(DateOnly day, string id, bool done, CancellationToken ct = default) => NoChecklist();
    public Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> RemoveCheckItemAsync(DateOnly day, string id, CancellationToken ct = default) => NoChecklist();

    private static Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> NoLog() =>
        Task.FromResult(HorizonPart<IReadOnlyList<HorizonLogEntry>>.Not(HorizonPartState.NeedsArkChange, LogNeedsArk));

    private static Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> NoChecklist() =>
        Task.FromResult(HorizonPart<IReadOnlyList<HorizonCheckItem>>.Not(HorizonPartState.NeedsArkChange, ChecklistNeedsArk));

    /// <summary>
    /// Reads Ark's day result (@ark/shared-types horizonResultSchema): {events, sources, complete, day, ...}.
    /// horizon_read answers with the Ark API's {ok, data} envelope as it is, and the Hivemind client hands on its
    /// data; a result still inside an envelope is read too. Null when it is not a day result, or is for a
    /// different day than was asked.
    /// </summary>
    internal static HorizonCalendarDay? ParseDay(JsonElement root, DateOnly asked)
    {
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("events", out _) &&
            root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            root = inner;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("events", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        string day = HorizonJson.Str(root, "day");
        if (day.Length > 0 && day != Horizon.DayKey(asked)) return null;

        var events = new List<HorizonEvent>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var parsed = HorizonJson.Event(
                id: HorizonJson.Str(item, "id"),
                title: HorizonJson.Str(item, "title"),
                startText: HorizonJson.Str(item, "start"),
                endText: HorizonJson.Str(item, "end"),
                url: HorizonJson.Str(item, "url"),
                source: SourceName(item));
            if (parsed != null) events.Add(parsed);
        }

        var issues = new List<string>();
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                string issue = HorizonJson.Str(source, "issue");
                if (issue.Length == 0) continue;
                string who = HorizonJson.Str(source, "accountEmail");
                issues.Add(who.Length > 0 ? $"{who}: {issue}" : issue);
            }
        }
        bool complete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
        return new HorizonCalendarDay(GoogleCalendarSource.Sorted(events), complete, issues);
    }

    private static string SourceName(JsonElement item)
    {
        if (!item.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return "Ark";
        var names = sources.EnumerateArray()
            .Select(s => HorizonJson.Str(s, "accountEmail") is { Length: > 0 } email ? email : HorizonJson.Str(s, "calendarName"))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > 0 ? string.Join(", ", names) : "Ark";
    }
}
