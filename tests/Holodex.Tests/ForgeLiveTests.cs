using System.Runtime.InteropServices;
using System.Windows.Forms;
using Omniwand;
using Xunit;
using Xunit.Abstractions;

namespace Holodex.Tests;

/// <summary>
/// 4GE against the real server on this PC: Holodex's <see cref="ForgeHost"/> starts forge/apps/server (built) with the
/// user's own Nebius key from the vault (never printed), reads GET /api/library, opens an item's Smith session, and
/// stops the server again. It runs a second instance on port 17402 (FORGE_PORT) with its data and AInbox queue in a
/// temp folder, so it never touches the user's 4GE data or a server on 17400. With HOLODEX_RENDER set, the drawer's
/// Library tab is rendered offscreen (never shown on the desktop) as forge-library.png. Runs only with HOLODEX_LIVE=1.
/// </summary>
public class ForgeLiveTests(ITestOutputHelper output)
{
    private const int TestPort = 17402;

    [Fact]
    public void Holodex_starts_4ge_reads_the_library_and_stops_it()
    {
        if (Environment.GetEnvironmentVariable("HOLODEX_LIVE") != "1") return;
        RunOnUiThread(ProveAsync);
    }

    // ITestOutputHelper shows nothing until the test ends; HOLODEX_TRACE names a file that gets each step as it runs.
    private void Step(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("HOLODEX_TRACE") is { Length: > 0 } trace) File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
    }

    private async Task ProveAsync()
    {
        PhosphorToast.Suppressed = true;
        string temp = Path.Combine(Path.GetTempPath(), $"holodex-4ge-live-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string>
        {
            ["FORGE_DATA_ROOT"] = Path.Combine(temp, "data"),
            ["FORGE_AINBOX_QUEUE"] = Path.Combine(temp, "ainbox-queue")
        };
        var ark = new ArkStationClient();
        Step($"nebius key present: {ark.HasNebiusKey}; hivemind sign-in saved: {HivemindClient.HasSavedSignIn}");
        using var host = new ForgeHost(ark, TestPort, env);
        try
        {
            Step("starting");
            bool ok = await host.EnsureRunningAsync();
            Step($"state {host.State} started-here {host.StartedHere} error {host.LastError}");
            Assert.True(ok, host.LastError);
            Assert.True(host.StartedHere, "port 17402 already had a 4GE server; the proof needs its own");

            using (var http = new HttpClient())
            {
                string health = await http.GetStringAsync($"{host.BaseUrl}/api/health");
                Step($"health {health}");
                string models = await (await http.GetAsync($"{host.BaseUrl}/api/models")).Content.ReadAsStringAsync();
                Step($"models {models[..Math.Min(400, models.Length)]}");
                Assert.DoesNotContain(ark.ReadNebiusApiKey() is { Length: > 8 } k ? k : "\u0000never", models);
            }

            Step("reading the library");
            var (groups, error) = await host.ReadLibraryAsync();
            Step(groups == null ? $"library error: {error}" :
                string.Join("\n", groups.Select(g => $"{g.Label} ({g.Items.Count}){(g.CanCreate ? " +" : "")}: {string.Join(", ", g.Items.Select(i => $"{i.Title} [{i.Status}]"))}")));
            if (groups != null)
            {
                Assert.Equal(ForgeHost.LibraryOrigins, groups.Select(g => g.Origin).ToArray());
                var first = groups.SelectMany(g => g.Items).FirstOrDefault();
                if (first != null)
                {
                    var (path, openError) = await host.OpenSessionAsync(first);
                    Step($"open {first.Id} -> {path ?? openError}");
                    Assert.True(path != null, openError);
                    // The page the studio window loads for that session: the built studio, by its single-page fallback.
                    using var http = new HttpClient();
                    var page = await http.GetAsync(host.BaseUrl + path);
                    string html = await page.Content.ReadAsStringAsync();
                    Step($"studio {path} -> HTTP {(int)page.StatusCode}, {html.Length} bytes, root div {html.Contains("id=\"root\"")}");
                    Assert.True(page.IsSuccessStatusCode);
                    Assert.Contains("id=\"root\"", html);
                }
            }

            string? renders = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
            // The drawer is rendered on its own background UI thread: a drawer built in a test never tears down
            // cleanly (its Dispose waits on the app's loop), so it must not hold up this thread or the test host.
            if (renders != null)
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var t = new Thread(() =>
                {
                    var sync = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(sync);
                    sync.Post(async _ =>
                    {
                        try { await RenderLibraryAsync(ark, host, Path.Combine(renders, "forge-library.png")); rendered.TrySetResult(); }
                        catch (Exception ex) { rendered.TrySetException(ex); }
                    }, null);
                    Application.Run();
                }) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }

            Step("checking the log");
            string log = Path.Combine(env["FORGE_DATA_ROOT"], "logs", "holodex-server.log");
            Assert.True(File.Exists(log));
            // Holodex keeps the log open while the server runs (FileShare.ReadWrite), so read it the same way.
            using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string logText = reader.ReadToEnd();
            Step($"log lines {logText.Split(Environment.NewLine).Length}");
            if (ark.ReadNebiusApiKey() is { Length: > 8 } key) Assert.DoesNotContain(key, logText);
        }
        finally
        {
            host.Dispose();
            await Task.Delay(800);
            var (stillUp, _) = await new ForgeHost(ark, TestPort).ProbeAsync();
            Step($"after dispose the server answers: {stillUp}");
            Assert.False(stillUp);
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    private async Task RenderLibraryAsync(ArkStationClient ark, ForgeHost host, string path)
    {
        Step("render: building the drawer");
        PhosphorFonts.EnsureLoaded();
        var drawer = new HolodexLiteDrawerForm(ark, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }) { Forge = host };
        _ = drawer.Handle;
        var home = drawer.Location;
        try
        {
            drawer.Location = new Point(-32000, -32000);
            ShowWindow(drawer.Handle, 4); // SW_SHOWNOACTIVATE, off-screen
            Step("render: drawer shown");
            drawer.SwitchTab("codex");
            Step("render: library tab");
            await drawer.RefreshForgeLibraryAsync();
            Step($"drawer library: {drawer.DescribeForgeLibrary()}");
            drawer.PerformLayout();
            drawer.Refresh();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var size = SurfaceCapture.SaveForm(drawer, path);
            Step($"render {path} {size.Width}x{size.Height}");
        }
        finally
        {
            Step("render: hiding");
            ShowWindow(drawer.Handle, 0);
            drawer.Location = home;
            // Not disposed here: the drawer's Dispose waits on the app's own message loop (as JudgePathLiveTests
            // leaves its drawer), and the test's UI thread ends right after.
            Step("render: drawer hidden");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
