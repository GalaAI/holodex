using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// As-built captures: the running app draws its real surfaces offscreen to PNG (pipe command
/// <c>render:&lt;folder&gt;</c>), so design boards can show what actually ships instead of a redrawing.
/// Every capture comes from the live objects (the tray menu, the widget forms) or the real classes (toasts),
/// clipped to each window's region so the rounded corners match the screen. Runs on the UI thread.
/// </summary>
internal static class SurfaceCapture
{
    public static IReadOnlyList<string> CaptureAll(string dir, ContextMenuStrip trayMenu, params (string Name, Form Form)[] widgets)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();

        // Tray menu at rest, then with the orb row lit and its submenu open on its first row.
        Save(Render(trayMenu), Path.Combine(dir, "menu.png"), written);
        var orb = trayMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.DropDownItems.Count > 0 && i.Text.Contains("orb", StringComparison.OrdinalIgnoreCase));
        if (orb != null)
        {
            // Paint each row once at rest so the renderer has measured its label before it is lit.
            using (Render(trayMenu)) { }
            using (Render(orb.DropDown)) { }
            orb.Select();
            Save(Render(trayMenu), Path.Combine(dir, "menu-orb-lit.png"), written);
            orb.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault()?.Select();
            Save(Render(orb.DropDown), Path.Combine(dir, "menu-orb-submenu.png"), written);
            orb.DropDown.Close();
            trayMenu.Close();
        }

        // Toasts: one per tone, built by the real class with the copy the app raises.
        Save(PhosphorToast.RenderPreview("Omniwand Active", "Actions Ring casts the eight GrimX Flow slots. Right-click the tray mark for settings.", "PHOSPHOR SHELL // GRIMX WAND", ToastTone.Info), Path.Combine(dir, "toast-info.png"), written);
        Save(PhosphorToast.RenderPreview("Spell cast", "Cast Spell dispatched through Input Spine.", "GRIMX FLOW // CAST", ToastTone.Success), Path.Combine(dir, "toast-success.png"), written);
        Save(PhosphorToast.RenderPreview("Ritual not cast", "No Input Spine binding for grimx/ritual, so nothing was cast. Bind it in Input Studio.", "GRIMX FLOW // PERFORM", ToastTone.Warning), Path.Combine(dir, "toast-warning.png"), written);
        Save(PhosphorToast.RenderPreview("Ritual not cast", "Input Spine (:8842) is unreachable, so nothing was cast.", "GRIMX FLOW // PERFORM", ToastTone.Error), Path.Combine(dir, "toast-error.png"), written);

        foreach (var (name, form) in widgets)
            Save(RenderForm(form), Path.Combine(dir, $"{name}.png"), written);

        return written;
    }

    /// <summary>Draws a control offscreen and clips it to its window region (transparent outside).</summary>
    public static Bitmap Render(Control control)
    {
        // DrawToBitmap needs real windows for the control and every child; a widget that was never shown has
        // neither, and drew as an empty panel.
        control.CreateControl();
        CreateChildren(control);
        control.PerformLayout();
        int w = Math.Max(1, control.Width), h = Math.Max(1, control.Height);
        using var raw = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        control.DrawToBitmap(raw, new Rectangle(0, 0, w, h));
        var clipped = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(clipped))
        {
            g.Clear(Color.Transparent);
            if (control.Region != null) g.SetClip(control.Region, CombineMode.Replace);
            g.DrawImageUnscaled(raw, 0, 0);
        }
        return clipped;
    }

    /// <summary>
    /// A hidden form's children report themselves invisible and skip WM_PRINT, so a never-shown widget drew
    /// as an empty panel. Show it far off-screen without activating it (no focus taken from the operator),
    /// draw it, then hide it and put it back. A widget that is already open is drawn where it is.
    /// </summary>
    private static Bitmap RenderForm(Form form)
    {
        if (form.Visible) return Render(form);
        var home = form.Location;
        try
        {
            form.Location = new Point(-32000, -32000);
            form.CreateControl();
            ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
            form.PerformLayout();
            form.Refresh();
            return Render(form);
        }
        finally
        {
            ShowWindow(form.Handle, 0); // SW_HIDE
            form.Location = home;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void CreateChildren(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            _ = child.Handle;
            CreateChildren(child);
        }
    }

    private static void Save(Bitmap bmp, string path, List<string> written)
    {
        using (bmp) bmp.Save(path, ImageFormat.Png);
        written.Add(Path.GetFileName(path));
    }
}
