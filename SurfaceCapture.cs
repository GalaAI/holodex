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

        // The menu as it opens from the tray, one sheet per submenu, placed on a 1920x1080 screen.
        written.AddRange(CaptureTrayComposites(dir, trayMenu));

        // Render a composite sheet of the 240Hz ArkApertureMorphEngine states
        Save(RenderCursorMorphSheet(), Path.Combine(dir, "cursor-morph-sheet.png"), written);

        // Toasts: every entry the toast catalog keeps or integrates, built by the real class with the copy the app raises.
        (string File, string Title, string Body, string Eyebrow, ToastTone Tone)[] toasts =
        {
            ("toast-info", "Omniwand Active", "Actions Ring casts the eight Spell Orb slots. Right-click the tray mark for settings.", "PHOSPHOR SHELL // GRIMX WAND", ToastTone.Info),
            ("toast-success", "Cast: Quick Cast", "Quick Cast dispatched through Input Spine.", "GRIMX SPELL // CAST", ToastTone.Success),
            ("toast-warning", "Ritual not cast", "No Input Spine binding for grimx/ritual, so nothing was cast. Bind it in Input Studio.", "GRIMX SPELL // PERFORM", ToastTone.Warning),
            ("toast-error", "Input Spine unreachable", "Input Spine (:8842) is unreachable, so nothing was cast. Start the Ark runtime and cast again.", "GRIMX SPELL // CAST", ToastTone.Error),
            ("toast-cursor-profile", "Cursor profile", "Active Input Studio profile: Precision", "MX MASTER 4", ToastTone.Info),
            ("toast-vr-mode", "VR mode", "Air Link detected. Native cursor hidden, wand enlarged.", "OMNIWAND // VR", ToastTone.Info),
            ("toast-gateway", "Gateway unavailable", "No inbound key in Keymaster, so the HUD asks Nemotron directly through Token Factory. Wand and ring still work.", "GATEWAY // KEYMASTER", ToastTone.Info),
            ("toast-ghosttrailer", "GhostTrailer on", "Streaming the ribbon. F16 stops it.", "GHOSTTRAILER // F16", ToastTone.Info),
            ("toast-cursor-shown", "Windows cursor shown", "Native cursor restored. Ctrl+Alt+C hides it again.", "OMNIWAND // CURSOR", ToastTone.Info),
            ("toast-intake", "Intake submitted", "Handoff Scroll sent to Eden and its inbox pulsed. Seal sha256:3f9a1c07b2de.", "AINBOX // POD EDEN", ToastTone.Success),
            ("toast-crafted", "Ritual crafted", "Ritual drafted in Wiz. Bind it to a ring slot in Input Studio.", "WIZ // 4GE BENCH", ToastTone.Success),
        };
        foreach (var t in toasts)
            Save(PhosphorToast.RenderPreview(t.Title, t.Body, t.Eyebrow, t.Tone), Path.Combine(dir, $"{t.File}.png"), written);

        foreach (var (name, form) in widgets)
        {
            Save(RenderForm(form), Path.Combine(dir, $"{name}.png"), written);
            if (form is HolodexLiteDrawerForm holodex)
            {
                foreach (var tab in new[] { "ainbox", "codex", "grimx", "mods" })
                {
                    holodex.SwitchTab(tab);
                    Save(RenderForm(holodex), Path.Combine(dir, $"holodex-{tab}.png"), written);
                }
                holodex.SwitchTab("codex");
                holodex.PreviewState("codex");
                Save(RenderForm(holodex), Path.Combine(dir, "holodex-codex-entry.png"), written);
                holodex.PreviewState("reset");
                holodex.SwitchTab("session");
            }
            if (form is ScreenSnipAndElementPickerForm snip)
            {
                snip.PreviewState("answered");
                Save(RenderForm(snip), Path.Combine(dir, "snip-answer.png"), written);
                snip.PreviewState("reset");
            }
            if (form is OmniwandHudForm hud)
            {
                // A question with the screen and its answer, as cards.
                hud.PreviewState("answered");
                Save(RenderForm(hud), Path.Combine(dir, "hud-answer.png"), written);
                hud.PreviewState("reset");
            }
            if (form is WizCraftingWidgetForm wiz)
            {
                // The agent mid-run: steps, an action and its result, then a work order waiting on Approve/Skip.
                wiz.PreviewState("agent");
                Save(RenderForm(wiz), Path.Combine(dir, "wiz-agent.png"), written);
                wiz.PreviewState("reset");
            }
            if (form is AInboxIntakeWidgetForm ainbox)
            {
                // The composer's other states: a file dragged over (TraceBeam mid-orbit), one capture, a two-context
                // stack with predictions and the Aegis gate, and that stack with its scroll open for review.
                ainbox.PreviewState("drag");
                Save(RenderForm(ainbox), Path.Combine(dir, "ainbox-drag.png"), written);
                ainbox.PreviewState("captured");
                Save(RenderForm(ainbox), Path.Combine(dir, "ainbox-captured.png"), written);
                ainbox.PreviewState("stack");
                Save(RenderForm(ainbox), Path.Combine(dir, "ainbox-stack.png"), written);
                ainbox.PreviewState("review");
                Save(RenderForm(ainbox), Path.Combine(dir, "ainbox-review.png"), written);
                ainbox.PreviewState("reset");
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
                snipForm.PreviewAllMarks = true; // the lasso and the element card on one board
                snipForm.CreateControl();
                ShowWindow(snipForm.Handle, 4); // SW_SHOWNOACTIVATE
                snipForm.PerformLayout();
                snipForm.Refresh();
                return Render(snipForm);
            }
            finally
            {
                snipForm.PreviewAllMarks = false;
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

    /// <summary>The screen the tray composites are placed on: 1920x1080 with a 48px taskbar along the bottom.</summary>
    internal static readonly Rectangle TrayScreen = new(0, 0, 1920, 1080);
    internal static readonly Rectangle TrayWorkArea = new(0, 0, 1920, 1032);

    /// <summary>Pointer on the Omniwand tray icon, near the clock at the bottom-right.</summary>
    internal static readonly Point TrayAnchor = new(1790, 1056);

    /// <summary>
    /// <c>menu-tray-*.png</c>: the main menu with each submenu open (and each Settings submenu open under
    /// Settings), placed where they open from the tray on <see cref="TrayScreen"/>.
    /// </summary>
    public static IReadOnlyList<string> CaptureTrayComposites(string dir, ContextMenuStrip trayMenu)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var root in trayMenu.Items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
        {
            Save(RenderTrayComposite(trayMenu, new[] { root }, out _), Path.Combine(dir, $"menu-tray-{Slug(root.Text)}.png"), written);
            foreach (var nested in root.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
                Save(RenderTrayComposite(trayMenu, new[] { root, nested }, out _), Path.Combine(dir, $"menu-tray-{Slug(root.Text)}-{Slug(nested.Text)}.png"), written);
        }
        return written;
    }

    /// <summary>
    /// Draws <paramref name="trayMenu"/> with the submenus on <paramref name="path"/> open, placed the way the
    /// app places them: NotifyIcon opens the menu above and left of the pointer, PhosphorTrayMenu moves it left
    /// until the submenu chain fits and up out of the taskbar (<see cref="PhosphorMenuLayout.PlaceRoot"/>), and each submenu opens in
    /// its row's <see cref="ToolStripDropDownItem.DropDownDirection"/>, kept inside the working area the way
    /// WinForms keeps it. <paramref name="panels"/> receives each panel's screen rectangle, root first.
    /// </summary>
    internal static Bitmap RenderTrayComposite(ContextMenuStrip trayMenu, IReadOnlyList<ToolStripMenuItem> path, out List<Rectangle> panels)
    {
        PhosphorMenuLayout.Apply(trayMenu);
        var chain = new List<ToolStripDropDown> { trayMenu };
        chain.AddRange(path.Select(i => i.DropDown));
        foreach (var dd in chain) using (Render(dd)) { }

        var root = new Rectangle(TrayAnchor.X - trayMenu.Width, TrayAnchor.Y - trayMenu.Height, trayMenu.Width, trayMenu.Height);
        root.Location = PhosphorMenuLayout.PlaceRoot(root, PhosphorMenuLayout.Reach(trayMenu, root.Width), TrayWorkArea);
        panels = new List<Rectangle> { root };
        var parent = root;
        foreach (var item in path)
        {
            var row = item.Bounds;
            var sub = item.DropDown;
            int x = item.DropDownDirection == ToolStripDropDownDirection.Left ? parent.X + row.X - sub.Width : parent.X + row.Right;
            parent = Constrain(new Rectangle(x, parent.Y + row.Y, sub.Width, sub.Height), TrayWorkArea);
            panels.Add(parent);
        }

        // Light each open row's parent, as the live menu does while a submenu is open.
        foreach (var item in path) item.Select();
        var shots = chain.Select(Render).ToList();

        int left = Math.Max(TrayScreen.Left, panels.Min(r => r.Left) - 48);
        int top = Math.Max(TrayScreen.Top, panels.Min(r => r.Top) - 48);
        var origin = new Point(left, top);
        var bmp = new Bitmap(TrayScreen.Right - left, TrayScreen.Bottom - top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(PhosphorTheme.Ground);
            using (var bar = new SolidBrush(PhosphorTheme.Surface)) g.FillRectangle(bar, 0, TrayWorkArea.Bottom - top, bmp.Width, TrayScreen.Bottom - TrayWorkArea.Bottom);
            using (var line = new Pen(PhosphorTheme.Line)) g.DrawLine(line, 0, TrayWorkArea.Bottom - top, bmp.Width, TrayWorkArea.Bottom - top);
            PhosphorTheme.DrawArkMark(g, new RectangleF(TrayAnchor.X - left - 10, TrayAnchor.Y - top - 10, 20, 20));
            PhosphorPaint.DrawEyebrow(g, $"Tray menu // {TrayScreen.Width} x {TrayScreen.Height} // screen edge right", PhosphorTheme.Ghost, 16, 14, 1f);
            for (int i = 0; i < shots.Count; i++)
                g.DrawImageUnscaled(shots[i], panels[i].X - origin.X, panels[i].Y - origin.Y);
        }
        foreach (var shot in shots) shot.Dispose();

        // Put the rows back unlit: the menu was never shown, so nothing else clears the selection, and the next
        // capture (or the next time the tray opens it) would draw them lit. Hiding an item unselects it.
        foreach (var item in path)
        {
            item.Available = false;
            item.Available = true;
        }
        for (int i = path.Count - 1; i >= 0; i--) path[i].DropDown.Close();
        trayMenu.Close();
        return bmp;
    }

    /// <summary>WinForms' ConstrainToScreenBounds: slides <paramref name="r"/> inside <paramref name="area"/>.</summary>
    private static Rectangle Constrain(Rectangle r, Rectangle area)
    {
        if (r.Right > area.Right) r.X = area.Right - r.Width;
        if (r.Left < area.Left) r.X = area.Left;
        if (r.Bottom > area.Bottom) r.Y = area.Bottom - r.Height;
        if (r.Top < area.Top) r.Y = area.Top;
        return r;
    }

    private static string Slug(string? text)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\(.*?\)", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(plain.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
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
