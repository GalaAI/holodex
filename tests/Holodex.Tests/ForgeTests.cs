using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>4GE in Holodex: the server host, the Library's origin groups, studio deep links and the Seed "later" drop.</summary>
public class ForgeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omniwand.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Omniwand.csproj not found above the test output");
    }

    private static string ContractSource(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "forge", "packages", "contracts", "src", file));

    [Fact]
    public void Library_origins_and_labels_match_the_4ge_contract()
    {
        string library = ContractSource("library.ts");
        var origins = Regex.Match(library, @"LIBRARY_ORIGINS\s*=\s*\[(?<list>[^\]]*)\]").Groups["list"].Value;
        Assert.Equal(ForgeHost.LibraryOrigins, Regex.Matches(origins, "\"([a-z]+)\"").Select(m => m.Groups[1].Value).ToArray());
        foreach (var (origin, label) in ForgeHost.LibraryLabels)
            Assert.Matches(new Regex($@"\b{origin}:\s*\{{\s*label:\s*""{Regex.Escape(label)}"""), library);
        Assert.Equal(new[] { "My projects", "Repos", "Mods", "Augments", "Adapters" }, ForgeHost.LibraryOrigins.Select(o => ForgeHost.LibraryLabels[o]));
    }

    [Fact]
    public void Library_reads_five_groups_in_order_even_when_the_server_leaves_some_out()
    {
        const string json = """
        {"groups":[
          {"origin":"mod","label":"Mods","canCreate":true,"items":[
            {"id":"mod:dev","origin":"mod","title":"Dev Mod","status":"Ready","tone":"ok","open":{"agent":"smith","kind":"mod","task":"Tell me about Dev Mod","subject":{"type":"mod","id":"mod:dev"}}}]},
          {"origin":"project","label":"My projects","canCreate":false,"items":[
            {"id":"project:bld_a1","origin":"project","title":"Ink Studio","subtitle":"Website","status":"Building","tone":"live","open":{"agent":"smith","kind":"build","task":"Continue","subject":{"type":"project","id":"bld_a1"}}},
            {"id":"project:broken","origin":"project","title":"No open"}]},
          {"origin":"bogus","label":"Nope","canCreate":false,"items":[]}],
         "total":2}
        """;
        var groups = ForgeHost.ParseLibrary(json);
        Assert.Equal(ForgeHost.LibraryOrigins, groups.Select(g => g.Origin).ToArray());
        Assert.Equal("Ink Studio", Assert.Single(groups[0].Items).Title);
        Assert.Empty(groups[1].Items);
        Assert.True(groups[2].CanCreate);
        Assert.Equal("Ready", groups[2].Items[0].Status);
        Assert.Equal("Adapters", groups[4].Label);
    }

    [Fact]
    public void A_library_click_opens_the_items_own_session_with_its_build()
    {
        using var open = JsonDocument.Parse("""{"agent":"smith","kind":"build","task":"Continue the build","subject":{"type":"project","id":"bld_a1","label":"Ink Studio"},"extra":1}""");
        using var body = JsonDocument.Parse(ForgeHost.CreateSessionBody(open.RootElement));
        var b = body.RootElement;
        Assert.Equal("smith", b.GetProperty("agent").GetString());
        Assert.Equal("build", b.GetProperty("kind").GetString());
        Assert.Equal("bld_a1", b.GetProperty("buildId").GetString());
        Assert.Equal("Ink Studio", b.GetProperty("subject").GetProperty("label").GetString());
        Assert.False(b.TryGetProperty("extra", out _));

        using var adapter = JsonDocument.Parse("""{"agent":"smith","kind":"library","task":"Connect Vercel","subject":{"type":"adapter","id":"vercel"}}""");
        Assert.DoesNotContain("buildId", ForgeHost.CreateSessionBody(adapter.RootElement));
    }

    [Theory]
    // The same cases as forge/packages/contracts/src/links.test.ts (studioPathForSession).
    [InlineData("""{"id":"ses_a1","kind":"mod","subject":{"type":"mod","id":"mod:dev"}}""", "/mods/mod%3Adev?session=ses_a1")]
    [InlineData("""{"id":"ses_b2","kind":"library","buildId":"bld_x9","subject":{"type":"project","id":"bld_x9"}}""", "/ide/bld_x9?session=ses_b2")]
    [InlineData("""{"id":"ses_c3","kind":"imagistation"}""", "/imagistation/ses_c3")]
    [InlineData("""{"id":"ses_d4","kind":"library","subject":{"type":"adapter","id":"vercel"}}""", "/library?session=ses_d4")]
    public void Studio_paths_match_the_contract(string session, string expected)
    {
        using var doc = JsonDocument.Parse(session);
        Assert.Equal(expected, ForgeHost.StudioPathFor(doc.RootElement));
        Assert.Contains("STUDIO_SESSION_PARAM = \"session\"", ContractSource("links.ts"));
    }

    [Fact]
    public void The_server_gets_only_the_keys_it_has_and_never_an_ark_port()
    {
        var env = ForgeHost.ServerEnvironment(ForgeHost.DefaultPort, "nb-key-123456", "https://hive.example/mcp", null);
        Assert.Equal(new[] { "NEBIUS_API_KEY" }, env.Keys.ToArray());
        env = ForgeHost.ServerEnvironment(17402, " ", "https://hive.example/mcp", "tok-abcdefgh");
        Assert.Equal("https://hive.example/mcp", env["HIVEMIND_URL"]);
        Assert.Equal("tok-abcdefgh", env["HIVEMIND_TOKEN"]);
        Assert.Equal("17402", env["FORGE_PORT"]);
        Assert.False(env.ContainsKey("NEBIUS_API_KEY"));
        foreach (int port in new[] { 5300, 8788, 4200, 15300, 18788, 14200 })
            Assert.Throws<ArgumentException>(() => new ForgeHost(new ArkStationClient(), port));
        Assert.Equal("key [redacted] and [redacted]", ForgeHost.Mask("key nb-key-123456 and tok-abcdefgh", new[] { "nb-key-123456", "tok-abcdefgh" }));
    }

    [Fact]
    public void The_forge_folder_and_studio_paths_are_found()
    {
        string root = RepoRoot();
        if (File.Exists(ForgeHost.ServerEntry(Path.Combine(root, "forge"))))
            Assert.Equal(Path.Combine(root, "forge"), ForgeHost.FindForgeRoot(Path.Combine(root, "bin", "Release")));
        Assert.Equal("/", ForgeStudioForm.NormalizePath(null));
        Assert.Equal("/imagistation", ForgeStudioForm.NormalizePath("imagistation"));
        Assert.Equal("/mods?x=1", ForgeStudioForm.NormalizePath(" /mods?x=1 "));
        Assert.Equal("/", ForgeStudioForm.NormalizePath("https://evil.example/"));
        Assert.Equal("/", ForgeStudioForm.NormalizePath("/../../etc"));
    }

    private const string SeedJson = """
    {"id":"seed_k3x9","status":"ready","decision":"later","idea":"A website for my tattoo shop","problem":"Bookings live in DMs",
     "audience":"Walk-ins and regulars","successSignal":"Ten online bookings a week","kind":"website","platforms":["web"],
     "features":[],"constraints":[],"references":[],"answers":[],"parameters":{},"blueprintId":"blueprint:website",
     "reminderNote":"Saturday morning works best.","createdAt":"2026-10-30T10:00:00.000Z","updatedAt":"2026-10-30T10:00:00.000Z"}
    """;

    [Fact]
    public void A_later_seed_becomes_a_sealed_ainbox_handoff_with_a_time_block_reminder()
    {
        var h = ForgeSeedInbox.FromSeed(SeedJson, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(h);
        Assert.Equal("scroll:4ge-seed:seed_k3x9", h!.ScrollId);
        Assert.Equal("forge", h.PodId);
        Assert.Equal(ForgeSeedInbox.IntentLabel, h.IntentLabel);
        Assert.Contains("Time-block", h.Ask);
        Assert.Contains("Saturday morning works best.", h.Ask);
        Assert.Equal(new[] { "Idea", "Problem", "Audience", "Success", "Kind", "Platforms", "Blueprint", "Reminder" }, h.Fields.Select(f => f.Label));

        // AInbox's own seal over the Seed's bytes, and AInbox's own scroll Markdown.
        string seedSha = ArkStationClient.Sha256Hex(Encoding.UTF8.GetBytes(SeedJson));
        Assert.Equal(seedSha, h.Items[1].Sha256);
        Assert.Equal(HandoffSeal.Seal("forge", ForgeSeedInbox.IntentId, h.Ask, h.Fields, new[] { seedSha }), h.Sha256);
        Assert.Equal("scroll", h.Items[0].Id);
        Assert.Contains("# Handoff scroll: Build later in 4GE", h.Items[0].Text);
        Assert.Contains($"sha256:{h.Sha256}", h.Items[0].Text);
        Assert.Contains("## Ask", h.Items[0].Text);

        // The queue reads it back as it reads every pending handoff (OutboundQueue.DrainAsync).
        var back = JsonSerializer.Deserialize<StackHandoff>(JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(h.ScrollId, back!.ScrollId);
        Assert.Equal(2, back.Items.Count);
        Assert.Equal(SeedJson, back.Items[1].Text);

        Assert.Contains("Time-block a session", ForgeSeedInbox.AdoptedText(h, @"C:\Docs\Holodex\Scrolls\x.md"));
        Assert.Contains("Documents\\Holodex\\Scrolls (x.md)", ForgeSeedInbox.AdoptedText(h, @"C:\Docs\Holodex\Scrolls\x.md"));
    }

    [Theory]
    [InlineData("""{"id":"bld_1","idea":"x"}""")]
    [InlineData("""{"id":"seed_1","idea":"  "}""")]
    [InlineData("""[1,2]""")]
    public void Anything_that_is_not_a_seed_is_left_alone(string json) => Assert.Null(ForgeSeedInbox.FromSeed(json, DateTime.UtcNow));

    [Fact]
    public async Task Dropped_seeds_are_delivered_once_and_moved_to_adopted()
    {
        string root = Path.Combine(Path.GetTempPath(), $"holodex-4ge-queue-{Guid.NewGuid():N}");
        try
        {
            var delivered = new List<StackHandoff>();
            using var inbox = new ForgeSeedInbox(h => { delivered.Add(h); return Task.FromResult("AInbox"); }, root);
            Directory.CreateDirectory(inbox.DropDir);
            await File.WriteAllTextAsync(Path.Combine(inbox.DropDir, "seed_k3x9.json"), SeedJson);
            await File.WriteAllTextAsync(Path.Combine(inbox.DropDir, "notes.json"), """{"hello":"world"}""");

            var first = await inbox.AdoptAllAsync();
            Assert.Equal("scroll:4ge-seed:seed_k3x9", Assert.Single(first).Handoff.ScrollId);
            Assert.True(File.Exists(Path.Combine(inbox.AdoptedDir, "seed_k3x9.json")));
            Assert.False(File.Exists(Path.Combine(inbox.DropDir, "seed_k3x9.json")));
            Assert.True(File.Exists(Path.Combine(inbox.DropDir, "notes.json")));

            Assert.Empty(await inbox.AdoptAllAsync());
            Assert.Single(delivered);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_seed_drop_folder_matches_the_contract()
    {
        string node = ContractSource("node.ts");
        Assert.Contains("AINBOX_SEED_DROP = \"4ge\"", node);
        Assert.Equal(Path.Combine(ForgeSeedInbox.DefaultQueueRoot, "4ge"), new ForgeSeedInbox(_ => Task.FromResult("")).DropDir);
        Assert.EndsWith(Path.Combine("Omniwand", "ainbox-queue"), ForgeSeedInbox.DefaultQueueRoot);
    }

    [Fact]
    public void Every_origin_has_an_empty_state_and_an_icon()
    {
        foreach (string origin in ForgeHost.LibraryOrigins)
        {
            Assert.False(string.IsNullOrWhiteSpace(HolodexLiteDrawerForm.ForgeEmptyText(origin)));
            _ = HolodexLiteDrawerForm.ForgeGlyph(origin);
        }
        Assert.Equal(PhosphorGlyph.Hammer, HolodexLiteDrawerForm.ForgeGlyph("project"));
        foreach (string text in ForgeHost.LibraryOrigins.Select(HolodexLiteDrawerForm.ForgeEmptyText).Append(ForgeSeedInbox.IntentLabel))
            Assert.DoesNotMatch("[\u2013\u2014]", text);
    }
}
