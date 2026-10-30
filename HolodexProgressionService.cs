using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

/// <summary>Model calls for the progression agents, routed by kind: Nebius Nemotron unless the router picked another provider.</summary>
internal sealed class RoutedGalaModel : IGalaModel
{
    private readonly ArkStationClient _ark;
    public RoutedGalaModel(ArkStationClient ark) => _ark = ark;

    public async Task<(bool Ok, string Output, string Model)> AskAsync(TaskKind kind, string prompt, string? imageDataUrl = null, CancellationToken ct = default)
    {
        var route = _ark.Router.Route(kind);
        // AskNebiusTokenFactoryAsync keeps to Nemotron and moves an image to the VL model itself.
        string model = route.Available && route.ProviderId == ModelInventory.NebiusProviderId && ArkStationClient.IsNemotronModel(route.ModelId)
            ? route.ModelId
            : kind == TaskKind.Vision || imageDataUrl != null ? TaskRouter.VisionModel : TaskRouter.GalaModel;
        // Without a Nebius key on this PC, the shared ask path answers text through the user's Hivemind (or says to sign in).
        var answer = _ark.HasNebiusKey
            ? await _ark.AskNebiusTokenFactoryAsync(prompt, imageDataUrl, model)
            : await _ark.AskAsync(prompt, imageDataUrl, model);
        return (answer.Ok, answer.Output, string.IsNullOrEmpty(answer.Model) ? model : answer.Model);
    }
}

/// <summary>
/// Holds the progression pieces for the running app: the ledger, the quest board, the Watcher, the AInbox link and
/// Herald. It turns queued proof into claims the Watcher reviews, polls AInbox for quests and verdicts, and awards
/// small Activity XP for things Holodex sees.
/// </summary>
public sealed class HolodexProgressionService : IDisposable
{
    public static readonly string HabitsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "habits.txt");

    private readonly ArkStationClient _ark;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _poll;

    public ProgressionLedger Ledger { get; } = new();
    public QuestBoard Board { get; } = new();
    public IGalaModel Model { get; }
    public WatcherAgent Watcher { get; }
    public AInboxQuestLink Link { get; }
    public HeraldPublisher Herald { get; }
    public ApexEvaluator Apex { get; }

    public HolodexProgressionService(ArkStationClient ark, SynchronizationContext ui)
    {
        _ark = ark;
        _ui = ui;
        Model = new RoutedGalaModel(ark);
        Watcher = new WatcherAgent(Model, Ledger);
        Link = new AInboxQuestLink(Ledger, Board);
        Herald = new HeraldPublisher(ark.Connectors, ArkStationClient.TryReadWindowsCredential);
        Apex = new ApexEvaluator(Model);
        OutboundQueue.ProofQueued += h => _ = ReviewQueuedProofAsync(h);
        _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 8000, 60_000);
    }

    /// <summary>The habits Gala checks for in the recap, one per line in habits.txt (skate, draw, train...).</summary>
    public static IReadOnlyList<string> Habits()
    {
        try { return File.Exists(HabitsPath) ? File.ReadAllLines(HabitsPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    public DailyRecap Recap() => new(Model, Ledger, new IActivitySource[] { new LedgerActivitySource(Ledger), new DemoActivitySource() });

    /// <summary>Small XP for something Holodex saw happen; never throws into the caller.</summary>
    public void Activity(Skill skill, string what)
    {
        try { Ledger.AwardActivity(skill, what); } catch (Exception) { }
    }

    /// <summary>Starts a quest's proof from files the user picked: a claim, the AInbox handoff, then the Watcher.</summary>
    public async Task SubmitProofAsync(Quest quest, IReadOnlyList<string> files, string whatIDid)
    {
        var claim = Ledger.SubmitClaim(new XpClaim { Skill = quest.Skill, Description = $"{quest.Title}: {whatIDid}", Difficulty = quest.Difficulty });
        Board.Update(quest.Id, q => q with { State = QuestState.Submitted, ClaimId = claim.Id });
        string scrollId = await Link.SendProofAsync(claim, quest, files);
        Ledger.AttachEvidence(claim.Id, EvidenceKind.Upload, scrollId + "|" + string.Join("|", files));
        await ReviewAsync(claim.Id, quest, files);
    }

    /// <summary>Proof sent from the AInbox widget with the "Prove a quest" intent: same path as a quest's own proof.</summary>
    private async Task ReviewQueuedProofAsync(StackHandoff h)
    {
        string Field(string label) => h.Fields.FirstOrDefault(f => f.Label == label)?.Value ?? string.Empty;
        if (!Enum.TryParse(Field("Skill"), true, out Skill skill)) skill = Skill.Learning;
        var quest = Board.Open.FirstOrDefault(q => q.Title.Equals(Field("Quest"), StringComparison.OrdinalIgnoreCase));
        var files = h.Items.Where(i => i.FilePath != null && i.Kind != "scroll").Select(i => i.FilePath!).ToList();
        var claim = Ledger.SubmitClaim(new XpClaim
        {
            Skill = quest?.Skill ?? skill,
            Description = string.IsNullOrWhiteSpace(Field("What I did")) ? h.Ask : Field("What I did"),
            Difficulty = quest?.Difficulty ?? 1
        });
        if (quest != null) Board.Update(quest.Id, q => q with { State = QuestState.Submitted, ClaimId = claim.Id });
        Ledger.AttachEvidence(claim.Id, EvidenceKind.Upload, h.ScrollId + "|" + string.Join("|", files));
        await ReviewAsync(claim.Id, quest, files);
    }

    private async Task ReviewAsync(string claimId, Quest? quest, IReadOnlyList<string> files)
    {
        var images = files.Where(f => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(DataUrl).Where(u => u != null).Select(u => u!).ToList();
        try
        {
            var (claim, verdict) = await Watcher.ReviewClaimAsync(claimId, quest?.Rubric ?? "The evidence clearly shows the work described.", images);
            if (quest != null)
                Board.Update(quest.Id, q => q with
                {
                    State = claim.State == ClaimState.Accepted ? QuestState.Completed : claim.State == ClaimState.Rejected ? QuestState.Failed : QuestState.Submitted,
                    Feedback = verdict.Note
                });
            await Link.PostVerdictAsync(claim, verdict);
            var award = claim.AwardId == null ? null : Ledger.Awards.FirstOrDefault(a => a.Id == claim.AwardId);
            _ui.Post(_ => PhosphorToast.Show(
                claim.State == ClaimState.Accepted ? $"+{award?.VerifiedXp:0} {claim.Skill} XP" : "Watcher needs more",
                verdict.Note, "WATCHER // QUEST", 6000,
                claim.State == ClaimState.Accepted ? ToastTone.Success : ToastTone.Warning), null);
        }
        catch (Exception ex)
        {
            _ui.Post(_ => PhosphorToast.Show("Watcher could not review", ex.Message, "WATCHER // QUEST", 5000, ToastTone.Error), null);
        }
    }

    private async Task PollAsync()
    {
        var (offered, applied) = await Link.PollAsync();
        if (offered + applied == 0) return;
        _ui.Post(_ => PhosphorToast.Show(offered > 0 ? "New quest from the Guild" : "Watcher verdict in",
            offered > 0 ? $"{offered} new quest{(offered == 1 ? "" : "s")} on your Stats page." : $"{applied} claim{(applied == 1 ? "" : "s")} reviewed.",
            "AINBOX // QUESTS", 4200, ToastTone.Info), null);
    }

    public static string? DataUrl(string path)
    {
        try
        {
            string mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (IOException) { return null; }
    }

    public void Dispose() => _poll.Dispose();
}
