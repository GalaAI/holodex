using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The UI Lab bridge: the element map written beside each render (ids, boxes in bitmap pixels, what is left out,
/// the component and token table) and the <c>ui:</c> pipe commands, which never take a path outside their folder.
/// </summary>
public class SurfaceElementMapTests
{
    [Fact]
    public void Map_lists_visible_controls_with_ids_and_boxes_from_the_root()
    {
        OnSta(() =>
        {
            var (form, go, panel, inner) = Board();
            try
            {
                var map = SurfaceElementMap.Build("lab", form);
                Assert.Equal("lab", map.Surface);
                Assert.Equal(form.Width, map.Width);
                Assert.Equal(form.Height, map.Height);

                // The root is the bitmap: listed first, at the origin, with no parent.
                var root = map.Elements[0];
                Assert.Equal(("Lab", "Form", 0, 0, 0, (string?)null), (root.Id, root.Type, root.X, root.Y, root.Depth, root.Parent));

                var button = Assert.Single(map.Elements, e => e.Id == "Lab/Lab.Go");
                Assert.Equal((go.Left, go.Top, go.Width, go.Height), (button.X, button.Y, button.W, button.H));
                Assert.Equal(("Lab.Go", "PhosphorButton", 1, "Lab", "Go"), (button.Name, button.Type, button.Depth, button.Parent!, button.Text));

                // A control with no Name is its type and its place among siblings of that type.
                var box = Assert.Single(map.Elements, e => e.Id == "Lab/Panel#0");
                Assert.Equal((panel.Left, panel.Top), (box.X, box.Y));
                Assert.Equal("panel", box.Component);
                Assert.Empty(box.Tokens);

                // A child's box adds its parent's: the panel sits away from the corner, so the sum differs from both.
                Assert.True(panel.Left > 0 && panel.Top > 0 && inner.Left > 0 && inner.Top > 0);
                var nested = Assert.Single(map.Elements, e => e.Id == "Lab/Panel#0/Lab.Inner");
                Assert.Equal((panel.Left + inner.Left, panel.Top + inner.Top), (nested.X, nested.Y));
                Assert.Equal((2, "Lab/Panel#0"), (nested.Depth, nested.Parent!));
            }
            finally { ShowWindow(form.Handle, 0); }
        });
    }

    [Fact]
    public void Map_leaves_out_what_the_render_does_not_show()
    {
        OnSta(() =>
        {
            var (form, _, panel, _) = Board();
            try
            {
                var map = SurfaceElementMap.Build("lab", form);
                Assert.DoesNotContain(map.Elements, e => e.Name == "Lab.Hidden");
                Assert.DoesNotContain(map.Elements, e => e.Name == "Lab.Empty");
                // Wholly outside its panel, so the panel clips it away.
                Assert.DoesNotContain(map.Elements, e => e.Name == "Lab.Clipped");
                // Under a hidden parent nothing is listed, however visible the child says it is.
                Assert.DoesNotContain(map.Elements, e => e.Name == "Lab.UnderHidden");
                // The last unnamed panel keeps its number although the hidden one before it is not listed.
                Assert.Contains(map.Elements, e => e.Id == "Lab/Panel#0");
                Assert.DoesNotContain(map.Elements, e => e.Id == "Lab/Panel#1");
                Assert.Contains(map.Elements, e => e.Id == "Lab/Panel#2");
                Assert.All(map.Elements, e => Assert.True(e.W > 0 && e.H > 0));
                Assert.Equal(map.Elements.Count, map.Elements.Select(e => e.Id).Distinct().Count());
                Assert.True(panel.Visible);
            }
            finally { ShowWindow(form.Handle, 0); }
        });
    }

    [Fact]
    public void Map_text_is_cut_to_eighty_and_a_masked_box_gives_none()
    {
        OnSta(() =>
        {
            using var secret = new TextBox { UseSystemPasswordChar = true, Text = "not-for-the-map" };
            using var starred = new TextBox { PasswordChar = '*', Text = "not-for-the-map" };
            using var plain = new TextBox { Multiline = true, Text = "  line one\r\nline two " + new string('x', 200) };
            Assert.Equal("", SurfaceElementMap.TextOf(secret));
            Assert.Equal("", SurfaceElementMap.TextOf(starred));
            string text = SurfaceElementMap.TextOf(plain);
            Assert.Equal(SurfaceElementMap.MaxText, text.Length);
            Assert.StartsWith("line one", text);
            Assert.DoesNotContain('\n', text);
        });
    }

    [Fact]
    public void Component_table_names_the_lab_components_and_only_real_tokens()
    {
        var (component, tokens) = SurfaceElementMap.ComponentOf(typeof(PhosphorButton));
        Assert.Equal("button", component);
        foreach (string token in new[] { "radius-btn", "touch-min", "text-size-btn", "color-bone", "color-line-2" }) Assert.Contains(token, tokens);

        Assert.Equal("card", SurfaceElementMap.ComponentOf(typeof(ThreadView)).Component);
        Assert.Equal("card", SurfaceElementMap.ComponentOf(typeof(ThreadCard)).Component);
        Assert.Equal("chip", SurfaceElementMap.ComponentOf(typeof(SuggestionChip)).Component);
        Assert.Equal("chip", SurfaceElementMap.ComponentOf(typeof(ModelChip)).Component);
        Assert.Contains("color-ember", SurfaceElementMap.ComponentOf(typeof(ModelChip)).Tokens);
        Assert.Equal("field", SurfaceElementMap.ComponentOf(typeof(PromptTextBox)).Component);
        Assert.Equal("field", SurfaceElementMap.ComponentOf(typeof(PhosphorField)).Component);
        Assert.Equal("rail", SurfaceElementMap.ComponentOf(typeof(RailButton)).Component);
        Assert.Equal("rail", SurfaceElementMap.ComponentOf(typeof(RailMark)).Component);
        Assert.Equal("led", SurfaceElementMap.ComponentOf(typeof(LedRow)).Component);
        Assert.Equal("select", SurfaceElementMap.ComponentOf(typeof(FluidSelectDrop)).Component);
        Assert.Equal("label", SurfaceElementMap.ComponentOf(typeof(DataLabel)).Component);

        // Anything else is its type name, lowercased, with no tokens claimed for it.
        Assert.Equal(("textbox", 0), (SurfaceElementMap.ComponentOf(typeof(TextBox)).Component, SurfaceElementMap.ComponentOf(typeof(TextBox)).Tokens.Length));

        // A token the table names is one PhosphorTheme resolves, so the lab never shows a name with no value.
        var known = PhosphorTokens.All.Select(t => t.Name).Concat(PhosphorTheme.Locals.Select(l => l.Name)).ToHashSet();
        foreach (var (type, row) in SurfaceElementMap.Components)
            foreach (string token in row.Tokens)
                Assert.True(known.Contains(token), $"{type} lists {token}, which is not a Phosphor token");
    }

    [Fact]
    public void Map_json_has_the_shape_the_lab_reads()
    {
        var map = new SurfaceElementMap("lab", 400, 300, new[]
        {
            new SurfaceElement("Lab", "Lab", "Form", 0, 0, 400, 300, 0, null, "", "form", Array.Empty<string>()),
            new SurfaceElement("Lab/Lab.Go", "Lab.Go", "PhosphorButton", 20, 30, 140, 44, 1, "Lab", "Go", "button", new[] { "radius-btn" }) { Source = new object() },
        });
        using var doc = JsonDocument.Parse(map.ToJson());
        Assert.Equal(new[] { "surface", "width", "height", "elements" }, doc.RootElement.EnumerateObject().Select(p => p.Name));
        var elements = doc.RootElement.GetProperty("elements");
        Assert.Equal(JsonValueKind.Null, elements[0].GetProperty("parent").ValueKind);
        Assert.Equal(new[] { "id", "name", "type", "x", "y", "w", "h", "depth", "parent", "text", "component", "tokens" },
            elements[1].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Lab", elements[1].GetProperty("parent").GetString());
        Assert.Equal("radius-btn", elements[1].GetProperty("tokens")[0].GetString());
        Assert.Equal(44, elements[1].GetProperty("h").GetInt32());
    }

    [Fact]
    public void Colours_read_as_hex_with_alpha_only_when_it_matters()
    {
        Assert.Equal("#dad1b9", SurfaceElementMap.Hex(Color.FromArgb(0xDA, 0xD1, 0xB9)));
        Assert.Equal("#ffffff00", SurfaceElementMap.Hex(Color.Transparent));
        Assert.Null(SurfaceElementMap.Hex(null));
    }

    [Fact]
    public void Ui_commands_parse_to_a_verb_a_surface_and_an_argument()
    {
        Assert.Equal(new UiLabCommand(UiLabVerb.Surfaces), UiLabCommand.Parse("ui:surfaces", out _));
        Assert.Equal(new UiLabCommand(UiLabVerb.Surfaces), UiLabCommand.Parse("UI:Surfaces", out _));

        string png = Path.Combine(UiLabCommand.RenderRoot, "holodex-mods.png");
        var render = UiLabCommand.Parse($"ui:render:holodex-mods:{png}", out string error);
        Assert.Equal("", error);
        // The drive letter's colon stays in the path.
        Assert.Equal(new UiLabCommand(UiLabVerb.Render, "holodex-mods", Path.GetFullPath(png)), render);
        Assert.Equal("hud", UiLabCommand.Parse($"ui:render:HUD:{png}", out _)!.Surface);

        // An element id is a path of names and may hold colons and slashes of its own.
        var element = UiLabCommand.Parse("ui:element:hud:Hud/Panel#0/Hud.Send:primary", out error);
        Assert.Equal("", error);
        Assert.Equal(new UiLabCommand(UiLabVerb.Element, "hud", "Hud/Panel#0/Hud.Send:primary"), element);
    }

    [Fact]
    public void Ui_commands_refuse_what_does_not_parse_and_any_path_outside_the_lab_folder()
    {
        void Refused(string cmd, string start)
        {
            Assert.Null(UiLabCommand.Parse(cmd, out string error));
            Assert.StartsWith(start, error);
        }

        Refused("ui:", "error:ui:use surfaces");
        Refused("ui:paint:hud", "error:ui:use surfaces");
        Refused("ui:render", "error:ui:render:surface name required");
        Refused("ui:render:bad name:x.png", "error:ui:render:surface name required");
        Refused("ui:element:hud", "error:ui:element:element id required");
        Refused("ui:element:hud:  ", "error:ui:element:element id required");

        Refused("ui:render:hud", "error:ui:render:renders are .png files under");
        Refused($"ui:render:hud:{Path.Combine(Path.GetTempPath(), "hud.png")}", "error:ui:render:renders are .png files under");
        Refused($"ui:render:hud:{Path.Combine(UiLabCommand.RenderRoot, "..", "..", "hud.png")}", "error:ui:render:renders are .png files under");
        Refused($"ui:render:hud:{Path.Combine(UiLabCommand.RenderRoot, "hud.cmd")}", "error:ui:render:renders are .png files under");
        Refused($"ui:render:hud:{UiLabCommand.RenderRoot}-elsewhere{Path.DirectorySeparatorChar}hud.png", "error:ui:render:renders are .png files under");
        Refused("ui:render:hud:hud.png", "error:ui:render:renders are .png files under");

        Assert.True(UiLabCommand.PathAllowed(Path.Combine(UiLabCommand.RenderRoot, "hud.PNG")));
        Assert.False(UiLabCommand.PathAllowed(""));
    }

    /// <summary>
    /// A borderless board, shown off-screen without activation so its children report the visibility a render sees:
    /// a button, an unnamed panel holding a button and a control pushed outside it, and controls a render never shows.
    /// </summary>
    private static (Form Form, PhosphorButton Go, Panel Panel, PhosphorButton Inner) Board()
    {
        // Not disposed: closing a form from a test thread can wait on SystemEvents bound to another test's ended thread.
        var form = new Form
        {
            Name = "Lab", FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(400, 300)
        };
        var go = new PhosphorButton("Go") { Name = "Lab.Go", Location = new Point(20, 30) };
        var hiddenPanel = new Panel { Visible = false, Bounds = new Rectangle(0, 0, 50, 50) };
        hiddenPanel.Controls.Add(new Label { Name = "Lab.UnderHidden", Text = "under", Bounds = new Rectangle(2, 2, 20, 20) });
        var emptyPanel = new Panel { Name = "Lab.Empty", Bounds = new Rectangle(300, 10, 0, 0) };
        var panel = new Panel { Bounds = new Rectangle(50, 100, 200, 120) };
        var inner = new PhosphorButton("Inner") { Name = "Lab.Inner", Location = new Point(10, 15), Width = 100 };
        panel.Controls.Add(inner);
        panel.Controls.Add(new Label { Name = "Lab.Clipped", Text = "clipped", Bounds = new Rectangle(500, 500, 40, 20) });
        var last = new Panel { Bounds = new Rectangle(260, 100, 40, 40) };
        // Add order is the order ids count unnamed siblings in: panel is Panel#0, the hidden one Panel#1, last Panel#2.
        form.Controls.Add(go);
        form.Controls.Add(panel);
        form.Controls.Add(hiddenPanel);
        form.Controls.Add(emptyPanel);
        form.Controls.Add(last);
        form.Controls.Add(new DataLabel("hidden") { Name = "Lab.Hidden", Dock = DockStyle.None, Visible = false, Bounds = new Rectangle(20, 240, 80, 18) });
        form.CreateControl();
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        form.PerformLayout();
        return (form, go, panel, inner);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void OnSta(Action work)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
