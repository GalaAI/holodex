using System.Text;
using System.Text.Json;

namespace Omniwand;

/// <summary>
/// The 4GE Seed "later" path on the Holodex side. When the user picks "build later", the 4GE server drops the Seed
/// Scroll (its seedSchema JSON) in ainbox-queue\4ge (@4ge/contracts ainboxSeedDropDir). Holodex turns each drop into
/// AInbox's own item, a sealed Handoff Scroll with a reminder to time-block the build, through the same
/// <see cref="HandoffSeal"/> and <see cref="OutboundQueue"/> AInbox's Send uses: queued for the AInbox service on an
/// Ark, saved in Documents\Holodex\Scrolls on a PC without one. The drop then moves to 4ge\adopted.
/// </summary>
internal sealed class ForgeSeedInbox : IDisposable
{
    public const string IntentId = "4ge.seed";
    public const string IntentLabel = "Build later in 4GE";
    /// <summary>The pod a Seed lands in: Forge on an Ark ("4GE"), Automations on a public install.</summary>
    public const string PodId = "forge";

    private readonly Func<StackHandoff, Task<string>> _deliver;
    private readonly string _queueRoot;
    private readonly SynchronizationContext? _ui;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private System.Threading.Timer? _debounce;

    /// <summary>Raised after a Seed is adopted: the handoff and where it went (a saved file, or the AInbox queue).</summary>
    public event Action<StackHandoff, string>? Adopted;

    /// <param name="deliver">Sends one handoff and says where it went; <see cref="DeliverTo"/> is AInbox's own.</param>
    public ForgeSeedInbox(Func<StackHandoff, Task<string>> deliver, string? queueRoot = null, SynchronizationContext? ui = null)
    {
        _deliver = deliver;
        _queueRoot = queueRoot ?? DefaultQueueRoot;
        _ui = ui;
    }

    /// <summary>
    /// AInbox's own send, as its Send button does it: saved in Documents\Holodex\Scrolls on a PC without the AInbox
    /// service, else queued for it (and retried until it accepts).
    /// </summary>
    public static Func<StackHandoff, Task<string>> DeliverTo(OutboundQueue queue) => async h =>
    {
        if (queue.LocalOnly) return await queue.SaveLocallyAsync(h);
        var sent = await queue.EnqueueAsync(h);
        return sent.Items.All(i => i.AInboxItemId != null) ? "AInbox" : "the AInbox queue (it retries until AInbox accepts it)";
    };

