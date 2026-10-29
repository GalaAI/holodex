using System.Text.Json;

namespace Omniwand;

/// <summary>
/// Live overrides over PhosphorTheme's tokens, for tuning motion and metrics while the app runs. The overlay is a
/// flat map of token names to CSS values ("dur-disclosure": "240ms") in %LOCALAPPDATA%\Omniwand\tuning.json. A
/// hand edit is picked up on save; the pipe's holodex:tune:set merges values and holodex:tune:reset clears them.
/// Only names PhosphorTheme already knows are applied, so a typo cannot invent a token.
/// </summary>
internal static class PhosphorTuning
{
    internal static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "tuning.json");

    /// <summary>The tunable names: motion beats, the FluidSelect and tooltip metrics, the tray page turn and the accent.</summary>
    internal static readonly string[] Tunable =
    {
        "dur-disclosure", "dur-disclosure-item", "dur-bloom",
        "omniwand-tip-dwell", "omniwand-tip-gap", "omniwand-tip-shift", "omniwand-tip-from-scale",
        "omniwand-select-radius-closed", "omniwand-select-radius-open", "omniwand-select-gap", "omniwand-select-row",
        "omniwand-select-chevron", "omniwand-select-stagger", "omniwand-turn-shift", "omniwand-reduced-motion",
        "color-coral",
    };

    private static FileSystemWatcher? _watcher;

    /// <summary>The overlay as saved, or empty when there is none or it does not parse.</summary>
    internal static Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath)) ?? new();
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in map) d[k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            return d;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Starts watching the file so a hand edit applies on save.</summary>
    internal static void Watch()
    {
        if (_watcher != null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler reload = (_, _) => PhosphorTheme.ReloadTuning();
        _watcher.Changed += reload;
        _watcher.Created += reload;
        _watcher.Deleted += reload;
        _watcher.Renamed += (_, _) => PhosphorTheme.ReloadTuning();
    }

    /// <summary>Merges <paramref name="json"/> (a flat object) into the overlay, keeping only tunable names. Returns what was refused.</summary>
    internal static string Set(string json)
    {
        var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
        var overlay = Read();
        var refused = new List<string>();
        foreach (var (k, v) in incoming)
        {
            if (Array.IndexOf(Tunable, k) < 0) { refused.Add(k); continue; }
            if (v.ValueKind == JsonValueKind.Null) overlay.Remove(k);
            else overlay[k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
        }
        Write(overlay);
        return refused.Count == 0 ? "" : "refused " + string.Join(",", refused);
    }

    internal static void Reset() => Write(new(StringComparer.Ordinal));

    private static void Write(Dictionary<string, string> overlay)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (overlay.Count == 0) File.Delete(FilePath);
        else File.WriteAllText(FilePath, JsonSerializer.Serialize(overlay, new JsonSerializerOptions { WriteIndented = true }));
        PhosphorTheme.ReloadTuning();
    }

    /// <summary>Each tunable name with its effective value, and whether the overlay sets it.</summary>
    internal static string Snapshot()
    {
        var overlay = Read();
        var d = Tunable.ToDictionary(n => n, n => new { value = PhosphorTheme.Value(n), tuned = overlay.ContainsKey(n) });
        return JsonSerializer.Serialize(d);
    }
}
