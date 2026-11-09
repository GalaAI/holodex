using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Holodex;

/// <summary>
/// A Cursor Kit: the shareable file of the Holodex Cursor Mod that holds actions (shell commands, keys, typing, clicks,
/// opening, cursor sets, agent prompts, sequences) and the bindings from triggers to those actions. It keeps the Input
/// Spine contract (version 0.1.0) of GalaAI/ark <c>packages/input-spine-core</c>, so a <c>spine.json</c> saved by Input
/// Studio loads unchanged and the Stream Deck, the AHK bridge and Spells keep working.
/// </summary>
internal sealed record CursorKit(IReadOnlyList<KitAction> Actions, IReadOnlyList<KitBinding> Bindings)
{
    public const string ContractVersion = "0.1.0";
    public static CursorKit Empty { get; } = new(Array.Empty<KitAction>(), Array.Empty<KitBinding>());

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly string[] ActionKinds = { "shell", "keys", "type", "click", "open", "cursor", "agent", "sequence" };
    private static readonly string[] TriggerKinds = { "hotkey", "device", "voice", "gesture" };
    private static readonly string[] Modifiers = { "CTRL", "ALT", "SHIFT", "META" };

    /// <summary>Parses and validates a kit the way the TypeScript schema does: unknown fields and bad values are refused, defaults filled in.</summary>
    public static CursorKit Parse(JsonNode? node)
    {
        var o = node as JsonObject ?? (node == null ? new JsonObject() : throw new KitException("a Cursor Kit is a JSON object"));
        Only(o, "kit", "version", "actions", "bindings");
        if (o["version"] is JsonNode v && v.GetValueKind() != JsonValueKind.Null && Str(v, "version") != ContractVersion)
            throw new KitException($"version must be {ContractVersion}");
        var actions = Arr(o["actions"], "actions").Select((n, i) => ParseAction(n, $"actions[{i}]")).ToList();
        var bindings = Arr(o["bindings"], "bindings").Select((n, i) => ParseBinding(n, $"bindings[{i}]")).ToList();
        return new CursorKit(actions, bindings);
    }

    public static CursorKit Parse(string json) => Parse(JsonNode.Parse(json));

    public JsonObject ToJson() => new()
    {
        ["version"] = ContractVersion,
        ["actions"] = new JsonArray(Actions.Select(a => (JsonNode)a.ToJson()).ToArray()),
        ["bindings"] = new JsonArray(Bindings.Select(b => (JsonNode)b.ToJson()).ToArray()),
    };

    /// <summary>Every shell command in the kit, for the import check and the save check.</summary>
    public IEnumerable<string> ShellCommands => Actions.Where(a => a.Kind == "shell").Select(a => a.Command!);

