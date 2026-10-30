using Omniwand;
using Xunit;
using Xunit.Abstractions;

namespace Holodex.Tests;

/// <summary>Against the real providers on this machine; runs only with HOLODEX_LIVE=1 (it needs the Nebius key).</summary>
public class LiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_inventory_lists_nebius_and_routes_gala_to_super()
    {
        if (Environment.GetEnvironmentVariable("HOLODEX_LIVE") != "1") return;
        var ark = new ArkStationClient();
        var added = await ark.Models.Refresh();
        output.WriteLine($"models {ark.Models.Models.Count}, new {added.Count}");
        foreach (var g in ark.Models.Models.GroupBy(m => m.ProviderId)) output.WriteLine($"{g.Key}: {g.Count()}");
        foreach (TaskKind k in Enum.GetValues<TaskKind>())
        {
            var r = ark.Router.Route(k);
            output.WriteLine($"{k} -> {r.ProviderId} {r.ModelId} ({r.Reason})");
        }
        Assert.Equal(TaskRouter.GalaModel, ark.Router.Route(TaskKind.Reason).ModelId);
        Assert.Contains(ark.Models.Models, m => m.ProviderId == "nebius" && m.ModelId == TaskRouter.VisionModel);
    }
}
