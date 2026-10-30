using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

[Flags]
public enum ModelCapability
{
    None = 0,
    Chat = 1,
    Reason = 2,
    Vision = 4,
    Detect = 8,
    Segment = 16,
    Code = 32
}

/// <summary>
/// A place models come from. Every provider speaks the OpenAI-compatible <c>GET {BaseUrl}/models</c> list, which is
/// how Holodex notices a new model: Nebius Token Factory (the default), LM Studio, or a local vision server that
/// serves YOLO and SAM 2. The key is read from Windows Credential Manager or an environment variable, never stored here.
/// </summary>
public sealed record ModelProvider
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string CredentialTarget { get; init; } = string.Empty;
    public string KeyEnvVar { get; init; } = string.Empty;
    /// <summary>Models assumed present even when the list call fails (Nebius keeps its Nemotron set this way).</summary>
    public IReadOnlyList<string> BuiltIn { get; init; } = Array.Empty<string>();
}

public sealed record ModelEntry(string ProviderId, string ModelId, ModelCapability Capabilities)
{
    public string Key => ProviderId + ":" + ModelId;
    public bool Can(ModelCapability c) => (Capabilities & c) == c;
}

/// <summary>Which provider and model a task goes to, and why.</summary>
public sealed record RouteDecision(TaskKind Kind, string ProviderId, string ModelId, string Reason)
{
    public bool Available => !string.IsNullOrEmpty(ModelId);
    public static RouteDecision None(TaskKind kind, string reason) => new(kind, string.Empty, string.Empty, reason);
}

/// <summary>
/// Every model Holodex can reach, refreshed from each provider's model list. <see cref="Refresh"/> reports the
/// models that were not there before, so the UI can grow its model list and say what arrived.
/// </summary>
public sealed class ModelInventory
{
    public const string NebiusProviderId = "nebius";

