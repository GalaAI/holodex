using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Omniwand;

/// <summary>Where a tool stands on this PC: not here, installed but not answering, or running.</summary>
internal enum PcToolState { Missing, Installed, Running }

/// <summary>The one thing a Set up this PC row offers.</summary>
internal enum PcSetupAction { None, Connect, Open, GetIt }

/// <summary>One program from the Windows uninstall list (Apps in Settings).</summary>
internal sealed record InstalledApp(string Name, string Version, string Location, string Icon, string Publisher = "");

/// <summary>
/// A tool Set up this PC looks for. <see cref="Id"/> is also its Vault catalog id. Detection uses only what the tool
/// itself publishes: its local endpoint, its named pipe, its process names, its install path and its uninstall entry.
/// </summary>
internal sealed record PcTool(
    string Id,
    string Label,
    string Blurb,
    string[] Endpoints,
    string[] Processes,
    string[] Paths,
    string UninstallName,
    string DownloadUrl,
    string WingetId,
    string Pipe = "",
    bool HideWhenMissing = false);

/// <summary>What the scan found for one tool.</summary>
internal sealed record PcToolResult(PcTool Tool, PcToolState State, string Version, string Detail, string Endpoint, string LaunchPath, bool InVault)
{
    public PcSetupAction Action => PcSetupScan.ActionFor(this);
    public bool Found => State != PcToolState.Missing;
}

/// <summary>What the machine looked like when the scan started: processes, installed programs and named pipes.</summary>
internal sealed record PcSnapshot(IReadOnlySet<string> Processes, IReadOnlyList<InstalledApp> Installed, IReadOnlySet<string> Pipes);

/// <summary>
/// How the scan reads this PC. The defaults read the real machine (read-only: process list, uninstall keys, pipe
/// names, loopback HTTP); tests replace any of them.
/// </summary>
internal sealed class PcProbeEnv
{
    private static readonly HttpClient LiveHttp = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public HttpClient Http { get; init; } = LiveHttp;
    public Func<IReadOnlySet<string>> ProcessNames { get; init; } = PcSetupScan.LiveProcessNames;
    public Func<IReadOnlyList<InstalledApp>> InstalledApps { get; init; } = PcSetupScan.LiveInstalledApps;
    public Func<IReadOnlySet<string>> PipeNames { get; init; } = PcSetupScan.LivePipeNames;
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    /// <summary>
    /// The Docker engine's own version, asked over its named pipe (Engine API GET /version), or null when the engine
    /// does not answer in time. No docker.exe is started.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task<string?>> DockerVersion { get; init; } = PcSetupScan.LiveDockerVersionAsync;
    public Func<IReadOnlyList<VaultEntry>> Vault { get; init; } = HolodexVault.Load;
    /// <summary>The most any one probe may take. A probe that runs longer is reported from what is installed.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Set up this PC: finds the local tools Holodex can use (LM Studio, Ollama, ComfyUI, Docker Desktop, Logi Options+,
/// Elgato Stream Deck and its MCP bridge, and Input Studio on Ark PCs) and connects them into the user's own Vault.
/// Every probe runs in parallel with a short timeout, so the scan never holds the UI. Nothing installs or downloads by
/// itself: Get it opens the vendor's page, and Install with winget runs through the approved PC command line.
/// </summary>
internal static class PcSetupScan
{
    public const string LmStudioId = "local-lmstudio";
    public const string OllamaId = "local-ollama";
    public const string ComfyId = "local-comfyui";
    public const string DockerId = "local-docker";
    public const string LogiId = "local-logi-options";
    public const string StreamDeckId = "local-streamdeck";
    public const string StreamDeckMcpId = "mcp-elgato-streamdeck";
    public const string InputStudioId = "local-input-studio";

