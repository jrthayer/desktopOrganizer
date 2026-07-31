using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
///
/// Rendering is pushed via UpdateLayeredWindow (see LayeredWindowPresenter) rather than drawn in
/// response to WM_PAINT with a SetWindowRgn-clipped shape. The region approach was tried first and
/// works, but a GDI region is a hard-edged, non-antialiased mask, so the rounded corners always
/// came out as a visible pixel staircase no matter the radius. Per-pixel alpha draws a genuinely
/// smooth edge, and Windows uses that same alpha for hit-testing, so fully-transparent pixels
/// (outside the rounded corner) are naturally click-through with no region needed at all.
/// </summary>
public sealed class FenceForm : Form
{
    internal const int TitleBarHeight = 26;
    private const int ResizeMargin = 12;
    // Extra invisible band around the visible fence, purely so the resize cursor is easier to
    // grab - only possible now that per-pixel alpha (not SetWindowRgn) defines the window's shape,
    // since Windows treats fully-transparent pixels as click-through; a hard region couldn't do
    // this at all (you can't hit-test past a window's own rectangle). Painted at a barely-non-zero
    // alpha (see RenderAndPresent) since alpha 0 would be click-through too, defeating the point.
    private const int OuterMargin = 8;
    private const int CornerRadius = 16;
    private const float FenceOpacity = 0.85f;

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

    // Internal drag state for reordering/removing items - this is all local mouse tracking, not
    // OLE drag-and-drop (which is only for accepting drops from outside the app, via
    // OnDragEnter/OnDragDrop above). "Armed" means the mouse is down on an item but hasn't moved
    // far enough yet to count as a drag rather than a click.
    private const int DragThreshold = 4;
    private int? _dragArmIndex;
    private Point _dragArmPoint;
    private int? _draggingIndex;
    private Point _dragCurrentPoint;
    private DragGhostWindow? _dragGhost;

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

            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = _model.Bounds.X - OuterMargin;
            cp.Y = _model.Bounds.Y - OuterMargin;
            cp.Width = _model.Bounds.Width + OuterMargin * 2;
            cp.Height = _model.Bounds.Height + OuterMargin * 2;
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

