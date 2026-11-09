namespace Holodex;

/// <summary>
/// The Input Spine, ported from GalaAI/ark <c>packages/input-spine-core</c> (<c>resolve.ts</c>, <c>run.ts</c>). A
/// trigger fires, it resolves to an action of the Cursor Kit, the action runs, and the result is recorded. A mouse
/// button, a spoken phrase, a gesture and a hotkey differ only in how the trigger is described.
/// </summary>
internal static class InputSpine
{
    /// <summary>
    /// A sequence names its steps by id, so a kit edited by hand can describe a cycle. Saving refuses cycles, but the
    /// file can change on disk, so the runner also refuses to descend past this depth.
    /// </summary>
    public const int MaxSequenceDepth = 12;

    /// <summary>
    /// Triggers are typed by hand and spoken aloud, so they compare on a normalised form. Hotkeys have no inherent
    /// modifier order: CTRL+ALT+K and ALT+CTRL+K are the same chord.
    /// </summary>
    public static string NormaliseMatch(string kind, string match)
    {
        string flat = System.Text.RegularExpressions.Regex.Replace(match.Trim().ToLowerInvariant(), @"\s+", " ");
        if (kind != "hotkey") return flat;
        return string.Join("+", flat.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).OrderBy(p => p, StringComparer.Ordinal));
    }

    public static bool TriggersMatch(KitTrigger a, KitTrigger b) =>
        a.Kind == b.Kind && NormaliseMatch(a.Kind, a.Match) == NormaliseMatch(b.Kind, b.Match);

    /// <summary>
    /// The binding that handles an event. One scoped to the foreground app beats an unscoped one for the same trigger;
    /// among equals the first wins. An unbound trigger is an ordinary outcome, so this returns null, never throws.
    /// </summary>
    public static (KitBinding Binding, KitAction Action)? Resolve(CursorKit kit, KitTrigger trigger, string? profile)
    {
        string? p = profile?.Trim().ToLowerInvariant();
        var candidates = kit.Bindings.Where(b => b.Enabled && TriggersMatch(b.Trigger, trigger)).ToList();
        if (candidates.Count == 0) return null;
        var binding = (p != null ? candidates.FirstOrDefault(b => b.Profile?.Trim().ToLowerInvariant() == p) : null)
            ?? candidates.FirstOrDefault(b => b.Profile == null);
        if (binding == null) return null;
        var action = kit.Actions.FirstOrDefault(a => a.Id == binding.ActionId);
        return action == null ? null : (binding, action);
    }

    /// <summary>Ids that take part in a sequence cycle, and steps that name an action that does not exist.</summary>
    public static (List<string> Cycles, List<(string SequenceId, string MissingId)> Missing) FindSequenceProblems(CursorKit kit)
    {
        var byId = new Dictionary<string, KitAction>();
        foreach (var a in kit.Actions) byId.TryAdd(a.Id, a);
        var missing = new List<(string, string)>();
        var cycles = new HashSet<string>();
        var grey = new HashSet<string>();
        var black = new HashSet<string>();

        bool Walk(string id)
        {
            if (black.Contains(id)) return false;
            if (grey.Contains(id)) { cycles.Add(id); return true; }
            if (!byId.TryGetValue(id, out var action) || action.Kind != "sequence") { black.Add(id); return false; }
            grey.Add(id);
            bool looped = false;
            foreach (var step in action.Steps)
            {
                if (!byId.ContainsKey(step)) { missing.Add((id, step)); continue; }
                if (Walk(step)) { cycles.Add(id); looped = true; }
            }
            grey.Remove(id);
            black.Add(id);
            return looped;
        }

        foreach (var a in kit.Actions) if (a.Kind == "sequence") Walk(a.Id);
        return (cycles.ToList(), missing);
    }

    /// <summary>Runs one action. A runner that throws becomes a failed result, never an unhandled exception.</summary>
    public static async Task<SpineResult> RunActionAsync(CursorKit kit, KitAction action, ISpineRunner deps, int depth = 0)
    {
        long started = deps.Now();
        SpineResult Done(SpineOutcome r, List<SpineResult>? steps = null) =>
            new(action.Id, r.Ok, r.Output, r.Error, deps.Now() - started, steps);
        try
        {
            switch (action.Kind)
            {
                case "shell": return Done(await deps.ShellAsync(action));
                case "keys": return Done(await deps.InputAsync("pc.key.press", action));
                case "type": return Done(await deps.InputAsync("pc.text.type", action));
                case "click": return Done(await deps.InputAsync("pc.pointer.click", action));
                case "open": return Done(await deps.InputAsync("pc.app.open", action));
                case "cursor": return Done(await deps.CursorAsync(action.Profile!));
                case "agent": return Done(await deps.AgentAsync(action.Prompt!, action.Capabilities));
                case "sequence":
                {
                    if (depth >= MaxSequenceDepth)
                        return Done(SpineOutcome.Fail($"sequence nesting exceeded {MaxSequenceDepth}, which means the Cursor Kit contains a cycle"));
                    var steps = new List<SpineResult>();
                    foreach (var stepId in action.Steps)
                    {
                        var step = kit.Actions.FirstOrDefault(a => a.Id == stepId);
                        if (step == null)
                        {
                            steps.Add(new SpineResult(stepId, false, null, $"no action with id \"{stepId}\"", 0, null));
                            if (action.StopOnError) break;
                            continue;
                        }
                        var result = await RunActionAsync(kit, step, deps, depth + 1);
                        steps.Add(result);
                        if (!result.Ok && action.StopOnError) break;
                    }
                    // Every step that ran must succeed: a later success does not excuse an earlier failure.
                    return Done(new SpineOutcome(steps.All(s => s.Ok), null, null), steps);
                }
                default: return Done(SpineOutcome.Fail($"unknown action kind \"{action.Kind}\""));
            }
        }
        catch (Exception ex)
        {
            return Done(SpineOutcome.Fail(ex.Message));
        }
    }
}

/// <summary>What a runner reports. A non-zero exit is a normal result, so failures carry their output too.</summary>
internal sealed record SpineOutcome(bool Ok, string? Output, string? Error)
{
    public static SpineOutcome Success(string? output = null) => new(true, output, null);
    public static SpineOutcome Fail(string error) => new(false, null, error);
}

internal sealed record SpineResult(string ActionId, bool Ok, string? Output, string? Error, long Ms, List<SpineResult>? Steps)
{
    public System.Text.Json.Nodes.JsonObject ToJson()
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["actionId"] = ActionId, ["ok"] = Ok };
        if (Output != null) o["output"] = Output;
        if (Error != null) o["error"] = Error;
        o["ms"] = Ms;
        if (Steps != null) o["steps"] = new System.Text.Json.Nodes.JsonArray(Steps.Select(s => (System.Text.Json.Nodes.JsonNode)s.ToJson()).ToArray());
        return o;
    }
}

/// <summary>Everything the spine needs from the outside world, injected so resolution and sequencing test without a mouse or a shell.</summary>
internal interface ISpineRunner
{
    /// <summary>Real input: keys, typing, clicks and opening apps. The operation names match Ark's PC Mod operations.</summary>
    Task<SpineOutcome> InputAsync(string operation, KitAction action);
    Task<SpineOutcome> ShellAsync(KitAction action);
    Task<SpineOutcome> AgentAsync(string prompt, IReadOnlyList<string> capabilities);
    Task<SpineOutcome> CursorAsync(string profile);
    long Now();
}
