using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Omniwand;

/// <summary>
/// Records what the cursor overlay actually draws, for the control pipe's <c>cursor:film:&lt;dir&gt;|&lt;seconds&gt;</c>.
/// Every render-loop frame the overlay is not suppressed writes one line to <c>frames.tsv</c> (role, detection, angle, whether the overlay
/// redrew, and the morph engine's pose); every redraw saves the overlay bitmap's centre as a PNG, at most one per
/// 1/60 s. Files are written on a background thread so the 240 Hz loop never waits on the disk.
/// </summary>
internal static class CursorFilm
{
    private const int CropSize = 200;
    private const double MinFrameGapSec = 1.0 / 60.0;

    private static readonly BlockingCollection<Action> Writes = new();
    private static volatile bool _active;
    private static double _startSec = -1;
    private static double _endSec;
    private static double _lastFrameSec;
    private static int _frame;
    private static string _dir = string.Empty;
    private static StreamWriter? _log;

    /// <summary>The role detector's last raw reading, set by the render loop at each role poll.</summary>
    public static string Detection { get; set; } = string.Empty;

    public static bool Active => _active;

    static CursorFilm()
    {
        new Thread(() =>
        {
            foreach (var write in Writes.GetConsumingEnumerable())
            {
                try { write(); } catch { }
            }
        }) { IsBackground = true, Name = "OmniwandCursorFilm" }.Start();
    }

    public static void Begin(string dir, double seconds)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _frame = 0;
        _lastFrameSec = double.NegativeInfinity;
        _startSec = -1;
        _endSec = seconds;
        var log = new StreamWriter(Path.Combine(dir, "frames.tsv"), false);
        log.WriteLine("t_ms\trole\tdetection\tangle\tredrew\tframe\tengine");
        Writes.Add(() => { _log?.Dispose(); _log = log; });
        _active = true;
    }

    /// <summary>Called by the overlay after each RenderAndMove. <paramref name="dib"/> is the bitmap it just drew.</summary>
    public static void Record(double nowSec, WindowsCursorRole role, float angleDeg, bool redrew, Bitmap dib)
    {
        if (!_active) return;
        if (_startSec < 0) _startSec = nowSec;
        double t = nowSec - _startSec;
        if (t > _endSec)
        {
            _active = false;
            Writes.Add(() => { _log?.Dispose(); _log = null; });
            return;
        }

        string frameName = string.Empty;
        if (redrew && nowSec - _lastFrameSec >= MinFrameGapSec)
        {
            _lastFrameSec = nowSec;
            frameName = $"f{_frame++:D4}.png";
            var crop = new Rectangle((dib.Width - CropSize) / 2, (dib.Height - CropSize) / 2, CropSize, CropSize);
            var copy = dib.Clone(crop, PixelFormat.Format32bppArgb);
            string path = Path.Combine(_dir, frameName);
            Writes.Add(() => { using (copy) copy.Save(path, ImageFormat.Png); });
        }

        string line = string.Join('\t',
            (t * 1000).ToString("0.0", CultureInfo.InvariantCulture),
            role,
            Detection,
            angleDeg.ToString("0.0", CultureInfo.InvariantCulture),
            redrew ? "1" : "0",
            frameName,
            ArkApertureMorphEngine.Describe());
        Writes.Add(() => _log?.WriteLine(line));
    }
}
