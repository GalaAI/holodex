using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Holodex;

/// <summary>What the cursor routes need from Windows and from a model, injected so they test without either.</summary>
internal interface ICursorHost
{
    /// <summary>Points each role at its file in the registry and applies it now. Backs the original scheme up once.</summary>
    void Install(IReadOnlyList<(string Role, string File)> written, string backupPath);
    /// <summary>Puts the scheme from before the first install back and reloads the stock cursors.</summary>
    void Restore(string backupPath);
    Task<(bool Ok, string Text)> AskAsync(string prompt, string? imageDataUrl);
}

/// <summary>
/// The cursor routes of the Cursor Mod (plan step 2), ported from Input Studio's <c>/api/cursor/*</c>: write .cur and
/// .ani files, install and restore them, keep named designs as profiles, and ask a model for a design. Files go in
/// the same cursor folder Input Studio used, so profiles and written cursors carry over.
/// </summary>
internal sealed class CursorStudio(string cursorDir, ICursorHost host)
{
    private const int MaxCanvasDataUrlBytes = 1024 * 1024;
    public string CursorDir { get; } = cursorDir;
    private string ProfileDir => Path.Combine(CursorDir, "profiles");
    public string BackupPath => Path.Combine(CursorDir, "cursors-backup-original.reg");

    public static readonly string[] ReadOnlyRoutes = { "/api/cursor/profiles" };

