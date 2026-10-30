using System.Drawing.Imaging;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Omniwand;

/// <summary>
/// A Holodex window that hosts one page an Ark service serves, in WebView2 inside the Phosphor chrome. Holodex keeps
/// no copy of the page. When the page cannot be reached the window says which URL failed, with Copy and Try again,
/// and shows nothing in its place. The Augment Inventory and Input Studio are both this window.
/// </summary>
internal abstract class ArkPageForm : Form
{
    protected static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    protected readonly WebView2 Web;
    private readonly ThreadView _fail;
    private readonly Panel _host;
    private readonly string _failEyebrow;
    private bool _initialized;

    protected ArkPageForm(string name, string title, string eyebrow, Color accent, Size size)
    {
        PhosphorFonts.EnsureLoaded();
        _failEyebrow = title.ToUpperInvariant();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = name;
        Text = title;
        Size = size;
        Padding = WidgetChrome.FramePadding();

        var host = _host = new Panel { Dock = DockStyle.Fill, BackColor = PhosphorTheme.Ground, Name = name + ".Host" };
        Web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = PhosphorTheme.Ground, Name = name + ".Page" };
        _fail = new ThreadView(name + ".Error") { Dock = DockStyle.Fill, FollowNewest = false, Visible = false };
        host.Controls.Add(Web);
        host.Controls.Add(_fail);

        Controls.Add(host);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, eyebrow, title, accent, null);
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

    /// <summary>Docks native controls down the left of the page (a window's own settings beside the page).</summary>
    protected void AddRail(Control rail)
    {
        rail.Dock = DockStyle.Left;
        _host.Controls.Add(rail);
    }

    /// <summary>The page this window shows.</summary>
    protected abstract string PageUrl { get; }

    /// <summary>Which service failed and where the page lives, for the failure card ("ARK API", "INPUT STUDIO").</summary>
    protected abstract (string Service, string Explanation) FailureText { get; }

    /// <summary>Wires page messages once the browser exists.</summary>
    protected virtual void OnCoreReady(CoreWebView2 core) { }

    /// <summary>Reads anything the page needs before it loads; runs after the page answered.</summary>
    protected virtual Task BeforeNavigateAsync() => Task.CompletedTask;

    protected bool Initialized => _initialized;

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

    /// <summary>Opens the window centred on the screen under the cursor and loads the page.</summary>
    protected async Task PresentAsync(Point cursorPos)
    {
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Size = new Size(Math.Min(Width, screen.Width - 40), Math.Min(Height, screen.Height - 40));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        if (!Visible) SurfaceMotion.Present(this);
        Activate();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!_initialized)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "webview2");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await Web.EnsureCoreWebView2Async(env);
                var core = Web.CoreWebView2!;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                OnCoreReady(core);
                _initialized = true;
            }

            // The page first: if the route is missing or the service is down, say which URL failed instead of
            // showing an empty frame.
            string url = PageUrl;
            string? pageError = await ProbeAsync(url);
            if (pageError != null) { ShowFailure(url, pageError); return; }
            await BeforeNavigateAsync();
            _fail.Visible = false;
            Web.Visible = true;
            Web.CoreWebView2!.Navigate(url);
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

    protected static async Task<string?> ProbeAsync(string url)
    {
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Whether a URL answers 2xx within 1.5 s; for deciding at startup whether to offer a window at all.</summary>
    protected static bool Answers(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var res = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void ShowFailure(string url, string cause)
    {
        var (service, explanation) = FailureText;
        Web.Visible = false;
        _fail.Clear();
        _fail.AddSection($"{_failEyebrow} // NOT LOADED");
        var card = _fail.Add(ThreadTone.Fail, $"NOT LOADED // {service}", $"{url} did not answer: {cause}. {explanation}");
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
        if (Web.Visible && Web.CoreWebView2 != null)
        {
            using var ms = new MemoryStream();
            await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            using var page = Image.FromStream(ms);
            var at = PointToClient(Web.PointToScreen(Point.Empty));
            using var g = Graphics.FromImage(frame);
            g.DrawImage(page, new Rectangle(at, Web.Size));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        frame.Save(path, ImageFormat.Png);
        return path;
    }
}
