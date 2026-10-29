using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

public sealed class ArkStationStatus
{
    public string ArkRoot { get; init; } = ArkStationClient.DefaultArkRoot;
    public bool KeyLoaded { get; init; }
    public bool GatewayOnline { get; init; }
    public bool NebiusKeyLoaded { get; init; }
    public bool StandaloneMode => !GatewayOnline;
    public bool InputSpineOnline { get; init; }
    public bool PcModzOnline { get; init; }
    public string ActiveMousePackage { get; init; } = "universal-mouse";
    public string ActiveCursorProfile { get; init; } = "smooth-cursor";
}

public sealed record ArkPodInboxInfo(
    string PodId,
    string Label,
    string InboxChannel,
    string OrchestratorAgentId,
    string OrchestratorLabel,
    string Zone,
    string AccentToken,
    string Description);

public sealed record OmnitekAutoDetectInfo(
    string ItemType,
    string? ItemSubtype,
    string Confidence,
    string Rationale,
    string RecommendedPodId)
{
    public string DisplayBadge => string.IsNullOrEmpty(ItemSubtype)
        ? ItemType.ToUpperInvariant()
        : $"{ItemType.ToUpperInvariant()} / {ItemSubtype.ToUpperInvariant()}";
}

/// <summary>
/// Connects Omniwand to ArkStation Model Gateway (:8788), Input Studio / Spine (:8842),
/// PC-Modz (:8830), Antigravity CLI (Wiz), and AInbox Pod Inboxes when available, or
/// operates in Standalone Mode directly against Nebius Token Factory (NVIDIA Nemotron).
/// </summary>
/// <summary>What a GrimX Spell cast through Input Spine did; each outcome has its own toast.</summary>
public enum SpellCastOutcome { Cast, NoBinding, Refused, Unreachable, Standalone }

