using System.Drawing.Drawing2D;
using FenceTool.Native;

namespace FenceTool.Fences.UI;

/// <summary>
/// A small floating card (icon + label, styled like a fence cell) that follows the cursor during
/// an in-app item drag - see FenceForm's mouse handlers. A separate top-level window rather than
/// drawing the ghost inside the source fence's own bitmap, since that gets clipped as soon as the
/// cursor moves partway out of the fence (the ghost would visually vanish behind the fence's own
/// edge instead of following the cursor). WS_EX_TRANSPARENT makes it click-through, so it never
/// interferes with hit-testing on whatever fence (or the desktop) is underneath it.
/// </summary>
internal sealed class DragGhostWindow : Form
{
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;

    private const int CardWidth = 84;
    private const int CardHeight = 94;
    private const int IconSize = 48;
    private const int CornerRadius = 6;

    // The drop-target hint pill ("Move to Recycle Bin ->") that grows below the card - see SetHint.
    private const int HintGap = 6;
    private const int HintHeight = 26;
    private const int HintPaddingX = 10;

    private readonly Icon? _icon;
    private readonly string _label;
    private readonly Font _font = new("Segoe UI", 9f);

    private string? _hintText;
    private int _currentWidth = CardWidth;
    private int _currentHeight = CardHeight;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
            cp.ExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                         NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | 0x00000080 /* WS_EX_TOOLWINDOW */;
            cp.Width = CardWidth;
            cp.Height = CardHeight;
            return cp;
        }
    }

    public DragGhostWindow(Icon? icon, string label)
    {
        _icon = icon;
        _label = label;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        NativeMethods.SetLayeredWindowAttributes(Handle, 0, (byte)(0.85 * 255), NativeMethods.LWA_ALPHA);

        using (var path = RoundedRect(new Rectangle(0, 0, CardWidth, CardHeight), CornerRadius))
        using (var region = new Region(path))
        using (var g = Graphics.FromHwnd(Handle))
        {
            var hrgn = region.GetHrgn(g);
            NativeMethods.SetWindowRgn(Handle, hrgn, true);
        }

        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    /// <summary>Moves the ghost so its icon lands under the cursor at the given screen position.</summary>
    public void MoveTo(Point screenLocation) =>
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, screenLocation.X - 8, screenLocation.Y - 8, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>Shows/hides/updates the drop-target hint pill beneath the card - e.g. "Move to
    /// Recycle Bin ->", mirroring the tooltip Windows itself shows while dragging a file over the
    /// real desktop Recycle Bin icon. Pass null to hide it (the normal state - most drop targets
    /// don't get a hint, only ones where the effect wouldn't otherwise be obvious). Widens/heightens
    /// the actual window (not just what's drawn) since it's a shaped WS_EX_LAYERED popup with a
    /// SetWindowRgn region - text outside the current region wouldn't be click-through-transparent,
    /// it just wouldn't be part of the window at all and would never reach WM_PAINT.</summary>
    public void SetHint(string? hint)
    {
        if (hint == _hintText)
            return;
        _hintText = hint;

        var textWidth = hint is null ? 0 : TextRenderer.MeasureText(hint, _font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        _currentWidth = Math.Max(CardWidth, textWidth + HintPaddingX * 2);
        _currentHeight = CardHeight + (hint is null ? 0 : HintGap + HintHeight);

        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, _currentWidth, _currentHeight,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);

        using (var region = new Region(RoundedRect(new Rectangle(0, 0, CardWidth, CardHeight), CornerRadius)))
        {
            if (hint is not null)
            {
                using var hintPath = RoundedRect(new Rectangle(0, CardHeight + HintGap, _currentWidth, HintHeight), CornerRadius);
                region.Union(hintPath);
            }
            using var g = Graphics.FromHwnd(Handle);
            var hrgn = region.GetHrgn(g);
            NativeMethods.SetWindowRgn(Handle, hrgn, true);
        }

        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, false);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;
            case WM_PAINT:
                PaintGhost();
                return;
        }

        base.WndProc(ref m);
    }

    private void PaintGhost()
    {
        var hdc = NativeMethods.BeginPaint(Handle, out var ps);
        try
        {
            using var g = Graphics.FromHdc(hdc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using var body = RoundedRect(new Rectangle(0, 0, CardWidth - 1, CardHeight - 1), CornerRadius);
            using var bodyFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
            g.FillPath(bodyFill, body);

            if (_icon is not null)
            {
                using var iconBitmap = _icon.ToBitmap();
                g.DrawImage(iconBitmap, new Rectangle((CardWidth - IconSize) / 2, 8, IconSize, IconSize));
            }

            var labelRect = new Rectangle(0, IconSize + 10, CardWidth, CardHeight - IconSize - 10);
            TextRenderer.DrawText(g, _label, _font, labelRect, Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);

            if (_hintText is not null)
            {
                var hintRect = new Rectangle(0, CardHeight + HintGap, _currentWidth - 1, HintHeight - 1);
                using var hintPath = RoundedRect(hintRect, CornerRadius);
                using var hintFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
                g.FillPath(hintFill, hintPath);
                TextRenderer.DrawText(g, _hintText, _font, hintRect, Color.WhiteSmoke,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        finally
        {
            NativeMethods.EndPaint(Handle, ref ps);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }
}
