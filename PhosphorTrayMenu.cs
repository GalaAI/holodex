using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// One page of the tray menu: its rows in order and the eyebrow over them. Pages replace submenus: a page row
/// (<see cref="PhosphorPageRow"/>) turns the same panel to its page and the Back row turns it back, so nothing
/// opens beside the menu and nothing closes when the pointer leaves a panel.
/// </summary>
internal sealed class TrayPage
{
    private readonly HashSet<ToolStripItem> _settings = new();
    private PhosphorMenuEyebrow? _eyebrowRow;

    public TrayPage(string? eyebrow = null) => Eyebrow = eyebrow;

    /// <summary>The section eyebrow under the Back row; the home page has the header instead.</summary>
    public string? Eyebrow { get; }

    /// <summary>The label of the row that turns to this page (Back names it).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The page Back returns to; null for the home page.</summary>
    public TrayPage? Parent { get; private set; }

    public List<ToolStripItem> Items { get; } = new();

    /// <summary>Raised when the page comes into view on the open menu, and when it leaves (turned away or closed).</summary>
    public event EventHandler? Shown, Hidden;

    internal PhosphorMenuEyebrow? EyebrowRow => Eyebrow == null ? null : _eyebrowRow ??= new PhosphorMenuEyebrow(Eyebrow);

    /// <summary>Adds a row that does something and closes the menu (opens a surface, casts, quits).</summary>
    public T Add<T>(T item) where T : ToolStripItem
    {
        Items.Add(item);
        return item;
    }

    /// <summary>Adds a row that sets something (a choice or a toggle): the menu stays open so the change shows.</summary>
    public T AddSetting<T>(T item) where T : ToolStripItem
    {
        Items.Add(item);
        _settings.Add(item);
        return item;
    }

    /// <summary>Adds a row that turns the menu to <paramref name="page"/>.</summary>
    public PhosphorPageRow AddPage(string text, TrayPage page, string? tip = null)
    {
        page.Parent = this;
        page.Title = text;
        var row = new PhosphorPageRow(text, page);
        if (tip != null) row.ToolTipText = tip;
        Items.Add(row);
        return row;
    }

    /// <summary>Drops every row, for a page rebuilt from live data (the mice found).</summary>
    public void Clear()
    {
        Items.Clear();
        _settings.Clear();
    }

    /// <summary>Whether clicking <paramref name="item"/> keeps the menu open: page turns and settings do.</summary>
    internal bool KeepsOpen(ToolStripItem item) => item is PhosphorPageRow or PhosphorBackRow || _settings.Contains(item);

    /// <summary>This page and every page under it, this one first.</summary>
    public IEnumerable<TrayPage> AndBelow()
    {
        yield return this;
        foreach (var row in Items.OfType<PhosphorPageRow>())
            foreach (var page in row.Page.AndBelow())
                yield return page;
    }

    internal void RaiseShown() => Shown?.Invoke(this, EventArgs.Empty);
    internal void RaiseHidden() => Hidden?.Invoke(this, EventArgs.Empty);
}

/// <summary>A row that turns the menu to its page: its label and a right chevron (PhosphorMenuRenderer).</summary>
internal sealed class PhosphorPageRow : ToolStripMenuItem
{
    public PhosphorPageRow(string text, TrayPage page) : base(text) => Page = page;

    public TrayPage Page { get; }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        (Owner as PhosphorTrayMenu)?.TurnTo(Page);
    }
}

/// <summary>The first row of every page under home: back to the page before (Esc, Backspace and Left do the same).</summary>
internal sealed class PhosphorBackRow : ToolStripMenuItem
{
    public PhosphorBackRow() : base("Back") => ShortcutKeyDisplayString = "Esc";

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        (Owner as PhosphorTrayMenu)?.TurnBack();
    }
}

/// <summary>
/// The tray menu: one panel that turns pages (<see cref="TrayPage"/>) in place instead of opening submenus.
/// <list type="bullet">
/// <item>Placement: centered over the tray icon, above it (below it when the taskbar is along the top), inside
/// the work area (<see cref="PlaceAbove"/>). Its bottom edge stays put as pages change height.</item>
/// <item>Width: every page is as wide as the widest, so a page turn never moves the panel sideways. The header
/// never widens it; it breaks and ellipsizes its subline instead.</item>
/// <item>Dismissal: a click outside it, Esc on the home page, a click on the tray icon, or a row that does
/// something. Pointer movement never closes it, and page rows, Back and setting rows keep it open.</item>
/// </list>
/// </summary>
internal sealed class PhosphorTrayMenu : ContextMenuStrip
{
    private const int ProbeWidth = 4096;

    /// <summary>Each fixed-size hosted control's own width, before the column stretched it.</summary>
    private static readonly ConditionalWeakTable<ToolStripControlHost, StrongBox<int>> HostWidths = new();

