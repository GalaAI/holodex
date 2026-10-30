using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

/// <summary>A model call for the progression agents; the app routes it through <see cref="TaskRouter"/>.</summary>
public interface IGalaModel
{
    Task<(bool Ok, string Output, string Model)> AskAsync(TaskKind kind, string prompt, string? imageDataUrl = null, CancellationToken ct = default);
}

/// <summary>Kinds of quest (the Ark trial vocabulary: a Gauntlet trains and awards XP, a Crucible gates a level).</summary>
public enum QuestKind { Mission, Gauntlet, Daily, Crucible }

public enum QuestState { Offered, Accepted, Submitted, Completed, Failed, Declined }

public sealed record Quest
{
    public string Id { get; init; } = "quest_" + Guid.NewGuid().ToString("N")[..12];
    public QuestKind Kind { get; init; }
    public Skill Skill { get; init; }
    public string Title { get; init; } = string.Empty;
    /// <summary>What Gala says when offering it.</summary>
    public string Briefing { get; init; } = string.Empty;
    /// <summary>What counts as done, for the Watcher.</summary>
    public string Rubric { get; init; } = string.Empty;
    public int Difficulty { get; init; } = 1;
    /// <summary>Gauntlet length; zero for untimed quests.</summary>
    public int TimeLimitSeconds { get; init; }
    public EvidenceKind Proof { get; init; } = EvidenceKind.Upload;
    public QuestState State { get; init; } = QuestState.Offered;
    /// <summary>Who offered it: <c>guild</c>, <c>gala</c>, or the AInbox item it came from.</summary>
    public string Source { get; init; } = "guild";
    public string? ClaimId { get; init; }
    public string Feedback { get; init; } = string.Empty;
    public DateTimeOffset OfferedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>The quests on offer and in progress, saved to <c>%LOCALAPPDATA%\Omniwand\quests.json</c>.</summary>
public sealed class QuestBoard
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "quests.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>The first quests a new board offers, one Gauntlet and one upload quest to show both paths.</summary>
    public static IReadOnlyList<Quest> Starters() => new[]
    {
        new Quest
        {
            Kind = QuestKind.Gauntlet, Skill = Skill.Art, Difficulty = 3, TimeLimitSeconds = 600, Proof = EvidenceKind.Trial,
            Title = "Finger waves Gauntlet",
            Briefing = "The Guild wants to see your progress on finger waves. Point your webcam at your desk, grab your sketch pad, pencil and marker. You have 10 minutes to fill the page. Remember flow over form.",
            Rubric = "The page fills with finger waves. Strokes flow in one direction with even spacing; later waves are cleaner than the first ones."
        },
        new Quest
        {
            Kind = QuestKind.Mission, Skill = Skill.Code, Difficulty = 2, Proof = EvidenceKind.Upload,
            Title = "Ship one small fix",
            Briefing = "Fix one bug or finish one small feature, then send a screenshot of the diff or the passing test through AInbox.",
            Rubric = "A screenshot or file shows a real code change and a passing check."
        },
        new Quest
        {
            Kind = QuestKind.Daily, Skill = Skill.Fitness, Difficulty = 1, Proof = EvidenceKind.Upload,
            Title = "Move today",
            Briefing = "Skate, walk or train for 20 minutes and send a photo or a workout screenshot.",
            Rubric = "A photo or app screenshot shows activity from today."
        }
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<Quest> _quests;

    public event Action? Changed;

    public QuestBoard(string? path = null)
    {
        _path = path ?? DefaultPath;
        _quests = new List<Quest>();
        try
        {
            if (File.Exists(_path))
                _quests = JsonSerializer.Deserialize<List<Quest>>(File.ReadAllText(_path), JsonOptions) ?? new List<Quest>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (_quests.Count == 0)
        {
            _quests.AddRange(Starters());
            Persist();
        }
    }

    public IReadOnlyList<Quest> All { get { lock (_lock) return _quests.ToList(); } }

    public IReadOnlyList<Quest> Open => All.Where(q => q.State is QuestState.Offered or QuestState.Accepted or QuestState.Submitted).ToList();

    public Quest? Find(string id) => All.FirstOrDefault(q => q.Id == id);

    /// <summary>Adds a quest unless one with the same id (an AInbox item already seen) is on the board.</summary>
    public bool Offer(Quest quest)
    {
        lock (_lock)
        {
            if (_quests.Any(q => q.Id == quest.Id)) return false;
            _quests.Add(quest);
        }
        Persist();
        return true;
    }

    public Quest Update(string id, Func<Quest, Quest> change)
    {
        Quest updated;
        lock (_lock)
        {
            int i = _quests.FindIndex(q => q.Id == id);
            if (i < 0) throw new InvalidOperationException($"no quest {id}");
            updated = change(_quests[i]);
            _quests[i] = updated;
        }
        Persist();
        return updated;
    }

    private void Persist()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_quests, JsonOptions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
}

/// <summary>A Watcher's verdict, parsed from the model's JSON.</summary>
public sealed record WatcherVerdict(bool Accept, QualityComponents Quality, string Note, string Model);

/// <summary>
/// The Watcher: reviews proof for a claim or a Gauntlet and decides whether XP is earned. It describes the evidence
/// with the vision model, then scores it against the rubric with Nemotron Super, and the ledger mints the XP.
/// </summary>
public sealed class WatcherAgent
{
    private readonly IGalaModel _model;
    private readonly ProgressionLedger _ledger;

    public WatcherAgent(IGalaModel model, ProgressionLedger ledger)
    {
        _model = model;
        _ledger = ledger;
    }

    /// <summary>Looks at each image (up to four), then scores the claim. The claim must already carry its evidence.</summary>
    public async Task<(XpClaim Claim, WatcherVerdict Verdict)> ReviewClaimAsync(string claimId, string rubric,
        IReadOnlyList<string> imageDataUrls, string extraContext = "", CancellationToken ct = default)
    {
        var claim = _ledger.Claims.FirstOrDefault(c => c.Id == claimId) ?? throw new InvalidOperationException($"no claim {claimId}");
        var seen = new List<string>();
        foreach (string image in imageDataUrls.Take(4))
        {
            var (ok, output, _) = await _model.AskAsync(TaskKind.Vision,
                "You are a Watcher checking proof for a skill quest. Describe exactly what this image shows that is relevant to: " +
                $"\"{claim.Description}\". Say if it looks recent, staged or unrelated. Two to four sentences.", image, ct);
            if (ok) seen.Add(output.Trim());
        }
        var verdict = await ScoreAsync(claim.Skill, claim.Description, rubric, seen, extraContext, ct);
        var reviewed = _ledger.Review(claimId, verdict.Accept, verdict.Quality, verdict.Note);
        return (reviewed, verdict);
    }

    /// <summary>Scores a description of the work against a rubric. Without observations it never accepts.</summary>
    public async Task<WatcherVerdict> ScoreAsync(Skill skill, string description, string rubric, IReadOnlyList<string> observations,
        string extraContext = "", CancellationToken ct = default)
    {
        if (observations.Count == 0)
            return new WatcherVerdict(false, QualityComponents.Neutral, "No evidence could be read, so no XP yet. Send a clearer photo or file.", string.Empty);
        string prompt =
            "You are the Watcher for a skill progression system. Decide if the evidence proves the task and grade it.\n" +
            $"Skill: {skill}\nTask: {description}\nRubric: {rubric}\n" +
            (string.IsNullOrWhiteSpace(extraContext) ? "" : $"Context: {extraContext}\n") +
            "Observations:\n- " + string.Join("\n- ", observations) + "\n\n" +
            "Answer with only JSON: {\"accept\": true|false, \"correctness\": 0-1, \"recovery\": 0-1, \"efficiency\": 0-1, " +
            "\"collaboration\": 0-1, \"safety\": 0-1, \"note\": \"one or two sentences of feedback: what was good and what to focus on next\"}";
        var (ok, output, model) = await _model.AskAsync(TaskKind.Reason, prompt, null, ct);
        if (!ok) return new WatcherVerdict(false, QualityComponents.Neutral, "The Watcher could not reach its model; the claim stays pending.", model);
        return ParseVerdict(output, model);
    }

    public static WatcherVerdict ParseVerdict(string output, string model)
    {
        var match = Regex.Match(output, @"\{[\s\S]*\}");
        if (!match.Success) return new WatcherVerdict(false, QualityComponents.Neutral, "The Watcher's answer had no verdict; the claim stays pending.", model);
        try
        {
            var o = JsonNode.Parse(match.Value)!.AsObject();
            double Part(string k) => Math.Clamp(o[k]?.GetValue<double>() ?? 0.5, 0, 1);
            bool accept = o["accept"]?.GetValue<bool>() ?? false;
            return new WatcherVerdict(accept,
                new QualityComponents(Part("correctness"), Part("recovery"), Part("efficiency"), Part("collaboration"), Part("safety")),
                o["note"]?.GetValue<string>() ?? string.Empty, model);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new WatcherVerdict(false, QualityComponents.Neutral, "The Watcher's verdict could not be read; the claim stays pending.", model);
        }
    }
}

/// <summary>
/// A timed Gauntlet: a countdown, a frame captured every <c>interval</c> (the webcam preview window or a screen
/// region), a vision note on each frame, and a Watcher score at the end that mints Trial XP.
/// </summary>
public sealed class GauntletSession
{
    private readonly IGalaModel _model;
    private readonly WatcherAgent _watcher;
    private readonly ProgressionLedger _ledger;
    private readonly Func<string?> _captureFrame;
    private readonly List<string> _observations = new();

    public Quest Quest { get; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyList<string> Observations => _observations;
    public event Action<TimeSpan>? Tick;
    public event Action<string>? Observed;

    public GauntletSession(Quest quest, IGalaModel model, WatcherAgent watcher, ProgressionLedger ledger, Func<string?> captureFrame)
    {
        Quest = quest;
        _model = model;
        _watcher = watcher;
        _ledger = ledger;
        _captureFrame = captureFrame;
    }

    /// <summary>Runs the trial to the end (or until cancelled, which still scores what was seen).</summary>
    public async Task<(WatcherVerdict Verdict, XpAward? Award)> RunAsync(CancellationToken ct = default)
    {
        var length = TimeSpan.FromSeconds(Math.Max(10, Quest.TimeLimitSeconds));
        var started = DateTimeOffset.UtcNow;
        var nextFrame = started;
        while (DateTimeOffset.UtcNow - started < length && !ct.IsCancellationRequested)
        {
            var remaining = length - (DateTimeOffset.UtcNow - started);
            Tick?.Invoke(remaining);
            if (DateTimeOffset.UtcNow >= nextFrame)
            {
                nextFrame = DateTimeOffset.UtcNow + Interval;
                await ObserveAsync(DateTimeOffset.UtcNow - started, CancellationToken.None);
            }
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
        }
        await ObserveAsync(DateTimeOffset.UtcNow - started, CancellationToken.None);

        var verdict = await _watcher.ScoreAsync(Quest.Skill, Quest.Title + ": " + Quest.Briefing, Quest.Rubric, _observations,
            "Observations are in time order; judge improvement across the trial.", CancellationToken.None);
        XpAward? award = null;
        if (verdict.Accept)
        {
            award = _ledger.Award("user", ProgressTrack.Human, Quest.Skill,
                new XpAwardInput(Math.Clamp(Quest.Difficulty, 1, 5), verdict.Quality, ProgressionMath.DefaultConfidence(EvidenceKind.Trial), 1),
                EvidenceKind.Trial, $"gauntlet:{Quest.Id}:{_observations.Count} frames", "watcher", verdict.Note, Quest.Id);
        }
        return (verdict, award);
    }

    private async Task ObserveAsync(TimeSpan at, CancellationToken ct)
    {
        string? frame = _captureFrame();
        if (frame is null) return;
        var (ok, output, _) = await _model.AskAsync(TaskKind.Vision,
            $"You are watching a timed {Quest.Skill} trial: \"{Quest.Title}\". Describe the progress on the page or desk right now " +
            "(how much is done, quality of the strokes or work, anything going wrong). Two sentences.", frame, ct);
        if (!ok) return;
        string note = $"{(int)at.TotalMinutes}:{at.Seconds:00} {output.Trim()}";
        _observations.Add(note);
        Observed?.Invoke(note);
    }
}

/// <summary>Something that happened today, from Holodex itself or a connected account (through Hivemind).</summary>
public sealed record ActivityEvent(DateTimeOffset At, string Source, Skill? Skill, string Summary);

public interface IActivitySource
{
    string Id { get; }
    Task<IReadOnlyList<ActivityEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>Holodex's own record: accepted XP and quests touched since the given time.</summary>
public sealed class LedgerActivitySource : IActivitySource
{
    private readonly ProgressionLedger _ledger;
    public LedgerActivitySource(ProgressionLedger ledger) => _ledger = ledger;
    public string Id => "holodex";

    public Task<IReadOnlyList<ActivityEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActivityEvent>>(_ledger.Awards.Where(a => a.SubjectId == "user" && a.AcceptedAt >= since)
            .Select(a => new ActivityEvent(a.AcceptedAt, a.Evidence.ToString().ToLowerInvariant(), a.Skill,
                string.IsNullOrWhiteSpace(a.Note) ? a.EvidenceRef : a.Note)).ToList());
}

/// <summary>
/// A demo account's day, read from <c>%LOCALAPPDATA%\Omniwand\demo-activity.json</c> (a list of
/// <see cref="ActivityEvent"/>). It stands in for connected Google Photos, Maps and Mail until a dev account is
/// connected in Hivemind, and every event it gives is labelled demo.
/// </summary>
public sealed class DemoActivitySource : IActivitySource
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "demo-activity.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    public DemoActivitySource(string? path = null) => _path = path ?? DefaultPath;
    public string Id => "demo";

    public Task<IReadOnlyList<ActivityEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path)) return Task.FromResult<IReadOnlyList<ActivityEvent>>(Array.Empty<ActivityEvent>());
            var events = JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(_path), JsonOptions) ?? new List<ActivityEvent>();
            // Demo days replay on the current date so the recap always has a today.
            var today = DateTimeOffset.Now.Date;
            return Task.FromResult<IReadOnlyList<ActivityEvent>>(events
                .Select(e => e with { At = new DateTimeOffset(today + e.At.TimeOfDay, DateTimeOffset.Now.Offset), Source = "demo:" + e.Source })
                .Where(e => e.At >= since).ToList());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<ActivityEvent>>(Array.Empty<ActivityEvent>());
        }
    }
}

