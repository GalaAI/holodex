using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// v0.4.2: Holodex on someone else's PC. No global shortcut that a keyboard layout types with AltGr, no quit
/// shortcut at all, middle-hold and hold-to-draw opt-in on a new install, no writes into OculusDash without a real
/// headset stream, and the Ark route only for the real Ark gateway.
/// </summary>
public class SomeoneElsesPcTests
{
    // ---- Shortcuts ------------------------------------------------------------------------------------------------

    [Fact]
    public void Quit_is_not_a_global_shortcut()
    {
        Assert.DoesNotContain(GlobalHotkeys.All, s => s.Key == Keys.Q);
        Assert.DoesNotContain(GlobalHotkeys.All, s => s.Id == 9002);
        Assert.Equal(GlobalHotkeys.All.Count, GlobalHotkeys.All.Select(s => s.Id).Distinct().Count());
        Assert.All(GlobalHotkeys.All, s => Assert.InRange(s.Id, GlobalHotkeys.FirstId, GlobalHotkeys.LastId));
    }

    [Fact]
    public void Labels_read_the_way_people_type_them()
    {
        Assert.Equal("Ctrl+Alt+Space", GlobalHotkeys.Ask.Label);
        Assert.Equal("Ctrl+Alt+H", GlobalHotkeys.Holodex.Label);
        Assert.Equal("Ctrl+Shift+Space", GlobalHotkeys.Orb.Label);
        Assert.False(GlobalHotkeys.Orb.CtrlAlt);
    }

    [Theory]
    [InlineData(1, "@", true)]   // German AltGr+Q
    [InlineData(1, "€", true)]   // German AltGr+E
    [InlineData(-1, "", true)]   // a dead key is a collision too
    [InlineData(0, "", false)]   // US: Ctrl+Alt+Q types nothing
    [InlineData(1, "\u0011", false)] // a control character is not typing
    [InlineData(1, " ", false)] // AltGr+Space as a no-break space is not worth losing Ask over
    public void A_ctrl_alt_shortcut_collides_only_when_it_types(int result, string typed, bool collides) =>
        Assert.Equal(collides, GlobalHotkeys.Collides(result, typed));

    [Fact]
    public void Us_english_leaves_every_ctrl_alt_shortcut_free()
    {
        // 0x04090409 is the US layout. Windows answers ToUnicodeEx for any layout id it knows, loaded or not; when it
        // does not know it, the call returns 0 and nothing collides either.
        var us = new IntPtr(0x04090409);
        foreach (var spec in GlobalHotkeys.All.Where(s => s.CtrlAlt))
            Assert.Null(GlobalHotkeys.CtrlAltTypes((uint)spec.Key, us));
    }

    // ---- Opt-in triggers -------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_install_starts_with_middle_hold_and_hold_to_draw_off()
    {
        string path = TempFile();
        var s = TriggerSettings.LoadOrCreate(existingInstall: false, path);
        Assert.False(s.MiddleHoldOpensOrb);
        Assert.False(s.HoldCtrlToDraw);
        Assert.False(s.HoldRightClickToDraw);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void An_install_that_already_ran_keeps_what_it_had_then_the_saved_choice_wins()
    {
        string path = TempFile();
        var first = TriggerSettings.LoadOrCreate(existingInstall: true, path);
        Assert.True(first.MiddleHoldOpensOrb);
        Assert.True(first.HoldCtrlToDraw);
        Assert.False(first.HoldRightClickToDraw);

        first.MiddleHoldOpensOrb = false;
        first.Save(path);
        // Later starts read the file, whatever kind of install it is.
        Assert.False(TriggerSettings.LoadOrCreate(existingInstall: true, path).MiddleHoldOpensOrb);
        Assert.True(TriggerSettings.LoadOrCreate(existingInstall: false, path).HoldCtrlToDraw);
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "holodex-triggers-" + Guid.NewGuid().ToString("N") + ".json");

    // ---- VR memory writes ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Auto", "Meta Air Link / Link", true)]
    [InlineData("AlwaysOn", "Meta Air Link / Link", true)]
    [InlineData("Off", "Meta Air Link / Link", false)]
    [InlineData("Auto", null, false)]
    [InlineData("AlwaysOn", null, false)] // forced VR mode with no stream never touches OculusDash
    public void OculusDash_is_written_only_during_a_real_stream_with_vr_mode_on(string mode, string? stream, bool allowed) =>
        Assert.Equal(allowed, VrStreamDetector.ShouldAllowCacheFlush(Enum.Parse<VrStreamMode>(mode), stream));

    [Fact]
    public void Hiding_the_cursor_on_a_plain_desktop_never_opens_OculusDash()
    {
        VrStreamDetector.CacheFlushAllowed = false;
        Assert.False(VrStreamDetector.EnsureOculusDashCursorCacheClean(forceFlush: true));
    }

    // ---- The Ark route ---------------------------------------------------------------------------------------------

    /// <summary>The live Ark gateway's answer, read from 127.0.0.1:8788/model-gateway/health on 2026-10-30 (trimmed).</summary>
    private const string LiveArkHealth = """
        {"ok":true,"service":"ark-model-gateway","layer":"backend-core","compatibility":"openai-chat-completions",
         "compatibilities":["openai-chat-completions","anthropic-messages"],"ownerOperated":true}
        """;

    [Fact]
    public void Only_the_ark_gateways_own_health_answer_counts_as_the_ark()
    {
        Assert.True(ArkStationClient.IsArkGatewayHealth(LiveArkHealth));
        Assert.False(ArkStationClient.IsArkGatewayHealth("""{"ok":true}"""));
        Assert.False(ArkStationClient.IsArkGatewayHealth("""{"ok":false,"service":"ark-model-gateway","compatibility":"openai-chat-completions"}"""));
        Assert.False(ArkStationClient.IsArkGatewayHealth("""{"ok":true,"service":"some-other-app","compatibility":"openai-chat-completions"}"""));
        Assert.False(ArkStationClient.IsArkGatewayHealth("<html>Not Found</html>"));
        Assert.False(ArkStationClient.IsArkGatewayHealth(""));
    }

    [Fact]
    public void Without_a_probe_the_ark_route_is_not_named()
    {
        var ark = new ArkStationClient();
        Assert.False(ark.ArkRouteReady);
        Assert.NotEqual(ArkStationClient.RouteArkApi, ark.NextTextRoute(ArkStationClient.DefaultNemotronSuperModel));
    }
}
