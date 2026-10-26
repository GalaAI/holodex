using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// AInbox intake (Alt+I, Actions Ring slot 8, pipe ainbox:summon). One composer: what should happen, plus
/// whatever you drop, paste or pick, with a capture bar under it. Omnitek proposes the pod (a route chip you can
/// steer) and Scribe drafts the handoff scroll, shown as one line that opens into the CP-20 SpecCard ticket for
/// review. Send waits five seconds so it can be undone, then writes the scroll and pulses the pod orchestrator.
/// </summary>
public sealed class AInboxIntakeWidgetForm : Form
{
    private const int UndoSeconds = 5;

    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly CaptureWellControl _composer;
    private readonly CapturedRow _captureRow;
    private readonly Panel _captureGap;
    private readonly CapturedRow _contentRow;
    private readonly Panel _contentGap;
    private readonly PromptTextBox _intentBox;
    private readonly Panel _captureBar;
    private readonly DataLabel _suggestionHint;
    private readonly ScrollSummaryRow _summary;
    private readonly Panel _scrollGap;
    private readonly SpecCardPanel _scroll;
    private readonly PhosphorField _scrollContextField;
    private readonly PhosphorField _scrollGoalField;
    private readonly PhosphorField _evidenceReceivedField;
    private readonly PhosphorField _evidenceWorkingField;
    private readonly PhosphorField _evidenceDeliverableField;
    private readonly RouteChip _route;
    private readonly DataLabel _statusLabel;
    private readonly PhosphorButton _sendBtn;

    private string? _selectedFilePath;
    private string? _capturedDataUrl;
    private CaptureKind _captureKind = CaptureKind.File;
    private string? _content;
    private CaptureKind _contentKind = CaptureKind.Snippet;
    private string _suggestion = "";
    private int _podIndex = 0;
    private Point _summonPos;
    private bool _userChosePodManually;
    private bool _scrollOpen;
    private bool _working;
    private System.Windows.Forms.Timer? _undoTimer;
    private int _undoLeft;

    /// <summary>Opens the snip tools (Pick, Lasso); their Send to AInbox comes back through SummonAt.</summary>
    internal Action<WandCaptureTool>? RequestCapture { get; set; }

    public AInboxIntakeWidgetForm(ArkStationClient ark, Action<bool> setThinkingState)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;

        PhosphorFonts.EnsureLoaded();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "AInbox";
        Size = new Size(640, 420);
        Padding = WidgetChrome.FramePadding();

        // Composer (CP-27 well at rest, the whole drop target while a drag is over the widget).
        _composer = new CaptureWellControl { Dock = DockStyle.Top, Name = "AInbox.Well" };
        var stack = _composer.Content;

        _captureRow = new CapturedRow("AInbox.CapturedRow") { Dock = DockStyle.Top, Height = 48, Visible = false };
        _captureRow.RemoveClicked += (_, _) => ClearCapture();
        _captureGap = WidgetChrome.Spacer(6);
        _contentRow = new CapturedRow("AInbox.ContentRow") { Dock = DockStyle.Top, Height = 48, Visible = false };
        _contentRow.RemoveClicked += (_, _) => ClearContent();
        _contentGap = WidgetChrome.Spacer(6);