/// <summary>
/// Gala's daily recap: what the sources saw today, what the user meant to do and did not, and a question about it.
/// Each matched activity earns Connector XP; the habits list is the user's own (Settings), e.g. "skate".
/// </summary>
public sealed class DailyRecap
{
    private readonly IGalaModel _model;
    private readonly ProgressionLedger _ledger;
    private readonly IReadOnlyList<IActivitySource> _sources;

    public DailyRecap(IGalaModel model, ProgressionLedger ledger, IReadOnlyList<IActivitySource> sources)
    {
        _model = model;
        _ledger = ledger;
        _sources = sources;
    }

    public async Task<(bool Ok, string Text, IReadOnlyList<ActivityEvent> Events)> ComposeAsync(string userName, IReadOnlyList<string> habits, CancellationToken ct = default)
    {
        var since = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset);
        var events = new List<ActivityEvent>();
        foreach (var source in _sources)
            events.AddRange(await source.ReadAsync(since, ct));
        events.Sort((a, b) => a.At.CompareTo(b.At));

        var sb = new StringBuilder();
        sb.AppendLine($"You are Gala, {userName}'s co-op orchestrator. Write a short daily recap in your own warm, direct voice.");
        sb.AppendLine("Name what was seen today and where it came from (say 'demo account' for demo sources). Then name any habit that has no matching activity and ask what happened, and suggest one app or API that could log it automatically next time. Four to six sentences, no lists.");
        sb.AppendLine("Today's activity:");
        foreach (var e in events) sb.AppendLine($"- {e.At:HH:mm} [{e.Source}] {e.Summary}");
        if (events.Count == 0) sb.AppendLine("- nothing recorded yet");
        sb.AppendLine("Habits the user wants to keep: " + (habits.Count == 0 ? "none set" : string.Join(", ", habits)));
        var (ok, output, _) = await _model.AskAsync(TaskKind.Reason, sb.ToString(), null, ct);

