using System;
using System.Runtime.InteropServices;

namespace Omniwand;

/// <summary>
/// Owns the cursor overlay's place in the z-order. Two jobs, both on the render thread:
/// 1. Re-assert HWND_TOPMOST on a fixed clock (not only while the cursor moves), so windows that pop up
///    later (taskbar, menus, toasts, tooltips) do not stay above the wand.
/// 2. Some shell surfaces (Start, Search, taskbar and tray flyouts) live in z-order bands above every
///    ordinary topmost window, so no re-assert can cover them. When a visible window still sits above the
///    overlay under the cursor, the wand steps aside: the overlay hides and the native Windows cursor comes
///    back until the pointer leaves that surface. Without this the pointer simply vanished there.
/// </summary>
internal sealed class OverlayZGuard
{
    private const double RaiseEverySec = 0.15;
    private const double CheckEverySec = 0.02;
    private const int SettleChecks = 1; // immediate switch so notification/taskbar edges never clip the wand
    private const int CursorRadiusPad = 32; // pad by the rendered wand radius so wings never clip under higher z-band edges

    private double _lastRaise = double.NegativeInfinity;
    private double _lastCheck = double.NegativeInfinity;
    private bool _pendingCovered;
    private int _pendingCount;

    /// <summary>True while a surface the overlay cannot rise above is under the cursor.</summary>
    public bool Covered { get; private set; }

    /// <summary>Runs one step. Returns true when <see cref="Covered"/> changed.</summary>
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

    /// <summary>
    /// Walks top-level windows from the top of the z-order down to the overlay. Any visible, uncloaked window
    /// that takes input and contains the point is above the wand. Click-through layered windows (other apps'
    /// overlays, Omniwand's own orb and trailer) are ignored, since the pointer is not over them in any sense.
    /// </summary>
    private static bool IsPointCoveredAbove(IntPtr overlay, int x, int y)
    {
        bool covered = false;
        EnumWindows((h, _) =>
        {
            if (h == overlay) return false; // reached the overlay: nothing above covers the point
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT)) return true;
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
