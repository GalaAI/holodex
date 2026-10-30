using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Omniwand;

/// <summary>
/// Holodex's bridge to the user's own MCP servers found in other clients. A stdio server is started with exactly the
/// command its client config declares, only after the user approves that command line in Holodex's approval dialog;
/// its tools then join Nemotron's tool loop next to files and the PC command line (<see cref="HolodexTools"/>). An
/// HTTP server goes into the Vault with its own OAuth (<see cref="HolodexVault.ConnectMcpAsync"/>).
/// A started server runs for this Holodex session; it is recorded in the Vault and is started again only by the user.
/// </summary>
internal static class McpBridges
{
    public const string ProviderId = "mcp-stdio";
    private const int MaxResult = 6_000;

    private sealed record Live(string Id, string Label, McpClient Client, IReadOnlyList<McpClientTool> Tools);

    private static readonly ConcurrentDictionary<string, Live> Running = new();
    private static readonly ConcurrentDictionary<string, (string BridgeId, string Tool)> ToolNames = new();

    public static bool Any => !Running.IsEmpty;

    public static bool IsRunning(string id) => Running.ContainsKey(id);

    /// <summary>The route the next ask takes (set by Program from the shared client), so rows can say whether their tools are used.</summary>
    public static Func<string>? ActiveRoute { get; set; }

    /// <summary>A Nebius key on this PC (set by Program), for the same labels.</summary>
    public static Func<bool>? HasNebiusKey { get; set; }

    public const string ToolsUsedLabel = "Connected. Its tools work in answers";
    public const string ToolsNeedNebiusLabel = "Connected. Its tools work with a Nebius key";
    public const string ToolsNotOnThisRouteLabel = "Connected. Its tools work on Nebius direct only";
    public const string ListedLabel = "Listed. Not used yet";

    /// <summary>
    /// What a connected MCP server really does today, in one plain line. Only a stdio server started through the
    /// bridge offers its tools, and only to the Nebius direct loop (<see cref="HolodexTools.Offer"/>); an HTTP server
    /// is connected and counted, but no answer calls its tools yet. Null when the server is not connected.
    /// </summary>
    public static string? UsabilityLabel(bool stdio, bool connected, string activeRoute, bool hasNebiusKey)
    {
        if (!connected) return null;
        if (!stdio) return ListedLabel;
        if (activeRoute == ArkStationClient.RouteNebiusDirect) return ToolsUsedLabel;
        return hasNebiusKey ? ToolsNotOnThisRouteLabel : ToolsNeedNebiusLabel;
    }

    /// <summary><see cref="UsabilityLabel(bool, bool, string, bool)"/> for the live route and key.</summary>
    public static string? UsabilityLabel(bool stdio, bool connected) =>
        UsabilityLabel(stdio, connected, ActiveRoute?.Invoke() ?? ArkStationClient.RouteNone, HasNebiusKey?.Invoke() ?? false);

    /// <summary>The Vault id for a configured server: stable for the same name and command line.</summary>
    public static string IdFor(McpConfiguredServer s) =>
        $"{ProviderId}:{Slug(s.Name)}:{HolodexVault.ShortFingerprint(s.Key)[7..15]}";

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s : "server";

    /// <summary>
    /// Connects one finding: a stdio server through the bridge (asks first), an HTTP server through the Vault's OAuth.
    /// Returns what happened in one plain line.
    /// </summary>
    public static async Task<string> ConnectAsync(McpConfiguredServer s, CancellationToken ct = default)
    {
        if (!s.IsStdio)
        {
            var entry = await HolodexVault.ConnectMcpAsync(s.Url, s.Name, "mcp-custom", ct);
            return $"{s.Name} is in your vault: {entry.Detail}.";
        }
        string line = McpClientConfigs.CommandLine(s.Command, s.Args);
        bool approved = await HolodexTools.Approve(line,
            $"Starts the {s.Name} MCP server exactly as your {s.Client} config declares it, so Nemotron can use its tools. It runs on this PC under your account.");
        if (!approved) return $"{s.Name} was not started.";
        return await StartAsync(s, ct);
    }