    private static KitAction ParseAction(JsonNode? node, string at)
    {
        var o = node as JsonObject ?? throw new KitException($"{at} must be an object");
        string kind = Str(o["kind"], $"{at}.kind");
        if (!ActionKinds.Contains(kind)) throw new KitException($"{at}.kind \"{kind}\" is not an action kind");
        var a = new KitAction
        {
            Id = Id(o["id"], $"{at}.id"),
            Name = Text(o["name"], $"{at}.name", 1, 120),
            Description = OptText(o["description"], $"{at}.description", 400),
            Kind = kind,
        };
        switch (kind)
        {
            case "shell":
                Only(o, at, "id", "name", "description", "kind", "command", "shell", "cwd", "timeoutMs");
                a.Command = Text(o["command"], $"{at}.command", 1, 8_000);
                a.Shell = OptEnum(o["shell"], $"{at}.shell", "powershell", "bash") ?? "powershell";
                a.Cwd = OptText(o["cwd"], $"{at}.cwd", 400);
                a.TimeoutMs = OptInt(o["timeoutMs"], $"{at}.timeoutMs", 100, 600_000) ?? 30_000;
                break;
            case "keys":
                Only(o, at, "id", "name", "description", "kind", "key", "modifiers");
                a.Key = Text(o["key"], $"{at}.key", 1, 32);
                a.Modifiers = Arr(o["modifiers"], $"{at}.modifiers").Select(m => Str(m, $"{at}.modifiers")).ToList();
                if (a.Modifiers.Count > 3 || a.Modifiers.Any(m => !Modifiers.Contains(m)))
                    throw new KitException($"{at}.modifiers takes up to 3 of CTRL, ALT, SHIFT, META");
                break;
            case "type":
                Only(o, at, "id", "name", "description", "kind", "text");
                a.Text = Str(o["text"], $"{at}.text");
                if (a.Text.Length is < 1 or > 4_000) throw new KitException($"{at}.text must be 1 to 4000 characters");
                break;
            case "click":
                Only(o, at, "id", "name", "description", "kind", "x", "y", "button", "clickCount");
                a.X = Int(o["x"], $"{at}.x");
                a.Y = Int(o["y"], $"{at}.y");
                a.Button = OptEnum(o["button"], $"{at}.button", "left", "right", "middle") ?? "left";
                a.ClickCount = OptInt(o["clickCount"], $"{at}.clickCount", 1, 2) ?? 1;
                break;
            case "open":
                Only(o, at, "id", "name", "description", "kind", "target");
                a.Target = Text(o["target"], $"{at}.target", 1, 1_000);
                break;
            case "cursor":
                Only(o, at, "id", "name", "description", "kind", "profile");
                a.Profile = Text(o["profile"], $"{at}.profile", 1, 96);
                break;
            case "agent":
                Only(o, at, "id", "name", "description", "kind", "prompt", "capabilities");
                a.Prompt = Text(o["prompt"], $"{at}.prompt", 1, 8_000);
                a.Capabilities = o["capabilities"] == null ? new List<string> { "read" }
                    : Arr(o["capabilities"], $"{at}.capabilities").Select(c => Str(c, $"{at}.capabilities")).ToList();
                if (a.Capabilities.Count == 0 || a.Capabilities.Any(c => c is not ("code" or "terminal" or "read")))
                    throw new KitException($"{at}.capabilities takes one or more of code, terminal, read");
                break;
            case "sequence":
                Only(o, at, "id", "name", "description", "kind", "steps", "stopOnError");
                a.Steps = Arr(o["steps"], $"{at}.steps").Select((s, i) => Id(s, $"{at}.steps[{i}]")).ToList();
                if (a.Steps.Count is < 1 or > 50) throw new KitException($"{at}.steps takes 1 to 50 action ids");
                a.StopOnError = o["stopOnError"] is JsonNode b ? Bool(b, $"{at}.stopOnError") : true;
                break;
        }
        return a;
    }

    private static KitBinding ParseBinding(JsonNode? node, string at)
    {
        var o = node as JsonObject ?? throw new KitException($"{at} must be an object");
        Only(o, at, "id", "trigger", "actionId", "profile", "enabled");
        var t = o["trigger"] as JsonObject ?? throw new KitException($"{at}.trigger must be an object");
        Only(t, $"{at}.trigger", "kind", "match");
        string kind = Str(t["kind"], $"{at}.trigger.kind");
        if (!TriggerKinds.Contains(kind)) throw new KitException($"{at}.trigger.kind \"{kind}\" is not a trigger kind");
        return new KitBinding(
            Id(o["id"], $"{at}.id"),
            new KitTrigger(kind, Text(t["match"], $"{at}.trigger.match", 1, 200)),
            Id(o["actionId"], $"{at}.actionId"),
            OptText(o["profile"], $"{at}.profile", 120),
            o["enabled"] is JsonNode e ? Bool(e, $"{at}.enabled") : true);
    }

    internal static KitTrigger ParseTrigger(JsonNode? node, string at)
    {
        var t = node as JsonObject ?? throw new KitException($"{at} must be an object");
        Only(t, at, "kind", "match");
        string kind = Str(t["kind"], $"{at}.kind");
        if (!TriggerKinds.Contains(kind)) throw new KitException($"{at}.kind \"{kind}\" is not a trigger kind");
        return new KitTrigger(kind, Text(t["match"], $"{at}.match", 1, 200));
    }

    // ---- small validators, named for the zod rules they mirror ----

    internal static void Only(JsonObject o, string at, params string[] allowed)
    {
        foreach (var key in o.Select(p => p.Key))
            if (!allowed.Contains(key)) throw new KitException($"{at} has an unknown field \"{key}\"");
    }

