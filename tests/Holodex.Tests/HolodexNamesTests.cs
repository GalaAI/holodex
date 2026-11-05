using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// Phase 1 of the rename: every boundary that still carries the Omniwand name from before it reads both spellings,
/// the current one first, so new and old installs keep working side by side.
/// </summary>
public class HolodexNamesTests
{
    [Fact]
    public void CredentialTargetsListTheCurrentSpellingFirst()
    {
        Assert.Equal(new[] { "Holodex:NebiusTokenFactory", "Omniwand:NebiusTokenFactory" }, HolodexNames.CredentialTargets("Holodex:NebiusTokenFactory"));
        Assert.Equal(new[] { "Holodex:HivemindToken", "Omniwand:HivemindToken" }, HolodexNames.CredentialTargets("Omniwand:HivemindToken"));
        Assert.Equal(new[] { "NEBIUS_API_KEY" }, HolodexNames.CredentialTargets("NEBIUS_API_KEY"));
    }

    [Fact]
    public void ReadEnvFallsBackToTheVariableFromBeforeTheRename()
    {
        string suffix = "NAMES_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        try
        {
            Environment.SetEnvironmentVariable("OMNIWAND_" + suffix, "old");
            Assert.Equal("old", HolodexNames.ReadEnv("HOLODEX_" + suffix));
            Environment.SetEnvironmentVariable("HOLODEX_" + suffix, "new");
            Assert.Equal("new", HolodexNames.ReadEnv("HOLODEX_" + suffix));
            Assert.Equal("new", HolodexNames.ReadEnv("OMNIWAND_" + suffix));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNIWAND_" + suffix, null);
            Environment.SetEnvironmentVariable("HOLODEX_" + suffix, null);
        }
    }

    [Fact]
    public void TuningKeysFromBeforeTheRenameReadAsHolodexKeys()
    {
        Assert.Equal("holodex-tip-dwell", HolodexNames.CurrentTuningKey("omniwand-tip-dwell"));
        Assert.Equal("holodex-tip-dwell", HolodexNames.CurrentTuningKey("holodex-tip-dwell"));
        Assert.Equal("dur-bloom", HolodexNames.CurrentTuningKey("dur-bloom"));
        Assert.Contains("holodex-tip-dwell", PhosphorTuning.Tunable);
    }

    [Fact]
    public void ListensOnBothControlPipes()
    {
        Assert.Equal(new[] { "ArkStation.Holodex.Control", "ArkStation.Omniwand.Control" }, HolodexNames.ControlPipes);
    }
}
