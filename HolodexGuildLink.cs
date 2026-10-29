using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

/// <summary>
/// Quests and proof over AInbox, both ways.
/// <list type="bullet">
/// <item>Out: proof for a claim goes through the shared <see cref="OutboundQueue"/> as a handoff to the Nexus pod
/// (channel <c>holodex-proof</c>), so it waits on disk and retries when AInbox is down. Watcher verdicts are posted
/// back as a text capture on <c>holodex-watcher</c>, so the record lives in AInbox too.</item>
/// <item>In: <see cref="PollAsync"/> reads <c>GET /ainbox/items</c>. Items on <c>holodex-quest</c> become offered
/// quests (any agent or the Guild can assign one), and items on <c>holodex-watcher</c> from another Watcher apply
/// their verdict to the pending claim they name.</item>
/// </list>
/// </summary>
public sealed class AInboxQuestLink
{
    public const string ProofChannel = "holodex-proof";
    public const string QuestChannel = "holodex-quest";
    public const string VerdictChannel = "holodex-watcher";

    private readonly ProgressionLedger _ledger;
    private readonly QuestBoard _board;
    private readonly HttpClient _http;
    private readonly string _serviceUrl;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public AInboxQuestLink(ProgressionLedger ledger, QuestBoard board, HttpClient? http = null, string? serviceUrl = null)
    {
        _ledger = ledger;
        _board = board;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _serviceUrl = (serviceUrl ?? Environment.GetEnvironmentVariable("ARK_AINBOX_URL") ?? OutboundQueue.ServiceUrl).TrimEnd('/');
    }

    /// <summary>Queues the proof files for a claim and records where they went. Returns the scroll id.</summary>
    internal async Task<string> SendProofAsync(XpClaim claim, Quest? quest, IReadOnlyList<string> files)
    {
        string scrollId = $"scroll:proof:{claim.Id}";
        var fields = new List<ScrollField>
        {
            new("Quest", quest?.Title ?? "(self-reported)", "Holodex"),
            new("Skill", claim.Skill.ToString(), "Holodex"),
            new("What I did", claim.Description, "Operator"),
            new("Rubric", quest?.Rubric ?? "Show the work clearly.", "Holodex"),
            new("Claim", claim.Id, "Holodex")
        };
        var items = new List<QueuedItem>();
        foreach (string path in files.Where(File.Exists))
        {
            string sha = ArkStationClient.Sha256File(path) ?? string.Empty;
            items.Add(new QueuedItem(Guid.NewGuid().ToString("N")[..12], Path.GetFileName(path), "file", ProofChannel, MediaTypeFor(path),
                path, null, null, sha, new Dictionary<string, string> { ["claimId"] = claim.Id, ["questId"] = quest?.Id ?? "" }));
        }
        string seal = HandoffSeal.Seal("nexus", "proof", claim.Description, fields, items.Select(i => i.Sha256 ?? i.Name));
        var handoff = new StackHandoff(scrollId, "P", "Quest proof", "nexus", "proof", "Quest proof", claim.Description, fields, items, seal, DateTime.UtcNow.ToString("O"));
        await OutboundQueue.Shared.EnqueueAsync(handoff);
        return scrollId;
    }