    private static IEnumerable<JsonNode?> Arr(JsonNode? n, string at) =>
        n == null || n.GetValueKind() == JsonValueKind.Null ? Array.Empty<JsonNode?>()
        : n as JsonArray ?? throw new KitException($"{at} must be an array");

    internal static string Str(JsonNode? n, string at) =>
        n is JsonValue v && v.TryGetValue(out string? s) ? s : throw new KitException($"{at} must be a string");

    internal static string Text(JsonNode? n, string at, int min, int max)
    {
        string s = Str(n, at).Trim();
        if (s.Length < min || s.Length > max) throw new KitException($"{at} must be {min} to {max} characters");
        return s;
    }

    internal static string? OptText(JsonNode? n, string at, int max) =>
        n == null || n.GetValueKind() == JsonValueKind.Null ? null : Text(n, at, 1, max);

    private static string Id(JsonNode? n, string at)
    {
        string s = Text(n, at, 1, 96);
        if (!IdPattern.IsMatch(s)) throw new KitException($"{at} \"{s}\" must be lowercase kebab or dotted");
        return s;
    }

    private static string? OptEnum(JsonNode? n, string at, params string[] values)
    {
        if (n == null) return null;
        string s = Str(n, at);
        return values.Contains(s) ? s : throw new KitException($"{at} must be one of {string.Join(", ", values)}");
    }

    // A number node built in code holds an int, not a double, so it is read through its JSON text, which is the same
    // for either backing.
    private static int Int(JsonNode? n, string at) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
            && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue
            ? (int)d : throw new KitException($"{at} must be a whole number");

    private static int? OptInt(JsonNode? n, string at, int min, int max)
    {
        if (n == null) return null;
        int i = Int(n, at);
        return i >= min && i <= max ? i : throw new KitException($"{at} must be between {min} and {max}");
    }

    private static bool Bool(JsonNode n, string at) =>
        n is JsonValue v && v.TryGetValue(out bool b) ? b : throw new KitException($"{at} must be true or false");
}

/// <summary>One action of a Cursor Kit. Only the fields of its kind are set.</summary>
internal sealed class KitAction
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Kind { get; init; } = "";
    public string? Command { get; set; }
    public string Shell { get; set; } = "powershell";
    public string? Cwd { get; set; }
    public int TimeoutMs { get; set; } = 30_000;
    public string? Key { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public string? Text { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Button { get; set; } = "left";
    public int ClickCount { get; set; } = 1;
    public string? Target { get; set; }
    public string? Profile { get; set; }
    public string? Prompt { get; set; }
    public List<string> Capabilities { get; set; } = new() { "read" };
    public List<string> Steps { get; set; } = new();
    public bool StopOnError { get; set; } = true;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name };
        if (Description != null) o["description"] = Description;
        o["kind"] = Kind;
        switch (Kind)
        {
            case "shell":
                o["command"] = Command; o["shell"] = Shell; if (Cwd != null) o["cwd"] = Cwd; o["timeoutMs"] = TimeoutMs; break;
            case "keys": o["key"] = Key; o["modifiers"] = new JsonArray(Modifiers.Select(m => (JsonNode)m).ToArray()); break;
            case "type": o["text"] = Text; break;
            case "click": o["x"] = X; o["y"] = Y; o["button"] = Button; o["clickCount"] = ClickCount; break;
            case "open": o["target"] = Target; break;
            case "cursor": o["profile"] = Profile; break;
            case "agent": o["prompt"] = Prompt; o["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode)c).ToArray()); break;
            case "sequence": o["steps"] = new JsonArray(Steps.Select(s => (JsonNode)s).ToArray()); o["stopOnError"] = StopOnError; break;
        }
        return o;
    }
}

internal sealed record KitTrigger(string Kind, string Match)
{
    public JsonObject ToJson() => new() { ["kind"] = Kind, ["match"] = Match };
}

internal sealed record KitBinding(string Id, KitTrigger Trigger, string ActionId, string? Profile, bool Enabled)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["trigger"] = Trigger.ToJson(), ["actionId"] = ActionId };
        if (Profile != null) o["profile"] = Profile;
        o["enabled"] = Enabled;
        return o;
    }
}

/// <summary>A Cursor Kit or spine event that does not match the contract. The message names the field.</summary>
internal sealed class KitException(string message) : Exception(message);
