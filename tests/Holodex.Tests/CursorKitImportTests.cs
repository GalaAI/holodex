using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Holodex;
using Xunit;

namespace Holodex.Tests;

/// <summary>Importing a shared Cursor Kit: what it merges and which commands the user must confirm (ruling 2026-11-09).</summary>
public class CursorKitImportTests
{
    private static CursorKit Kit(string actions, string bindings = "[]") =>
        CursorKit.Parse($$"""{"actions":{{actions}},"bindings":{{bindings}}}""");

    [Fact]
    public void Incoming_actions_and_bindings_replace_same_ids_and_keep_the_rest()
    {
        var current = Kit("""[{"id":"a","name":"Mine","kind":"open","target":"https://a.example"},{"id":"b","name":"B","kind":"type","text":"hi"}]""",
            """[{"id":"x","trigger":{"kind":"hotkey","match":"CTRL+1"},"actionId":"a"}]""");
        var incoming = Kit("""[{"id":"a","name":"Theirs","kind":"open","target":"https://b.example"}]""",
            """[{"id":"y","trigger":{"kind":"hotkey","match":"CTRL+2"},"actionId":"a"}]""");
        var merged = CursorKitImport.Merge(current, incoming);
        Assert.Equal(new[] { "b", "a" }, merged.Actions.Select(a => a.Id));
        Assert.Equal("Theirs", merged.Actions.Single(a => a.Id == "a").Name);
        Assert.Equal(new[] { "x", "y" }, merged.Bindings.Select(b => b.Id));
    }

    [Fact]
    public void Only_commands_this_pc_does_not_already_have_need_confirming()
    {
        var current = Kit("""[{"id":"a","name":"A","kind":"shell","command":"Get-Date"}]""");
        var incoming = Kit("""
            [{"id":"b","name":"B","kind":"shell","command":"Get-Date"},
             {"id":"c","name":"C","kind":"shell","command":"Remove-Item C:\\temp -Recurse"},
             {"id":"d","name":"D","kind":"open","target":"tool.exe"},
             {"id":"e","name":"E","kind":"open","target":"https://example.com"}]
            """);
        var code = CursorKitImport.NewCode(current, CursorKitImport.Merge(current, incoming));
        Assert.Equal(new[] { "Remove-Item C:\\temp -Recurse", "open tool.exe" }, code);
    }

    /// <summary>The Cursor Mod panel and its confirmation dialog build and draw; HOLODEX_RENDER=folder saves the PNGs.</summary>
    [Fact]
    public void The_cursor_mod_panel_and_confirm_dialog_render()
    {
        string? folder = Environment.GetEnvironmentVariable("HOLODEX_RENDER");
        string dir = Path.Combine(Path.GetTempPath(), "holodex-cursormod-" + Guid.NewGuid().ToString("N"));
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                PhosphorFonts.EnsureLoaded();
                var gate = new ShellGate(dir);
                var store = new KitStore(Path.Combine(dir, "kit.json"));
                foreach (bool on in new[] { false, true })
                {
                    if (on) gate.TurnOn();
                    using var panel = new CursorModPanel(gate, store) { Size = new Size(326, 140) };
                    panel.CreateControl();
                    using var bmp = new Bitmap(panel.Width, panel.Height);
                    panel.DrawToBitmap(bmp, new Rectangle(Point.Empty, panel.Size));
                    if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); bmp.Save(Path.Combine(folder, $"cursor-mod-panel-{(on ? "on" : "off")}.png"), ImageFormat.Png); }
                }
                foreach (bool mono in new[] { false, true })
                {
                    using var form = new CursorModConfirmForm("Import 2 commands?", "HOLODEX // CURSOR MOD // IMPORT",
                        "3 actions and 1 binding from kit.json. It adds these commands:", "Get-Date\r\n\r\nopen tool.exe", "Import", mono);
                    form.CreateControl();
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                    if (!string.IsNullOrEmpty(folder)) bmp.Save(Path.Combine(folder, $"cursor-mod-confirm-{(mono ? "import" : "shell")}.png"), ImageFormat.Png);
                }
            }
            catch (Exception ex) { error = ex; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
