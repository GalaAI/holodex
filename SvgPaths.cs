using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Svg;
using Svg.Pathing;

namespace Omniwand;

/// <summary>
/// The one SVG path reader: SVG.NET (NuGet Svg) parses full path data (absolute and relative commands, H/V, arcs
/// with packed flags) into a GraphicsPath. The ArkMark emblem geometry and the ArkStation lucide icons both read
/// through it. A renderer is passed because SVG.NET's Path(null) appends circles at open path ends to estimate
/// bounds; with a renderer the path is the path alone.
/// </summary>
internal static class SvgPaths
{
    private static readonly object Gate = new();
    private static readonly Bitmap Probe = new(1, 1);
    private static readonly Graphics ProbeGraphics = Graphics.FromImage(Probe);
    private static readonly ISvgRenderer Renderer = SvgRenderer.FromGraphics(ProbeGraphics);

    /// <summary>A new path the caller owns, in SVG user units, with even-odd fill unless told otherwise.</summary>
    public static GraphicsPath Parse(string d, FillMode fill = FillMode.Alternate)
    {
        lock (Gate)
        {
            var element = new SvgPath { PathData = SvgPathBuilder.Parse(d.AsSpan()) };
            var path = (GraphicsPath)element.Path(Renderer).Clone();
            path.FillMode = fill;
            return path;
        }
    }
}
