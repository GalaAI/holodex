using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Holodex;

/// <summary>
/// One control on a rendered surface, in the bitmap's own pixels: where it is, what it is called and which Phosphor
/// component and tokens draw it. The UI Lab hit-tests a click on the PNG against these boxes.
/// </summary>
internal sealed record SurfaceElement(
    string Id, string Name, string Type, int X, int Y, int W, int H, int Depth, string? Parent, string Text, string Component, string[] Tokens)
{
    /// <summary>The live control or menu row behind the entry, for <c>ui:element</c>; never written to the map.</summary>
    [JsonIgnore] public object? Source { get; init; }
}

/// <summary>
/// The element map written beside every control-tree render (<c>&lt;name&gt;.json</c> next to <c>&lt;name&gt;.png</c>):
/// the surface, the bitmap size and every visible control with its box. It is built only while a capture is being
/// taken, from the tree the render just drew, so nothing here runs while the app paints or lays out.
/// </summary>
internal sealed record SurfaceElementMap(string Surface, int Width, int Height, IReadOnlyList<SurfaceElement> Elements)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Text longer than this is cut: the map names a control, it does not carry a conversation.</summary>
    internal const int MaxText = 80;

    /// <summary>
    /// Phosphor component kinds and the tokens each class paints with, by CLR type name. One hand-kept table: a
    /// control knows its tokens only inside OnPaint, so the list is read off the class and kept beside its name
    /// here. A subclass inherits its base class's row (ModelChip has its own because it adds the ember key state).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Component, string[] Tokens)> Components =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["PhosphorButton"] = ("button", new[] { "radius-btn", "touch-min", "text-size-btn", "color-bone", "color-line-2", "color-surface-2", "color-line", "color-ground", "color-ghost", "color-dim", "color-plate", "color-rust", "color-patina", "color-coral" }),
            ["ThreadView"] = ("card", new[] { "color-surface", "color-dim" }),
            ["ThreadCard"] = ("card", new[] { "color-bone", "color-dim", "color-ghost", "color-line", "color-line-2", "color-ground", "color-cream", "color-coral", "color-patina", "color-ember", "color-moss", "dur-pulse" }),
            ["SuggestionChip"] = ("chip", new[] { "holodex-select-radius-closed", "holodex-select-radius-open", "holodex-select-chevron", "dur-disclosure", "color-surface-2", "color-line-2", "color-bone", "color-dim", "color-ghost", "color-cream", "color-patina" }),
            ["ModelChip"] = ("chip", new[] { "holodex-select-radius-closed", "holodex-select-radius-open", "holodex-select-chevron", "dur-disclosure", "color-surface-2", "color-line-2", "color-bone", "color-dim", "color-ghost", "color-cream", "color-patina", "color-ember" }),
            ["PromptTextBox"] = ("field", new[] { "text-size-body", "color-surface-2", "color-bone", "color-ghost" }),
            ["PhosphorField"] = ("field", new[] { "radius-btn", "text-size-body", "text-size-data", "color-surface-2", "color-line-2", "color-bone", "color-ghost", "color-patina" }),
            ["RailButton"] = ("rail", new[] { "radius-btn", "color-surface-2", "color-line-2", "color-bone", "color-dim", "color-patina" }),
            ["RailMark"] = ("rail", new[] { "ark-emblem-cream", "ark-emblem-globe", "ark-emblem-globe-opacity", "color-ground" }),
            ["LedRow"] = ("led", new[] { "color-dim" }),
            // FluidSelect is a trigger chip and this drop; the drop and its list are the select.
            ["FluidSelectDrop"] = ("select", new[] { "holodex-select-radius-open", "holodex-select-gap", "dur-disclosure", "color-surface" }),
            ["FluidSelectList"] = ("select", new[] { "holodex-select-radius-open", "holodex-select-row", "holodex-select-stagger", "radius-badge", "s3", "u", "dur-disclosure", "dur-disclosure-item", "text-size-body", "text-size-data", "color-surface", "color-line-2", "color-bone", "color-dim", "color-ghost", "color-coral" }),
            ["DataLabel"] = ("label", new[] { "text-size-data", "color-dim" }),
        };

    /// <summary>The component kind and tokens for a type: its own row, else its nearest base class's, else its name lowercased with no tokens.</summary>
    internal static (string Component, string[] Tokens) ComponentOf(Type type)
    {
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            if (Components.TryGetValue(t.Name, out var row)) return row;
        return (type.Name.ToLowerInvariant(), Array.Empty<string>());
    }

    /// <summary>
    /// Walks <paramref name="root"/> as the render drew it. Boxes are bitmap pixels from the root's top-left, each
    /// child's Location added to its parents' (the widgets are borderless, so client and window share an origin).
    /// A control is listed when it is visible, has a size and is not wholly clipped away by a parent. The root is
    /// always listed: it is the bitmap. Call this while the surface is shown, or every child reports invisible.
    /// </summary>
    internal static SurfaceElementMap Build(string surface, Control root)
    {
        var elements = new List<SurfaceElement>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var bounds = new Rectangle(0, 0, Math.Max(1, root.Width), Math.Max(1, root.Height));
        string rootId = Unique(root.Name.Length > 0 ? root.Name : root.GetType().Name, taken);
        elements.Add(Describe(root, rootId, bounds, 0, null));
        Walk(root, rootId, Point.Empty, bounds, 1, elements, taken);
        return new SurfaceElementMap(surface, bounds.Width, bounds.Height, elements);
    }

    private static void Walk(Control parent, string parentId, Point origin, Rectangle clip, int depth, List<SurfaceElement> elements, HashSet<string> taken)
    {
        if (parent is ToolStrip strip)
        {
            WalkItems(strip, parentId, origin, clip, depth, elements, taken);
            return;
        }
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Control child in parent.Controls)
        {
            // Counted before the visibility check, so hiding a sibling does not renumber the ones after it.
            string id = Unique(parentId + "/" + Segment(child.Name, child.GetType().Name, seen), taken);
            var box = new Rectangle(origin.X + child.Left, origin.Y + child.Top, child.Width, child.Height);
            if (!child.Visible || box.Width <= 0 || box.Height <= 0) continue;
            var shown = Rectangle.Intersect(clip, box);
            if (shown.IsEmpty) continue;
            elements.Add(Describe(child, id, box, depth, parentId));
            Walk(child, id, box.Location, shown, depth + 1, elements, taken);
        }
    }

    /// <summary>
    /// A menu's rows are items, not child controls, and a menu that was never opened reports its hosted controls
    /// invisible, so the rows are read from the items' own boxes. A hosted control (a speed slider) is listed as
    /// itself, in its row's box.
    /// </summary>
    private static void WalkItems(ToolStrip strip, string parentId, Point origin, Rectangle clip, int depth, List<SurfaceElement> elements, HashSet<string> taken)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ToolStripItem item in strip.Items)
        {
            Control? hosted = (item as ToolStripControlHost)?.Control;
            Type type = hosted?.GetType() ?? item.GetType();
            string id = Unique(parentId + "/" + Segment(hosted?.Name is { Length: > 0 } n ? n : item.Name ?? string.Empty, type.Name, seen), taken);
            var box = new Rectangle(origin.X + item.Bounds.X, origin.Y + item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);
            if (!item.Available || box.Width <= 0 || box.Height <= 0) continue;
            var shown = Rectangle.Intersect(clip, box);
            if (shown.IsEmpty) continue;
            if (hosted != null)
            {
                elements.Add(Describe(hosted, id, box, depth, parentId));
                continue;
            }
            var (component, tokens) = ComponentOf(type);
            elements.Add(new SurfaceElement(id, item.Name ?? string.Empty, type.Name, box.X, box.Y, box.Width, box.Height, depth, parentId, Clip(item.Text), component, tokens) { Source = item });
        }
    }

    private static SurfaceElement Describe(Control control, string id, Rectangle box, int depth, string? parentId)
    {
        var (component, tokens) = ComponentOf(control.GetType());
        return new SurfaceElement(id, control.Name, control.GetType().Name, box.X, box.Y, box.Width, box.Height, depth, parentId, TextOf(control), component, tokens) { Source = control };
    }

    /// <summary>
    /// The control's Name, or its type and its place among the unnamed siblings of that type ("Panel#1") when it has
    /// none. Hidden siblings are counted too, so showing or hiding one does not renumber the rest.
    /// </summary>
    private static string Segment(string name, string type, Dictionary<string, int> seen)
    {
        if (name.Length > 0) return name;
        int index = seen.TryGetValue(type, out int n) ? n : 0;
        seen[type] = index + 1;
        return $"{type}#{index}";
    }

    /// <summary>Two siblings can share a Name; the later one takes a numbered id so every id finds one element.</summary>
    private static string Unique(string id, HashSet<string> taken)
    {
        string unique = id;
        for (int n = 2; !taken.Add(unique); n++) unique = $"{id}~{n}";
        return unique;
    }

    /// <summary>
    /// What the map says a control reads. A masked box never gives its text, and neither does any box in the Vault:
    /// its intake shows a pasted key in the clear while the user checks it.
    /// </summary>
    internal static string TextOf(Control control)
    {
        if (control is TextBoxBase box)
        {
            if (box is TextBox { UseSystemPasswordChar: true } or TextBox { PasswordChar: not '\0' }) return string.Empty;
            if (box is MaskedTextBox { UseSystemPasswordChar: true } or MaskedTextBox { PasswordChar: not '\0' }) return string.Empty;
            for (Control? c = box; c != null; c = c.Parent)
                if (c is VaultForm) return string.Empty;
        }
        return Clip(control.Text);
    }

    private static string Clip(string? text)
    {
        string flat = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= MaxText ? flat : flat[..MaxText];
    }

    internal string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Writes the map beside <paramref name="pngPath"/> and returns its path.</summary>
    internal string WriteBeside(string pngPath)
    {
        string path = PathBeside(pngPath);
        File.WriteAllText(path, ToJson());
        return path;
    }

    internal static string PathBeside(string pngPath) => Path.ChangeExtension(pngPath, ".json");

    /// <summary>
    /// One element as it stands now, for <c>ui:element</c>: its map entry plus the colours, font and padding the
    /// live control holds and the current value of each token it paints with. Null when no element has that id.
    /// </summary>
    internal string? DescribeElement(string elementId)
    {
        var e = Elements.FirstOrDefault(x => string.Equals(x.Id, elementId, StringComparison.Ordinal));
        if (e == null) return null;
        var control = e.Source as Control;
        var item = e.Source as ToolStripItem;
        Font? font = control?.Font ?? item?.Font;
        Padding? padding = control?.Padding ?? item?.Padding;
        var d = new Dictionary<string, object?>
        {
            ["surface"] = Surface,
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["type"] = e.Type,
            ["component"] = e.Component,
            ["x"] = e.X,
            ["y"] = e.Y,
            ["w"] = e.W,
            ["h"] = e.H,
            ["depth"] = e.Depth,
            ["parent"] = e.Parent,
            ["text"] = e.Text,
            ["enabled"] = control?.Enabled ?? item?.Enabled ?? true,
            ["backColor"] = Hex(control?.BackColor ?? item?.BackColor),
            ["foreColor"] = Hex(control?.ForeColor ?? item?.ForeColor),
            ["font"] = font == null ? null : new Dictionary<string, object?>
            {
                ["family"] = font.FontFamily.Name,
                ["size"] = Math.Round(font.Size, 2),
                ["unit"] = font.Unit.ToString().ToLowerInvariant(),
                ["style"] = font.Style.ToString().ToLowerInvariant(),
            },
            ["padding"] = padding is { } p ? new Dictionary<string, object?> { ["left"] = p.Left, ["top"] = p.Top, ["right"] = p.Right, ["bottom"] = p.Bottom } : null,
            ["tokens"] = e.Tokens,
            ["tokenValues"] = e.Tokens.ToDictionary(t => t, t => PhosphorTheme.Value(t)),
        };
        return JsonSerializer.Serialize(d);
    }

    /// <summary>#rrggbb, or #rrggbbaa when the colour is not opaque (a transparent BackColor reads #ffffff00).</summary>
    internal static string? Hex(Color? color) => color is not { } c ? null
        : c.A == 255 ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";
}

