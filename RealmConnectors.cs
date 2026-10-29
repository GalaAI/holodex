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

/// <summary>
/// A service Holodex can hand tasks to (Hivemind, Ryozan, and any added later). Each slot holds an address, a
/// health path, and the Windows Credential Manager target its key lives under; the key itself is never stored here.
/// <see cref="Enabled"/> stays false until the service is wired, so a slot can sit ready without being called.
/// </summary>
public sealed record RealmConnector
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string HealthPath { get; init; } = "/health";
    public string CredentialTarget { get; init; } = string.Empty;
    /// <summary>Environment variable that overrides <see cref="BaseUrl"/> without editing the file.</summary>
    public string UrlEnvVar { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    /// <summary>Task kinds this service can take once wired.</summary>
    public IReadOnlyList<TaskKind> Accepts { get; init; } = Array.Empty<TaskKind>();

    [JsonIgnore]
    public string ResolvedBaseUrl
    {
        get
        {
            string? env = string.IsNullOrEmpty(UrlEnvVar) ? null : Environment.GetEnvironmentVariable(UrlEnvVar);
            return (string.IsNullOrWhiteSpace(env) ? BaseUrl : env.Trim()).TrimEnd('/');
        }
    }
}

public sealed record RealmHealth(string ConnectorId, bool Reachable, int StatusCode, bool KeyPresent, string Detail);

/// <summary>
/// Loads the connector slots from <c>%LOCALAPPDATA%\Omniwand\connectors.json</c>, falling back to the defaults, and
/// probes their health. Adding a service is one more entry in that file; nothing else changes.
/// </summary>
public sealed class RealmConnectors
{
    public static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "connectors.json");

    public static readonly RealmConnector HivemindDefault = new()
    {
        Id = "hivemind",
        Label = "Hivemind",
        BaseUrl = "https://hivemind.arkomnitek.com",
        HealthPath = "/health",
        CredentialTarget = HivemindClient.CredentialTarget,
        UrlEnvVar = "OMNIWAND_HIVEMIND_URL",
        // Hivemind sign-in and Sessions already run through HivemindClient (OAuth over MCP, so its saved credential
        // is a token bundle, not a bearer key for Authorize); the slot gives it an address and routes task kinds.
        Enabled = true,
        Accepts = new[] { TaskKind.Reason, TaskKind.Code, TaskKind.Search }
    };

    public static readonly RealmConnector RyozanDefault = new()
    {
        Id = "ryozan",
        Label = "Ryozan",
        BaseUrl = "http://127.0.0.1:8801",
        HealthPath = "/health",
        CredentialTarget = "Omniwand:RyozanToken",
        UrlEnvVar = "OMNIWAND_RYOZAN_URL",
        Enabled = false,
        Accepts = new[] { TaskKind.Publish, TaskKind.Summarize }
    };

    public static readonly IReadOnlyList<RealmConnector> Defaults = new[] { HivemindDefault, RyozanDefault };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Func<string, string?> _readSecret;
    private readonly HttpClient _http;

    public IReadOnlyList<RealmConnector> All { get; private set; }

    public RealmConnectors(IReadOnlyList<RealmConnector> connectors, Func<string, string?> readSecret, HttpClient? http = null)
    {
        All = connectors;
        _readSecret = readSecret;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>The saved slots merged over the defaults (a saved id replaces its default; new ids are added).</summary>
    public static IReadOnlyList<RealmConnector> Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        var merged = Defaults.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<List<RealmConnector>>(File.ReadAllText(path), JsonOptions);
                foreach (var c in saved ?? new List<RealmConnector>())
                    if (!string.IsNullOrWhiteSpace(c.Id)) merged[c.Id] = c;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken file falls back to the defaults rather than taking the connectors down.
        }
        return merged.Values.ToList();
    }

    public static void Save(IReadOnlyList<RealmConnector> connectors, string? path = null)
    {
        path ??= DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(connectors, JsonOptions));
    }

    public RealmConnector? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first enabled service that takes this kind of task, or null to keep the work in Holodex.</summary>
    public RealmConnector? RouteFor(TaskKind kind) =>
        All.FirstOrDefault(c => c.Enabled && c.Accepts.Contains(kind));

    public async Task<RealmHealth> CheckHealthAsync(RealmConnector connector, CancellationToken ct = default)
    {
        bool keyPresent = !string.IsNullOrEmpty(connector.CredentialTarget) &&
                          !string.IsNullOrWhiteSpace(_readSecret(connector.CredentialTarget));
        string url = connector.ResolvedBaseUrl + connector.HealthPath;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct);
            return new RealmHealth(connector.Id, resp.IsSuccessStatusCode, (int)resp.StatusCode, keyPresent,
                resp.IsSuccessStatusCode ? "online" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new RealmHealth(connector.Id, false, 0, keyPresent, "unreachable");
        }
    }

    /// <summary>Adds the saved key as a bearer token when one exists.</summary>
    public HttpRequestMessage Authorize(RealmConnector connector, HttpRequestMessage request)
    {
        string? key = string.IsNullOrEmpty(connector.CredentialTarget) ? null : _readSecret(connector.CredentialTarget);
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
