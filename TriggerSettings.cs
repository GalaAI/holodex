using System.Text.Json;

namespace Omniwand;

/// <summary>
/// The mouse and keyboard triggers that act without a click on Holodex itself: holding the middle button opens the
/// Spell Orb, and holding Ctrl (or right-click) arms a drawn Spell. On someone else's PC they surprise (a middle-drag
/// to scroll opens the Orb; Ctrl held for a shortcut starts a gesture), so a new install starts with them off and they
/// are switched on in the tray. An install that already ran keeps them on, as it had them, the first time this file is
/// written; after that the file is the user's choice. Saved in %LOCALAPPDATA%\Omniwand\triggers.json.
/// </summary>
internal sealed class TriggerSettings
{
    public bool MiddleHoldOpensOrb { get; set; }
    public bool HoldCtrlToDraw { get; set; }
    public bool HoldRightClickToDraw { get; set; }

    public static string DefaultPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "triggers.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The saved choice, or the first-run defaults written now: off for a new install, and for an install that already
    /// ran, what it had before this file existed (middle-hold and Ctrl on, right-click off).
    /// </summary>
    public static TriggerSettings LoadOrCreate(bool existingInstall, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<TriggerSettings>(File.ReadAllText(path), Json) is { } saved)
                return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        var created = new TriggerSettings { MiddleHoldOpensOrb = existingInstall, HoldCtrlToDraw = existingInstall, HoldRightClickToDraw = false };
        created.Save(path);
        return created;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