    public const string DockerPipe = "docker_engine";
    public const string DockerUrl = "npipe://./pipe/docker_engine";
    public const string LogiUrl = "app:logi-options-plus";
    public const string StreamDeckUrl = "app:elgato-stream-deck";
    /// <summary>Stream Deck's own MCP pipe (Elgato MCP server technical specification, socket paths, Windows).</summary>
    public const string StreamDeckMcpPipe = "elgato-mcp-streamdeck";
    /// <summary>Elgato's bridge in HTTP mode serves /mcp and /health on port 9090 by default.</summary>
    public const string StreamDeckMcpUrl = "http://127.0.0.1:9090/mcp";
    public const string StreamDeckBridgeHealth = "http://127.0.0.1:9090/health";
    public const string ElgatoBridgePackage = "@elgato/mcp-server@0.1.8";
    public const string InputStudioUrl = "http://127.0.0.1:8842/api/spine/config";
    public const string NodeDownloadUrl = "https://nodejs.org/en/download";

    private const string StreamDeckDownload = "https://www.elgato.com/us/en/s/downloads";
    private static readonly string[] StreamDeckProcesses = { "StreamDeck" };
    private static readonly string[] StreamDeckPaths = { @"%ProgramFiles%\Elgato\StreamDeck\StreamDeck.exe" };

    /// <summary>
    /// The tools, in the order the panel lists them. Download pages and winget ids were checked on 2026-10-30
    /// (each page answered 200; <c>winget show --id ... -e</c> found each package).
    /// </summary>
    public static readonly IReadOnlyList<PcTool> Tools = new PcTool[]
    {
        new(LmStudioId, "LM Studio", "Local models, OpenAI-compatible.",
            new[] { "http://127.0.0.1:1234/v1/models" },
            new[] { "LM Studio", "llmster", "lms" },
            new[] { @"%LOCALAPPDATA%\Programs\LM Studio\LM Studio.exe", @"%ProgramFiles%\LM Studio\LM Studio.exe" },
            "LM Studio", "https://lmstudio.ai/download", "ElementLabs.LMStudio"),
        new(OllamaId, "Ollama", "Local models.",
            new[] { "http://127.0.0.1:11434/api/tags" },
            new[] { "ollama app", "ollama" },
            new[] { @"%LOCALAPPDATA%\Programs\Ollama\ollama app.exe", @"%LOCALAPPDATA%\Programs\Ollama\ollama.exe" },
            "Ollama", "https://ollama.com/download/windows", "Ollama.Ollama"),
        // ComfyUI Desktop listens on 8000 by default; the portable and manual installs use 8188.
        new(ComfyId, "ComfyUI", "Local image workflows.",
            new[] { "http://127.0.0.1:8188/system_stats", "http://127.0.0.1:8000/system_stats" },
            new[] { "ComfyUI" },
            new[] { @"%LOCALAPPDATA%\Programs\@comfyorgcomfyui-electron\ComfyUI.exe" },
            "ComfyUI", "https://comfy.org/download", "Comfy.ComfyUI-Desktop"),
        new(DockerId, "Docker Desktop", "Containers.",
            Array.Empty<string>(),
            new[] { "Docker Desktop", "com.docker.backend" },
            new[] { @"%ProgramFiles%\Docker\Docker\Docker Desktop.exe", @"%LOCALAPPDATA%\Programs\DockerDesktop\Docker Desktop.exe" },
            "Docker Desktop", "https://www.docker.com/products/docker-desktop/", "Docker.DockerDesktop", Pipe: DockerPipe),
        new(LogiId, "Logi Options+", "Your Logitech mouse and keyboard.",
            Array.Empty<string>(),
            new[] { "logioptionsplus_agent", "logioptionsplus" },
            new[] { @"%ProgramFiles%\LogiOptionsPlus\logioptionsplus.exe" },
            "Logi Options+", "https://www.logitech.com/en-us/software/logi-options-plus", "Logitech.OptionsPlus"),
        new(StreamDeckId, "Elgato Stream Deck", "Your Stream Deck keys and profiles.",
            Array.Empty<string>(), StreamDeckProcesses, StreamDeckPaths,
            "Elgato Stream Deck", StreamDeckDownload, "Elgato.StreamDeck"),
        new(StreamDeckMcpId, "Stream Deck MCP", "Stream Deck actions as tools, through Elgato's bridge.",
            new[] { StreamDeckBridgeHealth }, StreamDeckProcesses, StreamDeckPaths,
            "Elgato Stream Deck", StreamDeckDownload, "Elgato.StreamDeck", Pipe: StreamDeckMcpPipe),
        new(InputStudioId, "Input Studio", "Ark's input service.",
            new[] { InputStudioUrl }, Array.Empty<string>(), Array.Empty<string>(),
            "", "", "", HideWhenMissing: true),
    };

