using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Holodex;

/// <summary>
/// As-built captures: the running app draws its real surfaces offscreen to PNG (pipe command
/// <c>render:&lt;folder&gt;</c>), so design boards can show what actually ships instead of a redrawing.
/// Every capture comes from the live objects (the tray menu, the widget forms) or the real classes (toasts),
/// clipped to each window's region so the rounded corners match the screen. Runs on the UI thread.
/// </summary>
internal static class SurfaceCapture
{
    /// <summary>The toasts the catalog keeps or integrates, with the copy the app raises; rendered here, shown live by the pipe's toast:show.</summary>
    internal static readonly (string File, string Title, string Body, string Eyebrow, ToastTone Tone)[] ToastCatalog =
    {
        ("toast-info", "Holodex Active", "Actions Ring casts the eight Spell Orb slots. Right-click the tray mark for settings.", "HOLODEX // WAND", ToastTone.Info),
        ("toast-success", "Cast: Quick Cast", "Quick Cast ran. Wiz drafted the Spell on Nemotron.", "SPELL // CAST", ToastTone.Success),
        ("toast-warning", "Ritual not cast", "No Spell is bound to this slot, so nothing was cast. Pick one for the slot and cast again.", "SPELL // PERFORM", ToastTone.Warning),
        ("toast-error", "Not answered", "Nebius Token Factory did not answer within 24 seconds, so nothing was cast. Cast again.", "SPELL // CAST", ToastTone.Error),
        ("toast-cursor-profile", "Cursor profile", "Active Input Studio profile: Precision", "MX MASTER 4", ToastTone.Info),
        ("toast-vr-mode", "VR mode", "Air Link detected. Native cursor hidden, wand enlarged.", "HOLODEX // VR", ToastTone.Info),
        ("toast-gateway", "Gateway unavailable", "No Ark API on this PC, so the HUD asks Nemotron directly through Token Factory. Wand and ring still work.", "NEMOTRON // TOKEN FACTORY", ToastTone.Info),
        ("toast-ghosttrailer", "GhostTrailer on", "Streaming the ribbon. F16 stops it.", "GHOSTTRAILER // F16", ToastTone.Info),
        ("toast-cursor-shown", "Windows cursor shown", "Native cursor restored. Ctrl+Alt+C hides it again.", "HOLODEX // CURSOR", ToastTone.Info),
        ("toast-intake", "Intake submitted", "Handoff saved to your Holodex library. Seal sha256:3f9a1c07b2de.", "AINBOX // POD EDEN", ToastTone.Success),
        ("toast-crafted", "Ritual crafted", "Ritual drafted in Wiz. Bind it to a ring slot in Input Studio.", "WIZ // 4GE BENCH", ToastTone.Success),
    };

    /// <summary>
    /// One surface the app can draw on request: its file name without the extension, and how to draw it. Draw stages
    /// the state the surface is captured in, renders it, hands the root control to the inspector while it is still
    /// shown (so the element map reads the tree the bitmap came from) and puts the live widget back. A surface that
    /// is painted, not built from controls (a toast preview, a tray composite, a sheet), never calls the inspector
    /// and so gets no element map.
    /// </summary>
    internal sealed record CaptureSurface(string Name, Func<Action<Control>?, Bitmap> Draw);

