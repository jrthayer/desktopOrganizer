using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Rounded-corner GDI+ paths for a hand-painted layered window's own body/title fills
/// (FenceForm, LayoutLauncherWidget) - lifted out of FenceForm's own private RoundedRect/
/// RoundedRectTop, which both classes now share instead of each keeping their own copy.</summary>
internal static class RoundedRectPath
{
    public static GraphicsPath Full(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rounded on the top two corners only, square across the bottom - for a title/header
    /// band that sits flush against the rest of a rounded body beneath it.</summary>
    public static GraphicsPath Top(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        return path;
    }
}
