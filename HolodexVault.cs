using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Omniwand;

/// <summary>How a catalog adapter connects: a pasted key, an MCP server's own sign-in, or a service on this PC.</summary>
internal enum VaultKind { ApiKey, McpServer, LocalService, SignIn }

/// <summary>
/// One adapter in the Vault catalog. Key adapters carry the prefixes and env names the universal intake recognizes and,
/// where the provider has one, a read-only probe that proves a key works. MCP adapters carry the server URL; their
/// sign-in is the server's own OAuth (protected resource metadata, dynamic registration, PKCE).
/// </summary>
internal sealed record VaultProvider(
    string Id,
    string Label,
    string Group,
    VaultKind Kind,
    string Blurb,
    string EnvName = "",
    string[]? Prefixes = null,
    string Url = "",
    string KeysUrl = "",
    Func<string, HttpRequestMessage>? Probe = null);

/// <summary>
/// A connection in the user's vault. The manifest holds only this: names, where the secret lives, a short fingerprint
/// and dates. The secret itself is in Windows Credential Manager under <see cref="Target"/>.
/// </summary>
internal sealed class VaultEntry
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Label { get; set; } = "";
    public VaultKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string EnvName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    /// <summary>works, failed or unchecked.</summary>
    public string Status { get; set; } = "unchecked";
    public string Detail { get; set; } = "";
    [JsonIgnore] public bool HasSecret => Kind != VaultKind.LocalService;
}

/// <summary>The intake's reading of what was pasted.</summary>
internal sealed record VaultDetection(VaultProvider? Provider, string Value, string EnvName, string How);

/// <summary>
/// The user's own KeyMaster vault on this PC, shaped like ArkStation's (KeyMaster, apps/api/src/keymaster-routes.ts):
/// a universal intake that recognizes a key by its prefix or its ENV_NAME=value line, and failing that by asking each
/// provider's read-only endpoint; secrets in Windows Credential Manager (<c>Omniwand:Vault:*</c>) and never in a file,
/// a log or Hivemind; and copy, rotate and delete. It needs no Ark and no Hivemind account. The Nebius Token Factory
/// key keeps its own target (<c>Omniwand:NebiusTokenFactory</c>), so a key added here is the one Holodex's models use.
/// </summary>
internal static class HolodexVault
{
    public const string NebiusTarget = "Omniwand:NebiusTokenFactory";
    private const string TargetPrefix = "Omniwand:Vault:";
    public static readonly Uri McpRedirectUri = new("http://127.0.0.1:47613/vault/callback/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly object Gate = new();

    public static string ManifestPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "vault.json");

    private static HttpRequestMessage Bearer(string url, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return req;
    }

    private static HttpRequestMessage Header(string url, string name, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation(name, key);
        return req;
    }

    public const string GroupModels = "Models";
    public const string GroupSearch = "Web search";
    public const string GroupCloud = "Cloud and dev";
    public const string GroupApps = "Apps you sign in to";
    public const string GroupLocal = "On this PC";

