using System.Diagnostics;
using System.Text.Json;

namespace Holodex;

/// <summary>
/// Importing a shared Cursor Kit (ruling 2026-11-09: confirm shell commands on import). The incoming kit is merged
/// into this PC's kit, its actions and bindings replacing any with the same id, and every command it would add is
/// listed for the user before anything is saved.
/// </summary>
internal static class CursorKitImport
{
    public static CursorKit Merge(CursorKit current, CursorKit incoming)
    {
        var actionIds = incoming.Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var bindingIds = incoming.Bindings.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        return new CursorKit(
            current.Actions.Where(a => !actionIds.Contains(a.Id)).Concat(incoming.Actions).ToList(),
            current.Bindings.Where(b => !bindingIds.Contains(b.Id)).Concat(incoming.Bindings).ToList());
    }

    /// <summary>The commands and program opens the merged kit has that this PC's kit does not, in kit order.</summary>
    public static List<string> NewCode(CursorKit current, CursorKit merged)
    {
        var before = current.ShellCommands.ToHashSet(StringComparer.Ordinal);
        return merged.ShellCommands.Where(c => !before.Contains(c)).Distinct().ToList();
    }
}

/// <summary>
/// The Cursor Mod's own controls, native in the Mouse Mod Station rail: shell actions on or off, this PC's token for
/// the Stream Deck and the AHK bridge, the shell log, and importing a Cursor Kit with each new command confirmed.
/// </summary>
internal sealed class CursorModPanel : Panel
{
    private readonly ShellGate _gate;
    private readonly KitStore _store;
    private readonly LedRow _status;
    private readonly PhosphorButton _toggle;
    private readonly PhosphorButton _copyToken;

    public CursorModPanel(ShellGate gate, KitStore store)
    {
        _gate = gate;
        _store = store;
        Name = "CursorMod.Panel";
        BackColor = PhosphorTheme.Surface;
        Height = 22 + PhosphorTheme.S2 + 2 * (36 + 6);

        _status = new LedRow { Dock = DockStyle.Top, Height = 22, Name = "CursorMod.ShellStatus" };
        var grid = new Panel { Dock = DockStyle.Top, Height = 2 * (36 + 6), BackColor = PhosphorTheme.Surface, Name = "CursorMod.Buttons" };
        _toggle = new PhosphorButton("Turn shell on") { Name = "CursorMod.ShellToggle" };
        _copyToken = new PhosphorButton("Copy token") { Name = "CursorMod.CopyToken" };
        var log = new PhosphorButton("Shell log") { Name = "CursorMod.Log" };
        var import = new PhosphorButton("Import kit") { Name = "CursorMod.Import" };
        var buttons = new[] { _toggle, _copyToken, import, log };
        grid.Resize += (_, _) =>
        {
            int w = (grid.Width - 6) / 2;
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Location = new Point(i % 2 * (w + 6), i / 2 * 42);
                buttons[i].Size = new Size(w, 36);
            }
        };
        grid.Controls.AddRange(buttons);
        _toggle.Click += (_, _) => Toggle();
        _copyToken.Click += (_, _) => CopyToken();
        log.Click += (_, _) => OpenLog();
        import.Click += (_, _) => Import();

