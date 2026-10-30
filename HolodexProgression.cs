using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omniwand;

/// <summary>The skills on the Stats page skill tree. The same list is used for the user, the agents and Apex.</summary>
public enum Skill { Academics, Art, Code, Cooking, Business, Learning, Fitness, Social, Gaming, Craft }

/// <summary>Whose progress a record counts toward.</summary>
public enum ProgressTrack { Human, Agent, Apex }

/// <summary>
/// How an XP claim is backed. Each kind has its own default evidence confidence, which is a factor in the XP formula.
/// A self report earns nothing until a Watcher accepts it with real evidence.
/// </summary>
public enum EvidenceKind
{
    /// <summary>Something Holodex saw happen (a Spell cast, a Codex save, an AInbox send).</summary>
    Activity,
    /// <summary>A connected account showed it (photos, maps, mail through Hivemind).</summary>
    Connector,
    /// <summary>A file or photo submitted through AInbox.</summary>
    Upload,
    /// <summary>A timed Gauntlet watched by a Watcher agent.</summary>
    Trial,
    /// <summary>The user said they did it; waits for a Watcher.</summary>
    SelfReport
}

/// <summary>The five quality parts and their ratified weights (Ark packages/shared-types/src/progression.ts).</summary>
public sealed record QualityComponents(double Correctness, double Recovery, double Efficiency, double Collaboration, double SafetyAndProvenance)
{
    public static readonly QualityComponents Neutral = new(0.6, 0.6, 0.6, 0.6, 0.6);
}

public sealed record XpAwardInput(int Difficulty, QualityComponents Quality, double EvidenceConfidence, double NoveltyFactor);

/// <summary>
/// Ark's progression math, ported unchanged from <c>packages/shared-types/src/progression.ts</c> so Holodex levels mean
/// the same thing as Ark talent levels. XP is <c>100 * difficulty * quality * evidence_confidence * novelty</c>; a level
/// needs <c>10 * (level - 1)^2</c> XP, so each level costs more than the last.
/// </summary>
public static class ProgressionMath
{
    public const string PolicyVersion = "HOLODEX-XP-1.0.0";
    public static readonly IReadOnlyList<int> CrucibleCheckpoints = new[] { 5, 10, 20, 40, 60, 80, 95 };

    public static double QualityScore(QualityComponents q) =>
        q.Correctness * 0.4 + q.Recovery * 0.2 + q.Efficiency * 0.15 + q.Collaboration * 0.1 + q.SafetyAndProvenance * 0.15;

    public static double VerifiedXp(XpAwardInput input)
    {
        if (input.Difficulty is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(input), "difficulty must be 1 to 5");
        if (input.EvidenceConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(input), "evidence confidence must be 0 to 1");
        if (input.NoveltyFactor is < 0.25 or > 1) throw new ArgumentOutOfRangeException(nameof(input), "novelty must be 0.25 to 1");
        foreach (double part in new[] { input.Quality.Correctness, input.Quality.Recovery, input.Quality.Efficiency, input.Quality.Collaboration, input.Quality.SafetyAndProvenance })
            if (part is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(input), "quality parts must be 0 to 1");
        return 100 * input.Difficulty * QualityScore(input.Quality) * input.EvidenceConfidence * input.NoveltyFactor;
    }

    public static int LevelFromXp(double totalXp) =>
        !double.IsFinite(totalXp) || totalXp < 0 ? 1 : Math.Min(100, (int)Math.Floor(Math.Sqrt(totalXp / 10)) + 1);

    /// <summary>The XP where <paramref name="level"/> starts (the inverse of <see cref="LevelFromXp"/>).</summary>
    public static double XpForLevel(int level) => 10.0 * Math.Pow(Math.Clamp(level, 1, 100) - 1, 2);

    public static string BandForLevel(int level) =>
        level >= 95 ? "mastery" : level >= 80 ? "advanced" : level >= 60 ? "proficient" :
        level >= 40 ? "bounded_verified" : level >= 20 ? "assisted_sandbox" : "observed";