    public static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "model-providers.json");

    public static readonly ModelProvider NebiusDefault = new()
    {
        Id = NebiusProviderId,
        Label = "Nebius Token Factory",
        BaseUrl = ArkStationClient.DefaultNebiusBaseUrl,
        CredentialTarget = "Omniwand:NebiusTokenFactory",
        KeyEnvVar = "NEBIUS_API_KEY",
        BuiltIn = ArkStationClient.BuiltInNemotronModels
    };

    public static readonly ModelProvider LmStudioDefault = new()
    {
        Id = "lmstudio",
        Label = "LM Studio",
        BaseUrl = "http://127.0.0.1:1234/v1"
    };

    /// <summary>A local vision server (YOLO detection, SAM 2 segmentation) that lists its models the same way. 8856, because Ark agent dispatch owns 8850.</summary>
    public static readonly ModelProvider LocalVisionDefault = new()
    {
        Id = "local-vision",
        Label = "Local vision",
        BaseUrl = "http://127.0.0.1:8856/v1"
    };

    /// <summary>
    /// Ollama's OpenAI-compatible list (<c>GET /v1/models</c>). Not a default: it joins when the user connects Ollama in
    /// Set up this PC, which saves it to the providers file so it stays after a restart.
    /// </summary>
    public static readonly ModelProvider OllamaDefault = new()
    {
        Id = "ollama",
        Label = "Ollama",
        BaseUrl = "http://127.0.0.1:11434/v1"
    };

    public static readonly IReadOnlyList<ModelProvider> Defaults = new[] { NebiusDefault, LmStudioDefault, LocalVisionDefault };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<string, string?> _readSecret;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private List<ModelEntry> _models = new();
    private List<ModelProvider> _providers;

    /// <summary>The providers asked on each refresh; <see cref="AddProvider"/> grows it while Holodex runs.</summary>
    public IReadOnlyList<ModelProvider> Providers
    {
        get { lock (_lock) return _providers.ToList(); }
    }

    public ModelInventory(IReadOnlyList<ModelProvider> providers, Func<string, string?> readSecret, HttpClient? http = null)
    {
        _providers = providers.ToList();
        _readSecret = readSecret;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _models = providers.SelectMany(p => p.BuiltIn.Select(id => new ModelEntry(p.Id, id, InferCapabilities(id)))).ToList();
    }

    public IReadOnlyList<ModelEntry> Models
    {
        get { lock (_lock) return _models.ToList(); }
    }

    /// <summary>Saved providers merged over the defaults (same id replaces, new ids are added).</summary>
    public static IReadOnlyList<ModelProvider> LoadProviders(string? path = null)
    {
        path ??= DefaultConfigPath;
        var merged = Defaults.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<List<ModelProvider>>(File.ReadAllText(path), JsonOptions);
                foreach (var p in saved ?? new List<ModelProvider>())
                    if (!string.IsNullOrWhiteSpace(p.Id)) merged[p.Id] = p;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken file keeps the defaults.
        }
        return merged.Values.ToList();
    }

    /// <summary>
    /// Adds a provider for this run (a local server the user just connected). False when one with the same id is
    /// already asked, so connecting LM Studio again changes nothing.
    /// </summary>
    public bool AddProvider(ModelProvider provider)
    {
        lock (_lock)
        {
            if (_providers.Any(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase))) return false;
            _providers.Add(provider);
            return true;
        }
    }

    /// <summary>Writes a provider into the saved providers file (same id replaces), so it is asked after a restart too.</summary>
    public static void SaveProvider(ModelProvider provider, string? path = null)
    {
        path ??= DefaultConfigPath;
        var saved = new List<ModelProvider>();
        try
        {
            if (File.Exists(path))
                saved = JsonSerializer.Deserialize<List<ModelProvider>>(File.ReadAllText(path), JsonOptions) ?? saved;
        }
        catch (JsonException)
        {
            // A broken file is replaced by the providers saved from now on.
        }
        saved.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        saved.Add(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Lists every provider's models and returns the ones that are new since the last refresh. A provider that does
    /// not answer keeps the models it had, so a laptop going offline does not shrink the list.
    /// </summary>
    public async Task<IReadOnlyList<ModelEntry>> Refresh(CancellationToken ct = default)
    {
        var providers = Providers;
        var lists = await Task.WhenAll(providers.Select(p => ListAsync(p, ct)));
        lock (_lock)
        {
            var known = new HashSet<string>(_models.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
            var next = new List<ModelEntry>(_models);
            var added = new List<ModelEntry>();
            for (int i = 0; i < providers.Count; i++)
            {
                if (lists[i] is not { } ids) continue;
                foreach (string id in ids)
                {
                    var entry = new ModelEntry(providers[i].Id, id, InferCapabilities(id));
                    if (known.Add(entry.Key))
                    {
                        next.Add(entry);
                        added.Add(entry);
                    }
                }
            }
            _models = next;
            return added;
        }
    }

    private async Task<IReadOnlyList<string>?> ListAsync(ModelProvider provider, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, provider.BaseUrl.TrimEnd('/') + "/models");
            string? key = ReadKey(provider);
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            return data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? ReadKey(ModelProvider provider)
    {
        string? env = string.IsNullOrEmpty(provider.KeyEnvVar) ? null : Environment.GetEnvironmentVariable(provider.KeyEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return string.IsNullOrEmpty(provider.CredentialTarget) ? null : _readSecret(provider.CredentialTarget);
    }

    /// <summary>What a model can do, read from its id. Every model can at least chat unless it is a pure detector or segmenter.</summary>
    public static ModelCapability InferCapabilities(string modelId)
    {
        string id = modelId.ToLowerInvariant();
        string tail = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        if (tail.StartsWith("yolo") || id.Contains("rt-detr") || id.Contains("detr") || id.Contains("grounding-dino"))
            return ModelCapability.Detect;
        if (tail == "sam" || tail.StartsWith("sam2") || tail.StartsWith("sam-") || tail.StartsWith("sam_") ||
            id.Contains("segment-anything"))
            return ModelCapability.Segment;

        var caps = ModelCapability.Chat;
        if (id.Contains("-vl") || id.Contains("vision") || id.Contains("omni") || id.Contains("llava"))
            caps |= ModelCapability.Vision;
        if (id.Contains("coder") || id.Contains("code") || id.Contains("devstral"))
            caps |= ModelCapability.Code;
        if (id.Contains("super") || id.Contains("ultra") || id.Contains("reason") || id.Contains("thinking") ||
            id.Contains("-r1") || id.Contains("120b") || id.Contains("550b"))
            caps |= ModelCapability.Reason;
        return caps;
    }
}

/// <summary>
/// Picks the provider and model for a task. Language work stays on Nebius (Nemotron) by default; detection and
/// segmentation go to whichever provider serves a YOLO or SAM model. Gala, the orchestrator, runs on Nemotron Super.
/// </summary>
public sealed class TaskRouter
{
    public const string GalaModel = ArkStationClient.DefaultNemotronSuperModel;
    public const string FastModel = ArkStationClient.DefaultNemotronFastModel;
    public const string VisionModel = "nvidia/Nemotron-3.5-Super-VL";

    private readonly ModelInventory _inventory;

    public TaskRouter(ModelInventory inventory) => _inventory = inventory;

    public RouteDecision Route(HolodexTask task) => Route(task.Kind);

    public RouteDecision Route(TaskKind kind)
    {
        var models = _inventory.Models;
        return kind switch
        {
            TaskKind.Chat or TaskKind.Search or TaskKind.Summarize =>
                Pick(kind, models, FastModel, ModelCapability.Chat, "fast tier for quick replies"),
            TaskKind.Reason or TaskKind.Craft or TaskKind.Publish or TaskKind.Teach =>
                Pick(kind, models, GalaModel, ModelCapability.Reason, "Nemotron Super for planning and writing"),
            TaskKind.Vision =>
                Pick(kind, models, VisionModel, ModelCapability.Vision, "vision model for the attached image"),
            TaskKind.Code =>
                PickCapable(kind, models, ModelCapability.Code, "code model")
                ?? Pick(kind, models, GalaModel, ModelCapability.Reason, "no code model listed, so Nemotron Super"),
            TaskKind.Detect =>
                PickCapable(kind, models, ModelCapability.Detect, "object detector")
                ?? Pick(kind, models, VisionModel, ModelCapability.Vision, "no detector listed, so the vision model finds objects"),
            TaskKind.Segment =>
                PickCapable(kind, models, ModelCapability.Segment, "segmenter")
                ?? RouteDecision.None(kind, "no segmentation model listed; start a SAM 2 server to enable cutouts"),
            _ => Pick(kind, models, GalaModel, ModelCapability.Reason, "Gala's default")
        };
    }

    /// <summary>The preferred Nebius model when listed, else any Nebius model with the capability, else any provider's.</summary>
    private static RouteDecision Pick(TaskKind kind, IReadOnlyList<ModelEntry> models, string preferred, ModelCapability need, string reason)
    {
        var exact = models.FirstOrDefault(m => m.ProviderId == ModelInventory.NebiusProviderId &&
                                               string.Equals(m.ModelId, preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return new RouteDecision(kind, exact.ProviderId, exact.ModelId, reason);
        return PickCapable(kind, models, need, reason) ?? RouteDecision.None(kind, "no model can do this yet");
    }

    private static RouteDecision? PickCapable(TaskKind kind, IReadOnlyList<ModelEntry> models, ModelCapability need, string reason)
    {
        var match = models.Where(m => m.Can(need))
            .OrderBy(m => m.ProviderId == ModelInventory.NebiusProviderId ? 0 : 1)
            .FirstOrDefault();
        return match is null ? null : new RouteDecision(kind, match.ProviderId, match.ModelId, reason);
    }
}