    /// <summary>Posts the Watcher's verdict to AInbox so the decision has a record outside this PC.</summary>
    public async Task<bool> PostVerdictAsync(XpClaim claim, WatcherVerdict verdict, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["sourceId"] = "omniwand",
            ["kind"] = "desktop-hook",
            ["channel"] = VerdictChannel,
            ["actor"] = "agent:watcher",
            ["summary"] = $"Watcher {(verdict.Accept ? "accepted" : "held")} {claim.Skill} claim: {verdict.Note}",
            ["idempotencyKey"] = $"omniwand:verdict:{claim.Id}:{claim.State}",
            ["metadata"] = new JsonObject
            {
                ["claimId"] = claim.Id,
                ["skill"] = claim.Skill.ToString(),
                ["accept"] = verdict.Accept,
                ["awardId"] = claim.AwardId,
                ["model"] = verdict.Model,
                ["evaluator"] = "watcher:holodex"
            }
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serviceUrl}/captures/file")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(verdict.Note))
            };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            req.Headers.Add("x-ainbox-capture", Uri.EscapeDataString(payload.ToJsonString()));
            req.Headers.Add("x-ainbox-file-name", Uri.EscapeDataString($"verdict-{claim.Id}.md"));
            req.Headers.Add("x-ainbox-media-type", "text/markdown");
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads AInbox for quests offered to Holodex and verdicts from other Watchers. Returns what changed.</summary>
    public async Task<(int QuestsOffered, int VerdictsApplied)> PollAsync(CancellationToken ct = default)
    {
        JsonArray? items;
        try
        {
            using var resp = await _http.GetAsync($"{_serviceUrl}/ainbox/items", ct);
            if (!resp.IsSuccessStatusCode) return (0, 0);
            items = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["items"]?.AsArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (0, 0);
        }
        return Apply(items ?? new JsonArray());
    }

    /// <summary>Applies a page of AInbox items (split from <see cref="PollAsync"/> so it can be tested without a server).</summary>
    public (int QuestsOffered, int VerdictsApplied) Apply(JsonArray items)
    {
        int offered = 0, applied = 0;
        foreach (var node in items)
        {
            if (node is not JsonObject item) continue;
            string id = item["id"]?.GetValue<string>() ?? string.Empty;
            string channel = item["channel"]?.GetValue<string>() ?? string.Empty;
            if (id.Length == 0 || !_seen.Add(id)) continue;
            var meta = item["metadata"] as JsonObject;
            if (channel == QuestChannel && meta is not null && QuestFrom(id, item, meta) is { } quest && _board.Offer(quest))
                offered++;
            else if (channel == VerdictChannel && meta is not null && ApplyRemoteVerdict(meta))
                applied++;
        }
        return (offered, applied);
    }

    private static Quest? QuestFrom(string itemId, JsonObject item, JsonObject meta)
    {
        if (!Enum.TryParse(meta["skill"]?.GetValue<string>(), true, out Skill skill)) return null;
        Enum.TryParse(meta["kind"]?.GetValue<string>(), true, out QuestKind kind);
        return new Quest
        {
            Id = "ainbox:" + itemId,
            Kind = kind,
            Skill = skill,
            Title = meta["title"]?.GetValue<string>() ?? item["summary"]?.GetValue<string>() ?? "Quest",
            Briefing = meta["briefing"]?.GetValue<string>() ?? item["summary"]?.GetValue<string>() ?? string.Empty,
            Rubric = meta["rubric"]?.GetValue<string>() ?? string.Empty,
            Difficulty = Math.Clamp(meta["difficulty"]?.GetValue<int>() ?? 1, 1, 5),
            TimeLimitSeconds = meta["timeLimitSeconds"]?.GetValue<int>() ?? 0,
            Proof = kind == QuestKind.Gauntlet ? EvidenceKind.Trial : EvidenceKind.Upload,
            Source = item["actor"]?.GetValue<string>() ?? "ainbox"
        };
    }

    /// <summary>A verdict from another Watcher (not this Holodex) on a claim that is still pending here.</summary>
    private bool ApplyRemoteVerdict(JsonObject meta)
    {
        if (meta["evaluator"]?.GetValue<string>() == "watcher:holodex") return false;
        string claimId = meta["claimId"]?.GetValue<string>() ?? string.Empty;
        var claim = _ledger.Claims.FirstOrDefault(c => c.Id == claimId);
        if (claim is null || claim.State != ClaimState.Pending) return false;
        double Part(string k) => Math.Clamp(meta[k]?.GetValue<double>() ?? 0.6, 0, 1);
        var quality = new QualityComponents(Part("correctness"), Part("recovery"), Part("efficiency"), Part("collaboration"), Part("safety"));
        var after = _ledger.Review(claimId, meta["accept"]?.GetValue<bool>() ?? false, quality, meta["note"]?.GetValue<string>() ?? string.Empty);
        return after.State != ClaimState.Pending;
    }

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream"
    };
}

/// <summary>What a share to ArkHub carries: a title, a line of text, and the levels behind it.</summary>
public sealed record ArkHubShare(string Title, string Body, IReadOnlyList<SkillStanding> Standings, int OverallLevel);

