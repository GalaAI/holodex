using System.Drawing;
using System.Windows.Forms;
using Omniwand;
using Xunit;
using Xunit.Abstractions;

namespace Holodex.Tests;

/// <summary>
/// Draws the real drawer's Spells tab offscreen (HOLODEX_RENDER=folder): the orbs at rest and hovered, an orb open, a
/// Spell's card, and the composer with a goal typed. It reads this PC's Input Spine and spellbook, as the app does.
/// The drawer is shown far off-screen without activation and hidden again; nothing is crafted or cast.
/// </summary>
public class SpellOrbsRenderTests(ITestOutputHelper output)
{
    [Fact]
    public void Spells_tab_renders()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        SpellOrbsTests.RunOnUi(async () =>
        {
            PhosphorToast.Suppressed = true;
            PhosphorFonts.EnsureLoaded();
            var ark = new ArkStationClient();
            // Not disposed (see RunOnUi): it is hidden, and its windows go with the test host.
            var drawer = new HolodexLiteDrawerForm(ark, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { });
            var home = drawer.Location;
            drawer.Location = new Point(-32000, -32000);
            ShowWindow(drawer.Handle, 4); // SW_SHOWNOACTIVATE
            try
            {
                drawer.SwitchTab("grimx");
                await drawer.Spells.RefreshAsync();
                var orbs = drawer.Spells.Orbs;
                drawer.PerformLayout();
                void Save(string name)
                {
                    drawer.Refresh();
                    var size = SurfaceCapture.SaveForm(drawer, Path.Combine(folder, name));
                    output.WriteLine($"{name} {size.Width}x{size.Height}");
                }

                Save("spells-orbs.png");
                orbs.PreviewHover(0);
                Save("spells-hover.png");
                orbs.PreviewHover(-1);

                string kind = orbs.Kinds.First(k => orbs.SpellsOf(k.Id).Count > 0).Id;
                Assert.True(orbs.Open(kind));
                orbs.Settle();
                Save("spells-open.png");
                // The Spell that shares its route with another, so the related pulse shows; else the first.
                var spells = orbs.SpellsOf(kind);
                int pick = Enumerable.Range(0, spells.Count).FirstOrDefault(i => SpellOrbit.Related(spells, i).Count > 0);
                orbs.Select(pick);
                orbs.Settle();
                Save("spells-selected.png");
                orbs.Close();
                orbs.Settle();

                drawer.Spells.Prompt.Text = "Schedule a nightly cleanup of my Downloads folder";
                Save("spells-composer.png");
                drawer.Spells.Prompt.Text = "";
            }
            finally
            {
                ShowWindow(drawer.Handle, 0); // SW_HIDE
                drawer.Location = home;
            }
        }, timeoutMs: 90_000, leaveLoop: true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
