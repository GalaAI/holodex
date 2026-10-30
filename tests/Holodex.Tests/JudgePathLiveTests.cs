using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using Omniwand;
using Xunit;
using Xunit.Abstractions;

namespace Holodex.Tests;

/// <summary>
/// The judge path end to end against a Hivemind DEV gateway on this PC. Holodex runs as a fresh install (no Nebius key,
/// no Ark: <see cref="ArkStationClient.SimulateFreshInstall"/>); a new account signs up and consents in headless
/// Chromium (live/judge-signup.mjs, so no person's browser is used); Holodex's own OAuth client signs in; and the real
/// HUD and drawer, built offscreen and never shown, ask through model_invoke. With HOLODEX_RENDER set, each state is
/// saved as a PNG there.
/// Runs only with HOLODEX_LIVE_HIVEMIND=1 and OMNIWAND_HIVEMIND_URL set to a loopback gateway (set in the environment
/// of the test run, before any Holodex type loads). Needs PLAYWRIGHT_CORE and HOLODEX_SIGNUP_INVITE. It signs out and
/// revokes after. The generated password is never printed. HOLODEX_SIGNUP_EMAIL and HOLODEX_SIGNUP_PASSWORD reuse an
/// account (signed up on first use); HOLODEX_SIGNED_OUT_ONLY=1 stops after the signed-out states.
/// </summary>
public class JudgePathLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void A_fresh_install_signs_in_with_hivemind_and_answers_with_nemotron()
    {
        if (Environment.GetEnvironmentVariable("HOLODEX_LIVE_HIVEMIND") != "1") return;
        Assert.True(HivemindClient.Endpoint.IsLoopback, $"refusing {HivemindClient.Endpoint}: the live proof runs only against a local dev gateway");
        RunOnUiThread(ProveAsync);
    }

    private async Task ProveAsync()
    {
        output.WriteLine($"endpoint {HivemindClient.Endpoint} credential target {HivemindClient.CredentialTarget}");
        PhosphorToast.Suppressed = true;
        PhosphorFonts.EnsureLoaded();
        string? renders = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (renders != null) Directory.CreateDirectory(renders);
        void Render(Form form, string name, Action? whileShown = null)
        {
            if (renders == null) return;
            // Shown far off-screen without activation (as the as-built render does), so nothing appears on the
            // desktop and no focus moves; panels that size to their visible rows (the drawer's profile) fit first.
            var home = form.Location;
            try
            {
                form.Location = new System.Drawing.Point(-32000, -32000);
                ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
                form.PerformLayout();
                whileShown?.Invoke();
                form.Refresh();
                string path = Path.Combine(renders, name);
                var size = SurfaceCapture.SaveForm(form, path);
                // DrawToBitmap paints sibling children in reverse z-order, so the drawer's floating profile panel came
                // out under the content it floats over. It is drawn on its own and laid over at its place.
                if (form.Controls.Find("Holodex.ProfilePanel", true).FirstOrDefault() is { Visible: true } panel)
                {
                    System.Drawing.Bitmap composed;
                    using (var file = System.Drawing.Image.FromFile(path)) composed = new System.Drawing.Bitmap(file);
                    using (var over = SurfaceCapture.Render(panel))
                    using (var g = System.Drawing.Graphics.FromImage(composed))
                        g.DrawImageUnscaled(over, panel.Left, panel.Top);
                    composed.Save(path, ImageFormat.Png);
                    composed.Dispose();
                }
                output.WriteLine($"render {name} {size.Width}x{size.Height}");
            }
            finally
            {
                ShowWindow(form.Handle, 0); // SW_HIDE
                form.Location = home;
            }
        }

        var ark = new ArkStationClient { SimulateFreshInstall = true };
        await ark.Hivemind.SignOutAsync(); // a clean start: no saved sign-in for this dev gateway
        Assert.False(ark.HasAnswerRoute);
        Assert.Equal(ArkStationClient.RouteNone, ark.NextTextRoute(ark.SelectedModel));

        var hud = new OmniwandHudForm(ark, _ => { }, _ => { }, _ => { });
        var drawer = new HolodexLiteDrawerForm(ark, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { });
        _ = hud.Handle;
        _ = drawer.Handle;
        try
        {
            // 1. First run: nothing answers yet, so the drawer opens on its profile with Sign in (as Program does).
            await drawer.RefreshConnectionHeaderAsync();
            Render(drawer, "judge-1-first-run-drawer.png", () => drawer.SetProfileOpen(true));
            drawer.SetProfileOpen(false);
            await hud.AskForCaptureAsync("What is NVIDIA Nemotron?");
            output.WriteLine($"signed-out HUD: {hud.DescribeLastCard()}");
            Assert.Contains("NOT CONNECTED", hud.DescribeLastCard());
            Render(hud, "judge-2-hud-signed-out.png");
            if (Environment.GetEnvironmentVariable("HOLODEX_SIGNED_OUT_ONLY") == "1") return;

            // 2. Sign up and consent headlessly; Holodex's own client finishes the sign-in.
            // A new account each run, unless HOLODEX_SIGNUP_EMAIL and HOLODEX_SIGNUP_PASSWORD name one made earlier
            // (the dev gateway limits sign-ups per address); then it signs in on /login instead.
            string? givenEmail = Environment.GetEnvironmentVariable("HOLODEX_SIGNUP_EMAIL");
            string? givenPassword = Environment.GetEnvironmentVariable("HOLODEX_SIGNUP_PASSWORD");
            bool existing = !string.IsNullOrEmpty(givenEmail) && !string.IsNullOrEmpty(givenPassword);
            string email = existing ? givenEmail! : $"holodex-judge-{DateTime.UtcNow:yyyyMMddHHmmss}@example.test";
            string password = existing ? givenPassword! : Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            var signup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            HivemindClient.OpenAuthorizationUrl = url => Task.Run(async () =>
            {
                var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "live", "judge-signup.mjs")));
                psi.ArgumentList.Add(url.ToString());
                psi.Environment["HOLODEX_SIGNUP_EMAIL"] = email;
                psi.Environment["HOLODEX_SIGNUP_PASSWORD"] = password;
                psi.Environment["HOLODEX_SIGNIN_EXISTING"] = existing ? "1" : "0";
                using var node = Process.Start(psi)!;
                string log = await node.StandardOutput.ReadToEndAsync() + await node.StandardError.ReadToEndAsync();
                await node.WaitForExitAsync();
                signup.TrySetResult($"exit {node.ExitCode}\n{log}");
            });
            bool signedIn = await ark.Hivemind.SignInAsync();
            output.WriteLine($"account {email}");
            output.WriteLine(await signup.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(signedIn, $"sign-in failed: {ark.Hivemind.LastError}");
            output.WriteLine($"granted {string.Join(" ", HivemindClient.GrantedScopes ?? new HashSet<string>())}");
            Assert.Equal(ArkStationClient.RouteHivemind, ark.NextTextRoute(ark.SelectedModel));

            // A narrowed sign-in (no runtime.execute) never asks agents; if a runtime.execute tool is called anyway it
            // fails plainly and the sign-in stays.
            if (!HivemindClient.Granted("runtime.execute"))
            {
                var ask = await ark.Hivemind.AskAgentAsync(null, "gala", "hello", Guid.NewGuid());
                output.WriteLine($"ask_agent without runtime.execute: {(ask == null ? "refused" : "ACCEPTED")}: {ark.Hivemind.LastError}");
                Assert.Null(ask);
                Assert.True(HivemindClient.HasSavedSignIn, "a refused scope must not end the sign-in");
            }
            output.WriteLine($"hivemind offers web_search {await ark.Hivemind.OffersToolAsync(WebSearch.ToolName)}; search provider {await WebSearch.ProviderAsync(ark.Hivemind)}");

            // 3. InvokeModelAsync itself, then the HUD's real ask path.
            var direct = await ark.Hivemind.InvokeModelAsync(new List<(string Role, string Content)>
            {
                ("system", "Answer in one sentence."),
                ("user", "What is NVIDIA Nemotron?")
            }, "ark/nemotron-3-super", 512);
            output.WriteLine($"InvokeModelAsync ok={direct.Ok} model={direct.Model} kind={direct.Kind} text={direct.Text}{direct.Error}");
            Assert.True(direct.Ok, direct.Error);

            await hud.AskForCaptureAsync("What is NVIDIA Nemotron, in one sentence?");
            string answered = hud.DescribeLastCard();
            output.WriteLine($"HUD: {answered}");
            Assert.Contains("VIA HIVEMIND", answered);
            Render(hud, "judge-3-hud-answer.png");

            // 4. A question that needs the web: the answer lists its sources when a provider can search.
            ark.SelectedModel = ArkStationClient.DefaultNemotronSuperModel;
            await hud.AskForCaptureAsync($"What is the newest NVIDIA Nemotron model as of {DateTime.Now:MMMM yyyy}? Name it and when it came out.");
            string searched = hud.DescribeLastCard();
            output.WriteLine($"HUD search: {searched}");
            Assert.Contains("VIA HIVEMIND", searched);
            Render(hud, "judge-4-hud-search.png");

            // 5. The drawer after sign-in: the profile says Nemotron answers through Hivemind, and the session answers.
            await drawer.RefreshConnectionHeaderAsync();
            Render(drawer, "judge-5-drawer-signed-in.png", () => drawer.SetProfileOpen(true));
            drawer.SetProfileOpen(false);
            drawer.SendSessionMessage("nemotron", "In one sentence, what can Holodex do?");
            string thread = "";
            for (int i = 0; i < 60 && !thread.Contains("VIA HIVEMIND") && !thread.Contains("NOT ANSWERED"); i++)
            {
                await Task.Delay(1000);
                thread = drawer.DescribeSessionThread(2);
            }
            output.WriteLine($"drawer session: {thread}");
            Assert.Contains("VIA HIVEMIND", thread);
            Render(drawer, "judge-6-drawer-session.png");
        }
        finally
        {
            HivemindClient.OpenAuthorizationUrl = null;
            await ark.Hivemind.SignOutAsync();
            output.WriteLine($"signed out; saved sign-in left {HivemindClient.HasSavedSignIn}");
            hud.Dispose();
            drawer.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Runs <paramref name="work"/> on an STA thread with a WinForms message loop, as the app's UI thread.</summary>
    private static void RunOnUiThread(Func<Task> work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var context = new ApplicationContext();
            var sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);
            sync.Post(async _ =>
            {
                try { await work(); }
                catch (Exception ex) { error = ex; }
                finally { context.ExitThread(); }
            }, null);
            Application.Run(context);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
