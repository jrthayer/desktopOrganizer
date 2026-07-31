namespace FenceTool.Native;

/// <summary>
/// SetParent's a fence window onto the Progman/WorkerW window that hosts the desktop's icon
/// view, then places it immediately behind SHELLDLL_DefView in z-order - the same technique
/// native desktop-widget tools like Rainmeter use to sit "behind icons, above wallpaper".
///
/// NOT CURRENTLY ACTIVE. The SetParent mechanics themselves work correctly here (verified via
/// GetAncestor(hwnd, GA_PARENT), which is the reliable way to check this - plain GetParent is
/// documented as unreliable for WS_POPUP windows and gave false negatives during development).
/// The blocker is more fundamental: once genuinely placed behind SHELLDLL_DefView, the fence
/// renders as completely invisible - even in desktop regions with no icons at all, regardless of
/// WS_EX_LAYERED - indicating the icon view paints an opaque background across its entire area
/// rather than leaving transparent gaps for whatever is behind it to show through. A right-click
/// at the fence's location also produced no menu at all (neither the fence's nor the desktop's),
/// suggesting the icon view - sitting in front - also swallows mouse input there. So embedding a
/// window behind it this way risks making the fence both invisible and unclickable at once, which
/// defeats the purpose. FenceManager uses FloatingDesktopAnchorStrategy instead. This class is
/// kept in case a future Windows version's icon view behaves differently, but achieving the
/// actual "sits behind icons" look likely needs a different mechanism entirely (e.g. hooking the
/// shell's own drag-drop notifications) rather than a plain sibling-window z-order trick.
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
