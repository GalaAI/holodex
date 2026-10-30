using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Omniwand;

/// <summary>
/// One GrimX Spell kind as the Spells tab shows it: an orb. The kinds, their order and their descriptions are Wiz's
/// own lens list (<see cref="WizCraftingWidgetForm.FlowLenses"/> without "flow", which is every Spell); the accent is
/// the Spell Orb slot's (<see cref="GrimXFlowOrb.Slots"/>), and a kind with no slot (Combo) operates in patina.
/// </summary>
internal sealed record SpellKindInfo(string Id, string Title, string Plural, string Description, Color Accent)
{
    public static IReadOnlyList<SpellKindInfo> All { get; } = WizCraftingWidgetForm.FlowLenses
        .Where(l => l.Id != "flow")
        .Select(l => new SpellKindInfo(l.Id, l.Title, PluralOf(l.Title), l.Verb,
            GrimXFlowOrb.Slots.FirstOrDefault(s => s.Id == l.Id)?.AccentColor ?? PhosphorTheme.Patina))
        .ToArray();

    public static SpellKindInfo? Find(string? id) => All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Quick Casts, Combos, Rituals, Symphonies, Ultimas, Charms, Summons.</summary>
    public static string PluralOf(string title) => title.EndsWith('y') ? title[..^1] + "ies" : title + "s";

    /// <summary>The honest empty state of an orb.</summary>
    public string EmptyText => $"No {Plural} yet";
}

/// <summary>Where a Spell in an orb comes from: Input Spine's bound Spells, or a Wiz draft kept in the spellbook.</summary>
internal enum SpellSource { InputSpine, WizDraft }

/// <summary>
/// A Spell in an orb: its name, kind, what it binds to and what it does. <see cref="Route"/> is the executor it casts
/// through ("Agent Dispatch", "the API bench run"); Spells that share one are related and pulse together.
/// </summary>
internal sealed record OrbSpell(string Kind, string Slug, string Name, string Does, string Binding, SpellSource Source, bool Enabled, string Route, string? Model = null)
{
    public string SourceLabel => Source == SpellSource.InputSpine ? "INPUT SPINE" : "WIZ DRAFT";

    /// <summary>A Spine Spell as Input Spine lists it under its slot, with the trigger the Orb casts it by.</summary>
    public static OrbSpell FromSpine(string kind, SpellChoice choice) => new(
        kind, choice.Slug, choice.Name, choice.Description ?? "Input Spine lists no description for this Spell.",
        ArkStationClient.SpellTrigger(kind, choice.Slug), SpellSource.InputSpine, choice.Enabled, RouteOf(choice.Description, choice.Slug));

    public static OrbSpell FromDraft(CraftedSpell d) => new(
        d.Kind, d.Id, d.Name, d.Does, "Not bound in Input Spine yet", SpellSource.WizDraft, true, "Wiz draft", d.Model);

    /// <summary>"Summons doc through Agent Dispatch (ark agent mission) ..." is "Agent Dispatch"; no "through" is the slug alone.</summary>
    internal static string RouteOf(string? description, string slug)
    {
        var m = Regex.Match(description ?? "", @"\bthrough\s+(.+?)(?:\s*\(|\.(?:\s|$)|$)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : slug;
    }
}

/// <summary>A Spell Wiz crafted from the Spells composer, kept in the spellbook until Input Spine binds it.</summary>
internal sealed record CraftedSpell(string Id, string Kind, string Name, string Does, string Goal, string Draft, string Model, string Route, string CreatedAt);

/// <summary>
/// The Spells Wiz crafts on this PC. Input Spine (Ark) owns bound Spells and has no route to create one, so a crafted
/// Spell is kept here, beside the Spell picks (<see cref="SpellPicks"/>) in %LOCALAPPDATA%\Omniwand, as
/// spellbook.json: one list, newest last, rewritten whole on each add.
/// </summary>
internal sealed class Spellbook
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "spellbook.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public Spellbook(string? path = null) => _path = path ?? DefaultPath;

    public string FilePath => _path;

