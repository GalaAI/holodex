using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Omniwand;

/// <summary>
/// 4GE in Holodex: the studio (forge/apps/studio, served by the 4GE server on 127.0.0.1:17400) full screen in WebView2,
/// in the Phosphor chrome. Opening it starts the server first (<see cref="ForgeHost"/>); when the server cannot run,
/// the window says why in plain words, with Copy and Try again, and shows nothing in its place.
/// </summary>
internal sealed class ForgeStudioForm : ArkPageForm
{
    private readonly ForgeHost _host;
    private string _path = "/";

    public ForgeStudioForm(ForgeHost host)
        : base("ForgeStudio", "4GE", "HOLODEX // 4GE", PhosphorTheme.Coral, new Size(1600, 960))
    {
        _host = host;
    }

    protected override bool FullScreen => true;

    /// <summary>The studio page now shown, such as /imagistation or /mods/mod%3Adev?session=ses_x.</summary>
    internal string StudioPath => _path;

    protected override string PageUrl => _host.BaseUrl + _path;

    protected override (string Service, string Explanation) FailureText =>
        ("4GE SERVER", _host.LastError ?? "Holodex starts the 4GE server on this PC when you open 4GE, and it did not answer. Try again.");

    /// <summary>
    /// Starts the 4GE server if it is not running, then opens the studio full screen on the screen under the cursor at
    /// <paramref name="path"/> (the studio's home when null).
    /// </summary>
    public async Task SummonAsync(Point cursorPos, string? path = null)
    {
        _path = NormalizePath(path);
        await _host.EnsureRunningAsync();
        await PresentAsync(cursorPos);
    }

    /// <summary>"imagistation" and "/imagistation" both open /imagistation; anything that is not a path opens the home.</summary>
    internal static string NormalizePath(string? path)
    {
        string p = (path ?? "").Trim();
        if (p.Length == 0 || p.Contains("://", StringComparison.Ordinal) || p.Contains("..", StringComparison.Ordinal)) return "/";
        return p.StartsWith('/') ? p : "/" + p;
    }

    // Esc belongs to the studio (it closes its inspector and threads); the window closes from its own close button.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        keyData != Keys.Escape && base.ProcessCmdKey(ref msg, keyData);

    protected override void OnCoreReady(CoreWebView2 core)
    {
        // Repo shortcuts and sign-up links open in the user's own browser, not inside the studio.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { PhosphorToast.Show("Link did not open", ex.Message, "HOLODEX // 4GE", 3600, ToastTone.Warning); }
            }
        };
    }
}
