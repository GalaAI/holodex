using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The Spells tab: orbit geometry, the orb's open and close (modal) state, and a Spell crafted from the composer
/// landing in its orb and the spellbook. The Spine lists here mirror what Input Spine serves for the Quick Cast and
/// Summon slots; the crafter stands in for Wiz's model call, and the spellbook is a temp file.
/// </summary>
public class SpellOrbsTests
{
    private static Dictionary<string, List<SpellChoice>> SpineLists() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["spell"] = new()
        {
            new("holodex-search", "Holodex search", "Casts the Holodex search Spell (command-definition:holodex:search) through DLC, .bamf, Aegis and Nexus.", true),
            new("release-observation", "Release observation", "Casts navigate.release.observe through the API bench run (ark craft cast).", true),
            new("build-diagnosis", "Build failure diagnosis", "Casts navigate.release.diagnose-build through the API bench run (ark craft cast).", true),
        },
        ["summon"] = new()
        {
            new("sentinel", "Sentinel health sweep", "Summons sentinel through Agent Dispatch (ark agent mission) in discussion mode.", true),
            new("doc", "Doc service check", "Summons doc through Agent Dispatch (ark agent mission) in discussion mode.", true),
        },
        ["not-a-kind"] = new() { new("x", "Ignored", null, true) },
    };

    // ---- Geometry ----

    [Fact]
    public void Nodes_sit_at_index_over_total_of_the_turn_plus_the_rotation()
    {
        Assert.Equal(90f, SpellOrbit.AngleDeg(0, 3, SpellOrbit.RestRotation), 3);
        Assert.Equal(210f, SpellOrbit.AngleDeg(1, 3, SpellOrbit.RestRotation), 3);
        Assert.Equal(330f, SpellOrbit.AngleDeg(2, 3, SpellOrbit.RestRotation), 3);
        Assert.Equal(0f, SpellOrbit.AngleDeg(4, 4, 0f), 3);

        var c = new PointF(100, 100);
        var bottom = SpellOrbit.Position(c, 50, 90);
        Assert.Equal(100f, bottom.X, 2);
        Assert.Equal(150f, bottom.Y, 2);
        var right = SpellOrbit.Position(c, 50, 0);
        Assert.Equal(150f, right.X, 2);
        Assert.Equal(100f, right.Y, 2);
    }

    [Fact]
    public void Depth_fades_the_far_side_to_four_tenths()
    {
        Assert.Equal(0.4f, SpellOrbit.DepthOpacity(270), 3);
        Assert.Equal(1f, SpellOrbit.DepthOpacity(90), 3);
        Assert.Equal(0.7f, SpellOrbit.DepthOpacity(0), 3);
        for (int a = 0; a < 360; a += 7)
        {
            float o = SpellOrbit.DepthOpacity(a);
            Assert.InRange(o, 0.4f, 1f);
        }
    }

    [Fact]
    public void Selecting_a_node_turns_it_to_the_top_by_the_short_way()
    {
        for (int total = 1; total <= 7; total++)
            for (int i = 0; i < total; i++)
                Assert.Equal(SpellOrbit.SelectedAngle, SpellOrbit.AngleDeg(i, total, SpellOrbit.CenterOn(i, total)), 2);
        Assert.Equal(-20f, SpellOrbit.ShortestTurn(10, 350), 3);
        Assert.Equal(20f, SpellOrbit.ShortestTurn(350, 10), 3);
        Assert.Equal(180f, Math.Abs(SpellOrbit.ShortestTurn(0, 180)), 3);
        Assert.Equal(1.5f, SpellOrbit.ExpandedScale);
    }

    [Fact]
    public void Orbit_radius_is_200_on_a_desktop_and_shrinks_to_fit_the_drawer()
    {
        Assert.Equal(200f, SpellOrbit.Radius(1200, 900, 1f), 3);
        float drawer = SpellOrbit.Radius(466, 640, 1f);
        Assert.True(drawer < 200f && drawer > 120f, $"drawer radius {drawer}");
        Assert.True(drawer + SpellOrbit.LabelRoomPx <= 466 / 2f + 0.01f);
        Assert.Equal(400f, SpellOrbit.Radius(2400, 1800, 2f), 3);
        Assert.True(SpellOrbit.Radius(100, 100, 1f) >= 56f);
    }

    // ---- Data ----

    [Fact]
    public void Seven_kinds_in_the_GrimX_order_with_honest_empty_text()
    {
        Assert.Equal(new[] { "Quick Casts", "Combos", "Rituals", "Symphonies", "Ultimas", "Charms", "Summons" }, SpellKindInfo.All.Select(k => k.Plural));
        Assert.Equal(new[] { "spell", "combo", "ritual", "symphony", "ultima", "charm", "summon" }, SpellKindInfo.All.Select(k => k.Id));
        Assert.Equal("No Rituals yet", SpellKindInfo.Find("ritual")!.EmptyText);
        Assert.Equal(PhosphorTheme.Seal, SpellKindInfo.Find("ultima")!.Accent);
    }

    [Fact]
    public void Spine_spells_land_in_their_kind_and_share_routes_by_executor()
    {
        var merged = SpellInventory.Merge(SpineLists(), Array.Empty<CraftedSpell>());
        Assert.Equal(7, merged.Count);
        Assert.Equal(3, merged["spell"].Count);
        Assert.Equal(2, merged["summon"].Count);
        Assert.Empty(merged["ritual"]);
        Assert.Equal("grimx/spell/release-observation", merged["spell"][1].Binding);
        Assert.Equal("DLC, .bamf, Aegis and Nexus", merged["spell"][0].Route);
        Assert.Equal("the API bench run", merged["spell"][1].Route);
        Assert.Equal("Agent Dispatch", merged["summon"][0].Route);
        Assert.Equal(new[] { 2 }, SpellOrbit.Related(merged["spell"], 1));
        Assert.Empty(SpellOrbit.Related(merged["spell"], 0));
        Assert.Equal(new[] { 0 }, SpellOrbit.Related(merged["summon"], 1));
    }

    [Fact]
    public void A_wiz_draft_names_itself_and_says_what_it_does()
    {
        var spell = Spellbook.FromDraft("schedule a nightly log sweep", "ritual",
            "**Name:** Nightly log sweep\nDoes: Clears logs older than a week every night.\n1. Find old logs\n2. Delete them", "nvidia/nemotron-3-super", "nebius-direct");
        Assert.Equal("Nightly log sweep", spell.Name);
        Assert.Equal("Clears logs older than a week every night", spell.Does);
        Assert.Equal("ritual", spell.Kind);
        Assert.Contains("Delete them", spell.Draft);

        var bare = Spellbook.FromDraft("Rename my screenshots by date", "spell", "1. List the screenshots\n2. Rename each", "m", "r");
        Assert.Equal("Rename my screenshots by date", bare.Name);
        Assert.Equal("1. List the screenshots", bare.Does);
    }

    [Fact]
    public void The_spellbook_keeps_what_it_is_given()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spellbook-{Guid.NewGuid():N}.json");
        try
        {
            var book = new Spellbook(path);
            Assert.Empty(book.Load());
            Assert.True(book.Add(Spellbook.FromDraft("a", "combo", "Name: Chain A\nDoes: Runs A then B.", "m", "r"), out _));
            Assert.True(book.Add(Spellbook.FromDraft("b", "charm", "Name: Charm B\nDoes: Binds B.", "m", "r"), out _));
            var again = new Spellbook(path).Load();
            Assert.Equal(new[] { "Chain A", "Charm B" }, again.Select(s => s.Name));
        }
        finally { File.Delete(path); }
    }

    // ---- Modal state ----

    [Fact]
    public void An_orb_opens_modally_and_closes_on_esc_or_a_click_off()
    {
        RunOnUi(async () =>
        {
            PhosphorToast.Suppressed = true;
            using var form = Host(out var tab, CraftReply(true, "Name: X"), out _);
            await tab.RefreshAsync();
            var orbs = tab.Orbs;
            var composer = form.Controls.Find("Holodex.SpellComposerHost", true).Single();
            Assert.True(orbs.Loaded);
            Assert.Null(orbs.OpenKind);

            // An empty orb never opens; it hands its kind to the composer.
            Assert.False(orbs.Open("ritual"));
            Assert.Null(orbs.OpenKind);

            Assert.True(orbs.Open("summon"));
            Assert.Equal("summon", orbs.OpenKind);
            Assert.False(composer.Visible);
            Assert.Equal(DockStyle.None, orbs.Dock);
            Assert.Equal(tab.ClientRectangle, orbs.Bounds);
            Paint(orbs);
            Assert.Equal(2, orbs.NodesDrawn.Count);

            orbs.Select(1);
            Assert.Equal(1, orbs.SelectedIndex);
            Paint(orbs);
            // The selected node is at the top of the orbit, grown 1.5x.
            var (sel, selR) = orbs.NodesDrawn[1];
            var (other, otherR) = orbs.NodesDrawn[0];
            Assert.True(sel.Y < other.Y);
            Assert.Equal(otherR * SpellOrbit.ExpandedScale, selR, 2);

            // Esc closes the whole orb.
            Assert.True(PressEscape(orbs));
            Assert.Null(orbs.OpenKind);
            Assert.True(composer.Visible);
            Assert.Equal(DockStyle.Fill, orbs.Dock);

            // A click off closes the card first, then the orb.
            orbs.Open("spell");
            orbs.Select(0);
            Paint(orbs);
            Click(orbs, new Point(2, 2));
            Assert.Equal(-1, orbs.SelectedIndex);
            Assert.Equal("spell", orbs.OpenKind);
            Click(orbs, new Point(2, 2));
            Assert.Null(orbs.OpenKind);
            Assert.True(composer.Visible);
        });
    }

    // ---- Composer ----

    [Fact]
    public void A_spell_crafted_from_the_composer_lands_in_its_orb_and_the_spellbook()
    {
        RunOnUi(async () =>
        {
            PhosphorToast.Suppressed = true;
            string? asked = null;
            string? askedKind = null;
            SpellsTab.Crafter crafter = (prompt, kind, _) =>
            {
                asked = prompt;
                askedKind = kind;
                return Task.FromResult((true, "nebius-direct", "nvidia/nemotron-3-super-120b-a12b",
                    "Name: Nightly downloads sweep\nDoes: Moves week-old files out of Downloads every night.\n1. List files\n2. Move the old ones"));
            };
            using var form = Host(out var tab, crafter, out string book);
            try
            {
                await tab.RefreshAsync();
                Assert.Empty(tab.Orbs.SpellsOf("ritual"));

                tab.Prompt.Text = "Schedule a nightly cleanup of my Downloads folder";
                Assert.Equal("ritual", tab.Kind); // Omnitek read "schedule"
                var spell = await tab.CraftAsync();

                Assert.NotNull(spell);
                Assert.Equal("ritual", askedKind);
                Assert.Contains("Craft one Ritual", asked);
                Assert.Contains("Name:", asked);
                var ritual = Assert.Single(tab.Orbs.SpellsOf("ritual"));
                Assert.Equal("Nightly downloads sweep", ritual.Name);
                Assert.Equal(SpellSource.WizDraft, ritual.Source);
                Assert.Equal("", tab.Prompt.Text);
                Assert.Contains("ADDED TO RITUALS", tab.StatusText);
                var kept = Assert.Single(new Spellbook(book).Load());
                Assert.Equal("ritual", kept.Kind);
                Assert.Equal("nvidia/nemotron-3-super-120b-a12b", kept.Model);
                // Spine's Spells are still there beside it.
                Assert.Equal(3, tab.Orbs.SpellsOf("spell").Count);
            }
            finally { File.Delete(book); }
        });
    }

    [Fact]
    public void A_craft_that_does_not_answer_keeps_nothing()
    {
        RunOnUi(async () =>
        {
            PhosphorToast.Suppressed = true;
            using var form = Host(out var tab, CraftReply(false, "No model is connected. Sign in with Hivemind."), out string book);
            try
            {
                await tab.RefreshAsync();
                tab.SetKind("combo", confident: false);
                tab.Prompt.Text = "Chain my morning checks";
                Assert.Null(await tab.CraftAsync());
                Assert.Empty(tab.Orbs.SpellsOf("combo"));
                Assert.False(File.Exists(book));
                Assert.StartsWith("NOT CRAFTED // No model is connected", tab.StatusText);
                Assert.Equal("Chain my morning checks", tab.Prompt.Text);
            }
            finally { File.Delete(book); }
        });
    }

    // ---- Helpers ----

    private static SpellsTab.Crafter CraftReply(bool ok, string output) =>
        (_, _, _) => Task.FromResult((ok, "nebius-direct", "nvidia/nemotron-3-super-120b-a12b", output));

    /// <summary>The tab in a drawer-sized form, shown off-screen without activation so its controls lay out and paint.</summary>
    private static Form Host(out SpellsTab tab, SpellsTab.Crafter crafter, out string bookPath)
    {
        bookPath = Path.Combine(Path.GetTempPath(), $"spellbook-{Guid.NewGuid():N}.json");
        var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(466, 680) };
        tab = new SpellsTab(new ArkStationClient { SimulateFreshInstall = true }, () => { }, new Spellbook(bookPath), crafter, () => Task.FromResult(SpineLists()));
        form.Controls.Add(tab);
        form.CreateControl();
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        form.PerformLayout();
        return form;
    }

    /// <summary>Paints <paramref name="c"/> through WM_PRINT: an off-screen window gets no WM_PAINT.</summary>
    private static void Paint(Control c)
    {
        using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
    }

    private static bool PressEscape(Control c)
    {
        var m = Message.Create(c.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
        var method = typeof(Control).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] args = { m, Keys.Escape };
        return (bool)method.Invoke(c, args)!;
    }

    /// <summary>The click without the focus a real one takes: focusing an off-screen test window would activate it.</summary>
    private static void Click(SpellOrbsView view, Point at) => view.ClickAt(at);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Runs <paramref name="work"/> on an STA thread with a WinForms context, so awaits come back to it, and returns
    /// when the work is done. With <paramref name="leaveLoop"/> the thread's message loop is left idle instead of torn
    /// down: tearing down a thread that built a Holodex form (the drawer, the HUD) hangs in Form.Dispose on master
    /// too (probed 2026-10-30), and an idle background loop ends with the test host.
    /// </summary>
    internal static void RunOnUi(Func<Task> work, int timeoutMs = 60_000, bool leaveLoop = false)
    {
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var context = new ApplicationContext();
            var sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);
            sync.Post(async _ =>
            {
                try { await work(); }
                catch (Exception ex) { error = ex; }
                finally
                {
                    done.Set();
                    if (!leaveLoop) context.ExitThread();
                }
            }, null);
            Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!done.Wait(timeoutMs)) throw new TimeoutException("the UI work did not finish");
        if (!leaveLoop && !thread.Join(timeoutMs)) throw new TimeoutException("the UI thread did not end");
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
