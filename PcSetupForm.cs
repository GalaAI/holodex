namespace Omniwand;

/// <summary>
/// Your ArkStation: what Holodex found on this PC, grouped (models and engines, MCP servers, apps it can connect,
/// command line tools, services, installed software, local endpoints, gear, hardware and firmware). Each row shows its
/// plain name first and its Omnitek type as a small tag. Rows that can join the user's own Vault carry one action
/// (Connect, Open or Get it); large groups show a summary card that opens on demand; hardware and firmware are
/// inventory only. A single Scan again sits in the header. Nothing connects or installs without the user's OK.
/// </summary>
internal sealed class PcSetupForm : Form
{
    private const int CollapseOver = 8;
    private const int MaxRowsPerGroup = 150;

    private readonly ModelInventory? _models;
    private readonly DataLabel _status;
    private readonly LedRow _counts;
    private readonly PhosphorButton _scan;
    private readonly ThreadView _list;
    private readonly HashSet<InvGroup> _expanded = new();
    private Task<PcInventory>? _scanning;
    private InvGroup[]? _only;

    /// <summary>How the panel reads this PC; renders and tests swap it.</summary>
    public Func<PcInventoryEnv> Env { get; set; } = () => new PcInventoryEnv();

    public PcInventory Inventory { get; private set; } = PcInventory.Empty;

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
        Text = "Your PC";
        Size = new Size(680, 760);
        Padding = WidgetChrome.FramePadding();

        _list = new ThreadView("PcSetup.List") { Dock = DockStyle.Fill, FollowNewest = false };
        _status = new DataLabel(IdleStatus) { Name = "PcSetup.Status", Height = 22 };
        _counts = new LedRow { Name = "PcSetup.Counts", Dock = DockStyle.Top, Height = 22, Wrap = true };
        _scan = new PhosphorButton("Scan again") { Name = "PcSetup.ScanAgain", Width = 140, Height = 38 };
        _scan.Click += async (_, _) => await ScanAsync();

