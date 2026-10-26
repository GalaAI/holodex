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
    private WandCaptureTool _activeTool = WandCaptureTool.FreeformLasso;
    private Color _activeInk = PhosphorTheme.Coral;

    private readonly List<Point> _lassoPoints = new();
    private Rectangle _selectionRect = Rectangle.Empty;
    private bool _isDragging;
    private Point _dragStart;

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
    private readonly RichTextBox _visionOutputBox;
    private readonly Panel _outputGap;
    private readonly ComposerSurface _composer;
    private readonly DataLabel _telemetryLabel;
    private readonly PhosphorButton _analyzeBtn;
    private readonly IconToolButton _inkBtn;
    private string _visionModel;

    /// <summary>For the as-built render: draw the lasso and the element card together, as the board shows them.</summary>
    internal bool PreviewAllMarks { get; set; }

    public static readonly string[] VisionModels =
    {
        "nvidia/Nemotron-3.5-Super-VL",
        "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B",
        "openbmb/MiniCPM-V-4_5",
        "Qwen/Qwen2.5-VL-72B-Instruct",
        "google/gemma-3-27b-it",
        "ark/kimi"
    };

    public ScreenSnipAndElementPickerForm(
        ArkStationClient ark,
        Action<bool> setThinkingState,
        Action<CodexCaptureEntry>? onCodexSaved = null)
    {
        _ark = ark;
        _setThinkingState = setThinkingState;
        _onCodexSaved = onCodexSaved;

        PhosphorFonts.EnsureLoaded();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = PhosphorTheme.Ground;
        ForeColor = PhosphorTheme.Bone;
        Name = "SnipAnnotator";
        Size = new Size(760, 520);
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

        // Ink: a swatch of the current ink; each press cycles coral, patina, seal.
        _inkBtn = new IconToolButton("Snip.InkButton", (g, r, _, _) =>
        {
            using var b = new SolidBrush(_activeInk);
            float d = r.Width * 0.8f;
            g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }, "Ink color") { Location = new Point(bx, 6) };
        _inkBtn.Click += (_, _) =>
        {
            _activeInk = _activeInk == PhosphorTheme.Coral ? PhosphorTheme.Patina : _activeInk == PhosphorTheme.Patina ? PhosphorTheme.Seal : PhosphorTheme.Coral;
            _inkBtn.Invalidate();
        };
        _topToolbar.Controls.Add(_inkBtn);

        var closeBtn = new PhosphorButton("×")
        {
            Glyph = true,
            Location = new Point(_topToolbar.Width - 50, 10),
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        closeBtn.Click += (_, _) => Hide();
        _topToolbar.Controls.Add(closeBtn);

        // Bottom drawer: the answer once there is one, the question, and model plus Ask.
        _actionDrawer = new Panel
        {
            Size = new Size(640, 200),
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

        _visionOutputBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 120,
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(12.5f),
            Name = "Snip.VisionOutputBox",
            Text = string.Empty
        };
        PhosphorPaint.UseDarkScrollbars(_visionOutputBox);
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
        _telemetryLabel = new DataLabel("") { Dock = DockStyle.Fill, Height = 44, ForeColor = PhosphorTheme.Ghost, Name = "Snip.Telemetry" };
        bar.Controls.Add(_telemetryLabel);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        bar.Controls.Add(saveCodexBtn);
        bar.Controls.Add(toAInboxBtn);
        _composer.Content.Controls.Add(bar);
        _composer.Content.Controls.Add(_visionPromptBox);
        _composer.Height = _composer.HeightFor(_visionPromptBox.Height + bar.Height);

        var footer = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Name = "Snip.Footer" };
        _visionModel = VisionModels[0];
        _visionModelChip = new ModelChip(_ark, VisionModels, () => _visionModel, m => _visionModel = m) { Dock = DockStyle.Left, Name = "Snip.VisionModelSelect" };
        _visionModelChip.SizeChanged += (_, _) => SyncAskLabel();
        _analyzeBtn = new PhosphorButton("Ask", PhosphorButtonVariant.Primary) { Dock = DockStyle.Right, Width = 170, Name = "Snip.AnalyzeButton" };
        _analyzeBtn.Click += async (_, _) => await RunLiveVisionAnalysisAsync();
        footer.Controls.Add(_visionModelChip);
        footer.Controls.Add(_analyzeBtn);

        _actionDrawer.Controls.Add(footer);
        _actionDrawer.Controls.Add(WidgetChrome.Spacer(10));
        _actionDrawer.Controls.Add(_composer);
        _actionDrawer.Controls.Add(_outputGap);
        _actionDrawer.Controls.Add(_visionOutputBox);

        Controls.Add(_topToolbar);
        Controls.Add(_actionDrawer);
        SyncAskLabel();
        SelectTool(_activeTool);
        FitDrawer();

        // Seed preview state for SurfaceCapture offscreen rendering
        SeedPreviewDemoState();
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
        _telemetryLabel.Text = tool switch
        {
            WandCaptureTool.FreeformLasso => "LASSO // DRAG AROUND ANY SHAPE",
            WandCaptureTool.RectangleSnip => "RECTANGLE // DRAG A FRAME",
            WandCaptureTool.ElementPicker => "POINT // HOVER, CLICK TO LOCK",
            WandCaptureTool.AnnotatePen   => "DRAW // FREEHAND",
            WandCaptureTool.AnnotateArrow => "ARROW // DRAG TO THE TARGET",
            WandCaptureTool.AnnotateBox   => "BOX // DRAG TO HIGHLIGHT",
            _ => ""
        };
        _visionPromptBox.Prompt = tool == WandCaptureTool.ElementPicker
            ? "Ask about the element. Empty asks for Phosphor improvements."
            : "Ask about the capture. Empty asks for a description and next steps.";
        Invalidate();
    }

    private void SyncAskLabel()
    {
        string first = _visionModelChip.Label.Split(' ')[0];
        _analyzeBtn.Text = first.Length > 0 ? $"Ask {first}" : "Ask";
    }

    /// <summary>The drawer is as tall as what it holds; the answer opens above the question once there is one.</summary>
    private void FitDrawer()
    {
        bool hasAnswer = _visionOutputBox.TextLength > 0;
        _visionOutputBox.Visible = _outputGap.Visible = hasAnswer;
        int h = _actionDrawer.Padding.Vertical;
        foreach (Control c in _actionDrawer.Controls)
            if (c != _visionOutputBox && c != _outputGap || hasAnswer) h += c.Height;
        int bottom = _actionDrawer.Bottom;
        _actionDrawer.Height = h;
        if (Visible) _actionDrawer.Top = bottom - h;
        Invalidate();
    }

    private void ShowAnswer(string text, Color color)
    {
        _visionOutputBox.ForeColor = color;
        _visionOutputBox.Text = text;
        FitDrawer();
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

        try
        {
            _desktopSnapshot?.Dispose();
            _desktopSnapshot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(_desktopSnapshot))
                g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            LogSnapshot($"capture failed {screen}: {ex.GetType().Name}: {ex.Message}");
            _desktopSnapshot = null;
        }

        _annotations.Clear();
        _lassoPoints.Clear();
        _selectionRect = new Rectangle(
            Math.Clamp(cursorPos.X - screen.Left - 180, 40, Math.Max(40, screen.Width - 400)),
            Math.Clamp(cursorPos.Y - screen.Top - 120, 80, Math.Max(80, screen.Height - 360)),
            360,
            240);

        Bounds = screen;
        _topToolbar.Location = new Point(Math.Max(16, (screen.Width - _topToolbar.Width) / 2), 18);
        _visionOutputBox.Text = string.Empty;
        FitDrawer();
        _actionDrawer.Location = new Point(Math.Max(16, (screen.Width - _actionDrawer.Width) / 2), screen.Height - _actionDrawer.Height - 24);

        SelectTool(initialTool);
        if (initialTool == WandCaptureTool.ElementPicker) UpdateHoveredElement(cursorPos);
        else _hoveredElementRect = Rectangle.Empty;

        Show();
        Activate();
        Invalidate(true);
        Update();
        _visionPromptBox.Focus();
        _ = _visionModelChip.RefreshAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _isDragging = true;
        _dragStart = e.Location;

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
            _selectionRect = _hoveredElementRect;
            _telemetryLabel.Text = $"LOCKED // {_hoveredClass.ToUpperInvariant()} // {_hoveredElementRect.Width}x{_hoveredElementRect.Height}";
            _visionPromptBox.Text = $"Inspect UI target '{_hoveredTitle}' ({_hoveredClass}, {_hoveredElementRect.Width}x{_hoveredElementRect.Height}, color {_hoveredHex}): suggest Phosphor improvements or automation.";
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
        if (_activeTool == WandCaptureTool.FreeformLasso && _lassoPoints.Count > 2)
        {
            UpdateLassoBoundingBox();
            _telemetryLabel.Text = $"LASSO // {_lassoPoints.Count} PTS // {_selectionRect.Width}x{_selectionRect.Height}";
        }
        else if (_activeTool == WandCaptureTool.RectangleSnip)
        {
            _telemetryLabel.Text = $"RECTANGLE // {_selectionRect.Width}x{_selectionRect.Height}";
        }
        Invalidate();
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
            if (hwnd != IntPtr.Zero)
            {
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
                _telemetryLabel.Text = $"POINT // {_hoveredProcess.ToUpperInvariant()} // {_hoveredElementRect.Width}x{_hoveredElementRect.Height}";
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

    private string RenderSelectionDataUrl() => RenderSelectionDataUrl(out _);

    private string RenderSelectionDataUrl(out string error)
    {
        error = string.Empty;
        try
        {
            Rectangle crop = _selectionRect.Width >= 16 && _selectionRect.Height >= 16
                ? _selectionRect
                : new Rectangle(40, 60, 360, 220);

            using var outBmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_desktopSnapshot != null)
                {
                    g.DrawImage(_desktopSnapshot, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                }
                else
                {
                    g.Clear(PhosphorTheme.Surface);
                }

                g.TranslateTransform(-crop.X, -crop.Y);
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
        string prompt = _visionPromptBox.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = _activeTool == WandCaptureTool.ElementPicker
                ? $"Analyze UI element '{_hoveredTitle}' (class {_hoveredClass}, geometry {_hoveredElementRect.Width}x{_hoveredElementRect.Height}, pixel palette {_hoveredHex})."
                : "Describe the captured screen region and any annotations, and provide actionable technical guidance.";
        }

        string chosenVisionModel = _visionModel;
        string dataUrl = RenderSelectionDataUrl(out string renderError);
        if (string.IsNullOrEmpty(dataUrl))
        {
            // Never send a vision request without the image; a text-only answer would be invented.
            ShowAnswer($"Vision request not sent: capture could not be rendered ({renderError}).", PhosphorTheme.Rust);
            return;
        }

        ShowAnswer($"Asking {ModelChip.ShortName(chosenVisionModel)} about the {_selectionRect.Width}x{_selectionRect.Height} capture...", PhosphorTheme.Patina);
        _setThinkingState(true);

        try
        {
            string enrichedPrompt =
                $"[Omniwand Vision & Element Inspector | Tool={_activeTool} | Element={_hoveredTitle} ({_hoveredClass}) | Geometry={_selectionRect.Width}x{_selectionRect.Height} | Palette={_hoveredHex}]\n{prompt}";
            var (ok, output) = await _ark.AskVisionModelAsync(enrichedPrompt, dataUrl, chosenVisionModel);
            ShowAnswer(output, ok ? PhosphorTheme.Bone : PhosphorTheme.Rust);
        }
        catch (Exception ex)
        {
            ShowAnswer($"Vision request to {chosenVisionModel} failed: {ex.GetType().Name}: {ex.Message}", PhosphorTheme.Rust);
        }
        finally
        {
            _setThinkingState(false);
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
        string name = _activeTool == WandCaptureTool.ElementPicker ? "element-capture.png" : "lasso-capture.png";
        var at = Cursor.Position;
        Hide();
        OnSendToAInbox(at, dataUrl, name);
    }

    private void SaveCurrentCaptureToCodex()
    {
        string dataUrl = RenderSelectionDataUrl();
        string chosenModel = _visionModel;
        string kind = _activeTool == WandCaptureTool.ElementPicker ? "wand.element" : (_lassoPoints.Count > 2 ? "wand.lasso" : "wand.snip");
        var entry = new CodexCaptureEntry(
            $"codex:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
            kind,
            _activeTool == WandCaptureTool.ElementPicker ? $"{_hoveredProcess}: {_hoveredTitle}" : $"Screen {kind.Split('.')[1].ToUpperInvariant()} ({_selectionRect.Width}x{_selectionRect.Height})",
            string.IsNullOrWhiteSpace(_visionOutputBox.Text) ? "Captured via Omniwand Screen Snip & Element Inspector." : _visionOutputBox.Text,
            _hoveredClass,
            $"{_selectionRect.Width}x{_selectionRect.Height} @ ({_selectionRect.X},{_selectionRect.Y})",
            _hoveredHex,
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
        if (_desktopSnapshot != null)
        {
            // Explicit destination rect: DrawImage(img, x, y) rescales by the bitmap DPI on scaled monitors.
            var snapRect = new Rectangle(0, 0, _desktopSnapshot.Width, _desktopSnapshot.Height);
            g.DrawImage(_desktopSnapshot, snapRect, snapRect, GraphicsUnit.Pixel);
            using var dimBrush = new SolidBrush(Color.FromArgb(135, PhosphorTheme.Ground));
            g.FillRectangle(dimBrush, ClientRectangle);

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
        if ((_activeTool == WandCaptureTool.ElementPicker || PreviewAllMarks) && _hoveredElementRect.Width > 8)
        {
            using (var elemPath = PhosphorTheme.CreateRoundedRect(_hoveredElementRect, PhosphorTheme.RadiusTile))
            using (var elemPen = new Pen(PhosphorTheme.Patina, 2f))
                g.DrawPath(elemPen, elemPath);

            var cardRect = new Rectangle(_hoveredElementRect.Left + 10, _hoveredElementRect.Top + 10, 250, 96);
            PhosphorTheme.DrawPhosphorPanel(g, cardRect, PhosphorTheme.RadiusTile, PhosphorTheme.Surface, PhosphorTheme.Line);
            using var monoFont = PhosphorFonts.Mono(9.5f);
            using var titleFont = PhosphorFonts.Ui(10.5f, heavy: true);
            using var boneBrush = new SolidBrush(PhosphorTheme.Bone);
            using var dimBrush = new SolidBrush(PhosphorTheme.Dim);
            using var coralBrush = new SolidBrush(PhosphorTheme.Coral);

            g.DrawString("WAND.POINT // ELEMENT INSPECTOR", monoFont, coralBrush, cardRect.Left + 10, cardRect.Top + 8);
            g.DrawString(_hoveredTitle, titleFont, boneBrush, cardRect.Left + 10, cardRect.Top + 26);
            g.DrawString($"CLASS: {_hoveredClass}", monoFont, dimBrush, cardRect.Left + 10, cardRect.Top + 46);
            g.DrawString($"ARCH:  {_hoveredElementRect.Width}x{_hoveredElementRect.Height}px  PALETTE: {_hoveredHex}", monoFont, boneBrush, cardRect.Left + 10, cardRect.Top + 66);

            using var swatchBrush = new SolidBrush(_hoveredPixelColor);
            var swatchRect = new Rectangle(cardRect.Right - 30, cardRect.Top + 62, 18, 18);
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
