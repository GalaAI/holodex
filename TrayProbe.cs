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
/// keyboard: <c>tray:open</c>, <c>tray:path:Settings/Wand gestures</c>, <c>tray:capture:&lt;png&gt;</c>,
/// <c>tray:report</c>, <c>tray:close</c>, and <c>tray:shoot:&lt;png&gt;|Settings/Wand gestures</c>, which does open, path,
/// capture and close in one call (a menu the app opened without a click closes as soon as anything else is
/// activated, so separate calls can find it gone). It opens the menu the way NotifyIcon does when its icon is clicked, at the
/// icon's real screen rectangle, opens submenus through WinForms' own <see cref="ToolStripDropDownItem.ShowDropDown"/>,
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
        if (arg.Equals("report", StringComparison.OrdinalIgnoreCase)) return "ok:tray:report:" + Report(menu);
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
        var rect = IconRect(icon);
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
        return $"ok:tray:open:icon={(rect is Rectangle ir ? Format(ir) : "unknown(" + LastIconError + ")")}:at={pt.X},{pt.Y}:ms={ms}:{Report(menu)}";
    }

    private static string Shoot(NotifyIcon icon, PhosphorTrayMenu menu, string spec)
    {
        string[] parts = spec.Split('|', 2);
        var steps = new List<string> { Open(icon, menu) };
        if (parts.Length > 1 && parts[1].Trim().Length > 0) steps.Add(OpenPath(menu, parts[1]));
        for (int i = 0; i < 8; i++)
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
        steps.Add(Capture(icon, menu, parts[0].Trim()));
        menu.Close();
        return string.Join(" || ", steps);
    }

    private static string OpenPath(PhosphorTrayMenu menu, string path)
    {
        if (!menu.Visible) return "error:tray:path:menu is not open";
        CloseSubmenus(menu);
        ToolStripDropDown current = menu;
        var timings = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = current.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.HasDropDownItems && Clean(i.Text).StartsWith(segment, StringComparison.OrdinalIgnoreCase));
            if (item == null) return $"error:tray:path:no submenu '{segment}'";
            item.Select();
            var sw = Stopwatch.StartNew();
            item.ShowDropDown();
            timings.Add($"{segment}={sw.ElapsedMilliseconds}ms");
            current = item.DropDown;
        }
        return $"ok:tray:path:{string.Join(",", timings)}:{Report(menu)}";
    }

    private static string Capture(NotifyIcon icon, PhosphorTrayMenu menu, string file)
    {
        if (file.Length == 0) return "error:tray:capture:file required";
        var panels = Panels(menu).Where(p => p.Visible).ToList();
        if (panels.Count == 0) return "error:tray:capture:menu is not open";
        // Let the panels finish painting before the copy.
        foreach (var p in panels) p.Update();
        Application.DoEvents();

        var screen = Screen.FromRectangle(menu.Bounds);
        var area = panels.Select(p => p.Bounds).Aggregate(Rectangle.Union);
        if (IconRect(icon) is Rectangle ir && screen.Bounds.Contains(ir)) area = Rectangle.Union(area, ir);
        area.Inflate(32, 32);
        // Down to the screen bottom, so the taskbar shows beside the menu.
        area = Rectangle.FromLTRB(area.Left, area.Top, area.Right, screen.Bounds.Bottom);
        area.Intersect(screen.Bounds);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        bmp.Save(file, ImageFormat.Png);
        return $"ok:tray:capture:{file}:region={Format(area)}:{Report(menu)}";
    }

    /// <summary>
    /// Each open panel's screen rectangle, the work area, and the checks the menu has to pass: every panel
    /// inside the work area (never over the taskbar), every submenu right of its parent and clear of every
    /// panel before it, and no row label clipped by its panel.
    /// </summary>
    private static string Report(PhosphorTrayMenu menu)
    {
        var open = Panels(menu).Where(p => p.Visible).ToList();
        var work = Screen.FromRectangle(menu.Bounds).WorkingArea;
        var sb = new StringBuilder($"work={Format(work)}:dpi={menu.DeviceDpi}");
        var problems = new List<string>();
        for (int i = 0; i < open.Count; i++)
        {
            var p = open[i];
            string name = Name(p);
            sb.Append($":{name}={Format(p.Bounds)}");
            if (!work.Contains(p.Bounds)) problems.Add($"{name} leaves the work area");
            if (i > 0)
            {
                var parent = open[i - 1];
                if (p.Left < parent.Right - 2) problems.Add($"{name} not right of {Name(parent)}");
                foreach (var before in open.Take(i))
                {
                    var overlap = Rectangle.Intersect(before.Bounds, p.Bounds);
                    if (overlap.Width > 2 && overlap.Height > 2) problems.Add($"{name} overlaps {Name(before)}");
                }
            }
            foreach (ToolStripItem item in p.Items)
                if (item.Available && item.Bounds.Right > p.ClientSize.Width) problems.Add($"{name}/{Clean(item.Text)} clipped");
        }
        sb.Append(problems.Count == 0 ? ":checks=pass" : ":checks=FAIL[" + string.Join("; ", problems) + "]");
        return sb.ToString();
    }

    private static IEnumerable<ToolStripDropDown> Panels(ToolStripDropDown panel)
    {
        yield return panel;
        foreach (var item in panel.Items.OfType<ToolStripDropDownItem>().Where(i => i.HasDropDownItems && i.DropDown.Visible))
            foreach (var child in Panels(item.DropDown))
                yield return child;
    }

    private static void CloseSubmenus(ToolStripDropDown panel)
    {
        foreach (var item in panel.Items.OfType<ToolStripDropDownItem>().Where(i => i.HasDropDownItems && i.DropDown.Visible))
        {
            CloseSubmenus(item.DropDown);
            item.HideDropDown();
        }
    }

    private static string Name(ToolStripDropDown panel) => panel.OwnerItem == null ? "root" : Clean(panel.OwnerItem.Text);

    private static string Clean(string? text) => (text ?? string.Empty).Replace("&&", "&");

    private static string Format(Rectangle r) => $"{r.X},{r.Y},{r.Width}x{r.Height}";

    private static string LastIconError = string.Empty;

    /// <summary>The icon's screen rectangle from the shell, or null when the shell does not report it.</summary>
    private static Rectangle? IconRect(NotifyIcon icon)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(NotifyIcon).GetField("_window", flags)?.GetValue(icon) is not NativeWindow window) { LastIconError = "no _window"; return null; }
        object? rawId = typeof(NotifyIcon).GetField("_id", flags)?.GetValue(icon);
        if (rawId == null) { LastIconError = "no _id"; return null; }
        var ident = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = Convert.ToUInt32(rawId) };
        int hr = Shell_NotifyIconGetRect(ref ident, out var rc);
        if (hr != 0) { LastIconError = $"Shell_NotifyIconGetRect 0x{hr:X8}"; return null; }
        return Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
