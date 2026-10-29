using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// AInbox intake (Alt+I, Actions Ring slot 8, pipe ainbox:summon), built to the design targets "Capture tools" and
/// "Stack, predict, review". One composer takes any number of captures (file, screen, window, pick, lasso, snippet,
/// link; record and whisper planned), each scanned for its origin and stacked into context groups. For each group
/// Omnitek ranks what it should become, with the target pod and orchestrator, and Nemotron 3 Nano on Nebius Token
/// Factory re-ranks it, says why and synthesizes it. The Handoff Scroll's fields adapt to the chosen intent and are
/// sealed with SHA-256. Aegis redacts secrets before anything leaves. Send waits five seconds so it can be undone,
/// then queues the handoff for the AInbox service (:8840), which retries until it is delivered.
/// </summary>
public sealed class AInboxIntakeWidgetForm : Form
{
    private const int UndoSeconds = 5;

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly OutboundQueue _queue;

    private readonly FlowLayoutPanel _body;
    private readonly CaptureWellControl _composer;
    private readonly Panel _stackHost;
    private readonly Panel _stackGap;
    private readonly PromptTextBox _intentBox;
    private readonly Panel _captureBar;
    private readonly DataLabel _suggestionHint;
    private readonly DisclosureRow _summary;
    private readonly SpecCardPanel _scroll;
    private readonly DataLabel _statusLabel;
    private readonly SyncStatus _sync;
    private readonly PhosphorButton _sendBtn;
    private readonly List<Control> _cards = new();
    private readonly List<ScrollFieldRow> _fieldRows = new();

    private readonly List<StackItem> _items = new();
    private List<StackGroup> _groups = new();
    private StackGroup? _virtualAsk;
    private int _active;
    private string _scrollShape = "";
    private string _suggestion = "";
    private CaptureOrigin _summonOrigin = new("Desktop", "Desktop");
    private Point _summonPos;
    private bool _scrollOpen;
    private bool _working;
    private bool _preview;
    private System.Windows.Forms.Timer? _undoTimer;
    private int _undoLeft;
    private long _lastDragMs;
    private readonly System.Windows.Forms.Timer _dragLeaveTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _refineTimer = new() { Interval = 1200 };
    private readonly Dictionary<string, string> _refinedSig = new();
    private CancellationTokenSource? _refineCts;

    /// <summary>Opens the snip tools (Pick, Lasso); their Send to AInbox comes back through SummonAt.</summary>
    internal Action<WandCaptureTool>? RequestCapture { get; set; }

    public AInboxIntakeWidgetForm(ArkStationClient ark, Action<bool> setThinkingState)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;
        // Without the owner's Ark there is no AInbox service, so a sent Scroll is saved on this PC instead.
        _queue = new OutboundQueue(localOnly: !ark.ArkInstalled);

        PhosphorFonts.EnsureLoaded();
        // Designer order: layout suspended, then 96 DPI dimensions, then the mode, so WinForms scales the whole
        // built layout once at ResumeLayout to the monitor's DPI. The reverse order scaled the empty form only.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "AInbox";
        Size = new Size(680, 420);
        Padding = WidgetChrome.FramePadding();

        // Composer: the stack, the ask, and the capture bar.
        _composer = new CaptureWellControl { Name = "AInbox.Well", Margin = new Padding(0, 0, 0, 8) };
        var stack = _composer.Content;
        _stackHost = new Panel { Dock = DockStyle.Top, BackColor = Color.Transparent, Name = "AInbox.Stack" };
        _stackGap = WidgetChrome.Spacer(8);
        _intentBox = new PromptTextBox { Dock = DockStyle.Top, Height = 60, Name = "AInbox.IntentField" };
        _intentBox.TextChanged += (_, _) => OnAskChanged();

