using System.Text.Json;
using System.Text.Json.Serialization;

namespace Holodex;

/// <summary>
/// What a mission is, in the user's words. The structure is the product; the names are a skin over plain work:
/// a campaign is a multi-step project, a side quest a chore, a daily a habit, a bounty one bounded fix posted by a
/// person or a system, a gauntlet or lesson skill training, a boss a deadline or launch, a raid a shared goal. The
/// first five are the types in the Arkhive Mission Log mockup; daily, boss and raid are proposed additions.
/// </summary>
internal enum HorizonQuestKind { Campaign, Bounty, Gauntlet, Side, Lesson, Daily, Boss, Raid }

/// <summary>
/// Ark's quest states, unchanged (@ark/shared-types progression.ts questStateSchema), so a quest means the same
/// thing in Holodex, in ArkStation and in Nexus. Completion is an evaluator gate, never a self-report.
/// </summary>
internal enum HorizonQuestState { Offered, Accepted, InProgress, Submitted, EvaluatorReview, Completed, Failed, Abandoned }

/// <summary>
/// Proof that something happened, from a connected source: a merged pull request, a calendar event that took
/// place, an agent log entry, a mission receipt. <see cref="Confidence"/> is the evidence confidence the XP
/// formula takes (0 to 1). <see cref="SelfReported"/> evidence is the user's own tick.
/// </summary>
internal sealed record HorizonEvidence(string SourceId, string Kind, string Ref, string Actor, DateTimeOffset At, double Confidence, bool SelfReported = false);

/// <summary>One mission. <see cref="Difficulty"/> is Ark's 1 to 5. <see cref="AssignedAgent"/> is a party member the quest was handed to.</summary>
internal sealed record HorizonQuest(
    string Id, string Title, HorizonQuestKind Kind, HorizonQuestState State, int Difficulty, string SourceId,
    DateTimeOffset? Due = null, string? AssignedAgent = null, IReadOnlyList<HorizonEvidence>? Evidence = null)
{
    public IReadOnlyList<HorizonEvidence> Proof => Evidence ?? Array.Empty<HorizonEvidence>();
}

/// <summary>
/// A place missions come from and are verified against: the user's connected data, read with their own access.
/// A source proposes quests (the user accepts or dismisses each one; nothing is added silently) and answers
/// whether a quest's work shows up in its data. A source never sends the user's data anywhere but this PC or,
/// in Ark mode, the owner's own Ark. See docs/HORIZON_MOD.md.
/// </summary>
internal interface IHorizonMissionSource
{
    /// <summary>The id its configuration uses ("github", "calendar", "ark-agent-log").</summary>
    string Id { get; }
    string Name { get; }

    /// <summary>Whether its connection is signed in and usable right now.</summary>
    bool Connected { get; }

    /// <summary>Quests this source would offer for a day, in the Offered state.</summary>
    Task<IReadOnlyList<HorizonQuest>> ProposeAsync(DateOnly day, CancellationToken ct = default);

    /// <summary>Evidence that the quest's work happened, or null when this source has none.</summary>
    Task<HorizonEvidence?> VerifyAsync(HorizonQuest quest, CancellationToken ct = default);
}

/// <summary>Ark's quest transitions, unchanged (progression.ts QUEST_TRANSITIONS).</summary>
internal static class HorizonQuests
{
    private static readonly Dictionary<HorizonQuestState, HorizonQuestState[]> Next = new()
    {
        [HorizonQuestState.Offered] = new[] { HorizonQuestState.Accepted, HorizonQuestState.Abandoned },
        [HorizonQuestState.Accepted] = new[] { HorizonQuestState.InProgress, HorizonQuestState.Abandoned },
        [HorizonQuestState.InProgress] = new[] { HorizonQuestState.Submitted, HorizonQuestState.Failed, HorizonQuestState.Abandoned },
        [HorizonQuestState.Submitted] = new[] { HorizonQuestState.EvaluatorReview },
        [HorizonQuestState.EvaluatorReview] = new[] { HorizonQuestState.Completed, HorizonQuestState.Failed },
        [HorizonQuestState.Failed] = new[] { HorizonQuestState.Offered },
        [HorizonQuestState.Completed] = Array.Empty<HorizonQuestState>(),
        [HorizonQuestState.Abandoned] = Array.Empty<HorizonQuestState>()
    };

    public static bool CanMove(HorizonQuestState from, HorizonQuestState to) => Next[from].Contains(to);

    /// <summary>The Today view never shows more than this many quests beside the dailies.</summary>
    public const int TodayLimit = 3;