        Controls.Add(grid);
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(_status);
        ShowState();
    }

    private void ShowState()
    {
        bool on = _gate.IsOn;
        _status.Set(on
            ? new[] { (PhosphorTheme.Ember, "SHELL ACTIONS ON"), (PhosphorTheme.Ghost, "TOKEN REQUIRED, EVERY RUN LOGGED") }
            : new[] { (PhosphorTheme.Moss, "SHELL ACTIONS OFF"), (PhosphorTheme.Ghost, "KEYS, CLICKS AND LINKS STILL RUN") });
        _toggle.Text = on ? "Turn shell off" : "Turn shell on";
        _copyToken.Enabled = on;
    }

    private void Toggle()
    {
        if (_gate.IsOn)
        {
            _gate.TurnOff();
            _gate.Log("shell actions turned off in the Cursor Mod tab");
            ShowState();
            return;
        }
        using var confirm = new CursorModConfirmForm(
            "Turn shell actions on?",
            "HOLODEX // CURSOR MOD // SHELL ACTIONS",
            "Cursor Kit buttons will be able to run commands and open programs on this PC.",
            "Only a caller with this PC's token can run them (the Stream Deck and the AHK bridge read it from your Holodex folder). Every run is logged. Turning them off again deletes the token.",
            "Turn on");
        if (confirm.ShowDialog(FindForm()) != DialogResult.OK) return;
        _gate.TurnOn();
        _gate.Log("shell actions turned on in the Cursor Mod tab");
        ShowState();
        PhosphorToast.Show("Shell actions on", "Cursor Kit commands now run for callers with this PC's token.", "HOLODEX // CURSOR MOD");
    }

    private void CopyToken()
    {
        try
        {
            Clipboard.SetText(File.ReadAllText(_gate.TokenPath).Trim());
            PhosphorToast.Show("Token copied", "Paste it into the caller that sends Cursor Kit commands.", "HOLODEX // CURSOR MOD");
        }
        catch (Exception ex)
        {
            PhosphorToast.Show("No token to copy", ex.Message, "HOLODEX // CURSOR MOD", tone: ToastTone.Error);
        }
    }

    private void OpenLog()
    {
        if (!File.Exists(_gate.LogPath))
        {
            PhosphorToast.Show("No shell log yet", "Nothing has run or been refused.", "HOLODEX // CURSOR MOD");
            return;
        }
        try { Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { _gate.LogPath }, UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { PhosphorToast.Show("Could not open the log", ex.Message, "HOLODEX // CURSOR MOD", tone: ToastTone.Error); }
    }

    private void Import()
    {
        using var pick = new OpenFileDialog { Title = "Import a Cursor Kit", Filter = "Cursor Kit (*.json)|*.json|All files (*.*)|*.*" };
        if (pick.ShowDialog(FindForm()) != DialogResult.OK) return;

        CursorKit incoming;
        try { incoming = CursorKit.Parse(File.ReadAllText(pick.FileName)); }
        catch (Exception ex) when (ex is KitException or JsonException or IOException)
        {
            PhosphorToast.Show("Not a Cursor Kit", ex.Message, "HOLODEX // CURSOR MOD", 6000, ToastTone.Error);
            return;
        }

        var current = _store.Load();
        var merged = CursorKitImport.Merge(current, incoming);
        var cycles = InputSpine.FindSequenceProblems(merged).Cycles;
        if (cycles.Count > 0)
        {
            PhosphorToast.Show("Kit not imported", $"Its sequences loop through {string.Join(", ", cycles)}.", "HOLODEX // CURSOR MOD", 6000, ToastTone.Error);
            return;
        }

        var code = CursorKitImport.NewCode(current, merged);
        string summary = $"{incoming.Actions.Count} actions and {incoming.Bindings.Count} bindings from {Path.GetFileName(pick.FileName)}. Actions and bindings with the same id are replaced.";
        if (code.Count > 0)
        {
            using var confirm = new CursorModConfirmForm(
                $"Import {code.Count} command{(code.Count == 1 ? "" : "s")}?",
                "HOLODEX // CURSOR MOD // IMPORT // CONFIRM EACH COMMAND",
                summary + " It adds these commands:",
                string.Join(Environment.NewLine + Environment.NewLine, code),
                "Import",
                mono: true);
            if (confirm.ShowDialog(FindForm()) != DialogResult.OK) return;
            foreach (var c in code) _gate.Log($"confirmed on import from {pick.FileName}: {c}");
        }
        _store.Save(merged);
        PhosphorToast.Show("Cursor Kit imported", summary, "HOLODEX // CURSOR MOD", 5200, ToastTone.Info);
    }
}

/// <summary>A yes-or-no gate in Phosphor chrome: what happens, the detail to read, then the action or Cancel.</summary>
internal sealed class CursorModConfirmForm : Form
{
    public CursorModConfirmForm(string title, string eyebrow, string what, string detail, string confirmText, bool mono = false)
    {
        PhosphorFonts.EnsureLoaded();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "CursorModConfirm";
        Text = title;
        Size = new Size(660, mono ? 440 : 330);
        Padding = WidgetChrome.FramePadding();

        int detailHeight = mono ? 220 : 110;
        var box = new PhosphorField("", multiline: true, detailHeight, mono: mono) { Name = "CursorModConfirm.Detail" };
        box.Box.Text = what + Environment.NewLine + Environment.NewLine + detail;
        box.Box.ReadOnly = true;
        var lead = new DataLabel(mono ? "Read each command: it can do anything you can." : "Read this first.") { Name = "CursorModConfirm.Lead", Height = 22 };
        var ok = new PhosphorButton(confirmText, PhosphorButtonVariant.Primary) { Name = "CursorModConfirm.Ok", Dock = DockStyle.Right, Width = 150 };
        var cancel = new PhosphorButton("Cancel") { Name = "CursorModConfirm.Cancel", Dock = DockStyle.Right, Width = 150 };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var actions = new Panel { Dock = DockStyle.Bottom, Height = PhosphorTheme.TouchMin, BackColor = Color.Transparent, Name = "CursorModConfirm.Actions" };
        actions.Controls.Add(cancel);
        actions.Controls.Add(new Panel { Dock = DockStyle.Right, Width = PhosphorTheme.S2, BackColor = Color.Transparent });
        actions.Controls.Add(ok);

        Controls.Add(box);
        box.Dock = DockStyle.Top;
        box.Height = detailHeight;
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(lead);
        lead.Dock = DockStyle.Top;
        Controls.Add(actions);
        WidgetChrome.Attach(this, eyebrow, title, PhosphorTheme.Ember, null);
        // Cancel is the default: Enter does not confirm something that runs code.
        AcceptButton = null;
        ActiveControl = cancel;
        ResumeLayout(false);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }
}