/// <summary>
/// Shares progress to ArkHub, the Guild's channel, by handing a Publish task to Herald. Herald posts to each
/// enabled destination: a Discord webhook (the URL is the secret in Credential Manager <c>Omniwand:DiscordWebhook</c>),
/// and Hivemind's ArkHub tab and Social Mod once their connector slots are wired. Skool has no public posting API,
/// so its slot stays off. Nothing is posted unless the user presses Share.
/// </summary>
public sealed class HeraldPublisher
{
    public const string DiscordCredential = "Omniwand:DiscordWebhook";

    private readonly RealmConnectors _connectors;
    private readonly Func<string, string?> _readSecret;
    private readonly HttpClient _http;

    public HeraldPublisher(RealmConnectors connectors, Func<string, string?> readSecret, HttpClient? http = null)
    {
        _connectors = connectors;
        _readSecret = readSecret;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static HolodexTask TaskFor(ArkHubShare share) => new()
    {
        Kind = TaskKind.Publish,
        From = "user",
        To = HolodexAgents.Herald.Id,
        Intent = "Share progress to ArkHub",
        Inputs = new Dictionary<string, string> { ["title"] = share.Title, ["body"] = share.Body, ["overall"] = share.OverallLevel.ToString() }
    };

    /// <summary>The destinations a share would reach right now, for the confirm line.</summary>
    public IReadOnlyList<string> Destinations()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(_readSecret(DiscordCredential))) list.Add("Discord");
        foreach (var id in new[] { "arkhub", "social-mod" })
            if (_connectors.Find(id) is { Enabled: true } c) list.Add(c.Label);
        return list;
    }

    public async Task<(HolodexTaskResult Result, IReadOnlyList<string> Posted)> PublishAsync(ArkHubShare share, CancellationToken ct = default)
    {
        var task = TaskFor(share);
        var posted = new List<string>();
        var errors = new List<string>();

        string? webhook = _readSecret(DiscordCredential);
        if (!string.IsNullOrWhiteSpace(webhook))
        {
            if (await PostDiscordAsync(webhook.Trim(), share, ct) is { } error) errors.Add("Discord: " + error);
            else posted.Add("Discord");
        }
        foreach (var id in new[] { "arkhub", "social-mod" })
        {
            if (_connectors.Find(id) is not { Enabled: true } c) continue;
            try
            {
                using var req = _connectors.Authorize(c, new HttpRequestMessage(HttpMethod.Post, c.ResolvedBaseUrl + "/share")
                {
                    Content = new StringContent(JsonSerializer.Serialize(task), Encoding.UTF8, "application/json")
                });
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode) posted.Add(c.Label);
                else errors.Add($"{c.Label}: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add($"{c.Label}: unreachable");
            }
        }

        bool ok = posted.Count > 0;
        string output = ok ? "Posted to " + string.Join(", ", posted) : errors.Count > 0 ? string.Join("; ", errors) : "No ArkHub destination is set up yet.";
        return (new HolodexTaskResult(task.Id, ok, HolodexAgents.Herald.Id, string.Join(",", posted), string.Empty, output, DateTimeOffset.UtcNow), posted);
    }

    /// <summary>Discord's webhook execute call: POST the webhook URL with content and one embed. Returns an error or null.</summary>
    private async Task<string?> PostDiscordAsync(string webhookUrl, ArkHubShare share, CancellationToken ct)
    {
        if (!webhookUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
            !webhookUrl.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            return "the saved webhook is not a Discord webhook URL";
        var top = share.Standings.OrderByDescending(s => s.Level).Take(5)
            .Select(s => new JsonObject { ["name"] = s.Skill.ToString(), ["value"] = $"Level {s.Level}", ["inline"] = true });
        var body = new JsonObject
        {
            ["username"] = "Herald",
            ["content"] = share.Body,
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = share.Title,
                ["description"] = $"Overall level {share.OverallLevel}",
                ["fields"] = new JsonArray(top.ToArray<JsonNode>())
            })
        };
        try
        {
            using var resp = await _http.PostAsync(webhookUrl, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "unreachable";
        }
    }
}
