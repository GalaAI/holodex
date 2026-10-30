using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Omniwand;

/// <summary>
/// The Augment Inventory in Holodex: the one inventory page (packages/ui in Ark, served by the Ark API at
/// /api/omnitek/augment-inventory) hosted in WebView2 and fed the live Shell registry from /api/omnitek/shells.
/// </summary>
internal sealed class AugmentInventoryForm : ArkPageForm
{
    private string _base = ArkStationClient.ArkApiBaseUrl;
    private string _shell = "phosphor";
    private string? _feedJson;

    public AugmentInventoryForm()
        : base("AugmentInventory", "Augment Inventory", "OMNITEK // AUGMENT INVENTORY // BY SHELL", PhosphorTheme.Coral, new Size(1280, 820)) { }

    /// <summary>The page's own URL for the Shell shown, on the Ark API this window reads.</summary>
    protected override string PageUrl => $"{_base}/api/omnitek/augment-inventory?shell={Uri.EscapeDataString(_shell)}";
    private string FeedUrl => $"{_base}/api/omnitek/shells";

    protected override (string Service, string Explanation) FailureText =>
        ("ARK API", "The inventory page lives in Ark (packages/ui) and Holodex opens it from the Ark API; nothing is shown in its place.");

    /// <summary>
    /// Whether the Ark API serves the Shell registry this window shows (GET /api/omnitek/shells answers 2xx), checked
    /// once at startup on the owner's Ark. The tray and drawer offer the Inventory only then: no window that can
    /// only say the feed is missing.
    /// </summary>
    public static bool FeedAvailable(ArkStationClient ark) => ark.ArkInstalled && FeedAvailableLazy.Value;

    private static readonly Lazy<bool> FeedAvailableLazy = new(() => Answers($"{ArkStationClient.ArkApiBaseUrl}/api/omnitek/shells"));

    /// <summary>Opens the inventory centred on the screen under the cursor. A base URL points it at another Ark API.</summary>
    public async void Summon(Point cursorPos, string? baseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)) _base = baseUrl.Trim().TrimEnd('/');
        await PresentAsync(cursorPos);
    }

    /// <summary>Shows one Shell (phosphor, bandit): the page swaps in place, the next load opens on it.</summary>
    public void ShowShell(string id)
    {
        _shell = id.Trim().ToLowerInvariant();
        if (Initialized && Web.CoreWebView2 != null)
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "ark-inventory-shell", id = _shell }));
    }

    protected override void OnCoreReady(CoreWebView2 core)
    {
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "ark-inventory-ready") PostFeed();
            }
            catch (JsonException) { }
        };
        core.NavigationCompleted += (_, e) => { if (e.IsSuccess) PostFeed(); };
    }

    protected override async Task BeforeNavigateAsync() => _feedJson = await ReadFeedAsync();

    /// <summary>The registry's data object, or null when the feed cannot be read (the page then shows its dated copy).</summary>
    private async Task<string?> ReadFeedAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(FeedUrl));
            return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("brands", out _) ? data.GetRawText() : null;
        }
        catch { return null; }
    }

    private void PostFeed()
    {
        if (_feedJson == null || Web.CoreWebView2 == null) return;
        Web.CoreWebView2.PostWebMessageAsJson("{\"t\":\"ark-inventory-feed\",\"feed\":" + _feedJson + "}");
    }
}
