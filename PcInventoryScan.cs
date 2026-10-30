using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WmiOptions = System.Management.EnumerationOptions;

namespace Omniwand;

/// <summary>The groups Your ArkStation lists, in order.</summary>
internal enum InvGroup { Engines, Mcp, OnThisPc, Cli, Services, Apps, Endpoints, Gear, Hardware, Firmware }

/// <summary>
/// The Omnitek reading of an item, from Veritas canon (packages/lexicon/src/veritas-canon.ts on origin/main) and the
/// operator's 2026-10-29 rulings. <see cref="Type"/> is empty where canon has no clean home; the plain label stands in
/// and the item is listed for a ruling. Machina is a Vessel kind in canon, not an Omnitek item type, and is tagged as
/// such rather than forced into an item type.
/// </summary>
internal sealed record OmnitekTag(string Type, string Sub)
{
    public string Text => Type.Length == 0 ? Sub : Sub.Length > 0 ? $"{Type} // {Sub}" : Type;
    public bool Canon => Type.Length > 0;

    /// <summary>Gear: physical hardware a Machina uses to reach the physical world (input devices and other peripherals).</summary>
    public static readonly OmnitekTag Gear = new("Gear", "");
    /// <summary>Artifact, subtype App: a standalone application a person runs on its own.</summary>
    public static readonly OmnitekTag App = new("Artifact", "App");
    /// <summary>Mod: the capability unit agents consume; canon names MCP servers as Mods.</summary>
    public static readonly OmnitekTag McpServer = new("Mod", "MCP server");
    /// <summary>Machina kinds from canon's machinaKindSchema: cli, desktop, console, service, gateway, execution-environment.</summary>
    public static OmnitekTag Machina(string kind) => new("Machina", kind);
    /// <summary>No canon home: a plain label only.</summary>
    public static OmnitekTag Plain(string label) => new("", label);
}

internal enum InvAction { None, Connect, Open, GetIt }

/// <summary>One row of Your ArkStation. <see cref="Source"/> is what its action works on.</summary>
internal sealed record InvItem(InvGroup Group, string Id, string Label, string Detail, string Version, OmnitekTag Tag,
    InvAction Action = InvAction.None, bool InVault = false, object? Source = null);

internal sealed record ServiceInfo(string Name, string DisplayName, string State, string StartMode, string PathName);
internal sealed record Listener(int Port, string Address, int Pid, string Process);
internal sealed record CliFound(string Name, string Path, string VersionLine);
internal sealed record PnpDevice(string Name, string Manufacturer, string Class, string DeviceId);
internal sealed record GpuInfo(string Name, long VramBytes, string Driver);
internal sealed record DisplayInfo(string Name, int Width, int Height, bool Primary);
internal sealed record HardwareFacts(
    IReadOnlyList<string> Cpus, IReadOnlyList<GpuInfo> Gpus, long RamBytes, string SystemModel, string Board,
    IReadOnlyList<DisplayInfo> Displays, string BiosVendor, string BiosVersion, string BiosDate, string FirmwareType);

/// <summary>A local inference server found on its documented port (beyond LM Studio, Ollama and ComfyUI).</summary>
internal sealed record EngineFinding(EngineSpec Spec, string Name, int Port, int Models, string Process, bool ByPortOnly)
{
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public string ModelsUrl => BaseUrl + "/models";
}

/// <summary>An OpenAI-compatible engine and the port its own docs give as the default.</summary>
internal sealed record EngineSpec(string Id, string Name, int Port, string OwnedByHint, string ProcessHint, string DownloadUrl);

