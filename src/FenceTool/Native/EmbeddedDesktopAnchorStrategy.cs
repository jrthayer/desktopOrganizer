namespace FenceTool.Native;

/// <summary>
/// SetParent's a fence window onto the Progman/WorkerW window that hosts the desktop's icon
/// view, then places it immediately behind SHELLDLL_DefView in z-order. Because the icon
/// ListView paints no background, this is meant to let the fence's own translucent rectangle
/// show through in the gaps between icons while the icon glyphs/labels stay drawn on top - the
/// "sits behind icons, above wallpaper" look, same technique native desktop-widget tools like
/// Rainmeter use.
///
/// NOT CURRENTLY ACTIVE: confirmed empirically that WinForms actively reasserts its "top-level
/// forms have no Win32 parent" invariant and undoes the SetParent here almost immediately, even
/// well after the message loop is running normally. Achieving this for real would need the fence
/// window to be a raw Win32 window instead of a System.Windows.Forms.Form. FenceManager currently
/// uses FloatingDesktopAnchorStrategy instead; this class is kept for whenever that rewrite happens.
/// </summary>
public sealed class EmbeddedDesktopAnchorStrategy : IDesktopAnchorStrategy
{
    private readonly DesktopListView _desktopListView;

    public EmbeddedDesktopAnchorStrategy(DesktopListView desktopListView)
    {
        _desktopListView = desktopListView;
    }

    public void Apply(IntPtr formHandle, Rectangle desiredScreenBounds)
    {
        if (!_desktopListView.EnsureDiscovered())
            return;

        NativeMethods.SetParent(formHandle, _desktopListView.AnchorHandle);

        // SetParent doesn't reliably preserve on-screen position across the reparent, so the
        // desired absolute screen position is translated into the new parent's coordinate space
        // explicitly rather than relying on the window "staying put".
        if (!NativeMethods.GetWindowRect(_desktopListView.AnchorHandle, out var anchorRect))
            return;

        var relativeX = desiredScreenBounds.X - anchorRect.Left;
        var relativeY = desiredScreenBounds.Y - anchorRect.Top;

        NativeMethods.SetWindowPos(formHandle, _desktopListView.DefViewHandle,
            relativeX, relativeY, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
