using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

/// <summary>
/// What Wiz can reach outside its own widget. Program.cs fills these the way the HUD gets openWizCrafting: each
/// delegate opens or writes through the widget that owns the behavior (snip, AInbox, Holodex), so Wiz never
/// reproduces it.
/// </summary>
public sealed class WizAgentHost
{
    /// <summary>Opens the snip form's free-form lasso at a point.</summary>
    public Action<Point>? OpenLasso { get; init; }

    /// <summary>Opens the snip form's element picker at a point.</summary>
    public Action<Point>? OpenElementPicker { get; init; }

    /// <summary>Opens AInbox at a point with the ask typed into its composer (nothing is sent).</summary>
    public Action<Point, string>? OpenAInboxWithAsk { get; init; }

    /// <summary>Queues a Holodex work order (agent, task); returns the order id, or null with the reason.</summary>
    public Func<string, string, (string? OrderId, string Error)>? QueueWorkOrder { get; init; }

    /// <summary>Raised after Wiz writes a Codex entry, so Holodex can refresh its Codex tab.</summary>
    public Action<CodexCaptureEntry>? CodexSaved { get; init; }
}

/// <summary>The Wiz thread as the agent writes to it: every model turn, action, result and approval shows there.</summary>
internal interface IWizThread
{
    void Vision(string model, string description);
    void Step(int step, string model, string reasoning);
    void Action(string call);
    void Result(string text, bool ok);
    Task<bool> ApproveAsync(string call, string effect, CancellationToken ct);
}

internal enum WizToolEffect
{
    /// <summary>Reads only. Runs directly.</summary>
    ReadOnly,
    /// <summary>Opens a widget for the operator; nothing is saved. Runs directly.</summary>
    OpensWidget,
    /// <summary>Changes saved state. Runs only after the operator approves it in the thread.</summary>
    Persistent
}

internal sealed record WizTool(string Name, string Description, WizToolEffect Effect, string Approval, (string Name, string Description, string[]? Enum)[] Args);

internal sealed record WizAgentOutcome(bool Ok, string Model, string Text, int Steps);

/// <summary>
/// The Wiz agent loop. NVIDIA Nemotron 3 Super on Nebius Token Factory reasons over native OpenAI-style tool calls
/// (verified live 2026-10-26: <c>finish_reason: tool_calls</c>), for at most <see cref="MaxSteps"/> model turns
/// inside <see cref="RunTimeout"/>. When the screen is attached, Nemotron 3.5 Super VL describes it first and the
/// description goes to the reasoner. Tools that change saved state wait for the operator's Approve or Skip.
/// </summary>
internal sealed class WizAgent
{
    public const string DefaultReasoningModel = ArkStationClient.DefaultNemotronSuperModel;
    public const string VisionModel = "nvidia/Nemotron-3.5-Super-VL";
    public const int MaxSteps = 6;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(3);
    private const int MaxResultChars = 2000;

    public static readonly WizTool[] Tools =
    {
        new("capture_screen",
            "Capture the screen around the wand tip and get a written description of it from a vision model.",
            WizToolEffect.ReadOnly, "", Array.Empty<(string, string, string[]?)>()),
        new("open_lasso",
            "Open the free-form lasso at the wand tip so the operator can draw around a region and annotate it. The capture stays with the operator.",
            WizToolEffect.OpensWidget, "", Array.Empty<(string, string, string[]?)>()),
        new("open_element_picker",
            "Open the element and palette picker at the wand tip so the operator can pick a UI element. The pick stays with the operator.",
            WizToolEffect.OpensWidget, "", Array.Empty<(string, string, string[]?)>()),
        new("draft_ainbox_handoff",
            "Open AInbox with an ask typed into its composer, ready for the operator to review and send to a pod. Nothing is sent.",
            WizToolEffect.OpensWidget, "",
            new[] { ("ask", "The handoff ask, written as the operator would send it.", (string[]?)null) }),
        new("save_to_codex",
            "Save a note to the Holodex Codex (title and summary). Needs the operator's approval.",
            WizToolEffect.Persistent, "writes a Codex entry",
            new[] { ("title", "Short title for the Codex entry.", (string[]?)null), ("summary", "What to keep: the Spell, finding or plan.", (string[]?)null) }),
        new("queue_work_order",
            "Queue a Holodex work order for a Syndicate agent. Needs the operator's approval.",
            WizToolEffect.Persistent, "queues a Holodex work order",
            new[] { ("agent", "Syndicate agent id from the Holodex roster (Hivemind's live agents once signed in, for example gala, wiz, smith).", (string[]?)null), ("task", "The work order, one or two sentences.", (string[]?)null) }),
        new("list_spell_slots",
            "List the eight Spell Orb slots: number, title, verb and what each does.",
            WizToolEffect.ReadOnly, "", Array.Empty<(string, string, string[]?)>())
    };