    public static int? NextCrucible(int level) => CrucibleCheckpoints.Where(c => level < c).Select(c => (int?)c).FirstOrDefault();

    /// <summary>Weighted mean of the skill levels, capped at 15 above the lowest (display only, never for routing).</summary>
    public static int OverallLevel(IReadOnlyList<int> levels)
    {
        if (levels.Count == 0) return 1;
        double mean = levels.Average();
        return Math.Max(1, Math.Min(100, Math.Min((int)Math.Round(mean), levels.Min() + 15)));
    }

    public static double DefaultConfidence(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Activity => 0.3,
        EvidenceKind.Connector => 0.6,
        EvidenceKind.Upload => 0.7,
        EvidenceKind.Trial => 0.9,
        _ => 0
    };
}

/// <summary>Minted XP: only a Watcher, Gala or the Apex evaluator can accept one, and the XP must match the formula.</summary>
public sealed record XpAward
{
    public string Id { get; init; } = "xp_" + Guid.NewGuid().ToString("N")[..12];
    public string SubjectId { get; init; } = "user";
    public ProgressTrack Track { get; init; }
    public Skill Skill { get; init; }
    public string? TaskRef { get; init; }
    public XpAwardInput Input { get; init; } = new(1, QualityComponents.Neutral, 0.3, 1);
    public double VerifiedXp { get; init; }
    public EvidenceKind Evidence { get; init; }
    public string EvidenceRef { get; init; } = string.Empty;
    public string EvaluatorRef { get; init; } = string.Empty;
    public string PolicyVersion { get; init; } = ProgressionMath.PolicyVersion;
    public string Note { get; init; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum ClaimState { Pending, Accepted, Rejected }

/// <summary>A claim waiting for a Watcher: what the user says they did and what they sent as proof.</summary>
public sealed record XpClaim
{
    public string Id { get; init; } = "claim_" + Guid.NewGuid().ToString("N")[..12];
    public string SubjectId { get; init; } = "user";
    public Skill Skill { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Difficulty { get; init; } = 1;
    public EvidenceKind Evidence { get; init; } = EvidenceKind.SelfReport;
    /// <summary>A file path, AInbox item id or connector record; empty until the user submits proof.</summary>
    public string EvidenceRef { get; init; } = string.Empty;
    public ClaimState State { get; init; } = ClaimState.Pending;
    public string? AwardId { get; init; }
    public string ReviewNote { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The Apex evaluation taken at onboarding (or later). It is separate from the Genesis profile Gala keeps: Genesis
/// says who the user is and what they want, Apex says where each skill starts and how the traits read today.
/// </summary>
public sealed record ApexEvaluation
{
    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.UtcNow;
    public string EvaluatorModel { get; init; } = string.Empty;
    public IReadOnlyDictionary<Skill, int> BaselineLevels { get; init; } = new Dictionary<Skill, int>();
    /// <summary>Focus, consistency, curiosity and others, 0 to 100, as estimated at evaluation time.</summary>
    public IReadOnlyDictionary<string, int> Traits { get; init; } = new Dictionary<string, int>();
    /// <summary>Optional reasoning and emotional baselines (opt-in only). Estimates for the app, not a clinical test.</summary>
    public int? ReasoningBaseline { get; init; }
    public int? EmotionalBaseline { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed record SkillStanding(Skill Skill, int Level, double TotalXp, double XpIntoLevel, double XpForNext, string Band, int? NextCrucible)
{
    public double Progress => XpForNext <= 0 ? 1 : Math.Clamp(XpIntoLevel / XpForNext, 0, 1);
}

/// <summary>
/// The local XP ledger for every subject (the user, each agent, Apex), saved to
/// <c>%LOCALAPPDATA%\Omniwand\progression.json</c>. A skill's level counts its Apex baseline plus every accepted award.
/// </summary>
public sealed class ProgressionLedger
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "progression.json");

    /// <summary>Evaluators that may mint XP. Anything else is refused.</summary>
    public static readonly IReadOnlySet<string> Evaluators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "watcher", "gala", "apex", "holodex" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class State
    {
        public List<XpAward> Awards { get; set; } = new();
        public List<XpClaim> Claims { get; set; } = new();
        public ApexEvaluation? Apex { get; set; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private State _state = new();

    public event Action? Changed;

    public ProgressionLedger(string? path = null)
    {
        _path = path ?? DefaultPath;
        try
        {
            if (File.Exists(_path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _state = new State();
        }
    }

    public IReadOnlyList<XpAward> Awards { get { lock (_lock) return _state.Awards.ToList(); } }
    public IReadOnlyList<XpClaim> Claims { get { lock (_lock) return _state.Claims.ToList(); } }
    public ApexEvaluation? Apex { get { lock (_lock) return _state.Apex; } }

    /// <summary>Mints XP through the formula. Refuses an unknown evaluator or a self report (those go through a claim).</summary>
    public XpAward Award(string subjectId, ProgressTrack track, Skill skill, XpAwardInput input, EvidenceKind evidence,
        string evidenceRef, string evaluator, string note = "", string? taskRef = null)
    {
        if (!Evaluators.Contains(evaluator)) throw new InvalidOperationException($"'{evaluator}' cannot award XP");
        if (evidence == EvidenceKind.SelfReport) throw new InvalidOperationException("a self report needs a Watcher review first");
        var award = new XpAward
        {
            SubjectId = subjectId,
            Track = track,
            Skill = skill,
            TaskRef = taskRef,
            Input = input,
            VerifiedXp = ProgressionMath.VerifiedXp(input),
            Evidence = evidence,
            EvidenceRef = evidenceRef,
            EvaluatorRef = evaluator,
            Note = note
        };
        lock (_lock) _state.Awards.Add(award);
        Persist();
        return award;
    }

    /// <summary>An automatic award for something Holodex saw: difficulty 1, low confidence, so it levels slowly.</summary>
    public XpAward AwardActivity(Skill skill, string what, string subjectId = "user", ProgressTrack track = ProgressTrack.Human) =>
        Award(subjectId, track, skill, new XpAwardInput(1, QualityComponents.Neutral, ProgressionMath.DefaultConfidence(EvidenceKind.Activity), NoveltyFor(subjectId, skill)),
            EvidenceKind.Activity, what, "holodex", what);

    public XpClaim SubmitClaim(XpClaim claim)
    {
        lock (_lock) _state.Claims.Add(claim);
        Persist();
        return claim;
    }

    public XpClaim AttachEvidence(string claimId, EvidenceKind kind, string evidenceRef) =>
        UpdateClaim(claimId, c => c with { Evidence = kind, EvidenceRef = evidenceRef });

    /// <summary>
    /// A Watcher's verdict on a claim. Accepting needs proof attached (a self report alone stays pending) and mints XP
    /// from the Watcher's quality read; rejecting keeps the note so the user knows what to resubmit.
    /// </summary>
    public XpClaim Review(string claimId, bool accept, QualityComponents quality, string note, string evaluator = "watcher")
    {
        var claim = Claims.FirstOrDefault(c => c.Id == claimId) ?? throw new InvalidOperationException($"no claim {claimId}");
        if (claim.State != ClaimState.Pending) return claim;
        if (!accept) return UpdateClaim(claimId, c => c with { State = ClaimState.Rejected, ReviewNote = note });
        if (claim.Evidence == EvidenceKind.SelfReport || string.IsNullOrWhiteSpace(claim.EvidenceRef))
            return UpdateClaim(claimId, c => c with { ReviewNote = "Waiting for proof: send a photo, file or trial through AInbox." });
        var award = Award(claim.SubjectId, ProgressTrack.Human, claim.Skill,
            new XpAwardInput(claim.Difficulty, quality, ProgressionMath.DefaultConfidence(claim.Evidence), NoveltyFor(claim.SubjectId, claim.Skill)),
            claim.Evidence, claim.EvidenceRef, evaluator, note, claim.Id);
        return UpdateClaim(claimId, c => c with { State = ClaimState.Accepted, AwardId = award.Id, ReviewNote = note });
    }

    public void SetApexEvaluation(ApexEvaluation evaluation)
    {
        lock (_lock) _state.Apex = evaluation;
        Persist();
    }

    public SkillStanding Standing(string subjectId, Skill skill)
    {
        double baseline = subjectId == "user" && Apex?.BaselineLevels.TryGetValue(skill, out int b) == true ? ProgressionMath.XpForLevel(b) : 0;
        double total = baseline + Awards.Where(a => a.SubjectId == subjectId && a.Skill == skill).Sum(a => a.VerifiedXp);
        int level = ProgressionMath.LevelFromXp(total);
        double start = ProgressionMath.XpForLevel(level);
        double next = level >= 100 ? 0 : ProgressionMath.XpForLevel(level + 1) - start;
        return new SkillStanding(skill, level, total, total - start, next, ProgressionMath.BandForLevel(level), ProgressionMath.NextCrucible(level));
    }

    public IReadOnlyList<SkillStanding> Standings(string subjectId) =>
        Enum.GetValues<Skill>().Select(s => Standing(subjectId, s)).ToList();

    public int OverallLevel(string subjectId) => ProgressionMath.OverallLevel(Standings(subjectId).Select(s => s.Level).ToList());

    /// <summary>Subjects with any XP on the track (agents and Apex appear once they earn something).</summary>
    public IReadOnlyList<string> Subjects(ProgressTrack track) =>
        Awards.Where(a => a.Track == track).Select(a => a.SubjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Traits read from habits over the last 14 days, blended half and half with the Apex baseline when there is one:
    /// consistency is active days out of 14, curiosity is skills touched out of all ten, focus is the share of XP in
    /// the top skill.
    /// </summary>
    public IReadOnlyDictionary<string, int> Traits(string subjectId, DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.UtcNow).AddDays(-14);
        var recent = Awards.Where(a => a.SubjectId == subjectId && a.AcceptedAt >= since).ToList();
        int days = recent.Select(a => a.AcceptedAt.UtcDateTime.Date).Distinct().Count();
        int skills = recent.Select(a => a.Skill).Distinct().Count();
        double total = recent.Sum(a => a.VerifiedXp);
        double top = recent.GroupBy(a => a.Skill).Select(g => g.Sum(a => a.VerifiedXp)).DefaultIfEmpty(0).Max();
        var observed = new Dictionary<string, int>
        {
            ["consistency"] = (int)Math.Round(100.0 * days / 14),
            ["curiosity"] = (int)Math.Round(100.0 * skills / Enum.GetValues<Skill>().Length),
            ["focus"] = total <= 0 ? 0 : (int)Math.Round(100 * top / total)
        };
        if (subjectId != "user" || Apex is null) return observed;
        return observed.ToDictionary(kv => kv.Key,
            kv => Apex.Traits.TryGetValue(kv.Key, out int baseline) ? (kv.Value + baseline) / 2 : kv.Value);
    }

    /// <summary>Repeating the same skill on the same day earns less: full novelty first, down to the 0.25 floor.</summary>
    private double NoveltyFor(string subjectId, Skill skill)
    {
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        int already = Awards.Count(a => a.SubjectId == subjectId && a.Skill == skill && a.AcceptedAt.UtcDateTime.Date == today);
        return Math.Max(0.25, 1.0 - 0.15 * already);
    }

    private XpClaim UpdateClaim(string id, Func<XpClaim, XpClaim> change)
    {
        XpClaim updated;
        lock (_lock)
        {
            int i = _state.Claims.FindIndex(c => c.Id == id);
            if (i < 0) throw new InvalidOperationException($"no claim {id}");
            updated = change(_state.Claims[i]);
            _state.Claims[i] = updated;
        }
        Persist();
        return updated;
    }

    private void Persist()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_state, JsonOptions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory ledger; the next change retries the write.
        }
        Changed?.Invoke();
    }
}
