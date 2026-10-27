using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace Omniwand;

/// <summary>
/// Knows which physical mouse is in use. Windows Raw Input reports every mouse event with the device that sent it,
/// so the tracker listens on a background thread (a message-only window registered with RIDEV_INPUTSINK, which
/// hears input wherever the focus is) and counts events per device. A mouse that moves in a real burst (8 events
/// within 400 ms) becomes the active one; an idle mouse never takes over, and injected input (no device, such as a
/// streaming app's virtual pointer) is ignored. <see cref="MouseHardwareAndWandEngine"/> owns what happens on a switch.
/// </summary>
internal static class ActiveMouseTracker
{
    private const int BurstEvents = 8;
    private const long BurstWindowMs = 400;

    private static readonly Dictionary<IntPtr, string> NameByHandle = new();
    private static readonly Dictionary<string, (long WindowStart, int Count, long LastInputMs)> Activity = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static Thread? _thread;

    /// <summary>Raised on the tracker thread with the device name of a mouse that just started moving.</summary>
    public static event Action<string>? MouseStartedMoving;

    /// <summary>The device names of every mouse Windows lists now (GetRawInputDeviceList), virtual ones included.</summary>
    public static IReadOnlyList<string> ListMice()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return Array.Empty<string>();
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return Array.Empty<string>();
        return list.Where(d => d.dwType == RIM_TYPEMOUSE).Select(d => DeviceName(d.hDevice)).Where(n => n.Length > 0).Distinct().ToList();
    }

    /// <summary>Milliseconds since each mouse (by device name) last produced input, for the pipe's mouse:status.</summary>
    public static IReadOnlyDictionary<string, long> SinceLastInputMs()
    {
        lock (Gate)
        {
            long now = Clock.ElapsedMilliseconds;
            return Activity.ToDictionary(a => a.Key, a => now - a.Value.LastInputMs);
        }
    }

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            using var window = new SinkWindow();
            Application.Run();
        }) { IsBackground = true, Name = "OmniwandActiveMouse" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static void OnInput(IntPtr rawInput)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(rawInput, RID_HEADER, out var header, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue) return;
        if (header.dwType != RIM_TYPEMOUSE || header.hDevice == IntPtr.Zero) return;
        if (!NameByHandle.TryGetValue(header.hDevice, out var name))
            NameByHandle[header.hDevice] = name = DeviceName(header.hDevice);
        if (name.Length == 0) return;

        long now = Clock.ElapsedMilliseconds;
        bool burst;
        lock (Gate)
        {
            var a = Activity.TryGetValue(name, out var seen) ? seen : (WindowStart: now, Count: 0, LastInputMs: now);
            if (now - a.WindowStart > BurstWindowMs) a = (WindowStart: now, Count: 0, LastInputMs: now);
            a.Count++;
            a.LastInputMs = now;
            Activity[name] = a;
            burst = a.Count == BurstEvents;
        }
        if (burst) MouseStartedMoving?.Invoke(name);
    }

    private static string DeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0) return string.Empty;
        var sb = new StringBuilder((int)chars);
        return GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref chars) == uint.MaxValue ? string.Empty : sb.ToString();
    }

    /// <summary>VID_xxxx&amp;PID_yyyy from a device path, USB or Bluetooth form; empty for virtual devices.</summary>
    public static string VidPid(string deviceName)
    {
        var m = Regex.Match(deviceName, @"VID[_&](?:0002|02)?([0-9A-F]{4})[_&]PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}" : string.Empty;
    }

    /// <summary>The HID product string the device reports ("USB Receiver", "MX Master 4"), or empty.</summary>
    public static string ProductName(string deviceName)
    {
        try
        {
            using var handle = CreateFile(deviceName, 0, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid) return string.Empty;
            var buffer = new byte[256];
            return HidD_GetProductString(handle, buffer, buffer.Length) ? Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>A window that exists only to receive WM_INPUT for every mouse, wherever the focus is.</summary>
    private sealed class SinkWindow : NativeWindow, IDisposable
    {
        public SinkWindow()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
            var device = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle };
            RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) OnInput(m.LParam);
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_HEADER = 0x10000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEMOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUTHEADER data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);
}