    /// <summary>
    /// Every surface the as-built render draws, in the order it draws them. <c>render:&lt;folder&gt;</c> walks the
    /// whole list; the UI Lab bridge (<c>ui:render</c>, <c>ui:element</c>) picks one by name, so one surface is staged
    /// the same way whether it is drawn alone or with the rest. Each entry restores what it changed.
    /// </summary>
    public static IReadOnlyList<CaptureSurface> Surfaces(PhosphorTrayMenu trayMenu, params (string Name, Form Form)[] widgets)
    {
        var list = new List<CaptureSurface>();

        // Tray menu: the home page and every page it turns to, each as the panel alone (menu*.png) and as it opens
        // above the tray icon on a 1920x1080 screen (menu-tray*.png).
        foreach (var page in trayMenu.Home.AndBelow().ToList())
        {
            string slug = page == trayMenu.Home ? string.Empty : "-" + Slug(page.Title);
            list.Add(new($"menu{slug}", inspect => OnTrayPage(trayMenu, page, () => Inspected(RenderMenu(trayMenu), trayMenu, inspect))));
            list.Add(new($"menu-tray{slug}", _ => OnTrayPage(trayMenu, page, () => RenderTrayComposite(trayMenu, out Rectangle _))));
        }

        // Render a composite sheet of the 240Hz ArkApertureMorphEngine states
        list.Add(new("cursor-morph-sheet", _ => RenderCursorMorphSheet()));

        // The AI card kit as it stands: every state and footer the surfaces compose, drawn by the real classes.
        // Two sheets: a form cannot be taller than the screen, and the kit is.
        list.Add(new("kit-ai-components", inspect => RenderAiKitSheet(approvals: false, inspect)));
        list.Add(new("kit-ai-approvals", inspect => RenderAiKitSheet(approvals: true, inspect)));

        // Toasts: every entry the toast catalog keeps or integrates, built by the real class with the copy the app raises.
        foreach (var t in ToastCatalog)
            list.Add(new(t.File, _ => PhosphorToast.RenderPreview(t.Title, t.Body, t.Eyebrow, t.Tone)));

        // The approval dialogs: a command Nemotron wants to run, turning shell actions on, and a Cursor Kit import.
        list.Add(new("dialog-command-approval", inspect =>
        {
            using var approval = new CommandApprovalForm("Get-ChildItem $env:USERPROFILE\\Downloads | Sort-Object Length -Descending | Select-Object -First 5", "Finds the five largest files in Downloads, as you asked.");
            return RenderForm(approval, inspect);
        }));
        list.Add(new("dialog-shell-on", inspect =>
        {
            using var shellOn = new CursorModConfirmForm("Turn shell actions on?", "HOLODEX // CURSOR MOD // SHELL ACTIONS",
                "Cursor Kit buttons will be able to run commands and open programs on this PC.",
                "Only a caller with this PC's token can run them (the Stream Deck and the AHK bridge read it from your Holodex folder). Every run is logged. Turning them off again deletes the token.",
                "Turn on");
            return RenderForm(shellOn, inspect);
        }));
        list.Add(new("dialog-kit-import", inspect =>
        {
            using var import = new CursorModConfirmForm("Import 2 commands?", "HOLODEX // CURSOR MOD // IMPORT // CONFIRM EACH COMMAND",
                "3 actions and 1 binding from studio-kit.json. Actions and bindings with the same id are replaced. It adds these commands:",
                "Get-Date" + Environment.NewLine + Environment.NewLine + "open C:\\tools\\obs-scene.bat", "Import", mono: true);
            return RenderForm(import, inspect);
        }));

        foreach (var (name, form) in widgets)
        {
            if (form is VaultForm vaultForm)
            {
                // Both tabs, each sized to what it holds.
                list.Add(new("vault", inspect => { vaultForm.ShowTab(catalog: false); return RenderForm(vaultForm, inspect); }));
                list.Add(new("vault-catalog", inspect => { vaultForm.ShowTab(catalog: true); return RenderForm(vaultForm, inspect); }));
                continue;
            }
            list.Add(new(name, inspect => RenderForm(form, inspect)));
            if (form is HolodexLiteDrawerForm holodex)
            {
                // The session as a new user meets it (first steps) and once a conversation exists.
                list.Add(Staged("holodex-first-steps", holodex, () => holodex.PreviewState("session-empty"), () => holodex.PreviewState("reset")));
                list.Add(Staged("holodex-conversation", holodex, () => holodex.PreviewState("session-conversation"), () => holodex.PreviewState("reset")));
                foreach (var tab in new[] { "ainbox", "codex", "grimx", "mods" })
                    list.Add(Staged($"holodex-{tab}", holodex, () => holodex.SwitchTab(tab), () => holodex.SwitchTab("session")));
                list.Add(Staged("holodex-codex-entry", holodex,
                    () => { holodex.SwitchTab("codex"); holodex.PreviewState("codex"); },
                    () => { holodex.PreviewState("reset"); holodex.SwitchTab("session"); }));
            }
            if (form is ScreenSnipAndElementPickerForm snip)
                list.Add(Staged("snip-answer", snip, () => snip.PreviewState("answered"), () => snip.PreviewState("reset")));
            if (form is HolodexHudForm hud)
            {
                // A question with the screen, then the card it holds while asking, answered and failed.
                foreach (var (state, file) in new[] { ("asking", "hud-asking"), ("answered", "hud-answer"), ("failed", "hud-failed") })
                    list.Add(Staged(file, hud, () => hud.PreviewState(state), () => hud.PreviewState("reset")));
            }
            if (form is WizCraftingWidgetForm wiz)
            {
                // The agent mid-run: steps, an action and its result, then a work order waiting on Approve/Skip.
                list.Add(Staged("wiz-agent", wiz, () => wiz.PreviewState("agent"), () => wiz.PreviewState("reset")));
            }
            if (form is AInboxIntakeWidgetForm ainbox)
            {
                // The composer's other states: a file dragged over (TraceBeam mid-orbit), one capture, a two-context
                // stack with predictions and the Aegis gate, and that stack with its scroll open for review.
                foreach (var state in new[] { "drag", "captured", "stack", "review" })
                    list.Add(Staged($"ainbox-{state}", ainbox, () => ainbox.PreviewState(state), () => ainbox.PreviewState("reset")));
            }
        }

        return list;
    }

