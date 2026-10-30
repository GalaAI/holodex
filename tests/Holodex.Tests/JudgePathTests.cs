using System.Text.Json;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The judge path: a fresh install with no key and no Ark answers through the user's own Hivemind (model_invoke).
/// Covers the route order, the model_invoke envelope and its error mapping, the scopes a sign-in asks for, and the
/// web search pieces the Hivemind route uses.
/// </summary>
public class JudgePathTests
{
    // ---- Route order: Ark gateway, then a local Nebius key, then Hivemind, then the sign-in prompt -----------------

    [Theory]
    [InlineData(true, true, true, ArkStationClient.RouteArkApi)]
    [InlineData(true, false, false, ArkStationClient.RouteArkApi)]
    [InlineData(false, true, true, ArkStationClient.RouteNebiusDirect)]
    [InlineData(false, true, false, ArkStationClient.RouteNebiusDirect)]
    [InlineData(false, false, true, ArkStationClient.RouteHivemind)]
    [InlineData(false, false, false, ArkStationClient.RouteNone)]
    public void Text_asks_try_ark_then_key_then_hivemind_then_none(bool arkApi, bool nebiusKey, bool hivemind, string expected) =>
        Assert.Equal(expected, ArkStationClient.PlanTextRoute(arkApi, nebiusKey, hivemind));

    [Fact]
    public void Every_route_has_the_label_cards_and_chips_show()
    {
        Assert.Equal("VIA ARK API", ArkStationClient.RouteLabelFor(ArkStationClient.RouteArkApi));
        Assert.Equal("NEBIUS DIRECT", ArkStationClient.RouteLabelFor(ArkStationClient.RouteNebiusDirect));
        Assert.Equal("VIA HIVEMIND", ArkStationClient.RouteLabelFor(ArkStationClient.RouteHivemind));
        Assert.Equal("NOT CONNECTED", ArkStationClient.RouteLabelFor(ArkStationClient.RouteNone));
        var answer = new ArkStationClient.ModelAnswer(true, "hi", "nvidia/nemotron-3-super-120b-a12b", ArkStationClient.RouteHivemind);
        Assert.Equal("NEMOTRON 3 SUPER // VIA HIVEMIND", $"{ModelChip.ShortName(answer.Model).ToUpperInvariant()} // {answer.RouteLabel}");
    }

    // ---- Scopes ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sign_in_asks_for_model_invoke_and_web_search_only_when_offered()
    {
        string[] live = { "pc.observe", "runtime.read", "runtime.execute", "model.invoke", "navigate.read" };
        Assert.Equal(new[] { "runtime.read", "runtime.execute", "model.invoke" }, HivemindClient.SelectScopes(live));
        Assert.Equal(new[] { "runtime.read", "runtime.execute", "model.invoke", "web.search" }, HivemindClient.SelectScopes(live.Append("web.search")));
        // A server that lists no scopes gets the required ones, never the optional one.
        Assert.Equal(new[] { "runtime.read", "runtime.execute", "model.invoke" }, HivemindClient.SelectScopes(null));
        // A Hivemind that predates model.invoke is not asked for it.
        Assert.Equal(new[] { "runtime.read", "runtime.execute" }, HivemindClient.SelectScopes(new[] { "runtime.read", "runtime.execute" }));
    }

    // ---- model_invoke envelope and error mapping --------------------------------------------------------------------

    private const string Completion = """
        {"ok":true,"receiptId":"rcpt-123","data":{"id":"c1","model":"nvidia/nemotron-3-super-120b-a12b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Nemotron is NVIDIA's open model family."},"finish_reason":"stop"}]}}
        """;

    [Fact]
    public void A_completion_answers_with_its_text_model_and_receipt()
    {
        var a = HivemindClient.ReadModelInvocation(Completion, false, "ark/nemotron-3-super");
        Assert.True(a.Ok);
        Assert.Equal("Nemotron is NVIDIA's open model family.", a.Text);
        Assert.Equal("nvidia/nemotron-3-super-120b-a12b", a.Model);
        Assert.Equal("rcpt-123", a.ReceiptId);
        Assert.Equal(HivemindModelError.None, a.Kind);
    }

    [Fact]
    public void Reasoning_only_answer_is_read_and_a_missing_model_falls_back_to_the_request()
    {
        const string body = """{"ok":true,"data":{"choices":[{"message":{"content":"","reasoning_content":"Thought answer."}}]}}""";
        var a = HivemindClient.ReadModelInvocation(body, false, "ark/nemotron-3-nano");
        Assert.True(a.Ok);
        Assert.Equal("Thought answer.", a.Text);
        Assert.Equal("ark/nemotron-3-nano", a.Model);
    }

