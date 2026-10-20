using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Omniwand;

/// <summary>
/// Owns the cursor overlay's place in the z-order:
/// 1. Re-asserts HWND_TOPMOST on a fast clock so ordinary topmost windows do not rise above the wand.
/// 2. Detects higher DWM z-order bands (ZBID > 1: Windows Toast Notifications / Windows.UI.Core.CoreWindow,
///    Start, Search, Action Center, XAML Islands, Taskbar flyouts) via 5-point WindowFromPoint + GetWindowBand
///    so Omniwand immediately promotes the Ark Aperture into the Windows DWM Hardware Cursor plane
///    (rendering 100% IN FRONT OF Windows Notifications and shell surfaces with zero edge clipping).
/// </summary>
internal sealed class OverlayZGuard
{
    private const double RaiseEverySec = 0.12;
    private const double CheckEverySec = 0.016;
    private const int SettleChecks = 1;
    private const int CursorRadiusPad = 28;
    private static readonly uint SelfPid = (uint)Environment.ProcessId;

    private double _lastRaise = double.NegativeInfinity;
    private double _lastCheck = double.NegativeInfinity;
    private bool _pendingCovered;
    private int _pendingCount;

    /// <summary>True while a surface in a higher DWM z-band (e.g. Windows Notification, Taskbar, Start) is under or touching the cursor.</summary>
    public bool Covered { get; private set; }

    public bool Tick(IntPtr overlay, int x, int y, double nowSec)
    {
        if (overlay == IntPtr.Zero) return false;

        if (nowSec - _lastRaise >= RaiseEverySec)
        {
            _lastRaise = nowSec;
            // HWND_TOPMOST, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            SetWindowPos(overlay, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }

        if (nowSec - _lastCheck < CheckEverySec) return false;
        _lastCheck = nowSec;

        bool covered = IsPointCoveredAbove(overlay, x, y);
        if (covered == Covered) { _pendingCount = 0; return false; }
        if (covered != _pendingCovered) { _pendingCovered = covered; _pendingCount = 0; }
        if (++_pendingCount < SettleChecks) return false;

        _pendingCount = 0;
        Covered = covered;
        return true;
    }

    private static bool IsPointCoveredAbove(IntPtr overlay, int x, int y)
    {
        // 1. Fast 5-point probe around the cursor radius using WindowFromPoint + GetWindowBand.
        //    Windows 11 Toast Notifications (Windows.UI.Core.CoreWindow), Start Menu, Search, Action Center,
        //    and Taskbar flyouts live in higher DWM Z-bands (ZBID > 1) that EnumWindows does not order before ZBID_DEFAULT.
        ReadOnlySpan<(int dx, int dy)> offsets = stackalloc (int, int)[]
        {
            (0, 0),
            (-CursorRadiusPad, -CursorRadiusPad),
            (CursorRadiusPad, -CursorRadiusPad),
            (-CursorRadiusPad, CursorRadiusPad),
            (CursorRadiusPad, CursorRadiusPad)
        };

        foreach (var (dx, dy) in offsets)
        {
            IntPtr hit = WindowFromPoint(new POINT { X = x + dx, Y = y + dy });
            if (hit == IntPtr.Zero || hit == overlay) continue;

            IntPtr root = GetAncestor(hit, GA_ROOT);
            if (root == IntPtr.Zero) root = hit;
            if (root == overlay) continue;

            GetWindowThreadProcessId(root, out uint pid);
            if (pid == SelfPid) continue;

            if (GetWindowBand(root, out uint band) && band > 1)
            {
                return true;
            }

            string cls = GetWindowClass(root);
            if (IsAboveOverlayShellClass(cls))
            {
                return true;
            }
        }

        // 2. Fallback: walk top-level windows above the overlay in ZBID_DEFAULT.
        bool covered = false;
        EnumWindows((h, _) =>
        {
            if (h == overlay) return false;
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == SelfPid) return true;
            if (!GetWindowRect(h, out RECT r)) return true;
            if (x >= r.Left - CursorRadiusPad && x < r.Right + CursorRadiusPad &&
                y >= r.Top - CursorRadiusPad && y < r.Bottom + CursorRadiusPad)
            {
                covered = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return covered;
    }

    private static bool IsAboveOverlayShellClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("#32768", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int DWMWA_CLOAKED = 14;
    private const uint GA_ROOT = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT Point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
