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
    private readonly PhosphorComboBox _visionModelSelect;
    private readonly PhosphorField _visionPromptField;
    private readonly RichTextBox _visionOutputBox;
    private readonly DataLabel _telemetryLabel;

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

        // Top floating Phosphor toolbar
        _topToolbar = new Panel
        {
            Size = new Size(720, 52),
            Location = new Point(20, 16),
            BackColor = PhosphorTheme.Surface,
            Padding = new Padding(10, 6, 10, 6),
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
        void AddToolBtn(string label, WandCaptureTool tool, int width = 88)
        {
            var btn = new PhosphorButton(label, _activeTool == tool ? PhosphorButtonVariant.Primary : PhosphorButtonVariant.Default)
            {
                Location = new Point(bx, 8),
                Size = new Size(width, 36),
                Tag = tool
            };
            btn.Click += (_, _) => SelectTool(tool);
            _topToolbar.Controls.Add(btn);
            bx += width + 6;
        }

        AddToolBtn("Lasso", WandCaptureTool.FreeformLasso, 82);
        AddToolBtn("Rect", WandCaptureTool.RectangleSnip, 76);
        AddToolBtn("Point UI", WandCaptureTool.ElementPicker, 98);
        AddToolBtn("Draw", WandCaptureTool.AnnotatePen, 78);
        AddToolBtn("Arrow", WandCaptureTool.AnnotateArrow, 84);
        AddToolBtn("Box", WandCaptureTool.AnnotateBox, 72);

        // Ink color cycler
        var inkBtn = new PhosphorButton("Ink: Coral")
        {
            Location = new Point(bx, 8),
            Size = new Size(106, 36),
            Name = "Snip.InkButton"
        };
        inkBtn.Click += (_, _) =>
        {
            if (_activeInk == PhosphorTheme.Coral) { _activeInk = PhosphorTheme.Patina; inkBtn.Text = "Ink: Patina"; }
            else if (_activeInk == PhosphorTheme.Patina) { _activeInk = PhosphorTheme.Seal; inkBtn.Text = "Ink: Seal"; }
            else { _activeInk = PhosphorTheme.Coral; inkBtn.Text = "Ink: Coral"; }
        };
        _topToolbar.Controls.Add(inkBtn);
        bx += 112;

        var closeBtn = new PhosphorButton("×")
        {
            Glyph = true,
            Location = new Point(_topToolbar.Width - 44, 8),
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        closeBtn.Click += (_, _) => Hide();
        _topToolbar.Controls.Add(closeBtn);

        // Bottom floating Vision + Codex action drawer
        _actionDrawer = new Panel
        {
            Size = new Size(720, 196),
            Location = new Point(20, 304),
            BackColor = PhosphorTheme.Surface,
            Padding = new Padding(16, 12, 16, 12),
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

        _telemetryLabel = new DataLabel("WAND // LASSO + ANNOTATE + ELEMENT PICKER // SELECT REGION OR HOVER UI")
        {
            Location = new Point(16, 10),
            Size = new Size(440, 20),
            Dock = DockStyle.None
        };
        _actionDrawer.Controls.Add(_telemetryLabel);

        _visionModelSelect = new PhosphorComboBox
        {
            Location = new Point(468, 8),
            Size = new Size(236, 26),
            Name = "Snip.VisionModelSelect"
        };
        foreach (string vm in VisionModels) _visionModelSelect.Items.Add(vm);
        _visionModelSelect.SelectedIndex = 0;
        _actionDrawer.Controls.Add(_visionModelSelect);

        _visionPromptField = new PhosphorField(
            "Ask Live Vision Model about this lasso/snip or UI element (Enter sends)...",
            false,
            38)
        {
            Location = new Point(16, 38),
            Size = new Size(424, 38),
            Name = "Snip.VisionPromptField"
        };
        _visionPromptField.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                await RunLiveVisionAnalysisAsync();
            }
        };
        _actionDrawer.Controls.Add(_visionPromptField);

        var analyzeBtn = new PhosphorButton("Ask Vision", PhosphorButtonVariant.Primary)
        {
            Location = new Point(448, 36),
            Size = new Size(132, 40),
            Name = "Snip.AnalyzeButton"
        };
        analyzeBtn.Click += async (_, _) => await RunLiveVisionAnalysisAsync();
        _actionDrawer.Controls.Add(analyzeBtn);

        var saveCodexBtn = new PhosphorButton("Save Codex")
        {
            Location = new Point(586, 36),
            Size = new Size(118, 40),
            Name = "Snip.SaveCodexButton"
        };
        saveCodexBtn.Click += (_, _) => SaveCurrentCaptureToCodex();
        _actionDrawer.Controls.Add(saveCodexBtn);

        _visionOutputBox = new RichTextBox
        {
            Location = new Point(16, 84),
            Size = new Size(688, 98),
            ReadOnly = true,
            BackColor = PhosphorTheme.Surface2,
            ForeColor = PhosphorTheme.Bone,
            BorderStyle = BorderStyle.None,
            Font = PhosphorFonts.Body(12.5f),
            Name = "Snip.VisionOutputBox",
            Text = "Draw a free-form lasso or rectangle snip, add Phosphor annotations, or switch to 'Point UI' (Alt+E) to inspect any desktop window's HWND, geometry, and live pixel palette."
        };
        PhosphorPaint.UseDarkScrollbars(_visionOutputBox);
        _actionDrawer.Controls.Add(_visionOutputBox);

        Controls.Add(_topToolbar);
        Controls.Add(_actionDrawer);

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
        foreach (var ctrl in _topToolbar.Controls.OfType<PhosphorButton>())
        {
            if (ctrl.Tag is WandCaptureTool t)
            {
                ctrl.Variant = t == tool ? PhosphorButtonVariant.Primary : PhosphorButtonVariant.Default;
                ctrl.Invalidate();
            }
        }
        _telemetryLabel.Text = tool switch
        {
            WandCaptureTool.FreeformLasso => "WAND // FREE-FORM LASSO // DRAG TO LASSO ANY SCREEN SHAPE",
            WandCaptureTool.RectangleSnip => "WAND // RECTANGLE SNIP // DRAG TO FRAME CAPTURE REGION",
            WandCaptureTool.ElementPicker => "WAND // POINT + PALETTE + ARCHITECT // HOVER & CLICK ANY WINDOW",
            WandCaptureTool.AnnotatePen   => "WAND // ANNOTATE PEN // DRAW FREEHAND CALLOUTS",
            WandCaptureTool.AnnotateArrow => "WAND // ANNOTATE ARROW // DRAG TO POINT AT TARGET",
            WandCaptureTool.AnnotateBox   => "WAND // ANNOTATE BOX // DRAG TO HIGHLIGHT TARGET",
            _ => "WAND // READY"
        };
        Invalidate();
    }

    public void SummonAt(Point cursorPos, WandCaptureTool initialTool = WandCaptureTool.FreeformLasso)
    {
        var screen = Screen.FromPoint(cursorPos).Bounds;
        try
        {
            _desktopSnapshot?.Dispose();
            _desktopSnapshot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(_desktopSnapshot);
            g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);
        }
        catch
        {
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
        _actionDrawer.Location = new Point(Math.Max(16, (screen.Width - _actionDrawer.Width) / 2), screen.Height - _actionDrawer.Height - 24);

        SelectTool(initialTool);
        UpdateHoveredElement(cursorPos);

        Show();
        Activate();
        _visionPromptField.Box.Focus();
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
            _telemetryLabel.Text = $"LOCKED // {_hoveredClass.ToUpperInvariant()} // {_hoveredHex} // {_hoveredElementRect.Width}x{_hoveredElementRect.Height}";
            _visionPromptField.Box.Text = $"Inspect UI target '{_hoveredTitle}' ({_hoveredClass}, {_hoveredElementRect.Width}x{_hoveredElementRect.Height}, color {_hoveredHex}): suggest Phosphor improvements or automation.";
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
            _telemetryLabel.Text = $"LASSO READY // {_lassoPoints.Count} PTS // {_selectionRect.Width}x{_selectionRect.Height} PX";
        }
        else if (_activeTool == WandCaptureTool.RectangleSnip)
        {
            _telemetryLabel.Text = $"SNIP READY // {_selectionRect.Width}x{_selectionRect.Height} PX AT ({_selectionRect.X},{_selectionRect.Y})";
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

            // Temporarily hide or inspect underneath
            IntPtr hwnd = WindowFromPoint(new Win32.POINT { X = screenPt.X, Y = screenPt.Y });
            if (hwnd != IntPtr.Zero && hwnd != Handle)
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
            _telemetryLabel.Text = $"POINT // {_hoveredProcess.ToUpperInvariant()} · {_hoveredClass} // PALETTE {_hoveredHex} // {_hoveredElementRect.Width}x{_hoveredElementRect.Height}";
        }
        catch { }
    }

    private string RenderSelectionDataUrl()
    {
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
        catch
        {
            return string.Empty;
        }
    }

    private async System.Threading.Tasks.Task RunLiveVisionAnalysisAsync()
    {
        string prompt = _visionPromptField.Box.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = _activeTool == WandCaptureTool.ElementPicker
                ? $"Analyze UI element '{_hoveredTitle}' (class {_hoveredClass}, geometry {_hoveredElementRect.Width}x{_hoveredElementRect.Height}, pixel palette {_hoveredHex})."
                : "Describe the captured screen region and any annotations, and provide actionable technical guidance.";
        }

        string chosenVisionModel = _visionModelSelect.SelectedItem?.ToString() ?? VisionModels[0];
        string dataUrl = RenderSelectionDataUrl();

        _visionOutputBox.ForeColor = PhosphorTheme.Patina;
        _visionOutputBox.Text = $"Routing annotated capture ({_selectionRect.Width}x{_selectionRect.Height}px, palette {_hoveredHex}) to {chosenVisionModel}...";
        _setThinkingState(true);

        try
        {
            string enrichedPrompt =
                $"[Omniwand Vision & Element Inspector | Tool={_activeTool} | Element={_hoveredTitle} ({_hoveredClass}) | Geometry={_selectionRect.Width}x{_selectionRect.Height} | Palette={_hoveredHex}]\n{prompt}";
            var (ok, output) = await _ark.AskVisionModelAsync(enrichedPrompt, dataUrl, chosenVisionModel);
            _visionOutputBox.ForeColor = ok ? PhosphorTheme.Bone : PhosphorTheme.Rust;
            _visionOutputBox.Text = output;
        }
        finally
        {
            _setThinkingState(false);
        }
    }

    private void SaveCurrentCaptureToCodex()
    {
        string dataUrl = RenderSelectionDataUrl();
        string chosenModel = _visionModelSelect.SelectedItem?.ToString() ?? VisionModels[0];
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

        SaveCodexEntryToDisk(entry);
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

    public static void SaveCodexEntryToDisk(CodexCaptureEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CodexLogPath)!);
            File.AppendAllText(CodexLogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch { }
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

        if (list.Count == 0)
        {
            list.Add(new CodexCaptureEntry(
                "codex:seed-lasso",
                "wand.lasso",
                "Free-Form Lasso // Phosphor Component Audit",
                "Lasso capture of desktop widget chrome routed through nvidia/Nemotron-3.5-Super-VL.",
                "WinForms.PhosphorSurface",
                "360x198 @ (64,82)",
                "#79C4B7",
                "nvidia/Nemotron-3.5-Super-VL",
                null,
                DateTime.UtcNow.ToString("O")));
            list.Add(new CodexCaptureEntry(
                "codex:seed-element",
                "wand.element",
                "Element Picker // Desktop Window Telemetry",
                "Point + Palette + Architect readout: verified #0B0B0B Ground and #F3EFE6 Bone contrast.",
                "Chrome_WidgetWin_1",
                "460x820 @ (1460,40)",
                "#F3EFE6",
                "openbmb/MiniCPM-V-4_5",
                null,
                DateTime.UtcNow.ToString("O")));
        }
        return list;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_desktopSnapshot != null)
        {
            g.DrawImage(_desktopSnapshot, 0, 0);
            using var dimBrush = new SolidBrush(Color.FromArgb(135, 8, 10, 12));
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
                g.DrawImage(_desktopSnapshot, 0, 0);
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
            g.DrawPolygon(glowPen, _lassoPoints.ToArray());
            g.DrawPolygon(dashPen, _lassoPoints.ToArray());

            using var tagFont = PhosphorFonts.Mono(10f);
            using var tagBrush = new SolidBrush(PhosphorTheme.Patina);
            g.DrawString($"FREE-FORM LASSO // {_lassoPoints.Count} VERTICES · {_selectionRect.Width}x{_selectionRect.Height}PX",
                tagFont, tagBrush, _selectionRect.Left, Math.Max(74, _selectionRect.Top - 18));
        }
        else if (_selectionRect.Width > 8 && _selectionRect.Height > 8)
        {
            using var borderPen = new Pen(PhosphorTheme.Patina, 2f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(borderPen, _selectionRect);
        }

        // Draw Element Picker (wand.point + wand.palette + wand.architect) live card
        if (_hoveredElementRect.Width > 8)
        {
            using var elemPen = new Pen(PhosphorTheme.Coral, 2f);
            g.DrawRectangle(elemPen, _hoveredElementRect);

            var cardRect = new Rectangle(_hoveredElementRect.Left + 10, _hoveredElementRect.Top + 10, 250, 96);
            PhosphorTheme.DrawPhosphorPanel(g, cardRect, PhosphorTheme.RadiusTile, PhosphorTheme.Surface, PhosphorTheme.Coral);
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
        WidgetChrome.PaintFrame(this, g, PhosphorTheme.Patina);
    }

    public Bitmap OverlayCanvasPreview(Bitmap baseRender)
    {
        var outBmp = new Bitmap(baseRender.Width, baseRender.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.DrawImageUnscaled(baseRender, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Paint the middle canvas viewport (between top toolbar y=68 and bottom action drawer y=300)
            var prevClip = g.Clip;
            g.SetClip(new Rectangle(4, 70, Width - 8, 230), CombineMode.Replace);

            // Technical grid lines in the canvas region
            using (var gridPen = new Pen(Color.FromArgb(24, PhosphorTheme.Line2), 1f))
            {
                for (int x = 16; x < Width - 16; x += 32) g.DrawLine(gridPen, x, 70, x, 300);
                for (int y = 76; y < 300; y += 32) g.DrawLine(gridPen, 4, y, Width - 4, y);
            }

            if (_lassoPoints.Count > 2)
            {
                using var fillLasso = new SolidBrush(Color.FromArgb(24, PhosphorTheme.Patina));
                using var glowPen = new Pen(Color.FromArgb(90, PhosphorTheme.Patina), 5f) { LineJoin = LineJoin.Round };
                using var dashPen = new Pen(PhosphorTheme.Patina, 2f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
                g.FillPolygon(fillLasso, _lassoPoints.ToArray());
                g.DrawPolygon(glowPen, _lassoPoints.ToArray());
                g.DrawPolygon(dashPen, _lassoPoints.ToArray());

                using var tagFont = PhosphorFonts.Mono(9.5f);
                using var tagBrush = new SolidBrush(PhosphorTheme.Patina);
                g.DrawString($"FREE-FORM LASSO // {_lassoPoints.Count} VERTICES · {_selectionRect.Width}x{_selectionRect.Height}PX",
                    tagFont, tagBrush, _selectionRect.Left, Math.Max(74, _selectionRect.Top - 16));
            }

            if (_hoveredElementRect.Width > 8)
            {
                using var elemPen = new Pen(PhosphorTheme.Coral, 2f);
                g.DrawRectangle(elemPen, _hoveredElementRect);

                var cardRect = new Rectangle(_hoveredElementRect.Left + 10, _hoveredElementRect.Top + 12, 250, 96);
                PhosphorTheme.DrawPhosphorPanel(g, cardRect, PhosphorTheme.RadiusTile, PhosphorTheme.Surface, PhosphorTheme.Coral);
                using var monoFont = PhosphorFonts.Mono(9.0f);
                using var titleFont = PhosphorFonts.Ui(10.0f, heavy: true);
                using var boneBrush = new SolidBrush(PhosphorTheme.Bone);
                using var dimBrush = new SolidBrush(PhosphorTheme.Dim);
                using var coralBrush = new SolidBrush(PhosphorTheme.Coral);

                g.DrawString("WAND.POINT // ELEMENT INSPECTOR", monoFont, coralBrush, cardRect.Left + 10, cardRect.Top + 8);
                g.DrawString(_hoveredTitle, titleFont, boneBrush, cardRect.Left + 10, cardRect.Top + 26);
                g.DrawString($"CLASS: {_hoveredClass}", monoFont, dimBrush, cardRect.Left + 10, cardRect.Top + 46);
                g.DrawString($"ARCH:  {_hoveredElementRect.Width}x{_hoveredElementRect.Height}px  PALETTE: {_hoveredHex}", monoFont, boneBrush, cardRect.Left + 10, cardRect.Top + 66);

                using var swatchBrush = new SolidBrush(_hoveredPixelColor);
                var swatchRect = new Rectangle(cardRect.Right - 28, cardRect.Top + 62, 16, 16);
                using var swatchOutline = new Pen(PhosphorTheme.Bone, 1f);
                g.FillRectangle(swatchBrush, swatchRect);
                g.DrawRectangle(swatchOutline, swatchRect);
            }

            g.Clip = prevClip;
        }
        return outBmp;
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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32.POINT Point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
