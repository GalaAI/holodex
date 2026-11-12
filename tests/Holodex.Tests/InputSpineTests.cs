using System.Text.Json.Nodes;
using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The Input Spine inside Holodex (Cursor Mod, step 1). The first half ports GalaAI/ark
/// <c>packages/input-spine-core/src/spine.test.ts</c> case for case; the second covers what Holodex adds: the Cursor
/// Kit schema, the shell gate and its token, shell commands confirmed on save, and the Origin check.
/// </summary>
public class InputSpineTests
{
    private static CursorKit Kit(string actions, string bindings = "[]") =>
        CursorKit.Parse($$"""{"actions":{{actions}},"bindings":{{bindings}}}""");

    private static string Shell(string id, string command = "echo hi") =>
        $$"""{"id":"{{id}}","name":"{{id}}","kind":"shell","command":"{{command}}"}""";

    private static string Seq(string id, string steps, bool stopOnError = true) =>
        $$"""{"id":"{{id}}","name":"{{id}}","kind":"sequence","steps":{{steps}},"stopOnError":{{(stopOnError ? "true" : "false")}}}""";

    private sealed class FakeRunner : ISpineRunner
    {
        private long _clock;
        public List<string> Ops { get; } = new();
        public List<string> Shells { get; } = new();
        public Func<KitAction, SpineOutcome> Shell { get; set; } = _ => SpineOutcome.Success("ok");
        public Func<SpineOutcome> Agent { get; set; } = () => SpineOutcome.Success("answered");

        public Task<SpineOutcome> InputAsync(string operation, KitAction action) { Ops.Add(operation); return Task.FromResult(SpineOutcome.Success()); }
        public Task<SpineOutcome> ShellAsync(KitAction action) { Shells.Add(action.Command!); return Task.FromResult(Shell(action)); }
        public Task<SpineOutcome> AgentAsync(string prompt, IReadOnlyList<string> capabilities) => Task.FromResult(Agent());
        public Task<SpineOutcome> CursorAsync(string profile) => Task.FromResult(SpineOutcome.Success());
        public long Now() => ++_clock;
    }

    // ---- Trigger matching -------------------------------------------------------------------------------------------

    [Fact]
    public void A_hotkey_matches_regardless_of_modifier_order() =>
        Assert.Equal(InputSpine.NormaliseMatch("hotkey", "CTRL+ALT+K"), InputSpine.NormaliseMatch("hotkey", "Alt + ctrl + K"));

    [Fact]
    public void Non_hotkey_triggers_keep_their_order() =>
        Assert.NotEqual(InputSpine.NormaliseMatch("voice", "open the pod"), InputSpine.NormaliseMatch("voice", "pod the open"));

    // ---- Resolve ------------------------------------------------------------------------------------------------------

    private static readonly CursorKit Scoped = Kit($"[{Shell("global")},{Shell("in-code")}]", """
        [{"id":"b1","trigger":{"kind":"hotkey","match":"CTRL+K"},"actionId":"global","enabled":true},
         {"id":"b2","trigger":{"kind":"hotkey","match":"CTRL+K"},"actionId":"in-code","profile":"Code","enabled":true}]
        """);

    [Fact]
    public void An_app_scoped_binding_beats_a_global_one() =>
        Assert.Equal("in-code", InputSpine.Resolve(Scoped, new KitTrigger("hotkey", "ctrl+k"), "code")?.Action.Id);

    [Fact]
    public void The_global_binding_applies_in_every_other_app() =>
        Assert.Equal("global", InputSpine.Resolve(Scoped, new KitTrigger("hotkey", "CTRL+K"), "explorer")?.Action.Id);

    [Fact]
    public void An_unbound_trigger_resolves_to_nothing() =>
        Assert.Null(InputSpine.Resolve(Scoped, new KitTrigger("hotkey", "CTRL+J"), null));

    [Fact]
    public void A_disabled_binding_is_skipped()
    {
        var kit = Kit($"[{Shell("a")}]", """[{"id":"b","trigger":{"kind":"voice","match":"go"},"actionId":"a","enabled":false}]""");
        Assert.Null(InputSpine.Resolve(kit, new KitTrigger("voice", "go"), null));
    }

