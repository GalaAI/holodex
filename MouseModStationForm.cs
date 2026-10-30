namespace Omniwand;

/// <summary>
/// The Mouse Mod Station: everything about the mouse in one Holodex window. Down the left, the mouse in use (it
/// follows the one you move), its speed and the pointer look. Beside it, Input Studio, the cursor designer and the
/// button and gesture bindings, which the Input Studio service in Ark (apps/input-studio) serves on the Input Spine
/// port. That service keeps the parts a page cannot do (writing the .cur/.ani files and the Windows cursor scheme)
/// and stays the Input Spine that Spells, the Stream Deck and the AHK bridge send to.
/// </summary>
internal sealed class MouseModStationForm : ArkPageForm
{
    private const int RailWidth = 340;
    private readonly LedRow _mouseLine;
    private readonly LedRow _speedLine;
    private readonly Panel _styleRow;
    private readonly Action<CursorIconStyle> _onCursorStyleChanged;

    public MouseModStationForm(Action<CursorIconStyle> onCursorStyleChanged)
        : base("MouseModStation", "Mouse Mod Station", "HOLODEX // MOUSE MOD STATION", PhosphorTheme.Moss, new Size(1440, 880))
    {
        _onCursorStyleChanged = onCursorStyleChanged;

        var rail = new Panel { Width = RailWidth, BackColor = PhosphorTheme.Surface, Padding = new Padding(0, 0, 14, 0), Name = "MouseModStation.Rail" };

        // The mouse in use, then its speed. Holodex sets the Windows pointer speed for every mouse; a mouse's own DPI
        // lives in its maker's app, so the setting is speed.
        _mouseLine = new LedRow { Dock = DockStyle.Top, Height = 44, Wrap = true, Name = "MouseModStation.MouseLine" };
        _speedLine = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "MouseModStation.SpeedLine" };
        var slider = new PhosphorSpeedSlider { Dock = DockStyle.Top, Height = 46, Name = "MouseModStation.SpeedSlider" };
        var speedLabeled = WidgetChrome.Labeled("Mouse speed // slow to fast", slider, 46);

        // The pointer look Holodex draws over the Windows cursor.
        _styleRow = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = PhosphorTheme.Surface, Name = "MouseModStation.LookRow" };
        var styles = new (string Label, CursorIconStyle Style)[]
        {
            ("Aperture", CursorIconStyle.ArkAperture),
            ("Magic wand", CursorIconStyle.RelicWand),
            ("Arrow", CursorIconStyle.PhosphorArrow),
            ("Target", CursorIconStyle.ViewfinderReticle)
        };
        int buttonWidth = (RailWidth - 14 - 6) / 2;
        for (int i = 0; i < styles.Length; i++)
        {
            var (label, style) = styles[i];
            var btn = new PhosphorButton(label)
            {
                Location = new Point(i % 2 * (buttonWidth + 6), i / 2 * 42 + 2),
                Size = new Size(buttonWidth, 36),
                Tag = style,
                Name = $"MouseModStation.Look.{style}"
            };
            btn.Click += (_, _) => PickStyle(style);
            _styleRow.Controls.Add(btn);
        }
        var lookLabeled = WidgetChrome.Labeled("Pointer look", _styleRow, 84);

        var note = new ThreadView("MouseModStation.Note") { Dock = DockStyle.Fill, FollowNewest = false };
        note.Add(ThreadTone.Step, "Design // Input Studio",
            "Shape and animate the cursor, build and install it, and choose what each button and gesture does, on the right.");

        // Dock.Top stacks the last added on top, so add bottom to top.
        rail.Controls.Add(note);
        rail.Controls.Add(WidgetChrome.Spacer(14));
        rail.Controls.Add(lookLabeled);
        rail.Controls.Add(WidgetChrome.Spacer(14));
        rail.Controls.Add(speedLabeled);
        rail.Controls.Add(_speedLine);
        rail.Controls.Add(WidgetChrome.Spacer(6));
        rail.Controls.Add(_mouseLine);
        AddRail(rail);

        MouseHardwareAndWandEngine.SpeedChanged += speed => OnUi(() => ShowSpeed(speed));
        MouseHardwareAndWandEngine.ActiveMouseChanged += _ => OnUi(ShowMouse);
        ShowMouse();
        ShowStyle(MouseHardwareAndWandEngine.ActiveCursorStyle);
    }

    protected override string PageUrl => ArkStationClient.DefaultInputSpineUrl + "/";

    protected override (string Service, string Explanation) FailureText =>
        ("INPUT STUDIO", "The cursor designer is served by the Input Studio service in Ark (apps/input-studio), which the Ark runtime keeps running; start the runtime and try again. Your mouse, speed and pointer look still work from the left.");

    /// <summary>Offered on an Ark install; on a public install there is no Input Studio service to open.</summary>
    public static bool Available(ArkStationClient ark) => ark.ArkInstalled;

    public async void Summon(Point cursorPos)
    {
        ShowMouse();
        await PresentAsync(cursorPos);
    }

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void ShowMouse()
    {
        var hw = MouseHardwareAndWandEngine.ActiveMouse;
        int count = MouseHardwareAndWandEngine.KnownMice.Count;
        _mouseLine.Set((PhosphorTheme.Patina, $"Mouse // {hw.Name}"), (PhosphorTheme.Moss, "IN USE"),
            (PhosphorTheme.Ghost, count > 1 ? $"{count} MICE FOUND" : "1 MOUSE FOUND"));
        ShowSpeed(MouseHardwareAndWandEngine.GetWindowsMouseSpeed());
    }

    private void ShowSpeed(int speed) => _speedLine.Set(
        (PhosphorTheme.Patina, $"Speed {speed} of 20"),
        (PhosphorTheme.Ghost, MouseHardwareAndWandEngine.ActiveMouse.CanSetDpi ? "DPI SET HERE" : "DPI IS SET IN YOUR MOUSE'S OWN APP"));

    private void PickStyle(CursorIconStyle style)
    {
        MouseHardwareAndWandEngine.ActiveCursorStyle = style;
        _onCursorStyleChanged(style);
        ShowStyle(style);
    }

    private void ShowStyle(CursorIconStyle style)
    {
        foreach (var b in _styleRow.Controls.OfType<PhosphorButton>())
        {
            b.Selected = b.Tag is CursorIconStyle s && s == style;
            b.Invalidate();
        }
    }
}
