using System.Drawing.Drawing2D;
using FenceTool.Fences;
using FenceTool.Native;

namespace FenceTool.UI;

/// <summary>
/// A real WinForms Form (custom-drawn, WS_POPUP, no native chrome) rather than a raw NativeWindow.
/// An earlier version used a raw NativeWindow to avoid Form/Control fighting SetParent-based
/// z-order embedding onto Progman/WorkerW - but that embedding strategy is currently disabled
/// (see FenceManager, which uses FloatingDesktopAnchorStrategy instead) so that concern doesn't
/// apply right now. Being a real Form matters for a different reason: drag-and-drop needs to
/// register as an OLE drop target, and a hand-rolled P/Invoke RegisterDragDrop/IDropTarget CCW
/// turned out not to reliably receive DragEnter/Drop callbacks, while WinForms' own
/// AllowDrop/OnDragEnter/OnDragDrop machinery does.
///
/// A fence owns its contents as a plain list of file paths (FenceModel.Files) and draws its own
/// icon+label for each one (PaintItems) - the same approach used by NoFences
/// (https://github.com/Twometer/NoFences), an open-source Stardock Fences alternative this app's
/// drag-and-drop model is based on (see README's Credits section). It never touches the real
/// desktop's icons/positions; dropping a file here just adds a reference to it, leaving whatever
/// is on the actual desktop completely alone.
/// </summary>
public sealed class FenceForm : Form
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
    private const int CmdDelete = 3;
    private const int CmdOpenItem = 4;
    private const int CmdRemoveItem = 5;
    private const int CmdRenameItem = 6;

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;

    private readonly FenceManager _manager;
    private readonly FenceModel _model;
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Dictionary<string, Icon?> _iconCache = new();
    private EditBox? _renameBox;
    private EditBox? _itemRenameBox;
    private string? _itemRenamePath;
    private string? _contextItem;
    private int _hoverIndex = -1;

    public Guid FenceId => _model.Id;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Control's base constructor probes CreateParams before our own constructor body has
            // run (so _model is still null at that point) - the real, model-driven CreateParams
            // request comes later, when the constructor body first touches Handle.
            if (_model is null)
                return cp;

            // WS_CLIPCHILDREN is essential: without it, our own WM_PAINT full-repaint draws
            // over the rename EditBox child window instead of leaving its area alone.
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = _model.Bounds.X;
            cp.Y = _model.Bounds.Y;
            cp.Width = _model.Bounds.Width;
            cp.Height = _model.Bounds.Height;
            return cp;
        }
    }

    public FenceForm(FenceModel model, FenceManager manager, IDesktopAnchorStrategy anchorStrategy)
    {
        _model = model;
        _manager = manager;
        _anchorStrategy = anchorStrategy;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;

        NativeMethods.SetLayeredWindowAttributes(Handle, 0, (byte)(0.85 * 255), NativeMethods.LWA_ALPHA);
        ApplyRoundedRegion(model.Bounds.Width, model.Bounds.Height);
        Reanchor();
    }

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    /// <summary>Re-applies the desktop anchor (e.g. after explorer.exe restarts or a display
    /// change invalidates the previous z-order/parenting). Uses _model.Bounds (our own tracked
    /// absolute screen position), which is authoritative regardless of whatever coordinate
    /// convention the current native parent implies.</summary>
    public void Reanchor() => _anchorStrategy.Apply(Handle, _model.Bounds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renameBox?.Dispose();
            _itemRenameBox?.Dispose();
            _font.Dispose();
            foreach (var icon in _iconCache.Values)
                icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Move;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        _manager.AddFiles(FenceId, paths);
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        OpenItem(FileAtGridPosition(e.Location));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHoverIndex(IndexAtGridPosition(e.Location) ?? -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
    }

    private void SetHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
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
                m.Result = (IntPtr)1; // PaintFence() always fills the whole client area; avoids flicker
                return;

            case WM_PAINT:
                PaintFence();
                return;

            case WM_RBUTTONUP:
                var clientPoint = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                ShowContextMenu(clientPoint);
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

                // Without this, only the newly-exposed strip gets a fresh WM_PAINT, leaving
                // stale copies of the custom-painted border behind as the window resizes - the
                // raw-window equivalent of WinForms' ControlStyles.ResizeRedraw, which doesn't
                // apply here since this is no longer a WinForms Control.
                NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
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

    private void PaintFence()
    {
        var hdc = NativeMethods.BeginPaint(Handle, out var ps);
        try
        {
            NativeMethods.GetClientRect(Handle, out var clientRect);
            int width = clientRect.Right;
            int height = clientRect.Bottom;

            using var g = Graphics.FromHdc(hdc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // DrawIcon's native GDI stretch looks jagged when scaling a source icon down to
            // IconSize - drawing icons as bitmaps under high-quality interpolation instead avoids that.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

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

            PaintItems(g, width);
        }
        finally
        {
            NativeMethods.EndPaint(Handle, ref ps);
        }
    }

    /// <summary>
    /// Draws this fence's own icon+label for each file it holds, in a simple grid below the title
    /// bar - the fence never touches the real desktop icons (see FenceManager.AddFiles), so this is
    /// the only place those files are actually represented on screen.
    /// </summary>
    private void PaintItems(Graphics g, int width)
    {
        if (_model.Files.Count == 0)
            return;

        var columns = Math.Max(1, (width - GridPadding * 2) / CellWidth);

        for (int i = 0; i < _model.Files.Count; i++)
        {
            var item = _model.Files[i];
            var column = i % columns;
            var row = i / columns;
            var cellX = GridPadding + column * CellWidth;
            var cellY = TitleBarHeight + GridPadding + row * CellHeight;

            if (i == _hoverIndex)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var hoverRect = RoundedRect(new Rectangle(cellX, cellY, CellWidth, CellHeight), 4);
                g.FillPath(hoverBrush, hoverRect);
            }

            var icon = GetIcon(item.Path);
            if (icon is not null)
            {
                var iconX = cellX + (CellWidth - IconSize) / 2;
                using var bitmap = icon.ToBitmap();
                g.DrawImage(bitmap, new Rectangle(iconX, cellY + IconTopPadding, IconSize, IconSize));
            }

            if (item.Path == _itemRenamePath)
                continue;

            var labelRect = new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, CellHeight - IconTopPadding - IconSize - 2);
            TextRenderer.DrawText(g, GetDisplayName(item), _font, labelRect, Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
        }
    }

    /// <summary>An explicit rename (set via the item's context menu) always wins; otherwise
    /// shortcuts display without their .lnk extension, matching how Explorer shows them on the
    /// real desktop, and other files keep their extension.</summary>
    private static string GetDisplayName(FenceItem item)
    {
        if (!string.IsNullOrEmpty(item.DisplayName))
            return item.DisplayName;

        return string.Equals(Path.GetExtension(item.Path), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(item.Path)
            : Path.GetFileName(item.Path);
    }

    private Icon? GetIcon(string path)
    {
        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        Icon? icon = null;
        try
        {
            // The shell's large image list gives a genuinely high-resolution icon (crisp at
            // IconSize) rather than the ~32px one Icon.ExtractAssociatedIcon returns, which looks
            // blurry once drawn at a larger size - only fall back to it if the shell lookup fails.
            icon = ShellIcons.ExtractLargeIcon(path) ?? Icon.ExtractAssociatedIcon(path);
        }
        catch (IOException)
        {
            // File may have been moved/deleted since it was dropped here.
        }
        catch (System.Security.SecurityException)
        {
        }

        _iconCache[path] = icon;
        return icon;
    }

    private string? FileAtGridPosition(Point clientLocation)
    {
        var index = IndexAtGridPosition(clientLocation);
        return index is int i ? _model.Files[i].Path : null;
    }

    private int? IndexAtGridPosition(Point clientLocation)
    {
        if (_model.Files.Count == 0 || clientLocation.Y < TitleBarHeight)
            return null;

        NativeMethods.GetClientRect(Handle, out var clientRect);
        var columns = Math.Max(1, (clientRect.Right - GridPadding * 2) / CellWidth);

        var column = (clientLocation.X - GridPadding) / CellWidth;
        var row = (clientLocation.Y - TitleBarHeight - GridPadding) / CellHeight;
        if (column < 0 || column >= columns || row < 0)
            return null;

        var index = row * columns + column;
        return index >= 0 && index < _model.Files.Count ? index : null;
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

    private void ShowContextMenu(Point clientPoint)
    {
        _contextItem = FileAtGridPosition(clientPoint);
        NativeMethods.GetCursorPos(out var pt);

        var hMenu = NativeMethods.CreatePopupMenu();
        try
        {
            if (_contextItem is not null)
            {
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdOpenItem, "Open");
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdRenameItem, "Rename");
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdRemoveItem, "Remove From Fence");
            }
            else
            {
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdRename, "Rename");
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)CmdDelete, "Delete Fence");
            }

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
            case CmdDelete: ConfirmDelete(); break;
            case CmdOpenItem: OpenItem(_contextItem); break;
            case CmdRemoveItem: RemoveItem(_contextItem); break;
            case CmdRenameItem: BeginRenameItem(_contextItem); break;
        }
    }

    private void OpenItem(string? path)
    {
        if (path is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The file may have been moved/deleted since it was dropped here - nothing to do.
        }
    }

    private void RemoveItem(string? path)
    {
        if (path is null)
            return;

        _manager.RemoveFile(FenceId, path);
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
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

    private void BeginRenameItem(string? path)
    {
        if (path is null || _itemRenameBox is not null)
            return;

        var index = _model.Files.FindIndex(f => f.Path == path);
        if (index < 0 || !NativeMethods.GetClientRect(Handle, out var clientRect))
            return;

        var columns = Math.Max(1, (clientRect.Right - GridPadding * 2) / CellWidth);
        var column = index % columns;
        var row = index / columns;
        var cellX = GridPadding + column * CellWidth;
        var cellY = TitleBarHeight + GridPadding + row * CellHeight;
        var labelRect = new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, 20);

        _itemRenamePath = path;
        _itemRenameBox = new EditBox(Handle, GetDisplayName(_model.Files[index]), labelRect);
        _itemRenameBox.Commit += OnItemRenameCommit;
        _itemRenameBox.Cancel += OnItemRenameCancel;
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    private void OnItemRenameCommit(string newName)
    {
        _itemRenameBox?.Dispose();
        _itemRenameBox = null;
        var path = _itemRenamePath;
        _itemRenamePath = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && path is not null)
            _manager.RenameFile(FenceId, path, newName);

        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    private void OnItemRenameCancel()
    {
        _itemRenameBox?.Dispose();
        _itemRenameBox = null;
        _itemRenamePath = null;
        NativeMethods.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    private void ConfirmDelete()
    {
        var result = MessageBox.Show(this,
            $"Delete fence \"{_model.Name}\"? The files inside it won't be deleted.",
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
}
