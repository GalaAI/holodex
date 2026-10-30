using System.Runtime.InteropServices;
using System.Windows.Forms;
using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// The 2026-10-30 scroll beam rule: no native bar or box, nothing reserved for it, scrolling still works, and the beam
/// fades in on scroll and out 1 s after the last one.
/// </summary>
public class ScrollBeamTests
{
    [Fact]
    public void Beam_fades_in_holds_for_a_second_then_fades_out()
    {
        Assert.Equal(0f, ScrollBeam.OpacityAt(0, 0f, reducedMotion: false), 3);
        float rising = ScrollBeam.OpacityAt(90, 0f, reducedMotion: false);
        Assert.InRange(rising, 0.1f, 0.9f);
        Assert.Equal(1f, ScrollBeam.OpacityAt(ScrollBeam.FadeInMs, 0f, false), 3);
        Assert.Equal(1f, ScrollBeam.OpacityAt(ScrollBeam.IdleMs - 1, 0f, false), 3);
        Assert.InRange(ScrollBeam.OpacityAt(ScrollBeam.IdleMs + ScrollBeam.FadeOutMs / 2.0, 0f, false), 0.2f, 0.8f);
        Assert.Equal(0f, ScrollBeam.OpacityAt(ScrollBeam.IdleMs + ScrollBeam.FadeOutMs, 0f, false), 3);
        // Scrolling again while it is already showing does not dip it.
        Assert.Equal(1f, ScrollBeam.OpacityAt(10, 1f, false), 3);
    }

    [Fact]
    public void Reduced_motion_shows_and_hides_without_a_fade()
    {
        Assert.Equal(1f, ScrollBeam.OpacityAt(0, 0f, reducedMotion: true));
        Assert.Equal(1f, ScrollBeam.OpacityAt(ScrollBeam.IdleMs - 1, 0f, true));
        Assert.Equal(0f, ScrollBeam.OpacityAt(ScrollBeam.IdleMs, 0f, true));
    }

    [Fact]
    public void Thread_hides_the_native_bar_reserves_no_width_and_still_scrolls()
    {
        // A real window on its own STA thread: it passes alone but can stall when the whole suite builds forms on
        // parallel threads, so it runs on demand (HOLODEX_UI_TESTS=1), like the render tests.
        if (Environment.GetEnvironmentVariable("HOLODEX_UI_TESTS") != "1") return;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                PhosphorFonts.EnsureLoaded();
                // Not disposed: closing a form from a test thread can wait on SystemEvents bound to another test's ended thread.
                var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(420, 300) };
                var thread = new ThreadView("Test.Thread") { Dock = DockStyle.Fill, FollowNewest = false };
                form.Controls.Add(thread);
                form.Show();
                using (thread.Batch())
                    for (int i = 0; i < 30; i++) thread.Add(ThreadTone.Agent, $"CARD // {i}", "A line of text long enough to wrap once in this width so the list really overflows.");
                Application.DoEvents();

                Assert.True(thread.DisplayRectangle.Height > thread.ClientSize.Height, "the list overflows");
                // The native bar has no area on screen and takes no client width.
                GetScrollBarInfo(thread.Handle, OBJID_VSCROLL, out var sbi);
                Assert.True(sbi.right - sbi.left <= 0, $"native bar width {sbi.right - sbi.left}");
                Assert.Equal(thread.Width, thread.ClientSize.Width);
                // Cards stop at the beam's lane and never rewrap for a scrollbar.
                var card = thread.Cards.First();
                Assert.Equal(thread.ClientSize.Width - ScrollBeam.Lane(thread), card.Width);

                // The wheel scrolls (sent to the window, not through the desktop's input).
                int before = -thread.DisplayRectangle.Y;
                var center = thread.PointToScreen(new System.Drawing.Point(thread.Width / 2, thread.Height / 2));
                SendMessage(thread.Handle, 0x020A, (IntPtr)((-120) << 16), (IntPtr)((center.Y << 16) | (center.X & 0xFFFF)));
                Application.DoEvents();
                Assert.True(-thread.DisplayRectangle.Y > before, "the wheel scrolled the list");
                Assert.True(thread.Beam.Ratio > 0f);

                // A press in the beam's lane near the bottom drags the list down.
                int laneX = thread.ClientSize.Width - 2, nearBottom = thread.ClientSize.Height - 4;
                IntPtr lp = (IntPtr)((nearBottom << 16) | laneX);
                SendMessage(thread.Handle, 0x0201, (IntPtr)1, lp);
                SendMessage(thread.Handle, 0x0202, IntPtr.Zero, lp);
                Application.DoEvents();
                Assert.True(thread.Beam.Ratio > 0.9f, $"lane drag reached the bottom, ratio {thread.Beam.Ratio}");
                GetScrollBarInfo(thread.Handle, OBJID_VSCROLL, out sbi);
                Assert.True(sbi.right - sbi.left <= 0, "the bar stays hidden after scrolling");
                Assert.Equal(thread.Width, thread.ClientSize.Width);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(120)), "the scroll check finished");
        if (error != null) throw error;
    }

    private const int OBJID_VSCROLL = unchecked((int)0xFFFFFFFB);
    [StructLayout(LayoutKind.Sequential)] private struct SCROLLBARINFO { public int cbSize; public int left, top, right, bottom; public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved; public int rgstate0, rgstate1, rgstate2, rgstate3, rgstate4, rgstate5; }
    private static bool GetScrollBarInfo(IntPtr h, int id, out SCROLLBARINFO info) { info = new SCROLLBARINFO { cbSize = Marshal.SizeOf<SCROLLBARINFO>() }; return GetScrollBarInfoNative(h, id, ref info); }
    [DllImport("user32.dll", EntryPoint = "GetScrollBarInfo")] private static extern bool GetScrollBarInfoNative(IntPtr hWnd, int idObject, ref SCROLLBARINFO psbi);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