        if (ok)
        {
            // Connector events with a skill earn XP once each (the evidence ref keeps a replay from paying twice).
            var paid = new HashSet<string>(_ledger.Awards.Select(a => a.EvidenceRef));
            foreach (var e in events.Where(e => e.Skill.HasValue && !e.Source.StartsWith("holodex", StringComparison.Ordinal) && e.Source != "activity"))
            {
                string evidenceRef = $"{e.Source}:{e.At:yyyyMMddHHmm}:{e.Summary}";
                if (!paid.Add(evidenceRef)) continue;
                _ledger.Award("user", ProgressTrack.Human, e.Skill!.Value,
                    new XpAwardInput(1, QualityComponents.Neutral, ProgressionMath.DefaultConfidence(EvidenceKind.Connector), 1),
                    EvidenceKind.Connector, evidenceRef, "gala", e.Summary);
            }
        }
        return (ok, ok ? output.Trim() : "Gala could not write the recap: " + output, events);
    }
}

/// <summary>
/// The Apex evaluation at onboarding: the user rates each skill and says what they can do, optionally takes the
/// reasoning and emotional baselines, and Nemotron Super turns it into starting levels and trait estimates.
/// </summary>
public sealed class ApexEvaluator
{
    private readonly IGalaModel _model;
    public ApexEvaluator(IGalaModel model) => _model = model;

