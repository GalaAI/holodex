namespace Omniwand;

/// <summary>
/// The Vault in Holodex: Inventory // Adapters. The universal intake sits on top (paste a key, an ENV_NAME=value line,
/// or an MCP server link), then the user's connections with copy, rotate, check and delete, then the adapter catalog
/// (sign-in links for MCP servers, keys for models and cloud services, services running on this PC). It works with no
/// Ark and no Hivemind account; everything it stores is in this PC's Credential Manager (<see cref="HolodexVault"/>).
/// </summary>
internal sealed class VaultForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly PhosphorField _field;
    private readonly PhosphorBadge _forBadge;
    private readonly PhosphorButton _submit;
    private readonly PhosphorButton _cancel;
    private readonly DataLabel _status;
    private readonly PhosphorButton _connectedTab;
    private readonly PhosphorButton _catalogTab;
    private readonly ThreadView _list;
    private VaultProvider? _pinned;
    private VaultEntry? _rotating;
    private bool _showCatalog;
    private bool _busy;

    public VaultForm(ArkStationClient ark)
    {
        _ark = ark;
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
        Name = "Vault";
        Text = "Vault";
        Size = new Size(720, 820);
        Padding = WidgetChrome.FramePadding();

        _list = new ThreadView("Vault.List") { Dock = DockStyle.Fill, FollowNewest = false };

        _connectedTab = new PhosphorButton("Connected") { Name = "Vault.ConnectedTab", Width = 170, Left = 0, Top = 0 };
        _catalogTab = new PhosphorButton("Catalog") { Name = "Vault.CatalogTab", Width = 150, Top = 0 };
        _connectedTab.Click += (_, _) => ShowTab(catalog: false);
        _catalogTab.Click += (_, _) => ShowTab(catalog: true);
        var tabs = new Panel { Dock = DockStyle.Top, Height = PhosphorTheme.TouchMin, BackColor = Color.Transparent, Name = "Vault.Tabs" };
        tabs.Controls.Add(_connectedTab);
        tabs.Controls.Add(_catalogTab);
        tabs.Layout += (_, _) => _catalogTab.Left = _connectedTab.Right + PhosphorTheme.S2;

        _status = new DataLabel(IdleStatus) { Name = "Vault.IntakeStatus", Height = 22 };

        _forBadge = new PhosphorBadge("DETECT AUTOMATICALLY", PhosphorBadgeState.Unstated) { Name = "Vault.IntakeFor", Width = 260, Left = 0 };
        _cancel = new PhosphorButton("Cancel") { Name = "Vault.IntakeCancel", Width = 120, Visible = false };
        _submit = new PhosphorButton("Add to vault", PhosphorButtonVariant.Primary) { Name = "Vault.IntakeButton", Width = 170 };
        _cancel.Click += (_, _) => ResetIntake();
        _submit.Click += async (_, _) => await SubmitAsync();
        var actions = new Panel { Dock = DockStyle.Top, Height = PhosphorTheme.TouchMin, BackColor = Color.Transparent, Name = "Vault.IntakeActions" };
        actions.Controls.Add(_forBadge);
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_submit);
        actions.Layout += (_, _) =>
        {
            _submit.Left = actions.ClientSize.Width - _submit.Width;
            _cancel.Left = _submit.Left - PhosphorTheme.S2 - _cancel.Width;
            _forBadge.Top = (actions.ClientSize.Height - _forBadge.Height) / 2;
        };

        _field = new PhosphorField("Paste an API key, an ENV_NAME=value line, or an MCP server link", multiline: false, PhosphorTheme.TouchMin, mono: true)
        { Name = "Vault.IntakeField" };
        _field.Box.Name = "Vault.IntakeBox";
        _field.Box.UseSystemPasswordChar = false;
        _field.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SubmitAsync();
        };
        var intake = WidgetChrome.Labeled("UNIVERSAL INTAKE // KEY, ENV LINE OR MCP LINK", _field, PhosphorTheme.TouchMin);
        intake.Name = "Vault.Intake";

        Controls.Add(_list);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(tabs);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S3));
        Controls.Add(_status);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S1));
        Controls.Add(actions);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(intake);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "INVENTORY // ADAPTERS // KEYMASTER VAULT", "Vault", PhosphorTheme.Coral, null);
        WidgetDrag.Enable(this);
        ResumeLayout(false);
    }

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

    /// <summary>Opens the Vault centred on the screen under the cursor, on Connected, or on the Catalog when nothing is connected yet.</summary>
    public void Summon(Point cursorPos, bool? catalog = null)
    {
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Size = new Size(Math.Min(Width, screen.Width - 40), Math.Min(Height, screen.Height - 40));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        ShowTab(catalog ?? HolodexVault.Load().Count == 0);
        if (!Visible) SurfaceMotion.Present(this);
        Activate();
        _field.Box.Focus();
        _ = CheckUncheckedAsync();
    }

    /// <summary>Checks keys and services that have never been checked (a key saved before the Vault, say), once per open.</summary>
    private async Task CheckUncheckedAsync()
    {
        var pending = HolodexVault.Load().Where(e => e.CheckedAt == null && e.Kind != VaultKind.McpServer).ToList();
        if (pending.Count == 0) return;
        await Task.WhenAll(pending.Select(e => HolodexVault.VerifyAsync(e)));
        if (Visible && !_showCatalog) ShowTab(catalog: false);
    }

    // ---- intake ----

    private void PinProvider(VaultProvider provider)
    {
        _rotating = null;
        _pinned = provider;
        _forBadge.Text = $"FOR // {provider.Label.ToUpperInvariant()}";
        _forBadge.State = PhosphorBadgeState.Accent;
        _field.Box.PlaceholderText = $"Paste your {provider.Label} key";
        _submit.Text = "Add to vault";
        _cancel.Visible = true;
        SetStatus($"Paste the {provider.Label} key, then Add to vault.");
        _field.Box.Focus();
    }

    private void StartRotate(VaultEntry entry)
    {
        _pinned = null;
        _rotating = entry;
        _forBadge.Text = $"ROTATE // {entry.Label.ToUpperInvariant()}";
        _forBadge.State = PhosphorBadgeState.Accent;
        _field.Box.PlaceholderText = $"Paste the new {entry.Label} key";
        _submit.Text = "Rotate";
        _cancel.Visible = true;
        SetStatus($"The new key replaces the old one in place. Revoke the old one after.");
        _field.Box.Focus();
    }

    private void ResetIntake()
    {
        _pinned = null;
        _rotating = null;
        _field.Box.Clear();
        _forBadge.Text = "DETECT AUTOMATICALLY";
        _forBadge.State = PhosphorBadgeState.Unstated;
        _field.Box.PlaceholderText = "Paste an API key, an ENV_NAME=value line, or an MCP server link";
        _submit.Text = "Add to vault";
        _cancel.Visible = false;
        SetStatus(IdleStatus);
    }

    private const string IdleStatus = "Keys stay in Credential Manager on this PC, never in a file or a log.";

    private void SetStatus(string text) => _status.Text = text;

    private async Task SubmitAsync()
    {
        if (_busy) return;
        // Read once and clear at once: the key never stays in the box.
        string raw = _field.Box.Text.Trim();
        _field.Box.Clear();
        if (raw.Length == 0)
        {
            SetStatus("Paste a key, an ENV_NAME=value line or an MCP link first.");
            return;
        }
        _busy = true;
        _submit.Enabled = false;
        try
        {
            if (_rotating is { } rotating) await RotateAsync(rotating, raw);
            else if (HolodexVault.LooksLikeUrl(raw, out var uri)) await ConnectUrlAsync(uri);
            else await AddKeyAsync(raw);
        }
        catch (Exception ex)
        {
            SetStatus($"Not added: {ex.Message}");
            PhosphorToast.Show("Not added to the vault", ex.Message, "HOLODEX // VAULT", 4200, ToastTone.Error);
        }
        finally
        {
            _busy = false;
            _submit.Enabled = true;
        }
    }

    private async Task AddKeyAsync(string raw)
    {
        SetStatus("Reading the key...");
        var found = _pinned != null
            ? HolodexVault.Classify(raw) with { Provider = _pinned, How = "added for " + _pinned.Label }
            : await HolodexVault.DetectAsync(raw);
        var provider = found.Provider;
        var entry = HolodexVault.SaveKey(provider, found.Value, found.EnvName.Length > 0 ? found.EnvName : provider?.EnvName ?? "")
            ?? throw new InvalidOperationException("Windows Credential Manager refused the write.");
        ResetIntake();
        SetStatus(provider == null
            ? "Saved as a custom key. Paste ENV_NAME=value to name the provider."
            : $"{provider.Label} key saved ({found.How}). Checking it...");
        ShowTab(catalog: false);
        entry = await HolodexVault.VerifyAsync(entry);
        ShowTab(catalog: false);
        SetStatus(StatusLine(entry));
        if (entry.Target == HolodexVault.NebiusTarget) _ = _ark.CheckStatusAsync();
        PhosphorToast.Show($"{entry.Label} is in your vault", StatusLine(entry), "KEYMASTER // VAULT", 3600,
            entry.Status == "failed" ? ToastTone.Warning : ToastTone.Success);
    }

    private async Task RotateAsync(VaultEntry entry, string raw)
    {
        if (!HolodexVault.Rotate(entry, raw)) throw new InvalidOperationException("Windows Credential Manager refused the new key.");
        ResetIntake();
        SetStatus($"{entry.Label} rotated. Checking the new key...");
        entry = await HolodexVault.VerifyAsync(entry);
        ShowTab(catalog: false);
        SetStatus(StatusLine(entry));
        PhosphorToast.Show($"{entry.Label} rotated", StatusLine(entry), "KEYMASTER // ROTATE", 3600,
            entry.Status == "failed" ? ToastTone.Warning : ToastTone.Success);
    }

    private async Task ConnectUrlAsync(Uri uri)
    {
        var local = HolodexVault.Catalog.FirstOrDefault(p => p.Kind == VaultKind.LocalService && new Uri(p.Url).Port == uri.Port && uri.IsLoopback);
        if (local != null)
        {
            await ConnectLocalAsync(local);
            return;
        }
        var known = HolodexVault.Catalog.FirstOrDefault(p => p.Kind == VaultKind.McpServer && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) && u.Host == uri.Host);
        await ConnectMcpAsync(uri.ToString(), known?.Label, known?.Id ?? "mcp-custom");
    }

    private async Task ConnectMcpAsync(string url, string? label, string providerId)
    {
        ResetIntake();
        SetStatus($"Connecting {label ?? new Uri(url).Host}. Finish any sign-in in your browser.");
        var entry = await HolodexVault.ConnectMcpAsync(url, label, providerId);
        ShowTab(catalog: false);
        SetStatus($"{entry.Label} connected: {entry.Detail}.");
        PhosphorToast.Show($"{entry.Label} connected", entry.Detail, "VAULT // MCP SERVER", 3600, ToastTone.Success);
    }

    private async Task ConnectLocalAsync(VaultProvider provider)
    {
        SetStatus($"Looking for {provider.Label} on this PC...");
        var entry = await HolodexVault.ConnectLocalAsync(provider);
        ShowTab(catalog: false);
        SetStatus(entry.Status == "works" ? $"{provider.Label} is running on this PC and is in your vault." : $"{provider.Label} is {entry.Detail}. Start it, then Check.");
    }

    private void NotConnected(string label, Exception ex)
    {
        SetStatus($"{label} did not connect.");
        PhosphorToast.Show($"{label} did not connect", ex.Message, "VAULT // MCP SERVER", 5200, ToastTone.Error);
    }

    private static string StatusLine(VaultEntry entry) => entry.Status switch
    {
        "works" => $"{entry.Label} works.",
        "failed" => $"{entry.Label} is saved, but the check failed: {entry.Detail}.",
        _ => $"{entry.Label} is saved. It has no read-only check yet."
    };

    // ---- list ----

    private void ShowTab(bool catalog)
    {
        _showCatalog = catalog;
        _connectedTab.Selected = !catalog;
        _catalogTab.Selected = catalog;
        var entries = HolodexVault.Load();
        _connectedTab.Text = $"Connected ({entries.Count})";
        using (_list.Batch())
        {
            _list.Clear();
            if (catalog) BuildCatalog(entries);
            else BuildConnected(entries);
        }
        _list.Relayout();
        _list.AutoScrollPosition = Point.Empty;
    }

    private void BuildConnected(List<VaultEntry> entries)
    {
        if (entries.Count == 0)
        {
            var empty = _list.Add(ThreadTone.Intro, "VAULT // EMPTY",
                "Nothing connected yet. Paste a key above, or open the Catalog to sign in to an app. Nebius Token Factory is the default model, so start with its key.",
                PhosphorGlyph.Vault);
            empty.SetFooter("", new CardAction("Vault.OpenCatalog", PhosphorGlyph.BookOpen, "Catalog", (_, _) => ShowTab(catalog: true)));
            return;
        }
        foreach (var group in entries.GroupBy(e => e.Kind).OrderBy(g => g.Key))
        {
            _list.AddSection(group.Key switch
            {
                VaultKind.ApiKey => "KEYS",
                VaultKind.McpServer => "MCP SERVERS",
                _ => "ON THIS PC"
            });
            foreach (var entry in group) AddEntryCard(entry);
        }
    }

    private void AddEntryCard(VaultEntry entry)
    {
        var leds = new List<(Color, string)>
        {
            entry.Status switch
            {
                "works" => (PhosphorTheme.Moss, "Works"),
                "failed" => (PhosphorTheme.Rust, "Check failed"),
                _ => (PhosphorTheme.Ghost, "Not checked")
            }
        };
        if (entry.Target == HolodexVault.NebiusTarget) leds.Add((PhosphorTheme.Patina, "Default model"));
        leds.Add((PhosphorTheme.Ghost, entry.RotatedAt is { } r ? $"Rotated {r:MMM d}" : $"Added {entry.AddedAt:MMM d}"));

        string body = entry.Kind switch
        {
            VaultKind.ApiKey => $"{(entry.EnvName.Length > 0 ? entry.EnvName : "Key")} in Credential Manager. Fingerprint {entry.Fingerprint}.",
            VaultKind.McpServer => $"{entry.Url}. {(entry.Detail.Length > 0 ? entry.Detail + ". " : "")}Sign-in {entry.Fingerprint}, saved in Credential Manager.",
            _ => $"{new Uri(entry.Url).GetLeftPart(UriPartial.Authority)} on this PC. No key needed."
        };
        if (entry.Status == "failed" && entry.Detail.Length > 0 && entry.Kind == VaultKind.ApiKey) body += $" Last check: {entry.Detail}.";

        var tone = entry.Status switch { "works" => ThreadTone.Ok, "failed" => ThreadTone.Fail, _ => ThreadTone.Step };
        var card = _list.Add(tone, $"{KindEyebrow(entry.Kind)} // {entry.Label.ToUpperInvariant()}", body, PhosphorGlyph.Vault, leds);
        card.Name = $"Vault.Entry.{entry.Id}";

        var actions = new List<CardAction>();
        if (entry.Kind == VaultKind.ApiKey)
        {
            actions.Add(new CardAction("Vault.Copy", PhosphorGlyph.Copy, "Copy", (c, _) =>
                c.SetMeta(HolodexVault.CopyToClipboard(entry) ? "COPIED // CLEARS IN 30 S" : "NOT COPIED")));
            actions.Add(new CardAction("Vault.Rotate", PhosphorGlyph.Retry, "Rotate", (_, _) => StartRotate(entry)));
        }
        actions.Add(new CardAction("Vault.Check", PhosphorGlyph.Sparkles, "Check", async (c, _) =>
        {
            c.SetMeta("CHECKING");
            await HolodexVault.VerifyAsync(entry);
            ShowTab(catalog: false);
            SetStatus(StatusLine(entry));
        }));
        if (entry.Kind == VaultKind.McpServer && entry.Status == "failed")
            actions.Add(new CardAction("Vault.SignInAgain", PhosphorGlyph.Link, "Sign in", async (_, _) =>
            {
                try { await ConnectMcpAsync(entry.Url, entry.Label, entry.ProviderId); }
                catch (Exception ex) { NotConnected(entry.Label, ex); }
            }));
        DateTime armedAt = DateTime.MinValue;
        actions.Add(new CardAction("Vault.Delete", PhosphorGlyph.Stop, "Delete", (c, _) =>
        {
            if ((DateTime.Now - armedAt).TotalSeconds > 5)
            {
                armedAt = DateTime.Now;
                c.SetMeta("DELETE AGAIN TO REMOVE IT FROM THIS PC");
                return;
            }
            HolodexVault.Delete(entry);
            SetStatus($"{entry.Label} deleted from this PC.{(entry.Kind == VaultKind.ApiKey ? " Revoke it with the provider too." : "")}");
            ShowTab(catalog: false);
        }));
        card.SetFooter(entry.CheckedAt is { } at ? $"CHECKED {at:HH:mm}" : "", actions.ToArray());
    }

    private static string KindEyebrow(VaultKind kind) => kind switch
    {
        VaultKind.ApiKey => "KEY",
        VaultKind.McpServer => "MCP",
        _ => "LOCAL"
    };

    private void BuildCatalog(List<VaultEntry> entries)
    {
        var connected = entries.Select(e => e.ProviderId).ToHashSet();
        foreach (var group in HolodexVault.Catalog.GroupBy(p => p.Group))
        {
            _list.AddSection(group.Key.ToUpperInvariant());
            foreach (var provider in group) AddCatalogCard(provider, connected.Contains(provider.Id));
        }
        // Last on purpose: no account is the default, and nothing above needs one.
        AddHivemindCard();
    }

    /// <summary>Hivemind is optional: signing in shares the session with the Hivemind web app; the vault works without it.</summary>
    private void AddHivemindCard()
    {
        _list.AddSection("HIVEMIND ACCOUNT // OPTIONAL");
        bool signedIn = _ark.Hivemind.State == HivemindState.Connected || HivemindClient.HasSavedSignIn;
        var card = _list.Add(signedIn ? ThreadTone.Ok : ThreadTone.Step, "HIVEMIND // OPTIONAL",
            signedIn
                ? $"Signed in to {HivemindClient.EndpointHost}. Your vault stays on this PC either way."
                : "Not needed. Nemotron on Nebius and everything above work without an account. Sign in to share Sessions and agents with the Hivemind web app.",
            PhosphorGlyph.Holodex,
            new[] { signedIn ? (PhosphorTheme.Moss, "Signed in") : (PhosphorTheme.Ghost, "Not signed in"), (PhosphorTheme.Ghost, HivemindClient.EndpointHost) });
        card.Name = "Vault.Catalog.hivemind";
        if (!signedIn)
            card.SetFooter("", new CardAction("Vault.HivemindSignIn", PhosphorGlyph.Link, "Sign in", async (c, _) =>
            {
                c.SetMeta("FINISH IN YOUR BROWSER");
                bool ok = await _ark.Hivemind.SignInAsync();
                c.SetMeta(ok ? "SIGNED IN" : "NOT SIGNED IN");
                if (ok && _showCatalog) ShowTab(catalog: true);
            }));
    }

    private void AddCatalogCard(VaultProvider provider, bool isConnected)
    {
        var leds = new List<(Color, string)>();
        if (isConnected) leds.Add((PhosphorTheme.Moss, "Connected"));
        leds.Add((PhosphorTheme.Ghost, provider.Kind switch
        {
            VaultKind.ApiKey => provider.EnvName.Length > 0 ? provider.EnvName : "API key",
            VaultKind.McpServer => provider.Blurb.Contains("No sign-in") ? "MCP link" : "Sign-in link",
            _ => new Uri(provider.Url).GetLeftPart(UriPartial.Authority)
        }));
        if (provider.Id == "nebius") leds.Insert(0, (PhosphorTheme.Patina, "Default model"));

        var card = _list.Add(isConnected ? ThreadTone.Ok : ThreadTone.Action, $"{KindEyebrow(provider.Kind)} // {provider.Label.ToUpperInvariant()}",
            provider.Blurb, PhosphorGlyph.Vault, leds);
        card.Name = $"Vault.Catalog.{provider.Id}";
        var actions = new List<CardAction>();
        switch (provider.Kind)
        {
            case VaultKind.ApiKey:
                actions.Add(new CardAction("Vault.AddKey", PhosphorGlyph.Plus, isConnected ? "Replace key" : "Add key", (_, _) => PinProvider(provider)));
                if (provider.KeysUrl.Length > 0)
                    actions.Add(new CardAction("Vault.GetKey", PhosphorGlyph.ExternalLink, "Get a key", (_, _) => HolodexVault.OpenKeysPage(provider)));
                break;
            case VaultKind.McpServer:
                actions.Add(new CardAction("Vault.SignIn", PhosphorGlyph.Link, isConnected ? "Sign in again" : "Sign in", async (c, _) =>
                {
                    c.SetMeta("CONNECTING");
                    try { await ConnectMcpAsync(provider.Url, provider.Label, provider.Id); }
                    catch (Exception ex)
                    {
                        c.SetMeta("NOT CONNECTED");
                        NotConnected(provider.Label, ex);
                    }
                }));
                break;
            default:
                actions.Add(new CardAction("Vault.ConnectLocal", PhosphorGlyph.Link, "Connect", async (_, _) => await ConnectLocalAsync(provider)));
                break;
        }
        card.SetFooter("", actions.ToArray());
    }

    /// <summary>Draws the window offscreen to a PNG (pipe <c>vault:capture:&lt;png&gt;</c>) on the tab asked for.</summary>
    public string Capture(string path, bool catalog)
    {
        ShowTab(catalog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        SurfaceCapture.SaveForm(this, path);
        return path;
    }
}
