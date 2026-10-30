using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Omniwand;

/// <summary>
/// Web search for Nemotron, through Tavily (POST https://api.tavily.com/search, Bearer tvly- key; docs checked
/// 2026-10-29). It is a real tool: when the user has a Tavily key in the Vault (<see cref="Target"/>), the Token Factory
/// request offers a <c>web_search</c> function, Nemotron decides when to call it (Nano and Super both answer with
/// tool_calls on Token Factory, checked 2026-10-29), Holodex runs the search and hands the results back, and the
/// answer ends with the pages it used. With no Tavily key nothing changes. Each search costs one Tavily credit
/// (search_depth basic); the key is sent only to Tavily.
/// </summary>
internal static class WebSearch
{
    public const string Target = "Omniwand:Vault:tavily";
    public const int MaxRounds = 3;
    private const string ToolName = "web_search";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool Available => !string.IsNullOrWhiteSpace(ReadKey());

    /// <summary>TAVILY_API_KEY (process, user, machine) first, as NEBIUS_API_KEY is read, then the Vault.</summary>
    private static string? ReadKey() =>
        HolodexVault.FromEnvironment("TAVILY_API_KEY") ?? ArkStationClient.TryReadWindowsCredential(Target);

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
    public static async Task<bool> RunToolCallsAsync(JsonObject payload, string completionBody, List<string> sources, CancellationToken ct = default)
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
                result = query.Length == 0 ? "No query was given." : await SearchAsync(query, topic, sources, ct);
            }
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
        }
        return true;
    }

    /// <summary>One Tavily search, as numbered results for the model. Failures come back as text the model can report.</summary>
    public static async Task<string> SearchAsync(string query, string topic, List<string> sources, CancellationToken ct = default)
    {
        string? key = ReadKey();
        if (string.IsNullOrWhiteSpace(key)) return "Web search is not connected: no Tavily key in the Vault or TAVILY_API_KEY.";
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
            var results = JsonNode.Parse(body)?["results"] as JsonArray;
            if (results == null || results.Count == 0) return $"No web results for \"{query}\".";
            var text = new StringBuilder($"Web results for \"{query}\":\n");
            int n = 0;
            foreach (var r in results.OfType<JsonObject>())
            {
                string url = r["url"]?.GetValue<string>() ?? "";
                string content = r["content"]?.GetValue<string>() ?? "";
                if (content.Length > 700) content = content[..700] + "...";
                text.Append($"[{++n}] {r["title"]?.GetValue<string>()}\n{url}\n{content}\n\n");
                if (url.Length > 0 && !sources.Contains(url)) sources.Add(url);
            }
            return text.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"Web search did not answer: {ex.Message}";
        }
    }

    /// <summary>The answer with the pages it drew on listed after it (the card's Sources row reads URLs from the text).</summary>
    public static string WithSources(string answer, IReadOnlyList<string> sources)
    {
        var missing = sources.Where(u => !answer.Contains(u, StringComparison.Ordinal)).Take(4).ToList();
        return missing.Count == 0 ? answer : answer + "\n\nSources:\n" + string.Join("\n", missing.Select(u => "- " + u));
    }
}
