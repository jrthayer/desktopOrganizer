using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Hand-drawn triangle-with-"!" caution icon, shared by Layout Launcher's row error badge
/// and Manage Layouts' own profile list warning (see LayoutLauncherWidget.PaintErrorIcon and
/// LayoutEditorForm.DrawProfileListItem) - both used to render the Unicode "⚠" character via
/// TextRenderer, which showed up as a garbled/missing-glyph box in some fonts. Drawn with plain GDI+
/// primitives instead, the same "no icon asset library, just draw the shape" approach every other
/// glyph in this app already uses (see LayoutLauncherWidget's PaintCopyGlyph/PaintDeleteGlyph).</summary>
internal static class WarningIcon
{
    /// <summary>Colored to match the caller's own surrounding text (not a fixed warning tint) so it
    /// reads as part of the row instead of a differently-styled sticker.</summary>
    public static void Paint(Graphics g, Rectangle rect, Color color)
    {
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var size = Math.Min(rect.Width, rect.Height) * 0.8f;
        var halfWidth = size * 0.55f;
        var top = cy - size * 0.5f;
        var bottom = cy + size * 0.5f;

        var previousSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = new GraphicsPath())
        {
            path.AddPolygon(new[]
            {
                new PointF(cx, top),
                new PointF(cx + halfWidth, bottom),
                new PointF(cx - halfWidth, bottom),
            });
            using var outlinePen = new Pen(color, 1.3f) { LineJoin = LineJoin.Round };
            g.DrawPath(outlinePen, path);
        }

        using (var markPen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(markPen, cx, top + size * 0.34f, cx, top + size * 0.64f);
        using (var dotBrush = new SolidBrush(color))
            g.FillEllipse(dotBrush, cx - 1f, top + size * 0.74f, 2f, 2f);

        g.SmoothingMode = previousSmoothing;
    }
}