/// <summary>Everything one scan found.</summary>
internal sealed record PcInventory(
    IReadOnlyList<PcToolResult> Tools,
    IReadOnlyList<InvItem> Items,
    IReadOnlyList<string> Unfinished,
    int VaultConnections,
    TimeSpan Took)
{
    public static readonly PcInventory Empty = new(Array.Empty<PcToolResult>(), Array.Empty<InvItem>(), Array.Empty<string>(), 0, TimeSpan.Zero);

    /// <summary>Counts per Omnitek type; items with no canon home count under "No canon home".</summary>
    public IReadOnlyDictionary<string, int> CountsByType =>
        Items.GroupBy(i => i.Tag.Canon ? i.Tag.Type : "No canon home").ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>How the inventory reads this PC. The defaults read the real machine, read-only; tests replace them.</summary>
internal sealed class PcInventoryEnv
{
    public PcProbeEnv Probe { get; init; } = new();
    public Func<IReadOnlyList<InstalledApp>> Apps { get; init; } = PcSetupScan.LiveInstalledApps;
    public Func<IReadOnlyList<InstalledApp>> StoreApps { get; init; } = PcInventoryScan.LiveStoreApps;
    public Func<IReadOnlyList<ServiceInfo>> Services { get; init; } = PcInventoryScan.LiveServices;
    public Func<IReadOnlyList<Listener>?> Listeners { get; init; } = PcInventoryScan.LiveListeners;
    public Func<CancellationToken, Task<IReadOnlyList<CliFound>>> Clis { get; init; } = PcInventoryScan.LiveClisAsync;
    public Func<IReadOnlyList<PnpDevice>> Devices { get; init; } = PcInventoryScan.LiveDevices;
    public Func<HardwareFacts> Hardware { get; init; } = PcInventoryScan.LiveHardware;
    public Func<IReadOnlyList<McpServerFinding>> McpServers { get; init; } = () => McpClientConfigs.Discover();
    public Func<IReadOnlyList<VaultEntry>> Vault { get; init; } = HolodexVault.Load;
    /// <summary>The most one section may take; a slower section is reported as not finished, never waited on.</summary>
    public TimeSpan SectionTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// Your ArkStation: the whole-PC scan behind Set up this PC. It reads installed software, services, local endpoints
/// and the engines behind them, MCP servers the user configured in other clients, CLIs on PATH, devices, hardware and
/// firmware, every section in parallel with its own timeout, and classifies each item into Omnitek types. Discovery
/// is read-only. Connecting always asks the user and writes only to their own Vault on this PC.
/// </summary>
internal static class PcInventoryScan
{
    /// <summary>
    /// Engines beyond LM Studio, Ollama and ComfyUI, each on the default port its own docs give: vLLM 8000, LocalAI
    /// 8080, llama.cpp server 8080, text-generation-webui 5000 (with --api), Jan 1337, KoboldCpp 5001, GPT4All 4891.
    /// Each speaks the OpenAI-compatible GET /v1/models.
    /// </summary>
    public static readonly IReadOnlyList<EngineSpec> Engines = new EngineSpec[]
    {
        new("local-vllm", "vLLM", 8000, "vllm", "vllm", "https://docs.vllm.ai/"),
        new("local-localai", "LocalAI", 8080, "", "local-ai", "https://localai.io/"),
        new("local-llamacpp", "llama.cpp server", 8080, "llamacpp", "llama-server", "https://github.com/ggml-org/llama.cpp"),
        new("local-tgwui", "text-generation-webui", 5000, "", "", "https://github.com/oobabooga/text-generation-webui"),
        new("local-jan", "Jan", 1337, "", "jan", "https://jan.ai/"),
        new("local-koboldcpp", "KoboldCpp", 5001, "koboldcpp", "koboldcpp", "https://github.com/LostRuins/koboldcpp"),
        new("local-gpt4all", "GPT4All", 4891, "", "gpt4all", "https://www.nomic.ai/gpt4all"),
    };

    // ---- scan ----

    public static async Task<PcInventory> ScanAsync(PcInventoryEnv? env = null, CancellationToken ct = default)
    {
        env ??= new PcInventoryEnv();
        var watch = Stopwatch.StartNew();
        var unfinished = new List<string>();
        Task<T> Section<T>(string name, Func<T> read, T fallback) => Guard(name, Task.Run(read, ct), fallback, env.SectionTimeout, unfinished, ct);

        var tools = Guard("known tools", PcSetupScan.ScanAsync(env.Probe, ct), (IReadOnlyList<PcToolResult>)Array.Empty<PcToolResult>(), env.SectionTimeout, unfinished, ct);
        var apps = Section("installed software", env.Apps, (IReadOnlyList<InstalledApp>)Array.Empty<InstalledApp>());
        var store = Section("Store apps", env.StoreApps, (IReadOnlyList<InstalledApp>)Array.Empty<InstalledApp>());
        var services = Section("services", env.Services, (IReadOnlyList<ServiceInfo>)Array.Empty<ServiceInfo>());
        var listeners = Section("local endpoints", env.Listeners, (IReadOnlyList<Listener>?)null);
        var clis = Guard("command line tools", Task.Run(() => env.Clis(ct), ct), (IReadOnlyList<CliFound>)Array.Empty<CliFound>(), env.SectionTimeout, unfinished, ct);
        var devices = Section("devices", env.Devices, (IReadOnlyList<PnpDevice>)Array.Empty<PnpDevice>());
        var hardware = Section("hardware and firmware", env.Hardware, (HardwareFacts?)null);
        var mcp = Section("MCP servers", env.McpServers, (IReadOnlyList<McpServerFinding>)Array.Empty<McpServerFinding>());
        var vault = Section("vault", env.Vault, (IReadOnlyList<VaultEntry>)Array.Empty<VaultEntry>());
        var engines = Guard("engines", ProbeEnginesAsync(env.Probe, listeners, ct), (IReadOnlyList<EngineFinding>)Array.Empty<EngineFinding>(), env.SectionTimeout, unfinished, ct);

        await Task.WhenAll(tools, apps, store, services, listeners, clis, devices, hardware, mcp, vault, engines).ConfigureAwait(false);
        var items = Classify(tools.Result, apps.Result.Concat(store.Result).ToList(), services.Result, listeners.Result ?? Array.Empty<Listener>(),
            engines.Result, mcp.Result, clis.Result, devices.Result, hardware.Result, vault.Result);
        lock (unfinished) return new PcInventory(tools.Result, items, unfinished.ToList(), vault.Result.Count, watch.Elapsed);
    }

    private static async Task<T> Guard<T>(string name, Task<T> work, T fallback, TimeSpan timeout, List<string> unfinished, CancellationToken ct)
    {
        try { return await work.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            lock (unfinished) unfinished.Add(ex is TimeoutException ? $"{name} (over {timeout.TotalSeconds:0} s)" : $"{name} ({ex.Message})");
            return fallback;
        }
    }

    /// <summary>
    /// Asks each engine port that something listens on (or every engine port when the listener table could not be
    /// read) for its OpenAI-compatible model list, and names the engine by the list's owned_by, the owning process,
    /// or failing both by the port's documented default.
    /// </summary>
    private static async Task<IReadOnlyList<EngineFinding>> ProbeEnginesAsync(PcProbeEnv probe, Task<IReadOnlyList<Listener>?> listenersTask, CancellationToken ct)
    {
        IReadOnlyList<Listener>? listeners;
        try { listeners = await listenersTask.ConfigureAwait(false); } catch (Exception) { listeners = null; }
        var ports = Engines.Select(e => e.Port).Distinct()
            .Where(p => listeners == null || listeners.Any(l => l.Port == p)).ToList();
        var asks = ports.Select(async port =>
        {
            string? body = await GetAsync(probe, $"http://127.0.0.1:{port}/v1/models", ct).ConfigureAwait(false);
            if (body == null) return null;
            string process = listeners?.FirstOrDefault(l => l.Port == port)?.Process ?? "";
            return IdentifyEngine(port, body, process);
        });
        return (await Task.WhenAll(asks).ConfigureAwait(false)).OfType<EngineFinding>().ToList();
    }

    /// <summary>Which engine answered on a port. Null when the answer is not an OpenAI-compatible model list.</summary>
    public static EngineFinding? IdentifyEngine(int port, string modelsBody, string process)
    {
        JsonNode? doc;
        try { doc = JsonNode.Parse(modelsBody); } catch (Exception) { return null; }
        if (doc?["data"] is not JsonArray data) return null;
        string ownedBy = string.Join(" ", data.Select(m => m?["owned_by"]?.ToString() ?? "")).ToLowerInvariant();
        var onPort = Engines.Where(e => e.Port == port).ToList();
        var spec = onPort.FirstOrDefault(e => e.OwnedByHint.Length > 0 && ownedBy.Contains(e.OwnedByHint))
                   ?? onPort.FirstOrDefault(e => e.ProcessHint.Length > 0 && process.Contains(e.ProcessHint, StringComparison.OrdinalIgnoreCase));
        bool byPort = spec == null;
        spec ??= onPort.FirstOrDefault();
        if (spec == null) return null;
        string name = byPort && onPort.Count > 1 ? $"OpenAI-compatible server (port {port})" : spec.Name;
        return new EngineFinding(spec, name, port, data.Count, process, byPort);
    }

    private static async Task<string?> GetAsync(PcProbeEnv env, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(env.ProbeTimeout);
        try
        {
            using var res = await env.Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    // ---- actions ----

    /// <summary>
    /// The row's Connect, after the user chose it: a known tool through Set up this PC, an engine as a Vault entry and a
    /// model provider, an MCP server through the bridge (which asks again with the exact command) or the Vault's OAuth.
    /// </summary>
    public static async Task<string> ConnectAsync(InvItem item, ModelInventory? models, CancellationToken ct = default, string? providersPath = null)
    {
        switch (item.Source)
        {
            case PcToolResult tool:
                return await PcSetupScan.ConnectAsync(tool, models, ct, providersPath);
            case McpConfiguredServer server:
                return await McpBridges.ConnectAsync(server, ct);
            case EngineFinding engine:
            {
                var provider = HolodexVault.Find(engine.Spec.Id) ?? throw new InvalidOperationException($"{engine.Name} is not in the Vault catalog.");
                var entry = await HolodexVault.ConnectLocalAsync(provider, ct, url: engine.ModelsUrl);
                if (entry.Status != "works") return $"{engine.Name} did not answer: {entry.Detail}.";
                var mp = ModelProviderFor(engine);
                ModelInventory.SaveProvider(mp, providersPath);
                if (models == null) return $"{engine.Name} is in your vault.";
                models.AddProvider(mp);
                await models.Refresh(ct);
                int n = models.Models.Count(m => m.ProviderId == mp.Id);
                return $"{engine.Name} is in your vault. {n} of its models are in Settings, Models.";
            }
            default:
                return $"{item.Label} has nothing to connect.";
        }
    }

    /// <summary>The OpenAI-compatible model provider an engine becomes.</summary>
    public static ModelProvider ModelProviderFor(EngineFinding engine) => new()
    {
        Id = engine.Spec.Id["local-".Length..] + (engine.Port == engine.Spec.Port ? "" : $"-{engine.Port}"),
        Label = engine.Name,
        BaseUrl = engine.BaseUrl
    };

    // ---- classification ----

    private static readonly HashSet<string> EngineToolIds = new() { PcSetupScan.LmStudioId, PcSetupScan.OllamaId, PcSetupScan.ComfyId };

    /// <summary>Turns what each section read into rows, each with its plain label and its Omnitek tag.</summary>
    public static List<InvItem> Classify(
        IReadOnlyList<PcToolResult> tools, IReadOnlyList<InstalledApp> apps, IReadOnlyList<ServiceInfo> services,
        IReadOnlyList<Listener> listeners, IReadOnlyList<EngineFinding> engines, IReadOnlyList<McpServerFinding> mcp,
        IReadOnlyList<CliFound> clis, IReadOnlyList<PnpDevice> devices, HardwareFacts? hardware, IReadOnlyList<VaultEntry> vault)
    {
        var items = new List<InvItem>();
        var inVault = vault.Where(e => e.Status == "works").Select(e => e.Id).ToHashSet();
        var vaultProviders = vault.Where(e => e.Status == "works").Select(e => e.ProviderId).ToHashSet();

        foreach (var t in tools)
        {
            var (group, tag) = t.Tool.Id switch
            {
                _ when EngineToolIds.Contains(t.Tool.Id) => (InvGroup.Engines, OmnitekTag.Machina("service")),
                PcSetupScan.StreamDeckMcpId => (InvGroup.Mcp, OmnitekTag.McpServer),
                PcSetupScan.DockerId => (InvGroup.OnThisPc, OmnitekTag.Machina("execution environment")),
                PcSetupScan.InputStudioId => (InvGroup.OnThisPc, OmnitekTag.Machina("service")),
                _ => (InvGroup.OnThisPc, OmnitekTag.App)
            };
            items.Add(new InvItem(group, t.Tool.Id, t.Tool.Label, $"{t.Tool.Blurb} {t.Detail}", t.Version, tag, ToInv(t.Action), t.InVault, t));
        }

        foreach (var e in engines)
        {
            bool connected = vaultProviders.Contains(e.Spec.Id);
            string models = e.Models == 1 ? "1 model" : $"{e.Models} models";
            string by = e.ByPortOnly ? $" Named by its port; {e.Process} answers." : "";
            items.Add(new InvItem(InvGroup.Engines, e.Spec.Id + ":" + e.Port, e.Name, $"OpenAI-compatible, port {e.Port}, {models}.{by}", "",
                OmnitekTag.Machina("service"), connected ? InvAction.None : InvAction.Connect, connected, e));
        }

        foreach (var m in mcp)
        {
            string id = m.Server.IsStdio ? McpBridges.IdFor(m.Server) : "mcp:" + (Uri.TryCreate(m.Server.Url, UriKind.Absolute, out var u) ? u.Host : m.Name);
            bool connected = m.Server.IsStdio ? McpBridges.IsRunning(id) : vault.Any(v => v.Kind == VaultKind.McpServer && v.Status == "works" && SameServer(v.Url, m.Server.Url));
            string how = m.Server.IsStdio ? "Runs on this PC" : "Remote, own sign-in";
            string auth = m.Server.AuthConfigured ? " A key or sign-in is configured there." : "";
            items.Add(new InvItem(InvGroup.Mcp, id, m.Name, $"{how}. From {string.Join(", ", m.Clients)}: {m.Server.Display}.{auth}", "",
                OmnitekTag.McpServer, connected ? InvAction.None : InvAction.Connect, connected, m.Server));
        }

        foreach (var c in clis.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new InvItem(InvGroup.Cli, "cli:" + c.Name, c.Name, $"{c.Path}. Use it through the approved PC command line.", CliVersion(c.VersionLine),
                OmnitekTag.Machina("cli")));

        foreach (var s in ThirdPartyServices(services))
            items.Add(new InvItem(InvGroup.Services, "service:" + s.Name, s.DisplayName.Length > 0 ? s.DisplayName : s.Name,
                $"{(s.State == "Running" ? "Running" : "Stopped")}, starts {s.StartMode.ToLowerInvariant() switch { "auto" => "automatically", "manual" => "on demand", var x => x }}.",
                "", OmnitekTag.Machina("service")));

        foreach (var a in VisibleApps(apps))
            items.Add(new InvItem(InvGroup.Apps, "app:" + a.Name, a.Name,
                $"{(a.Publisher.Length > 0 ? a.Publisher + ". " : "")}Use it through the approved PC command line.", a.Version, OmnitekTag.App));

        foreach (var l in listeners.Where(l => l.Port < 49152).GroupBy(l => l.Port).Select(g => g.First()).OrderBy(l => l.Port))
            items.Add(new InvItem(InvGroup.Endpoints, $"port:{l.Port}", $"Port {l.Port}",
                $"{(l.Process.Length > 0 ? l.Process : "A process")} listens on {l.Address}.", "", OmnitekTag.Plain("Local endpoint")));

        foreach (var g in GroupDevices(devices))
            items.Add(new InvItem(InvGroup.Gear, "gear:" + g.Id, g.Label, g.Detail, "", OmnitekTag.Gear));

        if (hardware != null)
        {
            foreach (var d in hardware.Displays)
                items.Add(new InvItem(InvGroup.Gear, "display:" + d.Name, d.Name, $"{d.Width} x {d.Height}{(d.Primary ? ", main display" : "")}.", "", OmnitekTag.Gear));
            foreach (string cpu in hardware.Cpus)
                items.Add(new InvItem(InvGroup.Hardware, "cpu:" + cpu, cpu, "Processor.", "", OmnitekTag.Plain("PC hardware")));
            foreach (var gpu in hardware.Gpus)
                items.Add(new InvItem(InvGroup.Hardware, "gpu:" + gpu.Name, gpu.Name,
                    $"Graphics{(gpu.VramBytes > 0 ? $", {Gb(gpu.VramBytes)} of video memory" : "")}. Local models run here.", gpu.Driver, OmnitekTag.Plain("PC hardware")));
            if (hardware.RamBytes > 0)
                items.Add(new InvItem(InvGroup.Hardware, "ram", $"{Gb(hardware.RamBytes)} memory", "System memory.", "", OmnitekTag.Plain("PC hardware")));
            if (hardware.SystemModel.Length > 0)
                items.Add(new InvItem(InvGroup.Hardware, "system", hardware.SystemModel, hardware.Board.Length > 0 ? $"Board {hardware.Board}." : "This PC.", "", OmnitekTag.Plain("PC hardware")));
            if (hardware.BiosVendor.Length > 0 || hardware.BiosVersion.Length > 0)
                items.Add(new InvItem(InvGroup.Firmware, "firmware", $"{hardware.FirmwareType} firmware".Trim(),
                    $"{hardware.BiosVendor}{(hardware.BiosDate.Length > 0 ? $", released {hardware.BiosDate}" : "")}.", hardware.BiosVersion, OmnitekTag.Plain("Firmware")));
        }
        return items;
    }

    private static bool SameServer(string a, string b) =>
        McpClientConfigs.MaskUrl(a).TrimEnd('/').Equals(McpClientConfigs.MaskUrl(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static InvAction ToInv(PcSetupAction a) => a switch
    {
        PcSetupAction.Connect => InvAction.Connect,
        PcSetupAction.Open => InvAction.Open,
        PcSetupAction.GetIt => InvAction.GetIt,
        _ => InvAction.None
    };

    public static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>The version number in a --version line ("git version 2.51.0.windows.1" gives 2.51.0.windows.1).</summary>
    public static string CliVersion(string line)
    {
        var m = Regex.Match(line, @"\d+(\.\d+)+[\w.+-]*");
        return m.Success ? m.Value.TrimEnd('.', ',') : "";
    }

    /// <summary>Services that are running or start automatically and do not ship in Windows (their program is outside the Windows folder).</summary>
    public static IEnumerable<ServiceInfo> ThirdPartyServices(IReadOnlyList<ServiceInfo> services)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return services
            .Where(s => s.State == "Running" || s.StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.PathName.Length > 0 && !s.PathName.Trim('"').StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                        && !s.PathName.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Installed programs as Settings, Apps lists them: one per name, no updates or framework packages.</summary>
    public static IEnumerable<InstalledApp> VisibleApps(IReadOnlyList<InstalledApp> apps) =>
        apps.Where(a => a.Name.Length > 0 && !Regex.IsMatch(a.Name, @"^(Update for|Security Update|Hotfix|Microsoft Visual C\+\+ 20\d\d .*(Runtime|Redistributable).*Minimum|Microsoft \.NET .*(Targeting|Host|Runtime -))", RegexOptions.IgnoreCase))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    // ---- devices ----

    internal sealed record DeviceGroup(string Id, string Label, string Detail);

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = "Logitech", ["0FD9"] = "Elgato", ["045E"] = "Microsoft", ["054C"] = "Sony", ["057E"] = "Nintendo",
        ["1532"] = "Razer", ["1B1C"] = "Corsair", ["1038"] = "SteelSeries", ["3434"] = "Keychron", ["1235"] = "Focusrite",
        ["0582"] = "Roland", ["0499"] = "Yamaha", ["28DE"] = "Valve", ["2833"] = "Meta", ["0BB4"] = "HTC", ["0951"] = "HyperX",
        ["1395"] = "EPOS", ["2B89"] = "Insta360", ["0B05"] = "ASUS", ["0D8C"] = "C-Media", ["05AC"] = "Apple", ["2516"] = "Cooler Master",
        ["1E7D"] = "ROCCAT", ["258A"] = "Glorious", ["3297"] = "ZSA", ["0738"] = "Mad Catz", ["0E6F"] = "PDP", ["24AE"] = "Rapoo",
        ["17EF"] = "Lenovo", ["413C"] = "Dell", ["03F0"] = "HP", ["0A12"] = "Bluetooth", ["1D6B"] = "Linux Foundation"
    };

    private static readonly Regex VidPid = new(@"VID[_&](?:0002)?([0-9A-F]{4}).*?PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Generic = new(@"^(HID|USB)[- ]|^HID-compliant|^Bluetooth Low Energy GATT|^USB Input Device|^USB Composite|^HID Keyboard Device|^Microsoft|^Standard|^Generic", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// One Gear item per physical device: HID collections, mouse and keyboard entries that share a vendor and product
    /// id are one device, named by its most specific entry (or vendor and kind when every entry is generic).
    /// Software devices (ROOT\, SWD\) are not hardware and are left out.
    /// </summary>
    public static List<DeviceGroup> GroupDevices(IReadOnlyList<PnpDevice> devices)
    {
        var groups = new List<DeviceGroup>();
        var real = devices.Where(d => !d.DeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) && !d.DeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
                                      && !d.DeviceId.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase));
        foreach (var g in real.GroupBy(d => VidPid.Match(d.DeviceId) is { Success: true } m ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant() : "name:" + d.Name))
        {
            var list = g.ToList();
            string vid = g.Key.StartsWith("name:") ? "" : g.Key[..4];
            string vendor = vid.Length > 0 && Vendors.TryGetValue(vid, out var v) ? v : list.Select(d => d.Manufacturer).FirstOrDefault(m => m.Length > 0 && !Generic.IsMatch(m) && !m.StartsWith('(')) ?? "";
            string kind = Kind(list);
            if (kind.Length == 0) continue;
            string? specific = list.Select(d => d.Name).FirstOrDefault(n => n.Length > 0 && !Generic.IsMatch(n));
            string label = specific ?? (vendor.Length > 0 ? $"{vendor} {kind.ToLowerInvariant()}" : kind);
            if (vendor.Length > 0 && !label.Contains(vendor, StringComparison.OrdinalIgnoreCase) && specific != null) label = $"{vendor} {label}";
            // A device with neither a name nor a vendor id says nothing an agent can use.
            if (label == kind && vid.Length == 0) continue;
            string id = vid.Length > 0 ? $"device id {g.Key}" : "";
            string detail = label.Contains(kind, StringComparison.OrdinalIgnoreCase)
                ? (id.Length > 0 ? $"{char.ToUpperInvariant(id[0])}{id[1..]}." : "On this PC.")
                : $"{kind}{(id.Length > 0 ? ", " + id : "")}.";
            groups.Add(new DeviceGroup(g.Key, label, detail));
        }
        // A Bluetooth headset shows once per profile (A2DP, Hands-Free); it is one device.
        groups = groups.Select(d => d with { Label = BluetoothProfile.Replace(d.Label, "").Trim() }).ToList();
        return groups.GroupBy(d => d.Label, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly Regex BluetoothProfile = new(@"\s+(A2DP SNK|A2DP SRC|Hands-Free( HF| AG)?( Audio)?|Stereo|Avrcp Transport)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Kind(List<PnpDevice> list)
    {
        var classes = list.Select(d => d.Class.ToLowerInvariant()).ToHashSet();
        string names = string.Join(" ", list.Select(d => d.Name)).ToLowerInvariant();
        if (classes.Contains("mouse")) return "Mouse";
        if (classes.Contains("keyboard")) return names.Contains("mouse") ? "Mouse" : "Keyboard";
        if (classes.Contains("xnacomposite") || names.Contains("game controller") || names.Contains("gamepad")) return "Controller";
        if (classes.Contains("camera") || classes.Contains("image")) return "Camera";
        if (classes.Contains("media")) return "Audio device";
        if (classes.Contains("hidclass"))
            // A vendor HID with no mouse, keyboard or controller collection: a control surface like Stream Deck, a headset's buttons.
            return list.Any(d => !Generic.IsMatch(d.Name)) || list.Any(d => VidPid.IsMatch(d.DeviceId)) ? "Input device" : "";
        return "";
    }

    // ---- summary ----

    /// <summary>
    /// The control pipe's answer: counts per Omnitek type and per group, the known tools' states, engines and MCP
    /// servers by name and transport, and what did not finish. No paths, commands, URLs or secrets.
    /// </summary>
    public static string SummaryJson(PcInventory inv)
    {
        var counts = new JsonObject();
        foreach (var (type, n) in inv.CountsByType.OrderBy(kv => kv.Key)) counts[type] = n;
        counts["Adapter // Connection"] = inv.VaultConnections;
        var groups = new JsonObject();
        foreach (var g in inv.Items.GroupBy(i => i.Group).OrderBy(g => g.Key)) groups[g.Key.ToString()] = g.Count();
        return new JsonObject
        {
            ["took_ms"] = (int)inv.Took.TotalMilliseconds,
            ["counts_by_type"] = counts,
            ["counts_by_group"] = groups,
            ["tools"] = new JsonArray(inv.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Tool.Id, ["state"] = t.State.ToString().ToLowerInvariant(), ["action"] = t.Action.ToString().ToLowerInvariant(),
                ["version"] = t.Version, ["in_vault"] = t.InVault
            }).ToArray()),
            ["engines"] = new JsonArray(inv.Items.Where(i => i.Source is EngineFinding).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = i.Label, ["port"] = ((EngineFinding)i.Source!).Port, ["models"] = ((EngineFinding)i.Source!).Models
            }).ToArray()),
            ["mcp_servers"] = new JsonArray(inv.Items.Where(i => i.Source is McpConfiguredServer).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = i.Label, ["transport"] = ((McpConfiguredServer)i.Source!).Transport, ["in_vault"] = i.InVault
            }).ToArray()),
            ["no_canon_home"] = new JsonArray(inv.Items.Where(i => !i.Tag.Canon).Select(i => i.Tag.Sub).Distinct().Select(s => (JsonNode)s!).ToArray()),
            ["unfinished"] = new JsonArray(inv.Unfinished.Select(u => (JsonNode)u!).ToArray())
        }.ToJsonString();
    }

    // ---- live readers (read-only) ----

    /// <summary>Microsoft Store apps for this user, from the AppModel package repository (no PowerShell, no WinRT).</summary>
    public static IReadOnlyList<InstalledApp> LiveStoreApps()
    {
        const string Repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        var apps = new List<InstalledApp>();
        using var key = Registry.CurrentUser.OpenSubKey(Repo);
        if (key == null) return apps;
        foreach (string full in key.GetSubKeyNames())
        {
            // Name_Version_Arch_ResourceId_PublisherId
            string[] parts = full.Split('_');
            if (parts.Length < 5 || parts[3].Length > 0) continue; // resource and split packages carry a resource id
            string name = parts[0];
            if (Regex.IsMatch(name, @"^(Microsoft\.(VCLibs|NET|UI\.Xaml|WindowsAppRuntime|Services\.Store|DirectX|WinAppRuntime|Windows\.|LanguageExperiencePack|Advertising|DesktopAppInstaller)|Windows\.|MicrosoftWindows\.|Microsoft\.Windows\.|NcsiUwpApp|InputApp|E2A4F912|F46D4000|1527c705|c5e2524a|windows\.)", RegexOptions.IgnoreCase)) continue;
            using var pkg = key.OpenSubKey(full);
            string display = pkg?.GetValue("DisplayName") as string ?? "";
            if (display.StartsWith('@') || display.Length == 0) display = Regex.Replace(name.Contains('.') ? name[(name.IndexOf('.') + 1)..] : name, "([a-z])([A-Z])", "$1 $2");
            apps.Add(new InstalledApp(display, parts[1], "", "", "Microsoft Store"));
        }
        return apps;
    }

    public static IReadOnlyList<ServiceInfo> LiveServices()
    {
        var list = new List<ServiceInfo>();
        using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service",
            new WmiOptions { Timeout = TimeSpan.FromSeconds(6), ReturnImmediately = true, Rewindable = false });
        foreach (ManagementObject o in searcher.Get())
            using (o)
                list.Add(new ServiceInfo(S(o, "Name"), S(o, "DisplayName"), S(o, "State"), S(o, "StartMode"), S(o, "PathName")));
        return list;
    }

    private static string S(ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; } catch (ManagementException) { return ""; }
    }

    // GetExtendedTcpTable: the listener table with owning process ids (what netstat -ano reads).
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>TCP listeners reachable on loopback (bound to loopback or to every address), with their process.</summary>
    public static IReadOnlyList<Listener>? LiveListeners()
    {
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { names[p.Id] = p.ProcessName; } catch (Exception) { } finally { p.Dispose(); }
        }
        var list = new List<Listener>();
        if (!ReadTable(2, 24, list, names) & !ReadTable(23, 56, list, names)) return null;
        return list;
    }

    private static bool ReadTable(int af, int rowSize, List<Listener> list, Dictionary<int, string> names)
    {
        const int TcpTableOwnerPidListener = 3;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidListener, 0);
        if (size <= 0) return false;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidListener, 0) != 0) return false;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + i * rowSize;
                IPAddress address;
                int portRaw, pid;
                if (af == 2)
                {
                    address = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                    portRaw = Marshal.ReadInt32(row, 8);
                    pid = Marshal.ReadInt32(row, 20);
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    address = new IPAddress(bytes);
                    portRaw = Marshal.ReadInt32(row, 20);
                    pid = Marshal.ReadInt32(row, 52);
                }
                if (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)) continue;
                int port = ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
                list.Add(new Listener(port, address.ToString(), pid, names.TryGetValue(pid, out var n) ? n : ""));
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Command line tools worth knowing about; each is looked up on PATH and asked for its version.</summary>
    public static readonly IReadOnlyList<string> KnownClis = new[]
    {
        "git", "gh", "node", "npm", "bun", "deno", "python", "py", "uv", "pip", "docker", "winget", "claude", "codex", "gemini",
        "ollama", "lms", "code", "cursor", "dotnet", "java", "go", "rustc", "cargo", "pwsh", "wsl", "az", "aws", "gcloud",
        "kubectl", "terraform", "ffmpeg", "nvidia-smi", "vercel", "wrangler", "railway", "supabase", "choco", "scoop"
    };

    public static async Task<IReadOnlyList<CliFound>> LiveClisAsync(CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(16);
        // Every tool found on PATH is listed; versions are best effort inside one deadline, so slow CLIs never hold the scan.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        var tasks = KnownClis.Select(async name =>
        {
            string? path = Resolve(name);
            if (path == null) return null;
            // App execution aliases (WindowsApps) can open the Store instead of answering; only winget's is asked.
            bool alias = path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            if (alias && name != "winget") return new CliFound(name, path, "");
            if (name == "wsl") return new CliFound(name, path, ""); // wsl --version prints UTF-16 and may start the WSL service
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await gate.WaitAsync(left, ct).ConfigureAwait(false)) return new CliFound(name, path, "");
            try
            {
                string args = name is "go" ? "version" : name is "java" ? "-version" : "--version";
                var budget = deadline - DateTime.UtcNow;
                if (budget > TimeSpan.FromSeconds(3)) budget = TimeSpan.FromSeconds(3);
                return new CliFound(name, path, budget <= TimeSpan.Zero ? "" : await FirstLineAsync(path, args, budget, ct).ConfigureAwait(false));
            }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<CliFound>().ToList();
    }

    private static string? Resolve(string name)
    {
        foreach (string ext in new[] { ".exe", ".cmd", ".bat" })
            if (PcSetupScan.FindOnPath(name + ext) is { } path) return path;
        return null;
    }

    private static async Task<string> FirstLineAsync(string path, string args, TimeSpan timeout, CancellationToken ct)
    {
        bool shim = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = shim ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{path}\" {args}\"") : new ProcessStartInfo(path, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            proc.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return "";
            }
            string text = await stdout.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) text = await stderr.ConfigureAwait(false);
            string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return line.Length > 80 ? line[..80] : line;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            return "";
        }
    }

    public static IReadOnlyList<PnpDevice> LiveDevices()
    {
        var list = new List<PnpDevice>();
        using var searcher = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, Manufacturer, PNPClass, DeviceID FROM Win32_PnPEntity WHERE Present = TRUE AND (PNPClass = 'Mouse' OR PNPClass = 'Keyboard' OR PNPClass = 'HIDClass' OR PNPClass = 'Camera' OR PNPClass = 'Image' OR PNPClass = 'MEDIA' OR PNPClass = 'XnaComposite')",
            new WmiOptions { Timeout = TimeSpan.FromSeconds(6), ReturnImmediately = true, Rewindable = false });
        foreach (ManagementObject o in searcher.Get())
            using (o)
                list.Add(new PnpDevice(S(o, "Name"), S(o, "Manufacturer"), S(o, "PNPClass"), S(o, "DeviceID")));
        return list;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out uint firmwareType);

    public static HardwareFacts LiveHardware()
    {
        var options = new WmiOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false };
        List<Dictionary<string, string>> Query(string wql, string scope = "root\\CIMV2")
        {
            var rows = new List<Dictionary<string, string>>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wql, options);
                foreach (ManagementObject o in searcher.Get())
                    using (o)
                        rows.Add(o.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => p.Value switch
                        {
                            ushort[] chars => new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()),
                            var v => v?.ToString()?.Trim() ?? ""
                        }));
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
            return rows;
        }

        var cpus = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor")
            .Select(r => $"{r["Name"]} ({r["NumberOfCores"]} cores, {r["NumberOfLogicalProcessors"]} threads)").ToList();
        var vram = VideoMemoryByName();
        var gpus = Query("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController")
            .Where(r => !Regex.IsMatch(r["Name"], "Basic Display|Virtual|Mixed Reality|Remote Display|Indirect|Parsec|Meta Virtual", RegexOptions.IgnoreCase))
            .Select(r => new GpuInfo(r["Name"], vram.TryGetValue(r["Name"], out long q) ? q : long.TryParse(r["AdapterRAM"], out long a) ? a : 0, r["DriverVersion"])).ToList();
        var cs = Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var monitorNames = Query("SELECT UserFriendlyName FROM WmiMonitorID", "root\\WMI").Select(r => r.GetValueOrDefault("UserFriendlyName", "")).Where(n => n.Length > 0).ToList();
        var screens = Screen.AllScreens;
        var displays = screens.Select((s, i) => new DisplayInfo(i < monitorNames.Count ? monitorNames[i] : $"Display {i + 1}", s.Bounds.Width, s.Bounds.Height, s.Primary)).ToList();
        string firmware = GetFirmwareType(out uint ft) ? ft switch { 1 => "BIOS", 2 => "UEFI", _ => "" } : "";
        string date = bios?.GetValueOrDefault("ReleaseDate", "") is { Length: >= 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..8]}" : "";
        string model = cs == null ? "" : $"{cs.GetValueOrDefault("Manufacturer", "")} {cs.GetValueOrDefault("Model", "")}".Trim();
        if (model.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || model.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)) model = "";
        return new HardwareFacts(
            cpus, gpus,
            long.TryParse(cs?.GetValueOrDefault("TotalPhysicalMemory", ""), out long ram) ? ram : 0,
            model,
            board == null ? "" : $"{board.GetValueOrDefault("Manufacturer", "")} {board.GetValueOrDefault("Product", "")}".Trim(),
            displays,
            bios?.GetValueOrDefault("Manufacturer", "") ?? "", bios?.GetValueOrDefault("SMBIOSBIOSVersion", "") ?? "", date, firmware);
    }

    /// <summary>
    /// Video memory per adapter from the display class key (HardwareInformation.qwMemorySize), since WMI's AdapterRAM
    /// stops at 4 GB.
    /// </summary>
    private static Dictionary<string, long> VideoMemoryByName()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return result;
            foreach (string sub in cls.GetSubKeyNames().Where(s => s.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string name) continue;
                    long bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        _ => k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (uint)i,
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                            _ => 0
                        }
                    };
                    if (bytes > 0 && (!result.TryGetValue(name, out long have) || bytes > have)) result[name] = bytes;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return result;
    }
}
