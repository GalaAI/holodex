using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// v0.4.2 item 6 (server side GalaAI/ark#1337): image questions through Hivemind, its web_search envelope, and the
/// receipt every hosted envelope carries. A Hivemind without image parts degrades to the old text-only message.
/// </summary>
public class HivemindVisionTests
{
    private static string Png(int w, int h, bool noise = false)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.DarkSlateGray);
        if (noise)
        {
            // Random pixels compress badly, like a busy screen capture.
            var rng = new Random(7);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    bmp.SetPixel(x, y, Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256)));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    [Fact]
    public void An_image_question_is_one_text_part_and_one_image_part_on_super_vl()
    {
        string image = Png(8, 8);
        var args = HivemindClient.VisionArguments("You are Holodex.", "What is this?", image, maxTokens: 300);
        string json = JsonSerializer.Serialize(args);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("ark/nemotron-3.5-super-vl", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("maxTokens").GetInt32() >= 1024);
        var messages = root.GetProperty("messages");
        Assert.InRange(messages.GetArrayLength(), 1, 16);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind);
        var parts = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.InRange(parts.GetArrayLength(), 1, 8);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("What is this?", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal(image, parts[1].GetProperty("image_url").GetProperty("url").GetString());
        // At most one image in the whole call.
        Assert.Single(messages.EnumerateArray().Where(m => m.GetProperty("content").ValueKind == JsonValueKind.Array)
            .SelectMany(m => m.GetProperty("content").EnumerateArray()), p => p.GetProperty("type").GetString() == "image_url");
    }

    [Fact]
    public void A_small_png_goes_as_is_and_a_large_capture_is_reencoded_to_fit()
    {
        string small = Png(16, 16);
        Assert.Equal(small, HivemindClient.FitImage(small));

        // A capture over the limit (here a 20 KB limit stands in for 4 MB) comes back as a JPEG that fits.
        string big = Png(400, 300, noise: true);
        Assert.True(Convert.FromBase64String(big["data:image/png;base64,".Length..]).Length > 20_000);
        string? fitted = HivemindClient.FitImage(big, maxBytes: 20_000);
        Assert.NotNull(fitted);
        Assert.StartsWith("data:image/jpeg;base64,", fitted);
        Assert.True(Convert.FromBase64String(fitted!["data:image/jpeg;base64,".Length..]).Length <= 20_000);

        // Hivemind takes png or jpeg only: a jpg label is renamed, anything else is re-encoded.
        Assert.StartsWith("data:image/jpeg;base64,", HivemindClient.FitImage(small.Replace("image/png", "image/jpg"))!);
        Assert.Null(HivemindClient.FitImage("not a data url"));
        Assert.Null(HivemindClient.FitImage("data:image/png;base64,@@@"));
    }

    [Theory]
    // What an MCP server answers when model_invoke's content is still string-only (production before #1337).
    [InlineData("MCP error -32602: Input validation error: Invalid arguments for tool model_invoke: [{\"code\":\"invalid_type\",\"expected\":\"string\",\"received\":\"array\",\"path\":[\"messages\",1,\"content\"],\"message\":\"Expected string, received array\"}]")]
    [InlineData("Input validation error: Invalid arguments for tool model_invoke")]
    public void A_schema_refusal_of_the_image_becomes_the_old_nebius_key_message(string serverText)
    {
        var seen = HivemindClient.ReadVisionInvocation(serverText, isError: true);
        Assert.False(seen.Ok);
        Assert.Equal(HivemindModelError.ImagesNotSupported, seen.Kind);

        var answer = ArkStationClient.VisionAnswerFrom(seen);
        Assert.False(answer.Ok);
        Assert.Equal(ArkStationClient.ImageNeedsKeyMessage, answer.Output);
        Assert.Equal(ArkStationClient.RouteNone, answer.Route);
    }

    [Fact]
    public void Other_vision_failures_keep_their_own_words()
    {
        var limited = HivemindClient.ReadVisionInvocation("""{"ok":false,"message":"Hosted answers allow 20 per minute for this account."}""", isError: false);
        Assert.Equal(HivemindModelError.RateLimited, limited.Kind);
        Assert.NotEqual(ArkStationClient.ImageNeedsKeyMessage, ArkStationClient.VisionAnswerFrom(limited).Output);
    }

    [Fact]
    public void A_vision_answer_names_super_vl_and_carries_its_receipt()
    {
        const string body = """
            {"ok":true,"receiptId":"9f8e7d6c-1111-2222-3333-444455556666","data":{"id":"c1","model":"nvidia/Nemotron-3.5-Super-VL",
             "choices":[{"index":0,"message":{"role":"assistant","content":"A dark grey square."},"finish_reason":"stop"}]}}
            """;
        var answer = ArkStationClient.VisionAnswerFrom(HivemindClient.ReadVisionInvocation(body, isError: false));
        Assert.True(answer.Ok);
        Assert.Equal("A dark grey square.", answer.Output);
        Assert.Equal(ArkStationClient.RouteHivemind, answer.Route);
        Assert.Equal("RECEIPT 9f8e7d6c", answer.ReceiptTag);
    }

    [Fact]
    public void Hosted_web_search_results_are_read_from_data_results()
    {
        const string body = """
            {"ok":true,"receiptId":"aa11bb22-0000-0000-0000-000000000000","data":{"query":"nemotron","answer":"NVIDIA open models.",
             "results":[{"title":"NVIDIA Nemotron","url":"https://developer.nvidia.com/nemotron","content":"Open models.","score":0.91,"publishedDate":"2026-10-01"},
                        {"title":"Token Factory","url":"https://tokenfactory.nebius.com","content":"Hosted inference.","score":0.8,"publishedDate":null}],
             "responseTime":0.42,"provider":"tavily"}}
            """;
        var env = HivemindClient.ReadEnvelope(body, false, "web_search");
        Assert.True(env.Ok);
        var results = HivemindClient.ReadSearchResults(env.Data!.Value);
        Assert.Equal(2, results.Count);
        Assert.Equal("https://developer.nvidia.com/nemotron", results[0].Url);
        Assert.Equal("Open models.", results[0].Content);
    }

    [Fact]
    public void The_search_minute_limit_is_recognised_for_a_plain_message()
    {
        Assert.True(HivemindClient.IsSearchRateLimit("Web search allows 10 searches per minute for this account. Try again in 12 seconds."));
        Assert.True(HivemindClient.IsSearchRateLimit("Tavily is rate limiting search (HTTP 429). Try again shortly."));
        Assert.False(HivemindClient.IsSearchRateLimit("Web search is not configured."));
    }

    [Fact]
    public void Receipts_show_for_the_model_call_and_the_search()
    {
        var both = new ArkStationClient.ModelAnswer(true, "x", "m", ArkStationClient.RouteHivemind, "12345678-aaaa") { SearchReceiptId = "abcdef01-bbbb" };
        Assert.Equal("RECEIPT 12345678 // SEARCH abcdef01", both.ReceiptTag);
        Assert.Equal("", new ArkStationClient.ModelAnswer(true, "x", "m", ArkStationClient.RouteNebiusDirect).ReceiptTag);
    }
}
