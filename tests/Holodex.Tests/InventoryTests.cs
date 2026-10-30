using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>Your ArkStation: MCP configs from other clients, classification, engines, timeouts, the pipe summary and renders.</summary>
public class InventoryTests
{
    private const string Secret = "sk-ant-api03-SECRETSECRETSECRETSECRETSECRET";
    private const string EnvSecret = "env-value-that-must-never-be-read";

    /// <summary>A fake home with one MCP config per client, each holding values that must never come out.</summary>
    private static McpConfigLocations FakeClients(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"mcp-clients-{Guid.NewGuid():N}");
        string home = Path.Combine(root, "home"), app = Path.Combine(root, "roaming"), local = Path.Combine(root, "local");
        string project = Path.Combine(root, "work", "proj");
        void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Put(Path.Combine(app, "Claude", "claude_desktop_config.json"), $$"""
            { "mcpServers": {
                "elgato": { "command": "npx.cmd", "args": ["--yes", "@elgato/mcp-server@0.1.5"] },
                "github": { "command": "docker", "args": ["run", "-i", "--rm", "-e", "GITHUB_TOKEN", "ghcr.io/github/github-mcp-server", "--token", "{{Secret}}"],
                            "env": { "GITHUB_TOKEN": "{{EnvSecret}}" } }
            }, "preferences": {} }
            """);
        Put(Path.Combine(home, ".claude.json"), $$"""
            { "mcpServers": { "linear": { "type": "http", "url": "https://mcp.linear.app/mcp" } },
              "projects": { "{{project.Replace("\\", "\\\\")}}": { "mcpServers": { "local-db": { "command": "uvx", "args": ["db-mcp", "--api-key={{Secret}}"] } } } } }
            """);
        Put(Path.Combine(project, ".mcp.json"), """
            { "mcpServers": { "elgato": { "type": "stdio", "command": "npx.cmd", "args": ["--yes", "@elgato/mcp-server@0.1.5"] } } }
            """);
        Put(Path.Combine(home, ".cursor", "mcp.json"), $$"""
            { "mcpServers": { "notion": { "url": "https://mcp.notion.com/mcp?token={{Secret}}", "headers": { "Authorization": "Bearer {{EnvSecret}}" } } } }
            """);
        Put(Path.Combine(app, "Code", "User", "mcp.json"), """
            {
              // VS Code allows comments and trailing commas
              "servers": { "fetch": { "type": "stdio", "command": "uvx", "args": ["mcp-server-fetch"], }, },
            }
            """);
        Put(Path.Combine(home, ".codex", "config.toml"), $$"""
            model = "gpt-5"
            [mcp_servers.context7]
            command = "npx"
            args = ["-y", "@upstash/context7-mcp"]
            [mcp_servers.context7.env]
            CONTEXT7_API_KEY = "{{EnvSecret}}"
            [mcp_servers.figma]
            url = "https://mcp.figma.com/mcp"
            bearer_token_env_var = "FIGMA_TOKEN"
            """);
        Put(Path.Combine(home, ".gemini", "settings.json"), """
            { "mcpServers": { "huggingface": { "httpUrl": "https://huggingface.co/mcp" } } }
            """);
        return new McpConfigLocations(home, app, local);
    }

    [Fact]
    public void Mcp_servers_are_read_from_every_client_without_secret_values()
    {
        var where = FakeClients(out string root);
        try
        {
            var all = McpClientConfigs.ReadAll(where);
            Assert.Contains(all, s => s.Client == "Claude Desktop" && s.Name == "elgato" && s.IsStdio);
            Assert.Contains(all, s => s.Client == "Claude Code" && s.Name == "linear" && s.Transport == "http");
            Assert.Contains(all, s => s.Client == "Claude Code" && s.Name == "local-db");
            Assert.Contains(all, s => s.Client.StartsWith("Claude Code (") && s.Name == "elgato");
            Assert.Contains(all, s => s.Client == "Cursor" && s.Name == "notion" && s.AuthConfigured);
            Assert.Contains(all, s => s.Client == "VS Code" && s.Name == "fetch" && s.IsStdio);
            Assert.Contains(all, s => s.Client == "Codex" && s.Name == "context7" && s.AuthConfigured && s.Args.SequenceEqual(new[] { "-y", "@upstash/context7-mcp" }));
            Assert.Contains(all, s => s.Client == "Codex" && s.Name == "figma" && s.Transport == "http" && s.AuthConfigured);
            Assert.Contains(all, s => s.Client == "Gemini CLI" && s.Name == "huggingface" && s.Url == "https://huggingface.co/mcp");
            Assert.True(all.Single(s => s.Name == "github").AuthConfigured);
            Assert.False(all.First(s => s.Name == "elgato").AuthConfigured);

            // What is shown masks secret-shaped values; env and header values are never read at all.
            foreach (var s in all)
            {
                Assert.DoesNotContain(Secret, s.Display);
                Assert.DoesNotContain(EnvSecret, s.Display);
                Assert.DoesNotContain(EnvSecret, string.Join(" ", s.Args));
                Assert.DoesNotContain(EnvSecret, s.Url);
            }
            Assert.Equal("https://mcp.notion.com/mcp", all.Single(s => s.Name == "notion").Display);

            // The same Stream Deck bridge in Claude Desktop and in a project is one finding with both clients.
            var elgato = McpClientConfigs.Discover(where).Single(f => f.Name == "elgato");
            Assert.Equal(2, elgato.Clients.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--api-key", "abc", "--api-key ***")]
    [InlineData("--token=abc", null, "--token=***")]
    [InlineData("GITHUB_TOKEN=abc", null, "GITHUB_TOKEN=***")]
    [InlineData("https://x.example/mcp?key=abc", null, "https://x.example/mcp")]
    [InlineData("sk-proj-abcdef", null, "***")]
    public void Command_lines_mask_secret_shaped_arguments(string a, string? b, string expected)
    {
        var args = b == null ? new[] { a } : new[] { a, b };
        Assert.Equal("tool " + expected, McpClientConfigs.MaskCommand("tool", args));
        Assert.Equal("https://api.example/mcp", McpClientConfigs.WithoutTemplate("${EXAMPLE_MCP_URL:-https://api.example/mcp}"));
        Assert.Equal("npx -y @scope/server@1.0", McpClientConfigs.MaskCommand("npx", new[] { "-y", "@scope/server@1.0" }));
    }

    [Fact]
    public void Bridge_tool_names_fit_the_function_name_rules()
    {
        string name = McpBridges.ToolName("Elgato Stream Deck!", "streamdeck__execute_action");
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", name);
        Assert.StartsWith("mcp_elgato_stream_deck_", name);
        Assert.True(McpBridges.ToolName(new string('a', 80), new string('b', 80)).Length <= 64);
        var (cmd, args) = McpBridges.ForWindows("npx.cmd", new[] { "--yes", "pkg" });
        Assert.Equal("cmd.exe", cmd);
        Assert.Equal(new[] { "/d", "/s", "/c", "npx.cmd", "--yes", "pkg" }, args);
        var (py, pyArgs) = McpBridges.ForWindows("python.exe", new[] { "-m", "x" });
        Assert.Equal("python.exe", py);
        Assert.Equal(new[] { "-m", "x" }, pyArgs);
    }

    [Fact]
    public async Task A_stdio_server_starts_only_after_the_user_approves_its_command()
    {
        var server = new McpConfiguredServer("Claude Desktop", "never-run", "stdio", "definitely-not-a-real-mcp.exe", new[] { "--flag" }, "", false, Path.GetTempFileName());
        var saved = HolodexTools.Approve;
        string? shown = null;
        try
        {
            HolodexTools.Approve = (command, _) => { shown = command; return Task.FromResult(false); };
            string said = await McpBridges.ConnectAsync(server);
            Assert.Equal("definitely-not-a-real-mcp.exe --flag", shown);
            Assert.Contains("was not started", said);
            Assert.False(McpBridges.IsRunning(McpBridges.IdFor(server)));
        }
        finally { HolodexTools.Approve = saved; }
    }

    [Theory]
    [InlineData(8000, "{\"data\":[{\"id\":\"m\",\"owned_by\":\"vllm\"}]}", "", "vLLM", false)]
    [InlineData(8080, "{\"data\":[{\"id\":\"m\",\"owned_by\":\"llamacpp\"}]}", "", "llama.cpp server", false)]
    [InlineData(8080, "{\"data\":[{\"id\":\"m\"}]}", "local-ai", "LocalAI", false)]
    [InlineData(8080, "{\"data\":[{\"id\":\"m\"}]}", "python", "OpenAI-compatible server (port 8080)", true)]
    [InlineData(5000, "{\"data\":[]}", "python", "text-generation-webui", true)]
    [InlineData(1337, "{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}", "Jan", "Jan", false)]
    public void Engines_are_named_by_their_answer_their_process_or_their_port(int port, string body, string process, string name, bool byPort)
    {
        var found = PcInventoryScan.IdentifyEngine(port, body, process);
        Assert.NotNull(found);
        Assert.Equal(name, found!.Name);
        Assert.Equal(byPort, found.ByPortOnly);
        Assert.Equal($"http://127.0.0.1:{port}/v1", PcInventoryScan.ModelProviderFor(found).BaseUrl);
    }

    [Fact]
    public void A_non_model_answer_is_not_an_engine() =>
        Assert.Null(PcInventoryScan.IdentifyEngine(8000, "{\"system\":{\"comfyui_version\":\"0.3\"}}", ""));

    [Fact]
    public void Every_engine_has_a_vault_catalog_entry()
    {
        foreach (var e in PcInventoryScan.Engines)
        {
            var p = HolodexVault.Find(e.Id);
            Assert.NotNull(p);
            Assert.Equal(VaultKind.LocalService, p!.Kind);
            Assert.Equal($"http://127.0.0.1:{e.Port}/v1/models", p.Url);
        }
    }

    [Fact]
    public void Devices_group_into_one_gear_item_per_physical_device()
    {
        var devices = new[]
        {
            new PnpDevice("HID-compliant mouse", "Microsoft", "Mouse", @"HID\VID_046D&PID_C547&MI_00\8&1"),
            new PnpDevice("HID-compliant consumer control device", "Microsoft", "HIDClass", @"HID\VID_046D&PID_C547&MI_01&COL01\8&2"),
            new PnpDevice("USB Input Device", "(Standard system devices)", "HIDClass", @"USB\VID_0FD9&PID_0080\A1"),
            new PnpDevice("Focusrite USB Audio", "Focusrite", "MEDIA", @"USB\VID_1235&PID_8211&MI_00\7&3"),
            new PnpDevice("Xbox Wireless Controller", "Microsoft", "XnaComposite", @"USB\VID_045E&PID_0B12\3"),
            new PnpDevice("Steam Streaming Speakers", "Valve", "MEDIA", @"ROOT\MEDIA\0000"),
        };
        var gear = PcInventoryScan.GroupDevices(devices);
        Assert.Contains(gear, g => g.Label == "Logitech mouse" && g.Detail == "Device id 046D:C547.");
        Assert.Contains(gear, g => g.Label == "Elgato input device");
        Assert.Contains(gear, g => g.Label.Contains("Focusrite USB Audio") && g.Detail.StartsWith("Audio device"));
        Assert.Contains(gear, g => g.Label.Contains("Xbox Wireless Controller"));
        Assert.DoesNotContain(gear, g => g.Label.Contains("Steam Streaming"));
        Assert.Equal(4, gear.Count);
    }

    private static PcInventoryEnv FakeEnv(TimeSpan? sectionTimeout = null, Func<IReadOnlyList<ServiceInfo>>? services = null, McpConfigLocations? mcp = null)
    {
        var http = new FakeLoopback();
        http.Bodies["http://127.0.0.1:8000/v1/models"] = "{\"data\":[{\"id\":\"meta-llama/Llama-3.1-8B\",\"owned_by\":\"vllm\"}]}";
        http.Bodies["http://127.0.0.1:1234/v1/models"] = "{\"data\":[{\"id\":\"qwen\"}]}";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new PcInventoryEnv
        {
            Probe = new PcProbeEnv
            {
                Http = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                ProcessNames = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "llmster" },
                InstalledApps = () => Array.Empty<InstalledApp>(),
                PipeNames = () => new HashSet<string>(),
                FileExists = _ => false,
                DockerVersion = (_, _) => Task.FromResult<string?>(null),
                Vault = () => Array.Empty<VaultEntry>(),
                ProbeTimeout = TimeSpan.FromSeconds(1)
            },
            Apps = () => new[] { new InstalledApp("Blender", "4.5", "", "", "Blender Foundation"), new InstalledApp("Blender", "4.5", "", "", "Blender Foundation") },
            StoreApps = () => new[] { new InstalledApp("Claude", "1.0", "", "", "Microsoft Store") },
            Services = services ?? (() => new[]
            {
                new ServiceInfo("LGHUBUpdaterService", "Logitech G HUB Updater", "Running", "Auto", @"""C:\Program Files\LGHUB\lghub_updater.exe"" --run-as-service"),
                new ServiceInfo("Dnscache", "DNS Client", "Running", "Auto", Path.Combine(windows, @"system32\svchost.exe -k NetworkService")),
                new ServiceInfo("Stopped3p", "Some Manual Service", "Stopped", "Manual", @"C:\Program Files\X\x.exe"),
            }),
            Listeners = () => new[] { new Listener(1234, "127.0.0.1", 10, "llmster"), new Listener(8000, "0.0.0.0", 11, "python"), new Listener(50000, "127.0.0.1", 12, "rpc") },
            Clis = _ => Task.FromResult<IReadOnlyList<CliFound>>(new[] { new CliFound("git", @"C:\Program Files\Git\cmd\git.exe", "git version 2.51.0.windows.1") }),
            Devices = () => new[] { new PnpDevice("HID-compliant mouse", "Microsoft", "Mouse", @"HID\VID_046D&PID_C547\1") },
            Hardware = () => new HardwareFacts(new[] { "AMD Ryzen 9 7950X (16 cores, 32 threads)" }, new[] { new GpuInfo("NVIDIA GeForce RTX 4090", 24L << 30, "32.0.15.6094") },
                64L << 30, "ASUS ROG", "ASUSTeK ROG STRIX X670E-E", new[] { new DisplayInfo("PG32UCDM", 3840, 2160, true) }, "American Megatrends Inc.", "2611", "2026-04-01", "UEFI"),
            McpServers = () => McpClientConfigs.Discover(mcp ?? new McpConfigLocations(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath())),
            Vault = () => new[] { new VaultEntry { Id = "nebius", ProviderId = "nebius", Kind = VaultKind.ApiKey, Status = "works" } },
            SectionTimeout = sectionTimeout ?? TimeSpan.FromSeconds(5)
        };
    }

    [Fact]
    public async Task Every_item_gets_a_plain_label_and_its_canon_type()
    {
        var where = FakeClients(out string root);
        try
        {
            var inv = await PcInventoryScan.ScanAsync(FakeEnv(mcp: where));
            InvItem One(string id) => inv.Items.Single(i => i.Id == id);

            Assert.Equal("Machina // service", One(PcSetupScan.LmStudioId).Tag.Text);
            Assert.Equal(InvAction.Connect, One(PcSetupScan.LmStudioId).Action);
            var vllm = inv.Items.Single(i => i.Source is EngineFinding);
            Assert.Equal("vLLM", vllm.Label);
            Assert.Equal(InvGroup.Engines, vllm.Group);
            Assert.Equal("Artifact // App", One("app:Blender").Tag.Text);
            Assert.Single(inv.Items, i => i.Id == "app:Blender");
            Assert.Equal(InvGroup.Apps, One("app:Claude").Group);
            Assert.Equal("Machina // cli", One("cli:git").Tag.Text);
            Assert.Equal("2.51.0.windows.1", One("cli:git").Version);
            Assert.Contains(inv.Items, i => i.Id == "service:LGHUBUpdaterService" && i.Tag.Text == "Machina // service");
            Assert.DoesNotContain(inv.Items, i => i.Id == "service:Dnscache" || i.Id == "service:Stopped3p");
            Assert.Contains(inv.Items, i => i.Group == InvGroup.Gear && i.Label == "Logitech mouse" && i.Tag.Text == "Gear");
            Assert.Contains(inv.Items, i => i.Group == InvGroup.Gear && i.Label == "PG32UCDM");
            Assert.Contains(inv.Items, i => i.Group == InvGroup.Hardware && i.Label.Contains("RTX 4090") && i.Detail.Contains("24 GB") && !i.Tag.Canon);
            Assert.Contains(inv.Items, i => i.Group == InvGroup.Firmware && i.Label == "UEFI firmware" && i.Version == "2611" && !i.Tag.Canon);
            Assert.Contains(inv.Items, i => i.Group == InvGroup.Endpoints && i.Label == "Port 1234");
            Assert.DoesNotContain(inv.Items, i => i.Label == "Port 50000");
            Assert.All(inv.Items.Where(i => i.Group == InvGroup.Mcp && i.Source is McpConfiguredServer), i => Assert.Equal("Mod // MCP server", i.Tag.Text));
            // Hardware and firmware are inventory only.
            Assert.All(inv.Items.Where(i => i.Group is InvGroup.Gear or InvGroup.Hardware or InvGroup.Firmware), i => Assert.Equal(InvAction.None, i.Action));
            Assert.Equal(1, inv.VaultConnections);
            Assert.Empty(inv.Unfinished);

            var counts = inv.CountsByType;
            Assert.True(counts["Gear"] >= 2 && counts["Artifact"] >= 2 && counts["Mod"] >= 5 && counts["Machina"] >= 4 && counts["No canon home"] >= 4);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_pipe_summary_counts_by_type_and_carries_no_secrets_paths_or_commands()
    {
        var where = FakeClients(out string root);
        try
        {
            var inv = await PcInventoryScan.ScanAsync(FakeEnv(mcp: where));
            string json = PcInventoryScan.SummaryJson(inv);
            var doc = JsonNode.Parse(json)!;
            Assert.True(doc["counts_by_type"]!["Gear"]!.GetValue<int>() >= 2);
            Assert.Equal(1, doc["counts_by_type"]!["Adapter // Connection"]!.GetValue<int>());
            Assert.Contains(doc["mcp_servers"]!.AsArray(), s => s!["name"]!.GetValue<string>() == "github");
            Assert.Contains(doc["engines"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "vLLM");
            Assert.DoesNotContain(Secret, json);
            Assert.DoesNotContain(EnvSecret, json);
            Assert.DoesNotContain("npx", json);
            Assert.DoesNotContain("Program Files", json);
            Assert.DoesNotContain("mcp.notion.com", json);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_slow_section_is_reported_unfinished_and_never_holds_the_scan()
    {
        var env = FakeEnv(TimeSpan.FromMilliseconds(400), services: () => { Thread.Sleep(5000); return Array.Empty<ServiceInfo>(); });
        var watch = Stopwatch.StartNew();
        var inv = await PcInventoryScan.ScanAsync(env);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"scan took {watch.Elapsed}");
        Assert.Contains(inv.Unfinished, u => u.StartsWith("services"));
        Assert.Contains(inv.Items, i => i.Id == "cli:git");
    }

    [Theory]
    [InlineData("git version 2.51.0.windows.1", "2.51.0.windows.1")]
    [InlineData("v22.11.0", "22.11.0")]
    [InlineData("Python 3.12.7", "3.12.7")]
    [InlineData("Docker version 28.4.0, build d8eb465", "28.4.0")]
    [InlineData("", "")]
    public void Cli_versions_are_read_from_their_first_line(string line, string version) =>
        Assert.Equal(version, PcInventoryScan.CliVersion(line));

    /// <summary>Draws Your ArkStation offscreen (HOLODEX_RENDER=folder): a fixed inventory, then this PC's real scan.</summary>
    [Fact]
    public void Your_arkstation_renders()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (string.IsNullOrEmpty(folder)) return;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var where = FakeClients(out string root);
                try
                {
                    var fixedInv = PcInventoryScan.ScanAsync(FakeEnv(mcp: where)).GetAwaiter().GetResult();
                    using var form = new PcSetupForm(null) { Location = new Point(-4000, -4000) };
                    form.CaptureTo(Path.Combine(folder, "your-arkstation-fixed.png"), fixedInv);
                }
                finally { Directory.Delete(root, recursive: true); }

                // This PC, read-only. Nothing here connects; the vault view is the user's own.
                var live = PcInventoryScan.ScanAsync(new PcInventoryEnv()).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(folder, "your-arkstation-this-pc.json"), PcInventoryScan.SummaryJson(live));
                File.WriteAllLines(Path.Combine(folder, "your-arkstation-this-pc.txt"),
                    live.Items.Select(i => $"{i.Group} | {i.Tag.Text} | {i.Label} | {i.Version} | {i.Action}{(i.InVault ? " | in vault" : "")}"));
                void Shot(string file, InvGroup[]? only, params InvGroup[] expand)
                {
                    using var form = new PcSetupForm(null) { Location = new Point(-4000, -4000) };
                    form.CaptureTo(Path.Combine(folder, file), live, only, expand);
                }
                Shot("your-arkstation-this-pc.png", null);
                Shot("your-arkstation-this-pc-mcp.png", new[] { InvGroup.Mcp, InvGroup.OnThisPc });
                Shot("your-arkstation-this-pc-software.png", new[] { InvGroup.Cli, InvGroup.Services, InvGroup.Apps, InvGroup.Endpoints });
                Shot("your-arkstation-this-pc-gear.png", new[] { InvGroup.Gear, InvGroup.Hardware, InvGroup.Firmware }, InvGroup.Gear);
                Shot("your-arkstation-this-pc-cli.png", new[] { InvGroup.Cli }, InvGroup.Cli);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
