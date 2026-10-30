using System.Text.RegularExpressions;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The 2026-10-30 UI audit: what a judge reads is plain, every number has a name, and no first-run text sends a
/// signed-out judge to a key or to Ark lore.
/// </summary>
public class AuditFixTests
{
    [Fact]
    public void Your_pc_shows_plain_kinds_and_counts_named_like_its_headings()
    {
        Assert.Equal("Service", PcSetupForm.PlainKind(OmnitekTag.Machina("service")));
        Assert.Equal("MCP server", PcSetupForm.PlainKind(OmnitekTag.McpServer));
        Assert.Equal("App", PcSetupForm.PlainKind(OmnitekTag.App));
        Assert.Equal("Device", PcSetupForm.PlainKind(OmnitekTag.Gear));
        Assert.Equal("Firmware", PcSetupForm.PlainKind(OmnitekTag.Plain("Firmware")));
        foreach (InvGroup g in Enum.GetValues<InvGroup>())
            Assert.DoesNotMatch("MACHINA|ARTIFACT|GEAR|MOD\\b|CANON", PcSetupForm.CountLabel(g));
    }

    [Fact]
    public void Spell_orb_slots_say_what_they_do_without_lore()
    {
        foreach (var slot in GrimXFlowOrb.Slots)
            Assert.DoesNotMatch("Relic|Deluge|Automaton|Pod|Scroll|Orchestra|cadence", slot.Subtitle);
    }

    [Fact]
    public void Sign_in_tooltip_names_the_users_account_not_an_owner_key()
    {
        Assert.True(PhosphorTips.TryGet("Holodex.HivemindSignInButton", out var tip));
        Assert.DoesNotContain("owner key", tip.Body);
    }

    [Fact]
    public void First_run_text_carries_no_lore_or_key_demand()
    {
        string root = RepoRoot();
        var banned = new (string File, string Pattern)[]
        {
            ("OmniwandHudForm.cs", "THE SCREEN NEEDS A NEBIUS KEY|fallback orb|GhostTrailer ribbon"),
            ("ScreenSnipAndElementPickerForm.cs", "Phosphor annotations|HWND|\"Save to Codex\"|Vision request to \\{"),
            ("WizCraftingWidgetForm.cs", "4GE BENCH|Input Studio\\.|save to Codex"),
            ("AInboxStack.cs", "\"Scroll for content\""),
            ("Program.cs", "ARK APERTURE|Coral Bloom|Aegis Lock"),
            ("PhosphorComposer.cs", "\"Sign in with Hivemind\""),
        };
        foreach (var (file, pattern) in banned)
            Assert.DoesNotMatch(pattern, File.ReadAllText(Path.Combine(root, file)));
    }

    [Fact]
    public void Pointer_does_not_swap_on_one_reading_near_the_taskbar()
    {
        var guard = new OverlayZGuard();
        Assert.False(guard.Settle(true));
        Assert.False(guard.Settle(false));
        Assert.False(guard.Settle(true));
        Assert.False(guard.Settle(true));
        Assert.False(guard.Covered);
        Assert.True(guard.Settle(true));
        Assert.True(guard.Covered);
    }

    [Fact]
    public void Re_hiding_the_pointer_lists_no_processes_without_oculus_dash()
    {
        // The flush is allowed (a stream was seen), but Detect found no OculusDash: the call returns at once.
        // With a real OculusDash running the call would write its memory, so the test leaves that PC alone.
        if (System.Diagnostics.Process.GetProcessesByName("OculusDash").Length > 0) return;
        VrStreamDetector.CacheFlushAllowed = true;
        try
        {
            VrStreamDetector.Detect();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true);
            Assert.True(clock.ElapsedMilliseconds < 50, $"50 re-hides took {clock.ElapsedMilliseconds} ms");
        }
        finally { VrStreamDetector.CacheFlushAllowed = false; }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omniwand.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Omniwand.csproj not found above the test output.");
    }
}