    [Fact]
    public void A_binding_to_a_deleted_action_resolves_to_nothing()
    {
        var kit = Kit("[]", """[{"id":"b","trigger":{"kind":"voice","match":"go"},"actionId":"gone","enabled":true}]""");
        Assert.Null(InputSpine.Resolve(kit, new KitTrigger("voice", "go"), null));
    }

    // ---- Sequence validation ------------------------------------------------------------------------------------------

    [Fact]
    public void A_sequence_that_reaches_itself_is_a_cycle()
    {
        var kit = Kit($"""[{Seq("a", "[\"b\"]")},{Seq("b", "[\"a\"]")}]""");
        Assert.Equal(new[] { "a", "b" }, InputSpine.FindSequenceProblems(kit).Cycles.OrderBy(c => c));
    }

    [Fact]
    public void Shared_steps_are_not_a_cycle()
    {
        var kit = Kit($"""[{Shell("leaf")},{Seq("one", "[\"leaf\"]")},{Seq("two", "[\"leaf\",\"one\"]")}]""");
        Assert.Empty(InputSpine.FindSequenceProblems(kit).Cycles);
    }

    [Fact]
    public void A_step_naming_a_missing_action_is_reported()
    {
        var kit = Kit($"""[{Seq("a", "[\"nope\"]")}]""");
        Assert.Equal(new[] { ("a", "nope") }, InputSpine.FindSequenceProblems(kit).Missing);
    }

    // ---- Running ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Keys_go_through_the_input_operation()
    {
        var kit = Kit("""[{"id":"k","name":"k","kind":"keys","key":"F5","modifiers":["CTRL"]}]""");
        var runner = new FakeRunner();
        await InputSpine.RunActionAsync(kit, kit.Actions[0], runner);
        Assert.Equal(new[] { "pc.key.press" }, runner.Ops);
    }

    [Fact]
    public async Task A_sequence_stops_at_the_first_failure_when_told_to()
    {
        var kit = Kit($"""[{Shell("one", "1")},{Shell("two", "2")},{Shell("three", "3")},{Seq("seq", "[\"one\",\"two\",\"three\"]")}]""");
        var runner = new FakeRunner();
        runner.Shell = _ => runner.Shells.Count == 2 ? SpineOutcome.Fail("boom") : SpineOutcome.Success();
        var result = await InputSpine.RunActionAsync(kit, kit.Actions[3], runner);
        Assert.False(result.Ok);
        Assert.Equal(new[] { "one", "two" }, result.Steps!.Select(s => s.ActionId));
    }