    private readonly ArkStationClient _ark;
    private readonly WizAgentHost _host;

    public WizAgent(ArkStationClient ark, WizAgentHost host)
    {
        _ark = ark;
        _host = host;
    }

    public static bool CanAct(string model) => ArkStationClient.IsNemotronModel(model);

    public async Task<WizAgentOutcome> RunAsync(
        string goal, string spellTypeId, string model, Point wandTip, bool attachScreen, IWizThread thread, CancellationToken cancel)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        run.CancelAfter(RunTimeout);
        var ct = run.Token;
        int steps = 0;
        try
        {
            var user = new StringBuilder();
            user.AppendLine($"Goal: {goal}");
            user.AppendLine($"Spell type: {ArkStationClient.SpellTypeName(spellTypeId)}");
            if (attachScreen)
            {
                string described = await DescribeScreenAsync(wandTip, thread, ct);
                user.AppendLine();
                user.AppendLine($"Screen around the wand tip, described by {ModelChip.ShortName(VisionModel)}:");
                user.AppendLine(described);
            }

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(spellTypeId) },
                new JsonObject { ["role"] = "user", ["content"] = user.ToString().Trim() }
            };
            var tools = ToolSchemas();

            for (int step = 1; step <= MaxSteps; step++)
            {
                steps = step;
                bool last = step == MaxSteps;
                var (ok, message, error) = await _ark.ChatNebiusAsync(messages, tools, model, last ? "none" : "auto", ct);
                if (!ok || message == null) return new WizAgentOutcome(false, model, error, steps);

                var calls = message["tool_calls"] as JsonArray;
                if (last || calls is not { Count: > 0 })
                {
                    string final = FinalText(message);
                    if (final.Length == 0)
                    {
                        final = last
                            ? $"Wiz used all {MaxSteps} steps without writing a final draft. Narrow the goal and send it again."
                            : "Nemotron returned an empty answer.";
                    }
                    return new WizAgentOutcome(final.Length > 0, model, final, steps);
                }

                thread.Step(step, model, ReasonLine(message));
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = Text(message, "content") is { Length: > 0 } said ? said : null,
                    ["tool_calls"] = calls.DeepClone()
                });

                foreach (var node in calls)
                {
                    string id = node?["id"]?.GetValue<string>() ?? $"call_{step}";
                    string name = node?["function"]?["name"]?.GetValue<string>() ?? "";
                    string rawArgs = node?["function"]?["arguments"] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : node?["function"]?["arguments"]?.ToJsonString() ?? "";
                    string result = await ExecuteAsync(name, rawArgs, model, wandTip, thread, ct);
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = Clip(result, MaxResultChars) });
                }
            }
            return new WizAgentOutcome(false, model, "Wiz stopped without a final draft.", steps);
        }
        catch (OperationCanceledException)
        {
            return new WizAgentOutcome(false, model, cancel.IsCancellationRequested
                ? "Stopped by the operator."
                : $"Wiz hit its {RunTimeout.TotalMinutes:0}-minute limit and stopped.", steps);
        }
    }

    private static string SystemPrompt(string spellTypeId) =>
        "You are Wiz, the 4GE Workflow-Crafting and GrimX Catalog-Governance agent inside Omniwand, running on NVIDIA Nemotron through Nebius Token Factory. " +
        "You turn the operator's goal into a GrimX Spell and act through your tools when acting moves the goal forward. " +
        "Spell kinds: Quick Cast (Cast), Combo (Chain), Ritual (Perform, scheduled or long-running), Symphony (Conduct), Ultima (Pursue), Charm (Bind), Summon (Summon). " +
        $"Active Spell type: {ArkStationClient.SpellTypeName(spellTypeId)}.\n\n" +
        "Rules:\n" +
        "- Before each tool call, write one short sentence saying why you are calling it.\n" +
        $"- You have at most {MaxSteps} turns. Call a tool only when its result changes what you will draft; never call one just to show it.\n" +
        "- save_to_codex and queue_work_order change saved state. The operator approves or skips each one; if skipped, carry on without it.\n" +
        "- Opening the lasso, element picker or AInbox hands control to the operator; their captures do not come back to you.\n" +
        "- Never say an action happened unless its tool result says it did.\n" +
        "- Finish with the Spell draft in plain text: a title line, numbered steps, then one line on what you did with tools, if anything. No tables.";

    private static JsonArray ToolSchemas()
    {
        var list = new JsonArray();
        foreach (var t in Tools)
        {
            var props = new JsonObject();
            foreach (var a in t.Args)
            {
                var p = new JsonObject { ["type"] = "string", ["description"] = a.Description };
                if (a.Enum != null) p["enum"] = new JsonArray(a.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                props[a.Name] = p;
            }
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = new JsonArray(t.Args.Select(a => (JsonNode?)JsonValue.Create(a.Name)).ToArray())
                    }
                }
            });
        }
        return list;
    }

    /// <summary>Validates the call against its schema, shows it, asks for approval when it writes, then runs it.</summary>
    private async Task<string> ExecuteAsync(string name, string rawArgs, string model, Point wandTip, IWizThread thread, CancellationToken ct)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        string? invalid = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(rawArgs) && JsonNode.Parse(rawArgs) is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    args[kv.Key] = kv.Value is JsonValue jv && jv.TryGetValue(out string? str) ? str ?? "" : kv.Value?.ToJsonString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            invalid = "the arguments were not valid JSON";
        }

        thread.Action(CallText(name, args));

        if (tool == null) invalid = $"there is no tool named '{name}'";
        if (invalid == null && tool != null)
        {
            foreach (var a in tool.Args)
            {
                if (!args.TryGetValue(a.Name, out var val) || string.IsNullOrWhiteSpace(val)) { invalid = $"'{a.Name}' is required"; break; }
                if (a.Enum != null && !a.Enum.Contains(val.Trim(), StringComparer.OrdinalIgnoreCase)) { invalid = $"'{a.Name}' must be one of {string.Join(", ", a.Enum)}"; break; }
            }
        }
        if (invalid != null)
        {
            string error = $"Error: {invalid}. Nothing was done.";
            thread.Result(error, ok: false);
            return error;
        }

        if (tool!.Effect == WizToolEffect.Persistent && !await thread.ApproveAsync(CallText(name, args), tool.Approval, ct))
        {
            const string skipped = "The operator skipped this action. Nothing was saved; carry on without it.";
            thread.Result(skipped, ok: false);
            return skipped;
        }

        var (ok, text) = await RunToolAsync(name, args, model, wandTip, thread, ct);
        thread.Result(text, ok);
        return text;
    }

    private async Task<(bool Ok, string Text)> RunToolAsync(string name, Dictionary<string, string> args, string model, Point wandTip, IWizThread thread, CancellationToken ct)
    {
        switch (name)
        {
            case "capture_screen":
                string described = await DescribeScreenAsync(wandTip, thread, ct);
                return (true, described);

            case "open_lasso":
                if (_host.OpenLasso == null) return (false, "The lasso is not available in this session.");
                _host.OpenLasso(wandTip);
                return (true, "Opened the free-form lasso at the wand tip. The operator draws and annotates; the capture stays with them.");

            case "open_element_picker":
                if (_host.OpenElementPicker == null) return (false, "The element picker is not available in this session.");
                _host.OpenElementPicker(wandTip);
                return (true, "Opened the element picker at the wand tip. The operator picks; the pick stays with them.");

            case "draft_ainbox_handoff":
                if (_host.OpenAInboxWithAsk == null) return (false, "AInbox is not available in this session.");
                _host.OpenAInboxWithAsk(wandTip, args["ask"]);
                return (true, "AInbox is open with the ask typed into its composer. Nothing was sent; the operator reviews and sends it.");

            case "save_to_codex":
            {
                var entry = new CodexCaptureEntry(
                    $"codex:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
                    "wiz.note",
                    args["title"].Trim(),
                    args["summary"].Trim(),
                    "Wiz",
                    "-",
                    "-",
                    model,
                    null,
                    DateTime.UtcNow.ToString("O"));
                if (!ScreenSnipAndElementPickerForm.SaveCodexEntryToDisk(entry, out string error))
                {
                    return (false, $"Codex save failed: {error}");
                }
                _host.CodexSaved?.Invoke(entry);
                return (true, $"Saved \"{entry.Title}\" to the Holodex Codex ({entry.EntryId}).");
            }

            case "queue_work_order":
            {
                if (_host.QueueWorkOrder == null) return (false, "Holodex work orders are not available in this session.");
                var (orderId, error) = _host.QueueWorkOrder(args["agent"], args["task"]);
                return orderId != null
                    ? (true, $"Queued {orderId} for {args["agent"].Trim().ToUpperInvariant()} in Holodex.")
                    : (false, $"Work order not queued: {error}");
            }

            case "list_spell_slots":
            {
                var sb = new StringBuilder();
                foreach (var s in GrimXFlowOrb.Slots)
                {
                    sb.AppendLine($"[{s.Index + 1}] {s.Title} // {s.Verb} // {s.Subtitle}");
                }
                sb.Append("Slot bindings are fixed in this build.");
                return (true, sb.ToString());
            }
        }
        return (false, $"There is no tool named '{name}'.");
    }

    /// <summary>Captures around the wand tip and has Nemotron 3.5 Super VL describe it; the thread names the model.</summary>
    private async Task<string> DescribeScreenAsync(Point wandTip, IWizThread thread, CancellationToken ct)
    {
        string? dataUrl = ArkStationClient.CaptureRegionAroundCursor(wandTip.X, wandTip.Y);
        if (dataUrl == null)
        {
            const string failed = "The screen capture failed, so there is no description.";
            thread.Vision(VisionModel, failed);
            return failed;
        }
        var (ok, output) = await _ark.AskNebiusTokenFactoryAsync(
            "Describe this screen region for an agent that will plan work from it: the app, what is open, visible text that matters, and any errors or state. Be concrete and brief.",
            dataUrl,
            VisionModel).WaitAsync(ct);
        string text = ok ? output.Trim() : $"The vision model could not describe the screen: {output}";
        thread.Vision(VisionModel, text);
        return text;
    }

    private static string CallText(string name, IReadOnlyDictionary<string, string> args) =>
        $"{name}({string.Join(", ", args.Select(kv => $"{kv.Key}=\"{Clip(kv.Value.Replace('\n', ' '), 60)}\""))})";

    /// <summary>The one line the thread shows for a turn: what the model said before calling, else its first reasoning sentence.</summary>
    private static string ReasonLine(JsonObject message)
    {
        string said = StripThink(Text(message, "content"));
        if (said.Length == 0) said = Text(message, "reasoning_content");
        said = Regex.Replace(said, @"\s+", " ").Trim();
        if (said.Length == 0) return "(no reasoning returned)";
        int end = said.IndexOfAny(new[] { '.', '!', '?' });
        if (end > 20) said = said[..(end + 1)];
        return Clip(said, 200);
    }

    private static string FinalText(JsonObject message)
    {
        string text = StripThink(Text(message, "content"));
        return text.Length > 0 ? text : StripThink(Text(message, "reasoning_content"));
    }

    private static string Text(JsonObject message, string key) =>
        message[key] is JsonValue v && v.TryGetValue(out string? s) ? s?.Trim() ?? "" : "";

    private static string StripThink(string s) => Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
