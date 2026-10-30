using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Omniwand;

/// <summary>How a destination receives a Handoff Scroll and sends its reply back.</summary>
internal enum DestinationKind { HolodexChat, OpenAiCompatible, Anthropic, Hivemind, SaveOnPc }

/// <summary>
/// One place an AInbox Handoff Scroll can go on a public install. <see cref="KeyTarget"/> names where its key lives
/// (a Vault target, or env:NAME); the key itself is read only when the Scroll is sent.
/// </summary>
internal sealed record HandoffDestination(string Id, string Label, DestinationKind Kind, string Detail,
    string BaseUrl = "", string KeyTarget = "", string ModelPreference = "");

/// <summary>
/// The destination table a public install's AInbox routes to, built from what the user has connected rather than
/// hard-coded: Holodex chat (NVIDIA Nemotron on Token Factory) always and first, then every chat agent connected in
/// the Vault or set by environment variable, then Hivemind when signed in, then Save on this PC. Sending drops the
/// Scroll into the Holodex session and the destination's reply comes back there. An Ark keeps its pods instead.
/// </summary>
internal static class HandoffDestinations
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static readonly HandoffDestination HolodexChat = new("holodex", "Holodex chat", DestinationKind.HolodexChat,
        "NVIDIA Nemotron on Nebius Token Factory answers in the Holodex session.");

    public static readonly HandoffDestination SaveOnPc = new("save", "Save on this PC", DestinationKind.SaveOnPc,
        "A sealed Markdown file in Documents\\Holodex\\Scrolls. Nothing is sent.");

    /// <summary>
    /// Agents a Vault connection makes available: provider id, label, endpoint and a model preference (a regex over
    /// the provider's own /models list, so no model id is assumed). OpenAI-compatible endpoints from each provider's
    /// docs; Anthropic uses its Messages API.
    /// </summary>
    private static readonly (string Provider, string Label, DestinationKind Kind, string BaseUrl, string Prefer)[] Agents =
    {
        ("openai", "ChatGPT (OpenAI)", DestinationKind.OpenAiCompatible, "https://api.openai.com/v1", @"^gpt-(5|4\.1|4o)(-mini)?$"),
        ("anthropic", "Claude (Anthropic)", DestinationKind.Anthropic, "https://api.anthropic.com/v1", @"sonnet|opus|haiku"),
        ("gemini", "Gemini (Google)", DestinationKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", @"gemini-.*flash"),
        ("openrouter", "OpenRouter", DestinationKind.OpenAiCompatible, "https://openrouter.ai/api/v1", @"^openai/gpt|^anthropic/claude"),
        ("xai", "Grok (xAI)", DestinationKind.OpenAiCompatible, "https://api.x.ai/v1", @"^grok"),
        ("groq", "Groq", DestinationKind.OpenAiCompatible, "https://api.groq.com/openai/v1", @"llama|gpt-oss"),
        ("deepseek", "DeepSeek", DestinationKind.OpenAiCompatible, "https://api.deepseek.com", @"deepseek-chat"),
        ("mistral", "Mistral", DestinationKind.OpenAiCompatible, "https://api.mistral.ai/v1", @"mistral-(large|medium|small)-latest"),
        ("together", "Together AI", DestinationKind.OpenAiCompatible, "https://api.together.xyz/v1", @"instruct|chat"),
        ("fireworks", "Fireworks AI", DestinationKind.OpenAiCompatible, "https://api.fireworks.ai/inference/v1", @"instruct"),
        ("cerebras", "Cerebras", DestinationKind.OpenAiCompatible, "https://api.cerebras.ai/v1", @"llama|qwen|gpt-oss"),
        ("local-lmstudio", "LM Studio on this PC", DestinationKind.OpenAiCompatible, "http://127.0.0.1:1234/v1", @"."),
        ("local-ollama", "Ollama on this PC", DestinationKind.OpenAiCompatible, "http://127.0.0.1:11434/v1", @"."),
    };

    /// <summary>The destinations right now: Holodex chat first, then connected agents, Hivemind, and Save on this PC.</summary>
    public static IReadOnlyList<HandoffDestination> Current(ArkStationClient ark)
    {
        var list = new List<HandoffDestination> { HolodexChat };
        var saved = HolodexVault.Load();
        var fromEnv = HolodexVault.EnvironmentKeys(saved).Select(p => p.Id).ToHashSet();
        foreach (var (provider, label, kind, baseUrl, prefer) in Agents)
        {
            var entry = saved.FirstOrDefault(e => e.ProviderId == provider);
            bool local = provider.StartsWith("local-", StringComparison.Ordinal);
            string keyTarget = entry is { Kind: VaultKind.ApiKey } ? entry.Target
                : fromEnv.Contains(provider) ? "env:" + HolodexVault.Find(provider)!.EnvName
                : "";
            if (keyTarget.Length == 0 && !(local && entry != null)) continue;
            list.Add(new HandoffDestination(provider, label, kind,
                local ? $"{label}, through its OpenAI-compatible endpoint." : $"{label} with your own key from the Vault.",
                baseUrl, keyTarget, prefer));
        }
        if (OffersHivemindAgent(ark.Hivemind.State == HivemindState.Connected || HivemindClient.HasSavedSignIn, HivemindClient.GrantedScopes))
            list.Add(new HandoffDestination("hivemind", "Hivemind", DestinationKind.Hivemind,
                $"Gala in a new Hivemind Session on {HivemindClient.EndpointHost}; the reply lands there and in Holodex Sessions."));
        list.Add(SaveOnPc);
        return list;
    }

    /// <summary>
    /// Whether the Hivemind (Gala) destination is offered: only for a sign-in that may ask agents (runtime.execute).
    /// A Hivemind account without it (the public instance's accounts) cannot start a Gala session, so its handoffs go
    /// to Holodex chat, where Nemotron answers through the same account.
    /// </summary>
    internal static bool OffersHivemindAgent(bool signedIn, IReadOnlySet<string>? granted) =>
        signedIn && HivemindClient.Granted(granted, "runtime.execute");

    private static string? ReadKey(HandoffDestination d) =>
        d.KeyTarget.StartsWith("env:", StringComparison.Ordinal) ? HolodexVault.FromEnvironment(d.KeyTarget[4..])
        : d.KeyTarget.Length > 0 ? ArkStationClient.TryReadWindowsCredential(d.KeyTarget)
        : null;

    private const string SystemPrompt =
        "You are receiving a Handoff Scroll from Holodex AInbox, a desktop companion. The Scroll is Markdown: an ask, " +
        "labelled fields and the captures it carries. Do what it asks and answer directly and concisely.";

    /// <summary>Sends the Scroll to the destination and returns its reply (or why it failed) and who answered.</summary>
    public static async Task<(bool Ok, string Text, string AnsweredBy)> DeliverAsync(HandoffDestination d, string title, string scroll, ArkStationClient ark, CancellationToken ct = default)
    {
        try
        {
            switch (d.Kind)
            {
                case DestinationKind.HolodexChat:
                {
                    var answer = await ark.AskAsync(scroll, systemPrompt: SystemPrompt);
                    return (answer.Ok, answer.Output, $"{ModelChip.ShortName(answer.Model)} // {answer.RouteLabel}");
                }
                case DestinationKind.Hivemind:
                {
                    var ask = await ark.Hivemind.AskAgentAsync(null, "gala", scroll, Guid.NewGuid(), title, ct);
                    return ask == null
                        ? (false, ark.Hivemind.LastError.Length > 0 ? ark.Hivemind.LastError : "Hivemind did not take the handoff.", "HIVEMIND")
                        : (true, $"Posted to a new Hivemind Session ({ask.Status}). Gala answers there; open it from Sessions.", "HIVEMIND // GALA");
                }
                case DestinationKind.Anthropic:
                    return await AskAnthropicAsync(d, scroll, ct);
                case DestinationKind.OpenAiCompatible:
                    return await AskOpenAiCompatibleAsync(d, scroll, ct);
                default:
                    return (false, "Save on this PC sends nothing.", "");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return (false, $"{d.Label} did not answer: {ex.Message}", d.Label.ToUpperInvariant());
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, HandoffDestination d, string? key)
    {
        var req = new HttpRequestMessage(method, url);
        if (d.Kind == DestinationKind.Anthropic)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    /// <summary>The provider's own model list, filtered by the destination's preference; the first match answers.</summary>
    private static async Task<string> PickModelAsync(HandoffDestination d, string? key, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, $"{d.BaseUrl}/models", d, key);
        using var res = await Http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{d.Label} /models answered HTTP {(int)res.StatusCode}");
        var ids = (JsonNode.Parse(body)?["data"] as JsonArray ?? new JsonArray())
            .Select(m => m?["id"]?.GetValue<string>()?.Replace("models/", "") ?? "")
            .Where(id => id.Length > 0).ToList();
        var rx = new System.Text.RegularExpressions.Regex(d.ModelPreference, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return ids.FirstOrDefault(rx.IsMatch) ?? ids.FirstOrDefault() ?? throw new InvalidOperationException($"{d.Label} lists no models");
    }

    private static async Task<(bool, string, string)> AskOpenAiCompatibleAsync(HandoffDestination d, string scroll, CancellationToken ct)
    {
        string? key = ReadKey(d);
        string model = await PickModelAsync(d, key, ct);
        using var req = Request(HttpMethod.Post, $"{d.BaseUrl}/chat/completions", d, key);
        req.Content = new StringContent(new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = scroll }
            }
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, $"{d.Label} answered HTTP {(int)res.StatusCode}.", $"{model.ToUpperInvariant()} // {d.Label.ToUpperInvariant()}");
        string text = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
        return (text.Length > 0, text.Length > 0 ? text : $"{d.Label} returned no answer.", $"{model.ToUpperInvariant()} // {d.Label.ToUpperInvariant()}");
    }

    private static async Task<(bool, string, string)> AskAnthropicAsync(HandoffDestination d, string scroll, CancellationToken ct)
    {
        string? key = ReadKey(d);
        string model = await PickModelAsync(d, key, ct);
        using var req = Request(HttpMethod.Post, $"{d.BaseUrl}/messages", d, key);
        req.Content = new StringContent(new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 2048,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = scroll } }
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, $"{d.Label} answered HTTP {(int)res.StatusCode}.", $"{model.ToUpperInvariant()} // CLAUDE");
        string text = string.Concat((JsonNode.Parse(body)?["content"] as JsonArray ?? new JsonArray())
            .Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b?["text"]?.GetValue<string>())).Trim();
        return (text.Length > 0, text.Length > 0 ? text : "Claude returned no answer.", $"{model.ToUpperInvariant()} // CLAUDE");
    }
}