    [Fact]
    public async Task Without_stopOnError_every_step_runs_but_failure_is_reported()
    {
        var kit = Kit($"""[{Shell("one")},{Shell("two")},{Seq("seq", "[\"one\",\"two\"]", stopOnError: false)}]""");
        var runner = new FakeRunner();
        runner.Shell = _ => runner.Shells.Count == 1 ? SpineOutcome.Fail("boom") : SpineOutcome.Success();
        var result = await InputSpine.RunActionAsync(kit, kit.Actions[2], runner);
        Assert.Equal(2, result.Steps!.Count);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_missing_step_fails_that_step_instead_of_throwing()
    {
        var kit = Kit($"""[{Seq("seq", "[\"ghost\"]")}]""");
        var result = await InputSpine.RunActionAsync(kit, kit.Actions[0], new FakeRunner());
        Assert.False(result.Ok);
        Assert.Contains("ghost", result.Steps![0].Error);
    }

    [Fact]
    public async Task A_cycle_that_survived_validation_is_stopped_by_the_depth_guard()
    {
        var kit = Kit($"""[{Seq("a", "[\"b\"]")},{Seq("b", "[\"a\"]")}]""");
        var result = await InputSpine.RunActionAsync(kit, kit.Actions[0], new FakeRunner());
        Assert.False(result.Ok);
        Assert.Contains("cycle", result.ToJson().ToJsonString());
    }

    [Fact]
    public async Task A_runner_that_throws_becomes_a_failed_result()
    {
        var kit = Kit($"[{Shell("a")}]");
        var runner = new FakeRunner { Shell = _ => throw new InvalidOperationException("shell exploded") };
        var result = await InputSpine.RunActionAsync(kit, kit.Actions[0], runner);
        Assert.False(result.Ok);
        Assert.Equal("shell exploded", result.Error);
    }

    // ---- Cursor Kit schema --------------------------------------------------------------------------------------------

    [Fact]
    public void A_kit_fills_defaults_and_round_trips()
    {
        var kit = Kit($"[{Shell("a")}]", """[{"id":"b","trigger":{"kind":"hotkey","match":"CTRL+K"},"actionId":"a"}]""");
        var a = kit.Actions[0];
        Assert.Equal(("powershell", 30_000), (a.Shell, a.TimeoutMs));
        Assert.True(kit.Bindings[0].Enabled);
        var again = CursorKit.Parse(kit.ToJson());
        Assert.Equal(kit.ToJson().ToJsonString(), again.ToJson().ToJsonString());
    }

    [Theory]
    [InlineData("""{"actions":[{"id":"a","name":"a","kind":"shell","command":"x","extra":1}]}""", "unknown field")]
    [InlineData("""{"actions":[{"id":"Bad Id","name":"a","kind":"open","target":"x"}]}""", "lowercase")]
    [InlineData("""{"actions":[{"id":"a","name":"a","kind":"teleport"}]}""", "not an action kind")]
    [InlineData("""{"actions":[{"id":"k","name":"k","kind":"keys","key":"K","modifiers":["HYPER"]}]}""", "CTRL, ALT")]
    [InlineData("""{"version":"9.9.9"}""", "version")]
    public void A_kit_that_breaks_the_contract_is_refused(string json, string reason) =>
        Assert.Contains(reason, Assert.Throws<KitException>(() => CursorKit.Parse(json)).Message);

    // ---- The server: Origin, shell gate, token, save confirmation ----------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "holodex-spine-" + Guid.NewGuid().ToString("N"));
        public ShellGate Gate { get; }
        public KitStore Store { get; }
        public FakeRunner Runner { get; } = new();
        public InputSpineServer Server { get; }

        public Rig()
        {
            Gate = new ShellGate(Dir);
            Store = new KitStore(Path.Combine(Dir, "cursor-kit.json"));
            Server = new InputSpineServer(Store, Gate, Runner);
        }

        public Task<(int Status, JsonObject Body)> Post(string path, string body, string? token = null, string? origin = null)
        {
            var headers = new Dictionary<string, string>();
            if (token != null) headers["x-cursor-mod-token"] = token;
            if (origin != null) headers["origin"] = origin;
            return Server.HandleAsync("POST", path, headers, body);
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public async Task A_foreign_origin_is_refused_and_a_local_one_is_served()
    {
        using var rig = new Rig();
        var (status, _) = await rig.Post("/api/spine/event", """{"trigger":{"kind":"voice","match":"go"}}""", origin: "https://evil.example");
        Assert.Equal(403, status);
        (status, _) = await rig.Post("/api/spine/event", """{"trigger":{"kind":"voice","match":"go"}}""", origin: "http://127.0.0.1:8842");
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Only_config_recent_and_spells_answer_a_get()
    {
        using var rig = new Rig();
        var none = new Dictionary<string, string>();
        Assert.Equal(200, (await rig.Server.HandleAsync("GET", "/api/spine/config", none, "")).Status);
        Assert.Equal(200, (await rig.Server.HandleAsync("GET", "/api/spine/grimx/spells", none, "")).Status);
        Assert.Equal(404, (await rig.Server.HandleAsync("GET", "/api/spine/run", none, "")).Status);
    }

    [Fact]
    public async Task Shell_actions_stay_off_until_turned_on_and_then_need_the_token()
    {
        using var rig = new Rig();
        rig.Store.Save(Kit($"[{Shell("a")}]"));

        var (_, off) = await rig.Post("/api/spine/run", """{"actionId":"a"}""");
        Assert.False(off["result"]!["ok"]!.GetValue<bool>());
        Assert.Contains("off", off["result"]!["error"]!.GetValue<string>());

        string token = rig.Gate.TurnOn();
        var (_, noToken) = await rig.Post("/api/spine/run", """{"actionId":"a"}""", token: "wrong");
        Assert.Contains("token", noToken["result"]!["error"]!.GetValue<string>());
        Assert.Empty(rig.Runner.Shells);

        var (_, ran) = await rig.Post("/api/spine/run", """{"actionId":"a"}""", token: token);
        Assert.True(ran["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "echo hi" }, rig.Runner.Shells);
        Assert.Contains("ran a", File.ReadAllText(rig.Gate.LogPath));

        rig.Gate.TurnOff();
        Assert.False(rig.Gate.TokenMatches(token));
    }

    [Fact]
    public async Task A_code_agent_passes_the_same_gate_but_a_read_agent_does_not_need_it()
    {
        using var rig = new Rig();
        rig.Store.Save(Kit("""
            [{"id":"ask","name":"ask","kind":"agent","prompt":"what time is it"},
             {"id":"fix","name":"fix","kind":"agent","prompt":"fix the build","capabilities":["code"]}]
            """));
        var (_, ask) = await rig.Post("/api/spine/run", """{"actionId":"ask"}""");
        Assert.True(ask["result"]!["ok"]!.GetValue<bool>());
        var (_, fix) = await rig.Post("/api/spine/run", """{"actionId":"fix"}""");
        Assert.False(fix["result"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Saving_new_shell_commands_needs_the_token_and_each_command_confirmed()
    {
        using var rig = new Rig();
        string kit = $$"""{"actions":[{{Shell("a", "Get-Date")}}],"bindings":[]}""";

        var (_, noToken) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}}}""");
        Assert.False(noToken["ok"]!.GetValue<bool>());
        Assert.Contains("token", noToken["error"]!.GetValue<string>());

        string token = rig.Gate.TurnOn();
        var (_, unconfirmed) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}}}""", token: token);
        Assert.Equal("Get-Date", unconfirmed["unconfirmedShellCommands"]![0]!.GetValue<string>());
        Assert.False(File.Exists(rig.Store.Path));

        var (_, saved) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}},"confirmedShellCommands":["Get-Date"]}""", token: token);
        Assert.True(saved["ok"]!.GetValue<bool>());

        // A save that keeps the same commands and changes nothing else about them needs no confirmation again.
        var (_, again) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}}}""");
        Assert.True(again["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_save_with_a_sequence_cycle_is_refused()
    {
        using var rig = new Rig();
        string actions = $"[{Seq("a", "[\"b\"]")},{Seq("b", "[\"a\"]")}]";
        var (_, r) = await rig.Post("/api/spine/save", "{\"config\":{\"actions\":" + actions + "}}");
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("cycle", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_event_is_recorded_and_grimx_gestures_list_as_spells()
    {
        using var rig = new Rig();
        rig.Store.Save(Kit("""[{"id":"go","name":"Go","kind":"open","target":"https://example.com"}]""",
            """[{"id":"g","trigger":{"kind":"gesture","match":"grimx/orb-1/go-now"},"actionId":"go"}]"""));
        await rig.Post("/api/spine/event", """{"trigger":{"kind":"gesture","match":"grimx/orb-1/go-now"}}""");
        await rig.Post("/api/spine/event", """{"trigger":{"kind":"voice","match":"nothing bound"}}""");
        var (_, recent) = await rig.Server.HandleAsync("GET", "/api/spine/recent", new Dictionary<string, string>(), "");
        var events = recent["events"]!.AsArray();
        Assert.Equal(2, events.Count);
        Assert.False(events[0]!["matched"]!.GetValue<bool>());
        Assert.Equal(new[] { "pc.app.open" }, rig.Runner.Ops);

        var (_, spells) = await rig.Server.HandleAsync("GET", "/api/spine/grimx/spells", new Dictionary<string, string>(), "");
        Assert.Equal("go-now", spells["spells"]!["orb-1"]![0]!["slug"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://example.com/page", false)]
    [InlineData("mailto:me@example.com", false)]
    [InlineData("C:\\Users\\me\\notes.txt", false)]
    [InlineData("notepad", true)]
    [InlineData("C:\\tools\\run.bat", true)]
    [InlineData("payload.lnk", true)]
    [InlineData("ms-msdt:/id", true)]
    [InlineData("file:///C:/x.exe", true)]
    public void An_open_that_starts_a_program_counts_as_running_code(string target, bool runsCode) =>
        Assert.Equal(runsCode, CursorKit.OpenRunsCode(target));

    [Fact]
    public async Task Opening_a_program_passes_the_shell_gate_and_needs_confirming_on_save()
    {
        using var rig = new Rig();
        string kit = """{"actions":[{"id":"go","name":"Go","kind":"open","target":"C:\\tools\\run.bat"}]}""";
        var (_, refused) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}}}""");
        Assert.False(refused["ok"]!.GetValue<bool>());

        string token = rig.Gate.TurnOn();
        var (_, saved) = await rig.Post("/api/spine/save", $$"""{"config":{{kit}},"confirmedShellCommands":["open C:\\tools\\run.bat"]}""", token: token);
        Assert.True(saved["ok"]!.GetValue<bool>());

        var (_, noToken) = await rig.Post("/api/spine/run", """{"actionId":"go"}""");
        Assert.False(noToken["result"]!["ok"]!.GetValue<bool>());
        Assert.Empty(rig.Runner.Ops);
        var (_, ran) = await rig.Post("/api/spine/run", """{"actionId":"go"}""", token: token);
        Assert.True(ran["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal(new[] { "pc.app.open" }, rig.Runner.Ops);
    }

    [Fact]
    public void The_cursor_designer_page_and_its_token_files_are_served_and_nothing_else()
    {
        using var rig = new Rig();
        string root = Path.Combine(rig.Dir, "out");
        Directory.CreateDirectory(Path.Combine(root, "cursor-studio", "tokens", "src"));
        Directory.CreateDirectory(Path.Combine(root, "fonts"));
        File.WriteAllText(Path.Combine(root, "cursor-studio", "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(root, "cursor-studio", "tokens", "src", "tokens.css"), ":root{}");
        File.WriteAllBytes(Path.Combine(root, "fonts", "ArchivoBlack-Regular.ttf"), new byte[] { 0, 1 });
        File.WriteAllText(Path.Combine(root, "secret.txt"), "no");
        var server = new InputSpineServer(rig.Store, rig.Gate, rig.Runner) { PageRoot = root };

        Assert.StartsWith("text/html", server.StaticFile("GET", "/")!.Value.ContentType);
        Assert.Equal("<!doctype html>", System.Text.Encoding.UTF8.GetString(server.StaticFile("GET", "/index.html")!.Value.Body));
        Assert.StartsWith("text/css", server.StaticFile("GET", "/tokens/src/tokens.css")!.Value.ContentType);
        Assert.Equal("font/ttf", server.StaticFile("GET", "/tokens/assets/fonts/ArchivoBlack-Regular.ttf")!.Value.ContentType);

        Assert.Null(server.StaticFile("POST", "/"));
        Assert.Null(server.StaticFile("GET", "/tokens/src/missing.css"));
        Assert.Null(server.StaticFile("GET", "/tokens/src/../../secret.txt"));
        Assert.Null(server.StaticFile("GET", "/secret.txt"));
        Assert.Null(server.StaticFile("GET", "/api/spine/config"));
    }

    // ---- Windows runner pieces that need no input device --------------------------------------------------------------

    [Theory]
    [InlineData("F5", 0x74)]
    [InlineData("f24", 0x87)]
    [InlineData("a", 0x41)]
    [InlineData("7", 0x37)]
    [InlineData("Enter", 0x0D)]
    [InlineData("PageUp", 0x21)]
    [InlineData("VolumeUp", 0xAF)]
    public void Key_names_map_to_virtual_keys(string name, int vk) => Assert.Equal((ushort)vk, WindowsSpineRunner.VirtualKeyFor(name));

    [Theory]
    [InlineData("NotAKey")]
    [InlineData(";")]
    [InlineData("F25")]
    public void Unknown_keys_are_refused_not_guessed(string name) => Assert.Null(WindowsSpineRunner.VirtualKeyFor(name));

    [Fact]
    public async Task A_cursor_profile_name_cannot_leave_the_cursor_folder()
    {
        var runner = new WindowsSpineRunner(new ArkStationClient());
        var r = await runner.CursorAsync("..\\..\\Windows");
        Assert.False(r.Ok);
        Assert.Contains("not a cursor profile name", r.Error);
    }
}
