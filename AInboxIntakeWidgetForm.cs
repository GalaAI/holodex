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
    private readonly DisclosureRow _summary;
    private readonly Panel _scrollGap;
    private readonly SpecCardPanel _scroll;
    private readonly PhosphorField _scrollContextField;
    private readonly PhosphorField _scrollGoalField;
    private readonly PhosphorField _evidenceReceivedField;
    private readonly PhosphorField _evidenceWorkingField;
    private readonly PhosphorField _evidenceDeliverableField;
    private readonly SuggestionChip _route;
    private readonly DataLabel _statusLabel;
    private readonly PhosphorButton _sendBtn;

    private string? _selectedFilePath;
    private string? _capturedDataUrl;
    private CaptureKind _captureKind = CaptureKind.File;
    private string? _content;
    private string? _attachmentSha;
    private System.Threading.Tasks.Task<string?>? _hashTask;
    private OmnitekAutoDetectInfo? _detected;
    private CaptureKind _contentKind = CaptureKind.Snippet;
    private string _suggestion = "";
    private int _podIndex = 0;
    private Point _summonPos;
    private bool _userChosePodManually;
    private bool _scrollOpen;
    private bool _working;
    private System.Windows.Forms.Timer? _undoTimer;
    private int _undoLeft;
    private bool _evidenceEdited;
    private bool _settingScroll;
    private long _lastDragMs;
    private readonly System.Windows.Forms.Timer _dragLeaveTimer = new() { Interval = 150 };

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

        _intentBox = new PromptTextBox { Dock = DockStyle.Top, Height = 64, Name = "AInbox.IntentField" };
        _intentBox.TextChanged += (_, _) => RefreshAutoDetectionAndScroll();

        // Capture bar: the live kinds as icons; the right side offers Omnitek's suggested intent.
        _captureBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "AInbox.CaptureBar" };
        _suggestionHint = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "AInbox.SuggestionHint" };
        _captureBar.Controls.Add(_suggestionHint);
        _captureBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10, BackColor = Color.Transparent });
        foreach (var info in CaptureKinds.All.Where(k => !k.Planned).Reverse())
        {
            var tool = new IconToolButton("AInbox.Kind." + CaptureKinds.Name(info.Kind), CaptureKinds.Glyph(info.Kind), CaptureKinds.Name(info.Kind)) { Dock = DockStyle.Left };
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
        _summary = new DisclosureRow { Dock = DockStyle.Top, Height = 44, Name = "AInbox.ScrollSummary" };
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
        // Context and Goal are drafted from the intent and are what the scroll states, so they are read-only here;
        // the three evidence lines are the operator's to edit, and an edit sticks and re-seals.
        _scrollContextField.Box.ReadOnly = true;
        _scrollGoalField.Box.ReadOnly = true;
        foreach (var field in new[] { _evidenceReceivedField, _evidenceWorkingField, _evidenceDeliverableField })
            field.Box.TextChanged += (_, _) =>
            {
                if (!_settingScroll) _evidenceEdited = true;
                UpdateSeal();
            };

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
        _route = new SuggestionChip { Dock = DockStyle.Left, Name = "AInbox.RouteChip" };
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

        // A drop anywhere on the widget lands in the composer, and the well shows it while it is over. A leave settles
        // after a beat: moving onto another of the widget's targets keeps the well, a cancel (Esc) or real leave resets it.
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            if (_composer.Mode == CaptureWellMode.DragOver && Environment.TickCount64 - _lastDragMs > 120) _composer.SetMode(_composer.RestMode);
        };
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
        if (keyData == Keys.Escape)
        {
            if (_undoTimer != null) CancelPendingSend();
            else Hide();
            return true;
        }
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
        if (_composer.Mode == CaptureWellMode.DragOver) _composer.SetMode(_composer.RestMode);
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

        FitToContent.Apply(this, c => c == _scroll || c == _scrollGap ? _scrollOpen : true);
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
        HashAttachment();
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
        _attachmentSha = null;
        _hashTask = null;
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
        _evidenceEdited = false;
        _selectedFilePath = null;
        _capturedDataUrl = null;
        _attachmentSha = null;
        _hashTask = null;
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

    /// <summary>
    /// The attachment's SHA-256 for the seal: the captured image's data URL when there is one, else the file's
    /// bytes, streamed off the UI thread. A path with nothing readable behind it (the as-built sample) has none.
    /// </summary>
    private void HashAttachment()
    {
        _attachmentSha = null;
        _hashTask = null;
        if (!string.IsNullOrEmpty(_capturedDataUrl))
        {
            // Hash the image bytes, not the data URL text, so a receiver hashing the PNG gets the same value.
            int comma = _capturedDataUrl.IndexOf(',');
            try { _attachmentSha = ArkStationClient.Sha256Hex(Convert.FromBase64String(_capturedDataUrl[(comma + 1)..])); }
            catch (FormatException) { _attachmentSha = ArkStationClient.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(_capturedDataUrl)); }
            return;
        }
        string? path = _selectedFilePath;
        if (path == null || !File.Exists(path)) return;
        var task = System.Threading.Tasks.Task.Run(() => ArkStationClient.Sha256File(path));
        _hashTask = task;
        task.ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (_hashTask != task) return;
                _attachmentSha = t.Result;
                _hashTask = null;
                UpdateSeal();
            }));
        }, System.Threading.Tasks.TaskScheduler.Default);
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

    private void ShowRouteMenu() => _route.ShowChoices(ArkStationClient.PodInboxes.Select((pod, i) => new ChipChoice(
        $"{pod.Label}  ·  {ShortName(pod.OrchestratorLabel)}",
        pod.InboxChannel,
        i == _podIndex,
        () =>
        {
            _podIndex = i;
            _userChosePodManually = true;
            RefreshAutoDetectionAndScroll();
        },
        pod.Description)));

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

        _route.Label = pod.Label;
        _route.Detail = orchestrator;
        _route.Accent = podAccent;
        _route.Confident = high && !_userChosePodManually;
        _route.Fit();
        if (_undoTimer == null && !_working) _sendBtn.Text = $"Send to {pod.Label}";

        _detected = detected;
        var previewScroll = _ark.BuildPreviewHandoffScroll(pod.PodId, detected, intent, _selectedFilePath, _content);

        string what = intent.Trim().Length > 0 ? intent.Trim().Split('\n')[0]
            : _suggestion.Length > 0 ? _suggestion
            : "Nothing to send yet";
        _summary.Kicker = HasAnything && !HasCapture && !HasContent ? $"SCROLL // {item}" : "SCROLL";
        _summary.Sentence = HasAnything ? $"{what}. Receipt from {orchestrator}." : what;
        _summary.Muted = !HasAnything;
        _summary.Accent = podAccent;
        _summary.Invalidate();

        _settingScroll = true;
        _scrollContextField.Box.Text = previewScroll.Context;
        _scrollGoalField.Box.Text = previewScroll.Goal;
        if (!_evidenceEdited)
        {
            _evidenceReceivedField.Box.Text = previewScroll.DesiredEvidence.ProofOfReceived;
            _evidenceWorkingField.Box.Text = previewScroll.DesiredEvidence.WorkingState;
            _evidenceDeliverableField.Box.Text = previewScroll.DesiredEvidence.DeliverableEvidence;
        }
        _settingScroll = false;

        _scroll.Code = pod.PodId.Length <= 5 ? pod.PodId.ToUpperInvariant() : pod.PodId[..5].ToUpperInvariant();
        _scroll.Title = "Handoff Scroll";
        _scroll.Eyebrow = "Scroll // Handoff // Drafted by Scribe";
        _scroll.Alt = $"{pod.InboxChannel} // pulse {pod.OrchestratorAgentId}";
        _scroll.Board = "";
        _scroll.Edge = podAccent;
        _scroll.Specs = new[]
        {
            ("To", orchestrator),
            ("Item", detected.DisplayBadge),
            ("Confidence", detected.Confidence)
        };
        _scroll.Status = "Draft";
        _scroll.StatusState = PhosphorBadgeState.Unstated;
        UpdateSeal();
    }

    /// <summary>
    /// Seals the scroll as it would be sent (the evidence as edited, the attachment's hash) so the barcode and the
    /// seal line always match the receipt. The client owns the canonical form; this only asks it.
    /// </summary>
    private void UpdateSeal()
    {
        if (_detected == null) return;
        if (_hashTask != null)
        {
            _scroll.Seal = "";
            _scroll.Receipt = "SEAL // hashing the attachment";
            _scroll.Invalidate();
            return;
        }
        string seal = _ark.BuildPreviewHandoffScroll(SelectedPod.PodId, _detected, _intentBox.Text, _selectedFilePath, _content,
            _evidenceReceivedField.Box.Text, _evidenceWorkingField.Box.Text, _evidenceDeliverableField.Box.Text, _attachmentSha).Sha256;
        _scroll.Seal = seal;
        _scroll.Receipt = $"SEAL // SHA-256 // {ShortSeal(seal)} // CLICK BARS TO COPY";
        _scroll.Invalidate();
    }

    private static string ShortSeal(string sha) => sha.Length < 20 ? sha : $"{sha[..12]}...{sha[^8..]}";

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
            // A large file may still be hashing; the seal waits for it so the receipt matches the bytes sent.
            if (_hashTask != null) { _attachmentSha = await _hashTask; _hashTask = null; }
            var receipt = await _ark.SubmitPodInboxIntakeAsync(
                pod.PodId,
                _intentBox.Text,
                _selectedFilePath,
                _content,
                _capturedDataUrl,
                _evidenceReceivedField.Box.Text,
                _evidenceWorkingField.Box.Text,
                _evidenceDeliverableField.Box.Text,
                _attachmentSha);

            bool pulsed = receipt.PulseStatus == "pulsed";
            if (!Visible)
                PhosphorToast.Show(pulsed ? $"Sent to {receipt.PodLabel}" : "Handoff saved locally",
                    $"sha256:{receipt.HandoffScroll.Sha256[..12]}", "AINBOX // HANDOFF", 3200, ToastTone.Success);
            ResetComposer();
            SetStatus(pulsed
                ? $"SENT // {receipt.OrchestratorAgentId} @ {receipt.PodInboxChannel} // sha256:{receipt.HandoffScroll.Sha256[..12]}"
                : $"SAVED LOCALLY // ArkStation not running // sha256:{receipt.HandoffScroll.Sha256[..12]}", PhosphorTheme.Moss);
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
            _sendBtn.Text = $"Send to {SelectedPod.Label}";
            Invalidate();
        }
    }

    /// <summary>Closing the widget while a send is pending sends it now; the result comes back as a toast.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible || _undoTimer == null) return;
        StopUndoTimer();
        _ = SubmitIntakeAsync();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working || _undoTimer != null ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