    /// <summary>
    /// The starting catalog, from what ArkStation and Hivemind already broker. Key adapters and their probes follow
    /// KeyMaster's classifyUniversalKey and PROVIDER_PROBES; the MCP servers are the remote ones whose sign-in was
    /// checked on 2026-10-29 to publish protected resource metadata, dynamic client registration and PKCE S256.
    /// GitHub's MCP server refuses dynamic registration, so GitHub is a token here.
    /// </summary>
    public static readonly IReadOnlyList<VaultProvider> Catalog = new VaultProvider[]
    {
        new("nebius", "Nebius Token Factory", GroupModels, VaultKind.ApiKey, "Default model. NVIDIA Nemotron for Holodex, Wiz and dictation.",
            "NEBIUS_API_KEY", KeysUrl: "https://tokenfactory.nebius.com/", Probe: k => Bearer($"{ArkStationClient.GetNebiusBaseUrl()}/models", k)),
        new("openai", "OpenAI", GroupModels, VaultKind.ApiKey, "GPT models.", "OPENAI_API_KEY", new[] { "sk-proj-", "sk-svcacct-", "sk-" },
            KeysUrl: "https://platform.openai.com/api-keys", Probe: k => Bearer("https://api.openai.com/v1/models", k)),
        new("anthropic", "Anthropic", GroupModels, VaultKind.ApiKey, "Claude models.", "ANTHROPIC_API_KEY", new[] { "sk-ant-" },
            KeysUrl: "https://console.anthropic.com/settings/keys"),
        new("gemini", "Google Gemini", GroupModels, VaultKind.ApiKey, "Gemini models.", "GEMINI_API_KEY", new[] { "AIza" },
            KeysUrl: "https://aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter", GroupModels, VaultKind.ApiKey, "One key for many model providers.", "OPENROUTER_API_KEY", new[] { "sk-or-v1-" },
            KeysUrl: "https://openrouter.ai/keys", Probe: k => Bearer("https://openrouter.ai/api/v1/auth/key", k)),
        new("xai", "xAI", GroupModels, VaultKind.ApiKey, "Grok models.", "XAI_API_KEY", new[] { "xai-" }, KeysUrl: "https://console.x.ai/"),
        new("groq", "Groq", GroupModels, VaultKind.ApiKey, "Fast open models.", "GROQ_API_KEY", new[] { "gsk_" },
            KeysUrl: "https://console.groq.com/keys", Probe: k => Bearer("https://api.groq.com/openai/v1/models", k)),
        new("huggingface", "Hugging Face", GroupModels, VaultKind.ApiKey, "Model downloads and inference.", "HF_TOKEN", new[] { "hf_" },
            KeysUrl: "https://huggingface.co/settings/tokens", Probe: k => Bearer("https://huggingface.co/api/whoami-v2", k)),
        new("perplexity", "Perplexity", GroupModels, VaultKind.ApiKey, "Search-grounded answers.", "PERPLEXITY_API_KEY", new[] { "pplx-" },
            KeysUrl: "https://www.perplexity.ai/settings/api"),
        new("deepseek", "DeepSeek", GroupModels, VaultKind.ApiKey, "DeepSeek models.", "DEEPSEEK_API_KEY",
            KeysUrl: "https://platform.deepseek.com/api_keys", Probe: k => Bearer("https://api.deepseek.com/models", k)),
        new("mistral", "Mistral AI", GroupModels, VaultKind.ApiKey, "Mistral models.", "MISTRAL_API_KEY",
            KeysUrl: "https://console.mistral.ai/api-keys", Probe: k => Bearer("https://api.mistral.ai/v1/models", k)),
        new("together", "Together AI", GroupModels, VaultKind.ApiKey, "Open models, hosted.", "TOGETHER_API_KEY",
            KeysUrl: "https://api.together.ai/settings/api-keys", Probe: k => Bearer("https://api.together.xyz/v1/models", k)),
        new("fireworks", "Fireworks AI", GroupModels, VaultKind.ApiKey, "Open models, hosted.", "FIREWORKS_API_KEY",
            KeysUrl: "https://fireworks.ai/account/api-keys", Probe: k => Bearer("https://api.fireworks.ai/inference/v1/models", k)),
        new("cerebras", "Cerebras", GroupModels, VaultKind.ApiKey, "Fast inference.", "CEREBRAS_API_KEY",
            KeysUrl: "https://cloud.cerebras.ai/", Probe: k => Bearer("https://api.cerebras.ai/v1/models", k)),
        new("replicate", "Replicate", GroupModels, VaultKind.ApiKey, "Image, video and audio models.", "REPLICATE_API_TOKEN", new[] { "r8_" },
            KeysUrl: "https://replicate.com/account/api-tokens"),
        new("elevenlabs", "ElevenLabs", GroupModels, VaultKind.ApiKey, "Voices and speech.", "ELEVENLABS_API_KEY",
            KeysUrl: "https://elevenlabs.io/app/settings/api-keys", Probe: k => Header("https://api.elevenlabs.io/v1/user", "xi-api-key", k)),

        new("tavily", "Tavily", GroupSearch, VaultKind.ApiKey, "Live web search for Nemotron. Answers list the pages they used.", "TAVILY_API_KEY", new[] { "tvly-" },
            KeysUrl: "https://app.tavily.com/home", Probe: k => Bearer("https://api.tavily.com/usage", k)),

        new("github", "GitHub", GroupCloud, VaultKind.ApiKey, "Repositories and pull requests (token).", "GITHUB_TOKEN", new[] { "github_pat_", "ghp_", "gho_" },
            KeysUrl: "https://github.com/settings/personal-access-tokens", Probe: k => Header("https://api.github.com/user", "Authorization", $"Bearer {k}")),
        new("cloudflare", "Cloudflare", GroupCloud, VaultKind.ApiKey, "DNS, Workers and storage (API token).", "CLOUDFLARE_API_TOKEN",
            KeysUrl: "https://dash.cloudflare.com/profile/api-tokens", Probe: k => Bearer("https://api.cloudflare.com/client/v4/user/tokens/verify", k)),
        new("vercel-token", "Vercel", GroupCloud, VaultKind.ApiKey, "Deployments (API token).", "VERCEL_TOKEN",
            KeysUrl: "https://vercel.com/account/settings/tokens", Probe: k => Bearer("https://api.vercel.com/v2/user", k)),
        new("stripe-key", "Stripe", GroupCloud, VaultKind.ApiKey, "Payments (secret or restricted key).", "STRIPE_SECRET_KEY", new[] { "sk_live_", "sk_test_", "rk_live_", "rk_test_" },
            KeysUrl: "https://dashboard.stripe.com/apikeys"),
        new("resend", "Resend", GroupCloud, VaultKind.ApiKey, "Email sending.", "RESEND_API_KEY", new[] { "re_" }, KeysUrl: "https://resend.com/api-keys"),
        new("twilio", "Twilio", GroupCloud, VaultKind.ApiKey, "SMS and voice (auth token).", "TWILIO_AUTH_TOKEN", KeysUrl: "https://console.twilio.com/"),
        new("meta", "Meta Graph", GroupCloud, VaultKind.ApiKey, "Instagram and Facebook (access token).", "META_ACCESS_TOKEN", new[] { "EAA" },
            KeysUrl: "https://developers.facebook.com/tools/explorer/"),

        new(GoogleAdapter.Id, "Google Drive", GroupApps, VaultKind.SignIn,
            "Search and read your Drive. Paste your own Google OAuth client file (Desktop app) into the intake, then sign in.",
            KeysUrl: GoogleAdapter.ConsoleUrl),
        new("mcp-linear", "Linear", GroupApps, VaultKind.McpServer, "Issues and projects.", Url: "https://mcp.linear.app/mcp"),
        new("mcp-notion", "Notion", GroupApps, VaultKind.McpServer, "Pages and databases.", Url: "https://mcp.notion.com/mcp"),
        new("mcp-figma", "Figma", GroupApps, VaultKind.McpServer, "Design files.", Url: "https://mcp.figma.com/mcp"),
        new("mcp-canva", "Canva", GroupApps, VaultKind.McpServer, "Designs and brand kits.", Url: "https://mcp.canva.com/mcp"),
        new("mcp-vercel", "Vercel", GroupApps, VaultKind.McpServer, "Projects, deployments and logs.", Url: "https://mcp.vercel.com"),
        new("mcp-railway", "Railway", GroupApps, VaultKind.McpServer, "Services and deployments.", Url: "https://mcp.railway.com"),
        new("mcp-supabase", "Supabase", GroupApps, VaultKind.McpServer, "Databases and edge functions.", Url: "https://mcp.supabase.com/mcp"),
        new("mcp-cloudflare", "Cloudflare", GroupApps, VaultKind.McpServer, "Workers, KV, R2 and D1.", Url: "https://bindings.mcp.cloudflare.com/mcp"),
        new("mcp-sentry", "Sentry", GroupApps, VaultKind.McpServer, "Errors and issues.", Url: "https://mcp.sentry.dev/mcp"),
        new("mcp-stripe", "Stripe", GroupApps, VaultKind.McpServer, "Payments and customers.", Url: "https://mcp.stripe.com"),
        new("mcp-paypal", "PayPal", GroupApps, VaultKind.McpServer, "Invoices and payments.", Url: "https://mcp.paypal.com/mcp"),
        new("mcp-webflow", "Webflow", GroupApps, VaultKind.McpServer, "Sites and CMS.", Url: "https://mcp.webflow.com/mcp"),
        new("mcp-wix", "Wix", GroupApps, VaultKind.McpServer, "Sites and stores.", Url: "https://mcp.wix.com/mcp"),
        new("mcp-huggingface", "Hugging Face", GroupApps, VaultKind.McpServer, "Models, datasets and Spaces. No sign-in needed.", Url: "https://huggingface.co/mcp"),

        new(HolodexTools.FilesId, "Files", GroupLocal, VaultKind.LocalService, "A folder Nemotron and your destinations can read and write. Nothing outside it.",
            Url: "folder"),
        new(HolodexTools.CliId, "PC command line", GroupLocal, VaultKind.LocalService, "Nemotron runs PowerShell to configure PC Mod and other mods. You approve every command first.",
            Url: "cli"),
        new("local-ollama", "Ollama", GroupLocal, VaultKind.LocalService, "Local models.", Url: "http://127.0.0.1:11434/api/tags"),
        new("local-lmstudio", "LM Studio", GroupLocal, VaultKind.LocalService, "Local models, OpenAI-compatible.", Url: "http://127.0.0.1:1234/v1/models"),
        new("local-comfyui", "ComfyUI", GroupLocal, VaultKind.LocalService, "Local image workflows.", Url: "http://127.0.0.1:8188/system_stats"),
        // Found by Set up this PC (PcSetupScan), which holds how each one is detected.
        new(PcSetupScan.DockerId, "Docker Desktop", GroupLocal, VaultKind.LocalService, "Containers. Holodex checks that the engine answers.",
            Url: PcSetupScan.DockerUrl),
        new(PcSetupScan.LogiId, "Logi Options+", GroupLocal, VaultKind.LocalService, "Your Logitech mouse and keyboard settings.",
            Url: PcSetupScan.LogiUrl),
        new(PcSetupScan.StreamDeckId, "Elgato Stream Deck", GroupLocal, VaultKind.LocalService, "Your Stream Deck keys and profiles.",
            Url: PcSetupScan.StreamDeckUrl),
        new(PcSetupScan.StreamDeckMcpId, "Stream Deck MCP", GroupLocal, VaultKind.McpServer,
            "Stream Deck actions as tools, through Elgato's own bridge on this PC. No sign-in needed.", Url: PcSetupScan.StreamDeckMcpUrl),
        new(PcSetupScan.InputStudioId, "Input Studio", GroupLocal, VaultKind.LocalService, "Ark's input service. Present on Ark PCs only.",
            Url: PcSetupScan.InputStudioUrl),
    };

