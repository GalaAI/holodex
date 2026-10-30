using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>v0.4.2 redesign: the Vault sizes to content and says detection once; the empty drawer shows three real steps.</summary>
public class RedesignTests
{
    [Theory]
    [InlineData(200, 1040, 360)]   // almost nothing to show: the floor keeps the tabs and one card in view
    [InlineData(600, 1040, 600)]   // sized to content
    [InlineData(2400, 1040, 1000)] // never taller than the screen less 40
    public void The_vault_is_as_tall_as_what_it_holds(int wanted, int screen, int expected) =>
        Assert.Equal(expected, VaultForm.VaultHeight(wanted, screen));

    [Fact]
    public void The_vault_says_detection_is_automatic_once_in_the_prompt()
    {
        Assert.Contains("works out which", VaultForm.IdlePrompt);
        Assert.DoesNotContain("DETECT AUTOMATICALLY", File.ReadAllText(Path.Combine(RepoRoot(), "VaultForm.cs")));
    }

    [Fact]
    public void The_empty_session_offers_sign_in_ask_and_scan_in_that_order()
    {
        var signedOut = HolodexLiteDrawerForm.FirstStepsFor(signedIn: false);
        Assert.Equal(new[] { "Holodex.FirstStep.SignIn", "Holodex.FirstStep.Ask", "Holodex.FirstStep.Scan" }, signedOut.Select(s => s.Name));
        Assert.Equal("1 // SIGN IN WITH HIVEMIND", signedOut[0].Kicker);
        Assert.False(signedOut[0].Done);
        Assert.Equal("2 // ASK NEMOTRON", signedOut[1].Kicker);
        Assert.Equal("3 // SCAN YOUR PC", signedOut[2].Kicker);

        // Signed in, step 1 is marked done rather than offered again.
        var signedIn = HolodexLiteDrawerForm.FirstStepsFor(signedIn: true);
        Assert.True(signedIn[0].Done);
        Assert.StartsWith("DONE //", signedIn[0].Kicker);
    }

    [Fact]
    public void Tabs_are_a_button_variant_of_their_own()
    {
        Assert.Contains(PhosphorButtonVariant.Tab, Enum.GetValues<PhosphorButtonVariant>());
        string vault = File.ReadAllText(Path.Combine(RepoRoot(), "VaultForm.cs"));
        Assert.Contains("new PhosphorButton(\"Connected\", PhosphorButtonVariant.Tab)", vault);
        Assert.Contains("new PhosphorButton(\"Catalog\", PhosphorButtonVariant.Tab)", vault);
        Assert.Contains("Vault.SetUpPcButton", vault);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omniwand.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Omniwand.csproj not found above the test output.");
    }
}