        Controls.Add(_list);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(_counts);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S1));
        Controls.Add(_status);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "HOLODEX // INVENTORY // THIS PC", "Your PC", PhosphorTheme.Patina, _scan);
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

    private const string IdleStatus = "Finds what this PC can do. Nothing connects or installs without your OK.";

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

    /// <summary>Scans (one at a time; a second call joins the running scan) and lists what was found.</summary>
    public Task<PcInventory> ScanAsync()
    {
        if (_scanning is { IsCompleted: false } running) return running;
        _scanning = RunScanAsync();
        return _scanning;
    }

    private async Task<PcInventory> RunScanAsync()
    {
        _scan.Enabled = false;
        SetStatus("Looking at this PC: software, services, engines, MCP servers, tools and hardware...");
        if (Inventory.Items.Count == 0)
            using (_list.Batch())
            {
                _list.Clear();
                _list.AddPending("SCANNING // THIS PC", PhosphorGlyph.Sparkles);
            }
        try
        {
            var env = Env();
            var inventory = await Task.Run(() => PcInventoryScan.ScanAsync(env));
            ShowInventory(inventory);
            return inventory;
        }
        catch (Exception ex)
        {
            SetStatus($"The scan stopped: {ex.Message}");
            return Inventory;
        }
        finally
        {
            _scan.Enabled = true;
        }
    }

    private static readonly InvGroup[] InventoryOnly = { InvGroup.Gear, InvGroup.Hardware, InvGroup.Firmware };
    private static readonly InvGroup[] Actionable = { InvGroup.Engines, InvGroup.Mcp, InvGroup.OnThisPc };

    private static string Heading(InvGroup g) => g switch
    {
        InvGroup.Engines => "MODELS AND ENGINES",
        InvGroup.Mcp => "MCP SERVERS FROM YOUR OTHER AI APPS",
        InvGroup.OnThisPc => "APPS HOLODEX CAN CONNECT",
        InvGroup.Cli => "COMMAND LINE TOOLS",
        InvGroup.Services => "BACKGROUND SERVICES",
        InvGroup.Apps => "INSTALLED SOFTWARE",
        InvGroup.Endpoints => "LOCAL ENDPOINTS",
        InvGroup.Gear => "DEVICES // WHAT YOUR AGENTS CAN SEE",
        InvGroup.Hardware => "PC HARDWARE // WHAT YOUR AGENTS CAN SEE",
        _ => "FIRMWARE // WHAT YOUR AGENTS CAN SEE"
    };

    /// <summary>The count row's name for a group: the heading's first words, so the two read as the same thing.</summary>
    internal static string CountLabel(InvGroup g) => g switch
    {
        InvGroup.Engines => "MODELS AND ENGINES",
        InvGroup.Mcp => "MCP SERVERS",
        InvGroup.OnThisPc => "APPS TO CONNECT",
        InvGroup.Cli => "COMMAND LINE TOOLS",
        InvGroup.Services => "SERVICES",
        InvGroup.Apps => "INSTALLED SOFTWARE",
        InvGroup.Endpoints => "LOCAL ENDPOINTS",
        InvGroup.Gear => "DEVICES",
        InvGroup.Hardware => "PC HARDWARE",
        _ => "FIRMWARE"
    };

    /// <summary>What a row is, in plain words: the Omnitek tag's kind (service, MCP server, App), never its canon type.</summary>
    internal static string PlainKind(OmnitekTag tag) =>
        tag.Sub.Length > 0 ? char.ToUpperInvariant(tag.Sub[0]) + tag.Sub[1..] : tag.Type == "Gear" ? "Device" : tag.Type;

    /// <summary>Lists an inventory (also the render path, which hands one in).</summary>
    public void ShowInventory(PcInventory inventory)
    {
        Inventory = inventory;
        int ready = inventory.Items.Count(i => i.Action == InvAction.Connect);
        string unfinished = inventory.Unfinished.Count > 0 ? $" Not finished: {string.Join(", ", inventory.Unfinished)}." : "";
        SetStatus($"Found {inventory.Items.Count} items on this PC, {ready} ready to connect. Nothing connects without your OK.{unfinished}");
        // One count per list group below, under the same name, so every number points at rows the user can see.
        var leds = new List<(Color, string)>();
        foreach (var g in inventory.Items.GroupBy(i => i.Group).OrderBy(g => g.Key))
            leds.Add((Actionable.Contains(g.Key) ? PhosphorTheme.Patina : PhosphorTheme.Dim, $"{CountLabel(g.Key)} {g.Count()}"));
        leds.Add((PhosphorTheme.Moss, $"SAVED IN YOUR VAULT {inventory.VaultConnections}"));
        _counts.Set(leds.ToArray());
        _counts.Height = Math.Max(22, _counts.HeightFor(Math.Max(200, ClientSize.Width - Padding.Horizontal)));

        using (_list.Batch())
        {
            _list.Clear();
            foreach (var group in inventory.Items.Where(i => _only == null || _only.Contains(i.Group)).GroupBy(i => i.Group).OrderBy(g => g.Key))
            {
                var rows = group.ToList();
                _list.AddSection($"{Heading(group.Key)} // {rows.Count}");
                // Groups with something to connect always show their rows; long inventory groups start as one card.
                bool collapse = rows.Count > CollapseOver && !Actionable.Contains(group.Key) && !_expanded.Contains(group.Key);
                if (collapse || group.Key is InvGroup.Cli or InvGroup.Apps) AddGroupCard(group.Key, rows, collapse);
                if (collapse) continue;
                foreach (var item in rows.Take(MaxRowsPerGroup)) AddRow(item);
            }
        }
        FitHeight();
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>
    /// The card that stands for a large group (its first names and a Show all) and, for command line tools and
    /// installed software, the one way Holodex uses them: the approved PC command line.
    /// </summary>
    private void AddGroupCard(InvGroup group, List<InvItem> rows, bool collapsed)
    {
        string names = string.Join(", ", rows.Take(10).Select(r => r.Label));
        string more = rows.Count > 10 ? $", and {rows.Count - 10} more." : ".";
        string use = group is InvGroup.Cli or InvGroup.Apps
            ? HolodexTools.CliConnected ? " Nemotron can use them through the approved PC command line." : " Connect the PC command line and Nemotron can use them, asking you before every command."
            : "";
        var tag = rows[0].Tag;
        var card = _list.Add(ThreadTone.Step, collapsed ? "AT A GLANCE" : "HOW HOLODEX USES THEM",
            collapsed ? $"{names}{more}{use}" : use.Trim(), PhosphorGlyph.Vault,
            new[] { (PhosphorTheme.Ghost, tag.Text.ToUpperInvariant()) });
        card.Name = $"PcSetup.Group.{group}";
        var actions = new List<CardAction>();
        if (rows.Count > CollapseOver && !Actionable.Contains(group))
            actions.Add(new CardAction("PcSetup.ShowAll", PhosphorGlyph.BookOpen, collapsed ? "Show all" : "Show less", (_, _) =>
            {
                if (!_expanded.Add(group)) _expanded.Remove(group);
                ShowInventory(Inventory);
            }));
        if (group is InvGroup.Cli or InvGroup.Apps && !HolodexTools.CliConnected && HolodexVault.Find(HolodexTools.CliId) is { } cli)
            actions.Add(new CardAction("PcSetup.ConnectCli", PhosphorGlyph.Link, "Connect the PC command line", async (c, _) =>
            {
                await HolodexVault.ConnectLocalAsync(cli, url: "powershell");
                c.SetMeta("CONNECTED // HOLODEX ASKS BEFORE EVERY COMMAND");
                SetStatus("PC command line connected. Holodex asks before every command.");
            }));
        card.SetFooter("", actions.ToArray());
    }

    private void AddRow(InvItem item)
    {
        var leds = new List<(Color, string)>();
        if (item.Source is PcToolResult tool)
            leds.Add(tool.State switch
            {
                PcToolState.Running => (PhosphorTheme.Moss, "Running"),
                PcToolState.Installed => (PhosphorTheme.Dim, "Installed"),
                _ => (PhosphorTheme.Ghost, "Not found")
            });
        if (item.Version.Length > 0) leds.Add((PhosphorTheme.Ghost, char.IsDigit(item.Version[0]) ? "v" + item.Version : item.Version));
        leds.Add((PhosphorTheme.Dim, PlainKind(item.Tag)));
        if (item.InVault) leds.Add((PhosphorTheme.Patina, "In your vault"));

        // A connected MCP server says whether answers use its tools yet, never just "connected".
        string detail = item.Detail;
        if (McpUse(item) is { } use)
        {
            leds.Insert(0, (use == McpBridges.ToolsUsedLabel ? PhosphorTheme.Moss : PhosphorTheme.Ghost, use));
            detail += $" {use}.";
        }

        var tone = item.InVault ? ThreadTone.Ok : item.Action == InvAction.Connect ? ThreadTone.Action : ThreadTone.Step;
        var card = _list.Add(tone, item.Label.ToUpperInvariant(), detail, Glyph(item.Group), leds);
        card.Name = $"PcSetup.{item.Id}";
        if (InventoryOnly.Contains(item.Group)) return;

        var actions = new List<CardAction>();
        switch (item.Action)
        {
            case InvAction.Connect:
                actions.Add(new CardAction("PcSetup.Connect", PhosphorGlyph.Link, "Connect", async (c, _) => await ConnectAsync(item, c)));
                break;
            case InvAction.Open when item.Source is PcToolResult open:
                actions.Add(new CardAction("PcSetup.Open", PhosphorGlyph.ExternalLink, "Open", (c, _) =>
                {
                    PcSetupScan.Open(open);
                    c.SetMeta("OPENING // SCAN AGAIN ONCE IT IS UP");
                }));
                break;
            case InvAction.GetIt when item.Source is PcToolResult missing:
                actions.Add(new CardAction("PcSetup.GetIt", PhosphorGlyph.ExternalLink, "Get it", (c, _) =>
                {
                    PcSetupScan.OpenUrl(missing.Tool.DownloadUrl);
                    c.SetMeta("DOWNLOAD PAGE OPEN");
                }));
                if (missing.Tool.WingetId.Length > 0)
                    actions.Add(new CardAction("PcSetup.Winget", PhosphorGlyph.Plus, "Install with winget", async (c, _) =>
                    {
                        c.SetMeta("WAITING FOR YOUR OK");
                        string said = await PcSetupScan.InstallWithWingetAsync(missing.Tool);
                        c.SetMeta(said.StartsWith("The user declined", StringComparison.Ordinal) ? "NOT INSTALLED" : "WINGET IS RUNNING // SCAN AGAIN WHEN IT FINISHES");
                    }));
                break;
        }
        if (actions.Count > 0) card.SetFooter("", actions.ToArray());
    }

    /// <summary>The truthful use line for a connected MCP row (stdio through the bridge, or an HTTP server); null otherwise.</summary>
    internal static string? McpUse(InvItem item) => item.Group != InvGroup.Mcp ? null : item.Source switch
    {
        McpConfiguredServer s => McpBridges.UsabilityLabel(s.IsStdio, item.InVault),
        _ => McpBridges.UsabilityLabel(stdio: false, item.InVault)
    };

    private static PhosphorGlyph Glyph(InvGroup g) => g switch
    {
        InvGroup.Engines => PhosphorGlyph.Sparkles,
        InvGroup.Mcp => PhosphorGlyph.Link,
        InvGroup.Gear or InvGroup.Hardware or InvGroup.Firmware => PhosphorGlyph.Box,
        _ => PhosphorGlyph.Vault
    };

    private async Task ConnectAsync(InvItem item, ThreadCard card)
    {
        card.SetMeta("CONNECTING");
        try
        {
            string said = await PcInventoryScan.ConnectAsync(item, _models);
            SetStatus(said);
            PhosphorToast.Show(item.Label, said, "HOLODEX // YOUR PC", 3600, ToastTone.Success);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            card.SetMeta("NOT CONNECTED");
            SetStatus($"{item.Label} did not connect: {ex.Message}");
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
        if (Inventory.Items.Count == 0) await ScanAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        SurfaceCapture.SaveForm(this, path);
        return path;
    }

    /// <summary>
    /// Draws the panel offscreen to a PNG for an inventory already in hand (tests and renders). <paramref name="only"/>
    /// lists just those groups (the summary still counts everything), so every part of a long inventory can be shown.
    /// </summary>
    public string CaptureTo(string path, PcInventory inventory, InvGroup[]? only = null, params InvGroup[] expand)
    {
        _expanded.Clear();
        foreach (var g in expand) _expanded.Add(g);
        _only = only;
        try
        {
            ShowInventory(inventory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            SurfaceCapture.SaveForm(this, path);
        }
        finally { _only = null; }
        return path;
    }
}