        _intentBox = new PromptTextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.None,
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            Font = PhosphorFonts.Body(PhosphorTheme.TextBody),
            Name = "AInbox.IntentField"
        };
        _intentBox.TextChanged += (_, _) => RefreshAutoDetectionAndScroll();

        // Capture bar: the live kinds as icons; the right side offers Omnitek's suggested intent.
        _captureBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "AInbox.CaptureBar" };
        _suggestionHint = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "AInbox.SuggestionHint" };
        _captureBar.Controls.Add(_suggestionHint);
        _captureBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10, BackColor = Color.Transparent });
        foreach (var info in CaptureKinds.All.Where(k => !k.Planned).Reverse())
        {
            var tool = new CaptureToolButton(info) { Dock = DockStyle.Left, Width = 44 };
            tool.Click += (_, _) => OnCaptureTool(info.Kind);
            _captureBar.Controls.Add(tool);
        }

        stack.Controls.Add(_captureBar);
        stack.Controls.Add(_intentBox);
        stack.Controls.Add(_contentGap);
        stack.Controls.Add(_contentRow);
        stack.Controls.Add(_captureGap);
        stack.Controls.Add(_captureRow);

        // Scroll summary: one line, opens into the full ticket.
        _summary = new ScrollSummaryRow { Dock = DockStyle.Top, Height = 44, Name = "AInbox.ScrollSummary" };
        _summary.Toggled += (_, _) => SetScrollOpen(!_scrollOpen);

        // Handoff scroll as a SpecCard ticket (CP-20), closed until reviewed.
        _scroll = new SpecCardPanel { Dock = DockStyle.Top, Visible = false, Name = "AInbox.HandoffScroll" };
        _scrollContextField = new PhosphorField("", true, 48, mono: true) { Name = "AInbox.ScrollContextField" };
        _scrollContextField.Box.Name = "AInbox.ScrollContextBox";
        _scrollGoalField = new PhosphorField("", true, 48, mono: true) { Name = "AInbox.ScrollGoalField" };
        _scrollGoalField.Box.Name = "AInbox.ScrollGoalBox";
        _evidenceReceivedField = new PhosphorField("", false, 36, mono: true) { Name = "AInbox.ProofOfReceivedField" };
        _evidenceReceivedField.Box.Name = "AInbox.ProofOfReceivedBox";
        _evidenceWorkingField = new PhosphorField("", true, 48, mono: true) { Name = "AInbox.WorkingStateField" };
        _evidenceWorkingField.Box.Name = "AInbox.WorkingStateBox";
        _evidenceDeliverableField = new PhosphorField("", true, 48, mono: true) { Name = "AInbox.DeliverableEvidenceField" };
        _evidenceDeliverableField.Box.Name = "AInbox.DeliverableEvidenceBox";

        var rowContext = WidgetChrome.Labeled("1 // Context", _scrollContextField, 48);
        ((Control)rowContext.Tag!).Name = "AInbox.ScrollContextLabel";
        var rowGoal = WidgetChrome.Labeled("2 // Goal", _scrollGoalField, 48);
        ((Control)rowGoal.Tag!).Name = "AInbox.ScrollGoalLabel";
        var rowProof = WidgetChrome.Labeled("3A // Proof of received", _evidenceReceivedField, 36);
        ((Control)rowProof.Tag!).Name = "AInbox.ProofOfReceivedLabel";
        var rowWorking = WidgetChrome.Labeled("3B // Working state", _evidenceWorkingField, 48);
        ((Control)rowWorking.Tag!).Name = "AInbox.WorkingStateLabel";
        var rowDeliverable = WidgetChrome.Labeled("3C // Deliverable evidence", _evidenceDeliverableField, 48);
        ((Control)rowDeliverable.Tag!).Name = "AInbox.DeliverableEvidenceLabel";

        _scroll.Controls.Add(rowDeliverable);
        _scroll.Controls.Add(WidgetChrome.Spacer(8));
        _scroll.Controls.Add(rowWorking);
        _scroll.Controls.Add(WidgetChrome.Spacer(8));
        _scroll.Controls.Add(rowProof);
        _scroll.Controls.Add(WidgetChrome.Spacer(8));
        _scroll.Controls.Add(rowGoal);
        _scroll.Controls.Add(WidgetChrome.Spacer(8));
        _scroll.Controls.Add(rowContext);
        _scrollGap = WidgetChrome.Spacer(10);
        _scrollGap.Visible = false;

        // Footer: where it goes, what is happening, and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "AInbox.Footer" };
        _route = new RouteChip { Dock = DockStyle.Left, Name = "AInbox.RouteChip" };
        _route.Click += (_, _) => ShowRouteMenu();
        _statusLabel = new DataLabel("CTRL+ENTER SENDS") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "AInbox.ReceiptStatus" };
        _sendBtn = new PhosphorButton("Send", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 196, Name = "AInbox.SubmitButton" };
        _sendBtn.Click += (_, _) => OnSendClicked();
        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_route);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_sendBtn);

        Controls.Add(_scroll);
        Controls.Add(_scrollGap);
        Controls.Add(_summary);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_composer);
        Controls.Add(WidgetChrome.Spacer(2));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(footer);
        WidgetChrome.Attach(this, "AINBOX // INTAKE", "AInbox", PhosphorTheme.Seal, null);
        WidgetDrag.Enable(this);

        // A drop anywhere on the widget lands in the composer, and the well shows it while it is over.
        WireDrop(this);
        WireDrop(_composer);
        WireDrop(_intentBox);

        RefreshAutoDetectionAndScroll();
        Relayout();
    }

    private bool HasCapture => _selectedFilePath != null;
    private bool HasContent => !string.IsNullOrEmpty(_content);
    private bool HasAnything => HasCapture || HasContent || !string.IsNullOrWhiteSpace(_intentBox.Text);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { OnSendClicked(); return true; }
        if (keyData == Keys.Tab && _intentBox.Focused && _intentBox.TextLength == 0 && _suggestion.Length > 0)
        {
            _intentBox.Text = _suggestion;
            _intentBox.SelectionStart = _intentBox.TextLength;
            return true;
        }
        if (keyData == (Keys.Control | Keys.V) && _intentBox.Focused && PasteAsAttachment()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens the widget at the cursor. A capture handed over from the snip tools is attached.</summary>
    public void SummonAt(Point cursorPos, string? preCapturedDataUrl = null, string? captureName = null)
    {
        _summonPos = cursorPos;
        if (!string.IsNullOrEmpty(preCapturedDataUrl))
        {
            _capturedDataUrl = preCapturedDataUrl;
            string name = captureName ?? "lasso-capture.png";
            bool pick = name.StartsWith("element", StringComparison.OrdinalIgnoreCase);
            SetCaptured(pick ? CaptureKind.Pick : CaptureKind.Lasso, name, pick ? "PICK // element + origin" : "LASSO // region", keepDataUrl: true);
        }

        RefreshAutoDetectionAndScroll();
        Relayout();

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);

        Show();
        Activate();
        _intentBox.Focus();
    }

    /// <summary>For the as-built capture: "drag", "captured" (a sample file), "review" (that file, scroll open), "reset".</summary>
    internal void PreviewState(string state)
    {
        switch (state)
        {
            case "drag":
                _composer.SetMode(CaptureWellMode.DragOver, CaptureKind.File);
                _composer.FreezeForCapture(0.18f);
                break;
            case "captured":
                _composer.Unfreeze();
                SetCaptured(CaptureKind.File, "report.pdf", "FILE // 1.4 MB");
                _composer.FreezeForCapture(0f);
                break;
            case "review":
                _composer.Unfreeze();
                SetCaptured(CaptureKind.File, "report.pdf", "FILE // 1.4 MB");
                _composer.FreezeForCapture(0f);
                SetScrollOpen(true);
                break;
            default:
                _composer.Unfreeze();
                ResetComposer();
                break;
        }
        Refresh();
    }

    // ---- Layout ----------------------------------------------------------------------------------------

    /// <summary>The widget is as tall as what it holds: the composer grows with attachments, the scroll opens below.</summary>
    private void Relayout()
    {
        _captureRow.Visible = _captureGap.Visible = HasCapture;
        _contentRow.Visible = _contentGap.Visible = HasContent;
        _scroll.Visible = _scrollGap.Visible = _scrollOpen;

        int content = _intentBox.Height + _captureBar.Height
            + (HasCapture ? _captureRow.Height + _captureGap.Height : 0)
            + (HasContent ? _contentRow.Height + _contentGap.Height : 0);
        _composer.Height = _composer.HeightFor(content);

        int scrollHeight = _scroll.Padding.Vertical + _scroll.Controls.Cast<Control>().Sum(c => c.Height);
        _scroll.Height = scrollHeight;

        int height = Padding.Vertical;
        foreach (Control c in Controls)
        {
            bool shown = c == _scroll || c == _scrollGap ? _scrollOpen : c.Dock is DockStyle.Top or DockStyle.Bottom;
            if (shown) height += c.Height;
        }
        if (height == Height) return;

        var top = Location;
        Height = height;
        if (Visible)
        {
            var work = Screen.FromControl(this).WorkingArea;
            Location = new Point(top.X, WidgetChrome.SafeClamp(top.Y, work.Top + 12, work.Bottom - Height - 12));
        }
        Invalidate();
    }

    private void SetScrollOpen(bool open)
    {
        _scrollOpen = open;
        _summary.Expanded = open;
        Relayout();
    }

    // ---- Capture ---------------------------------------------------------------------------------------

    private void WireDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) =>
        {
            if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap)))
            {
                e.Effect = DragDropEffects.Copy;
                if (_composer.Mode != CaptureWellMode.DragOver) _composer.SetMode(CaptureWellMode.DragOver, CaptureKinds.FromDrag(e.Data));
            }
        };
        c.DragOver += (_, e) => { if (_composer.Mode == CaptureWellMode.DragOver) e.Effect = DragDropEffects.Copy; };
        c.DragLeave += (_, _) =>
        {
            // Moving between the widget's own drop targets is not leaving it.
            if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) _composer.SetMode(_composer.RestMode);
        };
        c.DragDrop += OnDragDrop;
    }

    private void OnCaptureTool(CaptureKind kind)
    {
        switch (kind)
        {
            case CaptureKind.File: ChooseFile(); break;
            case CaptureKind.Screen: AttachScreen(); break;
            case CaptureKind.Pick: OpenSnipTool(WandCaptureTool.ElementPicker); break;
            case CaptureKind.Lasso: OpenSnipTool(WandCaptureTool.FreeformLasso); break;
            case CaptureKind.Snippet: AttachClipboardText(link: false); break;
            case CaptureKind.Link: AttachClipboardText(link: true); break;
        }
    }

    private void OpenSnipTool(WandCaptureTool tool)
    {
        if (RequestCapture == null) return;
        // Hide first so the frozen desktop the snip tool captures does not include this widget.
        Hide();
        var delay = new System.Windows.Forms.Timer { Interval = 150 };
        delay.Tick += (_, _) => { delay.Stop(); delay.Dispose(); RequestCapture(tool); };
        delay.Start();
    }

    private void AttachScreen()
    {
        _capturedDataUrl = ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y, 640, 420);
        SetCaptured(CaptureKind.Screen, "cursor-capture.png", "SCREEN // 640x420", keepDataUrl: true);
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
        SetContent(text);
    }

    /// <summary>Ctrl+V in the composer: files, images, links and long or multi-line text become attachments.</summary>
    private bool PasteAsAttachment()
    {
        if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
        {
            SetCaptured(CaptureKind.File, files[0]!, FileMeta(files[0]!) + (files.Count > 1 ? $" // {files.Count - 1} more not sent" : ""));
            return true;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image img)
        {
            AttachImage(img, "pasted-image.png");
            return true;
        }
        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText().Trim();
            if (CaptureKinds.IsLink(text) || text.Contains('\n') || text.Length > 160)
            {
                SetContent(text);
                return true;
            }
        }
        return false;
    }

    private void ChooseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select a file or artifact for AInbox intake",
            Filter = "All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetCaptured(CaptureKind.File, dlg.FileName, FileMeta(dlg.FileName));
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null) { _composer.SetMode(_composer.RestMode); return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            // One capture travels with a handoff today; extra files are named so nothing is dropped silently.
            SetCaptured(CaptureKind.File, files[0], FileMeta(files[0]) + (files.Length > 1 ? $" // {files.Length - 1} more not sent" : ""));
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is Image img)
        {
            AttachImage(img, "dropped-image.png");
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) && e.Data.GetData(DataFormats.Text) is string text && text.Trim().Length > 0)
        {
            _composer.SetMode(_composer.RestMode);
            SetContent(text.Trim());
        }
        else _composer.SetMode(_composer.RestMode);
        _intentBox.Focus();
    }

    private void AttachImage(Image img, string name)
    {
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        _capturedDataUrl = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        SetCaptured(CaptureKind.Screen, name, $"IMAGE // {img.Width}x{img.Height}", keepDataUrl: true);
    }

    private void SetCaptured(CaptureKind kind, string path, string meta, bool keepDataUrl = false)
    {
        if (!keepDataUrl) _capturedDataUrl = null;
        _selectedFilePath = path;
        _captureKind = kind;
        _userChosePodManually = false;
        _captureRow.Kind = kind;
        _captureRow.ItemName = Path.GetFileName(path);
        _captureRow.Meta = meta;
        _composer.RestMode = CaptureWellMode.Captured;
        _composer.SetMode(CaptureWellMode.Captured);
        RefreshAutoDetectionAndScroll();
        Relayout();
    }

    private void ClearCapture()
    {
        _selectedFilePath = null;
        _capturedDataUrl = null;
        _composer.RestMode = HasContent ? CaptureWellMode.Captured : CaptureWellMode.Idle;
        _composer.SetMode(_composer.RestMode);
        RefreshAutoDetectionAndScroll();
        Relayout();
    }

    private void SetContent(string text)
    {
        _content = text;
        _contentKind = CaptureKinds.IsLink(text) ? CaptureKind.Link : CaptureKind.Snippet;
        _userChosePodManually = false;
        _contentRow.Kind = _contentKind;
        string first = text.Split('\n')[0].Trim();
        int lines = text.Split('\n').Length;
        _contentRow.ItemName = first;
        _contentRow.Meta = _contentKind == CaptureKind.Link ? "LINK" : lines > 1 ? $"SNIPPET // {lines} lines" : $"SNIPPET // {text.Length} chars";
        _composer.RestMode = CaptureWellMode.Captured;
        _composer.SetMode(CaptureWellMode.Captured);
        RefreshAutoDetectionAndScroll();
        Relayout();
    }

    private void ClearContent()
    {
        _content = null;
        _composer.RestMode = HasCapture ? CaptureWellMode.Captured : CaptureWellMode.Idle;
        _composer.SetMode(_composer.RestMode);
        RefreshAutoDetectionAndScroll();
        Relayout();
    }

    private void ResetComposer()
    {
        _selectedFilePath = null;
        _capturedDataUrl = null;
        _content = null;
        _userChosePodManually = false;
        _intentBox.Text = "";
        _composer.RestMode = CaptureWellMode.Idle;
        _composer.SetMode(CaptureWellMode.Idle);
        _scrollOpen = false;
        _summary.Expanded = false;
        RefreshAutoDetectionAndScroll();
        Relayout();
    }

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

    // ---- Routing and the scroll ------------------------------------------------------------------------

    /// <summary>Omnitek's prediction as TYPE // SUBTYPE, for example ARTIFACT // DELIVERABLE.</summary>
    private static string ItemLabel(OmnitekAutoDetectInfo d) => string.IsNullOrEmpty(d.ItemSubtype)
        ? d.ItemType.ToUpperInvariant()
        : $"{d.ItemType.ToUpperInvariant()} // {d.ItemSubtype.Replace('-', ' ').ToUpperInvariant()}";

    private ArkPodInboxInfo SelectedPod => ArkStationClient.PodInboxes[Math.Clamp(_podIndex, 0, ArkStationClient.PodInboxes.Count - 1)];

    /// <summary>The orchestrator's short name: "Gala" from "Gala (Co-op Orchestrator)".</summary>
    private static string ShortName(string label)
    {
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? label[..paren] : label;
    }

    /// <summary>A one-line intent Omnitek offers for what is attached; Tab takes it.</summary>
    private string SuggestIntent(OmnitekAutoDetectInfo d)
    {
        if (HasCapture)
        {
            string name = Path.GetFileName(_selectedFilePath!);
            switch (_captureKind)
            {
                case CaptureKind.Pick: return "Look at this element and say what should change";
                case CaptureKind.Lasso: return "Read this region and file what matters";
                case CaptureKind.Screen: return "Read this capture and file what matters";
            }
            return (d.ItemType, d.ItemSubtype) switch
            {
                ("artifact", "deliverable") => $"File {name} as a deliverable and confirm it landed",
                ("artifact", "desktop-app") => $"Register {name} as a desktop app",
                ("scroll", "dataset") => $"Index {name} as a dataset",
                ("scroll", "skrypt") => $"Review {name} and stage it as a Skrypt",
                ("augment", _) => $"Add {name} to the Augment inventory",
                _ => $"Ingest {name} and route it"
            };
        }
        if (HasContent) return _contentKind == CaptureKind.Link ? "Read this link and summarize what matters" : "Review this snippet and act on it";
        return "";
    }

    private void ShowRouteMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new PhosphorMenuRenderer(), ShowImageMargin = false };
        for (int i = 0; i < ArkStationClient.PodInboxes.Count; i++)
        {
            int idx = i;
            var pod = ArkStationClient.PodInboxes[i];
            var item = new ToolStripMenuItem($"{pod.Label}  ·  {ShortName(pod.OrchestratorLabel)}")
            {
                Checked = i == _podIndex,
                ShortcutKeyDisplayString = pod.InboxChannel,
                ToolTipText = pod.Description
            };
            item.Click += (_, _) =>
            {
                _podIndex = idx;
                _userChosePodManually = true;
                RefreshAutoDetectionAndScroll();
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_route, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    private void RefreshAutoDetectionAndScroll()
    {
        string intent = _intentBox.Text;
        var detected = _ark.AutoDetectOmnitekItem(_selectedFilePath, _content, intent);
        bool high = string.Equals(detected.Confidence, "high", StringComparison.OrdinalIgnoreCase);

        if (!_userChosePodManually)
        {
            int found = ArkStationClient.PodInboxes.ToList().FindIndex(p => p.PodId == detected.RecommendedPodId);
            if (found >= 0) _podIndex = found;
        }
        var pod = SelectedPod;
        Color podAccent = PhosphorTheme.ResolveTokenColor(pod.AccentToken);
        string orchestrator = ShortName(pod.OrchestratorLabel);

        // The classification shows once: on the primary attachment, or in the scroll line when nothing is attached.
        string item = ItemLabel(detected);
        _captureRow.Prediction = HasCapture ? item : "";
        _contentRow.Prediction = !HasCapture && HasContent ? item : "";
        _captureRow.Invalidate();
        _contentRow.Invalidate();

        _suggestion = SuggestIntent(detected);
        _intentBox.Prompt = _suggestion.Length > 0 ? _suggestion : "What should happen? Drop, paste or pick something to send with it.";
        _suggestionHint.Text = _suggestion.Length > 0 && intent.Length == 0 ? "TAB // USE THE SUGGESTION" : "";

        _route.PodLabel = pod.Label;
        _route.Orchestrator = orchestrator;
        _route.Accent = podAccent;
        _route.Confident = high && !_userChosePodManually;
        _route.Manual = _userChosePodManually;
        _route.FitWidth();
        _route.Invalidate();
        if (_undoTimer == null && !_working) _sendBtn.Text = $"Send to {pod.Label}";

        var previewScroll = _ark.BuildPreviewHandoffScroll(pod.PodId, detected, intent, _selectedFilePath, _content);

        string what = intent.Trim().Length > 0 ? intent.Trim().Split('\n')[0]
            : _suggestion.Length > 0 ? _suggestion
            : "Nothing to send yet";
        _summary.Kicker = HasAnything && !HasCapture && !HasContent ? $"SCROLL // {item}" : "SCROLL";
        _summary.Sentence = HasAnything ? $"{what}. Receipt from {orchestrator}." : what;
        _summary.Muted = !HasAnything;
        _summary.Accent = podAccent;
        _summary.Invalidate();

        _scrollContextField.Box.Text = previewScroll.Context;
        _scrollGoalField.Box.Text = previewScroll.Goal;
        _evidenceReceivedField.Box.Text = previewScroll.DesiredEvidence.ProofOfReceived;
        _evidenceWorkingField.Box.Text = previewScroll.DesiredEvidence.WorkingState;
        _evidenceDeliverableField.Box.Text = previewScroll.DesiredEvidence.DeliverableEvidence;

        _scroll.Code = pod.PodId.Length <= 5 ? pod.PodId.ToUpperInvariant() : pod.PodId[..5].ToUpperInvariant();
        _scroll.Title = "Handoff Scroll";
        _scroll.Eyebrow = "Augment // Scroll // Handoff";
        _scroll.Alt = $"{pod.InboxChannel} // pulse {pod.OrchestratorAgentId}";
        _scroll.Board = "Board // Scribe";
        _scroll.Edge = podAccent;
        _scroll.Specs = new[]
        {
            ("Pod", pod.Label),
            ("Item", detected.DisplayBadge),
            ("Confidence", detected.Confidence),
            ("Drafted by", "Scribe")
        };
        _scroll.Status = "Draft";
        _scroll.StatusState = PhosphorBadgeState.Unstated;
        _scroll.Receipt = "RCPT // pending // written on send";
        _scroll.Invalidate();
    }

    // ---- Send with undo --------------------------------------------------------------------------------

    private void OnSendClicked()
    {
        if (_undoTimer != null) { CancelPendingSend(); return; }
        if (_working) return;
        if (!HasAnything)
        {
            SetStatus("NOTHING TO SEND // type, drop or paste something first", PhosphorTheme.Dim);
            _intentBox.Focus();
            return;
        }

        _undoLeft = UndoSeconds;
        _sendBtn.Text = "Undo";
        _sendBtn.Variant = PhosphorButtonVariant.Default;
        _sendBtn.Invalidate();
        SetStatus($"SENDING TO {SelectedPod.Label.ToUpperInvariant()} IN {_undoLeft}S", PhosphorTheme.Patina);
        _undoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _undoTimer.Tick += async (_, _) =>
        {
            _undoLeft--;
            if (_undoLeft > 0)
            {
                SetStatus($"SENDING TO {SelectedPod.Label.ToUpperInvariant()} IN {_undoLeft}S", PhosphorTheme.Patina);
                return;
            }
            StopUndoTimer();
            await SubmitIntakeAsync();
        };
        _undoTimer.Start();
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
        _sendBtn.Text = $"Send to {SelectedPod.Label}";
        _sendBtn.Invalidate();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.ForeColor = color;
        _statusLabel.Text = text;
    }

    private async System.Threading.Tasks.Task SubmitIntakeAsync()
    {
        var pod = SelectedPod;
        _sendBtn.Enabled = false;
        _working = true;
        _setThinkingState(true);
        SetStatus($"WRITING SCROLL // pulsing {pod.OrchestratorAgentId}", PhosphorTheme.Patina);
        Invalidate();

        try
        {
            var receipt = await _ark.SubmitPodInboxIntakeAsync(
                pod.PodId,
                _intentBox.Text,
                _selectedFilePath,
                _content,
                _capturedDataUrl,
                _evidenceReceivedField.Box.Text,
                _evidenceWorkingField.Box.Text,
                _evidenceDeliverableField.Box.Text);

            bool pulsed = receipt.PulseStatus == "pulsed";
            ResetComposer();
            SetStatus(pulsed
                ? $"SENT // {receipt.OrchestratorAgentId} @ {receipt.PodInboxChannel} // {receipt.HandoffScroll.ScrollId}"
                : $"SAVED LOCALLY // ArkStation not running // {receipt.HandoffScroll.ScrollId}", PhosphorTheme.Moss);
        }
        catch (Exception ex)
        {
            SetStatus($"NOT SENT // {ex.Message}", PhosphorTheme.Rust);
        }
        finally
        {
            _working = false;
            _setThinkingState(false);
            _sendBtn.Enabled = true;
            _sendBtn.Text = $"Send to {SelectedPod.Label}";
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working || _undoTimer != null ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}

/// <summary>Where the handoff goes, as one chip: arrow, POD · ORCHESTRATOR, a confidence dot and a caret. Click steers it.</summary>
internal sealed class RouteChip : Control
{
    private bool _hover;

    public RouteChip()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        Width = 170;
    }

    public string PodLabel { get; set; } = "";
    public string Orchestrator { get; set; } = "";
    public Color Accent { get; set; } = PhosphorTheme.Patina;
    /// <summary>Omnitek is sure of the pod: the dot glows in the pod's accent. Unsure or steered by hand: a ghost dot.</summary>
    public bool Confident { get; set; }
    public bool Manual { get; set; }

    private string Label => $"{PodLabel} · {Orchestrator}".ToUpperInvariant();

    public void FitWidth()
    {
        AccessibleName = $"Send to {PodLabel}, {Orchestrator}";
        float s = PhosphorPaint.Scale(this);
        using var g = CreateGraphics();
        using var font = PhosphorFonts.Mono(10.5f * s);
        float text = PhosphorPaint.MeasureTracked(g, Label, font, 1f * s);
        Width = (int)Math.Ceiling(text + 88f * s);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float r = PhosphorTheme.RadiusButton * s;
        PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover ? PhosphorTheme.Ghost : PhosphorTheme.Line2);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), Math.Max(1f, r - 2f), Color.FromArgb(160, PhosphorTheme.Patina));

        float cy = Height / 2f;
        float x = 14f * s;
        using (var pen = new Pen(Accent, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, x, cy, x + 12f * s, cy);
            g.DrawLines(pen, new[] { new PointF(x + 7.5f * s, cy - 4.5f * s), new PointF(x + 12f * s, cy), new PointF(x + 7.5f * s, cy + 4.5f * s) });
        }
        x += 22f * s;

        using (var font = PhosphorFonts.Mono(10.5f * s))
        using (var ink = new SolidBrush(_hover ? PhosphorTheme.Bone : PhosphorTheme.Cream))
            x += PhosphorPaint.DrawTracked(g, Label, font, ink, x, cy - font.GetHeight(g) / 2f, 1f * s);

        float dot = 6f * s;
        x += 8f * s;
        using (var b = new SolidBrush(Confident ? Accent : PhosphorTheme.Ghost)) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);

        float cx = Width - 16f * s;
        using var caret = new Pen(PhosphorTheme.Dim, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(caret, new[] { new PointF(cx - 4f * s, cy + 2f * s), new PointF(cx, cy - 2f * s), new PointF(cx + 4f * s, cy + 2f * s) });
    }
}