    public sealed record Answers(IReadOnlyDictionary<Skill, int> SelfRatings, IReadOnlyDictionary<Skill, string> CanDo,
        string Goals, int? ReasoningScore = null, int? EmotionalScore = null);

    public async Task<ApexEvaluation> EvaluateAsync(Answers answers, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the Apex evaluator. From a new user's self assessment, set a starting level (1 to 60) for each skill and estimate traits.");
        sb.AppendLine("Self ratings are 0 to 10 and often overconfident; weigh the concrete 'can do' text more. No evidence yet, so never exceed level 60.");
        foreach (Skill s in Enum.GetValues<Skill>())
        {
            answers.SelfRatings.TryGetValue(s, out int r);
            answers.CanDo.TryGetValue(s, out string? can);
            sb.AppendLine($"- {s}: rating {r}/10; can do: {(string.IsNullOrWhiteSpace(can) ? "(nothing said)" : can)}");
        }
        sb.AppendLine("Goals: " + answers.Goals);
        sb.AppendLine("Answer with only JSON: {\"levels\": {\"Academics\": n, ... all ten skills}, \"traits\": {\"focus\": 0-100, \"consistency\": 0-100, \"curiosity\": 0-100}, \"summary\": \"two sentences\"}");
        var (ok, output, model) = await _model.AskAsync(TaskKind.Reason, sb.ToString(), null, ct);
        var levels = new Dictionary<Skill, int>();
        var traits = new Dictionary<string, int>();
        string summary = string.Empty;
        if (ok && Regex.Match(output, @"\{[\s\S]*\}") is { Success: true } m)
        {
            try
            {
                var o = JsonNode.Parse(m.Value)!.AsObject();
                foreach (Skill s in Enum.GetValues<Skill>())
                    if (o["levels"]?[s.ToString()] is JsonNode n && double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        levels[s] = Math.Clamp((int)Math.Round(v), 1, 60);
                if (o["traits"] is JsonObject t)
                    foreach (var (k, node) in t)
                        if (node is not null && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tv))
                            traits[k] = Math.Clamp((int)Math.Round(tv), 0, 100);
                summary = o["summary"]?.GetValue<string>() ?? string.Empty;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        // Anything the model left out starts from the self rating, scaled to the same 1 to 60 range.
        foreach (Skill s in Enum.GetValues<Skill>())
            if (!levels.ContainsKey(s))
                levels[s] = Math.Clamp(1 + 4 * (answers.SelfRatings.TryGetValue(s, out int r) ? r : 0), 1, 41);
        return new ApexEvaluation
        {
            EvaluatorModel = model,
            BaselineLevels = levels,
            Traits = traits,
            ReasoningBaseline = answers.ReasoningScore,
            EmotionalBaseline = answers.EmotionalScore,
            Summary = ok ? summary : "Started from self ratings; the evaluator model was not reachable."
        };
    }
}
