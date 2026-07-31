using System.Text;
using FenceTool.Native;

namespace FenceTool.UI;

/// <summary>
/// Thin wrapper around a native Win32 "Edit" control, subclassed (via NativeWindow.AssignHandle) to
/// intercept Enter (commit) and Escape (cancel) - used for the fence rename textbox since FenceForm
/// is a raw window with no WinForms Controls collection to host a real TextBox in.
///
/// A top-level WS_POPUP window owned by the fence, positioned in screen coordinates - NOT a
/// WS_CHILD of the fence, even though visually it sits "inside" it. FenceForm paints itself via
/// UpdateLayeredWindow (see LayeredWindowPresenter), and a layered window updated that way does not
/// reliably composite child windows on top of its surface - the child still exists and can take
/// focus, but never visually appears. DragGhostWindow works around the same limitation the same way
/// (a separate top-level window rather than a child).
/// </summary>
internal sealed class EditBox : NativeWindow, IDisposable
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CHAR = 0x0102;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_GETDLGCODE = 0x0087;
    private const int WM_SETFONT = 0x0030;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;

    private readonly IntPtr _hFont;

    // Without this, WinForms' own Application.Run message loop (which does IsDialogMessage-style
    // preprocessing for Enter/Escape/Tab on every window) swallows the Enter keydown before it
    // ever reaches this WndProc, since a plain "Edit" control's default WM_GETDLGCODE response
    // doesn't claim it wants that key - confirmed via message tracing (WM_KEYUP for Enter arrived
    // here, but WM_KEYDOWN never did, until this fix).
    private const int DLGC_WANTALLKEYS = 0x0004;
    private const int DLGC_HASSETSEL = 0x0008;
    private const int DLGC_WANTCHARS = 0x0080;

    // Guards against firing Commit/Cancel twice: destroying the edit control while it has focus
    // synchronously sends it WM_KILLFOCUS, which would otherwise re-enter and commit a cancelled
    // edit (or double-commit) - same reentrancy hazard as the original WinForms TextBox version.
    private bool _finished;

    public event Action<string>? Commit;
    public event Action? Cancel;

    /// <summary>bounds is in SCREEN coordinates (not parent-client-relative) - this is a top-level
    /// window, not a child, so that's what CreateWindowEx's x/y expect. Callers position it via
    /// Control.PointToScreen on whatever window-relative rect the fence itself would have used.
    /// font is applied via WM_SETFONT so the box's text matches the fence's own font instead of the
    /// default system dialog font; colors are handled separately by the owner responding to
    /// WM_CTLCOLOREDIT (a plain Edit control has no owner-draw hook of its own for that).</summary>
    public EditBox(IntPtr owner, string initialText, Rectangle bounds, Font font)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0x00000080 /* WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab */,
            "Edit", initialText,
            NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_BORDER | NativeMethods.ES_AUTOHSCROLL,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        AssignHandle(hwnd);
        _hFont = font.ToHfont();
        NativeMethods.SendMessage(hwnd, WM_SETFONT, _hFont, (IntPtr)1);
        NativeMethods.SetFocus(hwnd);
        NativeMethods.SendMessage(hwnd, NativeMethods.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
    }

    public void Resize(int width) =>
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, width, 20,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GETDLGCODE)
        {
            m.Result = (IntPtr)(DLGC_WANTALLKEYS | DLGC_HASSETSEL | DLGC_WANTCHARS);
            return;
        }

        if (m.Msg == WM_KEYDOWN)
        {
            var vk = m.WParam.ToInt32();
            if (vk == VK_RETURN) { CommitNow(); return; }
            if (vk == VK_ESCAPE) { CancelNow(); return; }
        }
        else if (m.Msg == WM_CHAR)
        {
            // Swallow the WM_CHAR that follows Enter/Escape's WM_KEYDOWN so the edit control
            // doesn't do anything with those characters (already fully handled above).
            var ch = m.WParam.ToInt32();
            if (ch == VK_RETURN || ch == VK_ESCAPE) return;
        }
        else if (m.Msg == WM_KILLFOCUS)
        {
            base.WndProc(ref m);
            CommitNow();
            return;
        }

        base.WndProc(ref m);
    }

    private void CommitNow()
    {
        if (_finished)
            return;
        _finished = true;
        Commit?.Invoke(GetText());
    }

    private void CancelNow()
    {
        if (_finished)
            return;
        _finished = true;
        Cancel?.Invoke();
    }

    private string GetText()
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(Handle, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            DestroyHandle();
        if (_hFont != IntPtr.Zero)
            NativeMethods.DeleteObject(_hFont);
    }
}