/// <summary>What a <c>ui:</c> pipe command asks for.</summary>
internal enum UiLabVerb { Surfaces, Render, Element }

/// <summary>A parsed <c>ui:</c> command: the verb, the surface it names and its argument (a PNG path or an element id).</summary>
internal sealed record UiLabCommand(UiLabVerb Verb, string Surface = "", string Argument = "")
{
    /// <summary>
    /// The one folder the UI Lab bridge renders into. The bridge runs beside a web page, so unlike
    /// <c>render:&lt;folder&gt;</c> it never takes a path outside it, developer pipe or not.
    /// </summary>
    internal static readonly string RenderRoot = Path.Combine(Path.GetTempPath(), "Holodex", "ui-lab");

    /// <summary>
    /// Reads <c>ui:surfaces</c>, <c>ui:render:&lt;name&gt;:&lt;png path&gt;</c> and
    /// <c>ui:element:&lt;surface&gt;:&lt;element id&gt;</c>. A surface name is lowercase letters, digits and dashes, so
    /// the first colon after it starts the argument and a drive letter or an id with a colon passes through whole.
    /// Returns null and says why in <paramref name="error"/> when the command does not parse or the path is refused.
    /// </summary>
    internal static UiLabCommand? Parse(string cmd, out string error)
    {
        error = string.Empty;
        if (!cmd.StartsWith("ui:", StringComparison.OrdinalIgnoreCase)) { error = "error:ui:not a ui command"; return null; }
        string rest = cmd[3..];
        if (rest.Equals("surfaces", StringComparison.OrdinalIgnoreCase)) return new UiLabCommand(UiLabVerb.Surfaces);

        string[] parts = rest.Split(':', 3);
        string verb = parts[0].ToLowerInvariant();
        if (verb is not ("render" or "element")) { error = "error:ui:use surfaces, render:<name>:<png path> or element:<surface>:<element id>"; return null; }
        string surface = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : string.Empty;
        string argument = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        if (surface.Length == 0 || surface.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) { error = $"error:ui:{verb}:surface name required"; return null; }
        if (verb == "element")
        {
            if (argument.Length == 0) { error = "error:ui:element:element id required"; return null; }
            return new UiLabCommand(UiLabVerb.Element, surface, argument);
        }
        if (!PathAllowed(argument)) { error = $"error:ui:render:renders are .png files under {RenderRoot}"; return null; }
        return new UiLabCommand(UiLabVerb.Render, surface, Path.GetFullPath(argument));
    }

    /// <summary>Whether a render may be written to <paramref name="path"/>: a .png inside <see cref="RenderRoot"/>, after "..", is resolved.</summary>
    internal static bool PathAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(RenderRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }
}