        _captureBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "AInbox.CaptureBar" };
        _suggestionHint = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "AInbox.SuggestionHint" };
        _captureBar.Controls.Add(_suggestionHint);
        _captureBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        foreach (var info in CaptureKinds.All.Reverse())
        {
            var tool = new IconToolButton("AInbox.Kind." + CaptureKinds.Name(info.Kind), CaptureKinds.Glyph(info.Kind),
                CaptureKinds.Name(info.Kind) + (info.Planned ? " (planned)" : "")) { Dock = DockStyle.Left, Planned = info.Planned };
            tool.Click += (_, _) => OnCaptureTool(info);
            _captureBar.Controls.Add(tool);
        }
        stack.Controls.Add(_captureBar);
        stack.Controls.Add(_intentBox);
        stack.Controls.Add(_stackGap);
        stack.Controls.Add(_stackHost);

        // The Handoff Scroll: one line that opens into the CP-20 ticket with the intent's fields.
        _summary = new DisclosureRow { Height = 44, Name = "AInbox.ScrollSummary", Margin = new Padding(0, 0, 0, 8) };
        _summary.Toggled += (_, _) => { _scrollOpen = !_scrollOpen; _summary.Expanded = _scrollOpen; Relayout(); };
        _scroll = new SpecCardPanel { Name = "AInbox.HandoffScroll", Margin = new Padding(0, 0, 0, 8), Visible = false };

        // The body scrolls when a big stack outgrows the screen; header and footer stay put.
        _body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Name = "AInbox.Body"
        };
        _body.Controls.Add(_composer);
        _body.Controls.Add(_summary);
        _body.Controls.Add(_scroll);
        _body.Resize += (_, _) => SizeBodyChildren();
        PhosphorPaint.UseDarkScrollbars(_body);

        // Footer: what is happening and the queue's state on the left, the one primary on the right.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "AInbox.Footer" };
        var lines = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _statusLabel = new DataLabel("CTRL+ENTER SENDS") { Dock = DockStyle.Top, Height = 22, ForeColor = PhosphorTheme.Ghost, Name = "AInbox.ReceiptStatus" };
        _sync = new SyncStatus { Dock = DockStyle.Top, Height = 22 };
        lines.Controls.Add(_sync);
        lines.Controls.Add(_statusLabel);
        _sendBtn = new PhosphorButton("Send", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 210, Name = "AInbox.SubmitButton" };
        _sendBtn.Click += (_, _) => OnSendClicked();
        footer.Controls.Add(lines);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_sendBtn);

        Controls.Add(_body);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "AINBOX // INTAKE // 7 PODS", "AInbox", PhosphorTheme.Seal, null);
        WidgetDrag.Enable(this);

        // Drops land anywhere; a leave settles after a beat so moving between targets does not flicker.
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            if (_composer.Mode == CaptureWellMode.DragOver && Environment.TickCount64 - _lastDragMs > 120) _composer.SetMode(_composer.RestMode);
        };
        foreach (var c in new Control[] { this, _composer, _intentBox, _body }) WireDrop(c);

        _refineTimer.Tick += async (_, _) => { _refineTimer.Stop(); await RefineAsync(); };
        _queue.Changed += (_, _) => { if (InvokeRequired) BeginInvoke(new Action(SyncQueueStatus)); else SyncQueueStatus(); };
        SyncQueueStatus();
        Rebuild();
        ResumeLayout(false);
    }

    private StackGroup? Active => _groups.Count == 0 ? null : _groups[Math.Clamp(_active, 0, _groups.Count - 1)];
    private bool HasAnything => _items.Count > 0 || !string.IsNullOrWhiteSpace(_intentBox.Text);

    // ---- Keys and summon ---------------------------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (_undoTimer != null) CancelPendingSend();
            else SurfaceMotion.Dismiss(this);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Enter)) { OnSendClicked(); return true; }
        if (keyData == Keys.Tab && _intentBox.Focused && _intentBox.TextLength == 0 && _suggestion.Length > 0)
        {
            _intentBox.Text = _suggestion;
            _intentBox.SelectionStart = _intentBox.TextLength;
            return true;
        }
        if (keyData == (Keys.Control | Keys.V) && _intentBox.Focused && PasteAsCapture()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens the widget at the cursor, scanning the app under it as the origin of what follows.</summary>
    public void SummonAt(Point cursorPos, string? preCapturedDataUrl = null, string? captureName = null)
    {
        _summonPos = cursorPos;
        if (_preview) PreviewState("reset");
        if (!Visible) _summonOrigin = OriginScanner.AtPoint(cursorPos);
        if (_composer.Mode == CaptureWellMode.DragOver) _composer.SetMode(_composer.RestMode);
        if (!string.IsNullOrEmpty(preCapturedDataUrl))
        {
            string name = captureName ?? "lasso-capture.png";
            bool pick = name.StartsWith("element", StringComparison.OrdinalIgnoreCase);
            AddImage(preCapturedDataUrl, name, pick ? CaptureKind.Pick : CaptureKind.Lasso, OriginScanner.AtPoint(cursorPos), pick ? "PICK // element + origin" : "LASSO // region");
        }
        Rebuild();
        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);
        SurfaceMotion.Present(this);
        Activate();
        _intentBox.Focus();
        _ = _queue.DrainAsync();
    }

    /// <summary>Types an ask into the composer (Wiz's draft_ainbox_handoff). Nothing is sent; the operator reviews and sends.</summary>
    public void PrefillAsk(string ask)
    {
        _intentBox.Text = ask.Trim();
        _intentBox.SelectionStart = _intentBox.TextLength;
        _intentBox.Focus();
    }

    // ---- Captures ----------------------------------------------------------------------------------------------

    private void WireDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) =>
        {
            if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap)))
            {
                e.Effect = DragDropEffects.Copy;
                _lastDragMs = Environment.TickCount64;
                if (_composer.Mode != CaptureWellMode.DragOver) _composer.SetMode(CaptureWellMode.DragOver, CaptureKinds.FromDrag(e.Data));
            }
        };
        c.DragOver += (_, e) =>
        {
            if (_composer.Mode != CaptureWellMode.DragOver) return;
            e.Effect = DragDropEffects.Copy;
            _lastDragMs = Environment.TickCount64;
        };
        c.DragLeave += (_, _) => { _dragLeaveTimer.Stop(); _dragLeaveTimer.Start(); };
        c.DragDrop += OnDragDrop;
    }

    private void OnCaptureTool(CaptureKindInfo info)
    {
        if (info.Planned)
        {
            PhosphorToast.Show($"{CaptureKinds.Name(info.Kind)} is planned", info.Kind == CaptureKind.Record
                ? "Region recording with marks lands in the stack once the recorder ships."
                : "Ghost Whisper voice notes, pinned to a capture, are designed but not wired yet.", "AINBOX // CAPTURE", 2800, ToastTone.Info);
            return;
        }
        switch (info.Kind)
        {
            case CaptureKind.File: ChooseFiles(); break;
            case CaptureKind.Screen:
                if (ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y, 640, 420) is { } shot)
                    AddImage(shot, "cursor-capture.png", CaptureKind.Screen, _summonOrigin, "SCREEN // 640x420");
                break;
            case CaptureKind.Window:
                var (win, size) = OriginScanner.WindowShotAt(_summonPos);
                if (win != null) AddImage(win, $"{Slug(CaptureOrigin.CoreTitle(_summonOrigin.Window))}.png", CaptureKind.Window, _summonOrigin, $"WINDOW // {size.Width}x{size.Height}");
                break;
            case CaptureKind.Pick: OpenSnipTool(WandCaptureTool.ElementPicker); return;
            case CaptureKind.Lasso: OpenSnipTool(WandCaptureTool.FreeformLasso); return;
            case CaptureKind.Snippet: AttachClipboardText(link: false); break;
            case CaptureKind.Link: AttachClipboardText(link: true); break;
        }
        Rebuild();
    }

    private void OpenSnipTool(WandCaptureTool tool)
    {
        if (RequestCapture == null) return;
        Hide(); // the snip tool freezes the desktop; this widget must not be in it
        var delay = new System.Windows.Forms.Timer { Interval = 150 };
        delay.Tick += (_, _) => { delay.Stop(); delay.Dispose(); RequestCapture(tool); };
        delay.Start();
    }

    private void AttachClipboardText(bool link)
    {
        string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        if (text.Length == 0 || (link && !CaptureKinds.IsLink(text)))
        {
            PhosphorToast.Show(link ? "No link on the clipboard" : "Clipboard is empty",
                link ? "Copy a URL first, then press Link." : "Copy some text or code first, then press Snippet.",
                "AINBOX // CAPTURE", 2400, ToastTone.Warning);
            return;
        }
        AddText(text);
    }

    /// <summary>Ctrl+V in the composer: files, images, links and long or multi-line text join the stack.</summary>
    private bool PasteAsCapture()
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
        {
            foreach (string? f in files) if (f != null) AddFile(f);
            Rebuild();
            return true;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image img)
        {
            AddImage(ToDataUrl(img), "pasted-image.png", CaptureKind.Screen, _summonOrigin, $"IMAGE // {img.Width}x{img.Height}");
            Rebuild();
            return true;
        }
        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText().Trim();
            if (CaptureKinds.IsLink(text) || text.Contains('\n') || text.Length > 160)
            {
                AddText(text);
                Rebuild();
                return true;
            }
        }
        return false;
    }

    private void ChooseFiles()
    {
        using var dlg = new OpenFileDialog { Title = "Add files to the AInbox stack", Filter = "All Files (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (string f in dlg.FileNames) AddFile(f);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _composer.SetMode(_composer.RestMode);
        if (e.Data == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (string f in files) AddFile(f);
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is Image img)
            AddImage(ToDataUrl(img), "dropped-image.png", CaptureKind.Screen, _summonOrigin, $"IMAGE // {img.Width}x{img.Height}");
        else if (e.Data.GetDataPresent(DataFormats.Text) && e.Data.GetData(DataFormats.Text) is string text && text.Trim().Length > 0)
            AddText(text.Trim());
        Rebuild();
        _intentBox.Focus();
    }

    private void AddFile(string path)
    {
        if (Directory.Exists(path)) return;
        var item = new StackItem { Kind = CaptureKind.File, Name = Path.GetFileName(path), Path = path, Meta = FileMeta(path), Origin = OriginScanner.ForFile(path) };
        Scan(item);
        if (item.Secrets.Count == 0 && File.Exists(path))
        {
            // Hash off the UI thread; the seal waits for it.
            System.Threading.Tasks.Task.Run(() => ArkStationClient.Sha256File(path)).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { item.Sha256 = t.Result; RefreshScroll(); }));
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        _items.Add(item);
    }

    private void AddImage(string dataUrl, string name, CaptureKind kind, CaptureOrigin origin, string meta)
    {
        var item = new StackItem { Kind = kind, Name = name, DataUrl = dataUrl, Meta = meta, Origin = origin };
        try { item.Sha256 = ArkStationClient.Sha256Hex(DecodeDataUrl(dataUrl)); } catch (FormatException) { }
        _items.Add(item);
    }

    private void AddText(string text)
    {
        bool link = CaptureKinds.IsLink(text);
        string first = text.Split('\n')[0].Trim();
        int lines = text.Split('\n').Length;
        var item = new StackItem
        {
            Kind = link ? CaptureKind.Link : CaptureKind.Snippet,
            Name = first.Length > 80 ? first[..80] + "..." : first,
            Text = link ? null : text,
            Url = link ? text : null,
            Meta = link ? "LINK" : lines > 1 ? $"SNIPPET // {lines} LINES" : $"SNIPPET // {text.Length} CHARS",
            Origin = link ? OriginScanner.ForLink(text, _summonOrigin) : _summonOrigin
        };
        Scan(item);
        _items.Add(item);
    }

    /// <summary>Aegis scan plus the hash of exactly what will be sent (the redacted text when a secret was found).</summary>
    private static void Scan(StackItem item)
    {
        string? text = item.TextForAnalysis();
        item.Scanned = text != null;
        if (text == null) return;
        item.Secrets.AddRange(AegisScanner.Scan(text));
        if (item.Kind is CaptureKind.Snippet or CaptureKind.Link || item.Secrets.Count > 0)
            item.Sha256 = ArkStationClient.Sha256Hex(Encoding.UTF8.GetBytes(AegisScanner.Redact(text)));
    }

    private void RemoveItem(StackItem item)
    {
        _items.Remove(item);
        Rebuild();
    }

    // ---- Grouping, ranking and the views -----------------------------------------------------------------------

    /// <summary>Regroups the stack by origin, keeping each group's steering, fields and synthesis across rebuilds.</summary>
    private void Regroup()
    {
        var old = _groups.Where(g => g.Key != "ask").ToDictionary(g => g.Key);
        var groups = new List<StackGroup>();
        foreach (var it in _items)
        {
            var g = groups.FirstOrDefault(x => x.Key == it.Origin.ContextKey);
            if (g == null)
            {
                g = old.TryGetValue(it.Origin.ContextKey, out var prev) ? prev : new StackGroup { Key = it.Origin.ContextKey };
                g.Items.Clear();
                g.Label = it.Origin.ContextLabel;
                groups.Add(g);
            }
            g.Items.Add(it);
        }
        if (groups.Count == 0 && !string.IsNullOrWhiteSpace(_intentBox.Text))
        {
            _virtualAsk ??= new StackGroup { Key = "ask", Label = "YOUR ASK" };
            groups.Add(_virtualAsk);
        }
        for (int i = 0; i < groups.Count; i++) groups[i].Letter = ((char)('A' + i)).ToString();
        _groups = groups;
        _active = Math.Clamp(_active, 0, Math.Max(0, groups.Count - 1));
    }

    private string AskFor(StackGroup g) => g == Active ? _intentBox.Text : "";

    private void Rank()
    {
        foreach (var g in _groups)
        {
            // A Nemotron ranking stands until the group's captures or ask change.
            if (_refinedSig.TryGetValue(g.Key, out var sig) && sig == Signature(g)) continue;
            IntentRanker.Rank(g, AskFor(g), _ark);
            ScrollPredictor.Fill(g, AskFor(g));
        }
    }

    /// <summary>Rebuilds everything that depends on the stack's shape: groups, ranking, cards, the scroll.</summary>
    private void Rebuild()
    {
        Regroup();
        Rank();
        _composer.RestMode = _items.Count > 0 ? CaptureWellMode.Captured : CaptureWellMode.Idle;
        if (_composer.Mode != CaptureWellMode.DragOver) _composer.SetMode(_composer.RestMode);
        BuildStackViews();
        BuildCards();
        RefreshScroll();
        ScheduleRefine();
    }

    private void BuildStackViews()
    {
        foreach (Control c in _stackHost.Controls.Cast<Control>().ToList()) { _stackHost.Controls.Remove(c); c.Dispose(); }
        int h = 0;
        float s = PhosphorPaint.Scale(_stackHost);
        int gap = (int)Math.Round(8 * s);
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            var g = _groups[i];
            if (g.Items.Count == 0) continue;
            int index = i;
            var view = new StackGroupView(g, i == _active, RemoveItem, () => SetActive(index), s) { Dock = DockStyle.Top };
            if (h > 0) _stackHost.Controls.Add(WidgetChrome.Spacer(gap));
            _stackHost.Controls.Add(view);
            h += view.Height + (h > 0 ? gap : 0);
        }
        _stackHost.Height = h;
        _stackHost.Visible = _stackGap.Visible = h > 0;
    }

    /// <summary>One prediction card per group, then the Aegis gate when the stack has text to scan.</summary>
    private void BuildCards()
    {
        _body.SuspendLayout();
        foreach (var c in _cards) { _body.Controls.Remove(c); c.Dispose(); }
        _cards.Clear();
        for (int i = 0; i < _groups.Count; i++)
        {
            int index = i;
            var g = _groups[i];
            _cards.Add(new PredictionCard(g, OmnitekLine(g), id => ChooseIntent(g, id), () => SetActive(index)) { Active = i == _active, Margin = new Padding(0, 0, 0, 8) });
        }
        var findings = _items.SelectMany(it => it.Secrets.Select(f => (Where: $"stack {GroupOf(it)?.Letter}, {it.Kind.ToString().ToLowerInvariant()} \"{(it.Name.Length > 28 ? it.Name[..28] + "..." : it.Name)}\"", Finding: f))).ToList();
        if (_items.Any(it => it.Scanned))
            _cards.Add(new AegisCard(findings, _items.Any(it => it.DataUrl != null), StowSecrets) { Margin = new Padding(0, 0, 0, 8) });
        int at = _body.Controls.IndexOf(_composer) + 1;
        foreach (var c in _cards) { _body.Controls.Add(c); _body.Controls.SetChildIndex(c, at++); }
        _body.ResumeLayout();
        Relayout();
    }

    private StackGroup? GroupOf(StackItem it) => _groups.FirstOrDefault(g => g.Items.Contains(it));

    private string OmnitekLine(StackGroup g)
    {
        var d = _ark.AutoDetectOmnitekItem(g.Items.FirstOrDefault(i => i.Path != null)?.Path ?? g.Items.FirstOrDefault()?.Name,
            string.Join("\n", g.Items.Select(i => i.Text ?? i.Url ?? "")), AskFor(g));
        string item = string.IsNullOrEmpty(d.ItemSubtype) ? d.ItemType : $"{d.ItemType} // {d.ItemSubtype.Replace('-', ' ')}";
        return $"OMNITEK // {g.Label} // {item} // {d.Confidence}".ToUpperInvariant();
    }

    private void SetActive(int index)
    {
        if (index == _active || index >= _groups.Count) return;
        _active = index;
        Rebuild();
    }

    private void ChooseIntent(StackGroup g, string id)
    {
        g.ChosenIntent = id;
        g.ChosenByHand = true;
        ScrollPredictor.Fill(g, AskFor(g));
        foreach (var c in _cards) c.Invalidate();
        RefreshScroll();
        ScheduleRefine();
    }

    private void OnAskChanged()
    {
        bool hasVirtual = _virtualAsk != null && _groups.Contains(_virtualAsk);
        bool needVirtual = _items.Count == 0 && !string.IsNullOrWhiteSpace(_intentBox.Text);
        if (hasVirtual != needVirtual) { Rebuild(); return; }
        if (Active is { } g)
        {
            IntentRanker.Rank(g, _intentBox.Text, _ark);
            ScrollPredictor.Fill(g, _intentBox.Text);
            foreach (var c in _cards) c.Invalidate();
            ScheduleRefine();
        }
        RefreshScroll();
    }

    private void SizeBodyChildren()
    {
        int w = _body.ClientSize.Width;
        foreach (Control c in _body.Controls) if (c.Width != w) c.Width = w;
    }

    /// <summary>The widget is as tall as what it holds, up to the screen; past that the body scrolls.</summary>
    private void Relayout()
    {
        int content = _intentBox.Height + _captureBar.Height + (_stackHost.Height > 0 ? _stackHost.Height + _stackGap.Height : 0);
        _composer.Height = _composer.HeightFor(content);
        _scroll.Visible = _scrollOpen;
        _summary.Visible = _groups.Count > 0;
        if (_scrollOpen) _scroll.Height = _scroll.Padding.Vertical + _scroll.Controls.Cast<Control>().Sum(c => c.Height);

        SizeBodyChildren();
        int w = _body.ClientSize.Width;
        foreach (var c in _cards)
        {
            if (c is PredictionCard p) p.Height = p.HeightFor(w);
            else if (c is AegisCard a) a.Height = a.HeightFor(w);
        }

        int body = 0;
        foreach (Control c in _body.Controls)
            if (c == _scroll ? _scrollOpen : c == _summary ? _groups.Count > 0 : true) body += c.Height + c.Margin.Vertical;
        int chrome = Padding.Vertical;
        foreach (Control c in Controls) if (c != _body && c.Dock is DockStyle.Top or DockStyle.Bottom) chrome += c.Height;
        var area = (Visible ? Screen.FromControl(this) : Screen.FromPoint(Location)).WorkingArea;
        // The offscreen as-built render shows the whole stack. A visible window, sample states included, never
        // runs past the working area (its footer went under the taskbar); its body scrolls instead.
        int height = _preview && !Visible ? chrome + body + 4 : Math.Min(chrome + body + 4, area.Height - 24);
        if (height != Height)
        {
            var top = Location;
            Height = height;
            if (Visible) Location = new Point(top.X, WidgetChrome.SafeClamp(top.Y, area.Top + 12, area.Bottom - Height - 12));
        }
        Invalidate(true);
    }

    // ---- The scroll --------------------------------------------------------------------------------------------

    private static List<ScrollField> FieldsOf(StackGroup g) => IntentCatalog.Shape(g.ChosenIntent).Fields
        .Select(l => g.Fields.TryGetValue(l, out var f) ? f : new ScrollField(l, "", "PREDICTED")).ToList();

    private string SealOf(StackGroup g) => HandoffSeal.Seal(g.Chosen.PodId, g.ChosenIntent, AskFor(g), FieldsOf(g), g.Items.Select(i => i.Sha256 ?? i.Name));

    private void RefreshScroll()
    {
        var g = Active;
        string ask = _intentBox.Text.Trim();
        _suggestion = g == null ? "" : Suggestion(g);
        _intentBox.Prompt = _suggestion.Length > 0 ? _suggestion : "What should happen? Drop, paste or pick anything; it stacks by where it came from.";
        // The stack's context eyebrows already count each group, so the hint only offers the suggestion.
        _suggestionHint.Text = _suggestion.Length > 0 && ask.Length == 0 ? "TAB // USE THE SUGGESTION" : "";

        if (g == null)
        {
            if (_undoTimer == null && !_working) _sendBtn.Text = "Send";
            Relayout();
            return;
        }
        var shape = IntentCatalog.Shape(g.ChosenIntent);
        var pod = g.Chosen.Pod;
        string orch = ShortName(pod.OrchestratorLabel);
        if (_undoTimer == null && !_working) _sendBtn.Text = $"Send {g.Letter} to {pod.Label}";

        var fields = FieldsOf(g);
        _summary.Kicker = $"SCROLL {g.Letter} // {shape.Kicker}";
        _summary.Sentence = $"{fields.FirstOrDefault(f => f.Value.Length > 0)?.Value ?? shape.Label} Receipt from {orch}.";
        _summary.Accent = PhosphorTheme.ResolveTokenColor(pod.AccentToken);
        _summary.Muted = !HasAnything;
        _summary.Invalidate();

        // Rebuild the field rows only when the shape changes, so typing in a field keeps its caret.
        if (_scrollShape != g.Key + "|" + shape.Id)
        {
            foreach (Control c in _scroll.Controls.Cast<Control>().ToList()) { _scroll.Controls.Remove(c); c.Dispose(); }
            _fieldRows.Clear();
            float s = PhosphorPaint.Scale(_scroll);
            for (int i = fields.Count - 1; i >= 0; i--)
            {
                var f = fields[i];
                var group = g;
                var row = new ScrollFieldRow(i + 1, f, v =>
                {
                    group.Fields[f.Label] = new ScrollField(f.Label, v, "YOU EDITED");
                    UpdateSeal(group);
                }, s) { Dock = DockStyle.Top };
                _fieldRows.Insert(0, row);
                _scroll.Controls.Add(row);
                if (i > 0) _scroll.Controls.Add(WidgetChrome.Spacer((int)Math.Round(8 * s)));
            }
            _scrollShape = g.Key + "|" + shape.Id;
        }
        else
        {
            for (int i = 0; i < fields.Count && i < _fieldRows.Count; i++) _fieldRows[i].Set(fields[i]);
        }

        _scroll.Code = pod.PodId.Length <= 5 ? pod.PodId.ToUpperInvariant() : pod.PodId[..5].ToUpperInvariant();
        _scroll.Title = shape.Label;
        _scroll.Eyebrow = $"Scroll // Handoff // Stack {g.Letter} // Drafted by Scribe";
        _scroll.Alt = $"{pod.InboxChannel} // pulse {pod.OrchestratorAgentId}";
        _scroll.Board = "";
        _scroll.Edge = PhosphorTheme.ResolveTokenColor(pod.AccentToken);
        _scroll.Specs = new[]
        {
            ("To", $"{pod.Label} · {orch}"),
            ("Intent", shape.Kicker),
            ("Confidence", $"{Math.Round(g.Chosen.Score * 100)}% {g.WhySource.ToLowerInvariant()}"),
            ("Captures", g.Items.Count.ToString())
        };
        _scroll.Status = "Draft";
        _scroll.StatusState = PhosphorBadgeState.Unstated;
        UpdateSeal(g);
        Relayout();
    }

    private void UpdateSeal(StackGroup g)
    {
        if (g.Items.Any(i => i.Sha256 == null && i.Path != null && File.Exists(i.Path) && i.Secrets.Count == 0))
        {
            _scroll.Seal = "";
            _scroll.Receipt = "SEAL // hashing the captures";
        }
        else
        {
            string seal = SealOf(g);
            _scroll.Seal = seal;
            _scroll.Receipt = $"SEAL // SHA-256 // {seal[..12]}...{seal[^8..]} // CLICK BARS TO COPY";
        }
        _scroll.Invalidate();
    }

    private static string Suggestion(StackGroup g)
    {
        var first = g.Items.FirstOrDefault();
        if (first == null) return "";
        return g.ChosenIntent switch
        {
            "fix" => first.Kind == CaptureKind.Pick ? "Look at this element and say what should change" : $"Find out why {first.Name} breaks and fix it",
            "flow" => $"Turn {first.Name} into a Spell I can rerun",
            "research" => $"Read {first.Name} and give me a one-page digest",
            "absorb" => $"Import {first.Name} into the library as a dataset",
            _ => $"Draft a post around {first.Name}"
        };
    }

    // ---- Nemotron refinement -----------------------------------------------------------------------------------

    private bool CanRefine => !_preview && (_ark.LastGatewayOnline || !string.IsNullOrWhiteSpace(_ark.ReadNebiusApiKey()));

    private string Signature(StackGroup g) => string.Join("|", g.Items.Select(i => i.Id)) + "|" + AskFor(g).Trim() + "|" + (g.ChosenByHand ? g.ChosenIntent : "");

    private void ScheduleRefine()
    {
        if (!CanRefine) return;
        _refineTimer.Stop();
        _refineTimer.Start();
    }

    /// <summary>Asks Nemotron to re-rank, explain and synthesize each group whose captures or ask changed.</summary>
    private async System.Threading.Tasks.Task RefineAsync()
    {
        if (!CanRefine) return;
        _refineCts?.Cancel();
        var cts = _refineCts = new CancellationTokenSource();
        foreach (var g in _groups.ToList())
        {
            if (g.Items.Count == 0 && AskFor(g).Trim().Length == 0) continue;
            string sig = Signature(g);
            if (_refinedSig.TryGetValue(g.Key, out var done) && done == sig) continue;
            g.Refining = true;
            foreach (var c in _cards) c.Invalidate();
            BuildCards();
            bool ok = false;
            try { ok = await StackRefiner.RefineAsync(g, AskFor(g), _ark, cts.Token); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            g.Refining = false;
            if (cts.IsCancellationRequested || IsDisposed) return;
            if (ok)
            {
                _refinedSig[g.Key] = Signature(g);
                ScrollPredictor.Fill(g, AskFor(g));
            }
            BuildCards();
            RefreshScroll();
        }
    }

    // ---- Aegis -------------------------------------------------------------------------------------------------

    /// <summary>Keeps each found secret in Windows Credential Manager (VaultStow) under an Omniwand:Aegis target.</summary>
    private void StowSecrets()
    {
        var found = _items.SelectMany(i => i.Secrets).GroupBy(f => f.Secret).Select(x => x.First()).ToList();
        int stowed = 0;
        foreach (var f in found)
        {
            string target = $"Omniwand:Aegis:{f.Kind.Replace(' ', '-')}:{ArkStationClient.Sha256Hex(Encoding.UTF8.GetBytes(f.Secret))[..8]}";
            if (ArkStationClient.TryWriteWindowsCredential(target, f.Secret)) stowed++;
        }
        PhosphorToast.Show(stowed == found.Count ? $"Stowed {stowed} in the vault" : $"Stowed {stowed} of {found.Count}",
            "Windows Credential Manager, targets Omniwand:Aegis:*. The handoff still carries them redacted.", "AEGIS // VAULTSTOW", 3600,
            stowed == found.Count ? ToastTone.Success : ToastTone.Warning);
    }

    // ---- Send with undo ----------------------------------------------------------------------------------------

    private void OnSendClicked()
    {
        if (_undoTimer != null) { CancelPendingSend(); return; }
        if (_working) return;
        var g = Active;
        if (g == null || !HasAnything)
        {
            SetStatus("NOTHING TO SEND // drop, paste, pick or type something first", PhosphorTheme.Dim);
            _intentBox.Focus();
            return;
        }
        _undoLeft = UndoSeconds;
        _sendBtn.Text = "Undo";
        _sendBtn.Variant = PhosphorButtonVariant.Default;
        _sendBtn.Invalidate();
        void Countdown() => SetStatus($"SENDING {g.Letter} TO {g.Chosen.Pod.Label.ToUpperInvariant()} IN {_undoLeft}S // ESC UNDOES", PhosphorTheme.Patina);
        Countdown();
        _undoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _undoTimer.Tick += async (_, _) =>
        {
            _undoLeft--;
            if (_undoLeft > 0) { Countdown(); return; }
            StopUndoTimer();
            await SendAsync(g);
        };
        _undoTimer.Start();
        Invalidate();
    }

    private void CancelPendingSend()
    {
        StopUndoTimer();
        SetStatus("NOT SENT // still here to edit", PhosphorTheme.Dim);
    }

    private void StopUndoTimer()
    {
        _undoTimer?.Stop();
        _undoTimer?.Dispose();
        _undoTimer = null;
        _sendBtn.Variant = PhosphorButtonVariant.Primary;
        _sendBtn.Invalidate();
        RefreshScroll();
        Invalidate();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.ForeColor = color;
        _statusLabel.Text = text;
    }

    /// <summary>Writes the handoff (the scroll plus each capture, redacted) to the outbound queue and drains it.</summary>
    private async System.Threading.Tasks.Task SendAsync(StackGroup g)
    {
        var pod = g.Chosen.Pod;
        _sendBtn.Enabled = false;
        _working = true;
        _setThinkingState(true);
        SetStatus($"WRITING SCROLL {g.Letter} // QUEUEING FOR AINBOX", PhosphorTheme.Patina);
        try
        {
            string ask = AskFor(g).Trim();
            var fields = FieldsOf(g);
            string seal = SealOf(g);
            string scrollId = $"scroll:handoff:{pod.PodId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}";
            var items = new List<QueuedItem>();
            var draft = new StackHandoff(scrollId, g.Letter, g.Label, pod.PodId, g.ChosenIntent, IntentCatalog.Shape(g.ChosenIntent).Label, ask, fields, items, seal, DateTime.UtcNow.ToString("O"));
            items.Add(new QueuedItem("scroll", "handoff-scroll.md", "Scroll", "text", "text/markdown", null, HandoffSeal.Markdown(draft, pod), null, null, new()));
            foreach (var it in g.Items) items.Add(ToQueued(it));
            if (_queue.LocalOnly)
            {
                string saved = await _queue.SaveLocallyAsync(draft);
                SetStatus($"SAVED {g.Letter} // DOCUMENTS\\HOLODEX\\SCROLLS // sha256:{seal[..12]}", PhosphorTheme.Moss);
                PhosphorToast.Show("Scroll saved",
                    $"{Path.GetFileName(saved)} is sealed sha256:{seal[..12]} and saved in Documents\\Holodex\\Scrolls.",
                    "AINBOX // SCROLL", 4200, ToastTone.Success);
                ClearSent(g);
                return;
            }
            var sent = await _queue.EnqueueAsync(draft);
            bool delivered = sent.Items.All(i => i.AInboxItemId != null);
            string head = delivered ? $"SENT {g.Letter} // {items.Count} TO AINBOX // {pod.Label.ToUpperInvariant()}" : $"QUEUED {g.Letter} // AINBOX NOT READY, RETRYING";
            SetStatus($"{head} // sha256:{seal[..12]}", delivered ? PhosphorTheme.Moss : PhosphorTheme.Seal);
            PhosphorToast.Show(delivered ? "Intake submitted" : "Intake queued",
                delivered
                    ? $"Handoff Scroll sent to {pod.Label} and its inbox pulsed. Seal sha256:{seal[..12]}."
                    : $"AInbox is not ready, so the Handoff Scroll waits on disk and retries. Seal sha256:{seal[..12]}.",
                $"AINBOX // POD {pod.PodId.ToUpperInvariant()}", 3600, delivered ? ToastTone.Success : ToastTone.Info);
            ClearSent(g);
        }
        catch (Exception ex)
        {
            SetStatus($"NOT SENT // {ex.Message}", PhosphorTheme.Rust);
            if (!Visible) PhosphorToast.Show("Handoff not sent", ex.Message, "AINBOX // HANDOFF", 4200, ToastTone.Error);
        }
        finally
        {
            _working = false;
            _setThinkingState(false);
            _sendBtn.Enabled = true;
            RefreshScroll();
            Invalidate();
        }
    }

    /// <summary>A sent stack leaves the composer: its captures, refinement and draft scroll are cleared.</summary>
    private void ClearSent(StackGroup g)
    {
        foreach (var it in g.Items.ToList()) _items.Remove(it);
        _refinedSig.Remove(g.Key);
        if (g == _virtualAsk) _virtualAsk = null;
        _intentBox.Text = "";
        _scrollOpen = false;
        _summary.Expanded = false;
        Rebuild();
    }

    /// <summary>A capture as it leaves: secrets redacted, images decoded to PNG bytes, origin attached.</summary>
    private static QueuedItem ToQueued(StackItem it)
    {
        var origin = it.Origin.Rows().ToDictionary(r => r.Key.ToLowerInvariant().Replace(' ', '-'), r => r.Value);
        string? path = it.Path, text = null, url = null;
        if (it.Url != null) url = AegisScanner.Redact(it.Url);
        else if (it.Text != null) text = AegisScanner.Redact(it.Text);
        else if (it.DataUrl != null) path = OutboundQueue.StoreBlob(it.Name, DecodeDataUrl(it.DataUrl));
        else if (it.Secrets.Count > 0 && it.TextForAnalysis() is { } body) path = OutboundQueue.StoreBlob(it.Name, Encoding.UTF8.GetBytes(AegisScanner.Redact(body)));
        return new QueuedItem(it.Id, it.Name, it.Kind.ToString(), it.Channel, it.MediaType, path, text, url, it.Sha256, origin);
    }

    private void SyncQueueStatus()
    {
        _sync.LocalOnly = _queue.LocalOnly;
        _sync.Pending = _queue.LocalOnly ? 0 : _queue.Pending;
        _sync.SentToday = _queue.LocalOnly ? _queue.SavedToday : _queue.SentToday;
        _sync.Online = _queue.ServiceOnline;
        _sync.Invalidate();
    }

    /// <summary>Closing the widget while a send is pending sends it now; the result comes back as a toast.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible || _undoTimer == null || Active is not { } g) return;
        StopUndoTimer();
        _ = SendAsync(g);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working || _undoTimer != null ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }

    // ---- As-built states ---------------------------------------------------------------------------------------

    /// <summary>For the as-built capture: "drag", "captured", "stack" (two contexts, a secret), "review", "reset".</summary>
    internal void PreviewState(string state)
    {
        _preview = state != "reset";
        _composer.Unfreeze();
        _composer.SetMode(CaptureWellMode.Idle);
        _items.Clear();
        _groups.Clear();
        _virtualAsk = null;
        _refinedSig.Clear();
        _active = 0;
        _intentBox.Text = "";
        _scrollOpen = false;
        _summary.Expanded = false;
        switch (state)
        {
            case "drag":
                Rebuild();
                _composer.SetMode(CaptureWellMode.DragOver, CaptureKind.File);
                _composer.FreezeForCapture(0.18f);
                break;
            case "captured":
                _items.Add(new StackItem { Kind = CaptureKind.File, Name = "report.pdf", Path = "report.pdf", Meta = "FILE // 1.4 MB", Origin = new CaptureOrigin("File", "Downloads", Element: "PDF") });
                Rebuild();
                break;
            case "stack":
            case "review":
                var chrome = new CaptureOrigin("Google Chrome", "Pull Request #214 · acme/storefront - Google Chrome", "https://github.com/acme/storefront/pull/214/files", "div.diff-file", "acme/storefront", null);
                var exports = new CaptureOrigin("File", "exports", Element: "CSV");
                _items.Add(new StackItem { Kind = CaptureKind.Pick, Name = "div.diff-file, line 42", DataUrl = SampleShot(), Meta = "PICK // CHROME // 540x92", Origin = chrome });
                var snippet = new StackItem { Kind = CaptureKind.Snippet, Name = "button.disabled = false;", Text = "button.disabled = false;\nconst token = \"ghp_4f2c91d0aa7e93b1c6d2e8f0\";\nawait submitOrder(cart);", Meta = "SNIPPET // 3 LINES", Origin = chrome };
                Scan(snippet);
                _items.Add(snippet);
                var link = new StackItem { Kind = CaptureKind.Link, Name = "github.com/acme/storefront/pull/214", Url = "https://github.com/acme/storefront/pull/214", Meta = "LINK", Origin = chrome };
                Scan(link);
                _items.Add(link);
                _items.Add(new StackItem { Kind = CaptureKind.File, Name = "bookings-oct.csv", Path = "bookings-oct.csv", Meta = "FILE // 48.2 KB // 1,204 ROWS", Origin = exports });
                var sql = new StackItem { Kind = CaptureKind.Snippet, Name = "select * from bookings where month = 10", Text = "select * from bookings where month = 10", Meta = "SNIPPET // 39 CHARS", Origin = exports };
                Scan(sql);
                _items.Add(sql);
                _intentBox.Text = "Why does checkout submit the order twice?";
                Rebuild();
                if (state == "review")
                {
                    _scrollOpen = true;
                    _summary.Expanded = true;
                    Relayout();
                    // The window cannot be taller than the screen, so the review shot scrolls to the Aegis gate and the scroll.
                    if (_cards.LastOrDefault() is { } aegis) _body.AutoScrollPosition = new Point(0, aegis.Top - _body.AutoScrollPosition.Y - 8);
                }
                break;
            default:
                Rebuild();
                break;
        }
        Refresh();
    }

    private static string SampleShot()
    {
        using var bmp = new Bitmap(540, 92);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(22, 27, 34));
        return ToDataUrl(bmp);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private static string ToDataUrl(Image img)
    {
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static byte[] DecodeDataUrl(string dataUrl) => Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);

    private static string FileMeta(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            string size = bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";
            return $"FILE // {size}";
        }
        catch { return "FILE"; }
    }

    private static string ShortName(string label)
    {
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? label[..paren] : label;
    }

    private static string Slug(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } x ? x : "window";
}
