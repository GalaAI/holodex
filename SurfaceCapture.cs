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
        var orb = trayMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.DropDownItems.Count > 0 && (i.Text ?? string.Empty).Contains("orb", StringComparison.OrdinalIgnoreCase));
        if (orb != null)
        {
            using (Render(trayMenu)) { }
            using (Render(orb.DropDown)) { }
            orb.Select();
            Save(Render(trayMenu), Path.Combine(dir, "menu-orb-lit.png"), written);
            orb.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault()?.Select();
            Save(Render(orb.DropDown), Path.Combine(dir, "menu-orb-submenu.png"), written);
            orb.DropDown.Close();
            trayMenu.Close();
        }

        var settings = trayMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.DropDownItems.Count > 0 && (i.Text ?? string.Empty).Contains("Settings", StringComparison.OrdinalIgnoreCase));
        if (settings != null)
        {
            using (Render(settings.DropDown)) { }
            settings.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault()?.Select();
            Save(Render(settings.DropDown), Path.Combine(dir, "menu-settings-submenu.png"), written);

            var spells = settings.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.DropDownItems.Count > 0 && (i.Text ?? string.Empty).Contains("gestures", StringComparison.OrdinalIgnoreCase));
            if (spells != null)
            {
                using (Render(spells.DropDown)) { }
                Save(Render(spells.DropDown), Path.Combine(dir, "menu-spells-submenu.png"), written);
                spells.DropDown.Close();
            }

            var dpi = settings.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.DropDownItems.Count > 0 && (i.Text ?? string.Empty).Contains("DPI", StringComparison.OrdinalIgnoreCase));
            if (dpi != null)
            {
                using (Render(dpi.DropDown)) { }
                Save(RenderDpiSubmenuSheet(dpi), Path.Combine(dir, "menu-dpi-submenu.png"), written);
                dpi.DropDown.Close();
            }

            settings.DropDown.Close();
            trayMenu.Close();
        }

        // Render a composite sheet of the 240Hz ArkApertureMorphEngine states
        Save(RenderCursorMorphSheet(), Path.Combine(dir, "cursor-morph-sheet.png"), written);

        // Toasts: one per tone, built by the real class with the copy the app raises.
        Save(PhosphorToast.RenderPreview("Omniwand Active", "Actions Ring casts the eight Spell Orb slots. Right-click the tray mark for settings.", "PHOSPHOR SHELL // GRIMX WAND", ToastTone.Info), Path.Combine(dir, "toast-info.png"), written);
        Save(PhosphorToast.RenderPreview("Cast: Quick Cast", "Quick Cast dispatched through Input Spine.", "GRIMX SPELL // CAST", ToastTone.Success), Path.Combine(dir, "toast-success.png"), written);
        Save(PhosphorToast.RenderPreview("Ritual not cast", "No Input Spine binding for grimx/ritual, so nothing was cast. Bind it in Input Studio.", "GRIMX SPELL // PERFORM", ToastTone.Warning), Path.Combine(dir, "toast-warning.png"), written);
        Save(PhosphorToast.RenderPreview("Ritual not cast", "Input Spine (:8842) is unreachable, so nothing was cast.", "GRIMX SPELL // PERFORM", ToastTone.Error), Path.Combine(dir, "toast-error.png"), written);

        foreach (var (name, form) in widgets)
        {
            Save(RenderForm(form), Path.Combine(dir, $"{name}.png"), written);
            if (form is HolodexLiteDrawerForm holodex)
            {
                holodex.SwitchTab("mods");
                Save(RenderForm(holodex), Path.Combine(dir, "holodex-mods.png"), written);
                holodex.SwitchTab("session");
            }
        }

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
        if (form is ScreenSnipAndElementPickerForm snipForm)
        {
            var homeSnip = snipForm.Location;
            try
            {
                snipForm.Location = new Point(-32000, -32000);
                snipForm.CreateControl();
                ShowWindow(snipForm.Handle, 4); // SW_SHOWNOACTIVATE
                snipForm.PerformLayout();
                snipForm.Refresh();
                using var baseSnap = Render(snipForm);
                return snipForm.OverlayCanvasPreview(baseSnap);
            }
            finally
            {
                ShowWindow(snipForm.Handle, 0);
                snipForm.Location = homeSnip;
            }
        }
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

    private static Bitmap RenderDpiSubmenuSheet(ToolStripMenuItem dpiMenu)
    {
        var dd = dpiMenu.DropDown;
        int w = Math.Max(248, dd.Width);
        int h = Math.Max(228, dd.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PhosphorPaint.DrawPanel(g, new Rectangle(0, 0, w, h), PhosphorTheme.RadiusButton, PhosphorTheme.Surface, PhosphorTheme.Line2, PhosphorTheme.Surface, 1f);
            PhosphorPaint.DrawEyebrow(g, "MOUSE DPI // SPEED", PhosphorTheme.Ghost, 12f, 8f, 1f);

            var host = dd.Items.OfType<ToolStripControlHost>().FirstOrDefault();
            if (host?.Control is PhosphorDpiSlider slider)
            {
                var st = g.Save();
                g.TranslateTransform(10, 26);
                slider.PaintSlider(g);
                g.Restore(st);
            }

            using var sepPen = new Pen(PhosphorTheme.Line, 1f);
            g.DrawLine(sepPen, 10, 78, w - 10, 78);

            using var itemFont = PhosphorFonts.Ui(PhosphorTheme.TextData);
            using var monoFont = PhosphorFonts.Mono(PhosphorTheme.TextData);
            using var boneBrush = new SolidBrush(PhosphorTheme.Bone);
            using var ghostBrush = new SolidBrush(PhosphorTheme.Ghost);

            int ry = 84;
            foreach (var item in dd.Items.OfType<ToolStripMenuItem>())
            {
                g.DrawString(item.Text ?? "", itemFont, boneBrush, 12f, ry + 3f);
                string sc = (item.ShortcutKeyDisplayString ?? "").TrimEnd('\u2007');
                if (!string.IsNullOrEmpty(sc))
                {
                    var sz = g.MeasureString(sc, monoFont);
                    g.DrawString(sc, monoFont, ghostBrush, w - sz.Width - 12f, ry + 3f);
                }
                ry += 23;
            }
        }
        return bmp;
    }

    private static Bitmap RenderCursorMorphSheet()
    {
        var bmp = new Bitmap(880, 160, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.Clear(PhosphorTheme.Ground);

        var states = new (string Label, WindowsCursorRole Role, bool Thinking)[]
        {
            ("ARROW · PATINA", WindowsCursorRole.Arrow, false),
            ("WIZ / HAND · CORAL", WindowsCursorRole.Hand, true),
            ("IBEAM · PATINA BAR", WindowsCursorRole.IBeam, false),
            ("CROSSHAIR", WindowsCursorRole.Crosshair, false),
            ("RESIZE WE", WindowsCursorRole.SizeWE, false),
            ("MOVE 4-WAY", WindowsCursorRole.SizeAll, false),
            ("WAIT · TURBINE", WindowsCursorRole.Wait, false),
            ("BLOCKED · RUST", WindowsCursorRole.No, false)
        };

        using var labelFont = PhosphorFonts.Mono(8.5f);
        using var labelBrush = new SolidBrush(PhosphorTheme.Dim);
        using var cardBrush = new SolidBrush(PhosphorTheme.Surface);
        using var borderPen = new Pen(PhosphorTheme.Line, 1f);
        var sf = new StringFormat { Alignment = StringAlignment.Center };

        for (int i = 0; i < states.Length; i++)
        {
            var (label, role, thinking) = states[i];
            int x = 14 + i * 107;
            var cardRect = new Rectangle(x, 14, 98, 132);
            g.FillRectangle(cardBrush, cardRect);
            g.DrawRectangle(borderPen, cardRect);

            // Settle the morph state to target before drawing
            for (int step = 0; step < 32; step++)
            {
                ArkApertureMorphEngine.Step(0.04, role, thinking, 30f);
            }

            float cx = x + 49f;
            float cy = (role is WindowsCursorRole.Arrow or WindowsCursorRole.Hand) ? 50f : 64f;
            ArkApertureMorphEngine.DrawMorphedApertureCursor(g, cx, cy, -18f, 1.45f, thinking, role, 30f);

            g.DrawString(label, labelFont, labelBrush, new RectangleF(x + 2, 118, 94, 24), sf);
        }

        // Restore Arrow state after sheet render
        for (int step = 0; step < 32; step++)
        {
            ArkApertureMorphEngine.Step(0.04, WindowsCursorRole.Arrow, false, 0f);
        }

        return bmp;
    }

    private static void Save(Bitmap bmp, string path, List<string> written)
    {
        using (bmp) bmp.Save(path, ImageFormat.Png);
        written.Add(Path.GetFileName(path));
    }
}
