using System.Net;
using System.Net.Http;
using System.Text;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>Answers each URL with a fixed model list; any other URL is unreachable.</summary>
internal sealed class FakeModels : HttpMessageHandler
{
    public readonly Dictionary<string, string[]> Lists = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        if (url.EndsWith("/health")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        if (!Lists.TryGetValue(url, out var ids)) throw new HttpRequestException("unreachable");
        string json = "{\"data\":[" + string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\"}}")) + "]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

public class RouterTests
{
    private static (ModelInventory Inventory, FakeModels Fake) Make()
    {
        var fake = new FakeModels();
        return (new ModelInventory(ModelInventory.Defaults, _ => null, new HttpClient(fake)), fake);
    }

    [Fact]
    public void Defaults_route_language_work_to_nemotron_on_nebius()
    {
        var router = new TaskRouter(Make().Inventory);
        Assert.Equal(TaskRouter.GalaModel, router.Route(TaskKind.Reason).ModelId);
        Assert.Equal(TaskRouter.FastModel, router.Route(TaskKind.Chat).ModelId);
        Assert.Equal(TaskRouter.VisionModel, router.Route(TaskKind.Vision).ModelId);
        Assert.All(new[] { TaskKind.Reason, TaskKind.Chat, TaskKind.Vision }, k => Assert.Equal("nebius", router.Route(k).ProviderId));
    }

    [Fact]
    public void Without_a_detector_detect_falls_back_to_vision_and_segment_is_unavailable()
    {
        var router = new TaskRouter(Make().Inventory);
        Assert.Equal(TaskRouter.VisionModel, router.Route(TaskKind.Detect).ModelId);
        Assert.False(router.Route(TaskKind.Segment).Available);
    }

    [Fact]
    public async Task A_new_local_vision_server_is_announced_once_and_takes_detect_and_segment()
    {
        var (inventory, fake) = Make();
        fake.Lists["http://127.0.0.1:8856/v1/models"] = new[] { "yolo11n", "sam2.1-hiera-small" };

        var added = await inventory.Refresh();
        Assert.Equal(2, added.Count);
        Assert.Empty(await inventory.Refresh());

        var router = new TaskRouter(inventory);
        Assert.Equal("yolo11n", router.Route(TaskKind.Detect).ModelId);
        Assert.Equal("local-vision", router.Route(TaskKind.Segment).ProviderId);
        // Language work does not move off Nebius because a local server appeared.
        Assert.Equal(TaskRouter.GalaModel, router.Route(TaskKind.Reason).ModelId);
    }

    [Fact]
    public async Task An_unreachable_provider_keeps_its_models()
    {
        var (inventory, fake) = Make();
        fake.Lists["http://127.0.0.1:1234/v1/models"] = new[] { "qwen2.5-coder-7b" };
        await inventory.Refresh();
        fake.Lists.Clear();
        await inventory.Refresh();
        Assert.Contains(inventory.Models, m => m.ModelId == "qwen2.5-coder-7b");
    }

    [Theory]
    [InlineData("yolo11n", ModelCapability.Detect)]
    [InlineData("facebook/sam2-hiera-large", ModelCapability.Segment)]
    [InlineData("nvidia/Nemotron-3.5-Super-VL", ModelCapability.Vision)]
    [InlineData("qwen2.5-coder-7b", ModelCapability.Code)]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", ModelCapability.Reason)]
    public void Capabilities_are_read_from_the_model_id(string id, ModelCapability expected) =>
        Assert.True(ModelInventory.InferCapabilities(id).HasFlag(expected));

    [Fact]
    public void Samantha_is_not_a_segmenter() =>
        Assert.False(ModelInventory.InferCapabilities("samantha-7b").HasFlag(ModelCapability.Segment));
}

public class HandOffTests
{
    [Fact]
    public void Kinds_go_to_their_specialist_and_unclaimed_work_goes_to_gala()
    {
        Assert.Equal("wiz", HolodexAgents.ResolveFor(new HolodexTask { Kind = TaskKind.Craft }).Id);
        Assert.Equal("herald", HolodexAgents.ResolveFor(new HolodexTask { Kind = TaskKind.Publish }).Id);
        Assert.Equal("tutor", HolodexAgents.ResolveFor(new HolodexTask { Kind = TaskKind.Teach }).Id);
        Assert.Equal("gala", HolodexAgents.ResolveFor(new HolodexTask { Kind = TaskKind.Reason }).Id);
        Assert.Equal("herald", HolodexAgents.ResolveFor(new HolodexTask { Kind = TaskKind.Chat, To = "herald" }).Id);
    }

    [Fact]
    public void A_hand_off_keeps_its_parent_skill_and_sender()
    {
        var ask = new HolodexTask { Kind = TaskKind.Search, To = "oracle", Intent = "what is SAM 2", Skill = "vision" };
        var next = ask.HandOff("herald", TaskKind.Publish, "write a newsletter about it");
        Assert.Equal(ask.Id, next.ParentId);
        Assert.Equal("oracle", next.From);
        Assert.Equal("vision", next.Skill);
        Assert.NotEqual(ask.Id, next.Id);
    }
}

public class ConnectorTests
{
    [Fact]
    public void Defaults_hold_hivemind_and_a_ready_but_unwired_ryozan()
    {
        var all = RealmConnectors.Load(Path.Combine(Path.GetTempPath(), "no-such-connectors.json"));
        Assert.True(all.First(c => c.Id == "hivemind").Enabled);
        Assert.False(all.First(c => c.Id == "ryozan").Enabled);
    }

    [Fact]
    public void A_saved_file_replaces_a_default_and_adds_new_services()
    {
        string path = Path.Combine(Path.GetTempPath(), $"connectors-{Guid.NewGuid():N}.json");
        try
        {
            RealmConnectors.Save(new[]
            {
                RealmConnectors.RyozanDefault with { Enabled = true, BaseUrl = "http://localhost:9000" },
                new RealmConnector { Id = "studio", Label = "Studio", BaseUrl = "http://localhost:7000", Enabled = true, Accepts = new[] { TaskKind.Craft } }
            }, path);
            var loaded = RealmConnectors.Load(path);
            var connectors = new RealmConnectors(loaded, _ => null);
            Assert.Equal("http://localhost:9000", connectors.Find("ryozan")!.ResolvedBaseUrl);
            Assert.Equal("studio", connectors.RouteFor(TaskKind.Craft)!.Id);
            Assert.Equal("ryozan", connectors.RouteFor(TaskKind.Publish)!.Id);
            Assert.NotNull(connectors.Find("hivemind"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Health_reports_reachability_and_whether_a_key_is_saved()
    {
        var connectors = new RealmConnectors(RealmConnectors.Defaults, t => t == "Omniwand:RyozanToken" ? "k" : null, new HttpClient(new FakeModels()));
        var health = await connectors.CheckHealthAsync(connectors.Find("ryozan")!);
        Assert.True(health.Reachable);
        Assert.True(health.KeyPresent);
    }
}
