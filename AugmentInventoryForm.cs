using System.Drawing.Imaging;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Omniwand;

/// <summary>
/// The Augment Inventory in Holodex: the one inventory page (packages/ui in Ark, served by the Ark API at
/// /api/omnitek/augment-inventory) hosted in WebView2 and fed the live Shell registry from /api/omnitek/shells.
/// Holodex keeps no copy of the page. When the page or the API cannot be reached the window says which URL
/// failed, with Copy and Try again, and shows nothing in its place.
/// </summary>
internal sealed class AugmentInventoryForm : Form
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly WebView2 _web;
    private readonly ThreadView _fail;
    private string _base = ArkStationClient.ArkApiBaseUrl;
    private string _shell = "phosphor";
    private string? _feedJson;
    private bool _initialized;

    public AugmentInventoryForm()
    {
        PhosphorFonts.EnsureLoaded();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "AugmentInventory";
        Text = "Augment Inventory";
        Size = new Size(1280, 820);
        Padding = WidgetChrome.FramePadding();

        var host = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Ground, Name = "AugmentInventory.Host" };
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = PhosphorTheme.Ground, Name = "AugmentInventory.Page" };
        _fail = new ThreadView("AugmentInventory.Error") { Dock = DockStyle.Fill, ReserveScrollbar = false, FollowNewest = false, Visible = false };
        host.Controls.Add(_web);
        host.Controls.Add(_fail);

        Controls.Add(host);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "OMNITEK // AUGMENT INVENTORY // BY SHELL", "Augment Inventory", PhosphorTheme.Coral, null);
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

    /// <summary>The page's own URL for the Shell shown, on the Ark API this window reads.</summary>
    private string PageUrl => $"{_base}/api/omnitek/augment-inventory?shell={Uri.EscapeDataString(_shell)}";
    private string FeedUrl => $"{_base}/api/omnitek/shells";

    /// <summary>
    /// Whether the Ark API serves the Shell registry this window shows (GET /api/omnitek/shells answers 2xx), checked
    /// once at startup on the owner's Ark. The tray and drawer offer the Inventory only then: no window that can
    /// only say the feed is missing.
    /// </summary>
    public static bool FeedAvailable(ArkStationClient ark) => ark.ArkInstalled && FeedAvailableLazy.Value;

    private static readonly Lazy<bool> FeedAvailableLazy = new(() =>
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var res = Http.GetAsync($"{ArkStationClient.ArkApiBaseUrl}/api/omnitek/shells", HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .GetAwaiter().GetResult();
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    });

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { SurfaceMotion.Dismiss(this); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }

    /// <summary>Opens the inventory centred on the screen under the cursor. A base URL points it at another Ark API.</summary>
    public async void Summon(Point cursorPos, string? baseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)) _base = baseUrl.Trim().TrimEnd('/');
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Size = new Size(Math.Min(Width, screen.Width - 40), Math.Min(Height, screen.Height - 40));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        if (!Visible) SurfaceMotion.Present(this);
        Activate();
        await LoadAsync();
    }

    /// <summary>Shows one Shell (phosphor, bandit): the page swaps in place, the next load opens on it.</summary>
    public void ShowShell(string id)
    {
        _shell = id.Trim().ToLowerInvariant();
        if (_initialized && _web.CoreWebView2 != null)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "ark-inventory-shell", id = _shell }));
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!_initialized)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "webview2");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2!;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
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
                _initialized = true;
            }

            // The page first: if the route is missing (an Ark API older than the inventory) or the API is down, say
            // which URL failed instead of showing an empty frame.
            string? pageError = await ProbeAsync(PageUrl);
            if (pageError != null) { ShowFailure(PageUrl, pageError); return; }
            _feedJson = await ReadFeedAsync();
            _fail.Visible = false;
            _web.Visible = true;
            _web.CoreWebView2!.Navigate(PageUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFailure(PageUrl, "The Microsoft Edge WebView2 Runtime is not installed on this PC.");
        }
        catch (Exception ex)
        {
            ShowFailure(PageUrl, ex.Message);
        }
    }

    private static async Task<string?> ProbeAsync(string url)
    {
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex) { return ex.Message; }
    }

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
        if (_feedJson == null || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson("{\"t\":\"ark-inventory-feed\",\"feed\":" + _feedJson + "}");
    }

    private void ShowFailure(string url, string cause)
    {
        _web.Visible = false;
        _fail.Clear();
        _fail.AddSection("AUGMENT INVENTORY // NOT LOADED");
        var card = _fail.Add(ThreadTone.Fail, "NOT LOADED // ARK API",
            $"{url} did not answer: {cause}. The inventory page lives in Ark (packages/ui) and Holodex opens it from the Ark API; nothing is shown in its place.");
        card.SetFooter("",
            new CardAction("Thread.Copy", PhosphorGlyph.Copy, "Copy", (c, _) => { try { Clipboard.SetText(c.Body); } catch { } }),
            new CardAction("Thread.Retry", PhosphorGlyph.Retry, "Try again", (_, _) => _ = LoadAsync()));
        _fail.Visible = true;
        _fail.Relayout();
    }

    /// <summary>
    /// Writes the window as the operator sees it: the Phosphor chrome drawn by WinForms with the page's own
    /// WebView2 capture laid into its bounds (DrawToBitmap cannot see the browser surface).
    /// </summary>
    public async Task<string> CaptureAsync(string path)
    {
        using var frame = new Bitmap(Width, Height);
        DrawToBitmap(frame, new Rectangle(0, 0, Width, Height));
        if (_web.Visible && _web.CoreWebView2 != null)
        {
            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            using var page = Image.FromStream(ms);
            var at = PointToClient(_web.PointToScreen(Point.Empty));
            using var g = Graphics.FromImage(frame);
            g.DrawImage(page, new Rectangle(at, _web.Size));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        frame.Save(path, ImageFormat.Png);
        return path;
    }
}
