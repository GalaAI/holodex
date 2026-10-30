using System.Text.Json;

/// <summary>
/// Which Spell each Spell Orb slot casts. A slot with no pick casts its kind's first Spell (the bare
/// <c>grimx/&lt;slot&gt;</c> trigger); a pick casts <c>grimx/&lt;slot&gt;/&lt;spell&gt;</c>. Kept across restarts in
/// %LOCALAPPDATA%\Omniwand\spell-picks.json, beside the app's other local state.
/// </summary>
internal static class SpellPicks
{
    public sealed record Pick(string Slug, string Name);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "spell-picks.json");

    private static Dictionary<string, Pick>? _picks;

    private static Dictionary<string, Pick> Picks
    {
        get
        {
            if (_picks != null) return _picks;
            try
            {
                _picks = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Pick>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _picks = new();
            }
            return _picks;
        }
    }

    public static Pick? Get(string slotId) => Picks.TryGetValue(slotId, out var pick) ? pick : null;

    public static void Set(string slotId, string slug, string name)
    {
        Picks[slotId] = new Pick(slug, name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Picks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pick still holds for this session.
        }
    }
}