    public IReadOnlyList<CraftedSpell> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<CraftedSpell>();
            return JsonSerializer.Deserialize<List<CraftedSpell>>(File.ReadAllText(_path)) ?? new List<CraftedSpell>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<CraftedSpell>();
        }
    }

    /// <summary>Appends <paramref name="spell"/>; false with the reason when the file cannot be written.</summary>
    public bool Add(CraftedSpell spell, out string error)
    {
        try
        {
            var all = Load().ToList();
            all.Add(spell);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, _path, overwrite: true);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// A Wiz draft as a Spell: the "Name:" and "Does:" lines Wiz was asked for, else the goal and the draft's first
    /// line. The whole draft is kept.
    /// </summary>
    public static CraftedSpell FromDraft(string goal, string kind, string draft, string model, string route)
    {
        string? Field(string label)
        {
            var m = Regex.Match(draft, $@"^[\s>*#_-]*{label}[\s*_]*[:\-]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return m.Success ? Clean(m.Groups[1].Value) : null;
        }
        string firstLine = draft.Split('\n').Select(Clean).FirstOrDefault(l => l.Length > 0 && !Regex.IsMatch(l, @"^(name|does)\s*[:\-]", RegexOptions.IgnoreCase)) ?? "";
        string name = Field("name") is { Length: > 0 } n ? n : Clip(Clean(goal.Split('\n')[0]), 48);
        string does = Field("does") is { Length: > 0 } d ? d : firstLine.Length > 0 ? firstLine : Clean(goal);
        return new CraftedSpell($"draft-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}", kind, Clip(name, 48), Clip(does, 240),
            goal.Trim(), draft.Trim(), model, route, DateTime.UtcNow.ToString("O"));
    }

    private static string Clean(string s) => Regex.Replace(s, @"[*_`#]+", "").Trim().Trim('"', '\'', '.', ' ');

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
}

/// <summary>
/// The Spells per kind: Input Spine's bound Spells (GET /api/spine/grimx/spells, by Orb slot id) and then the
/// spellbook's Wiz drafts. Nothing else: a kind with neither is an empty orb.
/// </summary>
internal static class SpellInventory
{
    public static Dictionary<string, List<OrbSpell>> Merge(IReadOnlyDictionary<string, List<SpellChoice>> spine, IEnumerable<CraftedSpell> drafts)
    {
        var byKind = SpellKindInfo.All.ToDictionary(k => k.Id, _ => new List<OrbSpell>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, choices) in spine)
            if (byKind.TryGetValue(slot, out var list))
                list.AddRange(choices.Select(c => OrbSpell.FromSpine(slot.ToLowerInvariant(), c)));
        foreach (var d in drafts)
            if (byKind.TryGetValue(d.Kind, out var list))
                list.Add(OrbSpell.FromDraft(d));
        return byKind;
    }
}

/// <summary>
/// Orbit geometry for an open orb, after the orbital timeline reference: node i of n sits at i/n of the turn plus
/// the rotation; depth fades the far side (top) to 0.4; selecting a node turns the orbit so it sits at the top.
/// The radius is the reference's 200 desktop px, scaled down to fit the drawer with room for labels.
/// </summary>
internal static class SpellOrbit
{
    public const float DesktopRadius = 200f;
    public const float ExpandedScale = 1.5f;
    /// <summary>The rest rotation: the first Spell at the bottom, nearest.</summary>
    public const float RestRotation = 90f;
    /// <summary>Where a selected node is turned to: the top.</summary>
    public const float SelectedAngle = 270f;

    /// <summary>Room kept outside the orbit for a node and its label.</summary>
    public const float LabelRoomPx = 64f;

    public static float Radius(float width, float height, float scale) =>
        Math.Max(56f * scale, Math.Min(DesktopRadius * scale, Math.Min(width, height) / 2f - LabelRoomPx * scale));

    public static float Normalize(float deg) => ((deg % 360f) + 360f) % 360f;

    public static float AngleDeg(int index, int total, float rotationDeg) =>
        Normalize((total <= 0 ? 0f : index * 360f / total) + rotationDeg);