    /// <summary>Starts an approved stdio server, lists its tools and records it in the Vault.</summary>
    private static async Task<string> StartAsync(McpConfiguredServer s, CancellationToken ct)
    {
        string id = IdFor(s);
        if (Running.TryRemove(id, out var old)) await DisposeQuietly(old);
        var (command, args) = ForWindows(s.Command, s.Args);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = s.Name,
            Command = command,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(s.ConfigPath)
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // npx may fetch the package the first time.
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "holodex", Title = "Holodex", Version = HolodexVersion.Number }
            }, cancellationToken: timeout.Token);
            var tools = (await client.ListToolsAsync(cancellationToken: timeout.Token)).ToList();
            var live = new Live(id, s.Name, client, tools);
            Running[id] = live;
            Register(live);
            HolodexVault.Save(new VaultEntry
            {
                Id = id, ProviderId = ProviderId, Label = s.Name, Kind = VaultKind.McpServer,
                Url = $"stdio:{s.Client}/{s.Name}", Fingerprint = "bridged on this PC", Detail = $"{tools.Count} tools",
                Status = "works", AddedAt = DateTimeOffset.Now, CheckedAt = DateTimeOffset.Now
            });
            return $"{s.Name} is running with {tools.Count} tools for Nemotron.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (client != null) await DisposeQuietly(new Live(id, s.Name, client, Array.Empty<McpClientTool>()));
            string why = ex.InnerException?.Message ?? ex.Message;
            return $"{s.Name} did not start: {(why.Length > 200 ? why[..200] : why)}" +
                   (s.AuthConfigured ? " It may need the key your other client passes it; add that key to your Vault." : "");
        }
    }

    /// <summary>
    /// Windows runs .cmd shims (npx, uvx installed by npm) only through cmd.exe; anything else starts as declared.
    /// The command line the user approved is unchanged; this only says which program hosts it.
    /// </summary>
    public static (string Command, List<string> Args) ForWindows(string command, IReadOnlyList<string> args)
    {
        bool hasExt = Path.HasExtension(command);
        bool isCmd = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (!isCmd && !hasExt && PcSetupScan.FindOnPath(command + ".exe") == null && PcSetupScan.FindOnPath(command + ".cmd") != null) isCmd = true;
        return isCmd
            ? ("cmd.exe", new List<string> { "/d", "/s", "/c", command }.Concat(args).ToList())
            : (command, args.ToList());
    }

    private static void Register(Live live)
    {
        foreach (var stale in ToolNames.Where(kv => kv.Value.BridgeId == live.Id).Select(kv => kv.Key).ToList()) ToolNames.TryRemove(stale, out _);
        foreach (var tool in live.Tools)
        {
            string name = ToolName(live.Label, tool.Name);
            for (int n = 2; ToolNames.ContainsKey(name); n++) name = ToolName(live.Label, tool.Name, n);
            ToolNames[name] = (live.Id, tool.Name);
        }
    }

    /// <summary>A function name OpenAI-style tools accept: mcp_&lt;server&gt;_&lt;tool&gt;, letters, digits, _ and -, at most 64.</summary>
    public static string ToolName(string server, string tool, int n = 0)
    {
        string raw = $"mcp_{Slug(server).Replace('-', '_')}_{tool}{(n > 0 ? "_" + n : "")}";
        string clean = Regex.Replace(raw, "[^A-Za-z0-9_-]", "_");
        return clean.Length <= 64 ? clean : clean[..64];
    }

    public static bool Owns(string name) => ToolNames.ContainsKey(name);

    /// <summary>Adds every running bridge's tools to a chat payload's tools.</summary>
    public static void Offer(JsonArray tools, StringBuilder notes)
    {
        var labels = new List<string>();
        foreach (var (name, (bridgeId, toolName)) in ToolNames)
        {
            if (!Running.TryGetValue(bridgeId, out var live) || live.Tools.FirstOrDefault(t => t.Name == toolName) is not { } tool) continue;
            JsonNode parameters;
            try { parameters = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) ?? new JsonObject { ["type"] = "object" }; }
            catch (Exception) { parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }; }
            string description = $"[{live.Label}] {tool.Description}";
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description.Length > 1000 ? description[..1000] : description,
                    ["parameters"] = parameters
                }
            });
            if (!labels.Contains(live.Label)) labels.Add(live.Label);
        }
        if (labels.Count > 0)
            notes.Append($" You can use the user's own MCP servers on this PC ({string.Join(", ", labels)}) through their mcp_ tools.");
    }

    /// <summary>Runs one bridged tool call and returns its text for the model.</summary>
    public static async Task<string> RunAsync(string name, JsonNode? args, CancellationToken ct = default)
    {
        if (!ToolNames.TryGetValue(name, out var target) || !Running.TryGetValue(target.BridgeId, out var live))
            return $"{name} is not running. The user can connect it again from Your ArkStation.";
        var arguments = new Dictionary<string, object?>();
        if (args is JsonObject obj)
            foreach (var (key, value) in obj) arguments[key] = value == null ? null : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        try
        {
            var result = await live.Client.CallToolAsync(target.Tool, arguments, cancellationToken: ct);
            string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (text.Length == 0) text = result.Content.Count == 0 ? "(no output)" : $"({result.Content.Count} non-text results)";
            if (text.Length > MaxResult) text = text[..MaxResult] + "\n[output truncated]";
            return result.IsError == true ? $"{live.Label} reported an error: {text}" : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"{target.Tool} on {live.Label} failed: {ex.Message}";
        }
    }

    private static async Task DisposeQuietly(Live live)
    {
        try { await live.Client.DisposeAsync(); } catch (Exception) { }
    }
}