    /// <summary>
    /// The quests for Today: in-progress first, then the nearest deadline, then the hardest, at most
    /// <see cref="TodayLimit"/>. Dailies are listed apart and do not count toward the limit.
    /// </summary>
    public static IReadOnlyList<HorizonQuest> Today(IEnumerable<HorizonQuest> quests) => quests
        .Where(q => q.Kind != HorizonQuestKind.Daily && q.State is HorizonQuestState.Accepted or HorizonQuestState.InProgress)
        .OrderBy(q => q.State == HorizonQuestState.InProgress ? 0 : 1)
        .ThenBy(q => q.Due ?? DateTimeOffset.MaxValue)
        .ThenByDescending(q => q.Difficulty)
        .Take(TodayLimit)
        .ToList();
}

/// <summary>
/// Ark's progression math, unchanged, so XP earned in Holodex is the same number Ark would mint
/// (@ark/shared-types progression.ts). Progression is verified competence and never authority: a level unlocks
/// what is visible and installable, and nothing here is read as a permission.
/// </summary>
internal static class HorizonXp
{
    /// <summary>The policy this math mirrors.</summary>
    public const string PolicyVersion = "HAXES-XP-1.0.0";

    /// <summary>The most confidence a self-reported completion carries when no source could verify it.</summary>
    public const double SelfReportConfidence = 0.25;

    /// <summary>Ark's quality weights: correctness 0.4, recovery 0.2, efficiency 0.15, collaboration 0.1, safety and provenance 0.15.</summary>
    public static double QualityScore(double correctness, double recovery, double efficiency, double collaboration, double safetyAndProvenance) =>
        Unit(correctness) * 0.4 + Unit(recovery) * 0.2 + Unit(efficiency) * 0.15 + Unit(collaboration) * 0.1 + Unit(safetyAndProvenance) * 0.15;

    /// <summary>verified_xp = 100 x difficulty (1 to 5) x quality (0 to 1) x evidence confidence (0 to 1) x novelty (0.25 to 1). At most 500.</summary>
    public static double VerifiedXp(int difficulty, double quality, double evidenceConfidence, double novelty) =>
        100.0 * Math.Clamp(difficulty, 1, 5) * Unit(quality) * Unit(evidenceConfidence) * Math.Clamp(novelty, 0.25, 1.0);

    /// <summary>Level 1 to 100: floor(sqrt(xp / 10)) + 1.</summary>
    public static int LevelFromXp(double xp) => Math.Min(100, (int)Math.Floor(Math.Sqrt(Math.Max(0, xp) / 10.0)) + 1);

    /// <summary>The XP a level starts at, for a progress bar.</summary>
    public static double XpForLevel(int level) => 10.0 * Math.Pow(Math.Clamp(level, 1, 100) - 1, 2);

    /// <summary>
    /// The confidence a quest's proof carries: the best connected evidence, or the self-report cap when the only
    /// proof is the user's own tick. No proof at all is zero, so nothing is minted.
    /// </summary>
    public static double EvidenceConfidence(IReadOnlyList<HorizonEvidence> proof)
    {
        if (proof.Count == 0) return 0;
        var verified = proof.Where(e => !e.SelfReported).ToList();
        return verified.Count > 0 ? Unit(verified.Max(e => e.Confidence)) : SelfReportConfidence;
    }

    /// <summary>Ark's loot rarity score and tiers (computeLootRarityTelemetry).</summary>
    public static double RarityScore(double novelty, double verificationDepth, double reusability, double effortIntensity, double evidenceConfidence) =>
        Math.Round(Unit(novelty) * 0.35 + Unit(verificationDepth) * 0.25 + Unit(reusability) * 0.2 + Unit(effortIntensity) * 0.1 + Unit(evidenceConfidence) * 0.1, 3);

    public static string RarityTier(double score) => score switch
    {
        >= 0.92 => "mythic",
        >= 0.82 => "legendary",
        >= 0.7 => "epic",
        >= 0.55 => "rare",
        >= 0.38 => "uncommon",
        _ => "common"
    };

    private static double Unit(double value) => Math.Clamp(value, 0, 1);
}

/// <summary>
/// One mission source's configuration: which provider it is, which connection it reads with, and what it may
/// propose and verify. It names a connection and never holds a key: sign-ins live in the Vault (Credential
/// Manager) or, in Ark mode, in the owner's Ark.
/// </summary>
internal sealed class HorizonSourceConfig
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>"ark" (through Hivemind), "public" (the user's own connection) or "both".</summary>
    public string Mode { get; set; } = "both";

    /// <summary>The Vault entry or Hivemind tool this source reads with, by name. Never a secret.</summary>
    public string Connection { get; set; } = "";

    /// <summary>What it offers as quests ("review-requested", "event-with-prep", "open-work-order").</summary>
    public List<string> Proposes { get; set; } = new();

    /// <summary>What it accepts as proof ("pull-request-merged", "event-ended", "agent-log-entry").</summary>
    public List<string> Verifies { get; set; } = new();

    /// <summary>Provider settings that are not secrets (a repository name, a calendar id).</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
}

