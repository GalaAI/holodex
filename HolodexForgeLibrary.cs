namespace Omniwand;

/// <summary>
/// 4GE in the drawer: the 4GE button on the rail, and the Library tab's origin groups (My projects, Repos, Mods with
/// the plus tile, Augments, Adapters) read from the 4GE server's GET /api/library while it runs. A click on an item
/// opens its Smith session in the studio, full screen.
/// </summary>
public sealed partial class HolodexLiteDrawerForm
{
    /// <summary>The 4GE server Holodex starts and reads (Program owns it).</summary>
    internal ForgeHost? Forge { get; set; }

    /// <summary>Opens the 4GE studio full screen on a studio path (its home when null). Program owns the window.</summary>
    internal Action<string?>? OpenForge { get; set; }

    private bool _forgeChecked;
    private bool _forgeRunning;
    private string? _forgeError;
    private IReadOnlyList<ForgeLibraryGroup>? _forgeGroups;
    private string? _forgeShown;

    /// <summary>What to say in a group the server lists empty.</summary>
    internal static string ForgeEmptyText(string origin) => origin switch
    {
        "project" => "Apps you build in 4GE are listed here, finished and in progress. Open 4GE to start one.",
        "repo" => "Shortcuts to repositories you add in 4GE are listed here.",
        "mod" => "Your mods are listed here.",
        "augment" => "MCP servers, skills, prompts and tool packs your mods and builds use are listed here.",
        _ => "Connections to your accounts and services are listed here once you connect them in 4GE."
    };

    internal static PhosphorGlyph ForgeGlyph(string origin) => origin switch
    {
        "project" => PhosphorGlyph.Hammer,
        "repo" => PhosphorGlyph.Link,
        "mod" => PhosphorGlyph.WandSparkles,
        "augment" => PhosphorGlyph.Sparkles,
        _ => PhosphorGlyph.ExternalLink
    };

    private static Color ForgeToneColor(string tone) => tone switch
    {
        "live" => PhosphorTheme.Patina,
        "ok" => PhosphorTheme.Moss,
        "warn" => PhosphorTheme.Seal,
        "err" => PhosphorTheme.Rust,
        _ => PhosphorTheme.Ghost
    };

    /// <summary>The 4GE entry at the foot of the rail, above the profile; only when 4GE is on this PC.</summary>
    private void AddForgeRailButton(Panel rail)
    {
        if (ForgeHost.FindForgeRoot() == null) return;
        var btn = new RailButton("Holodex.Forge", PhosphorGlyph.Hammer, "4GE") { Dock = DockStyle.Bottom, Height = 44, AccessibleRole = AccessibleRole.PushButton };
        btn.Click += (_, _) => OpenForge?.Invoke(null);
        rail.Controls.Add(btn);
    }

    /// <summary>Reads the 4GE Library when the server runs and redraws the Library tab if what it lists changed.</summary>
    internal async Task RefreshForgeLibraryAsync()
    {
        if (Forge == null || IsDisposed) return;
        var (running, portError) = await Forge.ProbeAsync();
        IReadOnlyList<ForgeLibraryGroup>? groups = null;
        string? error = running ? null : portError ?? (Forge.State == ForgeState.Failed ? Forge.LastError : null);
        if (running) (groups, error) = await Forge.ReadLibraryAsync();
        if (IsDisposed) return;
        _forgeChecked = true;
        _forgeRunning = running;
        _forgeGroups = groups;
        _forgeError = error;
        if (ForgeSignature() == _forgeShown) return;
        _codexShown = null;
        RefreshCodexEntries();
    }

    /// <summary>For pipe forge:library: each group and its items, as the Library tab shows them.</summary>
    internal string DescribeForgeLibrary() =>
        !_forgeChecked ? "unchecked"
        : !_forgeRunning ? "not running" + (_forgeError != null ? ": " + _forgeError : "")
        : _forgeGroups == null ? "error: " + _forgeError
        : string.Join(" || ", _forgeGroups.Select(g => $"{g.Label} ({g.Items.Count}){(g.CanCreate ? " +" : "")}: {string.Join(", ", g.Items.Select(i => i.Title))}"));

