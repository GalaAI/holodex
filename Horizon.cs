using System.Globalization;

namespace Holodex;

/// <summary>
/// Where Horizon's data comes from. One mod, two modes, never both at once: a signed-in Hivemind account reads and
/// writes Ark's Horizon (the data stays in Ark, Holodex keeps no copy), and everyone else reads their own calendar
/// provider and keeps the missions log and checklist on this PC. See docs/HORIZON_MOD.md.
/// </summary>
internal enum HorizonMode { Ark, ConnectedServices }

/// <summary>
/// What one part of Horizon (calendar, missions log, checklist) can do right now. NeedsArkChange is a real state,
/// not an error: the account is in Ark mode and Ark does not expose that part to an owner's client yet.
/// </summary>
internal enum HorizonPartState { Ready, NeedsSignIn, NeedsArkChange, Unavailable }

/// <summary>One part's answer: its state, its value when Ready, and a plain sentence for every other state.</summary>
internal sealed record HorizonPart<T>(HorizonPartState State, T? Value, string Note)
{
    public bool Ready => State == HorizonPartState.Ready;
    public static HorizonPart<T> Of(T value, string note = "") => new(HorizonPartState.Ready, value, note);
    public static HorizonPart<T> Not(HorizonPartState state, string note) => new(state, default, note);
}

/// <summary>
/// A calendar event on one day. Timed events carry instants; all-day events carry days, with the end exclusive the
/// way both Ark's Horizon and Google Calendar give it. <see cref="Source"/> names the calendar account it came from.
/// </summary>
internal sealed record HorizonEvent(string Id, string Title, bool AllDay, DateTimeOffset? Start, DateTimeOffset? End, string Url, string Source)
{
    /// <summary>The time column a card shows: "All day", or the local start and end ("09:30 to 10:15").</summary>
    public string When() => AllDay || Start is not { } s
        ? "All day"
        : End is { } e
            ? $"{s.ToLocalTime():HH:mm} to {e.ToLocalTime():HH:mm}"
            : $"{s.ToLocalTime():HH:mm}";
}

/// <summary>The calendar for one day, and whether every connected account answered in full.</summary>
internal sealed record HorizonCalendarDay(IReadOnlyList<HorizonEvent> Events, bool Complete, IReadOnlyList<string> Issues);

/// <summary>One line in the day's missions log: what happened, and when it was written.</summary>
internal sealed record HorizonLogEntry(string Id, DateTimeOffset At, string Text);

/// <summary>One checklist item for the day.</summary>
internal sealed record HorizonCheckItem(string Id, string Text, bool Done);

/// <summary>
/// What the Horizon surface talks to. Every call is for one calendar day and answers with a
/// <see cref="HorizonPart{T}"/>, so a part that is signed out, not in Ark yet, or unreachable is a state the surface
/// shows, never an exception and never a silent fall back to the other mode's data.
/// </summary>
internal interface IHorizonProvider
{
    HorizonMode Mode { get; }

    /// <summary>The eyebrow's last word: where this data lives ("ARK" or "THIS PC").</summary>
    string Label { get; }

    Task<HorizonPart<HorizonCalendarDay>> ReadCalendarAsync(DateOnly day, CancellationToken ct = default);

    Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> ReadLogAsync(DateOnly day, CancellationToken ct = default);
    Task<HorizonPart<IReadOnlyList<HorizonLogEntry>>> AppendLogAsync(DateOnly day, string text, CancellationToken ct = default);

    Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> ReadChecklistAsync(DateOnly day, CancellationToken ct = default);
    Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> AddCheckItemAsync(DateOnly day, string text, CancellationToken ct = default);
    Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> SetCheckItemAsync(DateOnly day, string id, bool done, CancellationToken ct = default);
    Task<HorizonPart<IReadOnlyList<HorizonCheckItem>>> RemoveCheckItemAsync(DateOnly day, string id, CancellationToken ct = default);
}

/// <summary>The one place the mode is chosen, and the small rules both providers share.</summary>
internal static class Horizon
{
    /// <summary>Longest log line or checklist item kept; longer text is cut, not refused.</summary>
    public const int MaxTextLength = 500;

    /// <summary>
    /// A saved Hivemind sign-in means an Omnitek account: Horizon is Ark's. No sign-in means the public mod. The
    /// choice does not depend on whether Hivemind answers right now: a signed-in account whose Ark is unreachable
    /// sees that, not a second, local Horizon that would drift from Ark's.
    /// </summary>
    public static HorizonMode ModeFor(bool hivemindSignedIn) => hivemindSignedIn ? HorizonMode.Ark : HorizonMode.ConnectedServices;

    /// <summary>The day as both Ark and the local store name it: YYYY-MM-DD.</summary>
    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>One line of user text, trimmed, with line breaks folded to spaces and cut to <see cref="MaxTextLength"/>; empty when there is nothing to keep.</summary>
    public static string CleanText(string? text)
    {
        string one = string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        return one.Length > MaxTextLength ? one[..MaxTextLength] : one;
    }

    /// <summary>The day header: "WED 11 NOV 2026", with TODAY, TOMORROW or YESTERDAY when it is one.</summary>
    public static string DayHeader(DateOnly day, DateOnly today)
    {
        string date = day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        int offset = day.DayNumber - today.DayNumber;
        string? near = offset switch { 0 => "TODAY", 1 => "TOMORROW", -1 => "YESTERDAY", _ => null };
        return near == null ? date : $"{date} // {near}";
    }
}
