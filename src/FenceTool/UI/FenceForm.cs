using System.Drawing.Drawing2D;
using FenceTool.Fences;
using FenceTool.Native;

namespace FenceTool.UI;

/// <summary>
/// A raw Win32 window (via NativeWindow) rather than a WinForms Form or Control - both were found
/// to fight or fail at genuinely being reparented onto Progman/WorkerW via SetParent (Form actively
/// reasserts a "top-level forms have no Win32 parent" invariant; a plain Control's reparenting was
/// observed to silently revert too, for reasons that weren't fully pinned down). NativeWindow has
/// none of that machinery, so the anchor strategy's SetParent call has nothing working against it.
/// </summary>
public sealed class FenceForm : NativeWindow, IDisposable
{
    internal const int TitleBarHeight = 26;
    private const int ResizeMargin = 6;
    private const int CornerRadius = 10;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_SIZE = 0x0005;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int CmdRename = 1;
    private const int CmdArrange = 2;
    private const int CmdDelete = 3;

    private readonly FenceManager _manager;
    private readonly FenceModel _model;
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly Font _font = new("Segoe UI", 9f);
    private EditBox? _renameBox;

    public Guid FenceId => _model.Id;

    public FenceForm(FenceModel model, FenceManager manager, IDesktopAnchorStrategy anchorStrategy)
    {
        _model = model;
        _manager = manager;
        _anchorStrategy = anchorStrategy;

        var cp = new CreateParams
        {
            // WS_CLIPCHILDREN is essential: without it, our own WM_PAINT full-repaint draws
            // over the rename EditBox child window instead of leaving its area alone.
            Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN,
            ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED,
            X = model.Bounds.X,
            Y = model.Bounds.Y,
            Width = model.Bounds.Width,
            Height = model.Bounds.Height,
        };
        CreateHandle(cp);

        NativeMethods.SetLayeredWindowAttributes(Handle, 0, (byte)(0.85 * 255), NativeMethods.LWA_ALPHA);
        ApplyRoundedRegion(model.Bounds.Width, model.Bounds.Height);
        Reanchor();
    }

    public void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    /// <summary>Re-applies the desktop anchor (e.g. after explorer.exe restarts or a display
    /// change invalidates the previous z-order/parenting). Uses _model.Bounds (our own tracked
    /// absolute screen position), which is authoritative regardless of whatever coordinate
    /// convention the current native parent implies.</summary>
    public void Reanchor() => _anchorStrategy.Apply(Handle, _model.Bounds);

    public void Dispose()
    {
        _renameBox?.Dispose();
        _font.Dispose();
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                m.Result = (IntPtr)HitTest(m.LParam);
                return;

            case WM_NCLBUTTONDBLCLK:
                // HitTest reports HTCAPTION for the title bar, so a double-click there arrives as
                // this non-client message. Letting the default proc handle it would maximize the
                // window (the OS's standard double-click-caption behavior) - rename here instead.
                BeginRename();
                return;

            case WM_ERASEBKGND:
                m.Result = (IntPtr)1; // Paint() always fills the whole client area; avoids flicker
                return;

            case WM_PAINT:
                Paint();
                return;

            case WM_RBUTTONUP:
                ShowContextMenu();
                return;

            case WM_COMMAND:
                HandleCommand(m.WParam.ToInt32() & 0xFFFF);
                return;
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_SIZE:
                var lParam = m.LParam.ToInt64();
                var width = (int)(lParam & 0xFFFF);
                var height = (int)((lParam >> 16) & 0xFFFF);
                ApplyRoundedRegion(width, height);
                _renameBox?.Resize(Math.Max(width - 12, 0));
                break;

            case WM_EXITSIZEMOVE:
                if (NativeMethods.GetWindowRect(Handle, out var rect))
                    _manager.NotifyBoundsChanged(FenceId, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                Reanchor();
                break;
        }
    }

    private int HitTest(IntPtr lParam)
    {
        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);

        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        int x = screenX - rect.Left;
        int y = screenY - rect.Top;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        bool left = x <= ResizeMargin;
        bool right = x >= width - ResizeMargin;
        bool top = y <= ResizeMargin;
        bool bottom = y >= height - ResizeMargin;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        if (y <= TitleBarHeight) return HTCAPTION;
        return HTCLIENT;
    }

    private void Paint()
    {
        var hdc = NativeMethods.BeginPaint(Handle, out var ps);
        try
        {
            NativeMethods.GetClientRect(Handle, out var clientRect);
            int width = clientRect.Right;
            int height = clientRect.Bottom;

            using var g = Graphics.FromHdc(hdc);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var body = RoundedRect(new Rectangle(0, 0, width - 1, height - 1), CornerRadius);
            using var bodyFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
            g.FillPath(bodyFill, body);

            using var titleFill = new SolidBrush(Color.FromArgb(255, 20, 20, 24));
            using var titlePath = RoundedRectTop(new Rectangle(0, 0, width - 1, TitleBarHeight), CornerRadius);
            g.FillPath(titleFill, titlePath);

            using var borderPen = new Pen(Color.FromArgb(255, 70, 70, 78));
            g.DrawPath(borderPen, body);

            if (_renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Name, _font, new Rectangle(8, 0, width - 16, TitleBarHeight),
                    Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
        finally
        {
            NativeMethods.EndPaint(Handle, ref ps);
        }
    }

    private void ApplyRoundedRegion(int width, int height)
    {
        using var path = RoundedRect(new Rectangle(0, 0, width, height), CornerRadius);
        using var region = new Region(path);
        using var g = Graphics.FromHwnd(Handle);
        var hrgn = region.GetHrgn(g);
        // SetWindowRgn takes ownership of hrgn - it must not be deleted/released afterward.
        NativeMethods.SetWindowRgn(Handle, hrgn, true);
    }

    private void ShowContextMenu()
    {
        NativeMethods.GetCursorPos(out var pt);

        var hMenu = NativeMethods.CreatePopupMenu();
        try
        {
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdRename, "Rename");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdArrange, "Arrange Icons Now");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdDelete, "Delete Fence");

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdRename: BeginRename(); break;
            case CmdArrange: _manager.ArrangeFence(FenceId); break;
            case CmdDelete: ConfirmDelete(); break;
        }
    }

    private void BeginRename()
    {
        if (_renameBox is not null)
            return;

        if (!NativeMethods.GetClientRect(Handle, out var clientRect))
            return;

        _renameBox = new EditBox(Handle, _model.Name, new Rectangle(6, 3, Math.Max(clientRect.Right - 12, 0), 20));
        _renameBox.Commit += OnRenameCommit;
        _renameBox.Cancel += OnRenameCancel;
    }

    private void OnRenameCommit(string newName)
    {
        _renameBox?.Dispose();
        _renameBox = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != _model.Name)
            _manager.NotifyRenamed(FenceId, newName);

        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    private void OnRenameCancel()
    {
        _renameBox?.Dispose();
        _renameBox = null;
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    private void ConfirmDelete()
    {
        var result = MessageBox.Show(new Win32Window(Handle),
            $"Delete fence \"{_model.Name}\"? Icons inside it will remain on the desktop.",
            "Delete Fence", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
            _manager.DeleteFence(FenceId);
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

    private static GraphicsPath RoundedRectTop(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        return path;
    }

    private sealed class Win32Window : IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