        Reanchor();
        RenderAndPresent();
    }

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    /// <summary>Re-applies the desktop anchor (e.g. after explorer.exe restarts or a display
    /// change invalidates the previous z-order/parenting). Uses _model.Bounds (our own tracked
    /// absolute screen position), which is authoritative regardless of whatever coordinate
    /// convention the current native parent implies.</summary>
    public void Reanchor() => _anchorStrategy.Apply(Handle, _model.Bounds);

    /// <summary>The visible fence's size, i.e. the actual (padded) window size minus OuterMargin
    /// on all sides - all grid/hit-test math below is in this "content" space.</summary>
    private Size GetContentSize()
    {
        NativeMethods.GetClientRect(Handle, out var clientRect);
        return new Size(Math.Max(0, clientRect.Right - OuterMargin * 2), Math.Max(0, clientRect.Bottom - OuterMargin * 2));
    }

    private static Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - OuterMargin);

    private static Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + OuterMargin, contentRect.Width, contentRect.Height);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renameBox?.Dispose();
            _itemRenameBox?.Dispose();
            _dragGhost?.Dispose();
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
        RenderAndPresent();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        OpenItem(FileAtGridPosition(ToContent(e.Location)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (IndexAtGridPosition(ToContent(e.Location)) is int index)
        {
            _dragArmIndex = index;
            _dragArmPoint = e.Location; // raw window-space is fine here - only ever used as a delta
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_draggingIndex is null && _dragArmIndex is int armIndex && MouseButtons == MouseButtons.Left)
        {
            var dx = e.X - _dragArmPoint.X;
            var dy = e.Y - _dragArmPoint.Y;
            if (dx * dx + dy * dy >= DragThreshold * DragThreshold)
            {
                _draggingIndex = armIndex;
                _dragArmIndex = null;
                Capture = true;

                var item = _model.Files[armIndex];
                _dragGhost = new DragGhostWindow(GetIcon(item.Path), GetDisplayName(item));
            }
        }

        if (_draggingIndex is not null)
        {
            _dragCurrentPoint = ToContent(e.Location);
            _dragGhost?.MoveTo(PointToScreen(e.Location));
            RenderAndPresent();
            return;
        }

        SetHoverIndex(IndexAtGridPosition(ToContent(e.Location)) ?? -1);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        _dragArmIndex = null;
        if (_draggingIndex is not int sourceIndex)
            return;

        Capture = false;
        _draggingIndex = null;
        _dragGhost?.Dispose();
        _dragGhost = null;

        var contentPoint = ToContent(e.Location);
        var path = _model.Files[sourceIndex].Path;
        if (new Rectangle(Point.Empty, GetContentSize()).Contains(contentPoint))
            _manager.MoveFile(FenceId, path, IndexAtGridPosition(contentPoint) ?? _model.Files.Count);
        else
            _manager.RemoveFile(FenceId, path);

        RenderAndPresent();
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
        RenderAndPresent();
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
                m.Result = (IntPtr)1;
                return;

            case WM_PAINT:
                // Content is pushed via UpdateLayeredWindow (RenderAndPresent), not drawn in
                // response to WM_PAINT - just clear the update region so Windows stops re-posting it.
                NativeMethods.BeginPaint(Handle, out var ps);
                NativeMethods.EndPaint(Handle, ref ps);
                return;

            case WM_RBUTTONUP:
                var clientPoint = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                ShowContextMenu(ToContent(clientPoint));
                return;

            case WM_COMMAND:
                HandleCommand(m.WParam.ToInt32() & 0xFFFF);
                return;
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_SIZE:
                _renameBox?.Resize(Math.Max(GetContentSize().Width - 12, 0));
                RenderAndPresent();
                break;

            case WM_EXITSIZEMOVE:
                if (NativeMethods.GetWindowRect(Handle, out var rect))
                    _manager.NotifyBoundsChanged(FenceId, Rectangle.FromLTRB(
                        rect.Left + OuterMargin, rect.Top + OuterMargin, rect.Right - OuterMargin, rect.Bottom - OuterMargin));
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

        // The resize-sensitive band spans from OuterMargin outside the visible fence to
        // ResizeMargin inside it - i.e. measured from the window's true (padded) edge.
        int band = OuterMargin + ResizeMargin;
        bool left = x <= band;
        bool right = x >= width - band;
        bool top = y <= band;
        bool bottom = y >= height - band;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        if (y - OuterMargin <= TitleBarHeight) return HTCAPTION;
        return HTCLIENT;
    }

    /// <summary>
    /// Builds this frame's full appearance (body, title bar, items, drag feedback) into an
    /// off-screen ARGB bitmap and pushes it to the screen via UpdateLayeredWindow. Called any time
    /// something visible changes (hover, drag, rename, resize, items added/removed) rather than in
    /// response to WM_PAINT, since a layered window's content isn't repainted by Windows itself.
    /// </summary>
    private void RenderAndPresent()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var windowRect))
            return;

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        int contentWidth = width - OuterMargin * 2;
        int contentHeight = height - OuterMargin * 2;
        if (contentWidth <= 0 || contentHeight <= 0)
            return;

        using var buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.Clear(Color.Transparent);

            // OuterMargin needs a non-zero (if faint) alpha - Windows treats fully transparent
            // (alpha 0) pixels of a layered window as click-through, so a truly invisible margin
            // couldn't receive the resize hit-testing it exists for. This gets drawn first and the
            // opaque fence body then covers all of it except that outer band.
            using (var marginFill = new SolidBrush(Color.FromArgb(8, 0, 0, 0)))
                g.FillRectangle(marginFill, 0, 0, width, height);

            g.TranslateTransform(OuterMargin, OuterMargin);
            // Items that overflow the fence's set height (more rows than fit) would otherwise get
            // drawn into the near-transparent margin band above - GDI+ compositing a bitmap's
            // semi-transparent edge pixels over a fully/near-transparent destination (rather than
            // the opaque body fill) produces garbage colors there, not just invisible overflow.
            g.SetClip(new Rectangle(0, 0, contentWidth, contentHeight));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // DrawIcon's native GDI stretch looks jagged when scaling a source icon down to
            // IconSize - drawing icons as bitmaps under high-quality interpolation instead avoids that.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var body = RoundedRect(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1), CornerRadius);
            using var bodyFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
            g.FillPath(bodyFill, body);

            using var titleFill = new SolidBrush(Color.FromArgb(255, 20, 20, 24));
            using var titlePath = RoundedRectTop(new Rectangle(0, 0, contentWidth - 1, TitleBarHeight), CornerRadius);
            g.FillPath(titleFill, titlePath);

            using var borderPen = new Pen(Color.FromArgb(255, 70, 70, 78));
            g.DrawPath(borderPen, body);

            if (_renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Name, _font, new Rectangle(8, 0, contentWidth - 16, TitleBarHeight),
                    Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            PaintItems(g, contentWidth, contentHeight);
        }

        LayeredWindowPresenter.Present(Handle, buffer, new Point(windowRect.Left, windowRect.Top), FenceOpacity);
    }

    /// <summary>
    /// Draws this fence's own icon+label for each file it holds, in a simple grid below the title
    /// bar - the fence never touches the real desktop icons (see FenceManager.AddFiles), so this is
    /// the only place those files are actually represented on screen.
    /// </summary>
    private void PaintItems(Graphics g, int width, int height)
    {
        if (_model.Files.Count == 0)
            return;

        var columns = Math.Max(1, (width - GridPadding * 2) / CellWidth);

        for (int i = 0; i < _model.Files.Count; i++)
        {
            var item = _model.Files[i];
            var isDragSource = i == _draggingIndex;
            var column = i % columns;
            var row = i / columns;
            var cellX = GridPadding + column * CellWidth;
            var cellY = TitleBarHeight + GridPadding + row * CellHeight;

            if (i == _hoverIndex && !isDragSource)
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
                var iconRect = new Rectangle(iconX, cellY + IconTopPadding, IconSize, IconSize);
                // Faded in place while its being dragged - the ghost near the cursor (painted
                // after the grid, see PaintDragFeedback) is what's actually "held".
                if (isDragSource)
                    DrawImageWithOpacity(g, bitmap, iconRect, 0.35f);
                else
                    g.DrawImage(bitmap, iconRect);
            }

            if (item.Path == _itemRenamePath)
                continue;

            var labelRect = new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, CellHeight - IconTopPadding - IconSize - 2);
            TextRenderer.DrawText(g, GetDisplayName(item), _font, labelRect, Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
        }

        PaintDragFeedback(g, width, height);
    }

    /// <summary>Draws the drop-target outline while an in-progress item drag (started in
    /// OnMouseDown/OnMouseMove) is over this fence. The dragged item's own ghost is a separate
    /// floating window (DragGhostWindow) that follows the cursor, not drawn here.</summary>
    private void PaintDragFeedback(Graphics g, int width, int height)
    {
        if (_draggingIndex is null)
            return;

        if (!new Rectangle(0, 0, width, height).Contains(_dragCurrentPoint) ||
            IndexAtGridPosition(_dragCurrentPoint) is not int targetIndex)
            return;

        var columns = Math.Max(1, (width - GridPadding * 2) / CellWidth);
        var cellX = GridPadding + targetIndex % columns * CellWidth;
        var cellY = TitleBarHeight + GridPadding + targetIndex / columns * CellHeight;

        using var targetPen = new Pen(Color.FromArgb(200, 120, 170, 255), 2);
        using var targetRect = RoundedRect(new Rectangle(cellX + 1, cellY + 1, CellWidth - 2, CellHeight - 2), 4);
        g.DrawPath(targetPen, targetRect);
    }

    private static void DrawImageWithOpacity(Graphics g, Image image, Rectangle rect, float opacity)
    {
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity }, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        g.DrawImage(image, rect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>An explicit rename (set via the item's context menu) always wins; otherwise every
    /// item displays without its extension, for now - regardless of type.</summary>
    private static string GetDisplayName(FenceItem item) =>
        !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : Path.GetFileNameWithoutExtension(item.Path);

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

    /// <summary>contentLocation is relative to the visible fence (see ToContent), not the padded window.</summary>
    private string? FileAtGridPosition(Point contentLocation)
    {
        var index = IndexAtGridPosition(contentLocation);
        return index is int i ? _model.Files[i].Path : null;
    }

    private int? IndexAtGridPosition(Point contentLocation)
    {
        if (_model.Files.Count == 0 || contentLocation.Y < TitleBarHeight)
            return null;

        var columns = Math.Max(1, (GetContentSize().Width - GridPadding * 2) / CellWidth);

        var column = (contentLocation.X - GridPadding) / CellWidth;
        var row = (contentLocation.Y - TitleBarHeight - GridPadding) / CellHeight;
        if (column < 0 || column >= columns || row < 0)
            return null;

        var index = row * columns + column;
        return index >= 0 && index < _model.Files.Count ? index : null;
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
        RenderAndPresent();
    }

    private void BeginRename()
    {
        if (_renameBox is not null)
            return;

        var contentWidth = GetContentSize().Width;
        if (contentWidth <= 0)
            return;

        var rect = ToWindow(new Rectangle(6, 3, Math.Max(contentWidth - 12, 0), 20));
        _renameBox = new EditBox(Handle, _model.Name, rect);
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

        RenderAndPresent();
    }

    private void OnRenameCancel()
    {
        _renameBox?.Dispose();
        _renameBox = null;
        RenderAndPresent();
    }

    private void BeginRenameItem(string? path)
    {
        if (path is null || _itemRenameBox is not null)
            return;

        var index = _model.Files.FindIndex(f => f.Path == path);
        var contentWidth = GetContentSize().Width;
        if (index < 0 || contentWidth <= 0)
            return;

        var columns = Math.Max(1, (contentWidth - GridPadding * 2) / CellWidth);
        var column = index % columns;
        var row = index / columns;
        var cellX = GridPadding + column * CellWidth;
        var cellY = TitleBarHeight + GridPadding + row * CellHeight;
        var labelRect = ToWindow(new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, 20));

        _itemRenamePath = path;
        _itemRenameBox = new EditBox(Handle, GetDisplayName(_model.Files[index]), labelRect);
        _itemRenameBox.Commit += OnItemRenameCommit;
        _itemRenameBox.Cancel += OnItemRenameCancel;
        RenderAndPresent();
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

        RenderAndPresent();
    }

    private void OnItemRenameCancel()
    {
        _itemRenameBox?.Dispose();
        _itemRenameBox = null;
        _itemRenamePath = null;
        RenderAndPresent();
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