    public static PcTool? Find(string id) => Tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The model provider a connected tool adds to ModelInventory, if it serves models.</summary>
    public static ModelProvider? ModelProviderFor(string toolId) => toolId switch
    {
        LmStudioId => ModelInventory.LmStudioDefault,
        OllamaId => ModelInventory.OllamaDefault,
        _ => null
    };

    // ---- scan ----

    /// <summary>
    /// Scans this PC: one read of processes, uninstall keys and pipes off the UI thread, then every probe in parallel,
    /// each held to <see cref="PcProbeEnv.ProbeTimeout"/>. Input Studio is left out when it is not here.
    /// </summary>
    public static async Task<IReadOnlyList<PcToolResult>> ScanAsync(PcProbeEnv? env = null, CancellationToken ct = default)
    {
        env ??= new PcProbeEnv();
        var (snap, vault) = await Task.Run(() => (TakeSnapshot(env), SafeVault(env)), ct).ConfigureAwait(false);
        var results = await Task.WhenAll(Tools.Select(t => ProbeGuardedAsync(t, env, snap, vault, ct))).ConfigureAwait(false);
        return results.Where(r => !(r.Tool.HideWhenMissing && r.State == PcToolState.Missing)).ToList();
    }

    /// <summary>Null when the tool is running on this PC, else why not. The Vault's Check uses it for non-HTTP entries.</summary>
    public static async Task<string?> CheckAsync(string toolId, CancellationToken ct = default)
    {
        if (Find(toolId) is not { } tool) return "not something Set up this PC knows";
        var env = new PcProbeEnv();
        var snap = await Task.Run(() => TakeSnapshot(env), ct).ConfigureAwait(false);
        var result = await ProbeGuardedAsync(tool, env, snap, Array.Empty<VaultEntry>(), ct).ConfigureAwait(false);
        return result.State == PcToolState.Running ? null : result.State == PcToolState.Installed ? "installed but not running" : "not on this PC";
    }

    private static IReadOnlyList<VaultEntry> SafeVault(PcProbeEnv env)
    {
        try { return env.Vault(); }
        catch (Exception) { return Array.Empty<VaultEntry>(); }
    }

    public static PcSnapshot TakeSnapshot(PcProbeEnv env)
    {
        IReadOnlySet<string> processes, pipes;
        IReadOnlyList<InstalledApp> installed;
        try { processes = env.ProcessNames(); } catch (Exception) { processes = new HashSet<string>(); }
        try { installed = env.InstalledApps(); } catch (Exception) { installed = Array.Empty<InstalledApp>(); }
        try { pipes = env.PipeNames(); } catch (Exception) { pipes = new HashSet<string>(); }
        return new PcSnapshot(processes, installed, pipes);
    }