/// <summary>
/// The ready-to-fill stubs every install starts with, and the per-user file that overrides them
/// (horizon\sources.json under the Holodex data folder). Every stub is off until the user turns it on, and a
/// source whose connection is missing stays off whatever the file says.
/// </summary>
internal static class HorizonSourceConfigs
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Words that never belong in a source's settings: a key there is a mistake, and the file is refused.</summary>
    private static readonly string[] SecretWords = { "secret", "token", "password", "apikey", "api_key", "key" };

    public static string DefaultPath => Path.Combine(HolodexNames.DataRoot, "horizon", "sources.json");

    /// <summary>The stubs: one per source Horizon knows how to plug in. All off.</summary>
    public static IReadOnlyList<HorizonSourceConfig> Stubs() => new[]
    {
        Stub("capture", "local", "One-tap capture", "both", "this-pc",
            new[] { "captured-note" }, new[] { "self-report" }),
        Stub("calendar", "google-calendar", "Google Calendar", "public", GoogleCalendarSource.TokenTarget,
            new[] { "event-with-prep", "deadline" }, new[] { "event-ended" }, ("calendarId", "primary")),
        Stub("github", "github", "GitHub", "public", "Holodex:Vault:github",
            new[] { "review-requested", "assigned-issue" }, new[] { "pull-request-merged", "issue-closed", "review-submitted" }, ("repositories", "")),
        Stub("ark-calendar", "ark-horizon", "Ark Horizon calendar", "ark", "hivemind:horizon_read",
            new[] { "event-with-prep", "deadline" }, new[] { "event-ended" }),
        Stub("ark-agent-log", "ark-agent-log", "Ark agent log", "ark", "hivemind:agent_log_read",
            Array.Empty<string>(), new[] { "agent-log-entry" }),
        Stub("ark-missions", "ark-agent-runtime", "Ark agent missions", "ark", "hivemind:agent_mission_status",
            new[] { "open-work-order" }, new[] { "mission-completed", "pull-request-opened" }),
        Stub("ark-quests", "ark-progression", "Ark quests", "ark", "hivemind:needs-ark-change",
            new[] { "offered-quest" }, new[] { "evaluator-accepted" })
    };

    private static HorizonSourceConfig Stub(string id, string provider, string name, string mode, string connection, string[] proposes, string[] verifies, params (string Key, string Value)[] settings) => new()
    {
        Id = id, Provider = provider, Name = name, Mode = mode, Connection = connection, Enabled = false,
        Proposes = proposes.ToList(), Verifies = verifies.ToList(),
        Settings = settings.ToDictionary(s => s.Key, s => s.Value)
    };

    /// <summary>
    /// The stubs with the user's file laid over them by id. A file that cannot be read, or that carries something
    /// that looks like a key, changes nothing: the stubs are returned as they are, all off.
    /// </summary>
    public static IReadOnlyList<HorizonSourceConfig> Load(string? path = null)
    {
        var stubs = Stubs().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        string file = path ?? DefaultPath;
        if (!File.Exists(file)) return stubs.Values.ToList();
        List<HorizonSourceConfig>? saved;
        try { saved = JsonSerializer.Deserialize<List<HorizonSourceConfig>>(File.ReadAllText(file), Json); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return stubs.Values.ToList(); }
        if (saved == null || saved.Any(HoldsSecret)) return stubs.Values.ToList();
        foreach (var config in saved.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
            stubs[config.Id] = config;
        return stubs.Values.ToList();
    }

    public static string ToJson(IEnumerable<HorizonSourceConfig> configs) => JsonSerializer.Serialize(configs, Json);

    internal static bool HoldsSecret(HorizonSourceConfig config) =>
        config.Settings.Keys.Any(k => SecretWords.Any(w => k.Replace("-", "").Contains(w.Replace("_", ""), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The sources a mode may use: enabled, and declared for that mode.</summary>
    public static IReadOnlyList<HorizonSourceConfig> For(HorizonMode mode, IEnumerable<HorizonSourceConfig> configs)
    {
        string wanted = mode == HorizonMode.Ark ? "ark" : "public";
        return configs.Where(c => c.Enabled && (c.Mode == "both" || c.Mode == wanted)).ToList();
    }
}