/// <summary>The handoff scroll in one line: an accent edge, a kicker, what will happen, and Review to open the full ticket.</summary>
internal sealed class ScrollSummaryRow : Control
{
    private bool _hover;
    private bool _expanded;

    public ScrollSummaryRow()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Review the handoff scroll";
    }

    public string Kicker { get; set; } = "SCROLL";
    public string Sentence { get; set; } = "";
    public bool Muted { get; set; }
    public Color Accent { get; set; } = PhosphorTheme.Patina;

    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; AccessibleName = value ? "Close the handoff scroll" : "Review the handoff scroll"; Invalidate(); }
    }

    public event EventHandler? Toggled;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnClick(EventArgs e) { base.OnClick(e); Toggled?.Invoke(this, EventArgs.Empty); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = PhosphorPaint.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float r = 10f * s;
        if (_hover) PhosphorPaint.FillRounded(g, rect, r, PhosphorTheme.Surface2);
        PhosphorPaint.StrokeRounded(g, rect, r, _hover ? PhosphorTheme.Line2 : PhosphorTheme.Line);
        if (Focused) PhosphorPaint.StrokeRounded(g, RectangleF.Inflate(rect, -2f, -2f), r - 2f, Color.FromArgb(160, PhosphorTheme.Patina));
        using (var edge = new SolidBrush(Muted ? PhosphorTheme.Line2 : Accent))
            g.FillRectangle(edge, 0.5f, 10f * s, 2f * s, Height - 20f * s);

        float cy = Height / 2f;
        float x = 14f * s;
        using (var mono = PhosphorFonts.Mono(9.5f * s))
        using (var ghost = new SolidBrush(PhosphorTheme.Ghost))
            x += PhosphorPaint.DrawTracked(g, Kicker, mono, ghost, x, cy - mono.GetHeight(g) / 2f, 1.1f * s) + 12f * s;

        string action = Expanded ? "CLOSE" : "REVIEW";
        float actionW;
        using (var mono = PhosphorFonts.Mono(9.5f * s))
        using (var ink = new SolidBrush(_hover ? PhosphorTheme.Bone : PhosphorTheme.Dim))
        {
            actionW = PhosphorPaint.MeasureTracked(g, action, mono, 1.1f * s);
            float ax = Width - 30f * s - actionW;
            PhosphorPaint.DrawTracked(g, action, mono, ink, ax, cy - mono.GetHeight(g) / 2f, 1.1f * s);
            float cx = Width - 18f * s;
            using var caret = new Pen(_hover ? PhosphorTheme.Bone : PhosphorTheme.Dim, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float d = Expanded ? -1f : 1f;
            g.DrawLines(caret, new[] { new PointF(cx - 4f * s, cy - 2f * s * d), new PointF(cx, cy + 2f * s * d), new PointF(cx + 4f * s, cy - 2f * s * d) });
        }

        float tw = Width - 30f * s - actionW - 12f * s - x;
        using var body = PhosphorFonts.Body(12f * s);
        using var text = new SolidBrush(Muted ? PhosphorTheme.Ghost : PhosphorTheme.Bone);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(Sentence, body, text, new RectangleF(x, 0, Math.Max(0, tw), Height), fmt);
    }
}

