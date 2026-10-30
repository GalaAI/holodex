using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>Answers fixed JSON per URL; listed URLs hang until cancelled; anything else is unreachable.</summary>
internal sealed class FakeLoopback : HttpMessageHandler
{
    public readonly Dictionary<string, string> Bodies = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Hang = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        if (Hang.Contains(url)) await Task.Delay(Timeout.Infinite, ct);
        if (!Bodies.TryGetValue(url, out var body)) throw new HttpRequestException("refused");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

public class PcSetupTests
{
    private static string Exp(string path) => Environment.ExpandEnvironmentVariables(path);

    private static (PcProbeEnv Env, FakeLoopback Http) Machine(
        string[]? processes = null, InstalledApp[]? installed = null, string[]? pipes = null,
        string[]? files = null, Func<TimeSpan, CancellationToken, Task<string?>>? docker = null,
        VaultEntry[]? vault = null, TimeSpan? timeout = null)
    {
        var http = new FakeLoopback();
        var fileSet = new HashSet<string>(files ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var env = new PcProbeEnv
        {
            Http = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
            ProcessNames = () => new HashSet<string>(processes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            InstalledApps = () => installed ?? Array.Empty<InstalledApp>(),
            PipeNames = () => new HashSet<string>(pipes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            FileExists = fileSet.Contains,
            DockerVersion = docker ?? ((_, _) => Task.FromResult<string?>(null)),
            Vault = () => vault ?? Array.Empty<VaultEntry>(),
            ProbeTimeout = timeout ?? TimeSpan.FromSeconds(2)
        };
        return (env, http);
    }

    private static PcToolResult Of(IReadOnlyList<PcToolResult> results, string id) => results.Single(r => r.Tool.Id == id);

    [Fact]
    public void Every_scanned_tool_is_a_vault_catalog_entry_on_this_pc()
    {
        foreach (var tool in PcSetupScan.Tools)
        {
            var provider = HolodexVault.Find(tool.Id);
            Assert.NotNull(provider);
            Assert.Equal(HolodexVault.GroupLocal, provider!.Group);
            Assert.Equal(tool.Id == PcSetupScan.StreamDeckMcpId ? VaultKind.McpServer : VaultKind.LocalService, provider.Kind);
        }
        // The entries that were already there stay, with their ids.
        foreach (string id in new[] { HolodexTools.FilesId, HolodexTools.CliId, "local-ollama", "local-lmstudio", "local-comfyui" })
            Assert.NotNull(HolodexVault.Find(id));
        Assert.Equal(HolodexVault.Catalog.Count, HolodexVault.Catalog.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Missing_tools_link_to_their_vendor_page_and_a_winget_package()
    {
        foreach (var tool in PcSetupScan.Tools.Where(t => !t.HideWhenMissing))
        {
            Assert.StartsWith("https://", tool.DownloadUrl);
            Assert.NotEmpty(tool.WingetId);
            string cmd = PcSetupScan.WingetCommand(tool);
            Assert.Contains($"'{tool.WingetId}'", cmd);
            // winget asks for agreements in its own window; Holodex never accepts them for the user.
            Assert.DoesNotContain("accept", cmd, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Scan_maps_each_probe_to_its_state_version_and_action()
    {
        string lmExe = Exp(@"%LOCALAPPDATA%\Programs\LM Studio\LM Studio.exe");
        string deckExe = Exp(@"%ProgramFiles%\Elgato\StreamDeck\StreamDeck.exe");
        string ollamaExe = Exp(@"%LOCALAPPDATA%\Programs\Ollama\ollama app.exe");
        var (env, http) = Machine(
            processes: new[] { "llmster", "logioptionsplus_agent", "Docker Desktop" },
            installed: new[]
            {
                new InstalledApp("LM Studio 0.4.13+1", "0.4.13+1", "", lmExe + ",0"),
                new InstalledApp("Ollama version 0.40.13", "0.40.13", "", ""),
                new InstalledApp("Docker Desktop", "4.97.0", "", ""),
                new InstalledApp("Logi Options+", "2.9.994631", "", ""),
                new InstalledApp("Elgato Stream Deck", "7.5.1.22901", "", ""),
                new InstalledApp("Logi Plugin Service", "6.4.2", "", "")
            },
            pipes: new[] { PcSetupScan.DockerPipe },
            files: new[] { lmExe, deckExe, ollamaExe },
            docker: (_, _) => Task.FromResult<string?>("29.0.1"));
        http.Bodies["http://127.0.0.1:1234/v1/models"] = "{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]}";

        var results = await PcSetupScan.ScanAsync(env);

        var lm = Of(results, PcSetupScan.LmStudioId);
        Assert.Equal(PcToolState.Running, lm.State);
        Assert.Equal(PcSetupAction.Connect, lm.Action);
        Assert.Equal("0.4.13+1", lm.Version);
        Assert.Contains("3 models", lm.Detail);
        Assert.Equal("http://127.0.0.1:1234/v1/models", lm.Endpoint);

        var ollama = Of(results, PcSetupScan.OllamaId);
        Assert.Equal(PcToolState.Installed, ollama.State);
        Assert.Equal(PcSetupAction.Open, ollama.Action);
        Assert.Equal(ollamaExe, ollama.LaunchPath);

        var comfy = Of(results, PcSetupScan.ComfyId);
        Assert.Equal(PcToolState.Missing, comfy.State);
        Assert.Equal(PcSetupAction.GetIt, comfy.Action);

        var docker = Of(results, PcSetupScan.DockerId);
        Assert.Equal(PcToolState.Running, docker.State);
        Assert.Contains("29.0.1", docker.Detail);
        Assert.Equal("4.97.0", docker.Version);

        Assert.Equal(PcToolState.Running, Of(results, PcSetupScan.LogiId).State);

        var deck = Of(results, PcSetupScan.StreamDeckId);
        Assert.Equal(PcToolState.Installed, deck.State);
        Assert.Equal(PcSetupAction.Open, deck.Action);

        // Stream Deck is installed but its MCP pipe is not open: open Stream Deck, which offers it.
        var mcp = Of(results, PcSetupScan.StreamDeckMcpId);
        Assert.Equal(PcToolState.Installed, mcp.State);
        Assert.Equal(PcSetupAction.Open, mcp.Action);

        // Input Studio is an Ark PC service: a public PC does not see its row at all.
        Assert.DoesNotContain(results, r => r.Tool.Id == PcSetupScan.InputStudioId);
    }

    [Fact]
    public async Task Stream_deck_mcp_pipe_offers_connect_and_input_studio_shows_on_an_ark_pc()
    {
        var (env, http) = Machine(processes: new[] { "StreamDeck" }, pipes: new[] { PcSetupScan.StreamDeckMcpPipe });
        http.Bodies[PcSetupScan.InputStudioUrl] = "{\"ok\":true,\"config\":{\"version\":\"0.1.0\",\"actions\":[{\"id\":\"a\"},{\"id\":\"b\"}]}}";
        var results = await PcSetupScan.ScanAsync(env);

        var mcp = Of(results, PcSetupScan.StreamDeckMcpId);
        Assert.Equal(PcToolState.Running, mcp.State);
        Assert.Equal(PcSetupAction.Connect, mcp.Action);
        Assert.Equal(PcSetupScan.StreamDeckMcpUrl, mcp.Endpoint);
        Assert.Contains("approve", mcp.Detail);

        var input = Of(results, PcSetupScan.InputStudioId);
        Assert.Equal(PcToolState.Running, input.State);
        Assert.Contains("2 input actions", input.Detail);
    }

    [Fact]
    public async Task A_tool_already_in_the_vault_needs_no_action()
    {
        var (env, http) = Machine(vault: new[]
        {
            new VaultEntry { Id = PcSetupScan.LmStudioId, ProviderId = PcSetupScan.LmStudioId, Kind = VaultKind.LocalService, Status = "works" }
        });
        http.Bodies["http://127.0.0.1:1234/v1/models"] = "{\"data\":[]}";
        var lm = Of(await PcSetupScan.ScanAsync(env), PcSetupScan.LmStudioId);
        Assert.True(lm.InVault);
        Assert.Equal(PcSetupAction.None, lm.Action);
    }

    [Fact]
    public async Task Hung_probes_are_cut_off_at_the_timeout_and_reported_from_the_install()
    {
        string comfyExe = Exp(@"%LOCALAPPDATA%\Programs\@comfyorgcomfyui-electron\ComfyUI.exe");
        var (env, http) = Machine(
            installed: new[] { new InstalledApp("Docker Desktop", "4.97.0", "", "") },
            pipes: new[] { PcSetupScan.DockerPipe },
            files: new[] { comfyExe },
            // A Docker engine that never answers and ignores cancellation: only the backstop can stop it.
            docker: (_, _) => new TaskCompletionSource<string?>().Task,
            timeout: TimeSpan.FromMilliseconds(400));
        http.Hang.Add("http://127.0.0.1:8188/system_stats");
        http.Hang.Add("http://127.0.0.1:8000/system_stats");
        http.Hang.Add("http://127.0.0.1:1234/v1/models");

        var watch = Stopwatch.StartNew();
        var results = await PcSetupScan.ScanAsync(env);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"scan took {watch.Elapsed}");
        var docker = Of(results, PcSetupScan.DockerId);
        Assert.Equal(PcToolState.Installed, docker.State);
        Assert.Contains("did not answer", docker.Detail);
        var comfy = Of(results, PcSetupScan.ComfyId);
        Assert.Equal(PcToolState.Installed, comfy.State);
        Assert.Equal(PcSetupAction.Open, comfy.Action);
        Assert.Equal(PcToolState.Missing, Of(results, PcSetupScan.LmStudioId).State);
    }

    [Theory]
    [InlineData(PcSetupScan.ComfyId, "{\"system\":{\"comfyui_version\":\"0.3.66\",\"os\":\"nt\"}}", "http://127.0.0.1:8000/system_stats", "0.3.66", "port 8000")]
    [InlineData(PcSetupScan.OllamaId, "{\"models\":[{\"name\":\"llama3.2\"}]}", "http://127.0.0.1:11434/api/tags", "", "1 model.")]
    [InlineData(PcSetupScan.LmStudioId, "not json", "http://127.0.0.1:1234/v1/models", "", "0 models")]
    public void Endpoint_answers_become_a_version_and_a_plain_line(string id, string body, string endpoint, string version, string detail)
    {
        var (v, d) = PcSetupScan.Describe(id, body, endpoint);
        Assert.Equal(version, v);
        Assert.Contains(detail, d);
    }

    [Fact]
    public void Uninstall_names_match_whole_words_only()
    {
        var apps = new[] { new InstalledApp("Logi Plugin Service", "6", "", ""), new InstalledApp("Ollama version 0.40.13", "0.40.13", "", "") };
        Assert.Null(PcSetupScan.FindInstalled("Logi Options+", apps));
        Assert.Equal("0.40.13", PcSetupScan.FindInstalled("Ollama", apps)!.Version);
        Assert.Null(PcSetupScan.FindInstalled("Olla", apps));
    }

    [Fact]
    public async Task Connected_local_model_servers_become_providers_and_ollama_is_saved()
    {
        string path = Path.Combine(Path.GetTempPath(), $"providers-{Guid.NewGuid():N}.json");
        try
        {
            var fake = new FakeModels();
            fake.Lists["http://127.0.0.1:11434/v1/models"] = new[] { "llama3.2:3b", "qwen2.5-coder:7b" };
            var inventory = new ModelInventory(ModelInventory.Defaults, _ => null, new HttpClient(fake));
            Assert.DoesNotContain(inventory.Providers, p => p.Id == "ollama");

            var provider = PcSetupScan.ModelProviderFor(PcSetupScan.OllamaId)!;
            Assert.True(inventory.AddProvider(provider));
            Assert.False(inventory.AddProvider(provider));
            ModelInventory.SaveProvider(provider, path);
            await inventory.Refresh();

            Assert.Equal(2, inventory.Models.Count(m => m.ProviderId == "ollama"));
            Assert.Equal("qwen2.5-coder:7b", new TaskRouter(inventory).Route(TaskKind.Code).ModelId);
            Assert.Contains(ModelInventory.LoadProviders(path), p => p.Id == "ollama" && p.BaseUrl == "http://127.0.0.1:11434/v1");
            Assert.Same(ModelInventory.LmStudioDefault, PcSetupScan.ModelProviderFor(PcSetupScan.LmStudioId));
            Assert.Null(PcSetupScan.ModelProviderFor(PcSetupScan.DockerId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pipe_summary_names_state_action_and_version()
    {
        var tool = PcSetupScan.Find(PcSetupScan.LmStudioId)!;
        var line = PcSetupScan.Summarize(new[] { new PcToolResult(tool, PcToolState.Running, "0.4.13", "", "", "", false) });
        Assert.Equal("local-lmstudio=running:connect:0.4.13", line);
    }

}
