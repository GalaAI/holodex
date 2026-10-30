using System;
using System.IO;
using System.Threading;

namespace Omniwand;

/// <summary>
/// A small file that says "Holodex changed something system-wide and has not put it back yet". Written when the
/// change is made and deleted when it is undone, so a file still present at the next start means the last run was
/// killed or crashed before it could restore.
/// </summary>
internal sealed class CrashMarker
{
    public CrashMarker(string path) => Path = path;

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>Writes the marker (atomically, so a kill mid-write never leaves a half file). Never throws.</summary>
    public void Set(string value)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, value);
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Clear()
    {
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads and deletes the marker. False when there is none.</summary>
    public bool TryTake(out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(Path)) return false;
            value = File.ReadAllText(Path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Clear();
        return true;
    }
}

/// <summary>
/// Makes sure a judge never ends up without a visible pointer or with a changed mouse speed. Holodex hides every
/// system cursor and may set the Windows pointer speed; a normal exit puts both back. This covers the other ways a
/// run ends: an unhandled exception restores at once, and a kill (Task Manager, a crash the runtime cannot report)
/// leaves a marker that the next start reads and undoes before anything else runs.
/// </summary>
internal static class CursorCrashGuard
{
    private static readonly string Folder =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand");

    /// <summary>Present while the system cursors are hidden.</summary>
    public static CrashMarker CursorsHidden { get; set; } = new(System.IO.Path.Combine(Folder, "cursors-hidden.marker"));

    /// <summary>Holds the user's pointer speed (1 to 20) while Holodex has changed it.</summary>
    public static CrashMarker OriginalMouseSpeed { get; set; } = new(System.IO.Path.Combine(Folder, "mouse-speed-original.marker"));

    /// <summary>Where recoveries and errors are written: %TEMP%\omniwand-errors.log.</summary>
    public static string LogPath { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omniwand-errors.log");

    /// <summary>
    /// Run first at startup. Undoes what a killed or crashed run left behind: the stock cursors come back and the
    /// pointer speed returns to the user's own value. Returns what was restored ("" when the last run ended cleanly).
    /// </summary>
    public static string RecoverAtStartup(Action restoreCursors, Action<int> restoreSpeed)
    {
        var restored = new System.Collections.Generic.List<string>();
        if (CursorsHidden.TryTake(out _))
        {
            try { restoreCursors(); restored.Add("cursors"); } catch { }
        }
        if (OriginalMouseSpeed.TryTake(out string raw) && int.TryParse(raw, out int speed) && speed is >= 1 and <= 20)
        {
            try { restoreSpeed(speed); restored.Add($"mouse speed {speed}"); } catch { }
        }
        string summary = string.Join(", ", restored);
        if (summary.Length > 0) Log($"recovered after an unclean exit: restored {summary}");
        return summary;
    }

    private static int _errorGeneration;

    /// <summary>
    /// A UI or background error Holodex survived. The pointer comes back right away, so a broken overlay never leaves
    /// the user without one. When the cursors were hidden and no further error arrives for <paramref name="settleMs"/>,
    /// they are hidden again (the wand is still drawing); an overlay that keeps failing keeps the stock pointer.
    /// </summary>
    public static void OnSurvivedError(Exception? ex, bool cursorsWereHidden, Action restoreCursors, Action rehideCursors, int settleMs = 3000)
    {
        Log($"survived error: {ex}");
        int generation = Interlocked.Increment(ref _errorGeneration);
        try { restoreCursors(); } catch { }
        if (!cursorsWereHidden) return;
        var timer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref _errorGeneration) != generation) return;
            try { rehideCursors(); } catch { }
        });
        timer.Change(settleMs, Timeout.Infinite);
        _pending = timer;
    }

    /// <summary>Holds the pending re-hide so it is not collected; a newer error or an explicit choice replaces it.</summary>
    private static System.Threading.Timer? _pending;

    /// <summary>The user chose the stock pointer (or Holodex is exiting): a pending re-hide after an error is dropped.</summary>
    public static void CancelPendingRehide()
    {
        Interlocked.Increment(ref _errorGeneration);
        _pending?.Dispose();
        _pending = null;
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} {line}\n"); } catch { }
    }
}