    /// <summary>A widget drawn in a preview state: staged, rendered, then put back even when the render throws.</summary>
    private static CaptureSurface Staged(string name, Form form, Action stage, Action restore) => new(name, inspect =>
    {
        stage();
        try { return RenderForm(form, inspect); }
        finally { restore(); }
    });

    /// <summary>Turns the tray menu to <paramref name="page"/> for one draw, then back to its home page.</summary>
    private static Bitmap OnTrayPage(PhosphorTrayMenu trayMenu, TrayPage page, Func<Bitmap> draw)
    {
        trayMenu.ShowPage(page);
        try { return draw(); }
        finally { trayMenu.ShowPage(trayMenu.Home); }
    }

    /// <summary>Lets the inspector read <paramref name="root"/> while it still stands as <paramref name="bmp"/> drew it.</summary>
    private static Bitmap Inspected(Bitmap bmp, Control root, Action<Control>? inspect)
    {
        try { inspect?.Invoke(root); }
        catch { bmp.Dispose(); throw; }
        return bmp;
    }

    /// <summary>Draws every surface into <paramref name="dir"/>, each with its element map beside it when it has one. Returns the PNG names.</summary>
    public static IReadOnlyList<string> CaptureAll(string dir, PhosphorTrayMenu trayMenu, params (string Name, Form Form)[] widgets)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var surface in Surfaces(trayMenu, widgets))
        {
            Capture(surface, Path.Combine(dir, $"{surface.Name}.png"));
            written.Add($"{surface.Name}.png");
        }
        return written;
    }

    /// <summary>
    /// Draws one surface to <paramref name="pngPath"/> and writes its element map as the .json beside it. A surface
    /// with no control tree leaves no map, and a map from an earlier capture of that name is removed so a stale one
    /// cannot be read against the new picture.
    /// </summary>
    internal static (Size Size, string? MapPath) Capture(CaptureSurface surface, string pngPath)
    {
        // Every state is drawn as it settles: a surface easing open or resizing would be caught mid-tween.
        using var still = SurfaceMotion.Instant();
        SurfaceElementMap? map = null;
        using var bmp = surface.Draw(root => map = SurfaceElementMap.Build(surface.Name, root));
        return (bmp.Size, Write(bmp, map, pngPath));
    }

    /// <summary>
    /// One element of a surface as it stands once the surface is staged: the live control's properties as JSON, or
    /// null when the surface has no element with that id. The surface is drawn the way a capture draws it, because
    /// a hidden widget's controls have no settled layout or visibility until then.
    /// </summary>
    internal static string? DescribeElement(CaptureSurface surface, string elementId)
    {
        using var still = SurfaceMotion.Instant();
        string? json = null;
        using var bmp = surface.Draw(root => json = SurfaceElementMap.Build(surface.Name, root).DescribeElement(elementId));
        return json;
    }

    private static string? Write(Bitmap bmp, SurfaceElementMap? map, string pngPath)
    {
        bmp.Save(pngPath, ImageFormat.Png);
        if (map != null) return map.WriteBeside(pngPath);
        File.Delete(SurfaceElementMap.PathBeside(pngPath));
        return null;
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

    /// <summary>Renders one live form offscreen (as the as-built render does) and saves it as a PNG, with its element map beside it.</summary>
    internal static Size SaveForm(Form form, string path)
    {
        SurfaceElementMap? map = null;
        using var bmp = RenderForm(form, root => map = SurfaceElementMap.Build(Path.GetFileNameWithoutExtension(path), root));
        Write(bmp, map, path);
        return bmp.Size;
    }

    /// <summary>
    /// A hidden form's children report themselves invisible and skip WM_PRINT, so a never-shown widget drew
    /// as an empty panel. Show it far off-screen without activating it (no focus taken from the operator),
    /// draw it, then hide it and put it back. A widget that is already open is drawn where it is.
    /// <paramref name="inspect"/> runs after the draw and before the form is hidden again: only then do its
    /// children report the visibility and layout the bitmap shows.
    /// </summary>
    private static Bitmap RenderForm(Form form, Action<Control>? inspect = null)
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
                return Inspected(Render(snipForm), snipForm, inspect);
            }
            finally
            {
                snipForm.PreviewAllMarks = false;
                ShowWindow(snipForm.Handle, 0);
                snipForm.Location = homeSnip;
            }
        }
        if (form.Visible) return Inspected(Render(form), form, inspect);
        var home = form.Location;
        try
        {
            form.Location = new Point(-32000, -32000);
            form.CreateControl();
            ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
            form.PerformLayout();
            form.Refresh();
            return Inspected(Render(form), form, inspect);
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

    /// <summary>The screen the tray composites are placed on: 1920x1080 with a 48px taskbar along the bottom.</summary>
    internal static readonly Rectangle TrayScreen = new(0, 0, 1920, 1080);
    internal static readonly Rectangle TrayWorkArea = new(0, 0, 1920, 1032);

    /// <summary>Pointer on the Holodex tray icon, near the clock at the bottom-right.</summary>
    internal static readonly Point TrayAnchor = new(1790, 1056);

    /// <summary>The tray icon on <see cref="TrayScreen"/>: a 24px slot centered on <see cref="TrayAnchor"/>.</summary>
    internal static readonly Rectangle TrayIcon = new(TrayAnchor.X - 12, TrayAnchor.Y - 12, 24, 24);

    /// <summary>
    /// The menu panel as it draws, with each hosted speed slider painted into its row: a hosted control is a child
    /// window, which DrawToBitmap of a menu that was never shown leaves blank. The slider paints into a bitmap of
    /// its own (<see cref="PhosphorSpeedSlider.PaintSlider"/> clears the surface it is given).
    /// </summary>
    internal static Bitmap RenderMenu(PhosphorTrayMenu trayMenu)
    {
        var bmp = Render(trayMenu);
        using var g = Graphics.FromImage(bmp);
        foreach (var slider in trayMenu.Items.OfType<ToolStripControlHost>().Select(h => h.Control).OfType<PhosphorSpeedSlider>())
        {
            using var face = new Bitmap(Math.Max(1, slider.Width), Math.Max(1, slider.Height), PixelFormat.Format32bppArgb);
            using (var fg = Graphics.FromImage(face))
            {
                fg.SmoothingMode = SmoothingMode.AntiAlias;
                fg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                slider.PaintSlider(fg);
            }
            g.DrawImageUnscaled(face, slider.Left, slider.Top);
        }
        return bmp;
    }

    /// <summary>
    /// Draws the menu on its current page where it opens on <see cref="TrayScreen"/>: centered above the tray icon
    /// and clear of the taskbar (<see cref="PhosphorTrayMenu.PlaceAbove"/>). <paramref name="panel"/> receives its
    /// screen rectangle.
    /// </summary>
    internal static Bitmap RenderTrayComposite(PhosphorTrayMenu trayMenu, out Rectangle panel)
    {
        using var shot = RenderMenu(trayMenu);
        float s = trayMenu.DeviceDpi / 96f;
        panel = new Rectangle(PhosphorTrayMenu.PlaceAbove(TrayIcon, shot.Size, TrayWorkArea, (int)Math.Round(8 * s)), shot.Size);

        int left = Math.Max(TrayScreen.Left, Math.Min(panel.Left, TrayIcon.Left) - 48);
        int top = Math.Max(TrayScreen.Top, panel.Top - 48);
        int right = Math.Min(TrayScreen.Right, Math.Max(panel.Right, TrayIcon.Right) + 48);
        var bmp = new Bitmap(right - left, TrayScreen.Bottom - top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(PhosphorTheme.Ground);
            using (var bar = new SolidBrush(PhosphorTheme.Surface)) g.FillRectangle(bar, 0, TrayWorkArea.Bottom - top, bmp.Width, TrayScreen.Bottom - TrayWorkArea.Bottom);
            using (var line = new Pen(PhosphorTheme.Line)) g.DrawLine(line, 0, TrayWorkArea.Bottom - top, bmp.Width, TrayWorkArea.Bottom - top);
            PhosphorTheme.DrawArkMark(g, new RectangleF(TrayAnchor.X - left - 10, TrayAnchor.Y - top - 10, 20, 20));
            PhosphorPaint.DrawEyebrow(g, $"Tray menu // {TrayScreen.Width} x {TrayScreen.Height} // above the icon", PhosphorTheme.Ghost, 16, 14, 1f);
            g.DrawImageUnscaled(shot, panel.X - left, panel.Y - top);
        }
        return bmp;
    }

    private static string Slug(string? text)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\(.*?\)", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(plain.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    }

    /// <summary>
    /// The thread card kit on one sheet, built by the real ThreadView and ThreadCard: waiting with Stop, the honest
    /// fallback (Gala's real failure linked to the model that answered, with its footer), a cited answer, a failure
    /// keeping its cause, an agent action with its receipt, and approvals for a change, a client send and a payment.
    /// The copy is a labelled render sample, the same states the HUD, Wiz and Holodex show live.
    /// </summary>
    private static Bitmap RenderAiKitSheet(bool approvals, Action<Control>? inspect = null)
    {
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            BackColor = PhosphorTheme.Surface,
            Padding = WidgetChrome.FramePadding(),
            Size = new Size(560, 1600),
            Name = "Kit"
        };
        var thread = new ThreadView("Kit.Thread") { Dock = DockStyle.Fill, FollowNewest = false };
        form.Controls.Add(thread);
        CardAction Noop(string name, PhosphorGlyph glyph, string label, bool toggle = false) => new(name, glyph, label, (_, _) => { }, toggle);
        var answerActions = new[]
        {
            Noop("Thread.Copy", PhosphorGlyph.Copy, "Copy"), Noop("Thread.Retry", PhosphorGlyph.Retry, "Ask again"),
            Noop("Thread.ThumbUp", PhosphorGlyph.ThumbUp, "Good answer", true), Noop("Thread.ThumbDown", PhosphorGlyph.ThumbDown, "Bad answer", true)
        };

        thread.AddSection("KIT // SAMPLE COPY, DRAWN BY THE REAL CARD CLASSES");
        if (!approvals)
        {
            thread.AddSection("WAITING // A PENDING CARD WITH STOP");
            thread.AddPending("ASKING GALA // HIVEMIND").SetFooter("", Noop("Thread.Stop", PhosphorGlyph.Stop, "Stop"));

            thread.AddSection("HONEST FALLBACK // TWO LINKED CARDS, NEVER A DISGUISED ANSWER");
            thread.Add(ThreadTone.Fail, "GALA // NOT ANSWERED // HIVEMIND", PlainFailure.For("Native mission worker failed: Missing environment variable: ARK_MODEL_GATEWAY_INBOUND_KEY.", "Gala"))
                .SetFooter("", Noop("Thread.Copy", PhosphorGlyph.Copy, "Copy"), Noop("Thread.OpenInHivemind", PhosphorGlyph.ExternalLink, "Open in Hivemind"));
            var fallback = thread.Add(ThreadTone.Agent, "ANSWERED BY NEMOTRON 3 NANO // GALA DID NOT ANSWER", "A paused check runner holds the PR until it resumes; the diff itself is not the reason.");
            fallback.LinkedAbove = true;
            fallback.SetFooter("2.4 S", answerActions);

            thread.AddSection("AGENT ANSWER // MODEL, RECEIPT AND SOURCES");
            thread.Add(ThreadTone.Agent, "GALA // NEMOTRON 3 SUPER // HIVEMIND",
                    "Merges run once checks pass; the PR checklist is at https://github.com/octocat/Hello-World/pull/42")
                .SetFooter("RECEIPT SAMPLE01 // 41.2 S", answerActions.Append(Noop("Thread.OpenInHivemind", PhosphorGlyph.ExternalLink, "Open in Hivemind")).ToArray());

            thread.AddSection("FAILURE // THE REAL CAUSE, COPY AND RETRY");
            thread.Add(ThreadTone.Fail, "NOT ANSWERED // NEMOTRON 3.5 SUPER VL", "Vision request to nvidia/Nemotron-3.5-Super-VL returned no answer. Check the Nebius key and try again.")
                .SetFooter("", Noop("Thread.Copy", PhosphorGlyph.Copy, "Copy"), Noop("Thread.Retry", PhosphorGlyph.Retry, "Ask again"));

        }
        else
        {
            thread.AddSection("APPROVALS // EVERY CLIENT SEND AND MONEY MOVE ASKS BY NAME");
            thread.Add(ThreadTone.Approval, "NEEDS YOUR APPROVAL // QUEUES A HOLODEX WORK ORDER", "Approve lets Wiz do this; Skip leaves everything as it is.", PhosphorGlyph.Holodex);
            thread.Add(ThreadTone.Approval, "NEEDS YOUR APPROVAL // SENDS A CLIENT MESSAGE", "Mercury sends one SMS to the client on the booking. Nothing else is sent.", PhosphorGlyph.Chat)
                .SetRisk(ApprovalRisk.ClientSend);
            thread.Add(ThreadTone.Approval, "NEEDS YOUR APPROVAL // MOVES MONEY", "Collect a $150 deposit through Stripe for Friday 14:00.", PhosphorGlyph.Vault)
                .SetRisk(ApprovalRisk.MoneyMove);
        }

        thread.Relayout();
        // Section labels and approval buttons settle on layout; the headroom keeps the last card whole.
        int contentHeight = thread.ContentHeight(thread.FitWidth) + form.Padding.Vertical + 96;
        form.Height = Math.Min(contentHeight, 2400);
        return RenderForm(form, inspect);
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

        using var labelFont = PhosphorFonts.Mono(PhosphorTheme.EyebrowPx);
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
}
