using System.Text;
using DesktopTool.Native;

namespace DesktopTool.Features.Fences.UI;

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
///
/// Character input is handled entirely off WM_KEYDOWN (see TranslateChar/InsertText/DeleteBackward),
/// not the Edit control's normal WM_CHAR-driven insertion - confirmed via message tracing that in
/// this app's message loop, WM_KEYDOWN always reaches this WndProc (with correct focus/foreground
/// state) but a companion WM_CHAR never does, so typing silently did nothing even though the box
/// itself opened and Enter/Escape (both pure WM_KEYDOWN, no WM_CHAR involved) worked fine. Rather
/// than chase why TranslateMessage's generated WM_CHAR never survives to DispatchMessage here, this
/// sidesteps it: GetKeyboardState+ToUnicode reproduces what WM_CHAR would have carried directly from
/// the WM_KEYDOWN that's already reliably arriving, and every WM_CHAR is unconditionally swallowed
/// below so a system where it DOES arrive doesn't double-insert.
/// </summary>
internal sealed class EditBox : NativeWindow, IDisposable
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CHAR = 0x0102;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_GETDLGCODE = 0x0087;
    private const int WM_SETFONT = 0x0030;
    private const int WM_CUT = 0x0300;
    private const int WM_COPY = 0x0301;
    private const int WM_PASTE = 0x0302;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_BACK = 0x08;
    private const int VK_CONTROL = 0x11;

    private const int EM_GETSEL = 0x00B0;
    private const int EM_REPLACESEL = 0x00C2;
    private const uint MAPVK_VK_TO_VSC = 0;

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
            if (vk == VK_BACK) { DeleteBackward(); return; }

            // Copy/Cut/Paste/Select-All sent as their own dedicated messages rather than relying on
            // the Ctrl+C/X/V/A control characters a WM_CHAR would normally have carried - same "don't
            // depend on WM_CHAR" reasoning as TranslateChar below, and the Edit control natively
            // handles these three regardless of how they arrive.
            if ((NativeMethods.GetKeyState(VK_CONTROL) & 0x8000) != 0)
            {
                if (vk == 'C') { NativeMethods.SendMessage(Handle, WM_COPY, IntPtr.Zero, IntPtr.Zero); return; }
                if (vk == 'X') { NativeMethods.SendMessage(Handle, WM_CUT, IntPtr.Zero, IntPtr.Zero); return; }
                if (vk == 'V') { NativeMethods.SendMessage(Handle, WM_PASTE, IntPtr.Zero, IntPtr.Zero); return; }
                if (vk == 'A') { NativeMethods.SendMessage(Handle, NativeMethods.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); return; }
            }
            else if (TranslateChar(vk) is { } ch)
            {
                InsertText(ch.ToString());
                return;
            }
        }
        else if (m.Msg == WM_CHAR)
        {
            // Always swallowed - see this class's own doc comment. Character insertion already
            // happened (if at all) from the WM_KEYDOWN this followed.
            return;
        }
        else if (m.Msg == WM_KILLFOCUS)
        {
            base.WndProc(ref m);
            CommitNow();
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>What WM_CHAR would have carried for this keydown, reproduced directly from the
    /// current keyboard state via ToUnicode - null for anything that isn't a single printable
    /// character (navigation keys, dead keys still awaiting a second keystroke, Ctrl-combos already
    /// handled above, etc.).</summary>
    private static char? TranslateChar(int vk)
    {
        var keyboardState = new byte[256];
        if (!NativeMethods.GetKeyboardState(keyboardState))
            return null;

        var scanCode = NativeMethods.MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        var buffer = new StringBuilder(8);
        var result = NativeMethods.ToUnicode((uint)vk, scanCode, keyboardState, buffer, buffer.Capacity, 0);
        if (result != 1)
            return null;

        var ch = buffer[0];
        return char.IsControl(ch) ? null : ch;
    }

    private void InsertText(string text) =>
        NativeMethods.SendMessage(Handle, EM_REPLACESEL, (IntPtr)1 /* allow undo */, text);

    /// <summary>Deletes the current selection, or the single character before the caret when there
    /// isn't one - the Edit control's own default Backspace behavior, reimplemented here since it's
    /// normally WM_CHAR(0x08)-driven and this class no longer trusts WM_CHAR to arrive at all.</summary>
    private void DeleteBackward()
    {
        var selection = (long)NativeMethods.SendMessage(Handle, EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
        var start = (int)(selection & 0xFFFF);
        var end = (int)((selection >> 16) & 0xFFFF);
        if (start == end && start > 0)
            start--;

        NativeMethods.SendMessage(Handle, NativeMethods.EM_SETSEL, (IntPtr)start, (IntPtr)end);
        InsertText(string.Empty);
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