    [Fact]
    public void Empty_answer_is_an_upstream_error()
    {
        var a = HivemindClient.ReadModelInvocation("""{"ok":true,"data":{"model":"m","choices":[]}}""", false, "m");
        Assert.False(a.Ok);
        Assert.Equal(HivemindModelError.Upstream, a.Kind);
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"scope-denied","requiredScope":"model.invoke"}""", true)]
    [InlineData("Hivemind session is missing model.invoke.", true)]
    [InlineData("""{"ok":false,"error":"insufficient_scope","requiredScope":"model.invoke"}""", true)]
    public void A_missing_model_invoke_scope_says_to_sign_in_again(string text, bool isError)
    {
        var a = HivemindClient.ReadModelInvocation(text, isError, "ark/nemotron-3-super");
        Assert.False(a.Ok);
        Assert.Equal(HivemindModelError.ScopeMissing, a.Kind);
        Assert.Contains("model.invoke", a.Error);
        Assert.Contains("sign in again", a.Error);
    }

    [Theory]
    [InlineData("Hosted inference allows 20 model calls per minute for this profile. Try again in 12 seconds.")]
    [InlineData("Nebius Token Factory is rate limiting hosted inference (HTTP 429). Try again shortly.")]
    public void Rate_limits_keep_the_servers_wait_and_say_rate_limiting(string text)
    {
        var a = HivemindClient.ReadModelInvocation(text, true, "ark/nemotron-3-super");
        Assert.Equal(HivemindModelError.RateLimited, a.Kind);
        Assert.StartsWith("Hivemind is rate limiting answers.", a.Error);
        Assert.Contains(text, a.Error);
    }

    [Theory]
    [InlineData("Hosted inference is not configured.")]
    [InlineData("""{"ok":false,"error":"Model \"x\" is not available through hosted inference."}""")]
    public void Other_failures_are_upstream_errors_with_the_cause(string text)
    {
        var a = HivemindClient.ReadModelInvocation(text, true, "x");
        Assert.Equal(HivemindModelError.Upstream, a.Kind);
        Assert.StartsWith("Hivemind could not get an answer:", a.Error);
        Assert.DoesNotContain("{", a.Error);
    }

    [Fact]
    public void Model_messages_keep_hiveminds_limits()
    {
        var many = new List<(string Role, string Content)> { ("system", "sys") };
        for (int i = 0; i < 20; i++) many.Add((i % 2 == 0 ? "user" : "assistant", $"m{i}"));
        many.Add(("user", new string('x', 9000)));
        many.Add(("tool", "   "));
        var sent = HivemindClient.ModelMessages(many);
        Assert.Equal(16, sent.Count);
        Assert.Equal("system", sent[0]["role"]);
        Assert.Equal(8000, sent[^1]["content"].Length);
        Assert.All(sent, m => Assert.Contains(m["role"], new[] { "system", "user", "assistant" }));
    }

    [Fact]
    public void Tool_envelopes_with_plain_text_errors_do_not_throw()
    {
        var env = HivemindClient.ReadEnvelope("MCP error -32603: boom", true, "model_invoke");
        Assert.False(env.Ok);
        Assert.Equal("MCP error -32603: boom", env.Error);
        var ok = HivemindClient.ReadEnvelope("""{"ok":true,"data":{"sessions":[]}}""", false, "list_sessions");
        Assert.True(ok.Ok);
        Assert.Equal(JsonValueKind.Object, ok.Data!.Value.ValueKind);
    }

    // ---- Web search on the Hivemind route ---------------------------------------------------------------------------

    [Theory]
    [InlineData("SEARCH: NVIDIA Nemotron 3 release date 2026", "NVIDIA Nemotron 3 release date 2026")]
    [InlineData("  **SEARCH:** \"Nebius Token Factory pricing\"", "Nebius Token Factory pricing")]
    [InlineData("search: weather in Austin today\nignored", "weather in Austin today")]
    public void A_search_line_is_read_as_a_query(string reply, string query) => Assert.Equal(query, WebSearch.SearchLine(reply));

    [Theory]
    [InlineData("Nemotron is a model family. SEARCH: not at the start")]
    [InlineData("I would search for it.")]
    public void An_answer_is_not_a_search_line(string reply) => Assert.Null(WebSearch.SearchLine(reply));

    [Fact]
    public void Search_results_from_tavily_or_hivemind_list_their_sources()
    {
        using var doc = JsonDocument.Parse("""
            {"ok":true,"data":{"results":[{"title":"A","url":"https://a.example/1","content":"alpha"},
             {"title":"B","link":"https://b.example/2","snippet":"beta"},{"title":"no url"}]}}
            """);
        var results = HivemindClient.ReadSearchResults(doc.RootElement);
        Assert.Equal(2, results.Count);
        var sources = new List<string>();
        string text = WebSearch.Format("q", results, sources);
        Assert.Contains("[1] A", text);
        Assert.Contains("beta", text);
        Assert.Equal(new[] { "https://a.example/1", "https://b.example/2" }, sources);
        Assert.EndsWith("- https://b.example/2", WebSearch.WithSources("Answer from https://a.example/1.", sources));
    }

    [Fact]
    public void Gala_handoff_needs_an_agent_capable_sign_in()
    {
        // Public Hivemind accounts get model.invoke, web.search and runtime.read: no Gala session for them.
        var account = new HashSet<string> { "model.invoke", "web.search", "runtime.read" };
        var owner = new HashSet<string> { "runtime.read", "runtime.execute", "model.invoke" };
        Assert.False(HandoffDestinations.OffersHivemindAgent(true, account));
        Assert.True(HandoffDestinations.OffersHivemindAgent(true, owner));
        Assert.False(HandoffDestinations.OffersHivemindAgent(false, owner));
    }
}
