using DesktopTool.Features.Layouts.Native;
using DesktopTool.Native;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Select Window" in the layout editor - a full-virtual-screen click-catcher, same technique as
/// Fences' EyedropperOverlay (color-keyed WS_EX_LAYERED transparency, not Form.Opacity - see that
/// class's own comment for why the color key is needed instead), that lets the user click any window
/// anywhere on screen to add it to the layout being edited, instead of hunting down its .exe by hand
/// via Browse.
/// </summary>
internal sealed class WindowPickerOverlay : Form
{
    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);

    /// <summary>Fires once, only for a window WindowPlacer.CaptureWindow can actually resolve back to
    /// a launchable, placeable entry - never for a cancelled pick (Escape/right-click). A click on
    /// something CaptureWindow rejects (this app's own windows, Explorer/shell chrome, anything whose
    /// owning process can't be read) shows an explanatory message instead of firing this.</summary>
    public event Action<LayoutEntry>? WindowPicked;

    public WindowPickerOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        Cursor = Cursors.Cross;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        KeyPreview = true;
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

    /// <summary>Hides this overlay first so WindowFromPoint can see the real window underneath
    /// instead of this (otherwise topmost at every point on screen) one, same ordering
    /// EyedropperOverlay.Confirm uses before its own GetPixel sample. GA_ROOTOWNER walks up from
    /// whatever WindowFromPoint actually hit (often a child control, e.g. a button) to the real
    /// unowned top-level window CaptureWindow expects.</summary>
    private void Confirm(Point screenPoint)
    {
        Visible = false;

        var hit = NativeMethods.WindowFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y });
        var root = hit == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOTOWNER);
        var entry = root == IntPtr.Zero ? null : WindowPlacer.CaptureWindow(root);

        if (entry is not null)
            WindowPicked?.Invoke(entry);
        else
            MessageBox.Show("That window can't be added to a layout.", "Select Window",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

        Close();
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
