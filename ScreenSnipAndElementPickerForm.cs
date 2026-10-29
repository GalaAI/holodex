using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Omniwand;

public enum WandCaptureTool
{
    FreeformLasso,
    RectangleSnip,
    ElementPicker,
    AnnotatePen,
    AnnotateArrow,
    AnnotateBox
}

public sealed record CodexCaptureEntry(
    string EntryId,
    string Kind,
    string Title,
    string Summary,
    string ElementClass,
    string Geometry,
    string HexColor,
    string ModelUsed,
    string? DataUrl,
    string CreatedAt);

/// <summary>
/// Full-desktop Free-Form Lasso, Screen Snipping, Phosphor Annotation Tool, and Desktop Element Picker
/// (implementing the original Ark Wand capabilities: wand.point, wand.annotate, wand.capture, wand.palette, wand.architect)
/// with multi-model Live Vision routing (NVIDIA Nemotron VL, MiniCPM-V, Qwen-VL, or ArkHive/Gateway).
/// </summary>
public sealed class ScreenSnipAndElementPickerForm : Form
{
    private readonly ArkStationClient _ark;
    private readonly Action<bool> _setThinkingState;
    private readonly Action<CodexCaptureEntry>? _onCodexSaved;

    private Bitmap? _desktopSnapshot;
    /// <summary>The frozen desktop with the dim already applied, so a repaint is two blits, not a full-screen blend.</summary>
    private Bitmap? _dimmedSnapshot;
    /// <summary>The monitor the overlay covers (screen pixels).</summary>
    private Rectangle _overlayScreen = Rectangle.Empty;
    private WandCaptureTool _activeTool = WandCaptureTool.FreeformLasso;
    private Color _activeInk = PhosphorTheme.Coral;

    private readonly List<Point> _lassoPoints = new();
    private Rectangle _selectionRect = Rectangle.Empty;
    private bool _isDragging;
    private Point _dragStart;
    // The selection before the current drag, restored when the drag is a stray click or is cancelled with Esc.
    private Rectangle _selectionBeforeDrag;
    private List<Point> _lassoBeforeDrag = new();
    private ElementPick? _pickBeforeDrag;

    /// <summary>A picked UI element, frozen at the click: what Ask, Send to AInbox and Save to Codex act on.</summary>
    private sealed record ElementPick(Rectangle Rect, string Title, string Class, string Process, Color Pixel, string Hex);
    private ElementPick? _pick;
    private IntPtr _hoveredHwnd;

    // Each summon is a session; an answer that comes back after the overlay was closed or re-summoned is dropped.
    private int _session;
    private int _asksInFlight;

    private readonly List<(WandCaptureTool Tool, Color Color, List<Point> Points)> _annotations = new();
    private List<Point>? _currentStroke;

    // Element picker state (wand.point + wand.palette + wand.architect)
    private Point _hoverPos;
    private Rectangle _hoveredElementRect = Rectangle.Empty;
    private string _hoveredTitle = "Desktop Surface";
    private string _hoveredClass = "Win32.Desktop";
    private string _hoveredProcess = "explorer";
    private Color _hoveredPixelColor = PhosphorTheme.Patina;
    private string _hoveredHex = "#79C4B7";

    // Floating dock controls
    private readonly Panel _topToolbar;
    private readonly Panel _actionDrawer;
    private readonly ModelChip _visionModelChip;
    private readonly PromptTextBox _visionPromptBox;
    private readonly ThreadView _visionOutputBox;
    private readonly Panel _outputGap;
    private readonly ComposerSurface _composer;
    private readonly LedRow _telemetryLabel;
    private readonly PhosphorButton _analyzeBtn;
    private readonly IconToolButton _inkBtn;
    private bool _hasAnswer;
    // The answer (or failure) Codex keeps with the capture; the asking card and notices are not answers.
    private string _answerText = "";

    /// <summary>The output area's empty state: what the overlay does, visible until there is an answer.</summary>
    private const string GuidanceText =
        "Draw a free-form lasso or rectangle snip, add Phosphor annotations, or switch to Point at UI (Alt+E) to inspect any desktop window's HWND, geometry, and live pixel palette.";
    private string _visionModel;

    private const int DrawerLogicalWidth = 640;

    /// <summary>For the as-built render: draw the lasso and the element card together, as the board shows them.</summary>
    internal bool PreviewAllMarks { get; set; }

    // Only models that read the image: a text-only model (Nemotron Nano) would answer about a picture it never saw.
    public static readonly string[] VisionModels =
    {
        "nvidia/Nemotron-3.5-Super-VL",
        "openbmb/MiniCPM-V-4_5",
        "google/gemma-3-27b-it"
    };

    /// <summary>The picker's list: the Token Factory vision models, plus the Ark gateway's on an owner's Ark.</summary>
    private static string[] VisionModelsFor(ArkStationClient ark) =>
        ark.ArkInstalled ? VisionModels.Append("ark/kimi").ToArray() : VisionModels;

    public ScreenSnipAndElementPickerForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<CodexCaptureEntry>? onCodexSaved = null)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;
        _onCodexSaved = onCodexSaved;

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
        KeyPreview = true;
        BackColor = PhosphorTheme.Ground;
        ForeColor = PhosphorTheme.Bone;
        Name = "SnipAnnotator";
        Size = new Size(760, 640);
        Cursor = Cursors.Cross;

        // Top floating toolbar: the six tools as icons, the ink swatch, close.
        _topToolbar = new Panel
        {
            Size = new Size(14 + 6 * 44 + 8 + 44 + 14 + 44 + 10, 56),
            Location = new Point(20, 16),
            BackColor = PhosphorTheme.Surface,
            Name = "Snip.TopToolbar"
        };
        _topToolbar.Paint += (_, e) =>
        {
            PhosphorPaint.DrawPanel(
                e.Graphics,
                new Rectangle(0, 0, _topToolbar.Width, _topToolbar.Height),
                PhosphorTheme.RadiusWidget,
                PhosphorTheme.Surface,
                PhosphorTheme.Line2,
                PhosphorTheme.Patina,
                PhosphorPaint.Scale(_topToolbar));
        };

        int bx = 14;
        void AddToolBtn(string name, PhosphorGlyph glyph, string label, WandCaptureTool tool)
        {
            var btn = new IconToolButton(name, glyph, label) { Location = new Point(bx, 6), Tag = tool };
            btn.Click += (_, _) => SelectTool(tool);
            _topToolbar.Controls.Add(btn);
            bx += 44;
        }

        AddToolBtn("Snip.Tool.Lasso", PhosphorGlyph.Lasso, "Lasso", WandCaptureTool.FreeformLasso);
        AddToolBtn("Snip.Tool.Rect", PhosphorGlyph.Rect, "Rectangle", WandCaptureTool.RectangleSnip);
        AddToolBtn("Snip.Tool.Point", PhosphorGlyph.Pick, "Point at UI", WandCaptureTool.ElementPicker);
        AddToolBtn("Snip.Tool.Draw", PhosphorGlyph.Pen, "Draw", WandCaptureTool.AnnotatePen);
        AddToolBtn("Snip.Tool.Arrow", PhosphorGlyph.Arrow, "Arrow", WandCaptureTool.AnnotateArrow);
        AddToolBtn("Snip.Tool.Box", PhosphorGlyph.Box, "Box", WandCaptureTool.AnnotateBox);
        bx += 8;

        // Ink: a swatch of the current ink; each press cycles coral, patina, seal. Its name and tip say the color.
        _inkBtn = new IconToolButton("Snip.InkButton.Coral", (g, r, _, _) =>
        {
            using var b = new SolidBrush(_activeInk);
            float d = r.Width * 0.8f;
            g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }, "Ink color") { Location = new Point(bx, 6) };
        _inkBtn.Click += (_, _) =>
        {
            _activeInk = _activeInk == PhosphorTheme.Coral ? PhosphorTheme.Patina : _activeInk == PhosphorTheme.Patina ? PhosphorTheme.Seal : PhosphorTheme.Coral;
            SyncInkName();
            _inkBtn.Invalidate();
        };
        SyncInkName();
        _topToolbar.Controls.Add(_inkBtn);

        var closeBtn = new PhosphorButton("×")
        {
            Glyph = true,
            Location = new Point(_topToolbar.Width - 50, 10),
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        closeBtn.Click += (_, _) => CloseOverlay();
        _topToolbar.Controls.Add(closeBtn);

        // Bottom drawer: the output (guidance at rest, the answer after), the question, telemetry, model plus Ask.
        _actionDrawer = new Panel
        {
            Size = new Size(DrawerLogicalWidth, 200),
            Location = new Point(20, 304),
            BackColor = PhosphorTheme.Surface,
            Padding = new Padding(16, 12, 16, 14),
            Name = "Snip.ActionDrawer"
        };
        _actionDrawer.Paint += (_, e) =>
        {
            PhosphorPaint.DrawPanel(
                e.Graphics,
                new Rectangle(0, 0, _actionDrawer.Width, _actionDrawer.Height),
                PhosphorTheme.RadiusWidget,
                PhosphorTheme.Surface,
                PhosphorTheme.Line2,
                PhosphorTheme.Coral,
                PhosphorPaint.Scale(_actionDrawer));
        };

        // The output is one card in the AInbox card language: the guidance at rest, then the asking card, then the
        // answer kicked with the model and route that answered (or a failure card).
        _visionOutputBox = new ThreadView("Snip.VisionOutputBox") { Dock = DockStyle.Top, Height = 60, ReserveScrollbar = false };
        _visionOutputBox.ContentChanged += (_, _) => FitDrawer();
        _outputGap = WidgetChrome.Spacer(8);

        _composer = new ComposerSurface { Dock = DockStyle.Top, Name = "Snip.Composer" };
        _visionPromptBox = new PromptTextBox { Dock = DockStyle.Top, Height = 40, Name = "Snip.VisionPromptField" };
        _visionPromptBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                await RunLiveVisionAnalysisAsync();
            }
        };
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Snip.ComposerBar" };
        var toAInboxBtn = new IconToolButton("Snip.ToAInboxButton", PhosphorGlyph.AInbox, "Send to AInbox") { Dock = DockStyle.Left };
        toAInboxBtn.Click += (_, _) => SendCurrentCaptureToAInbox();
        var saveCodexBtn = new IconToolButton("Snip.SaveCodexButton", PhosphorGlyph.Codex, "Save to Codex") { Dock = DockStyle.Left };
        saveCodexBtn.Click += (_, _) => SaveCurrentCaptureToCodex();
        bar.Controls.Add(saveCodexBtn);
        bar.Controls.Add(toAInboxBtn);
        _composer.Content.Controls.Add(bar);
        _composer.Content.Controls.Add(_visionPromptBox);
        _composer.Height = _composer.HeightFor(_visionPromptBox.Height + bar.Height);

        var footer = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Snip.Footer" };
        _visionModel = VisionModels[0];
        _visionModelChip = new ModelChip(_ark, VisionModelsFor(_ark), () => _visionModel, m => _visionModel = m) { Dock = DockStyle.Left, Name = "Snip.VisionModelSelect" };
        _visionModelChip.ModelChanged += (_, _) => SyncAskLabel();
        _analyzeBtn = new PhosphorButton("Ask", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Snip.AnalyzeButton" };
        _analyzeBtn.Click += async (_, _) => await RunLiveVisionAnalysisAsync();
        footer.Controls.Add(_visionModelChip);
        footer.Controls.Add(_analyzeBtn);

        // Full-width telemetry as LEDs: the tool or what the wand holds, its class, palette (the LED is the color
        // itself), size, position and process. It wraps rather than dropping a fact.
        _telemetryLabel = new LedRow { Dock = DockStyle.Top, Height = 20, Wrap = true, Name = "Snip.Telemetry" };
        _telemetryLabel.Set((PhosphorTheme.Patina, "Wand // lasso + annotate + element picker"), (PhosphorTheme.Ghost, "Select a region or hover UI"));
        _telemetryLabel.LedsChanged += (_, _) => FitTelemetry();

        _actionDrawer.Controls.Add(footer);
        _actionDrawer.Controls.Add(WidgetChrome.Spacer(8));
        _actionDrawer.Controls.Add(_telemetryLabel);
        _actionDrawer.Controls.Add(WidgetChrome.Spacer(6));
        _actionDrawer.Controls.Add(_composer);
        _actionDrawer.Controls.Add(_outputGap);
        _actionDrawer.Controls.Add(_visionOutputBox);

        Controls.Add(_topToolbar);
        Controls.Add(_actionDrawer);
        SyncAskLabel();
        SelectTool(_activeTool);
        ShowGuidance();

        // Seed preview state for SurfaceCapture offscreen rendering
        SeedPreviewDemoState();
        ResumeLayout(false);
    }

    private void SeedPreviewDemoState()
    {
        _selectionRect = new Rectangle(64, 82, 360, 198);
        _lassoPoints.Clear();
        _lassoPoints.AddRange(new[]
        {
            new Point(74, 96), new Point(220, 84), new Point(398, 104),
            new Point(416, 210), new Point(340, 268), new Point(110, 262), new Point(68, 180)
        });
        _hoveredElementRect = new Rectangle(450, 92, 270, 184);
        _hoveredTitle = "ArkStation // Holodex Drawer";
        _hoveredClass = "Chrome_WidgetWin_1";
        _hoveredProcess = "ArkStation";
        _hoveredHex = "#79C4B7";
        _hoveredPixelColor = PhosphorTheme.Patina;
    }

    private void SelectTool(WandCaptureTool tool)
    {
        _activeTool = tool;
        foreach (var ctrl in _topToolbar.Controls.OfType<IconToolButton>())
            if (ctrl.Tag is WandCaptureTool t) ctrl.Selected = t == tool;
        // Annotate tools light their LED in the current ink.
        _telemetryLabel.Set(tool switch
        {
            WandCaptureTool.FreeformLasso => new[] { (PhosphorTheme.Patina, "Wand // free-form lasso"), (PhosphorTheme.Ghost, "Drag to lasso any screen shape") },
            WandCaptureTool.RectangleSnip => new[] { (PhosphorTheme.Patina, "Wand // rectangle snip"), (PhosphorTheme.Ghost, "Drag to frame the capture") },
            WandCaptureTool.ElementPicker => _pick == null
                ? new[] { (PhosphorTheme.Patina, "Wand // point + palette + architect"), (PhosphorTheme.Ghost, "Hover, then click to pick") }
                : PickedTelemetry(_pick),
            WandCaptureTool.AnnotatePen   => new[] { (_activeInk, "Wand // annotate pen"), (PhosphorTheme.Ghost, "Draw freehand callouts"), (PhosphorTheme.Ghost, "Ctrl+Z undo") },
            WandCaptureTool.AnnotateArrow => new[] { (_activeInk, "Wand // annotate arrow"), (PhosphorTheme.Ghost, "Drag to point at a target"), (PhosphorTheme.Ghost, "Ctrl+Z undo") },
            WandCaptureTool.AnnotateBox   => new[] { (_activeInk, "Wand // annotate box"), (PhosphorTheme.Ghost, "Drag to highlight a target"), (PhosphorTheme.Ghost, "Ctrl+Z undo") },
            _ => new[] { (PhosphorTheme.Moss, "Wand ready") }
        });
        _visionPromptBox.Prompt = tool == WandCaptureTool.ElementPicker
            ? "Ask about the element. Empty asks for Phosphor improvements."
            : "Ask about the capture. Empty asks for a description and next steps.";
        Invalidate();
    }

    /// <summary>Probes the gateway first so gateway-only models are offered exactly when it is up.</summary>
    private async System.Threading.Tasks.Task RefreshVisionModelsAsync()
    {
        await _ark.CheckStatusAsync();
        await _visionModelChip.RefreshAsync();
    }

    private void SyncAskLabel()
    {
        string first = _visionModelChip.Label.Split(' ')[0];
        _analyzeBtn.Text = first.Length > 0 ? $"Ask {first}" : "Ask";
    }

    /// <summary>
    /// The drawer is as tall as what it holds. The output area is always there: a short guidance block at rest,
    /// the full answer height once there is one.
    /// </summary>
    private void FitDrawer()
    {
        // Logical heights scaled the way WinForms scaled the drawer (it was built 640 logical px wide), so the
        // two-line guidance is not clipped on a 125% or 150% monitor.
        float k = _actionDrawer.Width / (float)DrawerLogicalWidth;
        // The card's own height, up to a cap (an answer gets more room), then it scrolls.
        int cap = (int)Math.Round((_hasAnswer ? 220 : 110) * k);
        _visionOutputBox.Height = Math.Min(cap, _visionOutputBox.ContentHeight(_visionOutputBox.FitWidth) + 2);
        int h = _actionDrawer.Padding.Vertical;
        foreach (Control c in _actionDrawer.Controls) h += c.Height;
        int bottom = _actionDrawer.Bottom;
        _actionDrawer.Height = h;
        // Live it grows upward from where it sits; the offscreen preview pins it to the bottom of the frame.
        _actionDrawer.Top = Visible ? bottom - h : ClientSize.Height - h - 16;
        Invalidate();
    }

    /// <summary>
    /// Shows one card in the output area. An Agent or Fail card is the answer Codex keeps with the capture; a Step
    /// card (asking) only takes the answer's room.
    /// </summary>
    private void ShowAnswer(ThreadTone tone, string kicker, string text)
    {
        _hasAnswer = true;
        _answerText = tone is ThreadTone.Agent or ThreadTone.Fail ? text : "";
        _visionOutputBox.Clear();
        _visionOutputBox.Add(tone, kicker, text);
    }

    /// <summary>
    /// Live capture (the pipe's snip:ask): lassoes <paramref name="screenRect"/> on the frozen desktop and asks the
    /// vision model about it through the real Ask path. Only the lassoed pixels are sent, as with a drawn lasso.
    /// </summary>
    internal Task AskAboutRegionForCaptureAsync(Rectangle screenRect, string question)
    {
        SelectTool(WandCaptureTool.FreeformLasso);
        var r = new Rectangle(screenRect.X - _overlayScreen.X, screenRect.Y - _overlayScreen.Y, screenRect.Width, screenRect.Height);
        _lassoPoints.Clear();
        _lassoPoints.AddRange(new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top) });
        UpdateLassoBoundingBox();
        _visionPromptBox.Text = question;
        Invalidate(true);
        return RunLiveVisionAnalysisAsync();
    }

    /// <summary>The overlay's own chrome in screen pixels (toolbar, drawer, selection), so a capture can mask the rest.</summary>
    internal Rectangle[] ChromeScreenRects() => new[]
    {
        RectangleToScreen(_topToolbar.Bounds),
        RectangleToScreen(_actionDrawer.Bounds),
        RectangleToScreen(Rectangle.Inflate(_selectionRect, 6, 6))
    };

    /// <summary>For the as-built render: "answered" shows an answer card for the seeded lasso; "reset" returns to guidance.</summary>
    internal void PreviewState(string state)
    {
        if (!_analyzeBtn.Enabled) return; // never draw over a live ask
        if (state == "answered")
        {
            _telemetryLabel.Set((PhosphorTheme.Moss, "Lasso ready"), (PhosphorTheme.Ghost, $"{_lassoPoints.Count} pts"), (PhosphorTheme.Ghost, $"{_selectionRect.Width}x{_selectionRect.Height} px"));
            ShowAnswer(ThreadTone.Agent, AnswerKicker(_visionModel),
                "A Holodex drawer on the Session tab. The roster line wraps onto two rows; the Send to all button names its target. Next: open Codex to check the saved picks.");
        }
        else
        {
            SelectTool(_activeTool);
            ShowGuidance();
        }
    }

    /// <summary>The model and route on the vision chip, for the answer card's kicker.</summary>
    private string AnswerKicker(string model, string suffix = "") =>
        $"{ModelChip.ShortName(model)} // {_visionModelChip.Detail}{suffix}";

    private void ShowGuidance() => ShowNotice(ThreadTone.Intro, "Wand // lasso, snip, annotate, point", GuidanceText);

    /// <summary>A short instruction in the output area; not an answer, so Codex never stores it as one.</summary>
    private void ShowNotice(ThreadTone tone, string kicker, string text)
    {
        _hasAnswer = false;
        _answerText = "";
        _visionOutputBox.Clear();
        _visionOutputBox.Add(tone, kicker, text);
    }

    /// <summary>A wrapping telemetry row is as tall as its lines; refit the drawer only when that changes.</summary>
    private void FitTelemetry()
    {
        int want = _telemetryLabel.HeightFor(Math.Max(80, _actionDrawer.ClientSize.Width - _actionDrawer.Padding.Horizontal));
        if (_telemetryLabel.Height == want) return;
        _telemetryLabel.Height = want;
        FitDrawer();
    }

    private static (Color, string)[] PickedTelemetry(ElementPick pick) => new[]
    {
        (PhosphorTheme.Moss, $"Picked // {pick.Class}"),
        (pick.Pixel, pick.Hex),
        (PhosphorTheme.Ghost, $"{pick.Rect.Width}x{pick.Rect.Height} at ({pick.Rect.X},{pick.Rect.Y})"),
        (PhosphorTheme.Ghost, "Click to re-pick")
    };

    /// <summary>Closes the overlay from Esc or the close button, dropping any drag in progress.</summary>
    private void CloseOverlay()
    {
        CancelDrag();
        Hide();
    }

    private static string InkColorName(Color ink) => ink == PhosphorTheme.Patina ? "patina" : ink == PhosphorTheme.Seal ? "seal" : "coral";

    /// <summary>The ink swatch says its color: accessible name "Ink color: coral" and a tip entry per color (PhosphorTips).</summary>
    private void SyncInkName()
    {
        string color = InkColorName(_activeInk);
        _inkBtn.AccessibleName = $"Ink color: {color}";
        _inkBtn.Name = $"Snip.InkButton.{char.ToUpperInvariant(color[0])}{color[1..]}";
    }

    private static void LogSnapshot(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "omniwand-snip.log"), $"{DateTime.Now:O} {line}{Environment.NewLine}"); } catch { }
    }

    public void SummonAt(Point cursorPos, WandCaptureTool initialTool = WandCaptureTool.FreeformLasso)
    {
        var screen = Screen.FromPoint(cursorPos).Bounds;

        // The frozen desktop must be captured with the overlay off screen, otherwise a re-summon
        // (or a window left shown by an offscreen render) captures the dim overlay itself.
        if (Visible || IsWindowVisible(Handle))
        {
            Hide();
            ShowWindow(Handle, 0); // SW_HIDE, also clears a raw ShowWindow from offscreen rendering
            try { Win32.DwmFlush(); } catch { }
        }

        ReleaseSnapshots();
        try
        {
            _desktopSnapshot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(_desktopSnapshot))
                g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);

            _dimmedSnapshot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dimmedSnapshot))
            using (var dim = new SolidBrush(Color.FromArgb(135, PhosphorTheme.Ground)))
            {
                var all = new Rectangle(0, 0, screen.Width, screen.Height);
                g.DrawImage(_desktopSnapshot, all, all, GraphicsUnit.Pixel);
                g.FillRectangle(dim, all);
            }
        }
        catch (Exception ex)
        {
            LogSnapshot($"capture failed {screen}: {ex.GetType().Name}: {ex.Message}");
            ReleaseSnapshots();
        }

        // A fresh capture: no marks, no pick, no question or answer carried over from the last one.
        _session++;
        _analyzeBtn.Enabled = true;
        CancelDrag();
        _annotations.Clear();
        _lassoPoints.Clear();
        _pick = null;
        _hoveredHwnd = IntPtr.Zero;
        _visionPromptBox.Clear();
        _selectionRect = new Rectangle(
            Math.Clamp(cursorPos.X - screen.Left - 180, 40, Math.Max(40, screen.Width - 400)),
            Math.Clamp(cursorPos.Y - screen.Top - 120, 80, Math.Max(80, screen.Height - 360)),
            360,
            240);

        _overlayScreen = screen;
        Bounds = screen;
        ShowGuidance();
        PlaceChrome();

        SelectTool(initialTool);
        if (initialTool == WandCaptureTool.ElementPicker) UpdateHoveredElement(cursorPos);
        else _hoveredElementRect = Rectangle.Empty;

        Show();
        // Showing on a monitor whose DPI differs from the last one rescales the form (OnDpiChanged); pin it back.
        if (Bounds != screen)
        {
            Bounds = screen;
            PlaceChrome();
        }
        Activate();
        Invalidate(true);
        Update();
        _visionPromptBox.Focus();
        _ = RefreshVisionModelsAsync();
    }

    /// <summary>Toolbar centered at the top of the monitor, drawer centered at its bottom.</summary>
    private void PlaceChrome()
    {
        if (_overlayScreen.IsEmpty) return;
        float k = _actionDrawer.Width / (float)DrawerLogicalWidth;
        _topToolbar.Location = new Point(Math.Max((int)(16 * k), (_overlayScreen.Width - _topToolbar.Width) / 2), (int)(18 * k));
        FitDrawer();
        _actionDrawer.Location = new Point(Math.Max((int)(16 * k), (_overlayScreen.Width - _actionDrawer.Width) / 2), _overlayScreen.Height - _actionDrawer.Height - (int)(24 * k));
    }

    /// <summary>
    /// WinForms answers a DPI change by resizing the form by the DPI ratio, which pulls the overlay off its
    /// monitor's edges when it is summoned on a monitor with a different scale. Pin it back to the monitor.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_overlayScreen.IsEmpty) return;
        Bounds = _overlayScreen;
        PlaceChrome();
        Invalidate(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            // Esc during a drag cancels the drag and keeps the overlay; otherwise it closes the overlay.
            if (_isDragging)
            {
                CancelDrag();
                Invalidate();
            }
            else
            {
                CloseOverlay();
            }
            return true;
        }
        // Ctrl+Z removes the last mark, unless the question box has text to undo.
        if (keyData == (Keys.Control | Keys.Z) && _annotations.Count > 0 && !(_visionPromptBox.Focused && _visionPromptBox.TextLength > 0))
        {
            _annotations.RemoveAt(_annotations.Count - 1);
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Ends a drag without applying it: the selection, lasso and pick from before the drag come back.</summary>
    private void CancelDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        Capture = false;
        if (_currentStroke != null)
        {
            _annotations.RemoveAll(a => ReferenceEquals(a.Points, _currentStroke));
            _currentStroke = null;
        }
        else if (_activeTool is WandCaptureTool.FreeformLasso or WandCaptureTool.RectangleSnip)
        {
            RestoreSelectionBeforeDrag();
        }
    }

    private void RestoreSelectionBeforeDrag()
    {
        _selectionRect = _selectionBeforeDrag;
        _lassoPoints.Clear();
        _lassoPoints.AddRange(_lassoBeforeDrag);
        _pick = _pickBeforeDrag;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        // Closed: drop the drag state and the frozen desktop (a full-monitor bitmap of whatever was on screen).
        CancelDrag();
        ReleaseSnapshots();
    }

    private void ReleaseSnapshots()
    {
        _desktopSnapshot?.Dispose();
        _desktopSnapshot = null;
        _dimmedSnapshot?.Dispose();
        _dimmedSnapshot = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseSnapshots();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _isDragging = true;
        _dragStart = e.Location;
        _selectionBeforeDrag = _selectionRect;
        _lassoBeforeDrag = _lassoPoints.ToList();
        _pickBeforeDrag = _pick;

        if (_activeTool == WandCaptureTool.FreeformLasso)
        {
            _lassoPoints.Clear();
            _lassoPoints.Add(e.Location);
        }
        else if (_activeTool == WandCaptureTool.RectangleSnip)
        {
            _lassoPoints.Clear();
            _selectionRect = new Rectangle(e.X, e.Y, 4, 4);
        }
        else if (_activeTool == WandCaptureTool.ElementPicker)
        {
            // A click picks whatever is under it, even after an earlier pick froze the hover card. The pick is
            // frozen here: moving to Ask crosses other windows, and their names must not replace the picked one.
            UpdateHoveredElement(PointToScreen(e.Location));
            _pick = new ElementPick(_hoveredElementRect, _hoveredTitle, _hoveredClass, _hoveredProcess, _hoveredPixelColor, _hoveredHex);
            _selectionRect = _pick.Rect;
            _lassoPoints.Clear(); // the bright cutout follows the element, not an earlier lasso
            _telemetryLabel.Set(PickedTelemetry(_pick));
        }
        else
        {
            _currentStroke = new List<Point> { e.Location };
            _annotations.Add((_activeTool, _activeInk, _currentStroke));
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverPos = e.Location;

        if (_activeTool == WandCaptureTool.ElementPicker && !_isDragging)
        {
            // After a pick the card stays on the picked element; the next click re-picks.
            if (_pick != null) return;
            UpdateHoveredElement(PointToScreen(e.Location));
            Invalidate();
            return;
        }

        if (!_isDragging) return;

        if (_activeTool == WandCaptureTool.FreeformLasso)
        {
            if (_lassoPoints.Count == 0 || Distance(_lassoPoints[^1], e.Location) > 3)
            {
                _lassoPoints.Add(e.Location);
                UpdateLassoBoundingBox();
            }
        }
        else if (_activeTool == WandCaptureTool.RectangleSnip)
        {
            int x = Math.Min(_dragStart.X, e.X);
            int y = Math.Min(_dragStart.Y, e.Y);
            int w = Math.Max(12, Math.Abs(e.X - _dragStart.X));
            int h = Math.Max(12, Math.Abs(e.Y - _dragStart.Y));
            _selectionRect = new Rectangle(x, y, w, h);
        }
        else if (_currentStroke != null)
        {
            if (_activeTool == WandCaptureTool.AnnotatePen)
            {
                _currentStroke.Add(e.Location);
            }
            else
            {
                if (_currentStroke.Count == 1) _currentStroke.Add(e.Location);
                else _currentStroke[1] = e.Location;
            }
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isDragging) return;
        _isDragging = false;
        _currentStroke = null;
        // A click or a jitter of a few pixels is not a new region: keep the selection from before it.
        bool stray = Math.Abs(e.X - _dragStart.X) < MinDragPx && Math.Abs(e.Y - _dragStart.Y) < MinDragPx;
        if (_activeTool == WandCaptureTool.FreeformLasso)
        {
            if (_lassoPoints.Count > 2 && !stray)
            {
                UpdateLassoBoundingBox();
                _pick = null;
                _telemetryLabel.Set((PhosphorTheme.Moss, "Lasso ready"), (PhosphorTheme.Ghost, $"{_lassoPoints.Count} pts"), (PhosphorTheme.Ghost, $"{_selectionRect.Width}x{_selectionRect.Height} px"));
            }
            else
            {
                RestoreSelectionBeforeDrag();
            }
        }
        else if (_activeTool == WandCaptureTool.RectangleSnip)
        {
            if (!stray)
            {
                _pick = null;
                _telemetryLabel.Set((PhosphorTheme.Moss, "Snip ready"), (PhosphorTheme.Ghost, $"{_selectionRect.Width}x{_selectionRect.Height} px at ({_selectionRect.X},{_selectionRect.Y})"));
            }
            else
            {
                RestoreSelectionBeforeDrag();
            }
        }
        Invalidate();
    }

    private const int MinDragPx = 6;

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // Capture taken away mid-drag (Alt+Tab, a system dialog): no mouse-up will come, so cancel the drag.
        if (_isDragging && !Capture)
        {
            CancelDrag();
            Invalidate();
        }
    }

    private void UpdateLassoBoundingBox()
    {
        if (_lassoPoints.Count == 0) return;
        int minX = _lassoPoints.Min(p => p.X);
        int minY = _lassoPoints.Min(p => p.Y);
        int maxX = _lassoPoints.Max(p => p.X);
        int maxY = _lassoPoints.Max(p => p.Y);
        _selectionRect = new Rectangle(minX, minY, Math.Max(16, maxX - minX), Math.Max(16, maxY - minY));
    }

    private static double Distance(Point a, Point b)
    {
        int dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void UpdateHoveredElement(Point screenPt)
    {
        try
        {
            if (_desktopSnapshot != null)
            {
                int sx = Math.Clamp(screenPt.X - Bounds.Left, 0, _desktopSnapshot.Width - 1);
                int sy = Math.Clamp(screenPt.Y - Bounds.Top, 0, _desktopSnapshot.Height - 1);
                _hoveredPixelColor = _desktopSnapshot.GetPixel(sx, sy);
                _hoveredHex = $"#{_hoveredPixelColor.R:X2}{_hoveredPixelColor.G:X2}{_hoveredPixelColor.B:X2}";
            }

            // The overlay is fullscreen and topmost, so WindowFromPoint would always hit it.
            // Walk the real windows underneath in z-order instead.
            IntPtr hwnd = ResolveWindowUnderPoint(screenPt);
            // Same window as the last move: its name, class, rect and process have not changed, so skip the
            // lookups (Process.GetProcessById on every mouse move made the card lag).
            if (hwnd != IntPtr.Zero && hwnd != _hoveredHwnd)
            {
                _hoveredHwnd = hwnd;
                var clsSb = new StringBuilder(128);
                GetClassName(hwnd, clsSb, clsSb.Capacity);
                _hoveredClass = clsSb.Length > 0 ? clsSb.ToString() : "Win32.Control";

                var titleSb = new StringBuilder(256);
                GetWindowText(hwnd, titleSb, titleSb.Capacity);
                _hoveredTitle = titleSb.Length > 0 ? titleSb.ToString() : _hoveredClass;

                if (GetWindowRect(hwnd, out RECT r))
                {
                    _hoveredElementRect = new Rectangle(
                        r.Left - Bounds.Left,
                        r.Top - Bounds.Top,
                        Math.Max(24, r.Right - r.Left),
                        Math.Max(24, r.Bottom - r.Top));
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid > 0)
                {
                    try { _hoveredProcess = Process.GetProcessById((int)pid).ProcessName; } catch { }
                }
            }
            if (_activeTool == WandCaptureTool.ElementPicker)
                _telemetryLabel.Set(
                    (PhosphorTheme.Patina, $"Point // {_hoveredProcess} · {_hoveredClass}"),
                    (_hoveredPixelColor, $"Palette {_hoveredHex}"),
                    (PhosphorTheme.Ghost, $"{_hoveredElementRect.Width}x{_hoveredElementRect.Height}"));
        }
        catch { }
    }

    private IntPtr ResolveWindowUnderPoint(Point screenPt)
    {
        IntPtr self = Handle;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (h == self || !IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;
            if (!GetWindowRect(h, out RECT r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
            if (screenPt.X < r.Left || screenPt.X >= r.Right || screenPt.Y < r.Top || screenPt.Y >= r.Bottom) return true;
            found = h;
            return false;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return IntPtr.Zero;

        // Descend to the deepest visible, non-transparent child under the point.
        IntPtr current = found;
        for (int depth = 0; depth < 32; depth++)
        {
            var pt = new Win32.POINT { X = screenPt.X, Y = screenPt.Y };
            if (!ScreenToClient(current, ref pt)) break;
            IntPtr child = ChildWindowFromPointEx(current, pt, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == current) break;
            current = child;
        }
        return current;
    }

    /// <summary>
    /// What Ask, Send to AInbox and Save to Codex capture: the picked element while the Point at UI tool is
    /// active, otherwise the lasso or rectangle. Empty when Point at UI has nothing picked yet.
    /// </summary>
    private Rectangle CaptureRect()
    {
        if (_activeTool == WandCaptureTool.ElementPicker && _pick == null) return Rectangle.Empty;
        return _selectionRect.Width >= 16 && _selectionRect.Height >= 16 ? _selectionRect : new Rectangle(40, 60, 360, 220);
    }

    private const string PickFirstText = "Click a window or control to pick it first. The card shows what a click will pick.";

    private string RenderSelectionDataUrl(out string error)
    {
        error = string.Empty;
        try
        {
            Rectangle crop = CaptureRect();
            if (crop.IsEmpty)
            {
                error = PickFirstText;
                return string.Empty;
            }
            // Only what is on this monitor: a maximized window reaches 8px past the edges, and a window can
            // continue onto another monitor that is not in the frozen frame.
            if (_desktopSnapshot != null)
                crop = Rectangle.Intersect(crop, new Rectangle(0, 0, _desktopSnapshot.Width, _desktopSnapshot.Height));
            if (crop.Width < 2 || crop.Height < 2)
            {
                error = "The selection is outside this monitor.";
                return string.Empty;
            }

            using var outBmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-crop.X, -crop.Y);
                if (_desktopSnapshot != null)
                {
                    // A lasso sends only what it encloses: outside it is the overlay ground, not more desktop.
                    bool lasso = _lassoPoints.Count > 2 && _pick == null;
                    if (lasso)
                    {
                        g.Clear(PhosphorTheme.Ground);
                        using var path = new GraphicsPath();
                        path.AddPolygon(_lassoPoints.ToArray());
                        g.SetClip(path);
                    }
                    g.DrawImage(_desktopSnapshot, crop, crop, GraphicsUnit.Pixel);
                    g.ResetClip();
                }
                else
                {
                    g.Clear(PhosphorTheme.Surface);
                }

                DrawAnnotations(g);
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return string.Empty;
        }
    }

    private async System.Threading.Tasks.Task RunLiveVisionAnalysisAsync()
    {
        if (!_analyzeBtn.Enabled) return; // this capture's question is already out
        var pick = _pick;
        string prompt = _visionPromptBox.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            // The empty ask is what the box's placeholder promises for the current tool.
            prompt = pick != null
                ? $"Inspect UI target '{pick.Title}' ({pick.Class}, {pick.Rect.Width}x{pick.Rect.Height}, color {pick.Hex}): suggest Phosphor improvements or automation."
                : "Describe the captured screen region and any annotations, and provide actionable technical guidance.";
        }

        string chosenVisionModel = _visionModel;
        string dataUrl = RenderSelectionDataUrl(out string renderError);
        if (string.IsNullOrEmpty(dataUrl))
        {
            // Never send a vision request without the image; a text-only answer would be invented.
            if (renderError == PickFirstText) ShowNotice(ThreadTone.Step, "Point at UI // pick first", PickFirstText);
            else ShowAnswer(ThreadTone.Fail, AnswerKicker(chosenVisionModel, " // not sent"), $"Vision request not sent: capture could not be rendered ({renderError}).");
            return;
        }

        var capture = CaptureRect();
        int session = _session;
        ShowAnswer(ThreadTone.Step, AnswerKicker(chosenVisionModel, " // asking"), $"Asking {ModelChip.ShortName(chosenVisionModel)} about the {capture.Width}x{capture.Height} capture...");
        _analyzeBtn.Enabled = false;
        _asksInFlight++;
        _setThinkingState(true);

        try
        {
            // Element facts only for a picked element; a lasso or snip has none, and the hover card's last
            // window is not what was captured.
            string header = pick != null
                ? $"[Holodex Vision & Element Inspector | Tool={_activeTool} | Element={pick.Title} ({pick.Class}, {pick.Process}) | Geometry={pick.Rect.Width}x{pick.Rect.Height} | Palette={pick.Hex}]"
                : $"[Holodex Vision | Tool={_activeTool} | Region={capture.Width}x{capture.Height} | Marks={_annotations.Count}]";
            var (ok, output) = await _ark.AskVisionModelAsync($"{header}\n{prompt}", dataUrl, chosenVisionModel);
            if (session == _session) ShowAnswer(ok ? ThreadTone.Agent : ThreadTone.Fail, AnswerKicker(chosenVisionModel, ok ? "" : " // not answered"), output);
        }
        catch (Exception ex)
        {
            if (session == _session) ShowAnswer(ThreadTone.Fail, AnswerKicker(chosenVisionModel, " // failed"), $"Vision request to {chosenVisionModel} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // A new summon already re-enabled Ask for its own capture.
            if (session == _session) _analyzeBtn.Enabled = true;
            if (--_asksInFlight == 0) _setThinkingState(false);
        }
    }

    /// <summary>Hands the current lasso or element capture to the AInbox capture well (CP-27).</summary>
    internal Action<Point, string, string>? OnSendToAInbox { get; set; }

    private void SendCurrentCaptureToAInbox()
    {
        string dataUrl = RenderSelectionDataUrl(out string renderError);
        if (string.IsNullOrEmpty(dataUrl) || OnSendToAInbox == null)
        {
            PhosphorToast.Show("Nothing to send", string.IsNullOrEmpty(renderError) ? "Lasso a region or pick an element first." : renderError, "SNIP // AINBOX", 2600, ToastTone.Warning);
            return;
        }
        string name = _pick != null ? "element-capture.png" : _lassoPoints.Count > 2 ? "lasso-capture.png" : "snip-capture.png";
        var at = Cursor.Position;
        Hide();
        OnSendToAInbox(at, dataUrl, name);
    }

    private void SaveCurrentCaptureToCodex()
    {
        string dataUrl = RenderSelectionDataUrl(out string renderError);
        if (string.IsNullOrEmpty(dataUrl))
        {
            PhosphorToast.Show("Nothing to save", renderError, "HOLODEX // CODEX", 2600, ToastTone.Warning);
            return;
        }
        string chosenModel = _visionModel;
        var pick = _pick;
        var capture = CaptureRect();
        string kind = pick != null ? "wand.element" : (_lassoPoints.Count > 2 ? "wand.lasso" : "wand.snip");
        var entry = new CodexCaptureEntry(
            $"codex:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
            kind,
            pick != null ? $"{pick.Process}: {pick.Title}" : $"Screen {kind.Split('.')[1].ToUpperInvariant()} ({capture.Width}x{capture.Height})",
            string.IsNullOrWhiteSpace(_answerText) ? "Captured via Holodex Screen Snip & Element Inspector." : _answerText,
            pick?.Class ?? "Screen.Region",
            $"{capture.Width}x{capture.Height} @ ({capture.X},{capture.Y})",
            pick?.Hex ?? "n/a",
            chosenModel,
            dataUrl,
            DateTime.UtcNow.ToString("O"));

        if (!SaveCodexEntryToDisk(entry, out string saveError))
        {
            PhosphorToast.Show(
                "Codex save failed",
                $"Could not write {CodexLogPath}: {saveError}",
                "HOLODEX // CODEX",
                5200,
                ToastTone.Error);
            return;
        }
        _onCodexSaved?.Invoke(entry);

        PhosphorToast.Show(
            "Saved to Codex",
            $"Stored {entry.Title} ({entry.Geometry}, palette {entry.HexColor}) in Holodex Codex.",
            "HOLODEX // CODEX",
            3200,
            ToastTone.Success);
    }

    public static string CodexLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "codex-entries.jsonl");

    public const string CodexEmptyStateText = "Nothing saved yet. Lasso something and press Save to Codex.";

    public static bool SaveCodexEntryToDisk(CodexCaptureEntry entry) => SaveCodexEntryToDisk(entry, out _);

    public static bool SaveCodexEntryToDisk(CodexCaptureEntry entry, out string error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CodexLogPath)!);
            File.AppendAllText(CodexLogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static List<CodexCaptureEntry> LoadCodexEntries()
    {
        var list = new List<CodexCaptureEntry>();
        try
        {
            if (File.Exists(CodexLogPath))
            {
                foreach (string line in File.ReadAllLines(CodexLogPath).Reverse().Take(30))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var item = JsonSerializer.Deserialize<CodexCaptureEntry>(line);
                    if (item != null) list.Add(item);
                }
            }
        }
        catch { }

        // Only real saved entries. Callers render CodexEmptyStateText when this is empty.
        return list;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_desktopSnapshot != null && _dimmedSnapshot != null)
        {
            // Explicit destination rect: DrawImage(img, x, y) rescales by the bitmap DPI on scaled monitors.
            // The dim is baked into _dimmedSnapshot at summon: blending the whole monitor on every mouse move
            // made the lasso and the hover card trail the pointer on large screens.
            var snapRect = new Rectangle(0, 0, _desktopSnapshot.Width, _desktopSnapshot.Height);
            var mode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_dimmedSnapshot, snapRect, snapRect, GraphicsUnit.Pixel);
            g.CompositingMode = mode;

            if (_selectionRect.Width > 8 && _selectionRect.Height > 8)
            {
                var prevClip = g.Clip;
                if (_lassoPoints.Count > 2)
                {
                    using var lassoPath = new GraphicsPath();
                    lassoPath.AddPolygon(_lassoPoints.ToArray());
                    g.SetClip(lassoPath, CombineMode.Replace);
                }
                else
                {
                    g.SetClip(_selectionRect, CombineMode.Replace);
                }
                g.DrawImage(_desktopSnapshot, snapRect, snapRect, GraphicsUnit.Pixel);
                g.Clip = prevClip;
            }
        }
        else
        {
            g.Clear(PhosphorTheme.Ground);
            // Draw subtle technical grid for offscreen preview capture
            using var gridPen = new Pen(Color.FromArgb(24, PhosphorTheme.Line2), 1f);
            for (int x = 0; x < Width; x += 32) g.DrawLine(gridPen, x, 0, x, Height);
            for (int y = 0; y < Height; y += 32) g.DrawLine(gridPen, 0, y, Width, y);
        }

        // Draw Free-Form Lasso contour or Rectangle Snip frame
        if (_lassoPoints.Count > 2)
        {
            using var glowPen = new Pen(Color.FromArgb(90, PhosphorTheme.Patina), 5f) { LineJoin = LineJoin.Round };
            using var dashPen = new Pen(PhosphorTheme.Patina, 2f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
            using (var fillLasso = new SolidBrush(Color.FromArgb(24, PhosphorTheme.Patina)))
                g.FillPolygon(fillLasso, _lassoPoints.ToArray());
            g.DrawPolygon(glowPen, _lassoPoints.ToArray());
            g.DrawPolygon(dashPen, _lassoPoints.ToArray());

            using var tagFont = PhosphorFonts.Mono(10f);
            using var tagBrush = new SolidBrush(PhosphorTheme.Patina);
            // The tag sits above the lasso, or under it when the toolbar is in the way.
            int lassoTop = _lassoPoints.Min(pt => pt.Y), lassoBottom = _lassoPoints.Max(pt => pt.Y), lassoLeft = _lassoPoints.Min(pt => pt.X);
            float tagY = lassoTop - 20 >= _topToolbar.Bottom + 6 ? lassoTop - 20 : lassoBottom + 8;
            g.DrawString($"LASSO // {_lassoPoints.Count} PTS // {_selectionRect.Width}x{_selectionRect.Height}", tagFont, tagBrush, lassoLeft, tagY);
        }
        else if (_selectionRect.Width > 8 && _selectionRect.Height > 8)
        {
            using var borderPen = new Pen(PhosphorTheme.Patina, 2f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(borderPen, _selectionRect);
        }

        // Draw Element Picker (wand.point + wand.palette + wand.architect) live card
        // The card shows the picked element once there is one, else what a click would pick.
        var shown = _pick ?? new ElementPick(_hoveredElementRect, _hoveredTitle, _hoveredClass, _hoveredProcess, _hoveredPixelColor, _hoveredHex);
        if ((_activeTool == WandCaptureTool.ElementPicker || PreviewAllMarks) && shown.Rect.Width > 8)
        {
            using (var elemPath = PhosphorTheme.CreateRoundedRect(shown.Rect, PhosphorTheme.RadiusTile))
            using (var elemPen = new Pen(PhosphorTheme.Patina, 2f))
                g.DrawPath(elemPen, elemPath);

            // Card geometry in logical pixels scaled to the monitor, like its point-size fonts, and kept on screen:
            // an element at the right or bottom edge would otherwise push the card off the monitor.
            float k = g.DpiX / 96f;
            int cw = (int)(264 * k), ch = (int)(96 * k), pad = (int)(10 * k);
            int cx = Math.Clamp(shown.Rect.Left + pad, pad, Math.Max(pad, ClientSize.Width - cw - pad));
            int cy = Math.Clamp(shown.Rect.Top + pad, pad, Math.Max(pad, ClientSize.Height - ch - pad));
            var cardRect = new Rectangle(cx, cy, cw, ch);
            PhosphorTheme.DrawPhosphorPanel(g, cardRect, PhosphorTheme.RadiusTile, PhosphorTheme.Surface, PhosphorTheme.Line);
            using var monoFont = PhosphorFonts.Mono(9.5f);
            using var titleFont = PhosphorFonts.Ui(10.5f, heavy: true);
            using var boneBrush = new SolidBrush(PhosphorTheme.Bone);
            using var dimBrush = new SolidBrush(PhosphorTheme.Dim);
            using var coralBrush = new SolidBrush(PhosphorTheme.Coral);
            using var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            float tx = cardRect.Left + pad, tw = cardRect.Width - 2 * pad;

            g.DrawString(_pick != null ? "WAND.POINT // PICKED" : "WAND.POINT // ELEMENT INSPECTOR", monoFont, coralBrush, new RectangleF(tx, cardRect.Top + 8 * k, tw, 18 * k), trim);
            g.DrawString(shown.Title, titleFont, boneBrush, new RectangleF(tx, cardRect.Top + 26 * k, tw, 20 * k), trim);
            g.DrawString($"CLASS: {shown.Class}", monoFont, dimBrush, new RectangleF(tx, cardRect.Top + 46 * k, tw, 18 * k), trim);
            g.DrawString($"ARCH:  {shown.Rect.Width}x{shown.Rect.Height}px  PALETTE: {shown.Hex}", monoFont, boneBrush, new RectangleF(tx, cardRect.Top + 66 * k, tw - 26 * k, 18 * k), trim);

            using var swatchBrush = new SolidBrush(shown.Pixel);
            var swatchRect = new Rectangle(cardRect.Right - (int)(30 * k), cardRect.Top + (int)(62 * k), (int)(18 * k), (int)(18 * k));
            using var swatchOutline = new Pen(PhosphorTheme.Bone, 1f);
            g.FillRectangle(swatchBrush, swatchRect);
            g.DrawRectangle(swatchOutline, swatchRect);
        }

        DrawAnnotations(g);
        using (var edgePen = new Pen(Color.FromArgb(160, PhosphorTheme.Patina), 2f))
            g.DrawRectangle(edgePen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    private void DrawAnnotations(Graphics g)
    {
        foreach (var (tool, color, pts) in _annotations)
        {
            if (pts.Count < 2) continue;
            using var pen = new Pen(color, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (tool == WandCaptureTool.AnnotatePen)
            {
                g.DrawLines(pen, pts.ToArray());
            }
            else if (tool == WandCaptureTool.AnnotateArrow)
            {
                pen.CustomEndCap = new AdjustableArrowCap(5f, 5f);
                g.DrawLine(pen, pts[0], pts[^1]);
            }
            else if (tool == WandCaptureTool.AnnotateBox)
            {
                int x = Math.Min(pts[0].X, pts[^1].X);
                int y = Math.Min(pts[0].Y, pts[^1].Y);
                int w = Math.Abs(pts[^1].X - pts[0].X);
                int h = Math.Abs(pts[^1].Y - pts[0].Y);
                g.DrawRectangle(pen, x, y, w, h);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int DWMWA_CLOAKED = 14;
    private const uint CWP_SKIPINVISIBLE = 0x0001;
    private const uint CWP_SKIPTRANSPARENT = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Win32.POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, Win32.POINT pt, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
