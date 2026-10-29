using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// The live tray menu, driven through the control pipe so it can be checked without touching the mouse or
/// keyboard: <c>tray:open</c>, <c>tray:path:Settings/Spells</c>, <c>tray:capture:&lt;png&gt;</c>,
/// <c>tray:report</c>, <c>tray:close</c>, and <c>tray:shoot:&lt;png&gt;|Settings/Spells</c>, which does open, path,
/// capture and close in one call (a menu the app opened without a click closes as soon as anything else is
/// activated, so separate calls can find it gone). It opens the menu the way NotifyIcon does when its icon is clicked, at the
/// icon's real screen rectangle, turns pages the way a click on a page row does (<see cref="PhosphorTrayMenu.ShowPage"/>),
/// and copies the screen, so what it reports is what a click would show. It never moves the pointer.
/// </summary>
internal static class TrayProbe
{
    public static string Run(NotifyIcon icon, PhosphorTrayMenu menu, string arg)
    {
        if (arg.Equals("open", StringComparison.OrdinalIgnoreCase)) return Open(icon, menu);
        if (arg.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) return OpenPath(menu, arg["path:".Length..]);
        if (arg.StartsWith("capture:", StringComparison.OrdinalIgnoreCase)) return Capture(icon, menu, arg["capture:".Length..].Trim());
        if (arg.StartsWith("shoot:", StringComparison.OrdinalIgnoreCase)) return Shoot(icon, menu, arg["shoot:".Length..]);
        if (arg.Equals("report", StringComparison.OrdinalIgnoreCase)) return "ok:tray:report:" + Report(menu, icon);
        if (arg.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            menu.Close();
            return "ok:tray:close";
        }
        return "error:tray:unknown command";
    }

