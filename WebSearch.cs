using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Omniwand;

/// <summary>Where a web search runs: the user's Hivemind, the Holodex Vault's Tavily key, TAVILY_API_KEY, or nowhere.</summary>
public enum WebSearchProvider { None, Hivemind, VaultKey, EnvKey }

/// <summary>
/// Web search for Nemotron, through Tavily (POST https://api.tavily.com/search, Bearer tvly- key; docs checked
/// 2026-10-29). It is a real tool: when a search provider is connected, the Token Factory request offers a
/// <c>web_search</c> function, Nemotron decides when to call it (Nano and Super both answer with tool_calls on Token
/// Factory, checked 2026-10-29), Holodex runs the search and hands the results back, and the answer ends with the
/// pages it used. The search runs, in order, on (<see cref="ProviderAsync"/>):
/// <list type="number">
/// <item>the signed-in Hivemind's hosted <c>web_search</c> tool, when its tool list offers it (no key on the PC);</item>
/// <item>Tavily with the Vault key (<see cref="Target"/>);</item>
/// <item>Tavily with TAVILY_API_KEY (process, user, machine);</item>
/// <item>no search: the model is told it cannot search and to say when an answer may be out of date.</item>
/// </list>
/// Hivemind's model_invoke carries no tools, so on that route the model asks for a search with one <c>SEARCH:</c>
/// line and Holodex runs it (<see cref="SearchLine"/>). Each Tavily search costs one credit (search_depth basic); the
/// key is sent only to Tavily.
/// </summary>
internal static class WebSearch
{
    public const string Target = "Omniwand:Vault:tavily";
    public const int MaxRounds = 3;
    public const string ToolName = "web_search";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>A Tavily key on this PC (the Vault or TAVILY_API_KEY); Hivemind's search is checked by <see cref="ProviderAsync"/>.</summary>
    public static bool Available => ReadKey() != null;