    public static PointF Position(PointF center, float radius, float angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new PointF(center.X + (float)(radius * Math.Cos(rad)), center.Y + (float)(radius * Math.Sin(rad)));
    }

    /// <summary>0.4 at the top (far), 1 at the bottom (near).</summary>
    public static float DepthOpacity(float angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return Math.Clamp(0.4f + 0.6f * (float)((1 + Math.Sin(rad)) / 2), 0.4f, 1f);
    }

    /// <summary>The rotation that brings node <paramref name="index"/> to the top.</summary>
    public static float CenterOn(int index, int total) =>
        Normalize(SelectedAngle - (total <= 0 ? 0f : index * 360f / total));

    /// <summary>The shortest signed turn from <paramref name="from"/> to <paramref name="to"/>, in degrees.</summary>
    public static float ShortestTurn(float from, float to)
    {
        float d = Normalize(to - from);
        return d > 180f ? d - 360f : d;
    }

    /// <summary>The other Spells that cast through the same route as <paramref name="index"/>.</summary>
    public static IReadOnlyList<int> Related(IReadOnlyList<OrbSpell> spells, int index)
    {
        if (index < 0 || index >= spells.Count) return Array.Empty<int>();
        string route = spells[index].Route;
        return Enumerable.Range(0, spells.Count).Where(j => j != index && string.Equals(spells[j].Route, route, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}

/// <summary>
/// The Spell kind glyphs: each kind's mark inside a ring, so every icon reads as an orb. Same 24-unit grid, round
/// caps and stroke weight as <see cref="PhosphorIcons"/>.
/// </summary>
internal static class SpellOrbGlyphs
{
    public static void Draw(Graphics g, string kind, RectangleF box, Color ink, float stroke, bool ring = true)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        float u = box.Width / 24f;
        g.ScaleTransform(u, u);
        using var pen = new Pen(ink, stroke / u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (ring) g.DrawEllipse(pen, 1.5f, 1.5f, 21, 21);
        switch (kind)
        {
            case "spell": // Quick Cast: one bolt
                g.DrawLines(pen, new[] { new PointF(13.5f, 5), new PointF(8, 13), new PointF(12, 13), new PointF(10.5f, 19), new PointF(16, 11), new PointF(12, 11), new PointF(13.5f, 5) });
                break;
            case "combo": // Combo: linked casts
                g.DrawEllipse(pen, 5.5f, 9, 6, 6);
                g.DrawEllipse(pen, 12.5f, 9, 6, 6);
                break;
            case "ritual": // Ritual: a cadence, a hand on a dial
                g.DrawArc(pen, 6, 6, 12, 12, -60, 300);
                g.DrawLines(pen, new[] { new PointF(12, 8.5f), new PointF(12, 12), new PointF(14.5f, 13.5f) });
                g.DrawLines(pen, new[] { new PointF(15.2f, 4.6f), new PointF(15, 7), new PointF(17.4f, 7.2f) });
                break;
            case "symphony": // Symphony: Spells in sequence
                g.DrawLine(pen, 7, 15, 7, 10);
                g.DrawLine(pen, 12, 17, 12, 7);
                g.DrawLine(pen, 17, 15, 17, 10);
                break;
            case "ultima": // Ultima: works toward a goal
                g.DrawEllipse(pen, 7, 7, 10, 10);
                using (var dot = new SolidBrush(ink)) g.FillEllipse(dot, 10.4f, 10.4f, 3.2f, 3.2f);
                break;
            case "charm": // Charm: ties a Spell to a trigger
                g.DrawPolygon(pen, new[] { new PointF(12, 5.5f), new PointF(17, 12), new PointF(12, 18.5f), new PointF(7, 12) });
                g.DrawLine(pen, 9.5f, 12, 14.5f, 12);
                break;
            case "summon": // Summon: calls one of your agents
                g.DrawEllipse(pen, 9.5f, 6, 5, 5);
                g.DrawArc(pen, 7, 12.5f, 10, 9, 200, 140);
                break;
            default:
                g.DrawLine(pen, 12, 7, 12, 17);
                g.DrawLine(pen, 7, 12, 17, 12);
                break;
        }
        g.Restore(state);
    }
}
