using System.Text.Json;
using System.Text.Json.Serialization;

namespace Holodex;

/// <summary>
/// The public mode's missions log and checklist: one small JSON file per day under the signed-in Windows user's
/// Holodex data folder (horizon\YYYY-MM-DD.json). Nothing here leaves the PC and nothing here is a secret. Writes go
/// to a temporary file and replace the day's file in one step, so a crash mid-write leaves the last good day.
/// Ark mode never touches this store.
/// </summary>
internal sealed class HorizonLocalStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public HorizonLocalStore(string? root = null, Func<DateTimeOffset>? now = null)
    {
        _root = root ?? Path.Combine(HolodexNames.DataRoot, "horizon");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The file one day lives in.</summary>
    public string PathFor(DateOnly day) => Path.Combine(_root, Horizon.DayKey(day) + ".json");

    public IReadOnlyList<HorizonLogEntry> ReadLog(DateOnly day)
    {
        lock (_gate) return Load(day).Log.Select(e => new HorizonLogEntry(e.Id, e.At, e.Text)).ToList();
    }

    public IReadOnlyList<HorizonCheckItem> ReadChecklist(DateOnly day)
    {
        lock (_gate) return Load(day).Checklist.Select(c => new HorizonCheckItem(c.Id, c.Text, c.Done)).ToList();
    }

    /// <summary>Adds a log line stamped now. Empty text adds nothing.</summary>
    public IReadOnlyList<HorizonLogEntry> AppendLog(DateOnly day, string text)
    {
        string clean = Horizon.CleanText(text);
        lock (_gate)
        {
            var file = Load(day);
            if (clean.Length > 0)
            {
                file.Log.Add(new LogRow { Id = NewId(), At = _now(), Text = clean });
                Save(day, file);
            }
            return file.Log.Select(e => new HorizonLogEntry(e.Id, e.At, e.Text)).ToList();
        }
    }

    /// <summary>Adds an unchecked item. Empty text adds nothing.</summary>
    public IReadOnlyList<HorizonCheckItem> AddCheckItem(DateOnly day, string text)
    {
        string clean = Horizon.CleanText(text);
        return Change(day, file =>
        {
            if (clean.Length == 0) return false;
            file.Checklist.Add(new CheckRow { Id = NewId(), Text = clean });
            return true;
        });
    }

    public IReadOnlyList<HorizonCheckItem> SetCheckItem(DateOnly day, string id, bool done) => Change(day, file =>
    {
        var row = file.Checklist.FirstOrDefault(c => c.Id == id);
        if (row == null || row.Done == done) return false;
        row.Done = done;
        return true;
    });

    public IReadOnlyList<HorizonCheckItem> RemoveCheckItem(DateOnly day, string id) =>
        Change(day, file => file.Checklist.RemoveAll(c => c.Id == id) > 0);

    private IReadOnlyList<HorizonCheckItem> Change(DateOnly day, Func<DayFile, bool> change)
    {
        lock (_gate)
        {
            var file = Load(day);
            if (change(file)) Save(day, file);
            return file.Checklist.Select(c => new HorizonCheckItem(c.Id, c.Text, c.Done)).ToList();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// A day that was never written, or whose file cannot be read as a day, is an empty day. An unreadable file is
    /// set aside beside itself (.unreadable) on the next write instead of being overwritten, so nothing is lost.
    /// </summary>
    private DayFile Load(DateOnly day)
    {
        string path = PathFor(day);
        if (!File.Exists(path)) return new DayFile();
        try
        {
            var file = JsonSerializer.Deserialize<DayFile>(File.ReadAllText(path), Json);
            if (file != null)
            {
                file.Log.RemoveAll(e => string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Text));
                file.Checklist.RemoveAll(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Text));
                return file;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Falls through to the empty day; Save keeps the unreadable file.
        }
        return new DayFile { Unreadable = true };
    }

    private void Save(DateOnly day, DayFile file)
    {
        Directory.CreateDirectory(_root);
        string path = PathFor(day);
        if (file.Unreadable && File.Exists(path))
        {
            File.Move(path, path + ".unreadable", overwrite: true);
            file.Unreadable = false;
        }
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, path, overwrite: true);
    }

    private sealed class DayFile
    {
        public int Version { get; set; } = 1;
        public List<LogRow> Log { get; set; } = new();
        public List<CheckRow> Checklist { get; set; } = new();
        [JsonIgnore] public bool Unreadable { get; set; }
    }

    private sealed class LogRow
    {
        public string Id { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class CheckRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }
}
