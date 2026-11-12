using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Holodex;

/// <summary>
/// The pure half of the cursor designer, ported from GalaAI/ark <c>packages/cursor-mod/src/cursor-files.ts</c>: the
/// Windows cursor role table, the check a generated design must pass, profile names, and the .cur and .ani encoders.
/// Nothing here touches the registry or the disk.
/// </summary>
internal static class CursorFiles
{
    /// <summary>
    /// Windows cursor roles: the registry value name and the OCR_ id SetSystemCursor needs. The registry makes a
    /// cursor stick across sign-in, SetSystemCursor applies it now, and both are needed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint> RoleIds = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["Arrow"] = 32512, ["IBeam"] = 32513, ["Wait"] = 32514, ["Crosshair"] = 32515, ["UpArrow"] = 32516,
        ["SizeNWSE"] = 32642, ["SizeNESW"] = 32643, ["SizeWE"] = 32644, ["SizeNS"] = 32645, ["SizeAll"] = 32646,
        ["No"] = 32648, ["Hand"] = 32649, ["AppStarting"] = 32650, ["Help"] = 32651,
    };

    /// <summary>The fields each shape kind must carry.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ShapeFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["poly"] = new[] { "pts", "scale" },
        ["ring"] = new[] { "r", "w" },
        ["beam"] = new[] { "h", "gap", "w" },
        ["cross"] = new[] { "len", "gap", "w" },
        ["dbl"] = new[] { "angle", "len", "head", "w" },
        ["quad"] = new[] { "len", "head", "w" },
        ["tri"] = new[] { "size", "w" },
        ["no"] = new[] { "r", "w" },
        ["spinner"] = new[] { "r", "w", "sweep" },
        ["bitmap"] = new[] { "src" },
    };

    /// <summary>
    /// Checks a generated design before it reaches the renderer. Returns readable problems instead of throwing,
    /// because they are shown to the person who asked; a design naming a shape it never defined is the commonest.
    /// </summary>
    public static List<string> ValidateDesign(JsonNode? design)
    {
        var problems = new List<string>();
        if ((design as JsonObject)?["shapes"] is not JsonObject shapes) return new() { "no shapes object in the design" };
        var root = (JsonObject)design!;

        foreach (var (name, raw) in shapes)
        {
            var shape = raw as JsonObject;
            string kind = shape?["kind"] is JsonValue k && k.TryGetValue(out string? s) ? s : "undefined";
            if (shape == null || !ShapeFields.TryGetValue(kind, out var fields))
            {
                problems.Add($"shape \"{name}\" has unknown kind \"{kind}\"");
                continue;
            }
            foreach (var f in fields)
                if (!shape.ContainsKey(f)) problems.Add($"shape \"{name}\" is missing \"{f}\"");
            if (kind == "poly" && (shape["pts"] is not JsonArray pts || pts.Count < 3))
                problems.Add($"shape \"{name}\" needs at least three points");
        }

        if (root["transitions"] is not JsonArray transitions)
        {
            problems.Add("no transitions array in the design");
            return problems;
        }
        bool hasShapes = shapes.Count > 0;
        if (hasShapes && transitions.Count == 0) problems.Add("generated shapes need at least one transition");
        if (!hasShapes && transitions.Count == 0 && !(root["message"] is JsonValue m && m.TryGetValue(out string? _)))
            problems.Add("the agent returned neither an answer nor a design change");
        foreach (var raw in transitions)
        {
            var t = raw as JsonObject;
            string role = Text(t?["role"]);
            if (!RoleIds.ContainsKey(role)) problems.Add($"\"{role}\" is not a Windows cursor role");
            foreach (var end in new[] { "from", "to" })
            {
                string reference = Text(t?[end]);
                if (shapes[reference] == null) problems.Add($"{role}.{end} names shape \"{reference}\", which is not defined");
            }
            if (t?["mode"] is JsonNode mode && Text(mode) is not ("morph" or "spin"))
                problems.Add($"{role} has mode \"{Text(mode)}\", expected morph or spin");
        }
        return problems;
    }

    private static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : n?.ToJsonString() ?? "undefined";

    /// <summary>
    /// A profile name becomes a file name, so it is squeezed to characters that cannot leave the folder. An empty
    /// result is what fails, not a merely punctuated name.
    /// </summary>
    public static string SafeProfileName(string? raw)
    {
        string s = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), "[^a-z0-9._-]+", "-");
        s = Regex.Replace(s, "^[.-]+|[.-]+$", "");
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>A .cur is an ICONDIR around a bottom-up 32bpp DIB and a legacy AND mask. Pixels arrive RGBA, top down.</summary>
    public static byte[] EncodeCur(ReadOnlySpan<byte> rgba, int size, int hotX, int hotY)
    {
        int xor = size * size * 4;
        int and = size / 8 * size;
        var buf = new byte[22 + 40 + xor + and];
        var b = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], 2); // type: cursor
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], 1); // one image
        b[6] = (byte)size;
        b[7] = (byte)size;
        BinaryPrimitives.WriteUInt16LittleEndian(b[10..], (ushort)hotX);
        BinaryPrimitives.WriteUInt16LittleEndian(b[12..], (ushort)hotY);
        BinaryPrimitives.WriteUInt32LittleEndian(b[14..], (uint)(40 + xor + and));
        BinaryPrimitives.WriteUInt32LittleEndian(b[18..], 22);
        BinaryPrimitives.WriteUInt32LittleEndian(b[22..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(b[26..], size);
        BinaryPrimitives.WriteInt32LittleEndian(b[30..], size * 2); // doubled height: XOR then AND
        BinaryPrimitives.WriteUInt16LittleEndian(b[34..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b[36..], 32);
        int o = 62;
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                buf[o++] = rgba[i + 2];
                buf[o++] = rgba[i + 1];
                buf[o++] = rgba[i];
                buf[o++] = rgba[i + 3];
            }
        return buf;
    }

    /// <summary>
    /// RIFF/ACON. Windows restarts an animated cursor from frame zero whenever it becomes current, so a long last
    /// rate turns a looping format into a one-shot that plays on arrival and then holds.
    /// </summary>
    public static byte[] EncodeAni(IReadOnlyList<byte[]> frames, int size, int hotX, int hotY, IReadOnlyList<uint> rates)
    {
        using var list = new MemoryStream();
        list.Write("fram"u8);
        foreach (var f in frames)
        {
            var cur = EncodeCur(f, size, hotX, hotY);
            Chunk(list, "icon", cur);
            if (cur.Length % 2 == 1) list.WriteByte(0);
        }
        var anih = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(anih, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(4), (uint)frames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(8), (uint)frames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(28), rates.Count > 0 ? rates[0] : 2);
        BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(32), 1); // frames are icon data, not raw DIBs
        var rate = new byte[frames.Count * 4];
        for (int i = 0; i < Math.Min(rates.Count, frames.Count); i++) BinaryPrimitives.WriteUInt32LittleEndian(rate.AsSpan(i * 4), rates[i]);

        using var body = new MemoryStream();
        body.Write("ACON"u8);
        Chunk(body, "anih", anih);
        Chunk(body, "rate", rate);
        Chunk(body, "LIST", list.ToArray());
        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)body.Length);
        file.Write(len);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static void Chunk(Stream s, string id, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(data);
    }

    /// <summary>The instruction that asks a model for a cursor design as shape parameters, never pixels.</summary>
    public static string DesignInstruction(string brief, string context) => string.Join("\n",
        "You design Windows cursors for a tool that renders shape parameters, not images.",
        "Return ONLY minified JSON, no prose and no code fence, of this exact form:",
        "{\"message\":\"short plain-language answer\",\"shapes\":{\"<name>\":{...}},\"transitions\":[{\"role\":\"<WindowsRole>\",\"name\":\"ark-<slug>\",\"from\":\"<shape>\",\"to\":\"<shape>\",\"mode\":\"morph\"|\"spin\"}]}",
        "",
        "The attached image is the currently selected cursor rendered by the studio.",
        "If the user asks a question about the canvas, answer it in message and return empty shapes and transitions unless they also request a design change.",
        "If the user requests a change, explain it briefly in message and return only the added or updated shapes and transitions.",
        "Never claim you saw the canvas when no image is attached.",
        $"Canvas context: {context}",
        "",
        "Allowed shape kinds and their fields:",
        "  poly    {\"kind\":\"poly\",\"pts\":[[x,y],...],\"scale\":n}  points are relative to the hotspot; the first point is the tip",
        "  ring    {\"kind\":\"ring\",\"r\":n,\"w\":n}",
        "  beam    {\"kind\":\"beam\",\"h\":n,\"gap\":n,\"w\":n}",
        "  cross   {\"kind\":\"cross\",\"len\":n,\"gap\":n,\"w\":n}",
        "  dbl     {\"kind\":\"dbl\",\"angle\":deg,\"len\":n,\"head\":n,\"w\":n}   double headed arrow",
        "  quad    {\"kind\":\"quad\",\"len\":n,\"head\":n,\"w\":n}              four headed arrow",
        "  tri     {\"kind\":\"tri\",\"size\":n,\"w\":n}",
        "  no      {\"kind\":\"no\",\"r\":n,\"w\":n}",
        "  spinner {\"kind\":\"spinner\",\"r\":n,\"w\":n,\"sweep\":deg}",
        "",
        $"Windows roles: {string.Join(", ", RoleIds.Keys)}.",
        "Units are pixels in a 32 wide box with the hotspot at 16,16 unless told otherwise.",
        "Keep every shape inside that box or it will be clipped when written.",
        "Use mode \"spin\" only for Wait and AppStarting; a spinning shape loops, everything else morphs once and holds.",
        "",
        $"Design brief: {brief}");

    /// <summary>Models add fences and preamble even when told not to, so the outermost braces are taken as the JSON.</summary>
    public static JsonNode? DesignFromReply(string reply)
    {
        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(reply[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }
}
