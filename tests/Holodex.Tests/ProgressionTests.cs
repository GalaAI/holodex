using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>Answers by task kind with fixed text and counts the calls.</summary>
internal sealed class FakeGala : IGalaModel
{
    public readonly Dictionary<TaskKind, string> Answers = new();
    public int Calls;
    public Task<(bool Ok, string Output, string Model)> AskAsync(TaskKind kind, string prompt, string? imageDataUrl = null, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Answers.TryGetValue(kind, out var a) ? (true, a, "fake/" + kind) : (false, "no answer", "fake"));
    }
}

internal sealed class Recorder : HttpMessageHandler
{
    public readonly List<(string Url, string Body)> Posts = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Posts.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

public class ProgressionTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"holodex-{Guid.NewGuid():N}.json");
    private const string Accept = "{\"accept\": true, \"correctness\": 0.9, \"recovery\": 0.8, \"efficiency\": 0.7, \"collaboration\": 0.6, \"safety\": 1, \"note\": \"Even waves; push the last row.\"}";

    [Fact]
    public void Math_matches_the_ark_formula()
    {
        // 0.4*0.9 + 0.2*0.8 + 0.15*0.7 + 0.1*0.6 + 0.15*1 = 0.835
        var q = new QualityComponents(0.9, 0.8, 0.7, 0.6, 1);
        Assert.Equal(0.835, ProgressionMath.QualityScore(q), 6);
        Assert.Equal(100 * 3 * 0.835 * 0.9 * 1, ProgressionMath.VerifiedXp(new XpAwardInput(3, q, 0.9, 1)), 6);
        Assert.Equal(1, ProgressionMath.LevelFromXp(9));
        Assert.Equal(2, ProgressionMath.LevelFromXp(10));
        Assert.Equal(11, ProgressionMath.LevelFromXp(1000));
        Assert.Equal(1000, ProgressionMath.XpForLevel(11));
        Assert.Equal("proficient", ProgressionMath.BandForLevel(60));
        Assert.Equal(10, ProgressionMath.NextCrucible(5));
        Assert.Equal(16, ProgressionMath.OverallLevel(new[] { 1, 50, 50 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgressionMath.VerifiedXp(new XpAwardInput(6, q, 1, 1)));
    }

    [Fact]
    public void Each_level_costs_more_than_the_last() =>
        Assert.True(Enumerable.Range(2, 98).All(l => ProgressionMath.XpForLevel(l + 1) - ProgressionMath.XpForLevel(l) > ProgressionMath.XpForLevel(l) - ProgressionMath.XpForLevel(l - 1)));

    [Fact]
    public void Self_reports_and_unknown_evaluators_mint_nothing()
    {
        var ledger = new ProgressionLedger(TempFile());
        var input = new XpAwardInput(1, QualityComponents.Neutral, 0.3, 1);
        Assert.Throws<InvalidOperationException>(() => ledger.Award("user", ProgressTrack.Human, Skill.Art, input, EvidenceKind.SelfReport, "", "watcher"));
        Assert.Throws<InvalidOperationException>(() => ledger.Award("user", ProgressTrack.Human, Skill.Art, input, EvidenceKind.Upload, "x", "random-bot"));
        var claim = ledger.SubmitClaim(new XpClaim { Skill = Skill.Art, Description = "drew a hannya" });
        var held = ledger.Review(claim.Id, true, QualityComponents.Neutral, "looks good");
        Assert.Equal(ClaimState.Pending, held.State);
        Assert.Empty(ledger.Awards);
    }

    [Fact]
    public void A_reviewed_claim_with_proof_levels_the_skill_and_survives_a_reload()
    {
        string path = TempFile();
        var ledger = new ProgressionLedger(path);
        var claim = ledger.SubmitClaim(new XpClaim { Skill = Skill.Craft, Description = "lined a rose", Difficulty = 4 });
        ledger.AttachEvidence(claim.Id, EvidenceKind.Upload, "scroll:proof:1|rose.jpg");
        var done = ledger.Review(claim.Id, true, new QualityComponents(1, 1, 1, 1, 1), "clean lines");
        Assert.Equal(ClaimState.Accepted, done.State);
        var reloaded = new ProgressionLedger(path);
        var st = reloaded.Standing("user", Skill.Craft);
        Assert.Equal(280, st.TotalXp, 6); // 100 * 4 * 1 * 0.7 (upload) * 1
        Assert.Equal(6, st.Level);
    }

    [Fact]
    public void Apex_baseline_sets_the_start_and_repeats_earn_less()
    {
        var ledger = new ProgressionLedger(TempFile());
        ledger.SetApexEvaluation(new ApexEvaluation { BaselineLevels = new Dictionary<Skill, int> { [Skill.Art] = 30 }, Traits = new Dictionary<string, int> { ["focus"] = 80 } });
        Assert.Equal(30, ledger.Standing("user", Skill.Art).Level);
        var first = ledger.AwardActivity(Skill.Code, "one");
        var second = ledger.AwardActivity(Skill.Code, "two");
        Assert.True(second.VerifiedXp < first.VerifiedXp);
        Assert.Equal((100 + 80) / 2, ledger.Traits("user")["focus"]);
    }

    [Fact]
    public async Task Watcher_accepts_proof_it_can_see_and_holds_proof_it_cannot()
    {
        var ledger = new ProgressionLedger(TempFile());
        var gala = new FakeGala();
        gala.Answers[TaskKind.Vision] = "A sketch page full of finger waves.";
        gala.Answers[TaskKind.Reason] = "Verdict:\n" + Accept;
        var watcher = new WatcherAgent(gala, ledger);
        var claim = ledger.SubmitClaim(new XpClaim { Skill = Skill.Art, Description = "finger waves", Difficulty = 2 });
        ledger.AttachEvidence(claim.Id, EvidenceKind.Upload, "page.jpg");
        var (after, verdict) = await watcher.ReviewClaimAsync(claim.Id, "fill the page", new[] { "data:image/png;base64,AA==" });
        Assert.True(verdict.Accept);
        Assert.Equal(ClaimState.Accepted, after.State);

        var blind = ledger.SubmitClaim(new XpClaim { Skill = Skill.Art, Description = "no picture" });
        ledger.AttachEvidence(blind.Id, EvidenceKind.Upload, "notes.txt");
        var (held, _) = await watcher.ReviewClaimAsync(blind.Id, "fill the page", Array.Empty<string>());
        Assert.Equal(ClaimState.Rejected, held.State);
    }

    [Fact]
    public void Verdict_parsing_tolerates_prose_and_refuses_garbage()
    {
        Assert.True(WatcherAgent.ParseVerdict("Sure! " + Accept + " Done.", "m").Accept);
        Assert.False(WatcherAgent.ParseVerdict("I think it is fine", "m").Accept);
    }

    [Fact]
    public async Task A_gauntlet_watches_frames_and_mints_trial_xp()
    {
        var ledger = new ProgressionLedger(TempFile());
        var gala = new FakeGala();
        gala.Answers[TaskKind.Vision] = "Half the page has waves; spacing is even.";
        gala.Answers[TaskKind.Reason] = Accept;
        var quest = new Quest { Kind = QuestKind.Gauntlet, Skill = Skill.Art, Difficulty = 3, TimeLimitSeconds = 10, Title = "Finger waves", Rubric = "fill the page" };
        var session = new GauntletSession(quest, gala, new WatcherAgent(gala, ledger), ledger, () => "data:image/png;base64,AA==") { Interval = TimeSpan.FromSeconds(4) };
        var (verdict, award) = await session.RunAsync();
        Assert.True(verdict.Accept);
        Assert.NotNull(award);
        Assert.Equal(EvidenceKind.Trial, award!.Evidence);
        Assert.True(session.Observations.Count >= 2);
    }

    [Fact]
    public async Task Recap_pays_connector_events_once()
    {
        string demo = TempFile();
        File.WriteAllText(demo, "[{\"at\":\"2026-10-29T14:00:00-07:00\",\"source\":\"google-photos\",\"skill\":\"craft\",\"summary\":\"Drew a hannya\"}]");
        var ledger = new ProgressionLedger(TempFile());
        var gala = new FakeGala();
        gala.Answers[TaskKind.Reason] = "You drew a hannya on the demo account. No skate today; what happened?";
        var recap = new DailyRecap(gala, ledger, new IActivitySource[] { new DemoActivitySource(demo) });
        var (ok, text, events) = await recap.ComposeAsync("Justin", new[] { "skate" });
        Assert.True(ok);
        Assert.Single(events);
        Assert.StartsWith("demo:", events[0].Source);
        await recap.ComposeAsync("Justin", new[] { "skate" });
        Assert.Single(ledger.Awards);
        Assert.Equal(EvidenceKind.Connector, ledger.Awards[0].Evidence);
    }

    [Fact]
    public void AInbox_items_offer_quests_and_apply_remote_verdicts_once()
    {
        var ledger = new ProgressionLedger(TempFile());
        var board = new QuestBoard(TempFile());
        var link = new AInboxQuestLink(ledger, board, new HttpClient(new Recorder()), "http://ainbox.test");
        var claim = ledger.SubmitClaim(new XpClaim { Skill = Skill.Fitness, Description = "skated" });
        ledger.AttachEvidence(claim.Id, EvidenceKind.Upload, "skate.jpg");
        var items = JsonNode.Parse($$$"""
        [
          {"id":"a1","channel":"holodex-quest","actor":"agent:tutor","summary":"Draw ten roses","metadata":{"skill":"art","kind":"mission","title":"Ten roses","rubric":"ten roses","difficulty":2}},
          {"id":"a2","channel":"holodex-watcher","metadata":{"claimId":"{{{claim.Id}}}","accept":true,"correctness":1,"note":"seen on the skate park cam","evaluator":"watcher:hivemind"}},
          {"id":"a3","channel":"holodex-watcher","metadata":{"claimId":"{{{claim.Id}}}","accept":true,"evaluator":"watcher:holodex"}},
          {"id":"a4","channel":"scout-creative-research","metadata":{}}
        ]
        """)!.AsArray();
        var (offered, applied) = link.Apply(items);
        Assert.Equal(1, offered);
        Assert.Equal(1, applied);
        Assert.Contains(board.Open, q => q.Id == "ainbox:a1" && q.Source == "agent:tutor");
        Assert.Equal(ClaimState.Accepted, ledger.Claims.Single(c => c.Id == claim.Id).State);
        Assert.Equal((0, 0), link.Apply(items));
    }

    [Fact]
    public async Task Verdicts_post_back_to_ainbox_as_a_capture()
    {
        var rec = new Recorder();
        var ledger = new ProgressionLedger(TempFile());
        var link = new AInboxQuestLink(ledger, new QuestBoard(TempFile()), new HttpClient(rec), "http://ainbox.test");
        var claim = ledger.SubmitClaim(new XpClaim { Skill = Skill.Art, Description = "waves" });
        Assert.True(await link.PostVerdictAsync(claim, new WatcherVerdict(true, QualityComponents.Neutral, "nice", "m")));
        Assert.Equal("http://ainbox.test/captures/file", rec.Posts.Single().Url);
    }

    [Fact]
    public async Task Herald_posts_to_a_saved_discord_webhook_only()
    {
        var rec = new Recorder();
        var connectors = new RealmConnectors(RealmConnectors.Defaults, _ => null);
        var none = new HeraldPublisher(connectors, _ => null, new HttpClient(rec));
        Assert.Empty(none.Destinations());
        var (r0, _) = await none.PublishAsync(new ArkHubShare("t", "b", Array.Empty<SkillStanding>(), 1));
        Assert.False(r0.Ok);

        string hook = "https://discord.com/api/webhooks/1/abc";
        var herald = new HeraldPublisher(connectors, t => t == HeraldPublisher.DiscordCredential ? hook : null, new HttpClient(rec));
        var standings = new ProgressionLedger(TempFile()).Standings("user");
        var (result, posted) = await herald.PublishAsync(new ArkHubShare("Level 5", "Shared from Holodex", standings, 5));
        Assert.True(result.Ok);
        Assert.Equal(new[] { "Discord" }, posted);
        Assert.Equal(hook, rec.Posts.Last().Url);
        Assert.Contains("\"embeds\"", rec.Posts.Last().Body);
        Assert.Equal("herald", result.AgentId);
    }

    [Fact]
    public async Task Apex_evaluator_reads_levels_and_falls_back_to_ratings()
    {
        var gala = new FakeGala();
        gala.Answers[TaskKind.Reason] = "{\"levels\": {\"Art\": 35, \"Craft\": 99}, \"traits\": {\"focus\": 70, \"curiosity\": 90}, \"summary\": \"Strong in art.\"}";
        var eval = await new ApexEvaluator(gala).EvaluateAsync(new ApexEvaluator.Answers(
            new Dictionary<Skill, int> { [Skill.Code] = 5 }, new Dictionary<Skill, string>(), "tattoo better"));
        Assert.Equal(35, eval.BaselineLevels[Skill.Art]);
        Assert.Equal(60, eval.BaselineLevels[Skill.Craft]);
        Assert.Equal(21, eval.BaselineLevels[Skill.Code]);
        Assert.Equal(90, eval.Traits["curiosity"]);
    }
}