    private static async Task<PcToolResult> ProbeGuardedAsync(PcTool tool, PcProbeEnv env, PcSnapshot snap, IReadOnlyList<VaultEntry> vault, CancellationToken ct)
    {
        try
        {
            // Each request inside the probe stops at ProbeTimeout too; this is the backstop for a probe that ignores it.
            var budget = env.ProbeTimeout + TimeSpan.FromMilliseconds(250);
            return await ProbeAsync(tool, env, snap, vault, ct).WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return FromInstall(tool, env, snap, vault, $"It did not answer within {env.ProbeTimeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FromInstall(tool, env, snap, vault, $"The check failed: {ex.Message}");
        }
    }

    /// <summary>What a tool's install says without asking it anything (used when a probe ran out of time).</summary>
    private static PcToolResult FromInstall(PcTool tool, PcProbeEnv env, PcSnapshot snap, IReadOnlyList<VaultEntry> vault, string why)
    {
        var (app, launch, _) = Locate(tool, env, snap);
        bool installed = app != null || launch.Length > 0;
        return new PcToolResult(tool, installed ? PcToolState.Installed : PcToolState.Missing, app?.Version ?? "",
            installed ? $"Installed. {why}" : NotHere, "", launch, InVault(tool, vault));
    }

    private const string NotHere = "Not on this PC.";

    private static (InstalledApp? App, string Launch, bool Process) Locate(PcTool tool, PcProbeEnv env, PcSnapshot snap)
    {
        var app = FindInstalled(tool.UninstallName, snap.Installed);
        string launch = tool.Paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(env.FileExists) ?? IconExe(app, env) ?? "";
        bool process = tool.Processes.Any(snap.Processes.Contains);
        return (app, launch, process);
    }

    /// <summary>The uninstall entry whose name is <paramref name="name"/> or starts with it and a space ("Ollama version 0.40").</summary>
    public static InstalledApp? FindInstalled(string name, IReadOnlyList<InstalledApp> installed) =>
        name.Length == 0 ? null : installed.FirstOrDefault(a =>
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            a.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The exe an uninstall entry names as its icon ("C:\...\LM Studio.exe,0"), when it exists.</summary>
    private static string? IconExe(InstalledApp? app, PcProbeEnv env)
    {
        if (app == null || app.Icon.Length == 0) return null;
        string path = app.Icon.Trim().Trim('"');
        int comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma].Trim('"');
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
               && !path.Contains("Installer", StringComparison.OrdinalIgnoreCase) && env.FileExists(path) ? path : null;
    }

    private static bool InVault(PcTool tool, IReadOnlyList<VaultEntry> vault) =>
        vault.Any(e => e.ProviderId == tool.Id && e.Status == "works");

    private static async Task<PcToolResult> ProbeAsync(PcTool tool, PcProbeEnv env, PcSnapshot snap, IReadOnlyList<VaultEntry> vault, CancellationToken ct)
    {
        var (app, launch, process) = Locate(tool, env, snap);
        bool installed = app != null || launch.Length > 0 || process;
        string version = app?.Version ?? "";
        bool inVault = InVault(tool, vault);
        PcToolResult Result(PcToolState state, string detail, string endpoint = "", string? v = null) =>
            new(tool, state, v ?? version, detail, endpoint, launch, inVault);
        PcToolResult NotRunning(string notRunning) =>
            installed ? Result(PcToolState.Installed, notRunning) : Result(PcToolState.Missing, NotHere);

        switch (tool.Id)
        {
            case DockerId:
            {
                if (!snap.Pipes.Contains(DockerPipe)) return NotRunning("Installed. The engine is not running.");
                string? server = await env.DockerVersion(env.ProbeTimeout, ct).ConfigureAwait(false);
                return server != null
                    ? Result(PcToolState.Running, $"The engine answers (Engine {server}).", DockerUrl)
                    : Result(installed ? PcToolState.Installed : PcToolState.Missing, "The engine pipe is open, but the engine did not answer.");
            }
            case LogiId or StreamDeckId:
                return process ? Result(PcToolState.Running, "Running.", tool.Id == LogiId ? LogiUrl : StreamDeckUrl) : NotRunning("Installed, not running.");
            case StreamDeckMcpId:
            {
                if (!snap.Pipes.Contains(StreamDeckMcpPipe))
                {
                    if (!installed) return Result(PcToolState.Missing, "Needs Elgato Stream Deck.");
                    return Result(PcToolState.Installed, process
                        ? "Stream Deck is running without MCP. Update Stream Deck to turn it on."
                        : "Open Stream Deck and it offers its tools here.");
                }
                bool bridge = await GetAsync(env, StreamDeckBridgeHealth, ct).ConfigureAwait(false) != null;
                return Result(PcToolState.Running, bridge
                    ? "Stream Deck offers MCP and Elgato's bridge is running."
                    : "Stream Deck offers MCP. Connect starts Elgato's bridge once you approve it.", StreamDeckMcpUrl, v: "");
            }
            default:
            {
                // Every endpoint at once (ComfyUI has two ports), plus Ollama's version: Windows takes about two seconds to
                // refuse a loopback connection, so asking one after another would add those seconds up.
                var asks = tool.Endpoints.Select(e => GetAsync(env, e, ct)).ToArray();
                var version2 = tool.Id == OllamaId ? GetAsync(env, "http://127.0.0.1:11434/api/version", ct) : Task.FromResult<string?>(null);
                var bodies = await Task.WhenAll(asks).ConfigureAwait(false);
                string? ollamaVersion = await version2.ConfigureAwait(false);
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i] is not { } body) continue;
                    var (v, detail) = Describe(tool.Id, body, tool.Endpoints[i]);
                    if (ollamaVersion != null) v = ReadString(ollamaVersion, "version");
                    return Result(PcToolState.Running, detail, tool.Endpoints[i], v.Length > 0 ? v : null);
                }
                return NotRunning("Installed, not running.");
            }
        }
    }

    /// <summary>Version (when the answer has one) and a plain line from what a tool's endpoint answered.</summary>
    public static (string Version, string Detail) Describe(string toolId, string body, string endpoint)
    {
        JsonNode? doc = null;
        try { doc = JsonNode.Parse(body); } catch (Exception) { }
        int port = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Port : 0;
        static string Models(int n) => n == 1 ? "1 model" : $"{n} models";
        switch (toolId)
        {
            case LmStudioId:
                return ("", $"Running with {Models((doc?["data"] as JsonArray)?.Count ?? 0)}.");
            case OllamaId:
                return ("", $"Running with {Models((doc?["models"] as JsonArray)?.Count ?? 0)}.");
            case ComfyId:
                return (doc?["system"]?["comfyui_version"]?.GetValue<string>() ?? "", $"Running on port {port}.");
            case InputStudioId:
                int actions = (doc?["config"]?["actions"] as JsonArray)?.Count ?? 0;
                return ("", actions > 0 ? $"Running with {actions} input actions." : "Running.");
            default:
                return ("", "Running.");
        }
    }

    private static string ReadString(string json, string key)
    {
        try { return JsonNode.Parse(json)?[key]?.GetValue<string>() ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>GETs a loopback URL within the probe timeout; the body on 2xx, else null.</summary>
    private static async Task<string?> GetAsync(PcProbeEnv env, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(env.ProbeTimeout);
        try
        {
            using var res = await env.Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>
    /// Docker Engine API over <c>\\.\pipe\docker_engine</c>: GET /version answers {"Version": ...} when the engine runs.
    /// This is the engine's own API, so a busy PC never waits on a CLI starting.
    /// </summary>
    public static async Task<string?> LiveDockerVersionAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", DockerPipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token).ConfigureAwait(false);
                return pipe;
            }
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var res = await http.GetAsync("http://docker/version", cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))?["Version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
    public static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => { try { return Path.Combine(dir.Trim('"'), exe); } catch (ArgumentException) { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));

    /// <summary>
    /// The row's one action: Connect what runs and is not in the vault yet, Open what is installed but stopped, Get it
    /// for what is missing. A running tool already in the vault needs nothing.
    /// </summary>
    public static PcSetupAction ActionFor(PcToolResult r) => r.State switch
    {
        PcToolState.Running => r.InVault ? PcSetupAction.None : PcSetupAction.Connect,
        PcToolState.Installed => r.LaunchPath.Length > 0 ? PcSetupAction.Open : PcSetupAction.None,
        _ => r.Tool.DownloadUrl.Length > 0 ? PcSetupAction.GetIt : PcSetupAction.None
    };

    /// <summary>One line per tool for the control pipe: id=state:action[:version].</summary>
    public static string Summarize(IReadOnlyList<PcToolResult> results) =>
        string.Join(";", results.Select(r =>
            $"{r.Tool.Id}={r.State.ToString().ToLowerInvariant()}:{r.Action.ToString().ToLowerInvariant()}{(r.Version.Length > 0 ? ":" + r.Version : "")}{(r.InVault ? ":in-vault" : "")}"));

    // ---- actions ----

    /// <summary>
    /// Connects a found tool into the user's own Vault. LM Studio and Ollama also join ModelInventory, so their models
    /// can be chosen; Ollama is saved to the providers file so it is still there after a restart.
    /// </summary>
    public static async Task<string> ConnectAsync(PcToolResult r, ModelInventory? models, CancellationToken ct = default, string? providersPath = null)
    {
        if (r.Tool.Id == StreamDeckMcpId) return await ConnectStreamDeckMcpAsync(ct);
        var provider = HolodexVault.Find(r.Tool.Id) ?? throw new InvalidOperationException($"{r.Tool.Label} is not in the Vault catalog.");
        var entry = await HolodexVault.ConnectLocalAsync(provider, ct, url: r.Endpoint.Length > 0 ? r.Endpoint : provider.Url);
        if (entry.Status != "works") return $"{r.Tool.Label} did not answer: {entry.Detail}.";
        string extra = "";
        if (ModelProviderFor(r.Tool.Id) is { } mp)
        {
            if (mp.Id == ModelInventory.OllamaDefault.Id) ModelInventory.SaveProvider(mp, providersPath);
            if (models != null)
            {
                models.AddProvider(mp);
                await models.Refresh(ct);
                int n = models.Models.Count(m => m.ProviderId == mp.Id);
                extra = n == 0 ? " Its models show in Settings, Models once it lists them." : $" {n} of its models are in Settings, Models.";
            }
        }
        return $"{r.Tool.Label} is in your vault.{extra}";
    }

    /// <summary>
    /// Stream Deck MCP: when Elgato's bridge is not running, starts it in HTTP mode through the approved PC command
    /// line (npx fetches the pinned package from npm the first time, which the approval says), waits for /health, then
    /// connects it as an MCP server with no sign-in.
    /// </summary>
    private static async Task<string> ConnectStreamDeckMcpAsync(CancellationToken ct)
    {
        var env = new PcProbeEnv { ProbeTimeout = TimeSpan.FromSeconds(2) };
        if (await GetAsync(env, StreamDeckBridgeHealth, ct) == null)
        {
            if (FindOnPath("npx.cmd") == null)
            {
                OpenUrl(NodeDownloadUrl);
                return "Elgato's bridge needs Node.js. Its download page is open; connect again once it is installed.";
            }
            string ran = await HolodexTools.RunAsync("run_command", new JsonObject
            {
                ["command"] = BridgeCommand,
                ["why"] = $"Starts Elgato's Stream Deck MCP bridge on this PC at 127.0.0.1:9090. The first time, npx downloads {ElgatoBridgePackage} from npm."
            }, ct);
            if (ran.StartsWith("The user declined", StringComparison.Ordinal)) return "Not connected. The bridge was not started.";
            var until = DateTime.UtcNow.AddSeconds(45);
            while (await GetAsync(env, StreamDeckBridgeHealth, ct) == null)
            {
                if (DateTime.UtcNow > until) return "Elgato's bridge did not come up within 45 s. Scan again in a moment.";
                await Task.Delay(1000, ct);
            }
        }
        var entry = await HolodexVault.ConnectMcpAsync(StreamDeckMcpUrl, "Stream Deck MCP", StreamDeckMcpId, ct);
        return $"Stream Deck MCP is in your vault: {entry.Detail}.";
    }

    /// <summary>The command that starts Elgato's bridge, shown to the user word for word before it runs.</summary>
    public static string BridgeCommand =>
        $"Start-Process -WindowStyle Hidden -FilePath npx.cmd -ArgumentList '--yes','{ElgatoBridgePackage}','--http','--port','9090'";

    /// <summary>
    /// The winget command for a missing tool. It opens winget in its own window, so the user watches the install and
    /// winget asks for any license agreement itself; Holodex accepts nothing on the user's behalf.
    /// </summary>
    public static string WingetCommand(PcTool tool) =>
        $"Start-Process winget -ArgumentList 'install','--id','{tool.WingetId}','-e','--source','winget'";

    /// <summary>Asks to run the winget command through the approved PC command line. Nothing runs without the user's Run.</summary>
    public static Task<string> InstallWithWingetAsync(PcTool tool, CancellationToken ct = default) =>
        tool.WingetId.Length == 0
            ? Task.FromResult($"{tool.Label} has no winget package.")
            : HolodexTools.RunAsync("run_command", new JsonObject
            {
                ["command"] = WingetCommand(tool),
                ["why"] = $"Installs {tool.Label} with winget in a window you can watch. winget asks before it accepts any license."
            }, ct);

    /// <summary>Starts the installed app (the user chose Open).</summary>
    public static void Open(PcToolResult r)
    {
        if (r.LaunchPath.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(r.LaunchPath) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show($"{r.Tool.Label} did not open", ex.Message, "HOLODEX // SET UP THIS PC", 3600, ToastTone.Warning); }
    }

    /// <summary>Opens the vendor's own download page.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show("The page did not open", ex.Message, "HOLODEX // SET UP THIS PC", 3600, ToastTone.Warning); }
    }

    // ---- the panel ----

    /// <summary>Set by Program at startup, so a panel opened from anywhere can make local models selectable.</summary>
    public static ModelInventory? Models { get; set; }

    private static PcSetupForm? _form;

    /// <summary>
    /// The one entry point: opens Your ArkStation (the Set up this PC step) and scans the whole PC. Call on the UI
    /// thread (the onboarding step, the Vault, the pipe command <c>holodex:setup:scan</c>). The task ends with what the
    /// scan found.
    /// </summary>
    public static Task<PcInventory> ShowAsync(IWin32Window? owner = null)
    {
        if (_form == null || _form.IsDisposed) _form = new PcSetupForm(Models);
        _form.Summon(owner);
        return _form.ScanAsync();
    }

    /// <summary>The panel, made on first use (pipe captures).</summary>
    public static PcSetupForm Panel => _form is { IsDisposed: false } f ? f : _form = new PcSetupForm(Models);

    // ---- live readers ----

    public static IReadOnlySet<string> LiveProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return names;
    }

    public static IReadOnlySet<string> LivePipeNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(@"\\.\pipe\")) names.Add(Path.GetFileName(path));
        return names;
    }

    /// <summary>The uninstall keys Settings, Apps reads: machine 64 and 32 bit, and this user's.</summary>
    public static IReadOnlyList<InstalledApp> LiveInstalledApps()
    {
        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var apps = new List<InstalledApp>();
        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(Uninstall);
                if (key == null) return;
                foreach (string sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                    // Settings, Apps hides system components and updates that belong to another entry; so does this.
                    if (app.GetValue("SystemComponent") is int system && system == 1) continue;
                    if (app.GetValue("ParentKeyName") is string parent && parent.Length > 0) continue;
                    apps.Add(new InstalledApp(name, app.GetValue("DisplayVersion") as string ?? "",
                        app.GetValue("InstallLocation") as string ?? "", app.GetValue("DisplayIcon") as string ?? "",
                        app.GetValue("Publisher") as string ?? ""));
                }
            }
            catch (Exception) { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Default);
        return apps;
    }
}