    public async Task<JsonObject?> HandleAsync(string path, JsonNode? body) => path switch
    {
        "/api/cursor/build" => Build(body),
        "/api/cursor/install" => Install(body),
        "/api/cursor/restore" => Restore(),
        "/api/cursor/generate" => await GenerateAsync(body),
        "/api/cursor/profile/save" => SaveProfile(body),
        "/api/cursor/profile/load" => LoadProfile(body),
        "/api/cursor/profile/delete" => DeleteProfile(body),
        "/api/cursor/profiles" => Profiles(),
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------- write and install

    private JsonObject Build(JsonNode? body)
    {
        var cursors = (body as JsonObject)?["cursors"] as JsonArray ?? throw new KitException("cursors must be an array");
        Directory.CreateDirectory(CursorDir);
        var written = new JsonArray();
        foreach (var node in cursors)
        {
            var c = node as JsonObject ?? throw new KitException("each cursor must be an object");
            string name = CursorFiles.SafeProfileName(Str(c, "name"));
            if (name.Length == 0) throw new KitException("a cursor name needs at least one letter or digit");
            string role = Str(c, "role");
            if (!CursorFiles.RoleIds.ContainsKey(role)) throw new KitException($"\"{role}\" is not a Windows cursor role");
            int size = Int(c, "size");
            if (size is < 8 or > 255 || size % 8 != 0) throw new KitException("size must be a multiple of 8 from 8 to 248");
            int hotX = Int(c, "hotX"), hotY = Int(c, "hotY");
            if (hotX < 0 || hotY < 0 || hotX >= size || hotY >= size) throw new KitException("the hotspot must be inside the cursor");

            var frames = (c["frames"] as JsonArray ?? throw new KitException("frames must be an array"))
                .Select(f => Convert.FromBase64String(f?.GetValue<string>() ?? "")).ToList();
            if (frames.Count == 0 || frames.Count > 120) throw new KitException("a cursor takes 1 to 120 frames");
            if (frames.Any(f => f.Length != size * size * 4)) throw new KitException($"each frame must be {size}x{size} RGBA ({size * size * 4} bytes)");
            var rates = (c["rates"] as JsonArray)?.Select(r => (uint)Math.Clamp(double.TryParse(r?.ToJsonString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 2, 1, uint.MaxValue)).ToList()
                ?? new List<uint>();

            bool isAni = frames.Count > 1;
            string file = Path.Combine(CursorDir, name + (isAni ? ".ani" : ".cur"));
            byte[] bytes = isAni ? CursorFiles.EncodeAni(frames, size, hotX, hotY, rates) : CursorFiles.EncodeCur(frames[0], size, hotX, hotY);
            File.WriteAllBytes(file, bytes);
            written.Add(new JsonObject { ["role"] = role, ["file"] = file, ["bytes"] = bytes.Length, ["frames"] = frames.Count });
        }
        return new JsonObject { ["ok"] = true, ["written"] = written };
    }

    /// <summary>
    /// Installs files written by <see cref="Build"/>. Each role must be a Windows cursor role and each file a .cur or
    /// .ani inside the cursor folder: the registry is pointed only at files Holodex wrote.
    /// </summary>
    private JsonObject Install(JsonNode? body)
    {
        var list = (body as JsonObject)?["written"] as JsonArray ?? throw new KitException("written must be an array");
        string root = Path.GetFullPath(CursorDir) + Path.DirectorySeparatorChar;
        var items = new List<(string Role, string File)>();
        foreach (var node in list)
        {
            var w = node as JsonObject ?? throw new KitException("each written entry must be an object");
            string role = Str(w, "role");
            if (!CursorFiles.RoleIds.ContainsKey(role)) throw new KitException($"\"{role}\" is not a Windows cursor role");
            string file = Path.GetFullPath(Str(w, "file"));
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || ext is not (".cur" or ".ani"))
                throw new KitException($"{file} is not a cursor file in {CursorDir}");
            if (!File.Exists(file)) throw new KitException($"{file} does not exist; build it first");
            items.Add((role, file));
        }
        Directory.CreateDirectory(CursorDir);
        host.Install(items, BackupPath);
        return new JsonObject { ["ok"] = true, ["installed"] = new JsonArray(items.Select(i => (JsonNode)i.Role).ToArray()) };
    }

    private JsonObject Restore()
    {
        host.Restore(BackupPath);
        return new JsonObject { ["ok"] = true, ["restored"] = true };
    }

    // ------------------------------------------------------------------------------------------------------ generate

    /// <summary>
    /// Turns a description into shape parameters the studio renders, never pixels: parameters stay editable and can
    /// morph. A canvas question goes only to a model that reads images, since a text-only answer about pixels it
    /// never received is worse than an honest error.
    /// </summary>
    private async Task<JsonObject> GenerateAsync(JsonNode? body)
    {
        var o = body as JsonObject ?? throw new KitException("the body must be an object");
        string brief = (o["prompt"] is JsonValue p && p.TryGetValue(out string? s) ? s : "").Trim();
        if (brief.Length == 0) return Fail("describe the cursor you want");
        string? image = o["imageDataUrl"] is JsonValue iv && iv.TryGetValue(out string? img) && img.Length > 0 ? img : null;
        if (image != null && !image.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            throw new KitException("the canvas snapshot must be a base64 PNG data URL");
        if (image != null && image.Length > MaxCanvasDataUrlBytes) throw new KitException("the canvas snapshot is larger than 1 MB");
        string context = o["context"] is JsonObject ctx ? Clip(ctx.ToJsonString(), 2_000) : "not provided";

        var (ok, reply) = await host.AskAsync(CursorFiles.DesignInstruction(brief, context), image);
        if (!ok) return Fail(reply);
        var design = CursorFiles.DesignFromReply(reply);
        if (design == null) return Fail("the agent did not return JSON", ("raw", Clip(reply, 400)));
        var problems = CursorFiles.ValidateDesign(design);
        if (problems.Count > 0) return Fail(string.Join("; ", problems), ("design", design));
        string message = design["message"] is JsonValue m && m.TryGetValue(out string? msg) ? msg.Trim() : "";
        return new JsonObject { ["ok"] = true, ["message"] = message, ["design"] = design };
    }

    // ------------------------------------------------------------------------------------------------------ profiles

    // Profiles are whole cursor designs saved as files beside the cursors they produce, so anything else that wants
    // one, the agent included, can read it.
    private JsonObject SaveProfile(JsonNode? body)
    {
        var o = body as JsonObject ?? throw new KitException("the body must be an object");
        string name = CursorFiles.SafeProfileName(o["name"]?.ToString());
        if (name.Length == 0) return Fail("a profile name needs at least one letter or digit");
        Directory.CreateDirectory(ProfileDir);
        File.WriteAllText(Path.Combine(ProfileDir, name + ".json"),
            (o["design"]?.DeepClone() ?? new JsonObject()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new JsonObject { ["ok"] = true, ["name"] = name };
    }

    private JsonObject LoadProfile(JsonNode? body)
    {
        string raw = (body as JsonObject)?["name"]?.ToString() ?? "";
        string name = CursorFiles.SafeProfileName(raw);
        string file = Path.Combine(ProfileDir, name + ".json");
        if (name.Length == 0 || !File.Exists(file)) return Fail($"no profile named \"{raw}\"");
        return new JsonObject { ["ok"] = true, ["name"] = name, ["design"] = JsonNode.Parse(File.ReadAllText(file)) };
    }

    private JsonObject DeleteProfile(JsonNode? body)
    {
        string raw = (body as JsonObject)?["name"]?.ToString() ?? "";
        string name = CursorFiles.SafeProfileName(raw);
        string file = Path.Combine(ProfileDir, name + ".json");
        if (name.Length == 0 || !File.Exists(file)) return Fail($"no profile named \"{raw}\"");
        File.Delete(file);
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject Profiles()
    {
        var names = Directory.Exists(ProfileDir)
            ? Directory.GetFiles(ProfileDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return new JsonObject { ["ok"] = true, ["profiles"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) };
    }

    // ------------------------------------------------------------------------------------------------------- helpers

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : throw new KitException($"{key} must be a string");

    private static int Int(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d == Math.Floor(d)
            && d is >= int.MinValue and <= int.MaxValue
            ? (int)d : throw new KitException($"{key} must be a whole number");

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    private static JsonObject Fail(string message, (string Key, JsonNode Value) extra = default)
    {
        var o = new JsonObject { ["ok"] = false, ["error"] = message };
        if (extra.Key != null) o[extra.Key] = extra.Value;
        return o;
    }
}

/// <summary>Installs and restores cursors on this PC with the registry and SetSystemCursor, and asks Holodex's models.</summary>
internal sealed class WindowsCursorHost(ArkStationClient ark) : ICursorHost
{
    private const string CursorsKey = @"Control Panel\Cursors";
    private const uint SpiSetCursors = 0x0057;

    // The stock files Input Studio's restore put back explicitly: SetSystemCursor has already replaced the live
    // handles for the session, and a blank IBeam value gives a reload nothing to pull from.
    private static readonly (string File, uint Id)[] StockFallbacks =
    {
        (@"C:\Windows\Cursors\aero_arrow.cur", 32512),
        (@"C:\Windows\Cursors\beam_r.cur", 32513),
        (@"C:\Windows\Cursors\aero_link.cur", 32649),
    };

    public void Install(IReadOnlyList<(string Role, string File)> written, string backupPath)
    {
        // Back the original scheme up exactly once, so there is always a way home however often this runs.
        if (!File.Exists(backupPath)) RunReg("export", @"HKCU\Control Panel\Cursors", backupPath, "/y");
        using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey, writable: true))
        {
            foreach (var (role, file) in written) key.SetValue(role, file, RegistryValueKind.ExpandString);
            key.SetValue("", "Ark");
        }
        foreach (var (role, file) in written)
        {
            IntPtr h = LoadCursorFromFile(file);
            if (h != IntPtr.Zero) SetSystemCursor(h, CursorFiles.RoleIds[role]);
        }
        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 2);
    }

    public void Restore(string backupPath)
    {
        if (File.Exists(backupPath)) RunReg("import", backupPath);
        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 3);
        foreach (var (file, id) in StockFallbacks)
        {
            if (!File.Exists(file)) continue;
            IntPtr h = LoadCursorFromFile(file);
            if (h != IntPtr.Zero) SetSystemCursor(h, id);
        }
    }

    public async Task<(bool Ok, string Text)> AskAsync(string prompt, string? imageDataUrl)
    {
        var answer = await ark.AskAsync(prompt, imageDataUrl);
        return (answer.Ok, answer.Output);
    }

    private static void RunReg(params string[] args)
    {
        var psi = new ProcessStartInfo("reg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
        if (p.ExitCode != 0) CursorCrashGuard.Log($"reg {args[0]} exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorFromFile(string path);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
}
