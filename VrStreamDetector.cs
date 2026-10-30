using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Omniwand;

internal enum VrStreamMode
{
    Auto,
    AlwaysOn,
    Off
}

/// <summary>Experiment switch for how the arrow slot is hidden while testing Air Link pointer behavior.</summary>
internal enum ArrowSlotStyle
{
    ArgbClear,
    Mono,
    NearClear
}

/// <summary>
/// Heuristic detection of an active desktop stream to a headset, plus cache invalidation for Meta Air Link (OculusDash).
/// OculusDash caches HCURSOR -> CursorTexture in a std::map at [ReactSystem + 0x218] and never invalidates entries when
/// Windows SetSystemCursor updates a system cursor slot in-place. Flushing stale nodes forces OculusDash to re-read
/// GetIconInfo(65539) after Omniwand replaces the system cursors with 32x32 transparent ARGB icons (hotspot 16,16).
/// </summary>
internal static class VrStreamDetector
{
    private const int ProcessVmOperation = 0x0008;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessVmWrite = 0x0020;
    private const long ReactSystemRva = 0x1d86780;

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static volatile bool _cacheFlushAllowed;

    /// <summary>
    /// True only while <see cref="Detect"/> finds a real headset stream and VR mode is not Off. Until then
    /// <see cref="EnsureOculusDashCursorCacheClean"/> never opens OculusDash, so hiding the cursor on an ordinary PC
    /// touches no other process.
    /// </summary>
    public static bool CacheFlushAllowed
    {
        get => _cacheFlushAllowed;
        set => _cacheFlushAllowed = value;
    }

    /// <summary>The gate's rule: a stream actually detected (not the forced placeholder) with VR mode on.</summary>
    internal static bool ShouldAllowCacheFlush(VrStreamMode mode, string? detectedStream) =>
        mode != VrStreamMode.Off && !string.IsNullOrEmpty(detectedStream);

    /// <summary>Whether OculusDash was running at the last <see cref="Detect"/> (Program.VrTick, every 1.5 s).</summary>
    private static volatile bool _dashRunning;

    /// <summary>
    /// Names the headset stream, from one process list per call: listing processes costs tens of milliseconds on a
    /// busy PC, so it happens here on the VR timer and never on the render thread (see <see cref="EnsureOculusDashCursorCacheClean"/>).
    /// </summary>
    public static string? Detect()
    {
        try
        {
            var names = new HashSet<string>(Process.GetProcesses().Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
            _dashRunning = names.Contains("OculusDash");
            if (names.Contains("VirtualDesktop.Streamer"))
                return "Virtual Desktop";
            if (names.Contains("OVRServer_x64") && _dashRunning)
                return "Meta Air Link / Link";
        }
        catch
        {
            // Process enumeration can transiently fail; treat as not streaming.
        }
        return null;
    }

    /// <summary>
    /// Checks OculusDash's in-memory std::map&lt;HCURSOR, CursorTexture&gt; at [ReactSystem + 0x218].
    /// If forceFlush is true or any cached stock cursor entry was captured before Omniwand's 32x32 ARGB
    /// cursor (hotspot 16,16 with non-zero hbmColor), clears the map so OculusDash re-reads GetIconInfo.
    /// Returns true if the cache was flushed.
    /// </summary>
    public static bool EnsureOculusDashCursorCacheClean(bool forceFlush = false)
    {
        // Writing into another process's memory is what antivirus watches for: only while a headset stream is really
        // detected and the user has not turned VR mode off (Program.VrTick sets this). A plain desktop never gets it.
        // The render thread calls this on every pointer re-hide: without OculusDash at the last VR tick it returns
        // before listing processes, which would stall the pointer for a frame or more.
        if (!CacheFlushAllowed || !_dashRunning) return false;
        try
        {
            var procs = Process.GetProcessesByName("OculusDash");
            if (procs.Length == 0) return false;

            var proc = procs[0];
            if (proc.MainModule == null) return false;
            long baseAddr = (long)proc.MainModule.BaseAddress;

            IntPtr hProc = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, proc.Id);
            if (hProc == IntPtr.Zero) return false;

            try
            {
                long reactSys = ReadInt64(hProc, baseAddr + ReactSystemRva);
                if (reactSys == 0) return false;

                long mapPtr = ReadInt64(hProc, reactSys + 0x218);
                if (mapPtr == 0) return false;

                long head = ReadInt64(hProc, mapPtr + 0x00);
                long count = ReadInt64(hProc, mapPtr + 0x08);
                if (head == 0) return false;

                // MSVC std::_Tree_node head sentinel has _Color = 1 (Black) and _Isnil = 1 -> 0x0101 at +0x18
                ushort sentinel = ReadUInt16(hProc, head + 0x18);
                if (sentinel != 0x0101) return false;

                long root = ReadInt64(hProc, head + 0x08);
                bool hasStale = forceFlush || (count > 0 && HasNonOmniwandNode(hProc, root, head, 0));
                if (!hasStale) return false;

                WriteInt64(hProc, head + 0x00, head); // _Left = head
                WriteInt64(hProc, head + 0x08, head); // _Parent = head (empty tree)
                WriteInt64(hProc, head + 0x10, head); // _Right = head
                WriteInt64(hProc, mapPtr + 0x08, 0);  // _Mysize = 0
                return true;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNonOmniwandNode(IntPtr hProc, long node, long head, int depth)
    {
        if (node == 0 || node == head || depth > 24) return false;
        if (ReadByte(hProc, node + 0x19) != 0) return false;

        long hCurKey = ReadInt64(hProc, node + 0x20);
        int hotX = ReadInt32(hProc, node + 0x7C);
        int hotY = ReadInt32(hProc, node + 0x80);
        long hbmColor = ReadInt64(hProc, node + 0x90);

        // System cursor handles live in the 0x10000..0x1FFFF range; our 32x32 ARGB GetHicon cursor has hot=(16,16) and hbmColor != 0
        if (hCurKey >= 0x10000 && hCurKey <= 0x2FFFF && (hotX != 16 || hotY != 16 || hbmColor == 0))
        {
            return true;
        }

        long left = ReadInt64(hProc, node + 0x00);
        long right = ReadInt64(hProc, node + 0x10);
        return HasNonOmniwandNode(hProc, left, head, depth + 1) ||
               HasNonOmniwandNode(hProc, right, head, depth + 1);
    }

    private static long ReadInt64(IntPtr hProc, long addr)
    {
        byte[] buf = new byte[8];
        return ReadProcessMemory(hProc, (IntPtr)addr, buf, 8, out _) ? BitConverter.ToInt64(buf, 0) : 0;
    }

    private static int ReadInt32(IntPtr hProc, long addr)
    {
        byte[] buf = new byte[4];
        return ReadProcessMemory(hProc, (IntPtr)addr, buf, 4, out _) ? BitConverter.ToInt32(buf, 0) : 0;
    }

    private static ushort ReadUInt16(IntPtr hProc, long addr)
    {
        byte[] buf = new byte[2];
        return ReadProcessMemory(hProc, (IntPtr)addr, buf, 2, out _) ? BitConverter.ToUInt16(buf, 0) : (ushort)0;
    }

    private static byte ReadByte(IntPtr hProc, long addr)
    {
        byte[] buf = new byte[1];
        return ReadProcessMemory(hProc, (IntPtr)addr, buf, 1, out _) ? buf[0] : (byte)1;
    }

    private static void WriteInt64(IntPtr hProc, long addr, long value)
    {
        byte[] buf = BitConverter.GetBytes(value);
        WriteProcessMemory(hProc, (IntPtr)addr, buf, 8, out _);
    }
}

