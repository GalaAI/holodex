namespace Omniwand;

/// <summary>
/// Set up this PC: one row per local tool with what the scan found, one action (Connect, Open or Get it), and a
/// single Scan again in the header. Missing tools also offer Install with winget, which runs only after the user
/// approves the exact command. Connect writes the user's own Vault entry on this PC (<see cref="PcSetupScan"/>).
/// </summary>
internal sealed class PcSetupForm : Form
{
    private readonly ModelInventory? _models;
    private readonly DataLabel _status;
    private readonly PhosphorButton _scan;
    private readonly ThreadView _list;
    private Task<IReadOnlyList<PcToolResult>>? _scanning;

    /// <summary>How the panel reads this PC; renders and tests swap it.</summary>
    public Func<PcProbeEnv> Env { get; set; } = () => new PcProbeEnv();

    public IReadOnlyList<PcToolResult> Results { get; private set; } = Array.Empty<PcToolResult>();

    public PcSetupForm(ModelInventory? models)
    {
        _models = models;
        PhosphorFonts.EnsureLoaded();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "PcSetup";
        Text = "Set up this PC";
        Size = new Size(640, 720);
        Padding = WidgetChrome.FramePadding();

        _list = new ThreadView("PcSetup.List") { Dock = DockStyle.Fill, FollowNewest = false };
        _status = new DataLabel(IdleStatus) { Name = "PcSetup.Status", Height = 22 };
        _scan = new PhosphorButton("Scan again") { Name = "PcSetup.ScanAgain", Width = 140, Height = 38 };
        _scan.Click += async (_, _) => await ScanAsync();

        Controls.Add(_list);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(_status);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "HOLODEX // ON THIS PC", "Set up this PC", PhosphorTheme.Patina, _scan);
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

    private const string IdleStatus = "Finds the tools on this PC. Nothing installs without your OK.";

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { SurfaceMotion.Dismiss(this); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }

