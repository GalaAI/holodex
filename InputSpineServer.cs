using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Holodex;

/// <summary>
/// Shell actions in a Cursor Kit (rulings, 2026-11-08 and 2026-11-09): allowed in every build, off until the user turns
/// them on in the Cursor Mod tab, and then only for a request that carries this install's token. Every shell action is
/// logged. Turning them on happens in Holodex itself, never over HTTP, so no caller can switch them on.
/// </summary>
internal sealed class ShellGate
{
    public const string TokenHeader = "X-Cursor-Mod-Token";
    private readonly string _dir;

    public ShellGate(string dir) => _dir = dir;

    public static ShellGate Default { get; } = new(Path.Combine(HolodexNames.DataRoot, "cursor-mod"));

    private string OnMarker => Path.Combine(_dir, "shell-actions-on");
    /// <summary>The token file. It sits in the user's own data folder, so other Windows users cannot read it; the Stream Deck plugin and the AHK bridge read it from here.</summary>
    public string TokenPath => Path.Combine(_dir, "shell-token");
    public string LogPath => Path.Combine(_dir, "shell-actions.log");

    public bool IsOn => File.Exists(OnMarker) && File.Exists(TokenPath);

    /// <summary>Turns shell actions on and mints a fresh token. Called from the Cursor Mod tab only.</summary>
    public string TurnOn()
    {
        Directory.CreateDirectory(_dir);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenPath, token);
        File.WriteAllText(OnMarker, DateTimeOffset.Now.ToString("O"));
        return token;
    }

    public void TurnOff()
    {
        try { File.Delete(OnMarker); } catch { }
        try { File.Delete(TokenPath); } catch { }
    }

    /// <summary>Whether a request's token is this install's token. Compared in constant time.</summary>
    public bool TokenMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented) || !File.Exists(TokenPath)) return false;
        string expected;
        try { expected = File.ReadAllText(TokenPath).Trim(); } catch { return false; }
        if (expected.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented.Trim()));
    }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {line.Replace('\n', ' ').Replace('\r', ' ')}\n");
        }
        catch { }
    }
}

/// <summary>Where the Cursor Kit lives: Input Studio's <c>spine.json</c> on an Ark PC, so both read the same file, else Holodex's own folder.</summary>
internal sealed class KitStore(string path)
{
    public string Path { get; } = path;

    public static KitStore Default { get; } = new(Directory.Exists(ArkStationClient.DefaultInputStudioStateDir)
        ? System.IO.Path.Combine(ArkStationClient.DefaultInputStudioStateDir, "spine.json")
        : System.IO.Path.Combine(HolodexNames.DataRoot, "cursor-mod", "cursor-kit.json"));

    /// <summary>A malformed file must not take the Cursor Mod down: it loads empty and the log says why.</summary>
    public CursorKit Load()
    {
        if (!File.Exists(Path)) return CursorKit.Empty;
        try { return CursorKit.Parse(File.ReadAllText(Path)); }
        catch (Exception ex) when (ex is KitException or JsonException or IOException)
        {
            CursorCrashGuard.Log($"cursor kit at {Path} is not usable, starting empty: {ex.Message}");
            return CursorKit.Empty;
        }
    }

    public void Save(CursorKit kit)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, kit.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>
/// The Input Spine endpoint inside Holodex (Cursor Mod plan, step 1): the spine routes Input Studio served, on the same
/// loopback port, so the Stream Deck, the AHK bridge and Spells keep their URLs. <see cref="HandleAsync"/> holds every
/// rule and takes no socket, so it tests without a network; <see cref="TryStart"/> puts it on 127.0.0.1:8842.
/// </summary>
internal sealed class InputSpineServer
{
    public const int Port = 8842;
    private const int MaxBodyBytes = 1 << 20;
    private readonly KitStore _store;
    private readonly ShellGate _gate;
    private readonly ISpineRunner _runner;
    private readonly List<JsonObject> _recent = new();
    private readonly object _recentLock = new();
    private TcpListener? _listener;

    public InputSpineServer(KitStore store, ShellGate gate, ISpineRunner runner)
    {
        _store = store;
        _gate = gate;
        _runner = runner;
    }