    public static string DefaultQueueRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "ainbox-queue");

    public string DropDir => Path.Combine(_queueRoot, "4ge");
    public string AdoptedDir => Path.Combine(DropDir, "adopted");

    /// <summary>Watches the drop folder and adopts what is already in it.</summary>
    public void Start()
    {
        Directory.CreateDirectory(DropDir);
        var w = new FileSystemWatcher(DropDir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, IncludeSubdirectories = false };
        w.Created += (_, _) => Schedule();
        w.Changed += (_, _) => Schedule();
        w.Renamed += (_, _) => Schedule();
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
        Schedule();
    }

    // A drop is written in one go but may still be open when the first event fires; adopt a moment later.
    private void Schedule()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ =>
        {
            if (_ui != null) _ui.Post(async _ => await AdoptAllAsync(), null);
            else _ = AdoptAllAsync();
        }, null, 400, Timeout.Infinite);
    }

    /// <summary>Adopts every Seed in the drop folder; returns what each became. A file that is not a Seed is left alone.</summary>
    public async Task<IReadOnlyList<(StackHandoff Handoff, string Where)>> AdoptAllAsync()
    {
        var done = new List<(StackHandoff, string)>();
        if (!await _gate.WaitAsync(0)) return done;
        try
        {
            if (!Directory.Exists(DropDir)) return done;
            foreach (string file in Directory.GetFiles(DropDir, "*.json").OrderBy(File.GetCreationTimeUtc))
            {
                string text;
                try { text = await File.ReadAllTextAsync(file); }
                catch (IOException) { continue; }
                StackHandoff? h;
                try { h = FromSeed(text, DateTime.UtcNow); }
                catch (JsonException) { continue; }
                if (h == null) continue;

                string where = await _deliver(h);
                Directory.CreateDirectory(AdoptedDir);
                try { File.Move(file, Path.Combine(AdoptedDir, Path.GetFileName(file)), overwrite: true); }
                catch (IOException) { }
                done.Add((h, where));
                Adopted?.Invoke(h, where);
            }
        }
        finally { _gate.Release(); }
        return done;
    }

    /// <summary>
    /// A Seed (seedSchema JSON) as AInbox's own item: a Handoff Scroll to the Forge pod whose ask is the reminder to
    /// time-block the build, the Seed's words as its fields, and the Seed itself as its one capture, sealed by
    /// <see cref="HandoffSeal.Seal"/>. Null when the JSON is not a Seed (no seed_ id or no idea).
    /// </summary>
    internal static StackHandoff? FromSeed(string seedJson, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(seedJson);
        var s = doc.RootElement;
        if (s.ValueKind != JsonValueKind.Object) return null;
        string id = Str(s, "id");
        string idea = Str(s, "idea");
        if (!id.StartsWith("seed_", StringComparison.Ordinal) || idea.Length == 0) return null;

        string reminder = Str(s, "reminderNote");
        string ask = "Time-block a session to build this in 4GE: open 4GE, pick this Seed and choose Build now."
            + (reminder.Length > 0 ? " " + reminder : "");
        string platforms = s.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join(", ", p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
            : "";
        var fields = new List<ScrollField>();
        void Field(string label, string value) { if (value.Length > 0) fields.Add(new ScrollField(label, value, "4GE seed")); }
        Field("Idea", idea);
        Field("Problem", Str(s, "problem"));
        Field("Audience", Str(s, "audience"));
        Field("Success", Str(s, "successSignal"));
        Field("Kind", Str(s, "kind"));
        Field("Platforms", platforms);
        Field("Blueprint", Str(s, "blueprintId"));
        Field("Reminder", reminder.Length > 0 ? reminder : "Time-block it on your calendar.");

        byte[] seedBytes = Encoding.UTF8.GetBytes(seedJson);
        string seedSha = ArkStationClient.Sha256Hex(seedBytes);
        string seal = HandoffSeal.Seal(PodId, IntentId, ask, fields, new[] { seedSha });
        string title = idea.Length > 60 ? idea[..60].TrimEnd() + "..." : idea;
        var items = new List<QueuedItem>();
        var h = new StackHandoff($"scroll:4ge-seed:{id}", "S", $"4GE seed // {title}", PodId, IntentId, IntentLabel, ask,
            fields, items, seal, nowUtc.ToString("O"));
        var pod = ArkStationClient.PodInboxes.FirstOrDefault(x => x.PodId == PodId) ?? ArkStationClient.PodInboxes[0];
        var origin = new Dictionary<string, string> { ["app"] = "4GE", ["seed"] = id };
        items.Add(new QueuedItem("scroll", "handoff-scroll.md", "Scroll", "text", "text/markdown", null, HandoffSeal.Markdown(h, pod), null, null, origin));
        items.Add(new QueuedItem("seed", $"{id}.json", "Seed", "text", "application/json", null, seedJson, null, seedSha, origin));
        return h;
    }

    /// <summary>The toast after an adoption: the idea, where it went, and the reminder to time-block it.</summary>
    internal static string AdoptedText(StackHandoff h, string where)
    {
        string idea = h.Fields.FirstOrDefault(f => f.Label == "Idea")?.Value ?? h.GroupLabel;
        if (idea.Length > 80) idea = idea[..80].TrimEnd() + "...";
        string place = where.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? $"Documents\\Holodex\\Scrolls ({Path.GetFileName(where)})"
            : where;
        return $"\"{idea}\" is in {place}. Time-block a session to build it in 4GE.";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce?.Dispose();
    }
}