    /// <summary>Shows the panel centred on the owner's screen, or the cursor's when there is no owner.</summary>
    public void Summon(IWin32Window? owner)
    {
        var at = owner is Control { IsHandleCreated: true } c ? c.RectangleToScreen(c.ClientRectangle).Location : Cursor.Position;
        var screen = Screen.FromPoint(at).WorkingArea;
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + Math.Max(20, (screen.Height - Height) / 2));
        if (!Visible) SurfaceMotion.Present(this);
        Activate();
    }

    /// <summary>Scans (once at a time; a second call joins the running scan) and lists what was found.</summary>
    public Task<IReadOnlyList<PcToolResult>> ScanAsync()
    {
        if (_scanning is { IsCompleted: false } running) return running;
        _scanning = RunScanAsync();
        return _scanning;
    }

    private async Task<IReadOnlyList<PcToolResult>> RunScanAsync()
    {
        _scan.Enabled = false;
        SetStatus("Looking at this PC...");
        if (Results.Count == 0)
        {
            using (_list.Batch())
            {
                _list.Clear();
                _list.AddPending("SCANNING // THIS PC", PhosphorGlyph.Sparkles);
            }
        }
        try
        {
            var env = Env();
            var results = await Task.Run(() => PcSetupScan.ScanAsync(env));
            ShowResults(results);
            return results;
        }
        catch (Exception ex)
        {
            SetStatus($"The scan stopped: {ex.Message}");
            return Results;
        }
        finally
        {
            _scan.Enabled = true;
        }
    }

    /// <summary>Lists scan results (also the render path, which hands in fixed results).</summary>
    public void ShowResults(IReadOnlyList<PcToolResult> results)
    {
        Results = results;
        int found = results.Count(r => r.Found);
        int ready = results.Count(r => r.InVault);
        SetStatus(found == 0
            ? "Nothing found yet. Get it opens each tool's own download page."
            : $"Found {found} of {results.Count}{(ready > 0 ? $", {ready} in your vault" : "")}. Nothing installs without your OK.");
        using (_list.Batch())
        {
            _list.Clear();
            foreach (var r in results) AddRow(r);
        }
        FitHeight();
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AddRow(PcToolResult r)
    {
        var leds = new List<(Color, string)>
        {
            r.State switch
            {
                PcToolState.Running => (PhosphorTheme.Moss, "Running"),
                PcToolState.Installed => (PhosphorTheme.Dim, "Installed"),
                _ => (PhosphorTheme.Ghost, "Not found")
            }
        };
        if (r.Version.Length > 0) leds.Add((PhosphorTheme.Ghost, r.Version.StartsWith('v') ? r.Version : "v" + r.Version));
        if (r.InVault) leds.Add((PhosphorTheme.Patina, "In your vault"));

        var tone = r.InVault ? ThreadTone.Ok : r.State == PcToolState.Running ? ThreadTone.Action : ThreadTone.Step;
        string eyebrow = r.Tool.Id == PcSetupScan.StreamDeckMcpId ? "MCP" : "LOCAL";
        var card = _list.Add(tone, $"{eyebrow} // {r.Tool.Label.ToUpperInvariant()}", $"{r.Tool.Blurb} {r.Detail}", PhosphorGlyph.Vault, leds);
        card.Name = $"PcSetup.{r.Tool.Id}";

        var actions = new List<CardAction>();
        switch (r.Action)
        {
            case PcSetupAction.Connect:
                actions.Add(new CardAction("PcSetup.Connect", PhosphorGlyph.Link, "Connect", async (c, _) => await ConnectAsync(r, c)));
                break;
            case PcSetupAction.Open:
                actions.Add(new CardAction("PcSetup.Open", PhosphorGlyph.ExternalLink, "Open", (c, _) =>
                {
                    PcSetupScan.Open(r);
                    c.SetMeta("OPENING // SCAN AGAIN ONCE IT IS UP");
                }));
                break;
            case PcSetupAction.GetIt:
                actions.Add(new CardAction("PcSetup.GetIt", PhosphorGlyph.ExternalLink, "Get it", (c, _) =>
                {
                    PcSetupScan.OpenUrl(r.Tool.DownloadUrl);
                    c.SetMeta("DOWNLOAD PAGE OPEN");
                }));
                if (r.Tool.WingetId.Length > 0)
                    actions.Add(new CardAction("PcSetup.Winget", PhosphorGlyph.Plus, "Install with winget", async (c, _) =>
                    {
                        c.SetMeta("WAITING FOR YOUR OK");
                        string said = await PcSetupScan.InstallWithWingetAsync(r.Tool);
                        bool declined = said.StartsWith("The user declined", StringComparison.Ordinal);
                        c.SetMeta(declined ? "NOT INSTALLED" : "WINGET IS RUNNING // SCAN AGAIN WHEN IT FINISHES");
                    }));
                break;
        }
        card.SetFooter("", actions.ToArray());
    }

    private async Task ConnectAsync(PcToolResult r, ThreadCard card)
    {
        card.SetMeta("CONNECTING");
        try
        {
            string said = await PcSetupScan.ConnectAsync(r, _models);
            SetStatus(said);
            PhosphorToast.Show($"{r.Tool.Label}", said, "HOLODEX // SET UP THIS PC", 3600, ToastTone.Success);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            card.SetMeta("NOT CONNECTED");
            SetStatus($"{r.Tool.Label} did not connect: {ex.Message}");
        }
    }

    /// <summary>Sizes the panel to its rows, up to the screen's height; the list scrolls past that.</summary>
    private void FitHeight()
    {
        int chrome = Padding.Vertical + Controls.Cast<Control>().Where(c => c != _list && c.Dock is DockStyle.Top or DockStyle.Bottom).Sum(c => c.Height);
        int want = chrome + _list.ContentHeight(_list.FitWidth) + PhosphorTheme.S2;
        var area = (Visible ? Screen.FromControl(this) : Screen.FromPoint(Location)).WorkingArea;
        Height = Math.Min(want, area.Height - 40);
    }

    /// <summary>Draws the panel offscreen to a PNG (pipe <c>holodex:setup:capture:&lt;png&gt;</c>), scanning first when it has not.</summary>
    public async Task<string> CaptureAsync(string path)
    {
        if (Results.Count == 0) await ScanAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        SurfaceCapture.SaveForm(this, path);
        return path;
    }

    /// <summary>Draws the panel offscreen to a PNG for results already in hand (tests and renders).</summary>
    public string CaptureTo(string path, IReadOnlyList<PcToolResult> results)
    {
        ShowResults(results);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        SurfaceCapture.SaveForm(this, path);
        return path;
    }
}
