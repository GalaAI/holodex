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
public enum SpellCastOutcome { Cast, Failed, NoBinding, Refused, Unreachable, Standalone }

/// <summary>One Spell an Orb slot can cast, as Input Spine lists it (<c>GET /api/spine/grimx/spells</c>).</summary>
public sealed record SpellChoice(string Slug, string Name, string? Description, bool Enabled);

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
    /// <summary>The Ark API (Arkhive) when no ARK_API_BASE_URL says otherwise.</summary>
    public const string DefaultArkApiBaseUrl = "http://127.0.0.1:8788";
    public const string DefaultInputSpineUrl = "http://127.0.0.1:8842";
    public const string DefaultPcModzUrl = "http://127.0.0.1:8830";
    public const string DefaultNebiusBaseUrl = "https://api.tokenfactory.nebius.com/v1";
    /// <summary>The Ark API route a non-Nemotron selection falls back to (Wiz's gateway fallback).</summary>
    public const string DefaultGatewayModelSlug = "ark/nemotron-3-super";
    /// <summary>Gemini 4 Argon as Antigravity names it (`agy models`); "gemini-argon" was never a real id.</summary>
    public const string DefaultWizModelSlug = "gemini-4-argon-medium";

    /// <summary>Default NVIDIA Nemotron models on Nebius Token Factory (fast tier first for cursor &amp; dictation calls).</summary>
    public const string DefaultNemotronFastModel = "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B";
    public const string DefaultNemotronSuperModel = "nvidia/nemotron-3-super-120b-a12b";
    public const string DefaultNemotronVisionModel = "nvidia/Nemotron-3.5-Super-VL";

    public static readonly IReadOnlyList<string> BuiltInNemotronModels = new[]
    {
        DefaultNemotronFastModel,
        "nvidia/Nemotron-3_5-Lightning",
        DefaultNemotronSuperModel,
        DefaultNemotronVisionModel,
        "nvidia/Nemotron-3-Ultra-550b-a55b"
    };

    /// <summary>
    /// The Ark API's Nemotron routes (packages/model-gateway-core, provider "nebius") and the Token Factory id each
    /// one serves. The Ark path asks by the route; the Nebius direct path asks by the id.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ArkNemotronRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ark/nemotron-3-nano"] = DefaultNemotronFastModel,
        ["ark/nemotron-3-super"] = DefaultNemotronSuperModel,
        ["ark/nemotron-3-ultra"] = "nvidia/Nemotron-3-Ultra-550b-a55b",
        ["ark/nemotron-3.5-super-vl"] = DefaultNemotronVisionModel,
    };

    /// <summary>The Ark API base: ARK_API_BASE_URL, else the local Ark API. Read on every call so a run can point it elsewhere.</summary>
    public static string ArkApiBaseUrl
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("ARK_API_BASE_URL");
            return string.IsNullOrWhiteSpace(custom) ? DefaultArkApiBaseUrl : custom.Trim().TrimEnd('/');
        }
    }

    /// <summary>
    /// True when this PC runs its owner's Ark (not OMNIWAND_STANDALONE, and an Ark root or a gateway inbound key is
    /// present), read once at startup. Surfaces that name the owner's private Ark (its pods, agents and gates) show
    /// them only then; a public install sees neutral names.
    /// </summary>
    public static bool ArkOnThisPc => ArkOnThisPcLazy.Value;

    private static readonly Lazy<bool> ArkOnThisPcLazy = new(() =>
        !StandaloneRequested() &&
        (Directory.Exists(DefaultArkRoot) ||
         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARK_MODEL_GATEWAY_INBOUND_KEY")) ||
         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARK_HIVEMIND_TOKEN")) ||
         !string.IsNullOrWhiteSpace(TryReadWindowsCredential(GatewayInboundKeyTarget))));

    private static bool StandaloneRequested() =>
        Environment.GetEnvironmentVariable("OMNIWAND_STANDALONE") is { } v &&
        (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Where an AInbox Handoff Scroll can go: the owner's Ark pods on an Ark, neutral destinations saved on this PC otherwise.</summary>
    public static IReadOnlyList<ArkPodInboxInfo> PodInboxes => ArkOnThisPc ? ArkPods : LocalDestinations;

    /// <summary>
    /// A public install's destinations. Their ids match the pods' ids so routing is the same; a Scroll sent to one is
    /// saved under Documents\Holodex\Scrolls (OutboundQueue local mode).
    /// </summary>
    private static readonly IReadOnlyList<ArkPodInboxInfo> LocalDestinations = new List<ArkPodInboxInfo>
    {
        new("nexus", "Briefs", "holodex:scrolls:briefs", "local", "Local", "operate", "patina",
            "Briefs, documents and anything that needs a decision."),
        new("eden", "Code & Infra", "holodex:scrolls:code", "local", "Local", "operate", "patina",
            "Scripts, schemas, adapters and infrastructure changes."),
        new("cortex", "Research", "holodex:scrolls:research", "local", "Local", "operate", "patina",
            "Research digests, datasets and study material."),
        new("magixlab", "Media", "holodex:scrolls:media", "local", "Local", "create", "coral",
            "Images, video, audio and visual references."),
        new("forge", "Automations", "holodex:scrolls:automations", "local", "Local", "create", "coral",
            "Spells, workflows and UI components to build."),
        new("anvl", "Hardware", "holodex:scrolls:hardware", "local", "Local", "create", "coral",
            "CAD, 3D print files and physical builds."),
        new("bazaar", "Publishing", "holodex:scrolls:publishing", "local", "Local", "distribute", "seal",
            "Content to publish or share.")
    };

    private static readonly IReadOnlyList<ArkPodInboxInfo> ArkPods = new List<ArkPodInboxInfo>
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

    /// <summary>Raised (on the probing thread) when <see cref="LastGatewayOnline"/> changes, so a model chip names the route the next ask takes.</summary>
    public event EventHandler? ArkApiStateChanged;

    private bool SetArkApiOnline(bool online)
    {
        bool changed = LastGatewayOnline != online;
        LastGatewayOnline = online;
        if (changed) ArkApiStateChanged?.Invoke(this, EventArgs.Empty);
        return online;
    }

    public ArkStationClient(string? arkRoot = null)
    {
        ArkRoot = !string.IsNullOrWhiteSpace(arkRoot) && Directory.Exists(arkRoot)
            ? arkRoot
            : DefaultArkRoot;
        SelectedModel = DefaultNemotronFastModel;
    }

    public bool ForceStandaloneMode => StandaloneRequested();

    /// <summary>
    /// True when this machine runs its owner's Ark: a configured Ark root or a gateway inbound key. A public install
    /// has neither, so Ark-only surfaces (Input Spine, the Ark gateway, Augment Inventory) stay out of its way.
    /// </summary>
    public bool ArkInstalled => !ForceStandaloneMode && (Directory.Exists(ArkRoot) || HasArkGatewayCredentials());

    /// <summary>
    /// Token Factory's base. NEBIUS_BASE_URL or TOKEN_FACTORY_BASE_URL may point at another Nebius region, but the
    /// bearer key goes only to an https host under nebius.com; any other override is ignored.
    /// </summary>
    public static string GetNebiusBaseUrl()
    {
        string? custom =
            Environment.GetEnvironmentVariable("NEBIUS_BASE_URL") ??
            Environment.GetEnvironmentVariable("TOKEN_FACTORY_BASE_URL");
        if (string.IsNullOrWhiteSpace(custom)) return DefaultNebiusBaseUrl;
        return IsNebiusHost(custom.Trim()) ? custom.Trim().TrimEnd('/') : DefaultNebiusBaseUrl;
    }

    internal static bool IsNebiusHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("nebius.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".nebius.com", StringComparison.OrdinalIgnoreCase));

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
        bool gateway = await ProbeArkApiAsync();
        bool spine = ArkInstalled && await ProbeHttpAsync(DefaultInputSpineUrl + "/api/spine/config");
        bool pcModz = ArkInstalled && await ProbeHttpAsync(DefaultPcModzUrl + "/health");

        if (!gateway && !IsNemotronModel(SelectedModel))
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

    /// <summary>
    /// Whether the Ark API answers at <see cref="ArkApiBaseUrl"/>: never in standalone mode. Sets
    /// <see cref="LastGatewayOnline"/>, which the pickers and LEDs read.
    /// </summary>
    public async Task<bool> ProbeArkApiAsync()
    {
        string baseUrl = ArkApiBaseUrl;
        // A down Ark API is remembered for a few seconds, so asks in a row do not each wait out a refused connection.
        if (_arkApiDownSince is { } down && down.BaseUrl == baseUrl && DateTime.UtcNow - down.At < ArkApiDownMemory)
            return SetArkApiOnline(false);
        bool online = !ForceStandaloneMode && await ProbeHttpAsync(baseUrl + "/model-gateway/health");
        _arkApiDownSince = online ? null : (baseUrl, DateTime.UtcNow);
        return SetArkApiOnline(online);
    }

    private static readonly TimeSpan ArkApiDownMemory = TimeSpan.FromSeconds(15);
    private (string BaseUrl, DateTime At)? _arkApiDownSince;

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
    /// The Spells Input Spine holds for each Orb slot id, in its order. Empty when Spine is offline or
    /// predates the listing, so the picker shows nothing to choose rather than guessing.
    /// </summary>
    public async Task<Dictionary<string, List<SpellChoice>>> GetGrimXSpellsAsync()
    {
        var spells = new Dictionary<string, List<SpellChoice>>(StringComparer.OrdinalIgnoreCase);
        if (ForceStandaloneMode) return spells;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = await _http.GetAsync($"{DefaultInputSpineUrl}/api/spine/grimx/spells", cts.Token);
            if (!resp.IsSuccessStatusCode) return spells;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("spells", out var bySlot) || bySlot.ValueKind != JsonValueKind.Object) return spells;
            foreach (var slot in bySlot.EnumerateObject())
            {
                var list = new List<SpellChoice>();
                foreach (var e in slot.Value.EnumerateArray())
                {
                    string? slug = e.TryGetProperty("slug", out var sl) ? sl.GetString() : null;
                    string? name = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new SpellChoice(
                        slug,
                        name,
                        e.TryGetProperty("description", out var ds) ? ds.GetString() : null,
                        !e.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False));
                }
                spells[slot.Name] = list;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Offline or an older Spine: no choices.
        }
        return spells;
    }

    /// <summary>
    /// Dispatches a GrimX Spell cast to Input Spine (:8842/api/spine/event), as <c>grimx/&lt;slot&gt;</c>, or
    /// <c>grimx/&lt;slot&gt;/&lt;spell&gt;</c> when a Spell was picked for the slot. Cast only when Spine matched a
    /// binding and the bound action reported success; a matched action that failed is Failed, with its error.
    /// In standalone mode (when Input Studio is absent) it degrades to a clean warning for the Phosphor toast.
    /// </summary>
    public async Task<(SpellCastOutcome Outcome, string Output)> TriggerGrimXFlowAsync(
        string flowKind,
        string verb,
        Point cursorPos,
        string? cursorRole = null,
        string? spellSlug = null)
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
                    match = SpellTrigger(flowKind, spellSlug)
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
            // Spine answers after the bound action finishes, and a Spell can take up to a minute (a bench cast
            // reads the operator credential, saves its flow and advances each part). Wait past the longest
            // spine action timeout so a slow cast is not misreported as an unreachable Spine.
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(70));
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{DefaultInputSpineUrl}/api/spine/event", content, cts.Token);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return (SpellCastOutcome.Refused, $"Input Spine refused the {flowKind} cast (HTTP {(int)resp.StatusCode}), so nothing was cast.");
            }

            bool matched = false;
            bool succeeded = false;
            string detail = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                matched = doc.RootElement.TryGetProperty("matched", out var m) && m.ValueKind == JsonValueKind.True;
                if (matched && doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                {
                    succeeded = result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                    string key = succeeded ? "output" : "error";
                    if (result.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String) detail = FirstLine(text.GetString());
                }
            }
            catch (JsonException)
            {
                // Unparseable reply: treat as not matched.
            }

            if (!matched)
                return (SpellCastOutcome.NoBinding, $"No Input Spine binding for {SpellTrigger(flowKind, spellSlug)}, so nothing was cast. Bind it in Input Studio.");
            return succeeded
                ? (SpellCastOutcome.Cast, string.IsNullOrEmpty(detail) ? $"{verb} {flowKind} cast through Input Spine." : detail)
                : (SpellCastOutcome.Failed, string.IsNullOrEmpty(detail) ? "The bound action reported a failure." : detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (SpellCastOutcome.Unreachable, "Input Spine (:8842) is unreachable, so nothing was cast. Start the Ark runtime and cast again.");
        }
    }

    /// <summary>The spine gesture for a slot, or for one picked Spell of that slot.</summary>
    private static string SpellTrigger(string flowKind, string? spellSlug) =>
        string.IsNullOrWhiteSpace(spellSlug)
            ? $"grimx/{flowKind.ToLowerInvariant()}"
            : $"grimx/{flowKind.ToLowerInvariant()}/{spellSlug.ToLowerInvariant()}";

    /// <summary>The first non-empty line of an action's output or error, short enough for a toast body.</summary>
    private static string FirstLine(string? text)
    {
        string line = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 180 ? line[..177] + "..." : line;
    }

    /// <summary>Whether Keymaster holds the inbound key the HUD needs to ask through the Ark gateway.</summary>
    public bool HasGatewayInboundKey => HasArkGatewayCredentials();

    /// <summary>
    /// Invokes Wiz (4GE Workflow-Crafting &amp; GrimX Governance Agent).
    /// Checks ArkStation routes first when reachable; when offline or when a Nemotron model is selected,
    /// calls Nebius Token Factory directly with NVIDIA Nemotron.
    /// </summary>
    public async Task<(bool Ok, string Runtime, string Model, string Output)> AskWizAsync(
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

        // 3. Nemotron: through the Ark API when it is up, else Token Factory direct. AskAsync owns that choice and
        // reports the route and model that answered.
        var answer = await AskAsync(wizSystem, imageDataUrl, isNemotronSelected ? selected : DefaultGatewayModelSlug);
        return (answer.Ok, answer.Route, answer.Model, answer.Output);
    }

    /// <summary>A Nemotron model, by its Token Factory id (nvidia/...Nemotron...) or its Ark API route (ark/nemotron-...). Other NVIDIA models are not.</summary>
    public static bool IsNemotronModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("nemotron", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Token Factory id for a selection: an Ark route maps to the Nemotron id it serves, a Token Factory id
    /// (org/model, such as nvidia/... or openbmb/...) stays, and anything Token Factory does not serve is Nano.
    /// </summary>
    public static string TokenFactoryModelFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return DefaultNemotronFastModel;
        if (ArkNemotronRoutes.TryGetValue(model, out string? id)) return id;
        bool tokenFactoryId = model.Contains('/') && !model.StartsWith("ark/", StringComparison.OrdinalIgnoreCase);
        return tokenFactoryId ? model : DefaultNemotronFastModel;
    }

    /// <summary>The Ark API route for a selection: an ark/ route as is, a Token Factory id by its route, or null when the Ark API has none.</summary>
    public static string? ArkRouteFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (model.StartsWith("ark/", StringComparison.OrdinalIgnoreCase)) return model;
        foreach (var (route, id) in ArkNemotronRoutes)
            if (string.Equals(id, model, StringComparison.OrdinalIgnoreCase)) return route;
        return null;
    }

    /// <summary>Model chosen in the HUD and other chat pickers; shared by those surfaces. Defaults to NVIDIA Nemotron fast tier. Wiz keeps its own agent model (WizAgent.DefaultReasoningModel).</summary>
    public string SelectedModel { get; set; } = DefaultNemotronFastModel;

    /// <summary>The real Hivemind (remote MCP and its OAuth sign-in); the Holodex session and work orders use it.</summary>
    public HivemindClient Hivemind { get; } = new();

    /// <summary>CLI form of the selection (no "ark/" route prefix).</summary>
    private static string CliModelSlug(string model) =>
        model.StartsWith("ark/", StringComparison.OrdinalIgnoreCase) ? model[4..] : model;

    /// <summary>
    /// Models offered by the pickers: Nemotron only, by its Token Factory id. One id answers through the Ark API when
    /// it is up and through Token Factory direct when it is not (<see cref="AskAsync"/>), so the list does not change
    /// with the Ark, and no private route (other providers, local or uncensored models) reaches a picker.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync()
    {
        await ProbeArkApiAsync();
        var standaloneModels = new List<string>(BuiltInNemotronModels);
        if (!IsNemotronModel(SelectedModel) || SelectedModel.StartsWith("ark/", StringComparison.OrdinalIgnoreCase))
        {
            SelectedModel = TokenFactoryModelFor(SelectedModel);
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

    /// <summary>Route ids an answer reports: the Ark API (Arkhive orchestration) or Token Factory direct.</summary>
    public const string RouteArkApi = "ark-api";
    public const string RouteNebiusDirect = "nebius-direct";

    /// <summary>
    /// One model answer and where it came from: the model that answered (as the provider reported it), the route
    /// (<see cref="RouteArkApi"/> or <see cref="RouteNebiusDirect"/>), the Ark API receipt when there is one, and
    /// why the Ark API was passed over when it was.
    /// </summary>
    public sealed record ModelAnswer(bool Ok, string Output, string Model, string Route, string? ReceiptId = null, string? FallbackReason = null)
    {
        /// <summary>"VIA ARK API" or "NEBIUS DIRECT", for card kickers and status lines.</summary>
        public string RouteLabel => Route == RouteArkApi ? "VIA ARK API" : "NEBIUS DIRECT";

        /// <summary>One line for logs and the control pipe: ok, route, model, receipt, fallback reason.</summary>
        public string Describe() =>
            $"ok={Ok}:route={Route}:model={Model}:receipt={ReceiptId ?? "none"}:fallback={(FallbackReason ?? "none").Replace(':', ' ')}";
    }

    // Grounding: a small model asked about the stack it runs on otherwise guesses (a live Nano answer once said Token
    // Factory "enables users to generate custom AI tokens"). These are the facts a judge is likely to ask about.
    private const string HolodexSystemPrompt =
        "You are Holodex, a Windows desktop companion that answers with NVIDIA Nemotron open models. " +
        "Facts about your own stack: NVIDIA Nemotron is a family of open models from NVIDIA. Nebius Token Factory is " +
        "Nebius's hosted inference service that serves open models such as Nemotron through an OpenAI-compatible API; " +
        "Holodex calls it directly, or through the owner's Ark API when one runs on the PC. Hivemind is the user's own " +
        "agent hub that Holodex can sign in to. " +
        "Answer concisely, accurately and directly. If you do not know something, say so instead of guessing.";

    // Token Factory serves Nemotron with its thinking on by default; with a reply budget, Nano could spend all of it
    // thinking and end at finish_reason=length with no answer. Holodex wants the answer, so thinking is off
    // (chat_template_kwargs.enable_thinking=false, accepted by every built-in Nemotron id, checked 2026-10-29).
    private const int AnswerMaxTokens = 2048;

    /// <summary>
    /// Asks a Nemotron model. When the Ark API is up and its inbound key is loaded, the ask goes through the Ark API's
    /// Nemotron route (<see cref="ArkRouteFor"/>), which records a receipt; on a timeout, an HTTP error or an empty
    /// answer it falls back to Token Factory direct with the matching Nemotron id. With no Ark API it asks Token
    /// Factory direct. The answer names the model and route that actually answered.
    /// </summary>
    public async Task<ModelAnswer> AskAsync(string prompt, string? imageDataUrl = null, string? model = null, string? systemPrompt = null)
    {
        string chosen = model ?? SelectedModel;
        // A screen goes to a model that can read it: the VL model unless the choice already reads images.
        if (!string.IsNullOrEmpty(imageDataUrl) && !ReadsImages(chosen)) chosen = DefaultNemotronVisionModel;

        string? fallbackReason = null;
        string? route = ArkRouteFor(chosen);
        string key = route == null ? string.Empty : ReadInboundKey();
        if (route == null) fallbackReason = $"{chosen} has no Ark API route";
        else if (ForceStandaloneMode) fallbackReason = "standalone mode does not use the Ark API";
        else if (!await ProbeArkApiAsync()) fallbackReason = $"Ark API at {ArkApiBaseUrl} is not reachable";
        else if (string.IsNullOrEmpty(key)) fallbackReason = "no Ark API inbound key";
        else
        {
            var viaArk = await AskArkApiAsync(prompt, imageDataUrl, route, key, systemPrompt ?? HolodexSystemPrompt);
            if (viaArk.Ok) return viaArk;
            fallbackReason = viaArk.Output;
        }

        var direct = await AskNebiusTokenFactoryAsync(prompt, imageDataUrl, TokenFactoryModelFor(chosen), systemPrompt);
        return direct with { FallbackReason = fallbackReason };
    }

    /// <summary>Models that read an attached image: Nemotron VL and the open vision models on Token Factory.</summary>
    public static bool ReadsImages(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("-vl", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("omni", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("minicpm", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("gemma-3", StringComparison.OrdinalIgnoreCase));

    /// <summary>One chat completion through the Ark API (POST {ArkApiBaseUrl}/v1/chat/completions). Not ok on any failure, with the reason.</summary>
    private async Task<ModelAnswer> AskArkApiAsync(string prompt, string? imageDataUrl, string route, string key, string systemPrompt)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ArkApiBaseUrl}/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(ChatPayload(route, systemPrompt, prompt, imageDataUrl).ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            string? receipt = resp.Headers.TryGetValues("x-ark-gateway-receipt-id", out var values) ? values.FirstOrDefault() : null;
            if (!resp.IsSuccessStatusCode)
                return new ModelAnswer(false, $"Ark API HTTP {(int)resp.StatusCode} on {route}: {Truncate(body, 200)}", route, RouteArkApi, receipt);

            var (text, finish, answeredBy) = ReadCompletion(body);
            if (string.IsNullOrWhiteSpace(text))
                return new ModelAnswer(false, $"Ark API returned no answer on {route} (finish_reason={finish ?? "none"})", route, RouteArkApi, receipt);
            return new ModelAnswer(true, text, answeredBy ?? route, RouteArkApi, receipt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new ModelAnswer(false, ex is TaskCanceledException
                ? $"Ark API did not answer on {route} within 20s"
                : $"Ark API error on {route}: {ex.Message}", route, RouteArkApi);
        }
    }

    /// <summary>The OpenAI-compatible request both routes take, with Nemotron's thinking off so the budget goes to the answer.</summary>
    private static JsonObject ChatPayload(string model, string systemPrompt, string prompt, string? imageDataUrl, int maxTokens = AnswerMaxTokens)
    {
        JsonNode userContent = string.IsNullOrEmpty(imageDataUrl)
            ? JsonValue.Create(prompt)!
            : new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = prompt },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } }
            };
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2
        };
        if (IsNemotronModel(model))
            payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        return payload;
    }

    /// <summary>The answer text, finish_reason and reporting model of a chat completion. Reasoning text is never the answer.</summary>
    private static (string? Text, string? Finish, string? Model) ReadCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return (null, "no choices", model);
        var choice = choices[0];
        string? finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        string? text = choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()?.Trim()
            : null;
        return (text, finish, model);
    }

    /// <summary>
    /// Calls Nebius Token Factory (`https://api.tokenfactory.nebius.com/v1/chat/completions`) directly with an NVIDIA
    /// Nemotron open model. An empty answer is a failure that names its finish_reason. Never hangs; raises a toast
    /// when the key is missing or rejected.
    /// </summary>
    public async Task<ModelAnswer> AskNebiusTokenFactoryAsync(
        string prompt,
        string? imageDataUrl = null,
        string? model = null,
        string? systemPrompt = null)
    {
        string targetModel = TokenFactoryModelFor(model ?? SelectedModel);
        // Text-only Nemotron models reject image_url arrays: an attached screen goes to the Nemotron VL model.
        if (!string.IsNullOrEmpty(imageDataUrl) && !ReadsImages(targetModel)) targetModel = DefaultNemotronVisionModel;

        string apiKey = ReadNebiusApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            PhosphorToast.Show(
                "Token Factory Key Missing",
                "Set NEBIUS_API_KEY or add Omniwand:NebiusTokenFactory in Windows Credential Manager to run NVIDIA Nemotron.",
                "NEBIUS TOKEN FACTORY",
                5200,
                ToastTone.Warning);
            return new ModelAnswer(false,
                "Nebius Token Factory API key not found. Set NEBIUS_API_KEY in your environment or add generic credential 'Omniwand:NebiusTokenFactory' in Windows Credential Manager.",
                targetModel, RouteNebiusDirect);
        }

        string json = ChatPayload(targetModel, systemPrompt ?? HolodexSystemPrompt, prompt, imageDataUrl).ToJsonString();
        // Bounded retry: two attempts on a 5xx or a dropped connection, never on auth or a bad request.
        string lastError = "Unknown error";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(24));
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{GetNebiusBaseUrl()}/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"Nebius Token Factory HTTP {(int)resp.StatusCode} ({targetModel}): {Truncate(body, 300)}";
                    if ((int)resp.StatusCode is 401 or 403)
                    {
                        PhosphorToast.Show(
                            "Token Factory Auth Rejected",
                            $"Nebius Token Factory returned HTTP {(int)resp.StatusCode}. Verify your NEBIUS_API_KEY.",
                            "NEBIUS TOKEN FACTORY",
                            4800,
                            ToastTone.Error);
                    }
                    if ((int)resp.StatusCode < 500) return new ModelAnswer(false, lastError, targetModel, RouteNebiusDirect);
                    continue;
                }

                var (text, finish, answeredBy) = ReadCompletion(body);
                if (string.IsNullOrWhiteSpace(text))
                    return new ModelAnswer(false, $"{targetModel} returned no answer (finish_reason={finish ?? "none"}).", answeredBy ?? targetModel, RouteNebiusDirect);
                return new ModelAnswer(true, text, answeredBy ?? targetModel, RouteNebiusDirect);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastError = ex is TaskCanceledException
                    ? $"Nebius Token Factory did not answer within 24s (attempt {attempt}/2, {targetModel})."
                    : $"Nebius Token Factory connection error (attempt {attempt}/2): {ex.Message}";
            }
        }

        PhosphorToast.Show(
            "Nebius Token Factory Unreachable",
            lastError,
            "NEBIUS TOKEN FACTORY",
            4500,
            ToastTone.Error);
        return new ModelAnswer(false, lastError, targetModel, RouteNebiusDirect);
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
    /// Live Vision for the lasso and element picker: NVIDIA Nemotron VL and the open vision models on Token Factory
    /// (openbmb/MiniCPM-V-4_5, google/gemma-3-27b-it). Nemotron VL goes through the Ark API when it is up; every other
    /// model answers Token Factory direct. There is no text-only fallback: a model that never saw the image would
    /// invent an answer about it.
    /// </summary>
    public Task<ModelAnswer> AskVisionModelAsync(string prompt, string? imageDataUrl, string preferredVisionModel) =>
        AskAsync(prompt, imageDataUrl, preferredVisionModel,
            "You are Holodex Vision & Desktop UI Inspector. Analyze the screen capture, annotations, and element telemetry concisely.");
}
