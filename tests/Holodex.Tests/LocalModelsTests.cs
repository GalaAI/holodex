using System.Net;
using System.Text;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// v0.4.2 connected items: LM Studio and Ollama chat models reach the ask picker as local models that answer through
/// their own OpenAI-compatible endpoint, and an MCP server's row says whether answers use its tools.
/// </summary>
public class LocalModelsTests
{
    [Fact]
    public void Local_ids_round_trip_and_never_look_like_cloud_models()
    {
        string id = ArkStationClient.LocalModelId("lmstudio", "qwen/qwen3-8b");
        Assert.Equal("local:lmstudio:qwen/qwen3-8b", id);
        Assert.True(ArkStationClient.TryParseLocalModel(id, out string provider, out string model));
        Assert.Equal("lmstudio", provider);
        Assert.Equal("qwen/qwen3-8b", model);
        // An Ollama tag keeps its own colon.
        Assert.True(ArkStationClient.TryParseLocalModel("local:ollama:llama3.2:3b", out provider, out model));
        Assert.Equal(("ollama", "llama3.2:3b"), (provider, model));
        Assert.False(ArkStationClient.IsLocalModel(ArkStationClient.DefaultNemotronSuperModel));
        Assert.False(ArkStationClient.IsLocalModel("local:"));
        Assert.Null(ArkStationClient.ArkRouteFor(id));
        Assert.Equal("qwen3", ModelChip.ShortName(id));
    }

    [Fact]
    public void The_local_provider_ids_are_lm_studios_and_ollamas()
    {
        Assert.Contains(ModelInventory.LmStudioDefault.Id, ArkStationClient.LocalProviderIds);
        Assert.Contains(ModelInventory.OllamaDefault.Id, ArkStationClient.LocalProviderIds);
        Assert.Equal(2, ArkStationClient.LocalProviderIds.Count);
    }

    [Fact]
    public void Only_lm_studio_and_ollama_chat_models_join_the_picker()
    {
        var entries = new[]
        {
            new ModelEntry("nebius", "nvidia/nemotron-3-super-120b-a12b", ModelCapability.Chat),
            new ModelEntry("lmstudio", "qwen/qwen3-8b", ModelInventory.InferCapabilities("qwen/qwen3-8b")),
            new ModelEntry("lmstudio", "text-embedding-nomic-embed-text-v1.5", ModelInventory.InferCapabilities("text-embedding-nomic-embed-text-v1.5")),
            new ModelEntry("ollama", "llama3.2:3b", ModelInventory.InferCapabilities("llama3.2:3b")),
            new ModelEntry("local-vision", "yolo11n", ModelInventory.InferCapabilities("yolo11n")),
        };
        Assert.Equal(new[] { "local:lmstudio:qwen/qwen3-8b", "local:ollama:llama3.2:3b" }, ArkStationClient.LocalModelIds(entries));
    }

    [Fact]
    public void A_local_model_routes_to_this_pc_and_says_so()
    {
        var ark = new ArkStationClient();
        Assert.Equal(ArkStationClient.RouteLocal, ark.NextTextRoute("local:lmstudio:qwen/qwen3-8b"));
        Assert.Equal("ON THIS PC", ArkStationClient.RouteLabelFor(ArkStationClient.RouteLocal));
        Assert.Equal("LM Studio", ark.LocalProviderLabel("local:lmstudio:qwen/qwen3-8b"));
        Assert.Null(ark.LocalProviderLabel(ArkStationClient.DefaultNemotronFastModel));
    }

    [Fact]
    public async Task A_local_ask_posts_an_openai_chat_completion_to_the_providers_endpoint()
    {
        using var server = new OneShotServer("""
            {"id":"c1","model":"qwen/qwen3-8b","choices":[{"index":0,"message":{"role":"assistant","content":"Hello from LM Studio."},"finish_reason":"stop"}]}
            """);
        var ark = new ArkStationClient();
        ark.Models.AddProvider(new ModelProvider { Id = "lmtest", Label = "LM Test", BaseUrl = server.BaseUrl + "/v1" });

        var answer = await ark.AskLocalAsync("hi", null, "local:lmtest:qwen/qwen3-8b", "be brief");

        Assert.True(answer.Ok, answer.Output);
        Assert.Equal("Hello from LM Studio.", answer.Output);
        Assert.Equal(ArkStationClient.RouteLocal, answer.Route);
        Assert.Equal("/v1/chat/completions", server.Path);
        Assert.Contains("\"model\":\"qwen/qwen3-8b\"", server.Body);
        Assert.Contains("\"content\":\"hi\"", server.Body);
    }

    [Fact]
    public async Task A_stopped_local_server_answers_plainly_without_a_cloud_fallback()
    {
        var ark = new ArkStationClient();
        ark.Models.AddProvider(new ModelProvider { Id = "lmdown", Label = "LM Down", BaseUrl = "http://127.0.0.1:9/v1" });
        var answer = await ark.AskAsync("hi", model: "local:lmdown:qwen3");
        Assert.False(answer.Ok);
        Assert.Equal(ArkStationClient.RouteLocal, answer.Route);
        Assert.Contains("LM Down is not reachable", answer.Output);
    }

    [Theory]
    [InlineData(false, true, ArkStationClient.RouteNebiusDirect, true, McpBridges.ListedLabel)]
    [InlineData(false, true, ArkStationClient.RouteArkApi, true, McpBridges.ListedLabel)]
    [InlineData(true, true, ArkStationClient.RouteNebiusDirect, true, McpBridges.ToolsUsedLabel)]
    [InlineData(true, true, ArkStationClient.RouteHivemind, false, McpBridges.ToolsNeedNebiusLabel)]
    [InlineData(true, true, ArkStationClient.RouteArkApi, true, McpBridges.ToolsNotOnThisRouteLabel)]
    [InlineData(true, false, ArkStationClient.RouteNebiusDirect, true, null)]
    public void Mcp_rows_say_whether_answers_use_their_tools(bool stdio, bool connected, string route, bool key, string? expected) =>
        Assert.Equal(expected, McpBridges.UsabilityLabel(stdio, connected, route, key));

    /// <summary>A loopback HTTP server that answers one request with a fixed body and records what it got.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public string Path { get; private set; } = "";
        public string Body { get; private set; } = "";

        public OneShotServer(string response)
        {
            int port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                var ctx = await _listener.GetContextAsync();
                Path = ctx.Request.Url!.AbsolutePath;
                using (var reader = new StreamReader(ctx.Request.InputStream)) Body = await reader.ReadToEndAsync();
                byte[] bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }
}
