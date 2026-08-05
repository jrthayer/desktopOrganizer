using FenceTool.Native;

namespace FenceTool.Fences.UI;

/// <summary>
/// A full-virtual-screen click-catcher for "Fence Color > Eyedropper" - covers every monitor so a
/// color can be sampled from anywhere on screen, not just inside this app's own windows. Made
/// invisible via TransparencyKey (a WS_EX_LAYERED color-key, not WS_EX_TRANSPARENT) rather than
/// Form.Opacity: Opacity blends this window's own rendered content against the desktop by a fixed
/// percentage, which would also fade out the little preview swatch drawn below; a color key instead
/// only hides pixels painted in that exact color, leaving the swatch fully visible while everything
/// else reads through to the desktop. Unlike WS_EX_TRANSPARENT, a color-keyed window still receives
/// its own mouse/keyboard input normally, which is the whole point here.
/// </summary>
internal sealed class EyedropperOverlay : Form
{
    private const int PreviewSize = 20;
    private const int PreviewOffset = 16;

    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);

    /// <summary>Fires once, only on a confirmed left-click - never on cancel (Escape/right-click),
    /// so the caller doesn't need to distinguish "picked black" from "picked nothing".</summary>
    public event Action<Color>? ColorPicked;

    private Point _lastScreenPoint;

    public EyedropperOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        KeyPreview = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Cursor.Position rather than waiting for the first OnMouseMove, so the preview swatch is
        // already showing the color under wherever the cursor happened to be when this opened,
        // instead of a blank/stale spot until the user moves the mouse.
        _lastScreenPoint = Cursor.Position;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastScreenPoint = PointToScreen(e.Location);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
            Confirm(PointToScreen(e.Location));
        else if (e.Button == MouseButtons.Right)
            Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
    }

    /// <summary>Hides this overlay first so its own (otherwise near-invisible, but still technically
    /// present) window content can't tint the sample, then reads the real desktop pixel via GDI
    /// GetPixel against the whole-screen DC - the same technique any classic Win32 eyedropper uses,
    /// since there's no WinForms API for "what color is currently displayed at this point".</summary>
    private void Confirm(Point screenPoint)
    {
        Visible = false;
        var color = SamplePixel(screenPoint);
        ColorPicked?.Invoke(color);
        Close();
    }

    private static Color SamplePixel(Point screenPoint)
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var colorRef = NativeMethods.GetPixel(hdc, screenPoint.X, screenPoint.Y);
            return Color.FromArgb(255,
                (int)(colorRef & 0xFF),
                (int)((colorRef >> 8) & 0xFF),
                (int)((colorRef >> 16) & 0xFF));
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>Live preview swatch that follows the cursor - sampled with this overlay still
    /// visible, so (unlike Confirm's sample) it can pick up a faint tint from this window's own
    /// near-invisible presence. Close enough for a live preview; only the confirmed pick needs to be
    /// exact.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var previewColor = SamplePixel(_lastScreenPoint);
        var local = PointToClient(_lastScreenPoint);
        var rect = new Rectangle(local.X + PreviewOffset, local.Y + PreviewOffset, PreviewSize, PreviewSize);

        using (var fill = new SolidBrush(previewColor))
            e.Graphics.FillEllipse(fill, rect);
        using var pen = new Pen(Color.White, 2f);
        e.Graphics.DrawEllipse(pen, rect);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab
            return cp;
        }
    }
}