    public static VaultProvider? Find(string id) => Catalog.FirstOrDefault(p => p.Id == id);

    // ---- manifest ----

    public static List<VaultEntry> Load()
    {
        lock (Gate)
        {
            var entries = ReadManifest();
            // A Nebius key saved before the Vault existed (the drawer's key bar, or NEBIUS_API_KEY) is still the default model.
            if (entries.All(e => e.ProviderId != "nebius") && !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(NebiusTarget)))
            {
                entries.Insert(0, new VaultEntry
                {
                    Id = "nebius", ProviderId = "nebius", Label = "Nebius Token Factory", Kind = VaultKind.ApiKey,
                    Target = NebiusTarget, EnvName = "NEBIUS_API_KEY",
                    Fingerprint = ShortFingerprint(ArkStationClient.TryReadWindowsCredential(NebiusTarget)!),
                    AddedAt = DateTimeOffset.Now
                });
                WriteManifest(entries);
            }
            return entries;
        }
    }

    private static List<VaultEntry> ReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new List<VaultEntry>();
            return JsonSerializer.Deserialize<List<VaultEntry>>(File.ReadAllText(ManifestPath), JsonOptions) ?? new List<VaultEntry>();
        }
        catch (Exception) { return new List<VaultEntry>(); }
    }

    private static void WriteManifest(List<VaultEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        string tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(tmp, ManifestPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Records a connection made outside the key intake (the Google sign-in).</summary>
    public static void Save(VaultEntry entry) => Upsert(entry);

    private static void Upsert(VaultEntry entry)
    {
        lock (Gate)
        {
            var entries = ReadManifest();
            entries.RemoveAll(e => e.Id == entry.Id);
            entries.Insert(0, entry);
            WriteManifest(entries);
        }
    }

    /// <summary>An environment variable's value from the process, the user or the machine, or null when unset.</summary>
    public static string? FromEnvironment(string name)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = Environment.GetEnvironmentVariable(name, scope);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Catalog keys set by environment variable (NEBIUS_API_KEY, TAVILY_API_KEY and the rest) and not saved in the
    /// vault: Holodex uses them, so the Vault shows them. Only the names are read here, never shown values.
    /// </summary>
    public static IReadOnlyList<VaultProvider> EnvironmentKeys(IEnumerable<VaultEntry> saved)
    {
        var inVault = saved.Select(e => e.ProviderId).ToHashSet();
        return Catalog.Where(p => p.Kind == VaultKind.ApiKey && p.EnvName.Length > 0 && !inVault.Contains(p.Id) && FromEnvironment(p.EnvName) != null).ToList();
    }

    // ---- universal intake ----

    /// <summary>
    /// Reads a pasted key the way KeyMaster's classifyUniversalKey does: an <c>ENV_NAME=value</c> line (with an
    /// optional export and quotes) names its provider by the env name; otherwise the value's prefix does, longest first
    /// so sk-or-v1- and sk-ant- win over sk-.
    /// </summary>
    public static VaultDetection Classify(string raw)
    {
        string value = raw.Trim();
        string env = "";
        var m = System.Text.RegularExpressions.Regex.Match(value, @"^(?:export\s+)?([A-Z][A-Z0-9_]{2,63})\s*=\s*(['""]?)(.+)\2$");
        if (m.Success) { env = m.Groups[1].Value; value = m.Groups[3].Value.Trim(); }
        if (env.Length > 0)
        {
            string alias = env switch
            {
                "GOOGLE_API_KEY" => "GEMINI_API_KEY",
                "GH_TOKEN" => "GITHUB_TOKEN",
                "TOKEN_FACTORY_API_KEY" => "NEBIUS_API_KEY",
                "STRIPE_API_KEY" => "STRIPE_SECRET_KEY",
                "INSTAGRAM_ACCESS_TOKEN" => "META_ACCESS_TOKEN",
                "TWILIO_API_KEY" or "TWILIO_API_SECRET" => "TWILIO_AUTH_TOKEN",
                _ => env
            };
            var byEnv = Catalog.FirstOrDefault(p => p.Kind == VaultKind.ApiKey && p.EnvName == alias);
            if (byEnv != null) return new VaultDetection(byEnv, value, env, "named by " + env);
        }
        var byPrefix = Catalog
            .Where(p => p.Prefixes != null)
            .SelectMany(p => p.Prefixes!.Select(prefix => (p, prefix)))
            .Where(x => value.StartsWith(x.prefix, StringComparison.Ordinal))
            .OrderByDescending(x => x.prefix.Length)
            .FirstOrDefault();
        if (byPrefix.p != null) return new VaultDetection(byPrefix.p, value, env.Length > 0 ? env : byPrefix.p.EnvName, "recognized by its prefix");
        return new VaultDetection(null, value, env, "");
    }

    /// <summary>
    /// Classify, then for a key no prefix names, ask each provider with a read-only endpoint whether it accepts it
    /// (KeyMaster's discoverKeyService). The key goes only to those providers' own APIs.
    /// </summary>
    public static async Task<VaultDetection> DetectAsync(string raw, CancellationToken ct = default)
    {
        var found = Classify(raw);
        // A bare sk- key is OpenAI's shape but also DeepSeek's and others'; the providers decide which one it is.
        bool bareSk = found.Provider?.Id == "openai" && found.EnvName == "OPENAI_API_KEY" && !raw.Contains('=') &&
                      !found.Value.StartsWith("sk-proj-", StringComparison.Ordinal) && !found.Value.StartsWith("sk-svcacct-", StringComparison.Ordinal);
        if ((found.Provider != null && !bareSk) || found.Value.Length < 12) return found;
        var probes = Catalog.Where(p => p.Probe != null && (!bareSk || p.Id != "openai")).ToList();
        if (bareSk && await ProbeAsync(found.Provider!, found.Value, ct) == null) return found with { How = "accepted by OpenAI" };
        var results = await Task.WhenAll(probes.Select(async p => await ProbeAsync(p, found.Value, ct) == null ? p : null));
        var winner = results.FirstOrDefault(p => p != null);
        if (winner == null && bareSk) return found;
        return winner == null ? found : new VaultDetection(winner, found.Value, found.EnvName.Length > 0 && !bareSk ? found.EnvName : winner.EnvName, $"accepted by {winner.Label}");
    }

    /// <summary>Null when the provider accepted the key, else why not.</summary>
    private static async Task<string?> ProbeAsync(VaultProvider provider, string key, CancellationToken ct)
    {
        if (provider.Probe == null) return "no check for this provider";
        try
        {
            using var req = provider.Probe(key);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.IsSuccessStatusCode ? null : $"{provider.Label} answered HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return $"{provider.Label} did not answer"; }
    }

    // ---- keys ----

    private static string TargetFor(VaultProvider? provider, string envName) =>
        provider?.Id == "nebius" ? NebiusTarget
        : provider != null ? TargetPrefix + provider.Id
        : TargetPrefix + "custom:" + (envName.Length > 0 ? envName.ToLowerInvariant() : Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Stores a key in Credential Manager and records it in the manifest. Null when Windows refused the write.</summary>
    public static VaultEntry? SaveKey(VaultProvider? provider, string value, string envName, string? label = null)
    {
        string target = TargetFor(provider, envName);
        if (!ArkStationClient.TryWriteWindowsCredential(target, value)) return null;
        if (provider?.Id == "nebius") Environment.SetEnvironmentVariable("NEBIUS_API_KEY", value);
        var entry = new VaultEntry
        {
            Id = target == NebiusTarget ? "nebius" : target[TargetPrefix.Length..],
            ProviderId = provider?.Id ?? "custom",
            Label = label ?? provider?.Label ?? (envName.Length > 0 ? envName : "Custom key"),
            Kind = VaultKind.ApiKey,
            Target = target,
            EnvName = envName.Length > 0 ? envName : provider?.EnvName ?? "",
            Fingerprint = ShortFingerprint(value),
            AddedAt = DateTimeOffset.Now
        };
        Upsert(entry);
        return entry;
    }

    /// <summary>Replaces the key in place: same target, new fingerprint, rotated now, checked again from scratch.</summary>
    public static bool Rotate(VaultEntry entry, string newValue)
    {
        string value = Classify(newValue).Value;
        if (value.Length == 0 || !ArkStationClient.TryWriteWindowsCredential(entry.Target, value)) return false;
        if (entry.Target == NebiusTarget) Environment.SetEnvironmentVariable("NEBIUS_API_KEY", value);
        entry.Fingerprint = ShortFingerprint(value);
        entry.RotatedAt = DateTimeOffset.Now;
        entry.Status = "unchecked";
        entry.Detail = "";
        entry.CheckedAt = null;
        Upsert(entry);
        return true;
    }

    /// <summary>Deletes the secret from Credential Manager and the connection from the manifest.</summary>
    public static bool Delete(VaultEntry entry)
    {
        if (entry.ProviderId == GoogleAdapter.Id) GoogleAdapter.Forget();
        bool gone = !entry.HasSecret || ArkStationClient.TryDeleteWindowsCredential(entry.Target);
        if (entry.Target == NebiusTarget) Environment.SetEnvironmentVariable("NEBIUS_API_KEY", null);
        lock (Gate)
        {
            var entries = ReadManifest();
            entries.RemoveAll(e => e.Id == entry.Id);
            WriteManifest(entries);
        }
        return gone;
    }

    /// <summary>
    /// Puts the key on the clipboard and clears it 30 seconds later if it is still there. MCP sign-ins hold a token
    /// bundle the server refreshes, so only key connections copy.
    /// </summary>
    public static bool CopyToClipboard(VaultEntry entry)
    {
        if (entry.Kind != VaultKind.ApiKey) return false;
        string? secret = ArkStationClient.TryReadWindowsCredential(entry.Target);
        if (string.IsNullOrEmpty(secret)) return false;
        try { Clipboard.SetText(secret); } catch (Exception) { return false; }
        var clear = new System.Windows.Forms.Timer { Interval = 30_000 };
        clear.Tick += (_, _) =>
        {
            clear.Dispose();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear(); } catch (Exception) { }
        };
        clear.Start();
        return true;
    }

    /// <summary>Checks a connection against its provider and records works or failed with the reason.</summary>
    public static async Task<VaultEntry> VerifyAsync(VaultEntry entry, CancellationToken ct = default)
    {
        var provider = Find(entry.ProviderId);
        string? problem;
        if (entry.ProviderId == GoogleAdapter.Id) return await GoogleAdapter.VerifyAsync(entry, ct);
        if (entry.ProviderId == HolodexTools.FilesId) problem = Directory.Exists(entry.Url) ? null : "the folder is gone";
        else if (entry.ProviderId == HolodexTools.CliId) problem = null;
        else if (entry.Kind == VaultKind.LocalService && !entry.Url.StartsWith("http", StringComparison.Ordinal))
            problem = await PcSetupScan.CheckAsync(entry.ProviderId, ct);
        else if (entry.Kind == VaultKind.LocalService) problem = await LocalProbeAsync(entry.Url, ct);
        else if (entry.Kind == VaultKind.McpServer) problem = (await ListMcpToolsAsync(entry, interactive: false, ct)).Problem;
        else if (provider?.Probe == null) problem = "unchecked";
        else
        {
            string? secret = ArkStationClient.TryReadWindowsCredential(entry.Target);
            problem = string.IsNullOrEmpty(secret) ? "the key is missing from Credential Manager" : await ProbeAsync(provider, secret, ct);
        }
        entry.CheckedAt = DateTimeOffset.Now;
        entry.Status = problem == null ? "works" : problem == "unchecked" ? "unchecked" : "failed";
        if (problem != "unchecked") entry.Detail = problem ?? entry.Detail;
        Upsert(entry);
        return entry;
    }

    public static string ShortFingerprint(string value)
    {
        string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"sha256:{hex[..8]}...{hex[^8..]}";
    }

    // ---- services on this PC ----

    private static async Task<string?> LocalProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.IsSuccessStatusCode ? null : $"answered HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return "not running on this PC"; }
    }

    public static async Task<VaultEntry> ConnectLocalAsync(VaultProvider provider, CancellationToken ct = default, string? url = null)
    {
        var entry = new VaultEntry
        {
            Id = provider.Id, ProviderId = provider.Id, Label = provider.Label, Kind = VaultKind.LocalService,
            Url = url ?? provider.Url, AddedAt = DateTimeOffset.Now
        };
        return await VerifyAsync(entry, ct);
    }

    // ---- MCP servers ----

    /// <summary>
    /// Connects an MCP server through the official C# SDK's OAuth client, the same one Holodex signs in to Hivemind
    /// with: it reads the server's protected resource metadata, registers Holodex dynamically, runs PKCE in the
    /// browser and redirects to this PC's loopback. Tokens stay in Credential Manager under the entry's target; the SDK
    /// refreshes them. A server without sign-in connects directly. The entry records the tool count it answered.
    /// </summary>
    public static async Task<VaultEntry> ConnectMcpAsync(string url, string? label, string providerId, CancellationToken ct = default)
    {
        var uri = new Uri(url.Trim());
        string id = "mcp:" + uri.Host + (uri.AbsolutePath is "/" or "" ? "" : ":" + ShortFingerprint(uri.AbsolutePath)[7..15]);
        var entry = new VaultEntry
        {
            Id = id, ProviderId = providerId, Label = label ?? uri.Host, Kind = VaultKind.McpServer,
            Target = TargetPrefix + id, Url = uri.ToString(), AddedAt = DateTimeOffset.Now
        };
        var (tools, problem) = await ListMcpToolsAsync(entry, interactive: true, ct);
        if (problem != null)
        {
            ArkStationClient.TryDeleteWindowsCredential(entry.Target);
            throw new InvalidOperationException(problem);
        }
        entry.Status = "works";
        entry.CheckedAt = DateTimeOffset.Now;
        entry.Detail = $"{tools} tools";
        entry.Fingerprint = HasStoredTokens(entry) ? "signed in" : "no sign-in needed";
        Upsert(entry);
        return entry;
    }

    private static bool HasStoredTokens(VaultEntry entry) => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(entry.Target));

    private static async Task<(int Tools, string? Problem)> ListMcpToolsAsync(VaultEntry entry, bool interactive, CancellationToken ct)
    {
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(entry.Url),
                TransportMode = HttpTransportMode.AutoDetect,
                Name = entry.Label,
                OAuth = new ClientOAuthOptions
                {
                    RedirectUri = McpRedirectUri,
                    DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Holodex", ApplicationType = "native" },
                    TokenCache = new HivemindClient.WinCredTokenCache(entry.Target),
                    AuthorizationCallbackHandler = interactive
                        ? (context, token) => HivemindClient.LoopbackSignInAsync(context, McpRedirectUri, entry.Label, token)
                        : (_, _) => throw new InvalidOperationException($"{entry.Label} needs a new sign-in.")
                }
            });
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "holodex", Title = "Holodex", Version = "0.3.0" }
            }, cancellationToken: ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return (tools.Count, null);
        }
        catch (Exception ex)
        {
            string text = ex.InnerException?.Message ?? ex.Message;
            return (0, text.Length > 220 ? text[..220] : text);
        }
    }

    /// <summary>Opens a provider's key page in the browser, where the user makes a key to paste into the intake.</summary>
    public static void OpenKeysPage(VaultProvider provider)
    {
        if (provider.KeysUrl.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(provider.KeysUrl) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show("The page did not open", ex.Message, "HOLODEX // VAULT", 3600, ToastTone.Warning); }
    }

    /// <summary>True when what was pasted is a link rather than a key: an MCP server or a service on this PC.</summary>
    public static bool LooksLikeUrl(string raw, out Uri uri) =>
        Uri.TryCreate(raw.Trim(), UriKind.Absolute, out uri!) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