public sealed class ArkStationClient
{
    // Optional ArkStation integration. A public install has no Ark checkout, so these resolve from ARK_ROOT and
    // ARK_RUNTIMES_ROOT, and otherwise point at folders that simply do not exist (standalone Nebius mode).
    public static readonly string DefaultArkRoot =
        Environment.GetEnvironmentVariable("ARK_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "ark-root-not-configured");
    private static readonly string RuntimesRoot =
        Environment.GetEnvironmentVariable("ARK_RUNTIMES_ROOT") ?? Path.Combine(Path.GetDirectoryName(DefaultArkRoot.TrimEnd('\\', '/')) ?? DefaultArkRoot, "runtimes");
    public static readonly string DefaultCursorsDir = Path.Combine(RuntimesRoot, "cursors");
    public static readonly string DefaultInputStudioStateDir = Path.Combine(RuntimesRoot, "input-studio");
    public const string DefaultGatewayUrl = "http://127.0.0.1:8788";
    public const string DefaultInputSpineUrl = "http://127.0.0.1:8842";
    public const string DefaultPcModzUrl = "http://127.0.0.1:8830";
    public const string DefaultNebiusBaseUrl = "https://api.tokenfactory.nebius.com/v1";
    public const string DefaultModelSlug = "ark/kimi";
    /// <summary>Gemini 4 Argon as Antigravity names it (`agy models`); "gemini-argon" was never a real id.</summary>
    public const string DefaultWizModelSlug = "gemini-4-argon-medium";

    /// <summary>Default NVIDIA Nemotron models on Nebius Token Factory (fast tier first for cursor &amp; dictation calls).</summary>
    public const string DefaultNemotronFastModel = "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B";
    public const string DefaultNemotronSuperModel = "nvidia/nemotron-3-super-120b-a12b";

    public static readonly IReadOnlyList<string> BuiltInNemotronModels = new[]
    {
        DefaultNemotronFastModel,
        "nvidia/Nemotron-3_5-Lightning",
        DefaultNemotronSuperModel,
        "nvidia/Nemotron-3.5-Super-VL",
        "nvidia/Nemotron-3-Ultra-550b-a55b"
    };

    public static readonly IReadOnlyList<ArkPodInboxInfo> PodInboxes = new List<ArkPodInboxInfo>
    {
        new("nexus", "Nexus", "ainbox:pod:nexus", "gala", "Gala (Co-op Orchestrator)", "operate", "patina",
            "Command control, Veritas canon records, Aegis gates, and Hivemind session routing."),
        new("eden", "Eden", "ainbox:pod:eden", "arbiter", "Arbiter (Eden & SolaTree Conductor)", "operate", "patina",
            "Living architecture, SolaTree worktree lanes, infrastructure topology, and grafts."),
        new("cortex", "Cortex", "ainbox:pod:cortex", "scholar", "Scholar (Research & Curriculum Conductor)", "operate", "patina",
            "Deep research, literature synthesis, datasets, training curricula, and memory indexing."),
        new("magixlab", "MagiXLab", "ainbox:pod:magixlab", "muse", "Muse (Creative & Media Conductor)", "create", "coral",
            "Multimodal media synthesis, Phosphor visual exploration, motion studies, and reference intake."),
        new("forge", "4GE", "ainbox:pod:forge", "wiz", "Wiz (GrimX & Crafting Conductor)", "create", "coral",
            "GrimX Spell crafting (Quick Casts, Combos, Rituals, Symphonies, Ultimas, Charms, Summons), Augments, and Blueprints."),
        new("anvl", "Anvl", "ainbox:pod:anvl", "fabrix", "Fabrix (Fabrication & Build Conductor)", "create", "coral",
            "Co-LabStation fabrication, hardware/Gear integration, CAD/3D print Skrypts, and physical builds."),
        new("bazaar", "Bazaar", "ainbox:pod:bazaar", "herald", "Herald (Distribution & Exchange Conductor)", "distribute", "seal",
            "Pack publishing, marketplace cards, external distribution, and Guild exchange.")
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    public string ArkRoot { get; }
    /// <summary>The input package of the mouse in use: MX Master 4's Actions Ring package, or the universal one.</summary>
    public string ActiveMousePackageId => MouseHardwareAndWandEngine.ActiveMouse.PackageId == "mx-master-4" ? "mx-master-4" : "universal-mouse";
    public string ActiveCursorProfile { get; private set; } = "smooth-cursor";
    public bool LastGatewayOnline { get; private set; }

    public ArkStationClient(string? arkRoot = null)
    {
        ArkRoot = !string.IsNullOrWhiteSpace(arkRoot) && Directory.Exists(arkRoot)
            ? arkRoot
            : DefaultArkRoot;
        SelectedModel = DefaultNemotronFastModel;
    }

    public bool ForceStandaloneMode =>
        string.Equals(Environment.GetEnvironmentVariable("OMNIWAND_STANDALONE"), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("OMNIWAND_STANDALONE"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this machine runs its owner's Ark: a configured Ark root or a gateway inbound key. A public install
    /// has neither, so Ark-only surfaces (Input Spine, the Ark gateway, Augment Inventory) stay out of its way.
    /// </summary>
    public bool ArkInstalled => !ForceStandaloneMode && (Directory.Exists(ArkRoot) || HasArkGatewayCredentials());

    public string GetNebiusBaseUrl()
    {
        string? custom =
            Environment.GetEnvironmentVariable("NEBIUS_BASE_URL") ??
            Environment.GetEnvironmentVariable("TOKEN_FACTORY_BASE_URL");
        return string.IsNullOrWhiteSpace(custom)
            ? DefaultNebiusBaseUrl
            : custom.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Reads the Nebius Token Factory API key from environment variables or Windows Credential Manager.
    /// Never reads from or writes to source files, config files, logs, or screenshots.
    /// </summary>
    public string ReadNebiusApiKey()
    {
        foreach (string envName in new[] { "NEBIUS_API_KEY", "TOKEN_FACTORY_API_KEY", "OMNIWAND_NEBIUS_API_KEY" })
        {
            string? val = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
            val = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
            val = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }

        foreach (string target in new[] { "Omniwand:NebiusTokenFactory", "NebiusTokenFactory", "NEBIUS_API_KEY", "TOKEN_FACTORY_API_KEY" })
        {
            string? secret = TryReadWindowsCredential(target);
            if (!string.IsNullOrWhiteSpace(secret))
                return secret.Trim();
        }

        // Also check .arkhive/keymaster/agent-plugin-creds.json when running on an ArkHive-equipped host
        try
        {
            string credPath = Path.Combine(ArkRoot, ".arkhive", "keymaster", "agent-plugin-creds.json");
            if (File.Exists(credPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(credPath));
                if (doc.RootElement.TryGetProperty("agentPluginEnv", out var env) &&
                    env.ValueKind == JsonValueKind.Object &&
                    env.TryGetProperty("NEBIUS_API_KEY", out var nk) &&
                    nk.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nk.GetString()))
                {
                    return nk.GetString()!.Trim();
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public static bool HasWinCredVaultBundle() =>
        !string.IsNullOrWhiteSpace(TryReadWindowsCredential("ArkKeymaster:VaultBundle:Manifest")) ||
        !string.IsNullOrWhiteSpace(TryReadWindowsCredential("Omniwand:NebiusTokenFactory"));

    private const string GatewayInboundKeyTarget = "Omniwand:GatewayInboundKey";

    public bool SaveTokenToWindowsVault(string rawToken)
    {
        string trimmed = rawToken.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        // An Ark gateway key (ark_ or hm_) goes to Omniwand:GatewayInboundKey; anything else is a Nebius Token Factory
        // key (Omniwand:NebiusTokenFactory). Omniwand:HivemindToken belongs to the Hivemind OAuth sign-in (HivemindClient).
        bool isArkToken = trimmed.StartsWith("ark_", StringComparison.OrdinalIgnoreCase) ||
                          trimmed.StartsWith("hm_", StringComparison.OrdinalIgnoreCase);

        if (isArkToken)
        {
            Environment.SetEnvironmentVariable("ARK_MODEL_GATEWAY_INBOUND_KEY", trimmed);
            return TryWriteWindowsCredential(GatewayInboundKeyTarget, trimmed);
        }
        else
        {
            Environment.SetEnvironmentVariable("NEBIUS_API_KEY", trimmed);
            return TryWriteWindowsCredential("Omniwand:NebiusTokenFactory", trimmed);
        }
    }

    internal static bool TryWriteWindowsCredential(string targetName, string secret)
    {
        IntPtr targetPtr = IntPtr.Zero;
        IntPtr userPtr = IntPtr.Zero;
        IntPtr blobPtr = IntPtr.Zero;
        try
        {
            byte[] blobBytes = Encoding.Unicode.GetBytes(secret);
            targetPtr = Marshal.StringToCoTaskMemUni(targetName);
            userPtr = Marshal.StringToCoTaskMemUni("ArkKeymaster");
            blobPtr = Marshal.AllocCoTaskMem(blobBytes.Length);
            Marshal.Copy(blobBytes, 0, blobPtr, blobBytes.Length);

            var cred = new CREDENTIALW
            {
                Flags = 0,
                Type = 1, // CRED_TYPE_GENERIC
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                CredentialBlobSize = (uint)blobBytes.Length,
                CredentialBlob = blobPtr,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr
            };
            return CredWriteW(ref cred, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    private bool HasArkGatewayCredentials() => !string.IsNullOrEmpty(ReadInboundKey());

    internal static string? TryReadWindowsCredential(string targetName)
    {
        if (!CredReadW(targetName, 1 /* CRED_TYPE_GENERIC */, 0, out IntPtr credPtr) || credPtr == IntPtr.Zero)
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0)
                return null;

            byte[] bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);

            // Windows cmdkey / generic credentials commonly store UTF-16LE when written via CredWriteW/cmdkey, or UTF-8 when raw.
            if (bytes.Length >= 2 && bytes[1] == 0)
            {
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW([In] ref CREDENTIALW userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    /// <summary>Deletes a generic Windows credential; true when it is gone (deleted, or was never there).</summary>
    internal static bool TryDeleteWindowsCredential(string targetName) =>
        CredDeleteW(targetName, 1, 0) || Marshal.GetLastWin32Error() == 1168; // ERROR_NOT_FOUND

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    public IReadOnlyList<string> ListCursorProfiles()
    {
        try
        {
            string profileDir = Path.Combine(DefaultCursorsDir, "profiles");
            if (!Directory.Exists(profileDir)) return new[] { "smooth-cursor" };
            var files = Directory.GetFiles(profileDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n)
                .ToList();
            return files.Count > 0 ? files : new[] { "smooth-cursor" };
        }
        catch
        {
            return new[] { "smooth-cursor" };
        }
    }

    public string CycleCursorProfile()
    {
        var profiles = ListCursorProfiles();
        int idx = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            if (string.Equals(profiles[i], ActiveCursorProfile, StringComparison.OrdinalIgnoreCase))
            {
                idx = (i + 1) % profiles.Count;
                break;
            }
        }
        ActiveCursorProfile = profiles[idx];
        return ActiveCursorProfile;
    }

    public string ReadInboundKey()
    {
        if (ForceStandaloneMode) return string.Empty;

        string? envKey = Environment.GetEnvironmentVariable("ARK_MODEL_GATEWAY_INBOUND_KEY") ??
                         Environment.GetEnvironmentVariable("ARK_HIVEMIND_TOKEN");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

        string? winCredToken = TryReadWindowsCredential(GatewayInboundKeyTarget);
        if (!string.IsNullOrWhiteSpace(winCredToken)) return winCredToken.Trim();

        try
        {
            string credPath = Path.Combine(ArkRoot, ".arkhive", "keymaster", "agent-plugin-creds.json");
            if (!File.Exists(credPath)) return string.Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(credPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("ARK_MODEL_GATEWAY_INBOUND_KEY", out var direct) &&
                direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("agentPluginEnv", out var env) &&
                env.ValueKind == JsonValueKind.Object &&
                env.TryGetProperty("ARK_MODEL_GATEWAY_INBOUND_KEY", out var nested) &&
                nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Ignore read failure and return empty
        }
        return string.Empty;
    }

    public async Task<ArkStationStatus> CheckStatusAsync()
    {
        string arkKey = ReadInboundKey();
        string nebiusKey = ReadNebiusApiKey();
        bool gateway = !ForceStandaloneMode &&
                       (await ProbeHttpAsync(DefaultGatewayUrl + "/model-gateway/health") ||
                        await ProbeHttpAsync(DefaultGatewayUrl + "/health"));
        bool spine = !ForceStandaloneMode && await ProbeHttpAsync(DefaultInputSpineUrl + "/api/spine/config");
        bool pcModz = !ForceStandaloneMode && await ProbeHttpAsync(DefaultPcModzUrl + "/health");

        LastGatewayOnline = gateway;
        if (!gateway &&
            (string.Equals(SelectedModel, DefaultWizModelSlug, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(SelectedModel, DefaultModelSlug, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedModel = DefaultNemotronFastModel;
        }

        return new ArkStationStatus
        {
            ArkRoot = ArkRoot,
            KeyLoaded = !string.IsNullOrEmpty(arkKey),
            GatewayOnline = gateway,
            NebiusKeyLoaded = !string.IsNullOrEmpty(nebiusKey),
            InputSpineOnline = spine,
            PcModzOnline = pcModz,
            ActiveMousePackage = ActiveMousePackageId,
            ActiveCursorProfile = ActiveCursorProfile
        };
    }

    private async Task<bool> ProbeHttpAsync(string url)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            using var resp = await _http.GetAsync(url, cts.Token);
            return (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public static string? CaptureRegionAroundCursor(int centerX, int centerY, int width = 512, int height = 384)
    {
        try
        {
            int left = centerX - width / 2;
            int top = centerY - height / 2;
            using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static string? CaptureRectangleRegion(Rectangle screenRect)
    {
        try
        {
            int w = Math.Max(16, screenRect.Width);
            int h = Math.Max(16, screenRect.Height);
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dispatches a GrimX Spell cast to Input Spine (:8842/api/spine/event). Ok is true only when
    /// Spine matched a binding for the gesture; in standalone mode (when Input Studio is absent),
    /// degrades gracefully and reports a clean warning message for the Phosphor toast.
    /// </summary>
    public async Task<(SpellCastOutcome Outcome, string Output)> TriggerGrimXFlowAsync(
        string flowKind,
        string verb,
        Point cursorPos,
        string? cursorRole = null)
    {
        if (!ArkInstalled)
        {
            return (SpellCastOutcome.Standalone, $"No local Spell runtime, so Wiz crafts {flowKind} with Nemotron.");
        }

        try
        {
            var payload = new
            {
                trigger = new
                {
                    kind = "gesture",
                    match = $"grimx/{flowKind.ToLowerInvariant()}"
                },
                context = new Dictionary<string, object>
                {
                    ["flowKind"] = flowKind,
                    ["verb"] = verb,
                    ["x"] = cursorPos.X,
                    ["y"] = cursorPos.Y,
                    ["cursorRole"] = cursorRole ?? "Arrow"
                }
            };
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{DefaultInputSpineUrl}/api/spine/event", content, cts.Token);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return (SpellCastOutcome.Refused, $"Input Spine refused the {flowKind} cast (HTTP {(int)resp.StatusCode}), so nothing was cast.");
            }

            bool matched = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                matched = doc.RootElement.TryGetProperty("matched", out var m) && m.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                // Unparseable reply: treat as not matched.
            }

            return matched
                ? (SpellCastOutcome.Cast, $"{verb} {flowKind} dispatched through Input Spine.")
                : (SpellCastOutcome.NoBinding, $"No Input Spine binding for grimx/{flowKind.ToLowerInvariant()}, so nothing was cast. Bind it in Input Studio.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (SpellCastOutcome.Unreachable, "Input Spine (:8842) is unreachable, so nothing was cast. Start the Ark runtime and cast again.");
        }
    }

    /// <summary>Whether Keymaster holds the inbound key the HUD needs to ask through the Ark gateway.</summary>
    public bool HasGatewayInboundKey => HasArkGatewayCredentials();

    /// <summary>
    /// Invokes Wiz (4GE Workflow-Crafting &amp; GrimX Governance Agent).
    /// Checks ArkStation routes first when reachable; when offline or when a Nemotron model is selected,
    /// calls Nebius Token Factory directly with NVIDIA Nemotron.
    /// </summary>
    public async Task<(bool Ok, string Runtime, string Model, string Output)> AskWizGeminiArgonAsync(
        string prompt,
        string flowKind = "flow",
        string? imageDataUrl = null,
        string? model = null)
    {
        // Wiz keeps its own model choice; other surfaces use the shared SelectedModel.
        string selected = model ?? SelectedModel;
        string cliModel = CliModelSlug(selected);
        bool isNemotronSelected = IsNemotronModel(selected);

        string wizSystem =
            "You are Wiz, the 4GE Workflow-Crafting and GrimX Catalog-Governance Agent inside Holodex. " +
            "Focus strictly on GrimX Spells. Spell kinds: Quick Cast (Cast), Combo (Chain), Ritual (Perform, scheduled or long-running), Symphony (Conduct), Ultima (Pursue), Charm (Bind), and Summon (Summon). " +
            $"Active Spell type: {SpellTypeName(flowKind)}.\n\nOperator Request: {prompt}";

        if (!ForceStandaloneMode && !isNemotronSelected)
        {
            // 1. Try Input Studio's /api/omniwand/wiz/chat endpoint first
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                var payload = new { prompt, flowKind, model = selected };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync($"{DefaultInputSpineUrl}/api/omniwand/wiz/chat", content, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                    {
                        string rt = root.TryGetProperty("runtime", out var r) ? r.GetString() ?? "subscription-gemini" : "subscription-gemini";
                        string mdl = root.TryGetProperty("model", out var m) ? m.GetString() ?? selected : selected;
                        string txt = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            return (true, rt, mdl, txt);
                        }
                    }
                }
            }
            catch
            {
                // Fall through to direct Antigravity CLI, Ark Model Gateway, or Nebius Token Factory
            }

            // 2. Direct Antigravity CLI (`agy` / `gemini`) with the selected `--model` when ArkRoot exists
            if (Directory.Exists(ArkRoot))
            {
                foreach (string cli in new[] { "agy", "gemini" })
                {
                    try
                    {
                        var psi = new ProcessStartInfo(cli)
                        {
                            WorkingDirectory = ArkRoot,
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        psi.ArgumentList.Add("--model");
                        psi.ArgumentList.Add(cliModel);
                        psi.ArgumentList.Add("-p");
                        psi.ArgumentList.Add(wizSystem);
                        using var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                            string stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                            await proc.WaitForExitAsync(cts.Token);
                            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                            {
                                return (true, $"subscription-gemini ({cli})", selected, stdout.Trim());
                            }
                        }
                    }
                    catch
                    {
                        // Try next route
                    }
                }
            }
        }

        // 3. Fallback to Ark Model Gateway (:8788) when online, or Nebius Token Factory (Nemotron) in standalone mode.
        bool gatewayOnline = !ForceStandaloneMode &&
                             (await ProbeHttpAsync(DefaultGatewayUrl + "/model-gateway/health") ||
                              await ProbeHttpAsync(DefaultGatewayUrl + "/health"));
        if (gatewayOnline && !isNemotronSelected)
        {
            bool gatewayNative = selected.StartsWith("ark/", StringComparison.OrdinalIgnoreCase);
            string gatewayModel = gatewayNative ? selected : DefaultModelSlug;
            var (gwOk, gwOut) = await AskArkGatewayAsync(wizSystem, imageDataUrl, gatewayModel);
            return (gwOk, gatewayNative ? "ark-model-gateway" : $"ark-model-gateway ({gatewayModel})", gatewayModel, gwOut);
        }

        string nemotronModel = ResolveNemotronModel(selected);
        var (nbOk, nbOut) = await AskNebiusTokenFactoryAsync(wizSystem, imageDataUrl, nemotronModel);
        return (nbOk, "nebius-token-factory", nemotronModel, nbOut);
    }

    public static bool IsNemotronModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("nemotron", StringComparison.OrdinalIgnoreCase) ||
         model.StartsWith("nvidia/", StringComparison.OrdinalIgnoreCase));

    public static string ResolveNemotronModel(string? model) =>
        IsNemotronModel(model) ? model! : DefaultNemotronFastModel;

    /// <summary>Model chosen in the HUD and other chat pickers; shared by those surfaces. Defaults to NVIDIA Nemotron fast tier. Wiz keeps its own agent model (WizAgent.DefaultReasoningModel).</summary>
    public string SelectedModel { get; set; } = DefaultNemotronFastModel;

    /// <summary>The real Hivemind (remote MCP and its OAuth sign-in); the Holodex session and work orders use it.</summary>
    public HivemindClient Hivemind { get; } = new();

    /// <summary>CLI form of the selection (no "ark/" route prefix).</summary>
    private static string CliModelSlug(string model) =>
        model.StartsWith("ark/", StringComparison.OrdinalIgnoreCase) ? model[4..] : model;

    /// <summary>Gateway route form of the selection (always "ark/&lt;slug&gt;").</summary>
    private string GatewayModelSlug() =>
        SelectedModel.Contains('/') ? SelectedModel : "ark/" + SelectedModel;

    /// <summary>
    /// Models offered by the pickers:
    /// - Always puts NVIDIA Nemotron models on Nebius Token Factory first.
    /// - When Ark Model Gateway (:8788) is reachable: appends live gateway `/v1/models` and `ark/kimi`.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync()
    {
        bool gatewayOnline = !ForceStandaloneMode &&
                             (await ProbeHttpAsync(DefaultGatewayUrl + "/model-gateway/health") ||
                              await ProbeHttpAsync(DefaultGatewayUrl + "/health"));
        LastGatewayOnline = gatewayOnline;

        if (gatewayOnline)
        {
            var models = new List<string>(BuiltInNemotronModels);
            if (!models.Contains(DefaultModelSlug, StringComparer.OrdinalIgnoreCase))
                models.Add(DefaultModelSlug);
            try
            {
                string key = ReadInboundKey();
                if (!string.IsNullOrEmpty(key))
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{DefaultGatewayUrl}/v1/models");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    using var resp = await _http.SendAsync(req, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in data.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s &&
                                    !models.Contains(s, StringComparer.OrdinalIgnoreCase))
                                {
                                    models.Add(s);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Keep defaults
            }
            return models;
        }

        // Standalone mode: show NVIDIA Nemotron models from Nebius Token Factory
        var standaloneModels = new List<string>(BuiltInNemotronModels);
        if (!IsNemotronModel(SelectedModel))
        {
            SelectedModel = DefaultNemotronFastModel;
        }

        string nebiusKey = ReadNebiusApiKey();
        if (!string.IsNullOrEmpty(nebiusKey))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{GetNebiusBaseUrl()}/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nebiusKey);
                using var resp = await _http.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s &&
                                IsNemotronModel(s) &&
                                !standaloneModels.Contains(s, StringComparer.OrdinalIgnoreCase))
                            {
                                standaloneModels.Add(s);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Offline or unreachable: keep BuiltInNemotronModels.
            }
        }

        return standaloneModels;
    }

    /// <summary>
    /// Display name for a Spell type id. Ids stay on the wire contract (slot:spell,
    /// grimx/spell); "spell" is the Quick Cast kind and "flow" is every Spell.
    /// </summary>
    public static string SpellTypeName(string id) => id.ToLowerInvariant() switch
    {
        "flow" => "All Spells",
        "spell" => "Quick Cast",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id.ToLowerInvariant())
    };

    /// <summary>
    /// Deterministic local + remote Omnitek item auto-detector matching @ark/ainbox-core.
    /// </summary>
    public OmnitekAutoDetectInfo AutoDetectOmnitekItem(string? fileName, string? text, string? intent)
    {
        string combined = $"{fileName} {text} {intent}".ToLowerInvariant();
        string ext = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();

        if (combined.Contains("ultima") || combined.Contains("deluge"))
            return new("flow", "ultima", "high", "Detected GrimX Ultima keywords.", "forge");
        if (combined.Contains("symphony") || combined.Contains("orchestra"))
            return new("flow", "symphony", "high", "Detected GrimX Symphony keywords.", "forge");
        if (combined.Contains("ritual") || combined.Contains("schedule") || combined.Contains("cron"))
            return new("flow", "ritual", "high", "Detected GrimX Ritual keywords.", "forge");
        if (combined.Contains("charm") || combined.Contains("relic") || combined.Contains("enchant"))
            return new("flow", "charm", "high", "Detected GrimX Charm keywords.", "forge");
        if (combined.Contains("summon") || combined.Contains("drone") || combined.Contains("clone"))
            return new("flow", "summon", "high", "Detected GrimX Summon keywords.", "forge");
        if (combined.Contains("combo") || combined.Contains("chain"))
            return new("flow", "combo", "high", "Detected GrimX Combo keywords.", "forge");
        if (combined.Contains("spell") || combined.Contains("cast"))
            return new("flow", "spell", "high", "Detected GrimX Quick Cast keywords.", "forge");

        if (ext is "tsx" or "jsx" or "css" || combined.Contains("component") || combined.Contains("phosphor") || combined.Contains("widget"))
            return new("augment", "component", "high", "Detected Phosphor UI Component source or widget reference.", "forge");
        if (ext == "svg" || System.Text.RegularExpressions.Regex.IsMatch(combined, @"\bdesign[- ]tokens?\b|\btokens?\.(css|json|ts)\b") || combined.Contains("icon") || combined.Contains("badge"))
            return new("augment", "element", "high", "Detected atomic visual Element or token.", "magixlab");
        if (combined.Contains("modz") || combined.Contains("mcp"))
            return new("mod", null, "high", "Detected an MCP Mod capability bundle.", "forge");
        if (combined.Contains("adapter") || combined.Contains("webhook") || combined.Contains("connector"))
            return new("augment", "adapter", "high", "Detected transport or protocol Adapter.", "eden");
        // Artifact is its own Omnitek item type (2026-10-23): App (Desktop App, Web App) and Deliverable.
        if (ext is "exe" or "msi" or "msix" or "appx")
            return new("artifact", "desktop-app", "high", "Detected a standalone Desktop App Artifact.", "forge");
        if (ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "mp4" or "webm" or "wav" or "mp3")
            return new("artifact", "deliverable", "high", "Detected a captured or generated media Deliverable.", "magixlab");
        if (ext is "pdf" or "docx" or "pptx" or "xlsx" or "zip")
            return new("artifact", "deliverable", "high", "Detected an exported document or bundle Deliverable.", "nexus");

        if (ext is "csv" or "jsonl" or "parquet" or "db" || combined.Contains("dataset") || combined.Contains("corpus"))
            return new("scroll", "dataset", "high", "Detected structured Dataset corpus.", "cortex");
        if (ext is "ps1" or "sh" or "py" or "ts" or "cs" or "bamf" || combined.Contains("skrypt") || combined.Contains("script"))
            return new("scroll", "skrypt", "high", "Detected executable Skrypt or runbook.", "eden");
        if (combined.Contains("contract") || combined.Contains("schema") || combined.Contains("zod"))
            return new("scroll", "contract", "high", "Detected typed schema Contract.", "eden");
        if (combined.Contains("curriculum") || combined.Contains("syllabus"))
            return new("scroll", "curriculum", "high", "Detected training Curriculum.", "cortex");
        if (ext is "stl" or "step" or "3mf" or "gcode" || combined.Contains("hardware") || combined.Contains("gear"))
            return new("gear", null, "high", "Detected physical Gear or CAD fabrication payload.", "anvl");

        return new("scroll", "brief", "medium", "Classified as operational Scroll Brief.", "nexus");
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>SHA-256 of a file's bytes, streamed. Null when the file cannot be read.</summary>
    public static string? Sha256File(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Calls ArkStation Model Gateway (:8788) first when reachable. When unreachable (or when a Nemotron
    /// model is explicitly chosen), calls Nebius Token Factory directly using NVIDIA Nemotron.
    /// </summary>
    public async Task<(bool Ok, string Output)> AskArkGatewayAsync(string prompt, string? imageDataUrl = null, string? model = null)
    {
        string chosen = model ?? SelectedModel;
        bool gatewayOnline = !ForceStandaloneMode &&
                             (await ProbeHttpAsync(DefaultGatewayUrl + "/model-gateway/health") ||
                              await ProbeHttpAsync(DefaultGatewayUrl + "/health"));
        LastGatewayOnline = gatewayOnline;

        if (!gatewayOnline || IsNemotronModel(chosen))
        {
            return await AskNebiusTokenFactoryAsync(prompt, imageDataUrl, ResolveNemotronModel(chosen));
        }

        string key = ReadInboundKey();
        if (string.IsNullOrEmpty(key))
        {
            return await AskNebiusTokenFactoryAsync(prompt, imageDataUrl, ResolveNemotronModel(chosen));
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{DefaultGatewayUrl}/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            object userContent = string.IsNullOrEmpty(imageDataUrl)
                ? prompt
                : new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = imageDataUrl } }
                };

            var payload = new
            {
                model = model ?? GatewayModelSlug(),
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You are Holodex, the desktop companion for ArkStation. Focus on GrimX Spells and Phosphor Shell. Answer concisely and directly."
                    },
                    new
                    {
                        role = "user",
                        content = userContent
                    }
                },
                max_tokens = 1024,
                temperature = 0.2
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"Ark Gateway HTTP {(int)resp.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                string? content = choices[0].GetProperty("message").GetProperty("content").GetString();
                return (true, content?.Trim() ?? "(empty response)");
            }

            return (false, "No choices returned by Ark Gateway.");
        }
        catch (Exception ex)
        {
            return (false, $"Ark Gateway connection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Calls Nebius Token Factory (`https://api.tokenfactory.nebius.com/v1/chat/completions`) directly
    /// with an NVIDIA Nemotron open model. Never hangs; raises a PhosphorToast if the API key is missing.
    /// </summary>
    public async Task<(bool Ok, string Output)> AskNebiusTokenFactoryAsync(
        string prompt,
        string? imageDataUrl = null,
        string? model = null)
    {
        string apiKey = ReadNebiusApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            const string missingMsg =
                "Nebius Token Factory API key not found. Set NEBIUS_API_KEY in your environment or add generic credential 'Omniwand:NebiusTokenFactory' in Windows Credential Manager.";
            PhosphorToast.Show(
                "Token Factory Key Missing",
                "Set NEBIUS_API_KEY or add Omniwand:NebiusTokenFactory in Windows Credential Manager to run NVIDIA Nemotron.",
                "STANDALONE // NEBIUS TOKEN FACTORY",
                5200,
                ToastTone.Warning);
            return (false, missingMsg);
        }

        string targetModel = ResolveNemotronModel(model ?? SelectedModel);
        string endpoint = $"{GetNebiusBaseUrl()}/chat/completions";

        // Text-only Nemotron models reject image_url arrays. When the screen is attached, route this one request to
        // the Nemotron VL model instead of silently dropping the image.
        bool supportsVision = targetModel.Contains("omni", StringComparison.OrdinalIgnoreCase) ||
                              targetModel.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
                              targetModel.Contains("vision", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(imageDataUrl) && !supportsVision)
        {
            targetModel = "nvidia/Nemotron-3.5-Super-VL";
            supportsVision = true;
        }

        object userContent = (!string.IsNullOrEmpty(imageDataUrl) && supportsVision)
            ? new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = imageDataUrl } }
            }
            : prompt;

        // Bounded retry (max 2 attempts per handoff rule: "Bounded retries, never hang. Fail twice, stop and report.")
        string lastError = "Unknown error";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var payload = new
                {
                    model = targetModel,
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = "You are Holodex, a native Windows desktop companion and GrimX Spell wand powered by NVIDIA Nemotron on Nebius Token Factory. Answer concisely, accurately, and directly."
                        },
                        new
                        {
                            role = "user",
                            content = userContent
                        }
                    },
                    max_tokens = 1024,
                    temperature = 0.2
                };

                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"Nebius Token Factory HTTP {(int)resp.StatusCode} ({targetModel}): {body}";
                    if ((int)resp.StatusCode is 401 or 403)
                    {
                        PhosphorToast.Show(
                            "Token Factory Auth Rejected",
                            $"Nebius Token Factory returned HTTP {(int)resp.StatusCode}. Verify your NEBIUS_API_KEY.",
                            "STANDALONE // NEBIUS TOKEN FACTORY",
                            4800,
                            ToastTone.Error);
                        return (false, lastError);
                    }
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var msg = choices[0].GetProperty("message");
                    string? content = msg.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String
                        ? cProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(content) &&
                        msg.TryGetProperty("reasoning_content", out var rProp) &&
                        rProp.ValueKind == JsonValueKind.String)
                    {
                        content = rProp.GetString();
                    }
                    return (true, content?.Trim() ?? "(empty response)");
                }

                return (false, "No choices returned by Nebius Token Factory.");
            }
            catch (Exception ex)
            {
                lastError = $"Nebius Token Factory connection error (attempt {attempt}/2): {ex.Message}";
            }
        }

        PhosphorToast.Show(
            "Nebius Token Factory Unreachable",
            lastError,
            "STANDALONE // NEMOTRON",
            4500,
            ToastTone.Error);
        return (false, lastError);
    }

    // Agent turns can run longer than the shared client's 25s ceiling, so they get their own client and a per-call deadline.
    private static readonly HttpClient AgentHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// One OpenAI-compatible chat turn on Nebius Token Factory with native tool calling (verified 2026-10-26: every
    /// built-in Nemotron model returns <c>tool_calls</c>). Returns the assistant message as sent back (content,
    /// reasoning_content, tool_calls). Bounded: one retry on 5xx or a dropped connection, never on auth or cancellation.
    /// </summary>
    public async Task<(bool Ok, JsonObject? Message, string Error)> ChatNebiusAsync(
        JsonArray messages,
        JsonArray? tools,
        string model,
        string? toolChoice,
        CancellationToken ct,
        int maxTokens = 1536)
    {
        string apiKey = ReadNebiusApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            PhosphorToast.Show(
                "Token Factory Key Missing",
                "Set NEBIUS_API_KEY or add Omniwand:NebiusTokenFactory in Windows Credential Manager to run NVIDIA Nemotron.",
                "STANDALONE // NEBIUS TOKEN FACTORY",
                5200,
                ToastTone.Warning);
            return (false, null, "Nebius Token Factory API key not found. Set NEBIUS_API_KEY or add 'Omniwand:NebiusTokenFactory' in Windows Credential Manager.");
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2
        };
        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.DeepClone();
            payload["tool_choice"] = toolChoice ?? "auto";
        }
        string json = payload.ToJsonString();

        string lastError = "Unknown error";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            call.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{GetNebiusBaseUrl()}/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await AgentHttp.SendAsync(req, call.Token);
                string body = await resp.Content.ReadAsStringAsync(call.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"Nebius Token Factory HTTP {(int)resp.StatusCode} ({model}): {Truncate(body, 300)}";
                    if ((int)resp.StatusCode is 401 or 403)
                    {
                        PhosphorToast.Show(
                            "Token Factory Auth Rejected",
                            $"Nebius Token Factory returned HTTP {(int)resp.StatusCode}. Verify your NEBIUS_API_KEY.",
                            "STANDALONE // NEBIUS TOKEN FACTORY",
                            4800,
                            ToastTone.Error);
                    }
                    if ((int)resp.StatusCode < 500) return (false, null, lastError);
                    continue;
                }
                var root = JsonNode.Parse(body);
                if (root?["choices"] is JsonArray { Count: > 0 } choices && choices[0]?["message"] is JsonObject message)
                {
                    return (true, (JsonObject)message.DeepClone(), "");
                }
                return (false, null, "No choices returned by Nebius Token Factory.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastError = ex is TaskCanceledException
                    ? $"Nebius Token Factory did not answer within 60s (attempt {attempt}/2, {model})."
                    : $"Nebius Token Factory connection error (attempt {attempt}/2): {ex.Message}";
            }
        }
        return (false, null, lastError);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Multi-model Live Vision router supporting NVIDIA Nemotron VL (`nvidia/Nemotron-3.5-Super-VL`),
    /// open vision models on Nebius Token Factory (`openbmb/MiniCPM-V-4_5`,
    /// `google/gemma-3-27b-it`), and Ark Model Gateway (`ark/kimi`), with automatic graceful fallback to
    /// `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` when a vision tier is unavailable.
    /// </summary>
    public async Task<(bool Ok, string Output)> AskVisionModelAsync(
        string prompt,
        string? imageDataUrl,
        string preferredVisionModel)
    {
        if (preferredVisionModel.StartsWith("ark/", StringComparison.OrdinalIgnoreCase))
        {
            var (gwOk, gwOut) = await AskArkGatewayAsync(prompt, imageDataUrl, preferredVisionModel);
            if (gwOk) return (true, gwOut);
        }

        string apiKey = ReadNebiusApiKey();
        if (!string.IsNullOrEmpty(apiKey))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(18));
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{GetNebiusBaseUrl()}/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                bool isVisionCapable =
                    preferredVisionModel.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
                    preferredVisionModel.Contains("minicpm", StringComparison.OrdinalIgnoreCase) ||
                    preferredVisionModel.Contains("gemma-3", StringComparison.OrdinalIgnoreCase);

                object userContent = (!string.IsNullOrEmpty(imageDataUrl) && isVisionCapable)
                    ? new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = imageDataUrl } }
                    }
                    : prompt;

                var payload = new
                {
                    model = preferredVisionModel,
                    messages = new object[]
                    {
                        new { role = "system", content = "You are Holodex Vision & Desktop UI Inspector. Analyze the screen capture, annotations, and element telemetry concisely." },
                        new { role = "user", content = userContent }
                    },
                    max_tokens = 800,
                    temperature = 0.2
                };

                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var msg = choices[0].GetProperty("message");
                        string? text = msg.TryGetProperty("content", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text) && msg.TryGetProperty("reasoning_content", out var rp) && rp.ValueKind == JsonValueKind.String)
                            text = rp.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            return (true, $"[{preferredVisionModel}]\n{text.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, $"Vision request to {preferredVisionModel} failed: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(imageDataUrl))
                return (false, $"Vision request to {preferredVisionModel} returned no answer. Check the Nebius key and try again.");
        }

        var (fbOk, fbOut) = await AskNebiusTokenFactoryAsync(prompt, null, DefaultNemotronFastModel);
        return (fbOk, fbOk ? $"[{DefaultNemotronFastModel} // text only, no image]\n{fbOut}" : fbOut);
    }

    /// <summary>
    /// The Holodex session's offline fallback, used only while <see cref="Hivemind"/> is not connected: Nemotron
    /// answers in the Syndicate's voice. It is role-play, not Hivemind, and returns the model so every card can say so.
    /// </summary>
    public async Task<(bool Ok, string Output, string Model)> AskOfflineSyndicateAsync(
        string message,
        string targetAgentId,
        string targetAgentName,
        string targetAgentRole)
    {
        string syndicatePrompt = targetAgentId == "all"
            ? "You are role-playing the ArkStation Syndicate in Holodex's offline session (Hivemind is not connected). " +
              "Reply with 2 concise, distinct in-character turns from the most relevant Syndicate conductors " +
              "(choose from GALA // Co-op Orchestrator, WIZ // GrimX & Mouse Macro Conductor, SMITH // Architecture & Code Authority, ARBITER // Eden & SolaTree Conductor, SCHOLAR // Cortex Research, MUSE // MagiXLab Visuals). " +
              $"Format each turn as 'AGENT_NAME // ROLE:\\n<concise actionable response>'.\n\nOperator message: {message}"
            : $"You are {targetAgentName} ({targetAgentRole}), role-played in Holodex's offline session (Hivemind is not connected). " +
              $"Reply in character as '{targetAgentName} // {targetAgentRole.ToUpperInvariant()}:' with a concise, high-signal operational response.\n\nOperator message: {message}";

        var (ok, output) = await AskArkGatewayAsync(syndicatePrompt);
        return (ok, output, ResolveNemotronModel(SelectedModel));
    }
}
