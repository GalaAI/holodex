using System.Text.RegularExpressions;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// v0.4.2: what a judge reads matches the build. The version comes from the assembly, shortcuts come from the one
/// table, and no stale version or shortcut is left in the source.
/// </summary>
public class JudgeTextTests
{
    [Fact]
    public void The_version_shown_is_the_assemblys()
    {
        var v = typeof(HolodexVersion).Assembly.GetName().Version!;
        Assert.Equal($"{v.Major}.{v.Minor}.{v.Build}", HolodexVersion.Number);
        Assert.Equal("v" + HolodexVersion.Number, HolodexVersion.Display);
        Assert.Matches(@"^\d+\.\d+\.\d+$", HolodexVersion.Number);
    }

    [Fact]
    public void The_tray_tooltip_fits_windows_and_names_only_registered_shortcuts()
    {
        // Nothing is registered in a test process, so the tooltip names the version only.
        string tip = GlobalHotkeys.TrayTooltip(HolodexVersion.Display);
        Assert.Equal($"Holodex {HolodexVersion.Display}", tip);
        Assert.True(GlobalHotkeys.TrayTooltip("v" + new string('9', 200)).Length <= 127);
        Assert.Equal("", GlobalHotkeys.Shown(GlobalHotkeys.Holodex));
    }

    [Fact]
    public void The_sign_in_help_points_at_devpost_and_carries_no_credentials()
    {
        Assert.Contains("Devpost testing instructions", HivemindClient.SignInHelp);
        Assert.Contains("invite code", HivemindClient.SignInHelp);
        Assert.DoesNotMatch(@"@|password|[A-Z0-9]{6,}-", HivemindClient.SignInHelp);
    }

    [Fact]
    public void No_stale_version_or_shortcut_is_left_in_the_source()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            if (Regex.IsMatch(text, @"[vV]0\.3\b(?!\.\d)")) offenders.Add($"{name}: v0.3");
            if (Regex.IsMatch(text, @"Version = ""0\.\d+\.\d+""")) offenders.Add($"{name}: hard-coded client version");
            if (text.Contains("\"Ctrl+Alt+Q\"")) offenders.Add($"{name}: Ctrl+Alt+Q");
            if (Regex.IsMatch(text, @"[""\s(]Alt\+[SWE]\b") && !name.Equals("GlobalHotkeys.cs")) offenders.Add($"{name}: Alt+S/W/E without Ctrl");
            // PhosphorTokens.cs is generated from packages/tokens and quotes their descriptions as they are.
            if (name != "PhosphorTokens.cs" && (text.Contains((char)0x2014) || text.Contains((char)0x2013))) offenders.Add($"{name}: em or en dash");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void Headers_say_ark_only_while_the_ark_route_is_there()
    {
        Assert.Equal("ARK // WAND", HolodexLiteDrawerForm.HeaderEyebrow(true, "ARK // WAND", "HOLODEX // WAND"));
        Assert.Equal("HOLODEX // WAND", HolodexLiteDrawerForm.HeaderEyebrow(false, "ARK // WAND", "HOLODEX // WAND"));
        // A client that never probed (any judge PC before the first ask) has no Ark route.
        Assert.False(new ArkStationClient().ArkRouteReady);
    }

    [Fact]
    public void Public_surfaces_print_no_port_numbers()
    {
        string root = RepoRoot();
        // The HUD status row and the drawer header are what a judge sees first.
        foreach (string name in new[] { "OmniwandHudForm.cs" })
            Assert.DoesNotMatch(@"""[A-Z ]*SPINE 8842", File.ReadAllText(Path.Combine(root, name)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omniwand.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Omniwand.csproj not found above the test output.");
    }
}
