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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omniwand.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Omniwand.csproj not found above the test output.");
    }
}