/// <summary>
/// The composer's text box. Its prompt (Omnitek's suggestion when something is attached) stays visible while the
/// box is empty, focused or not, and in offscreen renders; the stock placeholder hides on focus.
/// </summary>
internal sealed class PromptTextBox : TextBox
{
    private const int WmPaint = 0x000F;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;
    private string _prompt = "";

    public string Prompt
    {
        get => _prompt;
        set { if (_prompt == value) return; _prompt = value; Invalidate(); }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Filter on the message first: TextLength sends WM_GETTEXTLENGTH, which comes back through here.
        if (m.Msg is not (WmPaint or WmPrint or WmPrintClient) || _prompt.Length == 0 || TextLength > 0) return;
        if (m.Msg == WmPaint)
        {
            using var g = Graphics.FromHwnd(Handle);
            DrawPrompt(g);
        }
        else if (m.Msg is WmPrint or WmPrintClient && m.WParam != IntPtr.Zero)
        {
            using var g = Graphics.FromHdc(m.WParam);
            DrawPrompt(g);
        }
    }

    private void DrawPrompt(Graphics g) => TextRenderer.DrawText(g, _prompt, Font, new Rectangle(1, 0, ClientSize.Width - 2, ClientSize.Height),
        PhosphorTheme.Ghost, BackColor, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
}
