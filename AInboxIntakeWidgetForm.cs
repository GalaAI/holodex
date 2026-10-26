using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// AInbox intake (Alt+I, Actions Ring slot 8, pipe ainbox:summon).
/// The CP-27 capture well and its eight kind chips, intent and content composer, Omnitek auto-detect,
/// seven-Pod target, and the Scribe handoff scroll presented as a CP-20 SpecCard ticket. Submitting
/// writes the scroll and pulses the Pod orchestrator.
/// </summary>
public sealed class AInboxIntakeWidgetForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly CaptureWellControl _well;
    private readonly Panel _kindRow;
    private readonly List<KindChip> _chips = new();
    private readonly CapturedRow _capturedRow;
    private readonly PhosphorBadge _autoDetectBadge;
    private readonly ComboBox _podDropdown;
    private readonly DataLabel _podOrchestratorLabel;
    private readonly PhosphorField _intentField;
    private readonly PhosphorField _contentField;
    private readonly SpecCardPanel _scroll;
    private readonly PhosphorField _scrollContextField;
    private readonly PhosphorField _scrollGoalField;
    private readonly PhosphorField _evidenceReceivedField;
    private readonly PhosphorField _evidenceWorkingField;
    private readonly PhosphorField _evidenceDeliverableField;
    private readonly DataLabel _receiptStatusLabel;
    private readonly PhosphorButton _submitBtn;

    private string? _selectedFilePath;
    private string? _capturedDataUrl;
    private Point _summonPos;
    private bool _userChosePodManually;
    private bool _working;

    /// <summary>Opens the snip tools (Pick, Lasso) for the well; their Send to AInbox comes back through SummonAt.</summary>
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
        AllowDrop = true;
        DoubleBuffered = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "AInbox";
        Size = new Size(700, 1030);
        Padding = WidgetChrome.FramePadding();

        // A drop anywhere on the widget lands like a drop on the well, and the well shows it while it is over.
        DragEnter += (_, e) =>
        {
            if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap)))
            {
                e.Effect = DragDropEffects.Copy;
                _well!.SetMode(CaptureWellMode.DragOver, CaptureKinds.FromDrag(e.Data));
            }
        };
        DragLeave += (_, _) => _well!.SetMode(_well.RestMode);
        DragDrop += OnFormDragDrop;

        _autoDetectBadge = new PhosphorBadge("Scroll", PhosphorBadgeState.Unstated) { Name = "AInbox.AutoDetectBadge" };
        _autoDetectBadge.FitToText();

        // CP-27 capture well: replaces the old Choose file / Attach capture row.
        _well = new CaptureWellControl { Dock = DockStyle.Top, Height = 88, Name = "AInbox.Well" };
        _well.Activated += (_, _) => ChooseFile();
        _well.Dropped += (_, e) => OnFormDragDrop(this, e);

        // Eight kind chips while nothing is captured; the captured row takes their place after.
        _kindRow = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Transparent, Name = "AInbox.KindRow" };
        _capturedRow = new CapturedRow { Dock = DockStyle.Fill, Visible = false };
        _capturedRow.RemoveClicked += (_, _) => ClearCapture();
        _kindRow.Controls.Add(_capturedRow);
        foreach (var info in CaptureKinds.All)
        {
            var chip = new KindChip(info);
            chip.Click += (_, _) => OnKindChip(info);
            _chips.Add(chip);
            _kindRow.Controls.Add(chip);
        }
        _kindRow.Resize += (_, _) => LayoutChips();

        // Target Pod.
        _podDropdown = new PhosphorComboBox { Name = "AInbox.PodSelect" };
        foreach (var pod in ArkStationClient.PodInboxes)
        {
            _podDropdown.Items.Add($"{pod.Label} // {pod.InboxChannel} // pulse {pod.OrchestratorLabel}");
        }
        _podDropdown.SelectedIndex = 4; // 4GE
        _podDropdown.SelectionChangeCommitted += (_, _) =>
        {
            _userChosePodManually = true;
            RefreshAutoDetectionAndScroll();
        };
        // A ComboBox sizes itself from ItemHeight and ignores Height, so the row takes the control's real
        // height (40px, matching the composer fields) instead of clipping its bottom border.
        _podDropdown.ItemHeight = (int)Math.Ceiling(34f * PhosphorPaint.Scale(_podDropdown));
        var podRow = WidgetChrome.Labeled("Target Pod // 7 inboxes", _podDropdown, _podDropdown.Height);
        podRow.Name = "AInbox.PodBar";
        ((Control)podRow.Tag!).Name = "AInbox.PodHeader";
        _podOrchestratorLabel = new DataLabel("PULSE // wiz (4GE) @ ainbox:pod:forge") { Dock = DockStyle.Top, Height = 20, Name = "AInbox.OrchestratorPulse" };

        // Composer.
        _intentField = new PhosphorField("Declare what the Pod should build, analyze or route.", false, 40) { Name = "AInbox.IntentField" };
        _intentField.Box.Name = "AInbox.IntentBox";
        _intentField.Box.TextChanged += (_, _) => RefreshAutoDetectionAndScroll();
        var intentRow = WidgetChrome.Labeled("Intent // Composer", _intentField, 40);
        ((Control)intentRow.Tag!).Name = "AInbox.IntentLabel";

        _contentField = new PhosphorField("Paste a URL, code or notes.", false, 40, mono: true) { Name = "AInbox.ContentField" };
        _contentField.Box.Name = "AInbox.ContentBox";
        _contentField.Box.TextChanged += (_, _) => RefreshAutoDetectionAndScroll();
        var contentRow = WidgetChrome.Labeled("Content // Link // Snippet", _contentField, 40);
        ((Control)contentRow.Tag!).Name = "AInbox.ContentLabel";

        // Intent and Content share one row (two columns) so the Handoff Scroll below gets the height.
        var composer = new Panel { Dock = DockStyle.Top, Height = intentRow.Height, BackColor = Color.Transparent, Name = "AInbox.Composer" };
        intentRow.Dock = DockStyle.Left;
        contentRow.Dock = DockStyle.Fill;
        composer.Resize += (_, _) => intentRow.Width = (composer.Width - 12) / 2;
        composer.Controls.Add(contentRow);
        composer.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent });
        composer.Controls.Add(intentRow);

        // Handoff scroll as a SpecCard ticket (CP-20).
        _scroll = new SpecCardPanel { Dock = DockStyle.Fill, Name = "AInbox.HandoffScroll" };
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

        // Footer: receipt line and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "AInbox.Footer" };
        _receiptStatusLabel = new DataLabel("READY // submit writes the scroll and sends it to the Pod") { Dock = DockStyle.Fill, Height = 44, Name = "AInbox.ReceiptStatus" };
        _submitBtn = new PhosphorButton("Submit intake", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "AInbox.SubmitButton" };
        _submitBtn.Click += async (_, _) => await SubmitIntakeAsync();
        footer.Controls.Add(_receiptStatusLabel);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_submitBtn);

        // Equal 10px gaps above and below the scroll; the lower one docks Bottom so it sits between the card and the footer.
        Controls.Add(_scroll);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Color.Transparent });
        Controls.Add(footer);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(composer);
        Controls.Add(WidgetChrome.Spacer(8));
        Controls.Add(_podOrchestratorLabel);
        Controls.Add(WidgetChrome.Spacer(4));
        Controls.Add(podRow);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_kindRow);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(_well);
        Controls.Add(WidgetChrome.Spacer(6));
        WidgetChrome.Attach(this, "AINBOX // POD INTAKE", "AInbox intake", PhosphorTheme.Seal, _autoDetectBadge);
        WidgetDrag.Enable(this);

        RefreshAutoDetectionAndScroll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens the widget at the cursor. A capture handed over from the snip tools lands in the well.</summary>
    public void SummonAt(Point cursorPos, string? preCapturedDataUrl = null, string? captureName = null)
    {
        _summonPos = cursorPos;
        if (!string.IsNullOrEmpty(preCapturedDataUrl))
        {
            _capturedDataUrl = preCapturedDataUrl;
            string name = captureName ?? "lasso-capture.png";
            bool pick = name.StartsWith("element", StringComparison.OrdinalIgnoreCase);
            SetCaptured(pick ? CaptureKind.Pick : CaptureKind.Lasso, name, pick ? "PICK // element + origin" : "LASSO // region");
        }

        RefreshAutoDetectionAndScroll();

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);

        Show();
        Activate();
        _intentField.Box.Focus();
    }

    /// <summary>For the as-built capture: "drag" (beam mid-orbit, File lifted), "captured" (a sample file), "reset".</summary>
    internal void PreviewState(string state)
    {
        switch (state)
        {
            case "drag":
                _well.SetMode(CaptureWellMode.DragOver, CaptureKind.File);
                _well.FreezeForCapture(0.18f);
                break;
            case "captured":
                _well.Unfreeze();
                SetCaptured(CaptureKind.File, "report.pdf", "FILE // 1.4 MB");
                _well.FreezeForCapture(0f);
                break;
            default:
                _well.Unfreeze();
                ClearCapture();
                break;
        }
        Refresh();
    }

    private void LayoutChips()
    {
        float s = PhosphorPaint.Scale(_kindRow);
        int gap = (int)Math.Round(6 * s);
        int n = _chips.Count;
        int w = (_kindRow.Width - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            _chips[i].SetBounds(i * (w + gap), 0, i == n - 1 ? _kindRow.Width - i * (w + gap) : w, _kindRow.Height);
        }
    }

    private void OnKindChip(CaptureKindInfo info)
    {
        if (info.Planned)
        {
            PhosphorToast.Show($"{CaptureKinds.Name(info.Kind)} is planned", info.Kind == CaptureKind.Record
                ? "Screen recording into AInbox is not built yet."
                : "Ghost Whisper voice notes are not wired to AInbox yet.", "AINBOX // CAPTURE", 2600, ToastTone.Info);
            return;
        }
        switch (info.Kind)
        {
            case CaptureKind.File: ChooseFile(); break;
            case CaptureKind.Screen: AttachScreen(); break;
            case CaptureKind.Pick: OpenSnipTool(WandCaptureTool.ElementPicker); break;
            case CaptureKind.Lasso: OpenSnipTool(WandCaptureTool.FreeformLasso); break;
            case CaptureKind.Snippet: PasteClipboard(link: false); break;
            case CaptureKind.Link: PasteClipboard(link: true); break;
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
        SetCaptured(CaptureKind.Screen, "cursor-capture.png", "SCREEN // 640x420");
    }

    private void PasteClipboard(bool link)
    {
        string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        bool isLink = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (text.Length == 0 || (link && !isLink))
        {
            PhosphorToast.Show(link ? "No link on the clipboard" : "Clipboard is empty",
                link ? "Copy a URL first, then press Link." : "Copy some text or code first, then press Snippet.",
                "AINBOX // CAPTURE", 2400, ToastTone.Warning);
            return;
        }
        _contentField.Box.Text = text;
        _contentField.Box.Focus();
    }

    private void ChooseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select a file or artifact for AInbox intake",
            Filter = "All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _capturedDataUrl = null;
            SetCaptured(CaptureKind.File, dlg.FileName, FileMeta(dlg.FileName));
        }
    }

    private void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null) { _well.SetMode(_well.RestMode); return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            // One capture travels with a handoff today; extra files are named so nothing is dropped silently.
            string meta = FileMeta(files[0]) + (files.Length > 1 ? $" // {files.Length - 1} more not sent" : "");
            _capturedDataUrl = null;
            SetCaptured(CaptureKind.File, files[0], meta);
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is Image img)
        {
            using var ms = new MemoryStream();
            img.Save(ms, ImageFormat.Png);
            _capturedDataUrl = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            SetCaptured(CaptureKind.Screen, "dropped-image.png", $"IMAGE // {img.Width}x{img.Height}");
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) &&
                 e.Data.GetData(DataFormats.Text) is string text)
        {
            _contentField.Box.Text = text;
            _well.SetMode(_well.RestMode);
            RefreshAutoDetectionAndScroll();
        }
        else _well.SetMode(_well.RestMode);
    }

    private void SetCaptured(CaptureKind kind, string path, string meta)
    {
        _selectedFilePath = path;
        _userChosePodManually = false;
        _capturedRow.Kind = kind;
        _capturedRow.ItemName = Path.GetFileName(path);
        _capturedRow.Meta = meta;
        foreach (var chip in _chips) chip.Visible = false;
        _capturedRow.Visible = true;
        _well.RestMode = CaptureWellMode.Captured;
        _well.SetMode(CaptureWellMode.Captured);
        RefreshAutoDetectionAndScroll();
    }

    private void ClearCapture()
    {
        _selectedFilePath = null;
        _capturedDataUrl = null;
        _capturedRow.Visible = false;
        foreach (var chip in _chips) chip.Visible = true;
        _well.RestMode = CaptureWellMode.Idle;
        _well.SetMode(CaptureWellMode.Idle);
        RefreshAutoDetectionAndScroll();
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

    /// <summary>Omnitek's prediction as TYPE // SUBTYPE, for example ARTIFACT // DELIVERABLE.</summary>
    private static string ItemLabel(OmnitekAutoDetectInfo d) => string.IsNullOrEmpty(d.ItemSubtype)
        ? d.ItemType.ToUpperInvariant()
        : $"{d.ItemType.ToUpperInvariant()} // {d.ItemSubtype.Replace('-', ' ').ToUpperInvariant()}";

    private ArkPodInboxInfo GetSelectedPod()
    {
        int idx = Math.Clamp(_podDropdown.SelectedIndex, 0, ArkStationClient.PodInboxes.Count - 1);
        return ArkStationClient.PodInboxes[idx];
    }

    private void RefreshAutoDetectionAndScroll()
    {
        var detected = _ark.AutoDetectOmnitekItem(_selectedFilePath, _contentField.Box.Text, _intentField.Box.Text);
        string badge = detected.DisplayBadge;
        if (badge.Length > 24) badge = badge[..24];
        _autoDetectBadge.Text = badge;
        bool high = string.Equals(detected.Confidence, "high", StringComparison.OrdinalIgnoreCase);
        _autoDetectBadge.State = high ? PhosphorBadgeState.Validated : PhosphorBadgeState.Unstated;

        if (!_userChosePodManually)
        {
            for (int i = 0; i < ArkStationClient.PodInboxes.Count; i++)
            {
                if (ArkStationClient.PodInboxes[i].PodId == detected.RecommendedPodId)
                {
                    _podDropdown.SelectedIndex = i;
                    break;
                }
            }
        }

        var pod = GetSelectedPod();
        Color podAccent = PhosphorTheme.ResolveTokenColor(pod.AccentToken);
        _podOrchestratorLabel.ForeColor = podAccent;
        _podOrchestratorLabel.Text = $"PULSE // {pod.OrchestratorLabel} @ {pod.InboxChannel}";

        // The well's copy and its Omnitek line follow what is captured.
        string item = ItemLabel(detected);
        bool anyInput = _selectedFilePath != null || !string.IsNullOrWhiteSpace(_contentField.Box.Text) || !string.IsNullOrWhiteSpace(_intentField.Box.Text);
        if (_selectedFilePath != null)
        {
            _well.Title = "Captured";
            _well.Body = "Drop or click to replace it, then say what should happen.";
        }
        else
        {
            _well.Title = "Drop anything into AInbox";
            _well.Body = "Files, the screen at the tip, picks, lassos, snippets or links.";
        }
        _well.Line = anyInput
            ? $"OMNITEK // {item} // {detected.Confidence} // {pod.Label}"
            : "OMNITEK // waiting for a capture";
        _well.LineColor = !anyInput ? PhosphorTheme.Ghost : high ? PhosphorTheme.Patina : PhosphorTheme.Dim;
        _well.Invalidate();
        _capturedRow.Prediction = item;
        _capturedRow.Invalidate();

        var previewScroll = _ark.BuildPreviewHandoffScroll(
            pod.PodId,
            detected,
            _intentField.Box.Text,
            _selectedFilePath,
            _contentField.Box.Text);

        _scrollContextField.Box.Text = previewScroll.Context;
        _scrollGoalField.Box.Text = previewScroll.Goal;
        _evidenceReceivedField.Box.Text = previewScroll.DesiredEvidence.ProofOfReceived;
        _evidenceWorkingField.Box.Text = previewScroll.DesiredEvidence.WorkingState;
        _evidenceDeliverableField.Box.Text = previewScroll.DesiredEvidence.DeliverableEvidence;

        _scroll.Code = pod.PodId.Length <= 5 ? pod.PodId.ToUpperInvariant() : pod.PodId[..5].ToUpperInvariant();
        _scroll.Title = "Handoff Scroll";
        _scroll.Eyebrow = "Augment // Scroll // Handoff";
        _scroll.Alt = "Plain alternative // Work order";
        _scroll.Board = "Board // Scribe";
        _scroll.Edge = podAccent;
        _scroll.Specs = new[]
        {
            ("Pod", pod.Label),
            ("Item", detected.DisplayBadge),
            ("Confidence", detected.Confidence),
            ("Drafted by", "Scribe")
        };
        if (!_working && _scroll.StatusState != PhosphorBadgeState.Proven)
        {
            _scroll.Status = "Draft";
            _scroll.StatusState = PhosphorBadgeState.Unstated;
            _scroll.Receipt = "RCPT // pending // written on submit";
        }
        _scroll.Invalidate();
    }

    private async System.Threading.Tasks.Task SubmitIntakeAsync()
    {
        var pod = GetSelectedPod();
        _submitBtn.Enabled = false;
        _working = true;
        _setThinkingState(true);
        _receiptStatusLabel.ForeColor = PhosphorTheme.Patina;
        _receiptStatusLabel.Text = $"DRAFTING // scroll // pulsing {pod.OrchestratorAgentId}";
        _scroll.Status = "Writing";
        _scroll.StatusState = PhosphorBadgeState.Validated;
        _scroll.Invalidate();
        Invalidate();

        try
        {
            var receipt = await _ark.SubmitPodInboxIntakeAsync(
                pod.PodId,
                _intentField.Box.Text,
                _selectedFilePath,
                _contentField.Box.Text,
                _capturedDataUrl,
                _evidenceReceivedField.Box.Text,
                _evidenceWorkingField.Box.Text,
                _evidenceDeliverableField.Box.Text);

            bool pulsed = receipt.PulseStatus == "pulsed";
            _receiptStatusLabel.ForeColor = PhosphorTheme.Moss;
            _receiptStatusLabel.Text = pulsed
                ? $"PULSED // {receipt.OrchestratorAgentId} @ {receipt.PodInboxChannel}"
                : "SAVED // handoff written locally // ArkStation not running";
            _scroll.Status = pulsed ? "Sent" : "Saved";
            _scroll.StatusState = PhosphorBadgeState.Proven;
            _scroll.Receipt = $"RCPT // {receipt.HandoffScroll.ScrollId}";
        }
        catch (Exception ex)
        {
            _receiptStatusLabel.ForeColor = PhosphorTheme.Rust;
            _receiptStatusLabel.Text = $"ERROR // {ex.Message}";
            _scroll.Status = "Blocked";
            _scroll.StatusState = PhosphorBadgeState.Blocked;
        }
        finally
        {
            _working = false;
            _setThinkingState(false);
            _submitBtn.Enabled = true;
            _scroll.Invalidate();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, _working ? Color.FromArgb(200, PhosphorTheme.Patina) : PhosphorTheme.Line2);
    }
}