    private readonly PhosphorMenuSpacer _top = new(), _bottom = new();
    private readonly PhosphorBackRow _back = new() { Name = "Tray.Back" };
    /// <summary>Each page's label column, so every page lands on the same panel width.</summary>
    private readonly Dictionary<TrayPage, int> _columns = new();
    private string? _fittedFor;
    private bool _measuring, _keepOpen;
    private Rectangle? _icon;
    private Point _pointer;

    public PhosphorTrayMenu(TrayPage home)
    {
        Home = home;
        Page = home;
        ShowImageMargin = false;
    }

    public TrayPage Home { get; }

    /// <summary>The page the panel shows.</summary>
    public TrayPage Page { get; private set; }

    /// <summary>The tray icon's screen rectangle, read as the menu opens; null puts the menu over the pointer.</summary>
    public Func<Rectangle?>? IconBounds { get; set; }

    /// <summary>When the menu last closed, so the click on the tray icon that closed it does not open it again.</summary>
    public DateTime ClosedAtUtc { get; private set; }

    /// <summary>Turns to <paramref name="page"/> once the click that asked for it has finished.</summary>
    public void TurnTo(TrayPage page)
    {
        if (IsHandleCreated) BeginInvoke(() => ShowPage(page));
        else ShowPage(page);
    }

    public void TurnBack()
    {
        if (Page.Parent is TrayPage parent) TurnTo(parent);
    }

    /// <summary>Shows <paramref name="page"/> in the panel now: the page turn itself (the capture and probe call it directly).</summary>
    public void ShowPage(TrayPage page)
    {
        Fit();
        Load(page);
    }

    /// <summary>Shows the page again after its rows changed, if it is the one on screen.</summary>
    public void Refresh(TrayPage page)
    {
        if (Page == page) ShowPage(page);
    }

    private void Load(TrayPage page)
    {
        var before = Page;
        bool turning = Visible && !_measuring && before != page;
        if (turning) before.RaiseHidden();

        SuspendLayout();
        Items.Clear();
        if (page.Parent is TrayPage parent)
        {
            _back.Text = parent.Parent == null ? "Back" : $"Back to {parent.Title}";
            Items.Add(_top);
            Items.Add(_back);
            if (page.EyebrowRow is { } eyebrow) Items.Add(eyebrow);
        }
        foreach (var item in page.Items) Items.Add(item);
        Items.Add(_bottom);
        ShowCheckMargin = HasCheckGutter(page);
        Page = page;
        ResumeLayout(false);

        if (!_measuring && _columns.TryGetValue(page, out int column)) SetStrut(column, fitHeaders: true);
        else PerformLayout();
        if (turning)
        {
            // A page turn swaps every row at once; the new page settles in from a dim panel instead of snapping.
            SurfaceMotion.FadeIn(this, from: 0.45f);
            page.RaiseShown();
        }
    }

    /// <summary>Rows that can be checked get the 33px check gutter; otherwise labels start 12px in (--aura-lead).</summary>
    private static bool HasCheckGutter(TrayPage page) => page.Items.OfType<ToolStripMenuItem>().Any(i => i.Checked || i.CheckOnClick);

    /// <summary>
    /// Sizes every page to the widest one. Each page is its label column plus fixed chrome (check gutter,
    /// paddings), so the chrome is probed per page and its spacer sized to land the page on the shared width.
    /// Laying every page out costs a few ms each, so pages whose rows have not changed are left as they are.
    /// </summary>
    private void Fit()
    {
        string signature = Signature();
        if (signature == _fittedFor) return;
        var showing = Page;
        bool auto = AutoSize;
        _measuring = true;
        AutoSize = false;
        try
        {
            var pages = Home.AndBelow().ToList();
            var chrome = new Dictionary<TrayPage, int>();
            int width = 0;
            foreach (var page in pages)
            {
                Load(page);
                SetStrut(0, fitHeaders: false);
                width = Math.Max(width, PreferredWidth);
                SetStrut(ProbeWidth, fitHeaders: false);
                chrome[page] = PreferredWidth - ProbeWidth;
            }
            foreach (var page in pages) _columns[page] = Math.Max(0, width - chrome[page]);
            Load(showing);
        }
        finally
        {
            _measuring = false;
            AutoSize = auto;
        }
        _fittedFor = signature;
        SetStrut(_columns[showing], fitHeaders: true);
    }

    /// <summary>Everything that changes a page's width: DPI, font, each row's text and shortcut, and its check gutter.</summary>
    private string Signature()
    {
        var sb = new StringBuilder().Append(DeviceDpi).Append('|').Append(Font.Size);
        foreach (var page in Home.AndBelow())
        {
            sb.Append('{').Append(page.Eyebrow).Append(HasCheckGutter(page) ? '+' : '-');
            foreach (var item in page.Items)
            {
                sb.Append(item.Available ? '1' : '0').Append(item.Text).Append('\u0001');
                if (item is ToolStripMenuItem mi) sb.Append(mi.ShortcutKeyDisplayString);
                sb.Append('\u0002');
            }
            sb.Append('}');
        }
        return sb.ToString();
    }

    private int PreferredWidth => GetPreferredSize(Size.Empty).Width;