    private string ForgeSignature() => DescribeForgeLibrary() + "\u0001" +
        string.Join("\u0001", (_forgeGroups ?? Array.Empty<ForgeLibraryGroup>()).SelectMany(g => g.Items).Select(i => $"{i.Id}\u0002{i.Status}\u0002{i.Tone}\u0002{i.Subtitle}"));

    /// <summary>Writes the 4GE origin groups at the top of the Library list (inside the caller's batch).</summary>
    private void AddForgeLibrary()
    {
        if (Forge == null) return;
        _forgeShown = ForgeSignature();
        if (!_forgeChecked) return;
        if (!_forgeRunning)
        {
            _codexList.AddSection("4GE // projects, repos, mods, augments, adapters");
            var card = _codexList.Add(ThreadTone.Intro, "4GE is not running",
                _forgeError ?? "Open 4GE to start it. Your projects, repos, mods, augments and adapters are listed here while it runs.",
                PhosphorGlyph.Hammer);
            card.SetFooter("", new CardAction("Holodex.Forge.Open", PhosphorGlyph.Hammer, "Open 4GE", (_, _) => OpenForge?.Invoke(null)));
            return;
        }
        if (_forgeGroups == null)
        {
            _codexList.AddSection("4GE // Library");
            var card = _codexList.Add(ThreadTone.Fail, "Library not connected", _forgeError ?? "The 4GE server did not list the Library.");
            card.SetFooter("", new CardAction("Holodex.Forge.Retry", PhosphorGlyph.Retry, "Try again", (_, _) => _ = RefreshForgeLibraryAsync()));
            return;
        }
        foreach (var group in _forgeGroups)
        {
            _codexList.AddSection($"{group.Label} // {group.Items.Count}");
            if (group.Items.Count == 0)
                _codexList.Add(ThreadTone.Intro, "None yet", ForgeEmptyText(group.Origin));
            foreach (var item in group.Items)
            {
                var leds = new List<(Color, string)> { (ForgeToneColor(item.Tone), (item.Status ?? group.Label).ToUpperInvariant()) };
                if (item.Url != null && Uri.TryCreate(item.Url, UriKind.Absolute, out var u)) leds.Add((PhosphorTheme.Ghost, u.Host + u.AbsolutePath.TrimEnd('/')));
                var card = _codexList.Add(ThreadTone.Action, item.Title, item.Subtitle, ForgeGlyph(group.Origin), leds);
                var captured = item;
                var actions = new List<CardAction>
                {
                    new("Holodex.Forge.Item.Open", PhosphorGlyph.Chat, "Open with Smith", (_, _) => OpenForgeItem(captured))
                };
                if (item.Url != null)
                    actions.Add(new CardAction("Holodex.Forge.Item.Url", PhosphorGlyph.ExternalLink, "Open the repository", (_, _) => OpenUrl(captured.Url!)));
                card.SetFooter("", actions.ToArray());
                card.Click += (_, _) => OpenForgeItem(captured);
            }
            if (group.CanCreate)
            {
                var plus = _codexList.Add(ThreadTone.Step, "New mod", "Make a mod from your adapters and augments, with Smith.", PhosphorGlyph.Plus);
                plus.SetFooter("", new CardAction("Holodex.Forge.NewMod", PhosphorGlyph.Plus, "New mod", (_, _) => OpenForge?.Invoke("/mods")));
            }
        }
    }

    private async void OpenForgeItem(ForgeLibraryItem item)
    {
        if (Forge == null) return;
        var (path, error) = await Forge.OpenSessionAsync(item);
        if (path == null)
        {
            PhosphorToast.Show("Smith did not open", error ?? "The 4GE server opened no session.", "HOLODEX // 4GE", 4200, ToastTone.Warning);
            return;
        }
        OpenForge?.Invoke(path);
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show("Link did not open", ex.Message, "HOLODEX // 4GE", 3600, ToastTone.Warning); }
    }
}
