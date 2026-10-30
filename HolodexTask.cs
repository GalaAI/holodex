using System;
using System.Collections.Generic;
using System.Linq;

namespace Omniwand;

/// <summary>
/// What a task asks for. The router picks a model by kind, and an agent says which kinds it takes.
/// Every hand-off in Holodex (a Spell, an Oracle request, an agent passing work on) names one kind.
/// </summary>
public enum TaskKind
{
    /// <summary>Plain conversation; the fast tier is enough.</summary>
    Chat,
    /// <summary>Planning, orchestration and multi-step thinking.</summary>
    Reason,
    /// <summary>Anything with an image attached.</summary>
    Vision,
    /// <summary>Find and box the objects in an image (a YOLO-class detector).</summary>
    Detect,
    /// <summary>Cut one object out of an image from a point or box (a SAM-class segmenter).</summary>
    Segment,
    /// <summary>Look things up on the web or in the Codex and gather context.</summary>
    Search,
    /// <summary>Shorten material into a digest.</summary>
    Summarize,
    /// <summary>Write or repair code and scripts.</summary>
    Code,
    /// <summary>Craft a prompt (a Wiz potion).</summary>
    Craft,
    /// <summary>Write something for an audience, such as a newsletter.</summary>
    Publish,
    /// <summary>Guided learning in a subject.</summary>
    Teach
}

/// <summary>
/// One unit of work handed from one agent (or the user) to another. The same envelope travels everywhere, so a new
/// agent or a new connector only has to read and answer it. <see cref="ParentId"/> chains a hand-off to the task
/// that caused it; <see cref="Inputs"/> holds plain strings (text, a data URL, a file path, a JSON blob).
/// </summary>
public sealed record HolodexTask
{
    public string Id { get; init; } = NewId();
    public string? ParentId { get; init; }
    public TaskKind Kind { get; init; }
    /// <summary>Who asked: <c>user</c> or an agent id.</summary>
    public string From { get; init; } = "user";
    /// <summary>Who should do it. Empty lets <see cref="HolodexAgents.ResolveFor"/> pick by kind.</summary>
    public string To { get; init; } = string.Empty;
    /// <summary>One line on what is wanted, in the asker's words.</summary>
    public string Intent { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();
    /// <summary>Optional skill or subject the work counts toward (feeds the Stats page).</summary>
    public string? Skill { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>A follow-on task that keeps this one as its parent.</summary>
    public HolodexTask HandOff(string toAgent, TaskKind kind, string intent, IReadOnlyDictionary<string, string>? inputs = null) =>
        new()
        {
            ParentId = Id,
            Kind = kind,
            From = string.IsNullOrEmpty(To) ? "user" : To,
            To = toAgent,
            Intent = intent,
            Inputs = inputs ?? Inputs,
            Skill = Skill
        };

    public static string NewId() => "task_" + Guid.NewGuid().ToString("N")[..12];
}

/// <summary>What came back: who did it, on which provider and model, and the output or the reason it failed.</summary>
public sealed record HolodexTaskResult(
    string TaskId,
    bool Ok,
    string AgentId,
    string ProviderId,
    string ModelId,
    string Output,
    DateTimeOffset FinishedAt)
{
    public static HolodexTaskResult Failed(HolodexTask task, string agentId, string reason) =>
        new(task.Id, false, agentId, string.Empty, string.Empty, reason, DateTimeOffset.UtcNow);
}

/// <summary>An agent and the task kinds it accepts.</summary>
public sealed record HolodexAgent(string Id, string Label, string Role, IReadOnlyList<TaskKind> Accepts);

/// <summary>
/// The Holodex agent roster for hand-offs. Gala is the default orchestrator and takes anything no one else claims.
/// </summary>
public static class HolodexAgents
{
    public static readonly HolodexAgent Gala = new("gala", "Gala", "Orchestrator",
        new[] { TaskKind.Reason, TaskKind.Chat, TaskKind.Vision, TaskKind.Detect, TaskKind.Segment, TaskKind.Search, TaskKind.Summarize });
    public static readonly HolodexAgent Wiz = new("wiz", "Wiz", "Spell and prompt crafting",
        new[] { TaskKind.Craft, TaskKind.Code });
    public static readonly HolodexAgent Herald = new("herald", "Herald", "Distribution and newsletters",
        new[] { TaskKind.Publish });
    public static readonly HolodexAgent Tutor = new("tutor", "Tutor", "Guided learning",
        new[] { TaskKind.Teach });
    public static readonly HolodexAgent Oracle = new("oracle", "Oracle", "Library questions and dispatch",
        new[] { TaskKind.Search, TaskKind.Summarize });

    public static readonly IReadOnlyList<HolodexAgent> All = new[] { Gala, Wiz, Herald, Tutor, Oracle };

    public static HolodexAgent? Find(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The named agent when it exists, else the first specialist for the kind, else Gala.</summary>
    public static HolodexAgent ResolveFor(HolodexTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.To) && Find(task.To) is { } named) return named;
        return All.Where(a => a != Gala).FirstOrDefault(a => a.Accepts.Contains(task.Kind)) ?? Gala;
    }
}
