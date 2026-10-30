using System.Text.Json.Nodes;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The stdio bridge against a real MCP server from this PC's own client config (HOLODEX_LIVE_MCP=&lt;server name&gt;,
/// for example elgato). The approval is answered yes by the test; only read-only calls are made.
/// </summary>
public class McpBridgeLiveTests
{
    [Fact]
    public async Task A_configured_stdio_server_bridges_its_tools_into_nemotrons_loop()
    {
        string? name = Environment.GetEnvironmentVariable("HOLODEX_LIVE_MCP");
        if (string.IsNullOrEmpty(name)) return;
        var finding = McpClientConfigs.Discover().First(f => f.Name == name && f.Server.IsStdio);
        var saved = HolodexTools.Approve;
        string? approved = null;
        string vault = HolodexVault.ManifestPath;
        HolodexVault.ManifestPath = Path.Combine(Path.GetTempPath(), $"vault-{Guid.NewGuid():N}.json");
        try
        {
            HolodexTools.Approve = (command, _) => { approved = command; return Task.FromResult(true); };
            string said = await McpBridges.ConnectAsync(finding.Server);
            Assert.Equal(McpClientConfigs.CommandLine(finding.Server.Command, finding.Server.Args), approved);
            Assert.True(McpBridges.IsRunning(McpBridges.IdFor(finding.Server)), said);
            Assert.True(HolodexTools.Any);

            var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "x" }) };
            HolodexTools.Offer(payload);
            var tools = payload["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).Where(n => n.StartsWith("mcp_")).ToList();
            Assert.NotEmpty(tools);
            string report = $"{said}\n{string.Join("\n", tools)}";
            string? status = tools.FirstOrDefault(t => t.EndsWith("bridge_status"));
            if (status != null) report += "\n" + await HolodexTools.RunAsync(status, new JsonObject());
            string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
            if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, $"mcp-bridge-{name}.txt"), report);
            Assert.Equal("works", HolodexVault.Load().Single(e => e.Id == McpBridges.IdFor(finding.Server)).Status);
        }
        finally
        {
            HolodexTools.Approve = saved;
            File.Delete(HolodexVault.ManifestPath);
            HolodexVault.ManifestPath = vault;
        }
    }
}