    private static string Open(NotifyIcon icon, PhosphorTrayMenu menu)
    {
        if (menu.Visible) menu.Close();
        var rect = TrayIconBounds.Of(icon, out string iconError);
        var pt = rect is Rectangle r ? new Point(r.Left + r.Width / 2, r.Top + r.Height / 2) : Cursor.Position;

        // NotifyIcon.ShowContextMenu: bring the icon's window forward, then ContextMenuStrip.ShowInTaskbar at the
        // pointer (which sits on the icon when it is clicked).
        var sw = Stopwatch.StartNew();
        if (typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(icon) is NativeWindow window)
            SetForegroundWindow(window.Handle);
        var show = typeof(ContextMenuStrip).GetMethod("ShowInTaskbar", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(int), typeof(int) });
        if (show != null) show.Invoke(menu, new object[] { pt.X, pt.Y });
        else menu.Show(pt, ToolStripDropDownDirection.AboveLeft);
        long ms = sw.ElapsedMilliseconds;
        return $"ok:tray:open:icon={(rect is Rectangle ir ? Format(ir) : "unknown(" + iconError + ")")}:at={pt.X},{pt.Y}:ms={ms}:{Report(menu, icon)}";
    }

    private static string Shoot(NotifyIcon icon, PhosphorTrayMenu menu, string spec)
    {
        string[] parts = spec.Split('|', 2);
        // Record why the menu closed if it closes before the capture, so a miss names its cause.
        string? closedBy = null;
        ToolStripDropDownClosedEventHandler onClosed = (_, e) => closedBy ??= e.CloseReason.ToString();
        menu.Closed += onClosed;
        var steps = new List<string>();
        // A real click lets the shell hand Omniwand the foreground; the probe's first SetForegroundWindow from a
        // background process is refused, and the menu closes on AppFocusChange. That first try earns the right, so
        // one retry opens it the way a click does.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            closedBy = null;
            steps.Add(Open(icon, menu));
            if (parts.Length > 1 && parts[1].Trim().Length > 0) steps.Add(OpenPath(menu, parts[1]));
            for (int i = 0; i < 8; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            if (menu.Visible || closedBy != nameof(ToolStripDropDownCloseReason.AppFocusChange)) break;
            steps.Add("retry:AppFocusChange");
        }
        steps.Add(Capture(icon, menu, parts[0].Trim()));
        menu.Closed -= onClosed;
        if (closedBy != null) steps.Add($"closed-before-capture:{closedBy}");
        menu.Close();
        return string.Join(" || ", steps);
    }

    private static string OpenPath(PhosphorTrayMenu menu, string path)
    {
        if (!menu.Visible) return "error:tray:path:menu is not open";
        menu.ShowPage(menu.Home);
        var timings = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var row = menu.Page.Items.OfType<PhosphorPageRow>()
                .FirstOrDefault(i => Clean(i.Text).StartsWith(segment, StringComparison.OrdinalIgnoreCase));
            if (row == null) return $"error:tray:path:no page '{segment}' on {Name(menu.Page)}";
            // The turn through its first paint, as a click sees it.
            var sw = Stopwatch.StartNew();
            menu.ShowPage(row.Page);
            menu.Update();
            timings.Add($"{segment}={sw.ElapsedMilliseconds}ms");
        }
        return $"ok:tray:path:{string.Join(",", timings)}:{Report(menu)}";
    }

    private static string Capture(NotifyIcon icon, PhosphorTrayMenu menu, string file)
    {
        if (file.Length == 0) return "error:tray:capture:file required";
        if (!menu.Visible) return "error:tray:capture:menu is not open";
        // Let the panel finish painting before the copy.
        menu.Update();
        Application.DoEvents();

        var screen = Screen.FromRectangle(menu.Bounds);
        var area = menu.Bounds;
        if (TrayIconBounds.Of(icon, out _) is Rectangle ir && screen.Bounds.Contains(ir)) area = Rectangle.Union(area, ir);
        area.Inflate(32, 32);
        // Down to the screen bottom, so the taskbar shows beside the menu.
        area = Rectangle.FromLTRB(area.Left, area.Top, area.Right, screen.Bounds.Bottom);
        area.Intersect(screen.Bounds);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        bmp.Save(file, ImageFormat.Png);
        return $"ok:tray:capture:{file}:region={Format(area)}:{Report(menu, icon)}";
    }

    /// <summary>
    /// The panel's screen rectangle, its page, the work area, and the checks it has to pass: inside the work area
    /// (never over the taskbar), clear of the icon on the taskbar's side, and no row label clipped by the panel.
    /// </summary>
    private static string Report(PhosphorTrayMenu menu, NotifyIcon? icon = null)
    {
        var work = Screen.FromRectangle(menu.Bounds).WorkingArea;
        var sb = new StringBuilder($"work={Format(work)}:dpi={menu.DeviceDpi}:page={Name(menu.Page)}:panel={Format(menu.Bounds)}:visible={menu.Visible}");
        var problems = new List<string>();
        if (menu.Visible && !work.Contains(menu.Bounds)) problems.Add("panel leaves the work area");
        if (menu.Visible && icon != null && TrayIconBounds.Of(icon, out _) is Rectangle ir)
        {
            sb.Append($":icon={Format(ir)}");
            bool below = ir.Top + ir.Height / 2 < work.Top + work.Height / 2;
            if (below ? menu.Top < ir.Bottom : menu.Bottom > ir.Top) problems.Add("panel covers the icon");
        }
        foreach (ToolStripItem item in menu.Items)
            if (item.Available && item.Bounds.Right > menu.ClientSize.Width) problems.Add($"{Clean(item.Text)} clipped");
        sb.Append(problems.Count == 0 ? ":checks=pass" : ":checks=FAIL[" + string.Join("; ", problems) + "]");
        return sb.ToString();
    }

    private static string Name(TrayPage page) => page.Parent == null ? "home" : Clean(page.Title);

    private static string Clean(string? text) => (text ?? string.Empty).Replace("&&", "&");

    private static string Format(Rectangle r) => $"{r.X},{r.Y},{r.Width}x{r.Height}";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
