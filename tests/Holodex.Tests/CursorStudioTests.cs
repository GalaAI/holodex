using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The cursor half of the Cursor Mod (plan step 2). The first half ports GalaAI/ark
/// <c>packages/cursor-mod/src/cursor-mod.test.ts</c> (encoders, design check, role table, profile names); the second
/// covers the <c>/api/cursor/*</c> routes against a fake Windows host.
/// </summary>
public class CursorStudioTests
{
    private static byte[] Solid(int size, byte r, byte g, byte b, byte a)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
        return px;
    }

    // ---- Encoders ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cur_has_an_icondir_with_the_hotspot_and_a_bgra_bottom_up_dib()
    {
        const int size = 32;
        var buf = CursorFiles.EncodeCur(Solid(size, 10, 20, 30, 255), size, 3, 4);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(10)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(12)));
        Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(22 + 8)));
        Assert.Equal(22 + 40 + size * size * 4 + size / 8 * size, buf.Length);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, buf[62..66]);
    }

    [Fact]
    public void An_ani_wraps_frames_in_riff_acon_with_one_rate_per_frame()
    {
        const int size = 32;
        var buf = CursorFiles.EncodeAni(new[] { Solid(size, 0, 0, 0, 255), Solid(size, 255, 255, 255, 255) }, size, 0, 0, new uint[] { 2, 100000 });
        Assert.Equal("RIFF", Encoding.ASCII.GetString(buf, 0, 4));
        Assert.Equal((uint)(buf.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)));
        Assert.Equal("ACON", Encoding.ASCII.GetString(buf, 8, 4));
        int rate = buf.AsSpan().IndexOf("rate"u8);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(rate + 4)));
        Assert.Equal(100000u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(rate + 12)));
    }

    /// <summary>
    /// The writer matches Input Studio byte for byte: these hashes are what ark <c>packages/cursor-mod</c>'s
    /// <c>encodeCur</c> and <c>encodeAni</c> produced for the same input (bun, 2026-11-09).
    /// </summary>
    [Theory]
    [InlineData("cur32", "ac6d602ebb0048d939b2d60e03403d985000ca8826cdf34182aeb569a2f8f09d")]
    [InlineData("cur48", "37e92390e84d8cf85baf06f63bfc3e3aa77b11ed4d951d76465d723cb71de056")]
    [InlineData("ani32", "96829443663503caa26810d0c5fc10e054c04752f260e82e7f48552c0cad2e5e")]
    public void The_writer_matches_input_studio_byte_for_byte(string which, string sha256)
    {
        static byte[] Frame(int size, int seed)
        {
            var px = new byte[size * size * 4];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)((i * 31 + seed * 7) % 256);
            return px;
        }
        byte[] bytes = which switch
        {
            "cur32" => CursorFiles.EncodeCur(Frame(32, 1), 32, 5, 9),
            "cur48" => CursorFiles.EncodeCur(Frame(48, 2), 48, 0, 47),
            _ => CursorFiles.EncodeAni(new[] { Frame(32, 1), Frame(32, 2), Frame(32, 3) }, 32, 16, 16, new uint[] { 2, 3, 100000 }),
        };
        Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    // ---- Design check ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_complete_design_has_no_problems() =>
        Assert.Empty(CursorFiles.ValidateDesign(JsonNode.Parse("""
            {"shapes":{"a":{"kind":"ring","r":4,"w":1},"b":{"kind":"tri","size":6,"w":1}},
             "transitions":[{"role":"Arrow","from":"a","to":"b","mode":"morph"}]}
            """)));

    [Fact]
    public void Undefined_shapes_unknown_roles_and_bad_modes_are_named()
    {
        var problems = CursorFiles.ValidateDesign(JsonNode.Parse("""
            {"shapes":{"a":{"kind":"ring","r":4}},"transitions":[{"role":"Nope","from":"a","to":"zz","mode":"fade"}]}
            """));
        Assert.Contains("shape \"a\" is missing \"w\"", problems);
        Assert.Contains("\"Nope\" is not a Windows cursor role", problems);
        Assert.Contains("Nope.to names shape \"zz\", which is not defined", problems);
        Assert.Contains("Nope has mode \"fade\", expected morph or spin", problems);
    }

    [Fact]
    public void Every_windows_role_it_claims_is_covered()
    {
        Assert.Equal(32512u, CursorFiles.RoleIds["Arrow"]);
        Assert.Equal(14, CursorFiles.RoleIds.Count);
    }

    [Theory]
    [InlineData("../../Windows", "windows")]
    [InlineData("My Neon Set!", "my-neon-set")]
    [InlineData("...", "")]
    public void Profile_names_cannot_leave_the_folder(string raw, string safe) => Assert.Equal(safe, CursorFiles.SafeProfileName(raw));

    [Fact]
    public void A_reply_with_a_fence_and_preamble_still_yields_its_json()
    {
        var design = CursorFiles.DesignFromReply("Sure!\n```json\n{\"message\":\"hi\",\"shapes\":{},\"transitions\":[]}\n```");
        Assert.Equal("hi", design!["message"]!.GetValue<string>());
        Assert.Null(CursorFiles.DesignFromReply("no json here"));
    }

    // ---- Routes ------------------------------------------------------------------------------------------------------

    private sealed class FakeHost : ICursorHost
    {
        public List<(string Role, string File)> Installed { get; } = new();
        public int Restores { get; private set; }
        public string? LastImage { get; private set; }
        public string Reply { get; set; } = """{"message":"done","shapes":{"a":{"kind":"ring","r":4,"w":1}},"transitions":[{"role":"Arrow","from":"a","to":"a","mode":"morph"}]}""";

        public void Install(IReadOnlyList<(string Role, string File)> written, string backupPath) => Installed.AddRange(written);
        public void Restore(string backupPath) => Restores++;
        public Task<(bool Ok, string Text)> AskAsync(string prompt, string? imageDataUrl) { LastImage = imageDataUrl; return Task.FromResult((true, Reply)); }
    }

    private sealed class Rig : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "holodex-cursors-" + Guid.NewGuid().ToString("N"));
        public FakeHost Host { get; } = new();
        public InputSpineServer Server { get; }

        public Rig() => Server = new InputSpineServer(new KitStore(Path.Combine(Dir, "kit.json")), new ShellGate(Dir), new NoRunner(),
            new CursorStudio(Path.Combine(Dir, "cursors"), Host));

        public Task<(int Status, JsonObject Body)> Post(string path, string body) =>
            Server.HandleAsync("POST", path, new Dictionary<string, string>(), body);

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private sealed class NoRunner : ISpineRunner
    {
        public Task<SpineOutcome> InputAsync(string operation, KitAction action) => Task.FromResult(SpineOutcome.Fail("unused"));
        public Task<SpineOutcome> ShellAsync(KitAction action) => Task.FromResult(SpineOutcome.Fail("unused"));
        public Task<SpineOutcome> AgentAsync(string prompt, IReadOnlyList<string> capabilities) => Task.FromResult(SpineOutcome.Fail("unused"));
        public Task<SpineOutcome> CursorAsync(string profile) => Task.FromResult(SpineOutcome.Fail("unused"));
        public long Now() => 0;
    }

    private static string Frame(int size) => Convert.ToBase64String(Solid(size, 1, 2, 3, 255));

    [Fact]
    public async Task Build_writes_a_cur_for_one_frame_and_an_ani_for_several_then_install_takes_them()
    {
        using var rig = new Rig();
        string f = Frame(32);
        var (status, built) = await rig.Post("/api/cursor/build", $$"""
            {"cursors":[{"name":"Ark Arrow","role":"Arrow","size":32,"hotX":0,"hotY":0,"frames":["{{f}}"]},
                        {"name":"ark-wait","role":"Wait","size":32,"hotX":16,"hotY":16,"frames":["{{f}}","{{f}}"],"rates":[2,100000]}]}
            """);
        Assert.Equal(200, status);
        var written = built["written"]!.AsArray();
        Assert.EndsWith("ark-arrow.cur", written[0]!["file"]!.GetValue<string>());
        Assert.EndsWith("ark-wait.ani", written[1]!["file"]!.GetValue<string>());
        Assert.True(File.Exists(written[1]!["file"]!.GetValue<string>()));

        var (_, installed) = await rig.Post("/api/cursor/install", new JsonObject { ["written"] = written.DeepClone() }.ToJsonString());
        Assert.True(installed["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "Arrow", "Wait" }, rig.Host.Installed.Select(i => i.Role));

        await rig.Post("/api/cursor/restore", "");
        Assert.Equal(1, rig.Host.Restores);
    }

    [Theory]
    [InlineData("""{"cursors":[{"name":"a","role":"Banana","size":32,"hotX":0,"hotY":0,"frames":[]}]}""")]
    [InlineData("""{"cursors":[{"name":"a","role":"Arrow","size":33,"hotX":0,"hotY":0,"frames":[]}]}""")]
    [InlineData("""{"cursors":[{"name":"a","role":"Arrow","size":32,"hotX":40,"hotY":0,"frames":[]}]}""")]
    [InlineData("""{"cursors":[{"name":"a","role":"Arrow","size":32,"hotX":0,"hotY":0,"frames":["AAAA"]}]}""")]
    [InlineData("""{"cursors":[{"name":"a","role":"Arrow","size":32,"hotX":0,"hotY":0,"frames":["not base64!"]}]}""")]
    public async Task A_bad_cursor_is_refused_with_a_reason(string body)
    {
        using var rig = new Rig();
        var (status, reply) = await rig.Post("/api/cursor/build", body);
        Assert.Equal(400, status);
        Assert.False(reply["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Install_points_the_registry_only_at_cursor_files_holodex_wrote()
    {
        using var rig = new Rig();
        string outside = Path.Combine(rig.Dir, "evil.cur");
        Directory.CreateDirectory(rig.Dir);
        File.WriteAllBytes(outside, new byte[] { 0 });
        var (status, _) = await rig.Post("/api/cursor/install", new JsonObject
        {
            ["written"] = new JsonArray(new JsonObject { ["role"] = "Arrow", ["file"] = outside }),
        }.ToJsonString());
        Assert.Equal(400, status);
        Assert.Empty(rig.Host.Installed);
    }

    [Fact]
    public async Task Profiles_save_list_load_and_delete_under_a_safe_name()
    {
        using var rig = new Rig();
        var (_, saved) = await rig.Post("/api/cursor/profile/save", """{"name":"../My Set","design":{"shapes":{}}}""");
        Assert.Equal("my-set", saved["name"]!.GetValue<string>());

        var (_, list) = await rig.Server.HandleAsync("GET", "/api/cursor/profiles", new Dictionary<string, string>(), "");
        Assert.Equal("my-set", list["profiles"]![0]!.GetValue<string>());

        var (_, loaded) = await rig.Post("/api/cursor/profile/load", """{"name":"My Set"}""");
        Assert.NotNull(loaded["design"]!["shapes"]);

        await rig.Post("/api/cursor/profile/delete", """{"name":"my-set"}""");
        var (_, gone) = await rig.Post("/api/cursor/profile/load", """{"name":"my-set"}""");
        Assert.False(gone["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Generate_returns_a_checked_design_and_refuses_a_bad_one()
    {
        using var rig = new Rig();
        var (_, good) = await rig.Post("/api/cursor/generate", """{"prompt":"a neon ring"}""");
        Assert.True(good["ok"]!.GetValue<bool>());
        Assert.Equal("done", good["message"]!.GetValue<string>());

        rig.Host.Reply = """{"shapes":{"a":{"kind":"blob"}},"transitions":[]}""";
        var (_, bad) = await rig.Post("/api/cursor/generate", """{"prompt":"a blob"}""");
        Assert.False(bad["ok"]!.GetValue<bool>());
        Assert.Contains("unknown kind", bad["error"]!.GetValue<string>());

        var (status, _) = await rig.Post("/api/cursor/generate", """{"prompt":"x","imageDataUrl":"data:text/html,<b>"}""");
        Assert.Equal(400, status);
    }
}