    private void SetStrut(int width, bool fitHeaders)
    {
        _bottom.ColumnWidth = width;
        // A header spans the label column so its subline can use it, and a fixed-size hosted control (the speed
        // slider) stretches across it; while measuring, both keep their own content width.
        foreach (var header in Items.OfType<PhosphorMenuHeader>())
            header.ColumnWidth = fitHeaders ? width : null;
        foreach (var host in Items.OfType<ToolStripControlHost>().Where(h => !h.AutoSize))
        {
            int own = HostWidths.GetValue(host, h => new StrongBox<int>(h.Width)).Value;
            host.Width = fitHeaders ? Math.Max(own, width - host.Margin.Horizontal) : own;
        }
        // A dropdown reads its non-menu rows' widths from their bounds of the previous layout, so lay out twice:
        // once to size the spacer row, once to size the label column from it.
        PerformLayout();
        PerformLayout();
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        _pointer = Cursor.Position;
        _icon = IconBounds?.Invoke();
        // Every open starts on the home page.
        ShowPage(Home);
        base.OnOpening(e);
        if (e.Cancel) return;
        Location = Place(Size);
        // Clear until OnOpened fades it in, so the first frame is not a full-strength flash.
        if (!PhosphorMotion.ReducedMotion) Opacity = 0;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The panel fades in on dur-disclosure-item like every Phosphor surface (SurfaceMotion), not the system's menu fade.
        SurfaceMotion.FadeIn(this);
        Page.RaiseShown();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        base.OnClosed(e);
        ClosedAtUtc = DateTime.UtcNow;
        Page.RaiseHidden();
    }

    protected override void OnItemClicked(ToolStripItemClickedEventArgs e)
    {
        // Decided before the base class closes the menu for the click.
        _keepOpen = e.ClickedItem != null && Page.KeepsOpen(e.ClickedItem);
        base.OnItemClicked(e);
    }

    protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _keepOpen)
        {
            e.Cancel = true;
        }
        else if (e.CloseReason == ToolStripDropDownCloseReason.Keyboard && Page.Parent != null)
        {
            // Esc on a page goes back a page; on the home page it closes.
            e.Cancel = true;
            TurnBack();
        }
        _keepOpen = false;
        base.OnClosing(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if ((keyData == Keys.Back || keyData == Keys.Left) && Page.Parent != null)
        {
            TurnBack();
            return true;
        }
        if (keyData == Keys.Right && Items.OfType<PhosphorPageRow>().FirstOrDefault(i => i.Selected) is { } row)
        {
            TurnTo(row.Page);
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        // Wherever WinForms or NotifyIcon put it, the menu sits over the icon, and a page turn that changes its
        // height keeps its bottom edge where it was.
        if (!_measuring && Items.Count > 0)
        {
            var at = Place(new Size(width, height));
            x = at.X;
            y = at.Y;
        }
        base.SetBoundsCore(x, y, width, height, specified);
    }

    private Point Place(Size size)
    {
        var target = _icon ?? new Rectangle(_pointer, Size.Empty);
        var work = Screen.FromPoint(new Point(target.Left + target.Width / 2, target.Top + target.Height / 2)).WorkingArea;
        return PlaceAbove(target, size, work, (int)Math.Round(8 * DeviceDpi / 96f));
    }

    /// <summary>
    /// Top-left corner for a <paramref name="size"/> panel centered on <paramref name="icon"/>: above it, or below
    /// it when the icon sits in the top half of the screen (taskbar along the top), kept <paramref name="gap"/>
    /// inside <paramref name="work"/> so it floats clear of the taskbar and the screen edge.
    /// </summary>
    public static Point PlaceAbove(Rectangle icon, Size size, Rectangle work, int gap)
    {
        bool below = icon.Top + icon.Height / 2 < work.Top + work.Height / 2;
        int x = icon.Left + icon.Width / 2 - size.Width / 2;
        int y = below ? icon.Bottom + gap : icon.Top - gap - size.Height;
        return new Point(
            Math.Max(work.Left + gap, Math.Min(x, work.Right - gap - size.Width)),
            Math.Max(work.Top + gap, Math.Min(y, work.Bottom - gap - size.Height)));
    }
}

/// <summary>The tray icon's screen rectangle, from the shell.</summary>
internal static class TrayIconBounds
{
    /// <summary>The icon's rectangle, or null (with <paramref name="error"/> saying why) when the shell does not report it.</summary>
    public static Rectangle? Of(NotifyIcon icon, out string error)
    {
        error = string.Empty;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(NotifyIcon).GetField("_window", flags)?.GetValue(icon) is not NativeWindow window) { error = "no _window"; return null; }
        object? rawId = typeof(NotifyIcon).GetField("_id", flags)?.GetValue(icon);
        if (rawId == null) { error = "no _id"; return null; }
        var ident = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = Convert.ToUInt32(rawId) };
        int hr = Shell_NotifyIconGetRect(ref ident, out var rc);
        if (hr != 0) { error = $"Shell_NotifyIconGetRect 0x{hr:X8}"; return null; }
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
}