    /// <summary>The Vault's Tavily key first, then TAVILY_API_KEY (process, user, machine).</summary>
    private static (string Key, WebSearchProvider From)? ReadKey()
    {
        string? vault = ArkStationClient.TryReadWindowsCredential(Target);
        if (!string.IsNullOrWhiteSpace(vault)) return (vault.Trim(), WebSearchProvider.VaultKey);
        string? env = HolodexVault.FromEnvironment("TAVILY_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : (env.Trim(), WebSearchProvider.EnvKey);
    }

    /// <summary>The first provider that can search now; the Hivemind check lists its tools once per connection and never throws.</summary>
    public static async Task<WebSearchProvider> ProviderAsync(HivemindClient hivemind, CancellationToken ct = default) =>
        await hivemind.OffersToolAsync(ToolName, ct) ? WebSearchProvider.Hivemind : ReadKey()?.From ?? WebSearchProvider.None;

    /// <summary>"Today is ..." for system prompts: the model's training data is older than today.</summary>
    public static string Today => $"Today is {DateTime.Now:dddd, MMMM d, yyyy}.";

    /// <summary>What the model is told when nothing can search: answer, and say when the answer may be out of date.</summary>
    public static string NoSearchNote =>
        $" {Today} You cannot search the web here. If the question needs current facts, answer from what you know and say plainly that it may be out of date.";

    /// <summary>The instruction that lets a tool-less model ask for a search (Hivemind's model_invoke).</summary>
    public static string SearchLineInstruction =>
        $" {Today} Your training data is older. If answering needs current facts from the web (news, prices, releases, schedules, people), reply with exactly one line, SEARCH: followed by a short web search query, and nothing else. Otherwise answer directly.";

    /// <summary>The query when a reply is a search request (a "SEARCH: ..." line), else null.</summary>
    internal static string? SearchLine(string reply)
    {
        var m = Regex.Match(reply.Trim(), @"^\**\s*SEARCH\s*:\s*\**\s*(?<q>[^\r\n]{2,300})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["q"].Value.Trim().Trim('"', '*', '`').Trim() : null;
    }

    /// <summary>Offers the web_search tool on a chat payload and tells the model when to use it and to cite.</summary>
    public static void Offer(JsonObject payload)
    {
        if (payload["tools"] is not JsonArray tools) payload["tools"] = tools = new JsonArray();
        tools.Add(
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ToolName,
                    ["description"] = $"Search the live web. Today is {DateTime.Now:MMMM d, yyyy}; your training data is older, so use it for news, prices, releases, schedules, people and anything that may have changed, and put the current year in queries about recent events.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "A short web search query." },
                            ["topic"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("general", "news", "finance") }
                        },
                        ["required"] = new JsonArray("query")
                    }
                }
            });
        payload["tool_choice"] = "auto";
        if (payload["messages"] is JsonArray messages && messages.Count > 0 && messages[0] is JsonObject system && system["role"]?.GetValue<string>() == "system")
            system["content"] = system["content"]?.GetValue<string>() +
                $" Today is {DateTime.Now:dddd, MMMM d, yyyy}. You can call web_search when the question needs current or outside facts. Answer from the results and do not invent sources.";
    }

    /// <summary>
    /// When a completion asked for web_search, runs each search, appends the assistant turn and the tool results to
    /// the payload's messages, and returns true so the caller asks again. Sources gathers each result's URL.
    /// </summary>
    public static async Task<bool> RunToolCallsAsync(JsonObject payload, string completionBody, List<string> sources, HivemindClient hivemind, CancellationToken ct = default)
    {
        var message = JsonNode.Parse(completionBody)?["choices"]?[0]?["message"] as JsonObject;
        if (message?["tool_calls"] is not JsonArray calls || calls.Count == 0 || payload["messages"] is not JsonArray messages) return false;

        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = message["content"]?.DeepClone(),
            ["tool_calls"] = calls.DeepClone()
        });
        foreach (var call in calls.OfType<JsonObject>())
        {
            string id = call["id"]?.GetValue<string>() ?? "";
            string name = call["function"]?["name"]?.GetValue<string>() ?? "";
            string result;
            if (name != ToolName)
            {
                JsonNode? local = null;
                try { local = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}"); }
                catch (JsonException) { }
                result = await HolodexTools.RunAsync(name, local, ct);
            }
            else
            {
                string query = "", topic = "general";
                try
                {
                    var args = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}");
                    query = args?["query"]?.GetValue<string>() ?? "";
                    topic = args?["topic"]?.GetValue<string>() is "news" or "finance" ? args!["topic"]!.GetValue<string>() : "general";
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
                result = query.Length == 0 ? "No query was given." : await SearchAsync(query, topic, sources, hivemind, ct);
            }
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
        }
        return true;
    }

    /// <summary>
    /// One search as numbered results for the model: the signed-in Hivemind's hosted web_search when it offers one,
    /// else Tavily with the Vault key, else with TAVILY_API_KEY. A Hivemind search that fails hands over to the key.
    /// Failures come back as text the model can report.
    /// </summary>
    public static async Task<string> SearchAsync(string query, string topic, List<string> sources, HivemindClient hivemind, CancellationToken ct = default)
    {
        string? hivemindError = null;
        if (await hivemind.OffersToolAsync(ToolName, ct))
        {
            var found = await hivemind.WebSearchAsync(query, 5, ct);
            if (found != null) return Format(query, found, sources);
            hivemindError = hivemind.LastError;
        }
        if (ReadKey() is not { } tavily)
            return hivemindError != null
                ? $"Web search did not answer on Hivemind: {hivemindError}"
                : "Web search is not connected: Hivemind offers no web_search, and there is no Tavily key in the Vault or TAVILY_API_KEY.";
        string key = tavily.Key;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(new JsonObject
            {
                ["query"] = query,
                ["topic"] = topic,
                ["search_depth"] = "basic",
                ["max_results"] = 5,
                ["include_answer"] = false
            }.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return $"Web search failed: Tavily answered HTTP {(int)res.StatusCode}.";
            using var doc = JsonDocument.Parse(body);
            return Format(query, HivemindClient.ReadSearchResults(doc.RootElement), sources);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"Web search did not answer: {ex.Message}";
        }
    }

    /// <summary>Numbered results for the model (Tavily's and Hivemind's alike); each URL joins <paramref name="sources"/>.</summary>
    internal static string Format(string query, IReadOnlyList<WebSearchResult> results, List<string> sources)
    {
        if (results.Count == 0) return $"No web results for \"{query}\".";
        var text = new StringBuilder($"Web results for \"{query}\":\n");
        int n = 0;
        foreach (var r in results.Take(6))
        {
            string content = r.Content.Length > 700 ? r.Content[..700] + "..." : r.Content;
            text.Append($"[{++n}] {r.Title}\n{r.Url}\n{content}\n\n");
            if (!sources.Contains(r.Url)) sources.Add(r.Url);
        }
        return text.ToString();
    }

    /// <summary>The answer with the pages it drew on listed after it (the card's Sources row reads URLs from the text).</summary>
    public static string WithSources(string answer, IReadOnlyList<string> sources)
    {
        var missing = sources.Where(u => !answer.Contains(u, StringComparison.Ordinal)).Take(4).ToList();
        return missing.Count == 0 ? answer : answer + "\n\nSources:\n" + string.Join("\n", missing.Select(u => "- " + u));
    }
}
