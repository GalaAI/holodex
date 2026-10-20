using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Lets the borderless Phosphor widgets be dragged by any passive surface (the form itself,
/// panels and labels). Inputs, buttons, dropdowns and text areas keep their normal behavior.
/// </summary>
internal static class WidgetDrag
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void Enable(Form form)
    {
        Attach(form, form);
    }

    private static void Attach(Form form, Control control)
    {
        if (control == form || control is Panel || control is Label)
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            };
        }

        foreach (Control child in control.Controls)
        {
            Attach(form, child);
        }
    }
}