    private static readonly string[] ReadOnlyRoutes = { "/api/spine/config", "/api/spine/recent", "/api/spine/grimx/spells" };

    public async Task<(int Status, JsonObject Body)> HandleAsync(string method, string path, IReadOnlyDictionary<string, string> headers, string body)
    {
        // A page in any browser could otherwise post to this port. A browser always sends Origin on a cross-origin
        // request, so a foreign one is refused; callers with no Origin are local programs (Stream Deck, AHK, Holodex).
        if (headers.TryGetValue("origin", out var origin) && origin != $"http://127.0.0.1:{Port}" && origin != $"http://localhost:{Port}")
            return (403, Error($"refused cross-origin request from {origin}"));

        bool tokenOk = _gate.TokenMatches(headers.TryGetValue(ShellGate.TokenHeader.ToLowerInvariant(), out var t) ? t : null);
        try
        {
            if (method == "GET" && !ReadOnlyRoutes.Contains(path)) return (404, Error("not found"));
            if (method != "GET" && method != "POST") return (404, Error("not found"));
            JsonNode? json = method == "POST" && body.Length > 0 ? JsonNode.Parse(body) : null;
            return path switch
            {
                "/api/spine/config" => (200, Config()),
                "/api/spine/save" => (200, Save(json, tokenOk)),
                "/api/spine/grimx/spells" => (200, GrimxSpells()),
                "/api/spine/run" => (200, await RunAsync(json, tokenOk)),
                "/api/spine/event" => (200, await EventAsync(json, tokenOk)),
                "/api/spine/recent" => (200, Recent()),
                _ => (404, Error("not found")),
            };
        }
        catch (Exception ex) when (ex is KitException or JsonException)
        {
            return (400, Error(ex.Message));
        }
        catch (Exception ex)
        {
            CursorCrashGuard.Log($"input spine {path}: {ex}");
            return (500, Error(ex.Message));
        }
    }

    private JsonObject Config()
    {
        var kit = _store.Load();
        return new JsonObject { ["ok"] = true, ["config"] = kit.ToJson(), ["problems"] = Problems(kit) };
    }

