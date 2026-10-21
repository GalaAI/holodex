using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// AInbox intake (Alt+I, Actions Ring slot 8, pipe ainbox:summon).
/// Uploader, intent and content composer, Omnitek auto-detect, seven-Pod target, and the
/// Scribe handoff scroll presented as a CP-20 SpecCard ticket. Submitting writes the scroll
/// and pulses the Pod orchestrator.
/// </summary>
public sealed class AInboxIntakeWidgetForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;

    private readonly DataLabel _fileStatusLabel;
    private readonly PhosphorBadge _autoDetectBadge;
    private readonly DataLabel _detectLine;
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
        Size = new Size(700, 964);
        Padding = WidgetChrome.FramePadding();

        DragEnter += (_, e) =>
        {
            if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += OnFormDragDrop;

        _autoDetectBadge = new PhosphorBadge("Scroll", PhosphorBadgeState.Unstated) { Name = "AInbox.AutoDetectBadge" };
        _autoDetectBadge.FitToText();

        // Uploader: two default buttons and the drop hint.
        var uploader = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "AInbox.Uploader" };
        var chooseFileBtn = new PhosphorButton("Choose file") { Dock = DockStyle.Left, Width = 140, Name = "AInbox.ChooseFileButton" };
        chooseFileBtn.Click += (_, _) => ChooseFile();
        var captureBtn = new PhosphorButton("Attach capture") { Dock = DockStyle.Left, Width = 160, Name = "AInbox.CaptureButton" };
        captureBtn.Click += (_, _) =>
        {
            _capturedDataUrl = ArkStationClient.CaptureRegionAroundCursor(_summonPos.X, _summonPos.Y, 640, 420);
            _selectedFilePath = "cursor-capture.png";
            if (_fileStatusLabel != null)
                _fileStatusLabel.Text = "ATTACHED // cursor-capture.png // 640x420";
            RefreshAutoDetectionAndScroll();
        };
        _fileStatusLabel = new DataLabel("DROP // any file, script, dataset or capture") { Dock = DockStyle.Fill, Height = 44, Name = "AInbox.FileStatus" };
        uploader.Controls.Add(_fileStatusLabel);
        uploader.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 12, BackColor = Color.Transparent });
        uploader.Controls.Add(captureBtn);
        uploader.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        uploader.Controls.Add(chooseFileBtn);

        _detectLine = new DataLabel("OMNITEK // detecting") { Dock = DockStyle.Top, Height = 20, Name = "AInbox.DetectLine" };

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
        _scrollContextField = new PhosphorField("", true, 52, mono: true) { Name = "AInbox.ScrollContextField" };
        _scrollContextField.Box.Name = "AInbox.ScrollContextBox";
        _scrollGoalField = new PhosphorField("", false, 36, mono: true) { Name = "AInbox.ScrollGoalField" };
        _scrollGoalField.Box.Name = "AInbox.ScrollGoalBox";
        _evidenceReceivedField = new PhosphorField("", false, 36, mono: true) { Name = "AInbox.ProofOfReceivedField" };
        _evidenceReceivedField.Box.Name = "AInbox.ProofOfReceivedBox";
        _evidenceWorkingField = new PhosphorField("", false, 36, mono: true) { Name = "AInbox.WorkingStateField" };
        _evidenceWorkingField.Box.Name = "AInbox.WorkingStateBox";
        _evidenceDeliverableField = new PhosphorField("", false, 36, mono: true) { Name = "AInbox.DeliverableEvidenceField" };
        _evidenceDeliverableField.Box.Name = "AInbox.DeliverableEvidenceBox";

        var rowContext = WidgetChrome.Labeled("1 // Context", _scrollContextField, 52);
        ((Control)rowContext.Tag!).Name = "AInbox.ScrollContextLabel";
        var rowGoal = WidgetChrome.Labeled("2 // Goal", _scrollGoalField, 36);
        ((Control)rowGoal.Tag!).Name = "AInbox.ScrollGoalLabel";
        var rowProof = WidgetChrome.Labeled("3A // Proof of received", _evidenceReceivedField, 36);
        ((Control)rowProof.Tag!).Name = "AInbox.ProofOfReceivedLabel";
        var rowWorking = WidgetChrome.Labeled("3B // Working state", _evidenceWorkingField, 36);
        ((Control)rowWorking.Tag!).Name = "AInbox.WorkingStateLabel";
        var rowDeliverable = WidgetChrome.Labeled("3C // Deliverable evidence", _evidenceDeliverableField, 36);
        ((Control)rowDeliverable.Tag!).Name = "AInbox.DeliverableEvidenceLabel";

        _scroll.Controls.Add(rowDeliverable);
        _scroll.Controls.Add(WidgetChrome.Spacer(10));
        _scroll.Controls.Add(rowWorking);
        _scroll.Controls.Add(WidgetChrome.Spacer(10));
        _scroll.Controls.Add(rowProof);
        _scroll.Controls.Add(WidgetChrome.Spacer(10));
        _scroll.Controls.Add(rowGoal);
        _scroll.Controls.Add(WidgetChrome.Spacer(10));
        _scroll.Controls.Add(rowContext);

        // Footer: receipt line and the one primary.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Name = "AInbox.Footer" };
        _receiptStatusLabel = new DataLabel("READY // submit writes the scroll and pulses the Pod") { Dock = DockStyle.Fill, Height = 44, Name = "AInbox.ReceiptStatus" };
        _submitBtn = new PhosphorButton("Submit intake", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "AInbox.SubmitButton" };
        _submitBtn.Click += async (_, _) => await SubmitIntakeAsync();
        footer.Controls.Add(_receiptStatusLabel);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12, BackColor = Color.Transparent });
        footer.Controls.Add(_submitBtn);

        // Equal 14px gaps above and below the scroll; the lower one docks Bottom so it sits between the card and the footer.
        Controls.Add(_scroll);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 14, BackColor = Color.Transparent });
        Controls.Add(footer);
        Controls.Add(WidgetChrome.Spacer(14));
        Controls.Add(composer);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_podOrchestratorLabel);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(podRow);
        Controls.Add(WidgetChrome.Spacer(10));
        Controls.Add(_detectLine);
        Controls.Add(WidgetChrome.Spacer(6));
        Controls.Add(uploader);
        Controls.Add(WidgetChrome.Spacer(8));
        WidgetChrome.Attach(this, "AINBOX // POD INTAKE", "AInbox intake", PhosphorTheme.Seal, _autoDetectBadge);
        WidgetDrag.Enable(this);

        RefreshAutoDetectionAndScroll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SummonAt(Point cursorPos, string? preCapturedDataUrl = null)
    {
        _summonPos = cursorPos;
        if (!string.IsNullOrEmpty(preCapturedDataUrl))
        {
            _capturedDataUrl = preCapturedDataUrl;
            _selectedFilePath = "lasso-capture.png";
            _fileStatusLabel.Text = "ATTACHED // lasso-capture.png // region";
        }

        RefreshAutoDetectionAndScroll();

        var screen = Screen.FromPoint(cursorPos).WorkingArea;
        Location = WidgetChrome.PlaceNear(this, screen, cursorPos.X + 20, cursorPos.Y - Height / 3);

        Show();
        Activate();
        _intentField.Box.Focus();
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
            _selectedFilePath = dlg.FileName;
            _fileStatusLabel.Text = $"ATTACHED // {Path.GetFileName(dlg.FileName)}";
            _userChosePodManually = false;
            RefreshAutoDetectionAndScroll();
        }
    }

    private void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            _selectedFilePath = files[0];
            _fileStatusLabel.Text = $"DROPPED // {Path.GetFileName(files[0])}";
            _userChosePodManually = false;
            RefreshAutoDetectionAndScroll();
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) &&
                 e.Data.GetData(DataFormats.Text) is string text)
        {
            _contentField.Box.Text = text;
            RefreshAutoDetectionAndScroll();
        }
    }

    private ArkPodInboxInfo GetSelectedPod()
    {
        int idx = Math.Clamp(_podDropdown.SelectedIndex, 0, ArkStationClient.PodInboxes.Count - 1);
        return ArkStationClient.PodInboxes[idx];
    }

    private void RefreshAutoDetectionAndScroll()
    {
        var detected = _ark.AutoDetectOmnitekItem(_selectedFilePath, _contentField.Box.Text, _intentField.Box.Text);
        string badge = detected.DisplayBadge;
        if (badge.Length > 16) badge = badge[..16];
        _autoDetectBadge.Text = badge;
        _autoDetectBadge.State = string.Equals(detected.Confidence, "high", StringComparison.OrdinalIgnoreCase)
            ? PhosphorBadgeState.Validated
            : PhosphorBadgeState.Unstated;
        _detectLine.Text = $"OMNITEK // {detected.DisplayBadge.ToUpperInvariant()} // {detected.Confidence.ToUpperInvariant()} confidence";

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

            _receiptStatusLabel.ForeColor = PhosphorTheme.Moss;
            _receiptStatusLabel.Text = $"PULSED // {receipt.OrchestratorAgentId} @ {receipt.PodInboxChannel}";
            _scroll.Status = "Written";
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