    /// <summary>
    /// Saves a Cursor Kit. A kit that adds or changes a shell command is refused unless the request carries this
    /// install's token and lists those exact commands in <c>confirmedShellCommands</c>: the Cursor Mod tab shows each
    /// one to the user first (import confirmation, ruling 2026-11-09).
    /// </summary>
    private JsonObject Save(JsonNode? body, bool tokenOk)
    {
        var o = body as JsonObject ?? throw new KitException("the body must be an object");
        var kit = CursorKit.Parse(o["config"]);
        var problems = InputSpine.FindSequenceProblems(kit);
        if (problems.Cycles.Count > 0)
            return Fail($"sequence cycle through: {string.Join(", ", problems.Cycles)}", ("problems", Problems(kit)));

        var before = _store.Load().ShellCommands.ToHashSet(StringComparer.Ordinal);
        var added = kit.ShellCommands.Where(c => !before.Contains(c)).Distinct().ToList();
        if (added.Count > 0)
        {
            var confirmed = (o["confirmedShellCommands"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? new();
            var unconfirmed = added.Where(c => !confirmed.Contains(c)).ToList();
            if (!tokenOk || unconfirmed.Count > 0)
                return Fail(tokenOk
                    ? "This Cursor Kit adds shell commands that were not confirmed. Confirm each one in the Cursor Mod tab."
                    : "This Cursor Kit adds shell commands, so saving it needs this PC's Cursor Mod token.",
                    ("unconfirmedShellCommands", new JsonArray(unconfirmed.Select(c => (JsonNode)c).ToArray())));
            foreach (var c in added) _gate.Log($"confirmed shell command saved: {c}");
        }
        _store.Save(kit);
        return new JsonObject { ["ok"] = true, ["problems"] = Problems(kit) };
    }

    private async Task<JsonObject> RunAsync(JsonNode? body, bool tokenOk)
    {
        string? id = (body as JsonObject)?["actionId"]?.GetValue<string>();
        var kit = _store.Load();
        var action = kit.Actions.FirstOrDefault(a => a.Id == id);
        if (action == null) return Fail($"no action with id \"{id}\"");
        var result = await InputSpine.RunActionAsync(kit, action, new GatedRunner(_runner, _gate, tokenOk, $"run {id}"));
        return new JsonObject { ["ok"] = true, ["result"] = result.ToJson() };
    }

    /// <summary>What a device, hotkey or voice source posts when something fires.</summary>
    private async Task<JsonObject> EventAsync(JsonNode? body, bool tokenOk)
    {
        var o = body as JsonObject ?? throw new KitException("the body must be an object");
        CursorKit.Only(o, "event", "trigger", "profile", "context");
        var trigger = CursorKit.ParseTrigger(o["trigger"], "trigger");
        string? profile = CursorKit.OptText(o["profile"], "profile", 120);
        var kit = _store.Load();
        var hit = InputSpine.Resolve(kit, trigger, profile);
        SpineResult? result = hit == null ? null
            : await InputSpine.RunActionAsync(kit, hit.Value.Action, new GatedRunner(_runner, _gate, tokenOk, $"{trigger.Kind} {trigger.Match}"));

        // Every event is recorded, matched or not: an unbound trigger arriving proves the button reached Holodex.
        var seen = new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["trigger"] = trigger.ToJson(), ["matched"] = hit != null };
        if (profile != null) seen["profile"] = profile;
        if (hit != null) { seen["actionId"] = hit.Value.Action.Id; seen["ok"] = result!.Ok; }
        lock (_recentLock)
        {
            _recent.Insert(0, seen);
            if (_recent.Count > 50) _recent.RemoveRange(50, _recent.Count - 50);
        }
        var reply = new JsonObject { ["ok"] = true, ["matched"] = hit != null };
        if (result != null) reply["result"] = result.ToJson();
        return reply;
    }

    private JsonObject Recent()
    {
        lock (_recentLock)
            return new JsonObject { ["ok"] = true, ["events"] = new JsonArray(_recent.Select(e => e.DeepClone()).ToArray()) };
    }

    /// <summary>The Spells each Holodex Orb slot can cast: gesture bindings named <c>grimx/&lt;wire&gt;/&lt;slug&gt;</c>.</summary>
    private JsonObject GrimxSpells()
    {
        var kit = _store.Load();
        var spells = new JsonObject();
        foreach (var b in kit.Bindings)
        {
            if (b.Trigger.Kind != "gesture") continue;
            var m = Regex.Match(b.Trigger.Match.Trim(), "^grimx/([a-z0-9-]+)/([a-z0-9][a-z0-9-]*)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var action = kit.Actions.FirstOrDefault(a => a.Id == b.ActionId);
            if (action == null) continue;
            string wire = m.Groups[1].Value.ToLowerInvariant();
            var list = spells[wire] as JsonArray ?? new JsonArray();
            spells[wire] = list;
            var spell = new JsonObject { ["slug"] = m.Groups[2].Value.ToLowerInvariant(), ["name"] = action.Name };
            if (action.Description != null) spell["description"] = action.Description;
            spell["trigger"] = b.Trigger.Match;
            spell["enabled"] = b.Enabled;
            list.Add(spell);
        }
        return new JsonObject { ["ok"] = true, ["spells"] = spells };
    }

    private static JsonObject Problems(CursorKit kit)
    {
        var (cycles, missing) = InputSpine.FindSequenceProblems(kit);
        return new JsonObject
        {
            ["cycles"] = new JsonArray(cycles.Select(c => (JsonNode)c).ToArray()),
            ["missing"] = new JsonArray(missing.Select(m => (JsonNode)new JsonObject { ["sequenceId"] = m.SequenceId, ["missingId"] = m.MissingId }).ToArray()),
        };
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject Fail(string message, (string Key, JsonNode Value) extra = default)
    {
        var o = Error(message);
        if (extra.Key != null) o[extra.Key] = extra.Value;
        return o;
    }

    // ------------------------------------------------------------------------------------------------- the socket

    /// <summary>
    /// Listens on 127.0.0.1:8842 only. While Input Studio still holds the port (an Ark PC before step 4), this does
    /// nothing and says so in the log, so both never fight over it.
    /// </summary>
    public bool TryStart()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            CursorCrashGuard.Log($"input spine not started: port {Port} is in use ({ex.SocketErrorCode}); Input Studio may still own it");
            _listener = null;
            return false;
        }
        _ = Task.Run(AcceptLoopAsync);
        CursorCrashGuard.Log($"input spine listening on 127.0.0.1:{Port}, Cursor Kit at {_store.Path}");
        return true;
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10_000;
                var stream = client.GetStream();
                var request = await HttpRequestReader.ReadAsync(stream, MaxBodyBytes);
                (int status, JsonObject reply) = request == null
                    ? (400, Error("bad request"))
                    : await HandleAsync(request.Method, request.Path, request.Headers, request.Body);
                byte[] payload = Encoding.UTF8.GetBytes(reply.ToJsonString());
                string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
            catch (Exception ex)
            {
                CursorCrashGuard.Log($"input spine connection: {ex.Message}");
            }
        }
    }
}

/// <summary>Applies the shell rules for one request: shell actions on, a valid token, and a log line for every run.</summary>
internal sealed class GatedRunner(ISpineRunner inner, ShellGate gate, bool tokenOk, string source) : ISpineRunner
{
    /// <summary>Opening a program or script is as powerful as a shell command, so it passes the same gate.</summary>
    public async Task<SpineOutcome> InputAsync(string operation, KitAction action)
    {
        if (operation != "pc.app.open" || !CursorKit.OpenRunsCode(action.Target!)) return await inner.InputAsync(operation, action);
        if (Refusal($"open {action.Id}") is { } refused) return refused;
        var outcome = await inner.InputAsync(operation, action);
        gate.Log($"ran {action.Id} from {source} ok={outcome.Ok}: open {action.Target}");
        return outcome;
    }
    public Task<SpineOutcome> CursorAsync(string profile) => inner.CursorAsync(profile);
    public long Now() => inner.Now();

    public async Task<SpineOutcome> ShellAsync(KitAction action)
    {
        if (Refusal($"shell {action.Id}") is { } refused) return refused;
        var outcome = await inner.ShellAsync(action);
        gate.Log($"ran {action.Id} from {source} ok={outcome.Ok}: {action.Shell} {action.Command}");
        return outcome;
    }

    /// <summary>A code or terminal agent can run commands, so it passes the same gate as a shell action.</summary>
    public async Task<SpineOutcome> AgentAsync(string prompt, IReadOnlyList<string> capabilities)
    {
        if (!WindowsSpineRunner.NeedsHands(capabilities)) return await inner.AgentAsync(prompt, capabilities);
        if (Refusal("agent with code or terminal") is { } refused) return refused;
        var outcome = await inner.AgentAsync(prompt, capabilities);
        gate.Log($"ran agent [{string.Join(",", capabilities)}] from {source} ok={outcome.Ok}: {prompt}");
        return outcome;
    }

    private SpineOutcome? Refusal(string what)
    {
        if (!gate.IsOn)
        {
            gate.Log($"refused (shell actions are off) {what} from {source}");
            return SpineOutcome.Fail("Shell actions are off. Turn them on in the Holodex Cursor Mod tab.");
        }
        if (!tokenOk)
        {
            gate.Log($"refused (no valid token) {what} from {source}");
            return SpineOutcome.Fail("This request has no valid Cursor Mod token, so the action did not run.");
        }
        return null;
    }
}

/// <summary>A minimal HTTP/1.1 request reader: request line, headers and a Content-Length body. Enough for loopback JSON callers.</summary>
internal sealed record HttpRequestReader(string Method, string Path, Dictionary<string, string> Headers, string Body)
{
    public static async Task<HttpRequestReader?> ReadAsync(Stream stream, int maxBody)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count > 16_384) return null;
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') headerEnd = n;
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (length < 0 || length > maxBody) return null;
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int got = await stream.ReadAsync(body.AsMemory(read, length - read));
            if (got == 0) return null;
            read += got;
        }
        string path = first[1].Split('?')[0];
        return new HttpRequestReader(first[0].ToUpperInvariant(), path, headers, Encoding.UTF8.GetString(body));
    }
}
