using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
    // Also doubles as where the settings cog now lives (always outside the visible fence, to the
    // right of its top-right corner) - sized to comfortably fit it, not just resize-grabbing.
    private const int OuterMargin = 26;
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
    private const int CmdToggleHideLabels = 7;
    private const int CmdToggleHideTitle = 8;
    private const int CmdResizeBoth = 9;
    private const int CmdResizeLeftRight = 10;
    private const int CmdResizeTopDown = 11;

    // Not real WM_COMMAND ids (clicking a submenu-anchor row just expands it, it never fires a
    // command) - these only tag an owner-draw row's itemData so DrawMenuItem/MeasureMenuItem know
    // to render a submenu arrow instead of a checkbox. See ShowFenceOptionsMenu.
    private const int TagOcdFormattingHeader = 1001;
    private const int TagFenceDimensionsHeader = 1002;

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;
    private const int ScrollbarWidth = 6;
    private const int ScrollbarMargin = 3;
    private const int CogSize = 16;
    private const int CogTopOffset = 6;
    private const int MenuCheckboxSize = 12;
    private const int MenuTextPadding = 8;

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

    // Vertical scroll for fences that hold more rows of items than fit in their set height.
    private int _scrollOffset;
    private bool _scrollbarDragging;
    private int _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;

    // The settings cog only shows once the fence has been clicked (i.e. is the active window) -
    // keeps the title bar quiet until you're actually interacting with that particular fence.
    private bool _isActive;
    private readonly Font _cogFont = new("Segoe MDL2 Assets", 9f);

    // Backs both the rename EditBox (via WM_CTLCOLOREDIT, see WndProc) and every owner-draw popup
    // menu (fence-options dropdown and right-click context menus) - one shared dark fill, matching
    // the fence body color, for everything that would otherwise default to a native white/light
    // control background. Lives for the form's whole lifetime rather than being recreated per menu.
    private readonly IntPtr _darkBrush = NativeMethods.CreateSolidBrush(ColorRef(Color.FromArgb(32, 32, 36)));

    public Guid FenceId => _model.Id;

    /// <summary>Item cell height when labels are hidden (FenceModel.HideLabels) - just the icon
    /// plus a little breathing room, since there's no label text to make room for underneath.</summary>
    private int EffectiveCellHeight => _model.HideLabels ? IconTopPadding + IconSize + 8 : CellHeight;

    /// <summary>Where the item grid starts, content-relative - below the title bar normally, or
    /// right at the top when FenceModel.HideTitle reclaims that space entirely.</summary>
    private int GridTop => _model.HideTitle ? 0 : TitleBarHeight;

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

    private static Point ToWindow(Point contentPoint) => new(contentPoint.X + OuterMargin, contentPoint.Y + OuterMargin);

    private static Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + OuterMargin, contentRect.Width, contentRect.Height);

    /// <summary>Window-relative (e.g. already run through ToWindow) to screen coordinates - needed
    /// for EditBox, which (unlike everything else drawn here) is a real top-level window rather than
    /// something painted into the fence's own layered bitmap. See EditBox's class doc comment.</summary>
    private Rectangle ToScreen(Rectangle windowRect) => new(PointToScreen(windowRect.Location), windowRect.Size);

    private static int GetColumns(int contentWidth) => Math.Max(1, (contentWidth - GridPadding * 2) / CellWidth);

    /// <summary>How far the grid can scroll (0 if every item's row already fits in contentHeight).</summary>
    private int GetMaxScroll(int contentWidth, int contentHeight)
    {
        if (_model.Files.Count == 0)
            return 0;

        var columns = GetColumns(contentWidth);
        var rows = (_model.Files.Count + columns - 1) / columns;
        var availableHeight = Math.Max(0, contentHeight - GridTop - GridPadding * 2);
        return Math.Max(0, rows * EffectiveCellHeight - availableHeight);
    }

    /// <summary>Content-relative, positioned just outside the visible fence (to the right of its
    /// top-right corner, in the OuterMargin band) rather than inside the title bar - works the same
    /// whether or not FenceModel.HideTitle leaves a title bar to put it in. Only meaningful while
    /// _isActive (the cog isn't shown otherwise).</summary>
    private static Rectangle GetCogRect(int contentWidth) =>
        new(contentWidth + (OuterMargin - CogSize) / 2, CogTopOffset, CogSize, CogSize);

    private readonly record struct ScrollbarGeometry(int TrackX, int TrackTop, int TrackHeight, int ThumbY, int ThumbHeight);

    /// <summary>Null when the fence's content doesn't need to scroll (no scrollbar to draw or hit-test).</summary>
    private ScrollbarGeometry? GetScrollbarGeometry(int contentWidth, int contentHeight)
    {
        var maxScroll = GetMaxScroll(contentWidth, contentHeight);
        if (maxScroll <= 0)
            return null;

        var trackTop = GridTop + GridPadding;
        var trackHeight = Math.Max(0, contentHeight - trackTop - GridPadding);
        var trackX = contentWidth - ScrollbarWidth - ScrollbarMargin;
        var totalHeight = trackHeight + maxScroll;
        var thumbHeight = Math.Min(trackHeight, Math.Max(20, (int)((long)trackHeight * trackHeight / Math.Max(1, totalHeight))));
        var maxThumbTravel = Math.Max(0, trackHeight - thumbHeight);
        var thumbY = trackTop + (maxThumbTravel > 0 ? (int)((long)_scrollOffset * maxThumbTravel / maxScroll) : 0);

        return new ScrollbarGeometry(trackX, trackTop, trackHeight, thumbY, thumbHeight);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renameBox?.Dispose();
            _itemRenameBox?.Dispose();
            _dragGhost?.Dispose();
            _font.Dispose();
            _cogFont.Dispose();
            NativeMethods.DeleteObject(_darkBrush);
            foreach (var icon in _iconCache.Values)
                icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _isActive = true;
        RenderAndPresent();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _isActive = false;
        RenderAndPresent();
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

        var path = FileAtGridPosition(ToContent(e.Location));
        if (path is not null)
            OpenItem(path);
        else if (_model.HideTitle)
            // No title bar to double-click when it's hidden - empty background stands in for it.
            BeginRename();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentSize = GetContentSize();

        if (_isActive && GetCogRect(contentSize.Width).Contains(contentPoint))
        {
            ShowFenceOptionsMenu();
            return;
        }

        if (GetScrollbarGeometry(contentSize.Width, contentSize.Height) is { } sb)
        {
            // A little horizontal slack around the thin thumb/track makes it easier to grab.
            var thumbRect = new Rectangle(sb.TrackX - 2, sb.ThumbY, ScrollbarWidth + 4, sb.ThumbHeight);
            if (thumbRect.Contains(contentPoint))
            {
                _scrollbarDragging = true;
                _scrollbarDragStartY = e.Location.Y;
                _scrollbarDragStartOffset = _scrollOffset;
                Capture = true;
                return;
            }

            var trackRect = new Rectangle(sb.TrackX - 2, sb.TrackTop, ScrollbarWidth + 4, sb.TrackHeight);
            if (trackRect.Contains(contentPoint))
            {
                // Clicking the track outside the thumb pages toward the click, like a normal scrollbar.
                var page = Math.Max(EffectiveCellHeight, sb.TrackHeight - EffectiveCellHeight);
                var maxScroll = GetMaxScroll(contentSize.Width, contentSize.Height);
                _scrollOffset = Math.Clamp(_scrollOffset + (contentPoint.Y < sb.ThumbY ? -page : page), 0, maxScroll);
                RenderAndPresent();
                return;
            }
        }

        if (IndexAtGridPosition(contentPoint) is int index)
        {
            _dragArmIndex = index;
            _dragArmPoint = e.Location; // raw window-space is fine here - only ever used as a delta
            return;
        }

        if (_model.HideTitle)
        {
            // No title bar to grab when it's hidden - forward the click-drag to the OS's own
            // caption-move handling, the standard trick for a draggable-by-its-body borderless window.
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_scrollbarDragging)
        {
            var contentSize = GetContentSize();
            if (GetScrollbarGeometry(contentSize.Width, contentSize.Height) is { } sb && sb.TrackHeight > sb.ThumbHeight)
            {
                var maxScroll = GetMaxScroll(contentSize.Width, contentSize.Height);
                var maxThumbTravel = sb.TrackHeight - sb.ThumbHeight;
                var dy = e.Location.Y - _scrollbarDragStartY;
                var newOffset = _scrollbarDragStartOffset + (int)((long)dy * maxScroll / maxThumbTravel);
                _scrollOffset = Math.Clamp(newOffset, 0, maxScroll);
                RenderAndPresent();
            }
            return;
        }

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

        if (_scrollbarDragging)
        {
            _scrollbarDragging = false;
            Capture = false;
            return;
        }

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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        var contentSize = GetContentSize();
        var maxScroll = GetMaxScroll(contentSize.Width, contentSize.Height);
        if (maxScroll <= 0)
            return;

        _scrollOffset = Math.Clamp(_scrollOffset - e.Delta / 120 * EffectiveCellHeight, 0, maxScroll);
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

            case NativeMethods.WM_CTLCOLOREDIT:
                // Sent by the rename EditBox to its owner (GetParent resolves to us even though
                // it's a top-level WS_POPUP, not a true child - see EditBox's class comment) each
                // time it needs to know what to paint itself with. Recoloring here, rather than in
                // EditBox itself, is the standard way to restyle a plain Edit control - it has no
                // owner-draw hook of its own the way buttons/menus do.
                NativeMethods.SetTextColor(m.WParam, ColorRef(Color.WhiteSmoke));
                NativeMethods.SetBkColor(m.WParam, ColorRef(Color.FromArgb(32, 32, 36)));
                m.Result = _darkBrush;
                return;

            case NativeMethods.WM_MEASUREITEM:
                var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(m.LParam);
                if (mis.CtlType == NativeMethods.ODT_MENU)
                {
                    MeasureMenuItem(ref mis);
                    Marshal.StructureToPtr(mis, m.LParam, false);
                }
                m.Result = (IntPtr)1;
                return;

            case NativeMethods.WM_DRAWITEM:
                var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(m.LParam);
                if (dis.CtlType == NativeMethods.ODT_MENU)
                    DrawMenuItem(dis);
                m.Result = (IntPtr)1;
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

        // The cog sits near the top of the title bar, which overlaps the top resize band below -
        // check it first so it isn't shadowed by an HTTOP/HTTOPLEFT/HTTOPRIGHT resize result.
        if (_isActive && GetCogRect(width - OuterMargin * 2).Contains(ToContent(new Point(x, y))))
            return HTCLIENT;

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

        // No title bar to grab when it's hidden - OnMouseDown forwards an empty-background drag
        // to a native move instead (ReleaseCapture + WM_NCLBUTTONDOWN), so this stays HTCLIENT.
        if (!_model.HideTitle && y - OuterMargin <= TitleBarHeight)
            return HTCAPTION;

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

        _scrollOffset = Math.Clamp(_scrollOffset, 0, GetMaxScroll(contentWidth, contentHeight));

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

            // Items that overflow the fence's set height (more rows than fit) would otherwise get
            // drawn into the near-transparent margin band above - GDI+ compositing a bitmap's
            // semi-transparent edge pixels over a fully/near-transparent destination (rather than
            // the opaque body fill) produces garbage colors there, not just invisible overflow.
            g.SetClip(new Rectangle(OuterMargin, OuterMargin, contentWidth, contentHeight));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // DrawIcon's native GDI stretch looks jagged when scaling a source icon down to
            // IconSize - drawing icons as bitmaps under high-quality interpolation instead avoids that.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Coordinates below are content-relative (see Offset) rather than using
            // Graphics.TranslateTransform - TextRenderer.DrawText (GDI, not GDI+) doesn't reliably
            // respect a GDI+ world transform, which left title/item text rendered OuterMargin
            // pixels too high while shapes and images (which do respect it) looked fine.
            using var body = RoundedRect(ToWindow(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1)), CornerRadius);
            using var bodyFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
            g.FillPath(bodyFill, body);

            if (!_model.HideTitle)
            {
                using var titleFill = new SolidBrush(Color.FromArgb(255, 20, 20, 24));
                using var titlePath = RoundedRectTop(ToWindow(new Rectangle(0, 0, contentWidth - 1, TitleBarHeight)), CornerRadius);
                g.FillPath(titleFill, titlePath);
            }

            using var borderPen = new Pen(Color.FromArgb(255, 70, 70, 78));
            g.DrawPath(borderPen, body);

            if (!_model.HideTitle && _renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Name, _font, ToWindow(new Rectangle(8, 0, contentWidth - 16, TitleBarHeight)),
                    Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            if (_isActive)
            {
                var cogRect = ToWindow(GetCogRect(contentWidth));

                // The cog now lives in the near-transparent OuterMargin band (see that constant's
                // comment) rather than on the opaque title bar it used to sit on. GDI's
                // TextRenderer.DrawText only ever writes RGB, never alpha, so without an opaque
                // backing first, the glyph would inherit the margin's near-zero alpha and vanish
                // once WritePremultipliedPixels scales it down - the same class of bug as the old
                // scrolled-item-over-transparent-background issue, just for text instead of images.
                using var cogBackingFill = new SolidBrush(Color.FromArgb(255, 40, 40, 46));
                using var cogBackingPath = RoundedRect(Rectangle.Inflate(cogRect, 3, 3), 6);
                g.FillPath(cogBackingFill, cogBackingPath);

                TextRenderer.DrawText(g, "\uE713", _cogFont, cogRect, Color.Silver,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

        // Items scrolled above the grid top or below the fence's bottom edge must not be able to
        // paint there - see the SetClip comment above for why that's not just a visibility issue.
        g.SetClip(ToWindow(new Rectangle(0, GridTop, width, height - GridTop)), CombineMode.Intersect);

        var columns = GetColumns(width);

        for (int i = 0; i < _model.Files.Count; i++)
        {
            var item = _model.Files[i];
            var isDragSource = i == _draggingIndex;
            var column = i % columns;
            var row = i / columns;
            var cellX = GridPadding + column * CellWidth;
            var cellY = GridTop + GridPadding + row * EffectiveCellHeight - _scrollOffset;

            // A scrolled row can straddle the grid-top boundary. g.Clip normally handles that for
            // shapes/icons (GDI+ respects it), but TextRenderer.DrawText (GDI) draws its text in
            // full regardless of the clip region - the same disregard-for-Graphics-state quirk as
            // TranslateTransform above, just for clipping instead of position. So icons rely on the
            // clip as usual, but labels only draw when their whole rect is already within bounds.
            if (cellY + EffectiveCellHeight <= GridTop || cellY >= height)
                continue;

            if (i == _hoverIndex && !isDragSource)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var hoverRect = RoundedRect(ToWindow(new Rectangle(cellX, cellY, CellWidth, EffectiveCellHeight)), 4);
                g.FillPath(hoverBrush, hoverRect);
            }

            var icon = GetIcon(item.Path);
            if (icon is not null)
            {
                var iconX = cellX + (CellWidth - IconSize) / 2;
                using var bitmap = icon.ToBitmap();
                var iconRect = ToWindow(new Rectangle(iconX, cellY + IconTopPadding, IconSize, IconSize));
                // Faded in place while its being dragged - the ghost near the cursor (painted
                // after the grid, see PaintDragFeedback) is what's actually "held".
                if (isDragSource)
                    DrawImageWithOpacity(g, bitmap, iconRect, 0.35f);
                else
                    g.DrawImage(bitmap, iconRect);
            }

            if (item.Path == _itemRenamePath || _model.HideLabels)
                continue;

            var labelTop = cellY + IconTopPadding + IconSize + 2;
            var labelHeight = CellHeight - IconTopPadding - IconSize - 2;
            if (labelTop >= GridTop)
            {
                // Only the bottom can need trimming here (the top is already in bounds), so
                // shrinking the rect's height is a true clip - unlike g.Clip, TextRenderer.DrawText
                // does respect its own rect parameter (DT_NOCLIP isn't set), cutting off whatever
                // doesn't fit rather than needing the whole label to fit or nothing.
                var visibleHeight = Math.Min(labelHeight, height - labelTop);
                if (visibleHeight > 0)
                {
                    var labelRect = ToWindow(new Rectangle(cellX, labelTop, CellWidth, visibleHeight));
                    TextRenderer.DrawText(g, GetDisplayName(item), _font, labelRect, Color.WhiteSmoke,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
                }
            }
        }

        PaintDragFeedback(g, width, height);
        PaintScrollbar(g, width, height);
    }

    private void PaintScrollbar(Graphics g, int width, int height)
    {
        if (GetScrollbarGeometry(width, height) is not { } sb)
            return;

        using var trackBrush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
        g.FillRectangle(trackBrush, ToWindow(new Rectangle(sb.TrackX, sb.TrackTop, ScrollbarWidth, sb.TrackHeight)));

        using var thumbBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
        using var thumbPath = RoundedRect(ToWindow(new Rectangle(sb.TrackX, sb.ThumbY, ScrollbarWidth, sb.ThumbHeight)), ScrollbarWidth / 2);
        g.FillPath(thumbBrush, thumbPath);
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

        var columns = GetColumns(width);
        var cellX = GridPadding + targetIndex % columns * CellWidth;
        var cellY = GridTop + GridPadding + targetIndex / columns * EffectiveCellHeight - _scrollOffset;

        using var targetPen = new Pen(Color.FromArgb(200, 120, 170, 255), 2);
        using var targetRect = RoundedRect(ToWindow(new Rectangle(cellX + 1, cellY + 1, CellWidth - 2, EffectiveCellHeight - 2)), 4);
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
        if (_model.Files.Count == 0 || contentLocation.Y < GridTop)
            return null;

        var columns = GetColumns(GetContentSize().Width);

        var column = (contentLocation.X - GridPadding) / CellWidth;
        var row = (contentLocation.Y - GridTop - GridPadding + _scrollOffset) / EffectiveCellHeight;
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
                AppendItem(hMenu, CmdOpenItem, false);
                AppendItem(hMenu, CmdRenameItem, false);
                AppendItem(hMenu, CmdRemoveItem, false);
            }
            else
            {
                AppendItem(hMenu, CmdRename, false);
                NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
                AppendItem(hMenu, CmdDelete, false);
            }

            ApplyDarkMenuTheme(hMenu);

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    /// <summary>Per-fence settings, opened via the cog that appears in the title bar once this
    /// fence is the active window (see OnActivated/OnDeactivate and the cog hit-test carve-out).
    /// Builds a 2-level tree: this menu -> "OCD Formatting", whose own submenu holds a "Fence
    /// Dimensions" header (a plain disabled label, not a further nested submenu - see AppendHeader)
    /// followed by its three resize actions. AppendPopup stays available as general infrastructure
    /// for a real third level if a future subcategory needs one.</summary>
    private void ShowFenceOptionsMenu()
    {
        var contentSize = GetContentSize();
        var cogRect = GetCogRect(contentSize.Width);
        var menuPoint = PointToScreen(ToWindow(new Point(cogRect.X, cogRect.Bottom + 2)));

        var hOcdMenu = NativeMethods.CreatePopupMenu();
        var hMenu = NativeMethods.CreatePopupMenu();
        try
        {
            // MF_OWNERDRAW rather than MF_STRING so the menu can be painted dark (matching the
            // fence) instead of the native Windows menu chrome - see MeasureMenuItem/DrawMenuItem,
            // wired up via WM_MEASUREITEM/WM_DRAWITEM in WndProc. Every owner-draw row's label and
            // style is looked up from a tag carried in itemData (see AppendItem/GetMenuRowStyle)
            // rather than the item's id - a submenu-anchor row (MF_POPUP) has no command id of its
            // own to key off (its uIDNewItem slot holds the submenu handle instead), so itemData is
            // the only thing that works for every row uniformly.
            AppendHeader(hOcdMenu, TagFenceDimensionsHeader);
            AppendItem(hOcdMenu, CmdResizeBoth, false);
            AppendItem(hOcdMenu, CmdResizeLeftRight, false);
            AppendItem(hOcdMenu, CmdResizeTopDown, false);

            AppendItem(hMenu, CmdToggleHideLabels, _model.HideLabels);
            AppendItem(hMenu, CmdToggleHideTitle, _model.HideTitle);
            AppendPopup(hMenu, hOcdMenu, TagOcdFormattingHeader);

            ApplyDarkMenuTheme(hMenu);

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_LEFTBUTTON, menuPoint.X, menuPoint.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu); // recursively destroys the attached submenu too
        }
    }

    /// <summary>WM_DRAWITEM only paints each item's own row - the popup's outer margin/border is
    /// separately filled by the menu's own background brush, which defaults to the system's (light)
    /// COLOR_MENU and shows through as a stray light border around the dark rows unless replaced
    /// here to match. MIM_APPLYTOSUBMENUS cascades this to any attached submenus too. Reuses
    /// _darkBrush rather than creating/deleting a brush per menu invocation.</summary>
    private void ApplyDarkMenuTheme(IntPtr hMenu)
    {
        var menuInfo = new MENUINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUINFO>(),
            fMask = NativeMethods.MIM_BACKGROUND | NativeMethods.MIM_APPLYTOSUBMENUS,
            hbrBack = _darkBrush,
        };
        NativeMethods.SetMenuInfo(hMenu, ref menuInfo);
    }

    private static void AppendItem(IntPtr hMenu, int commandId, bool isChecked)
    {
        var flags = NativeMethods.MF_OWNERDRAW | (isChecked ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED);
        NativeMethods.AppendMenu(hMenu, flags, (IntPtr)commandId, (IntPtr)commandId);
    }

    /// <summary>A non-interactive section label within a submenu (e.g. "Fence Dimensions" above the
    /// resize actions) - MF_DISABLED|MF_GRAYED so it can't be clicked, hovered, or keyboard-selected;
    /// DrawMenuItem dims its text instead of relying on the native grayed-out rendering, since we're
    /// owner-drawing everything else in this menu anyway.</summary>
    private static void AppendHeader(IntPtr hMenu, int headerTag) =>
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_OWNERDRAW | NativeMethods.MF_DISABLED | NativeMethods.MF_GRAYED, (IntPtr)headerTag, (IntPtr)headerTag);

    /// <summary>General infrastructure for a nested submenu-anchor row (e.g. "OCD Formatting") -
    /// kept generic/reusable for a future third level, even though only one level currently uses it.</summary>
    private static void AppendPopup(IntPtr hParentMenu, IntPtr hSubMenu, int headerTag) =>
        NativeMethods.AppendMenu(hParentMenu, NativeMethods.MF_POPUP | NativeMethods.MF_OWNERDRAW, hSubMenu, (IntPtr)headerTag);

    private readonly record struct MenuRowStyle(string Text, bool HasCheckbox, bool IsHeader);

    /// <summary>Every owner-draw row's label and decoration, keyed by the tag carried in its
    /// itemData (see AppendItem/AppendHeader/AppendPopup) rather than its item id. Submenu-anchor
    /// rows (e.g. "OCD Formatting") need no flag here - Windows draws their arrow indicator itself,
    /// in a margin outside our own owner-draw rect (see DrawMenuItem).</summary>
    private static MenuRowStyle GetMenuRowStyle(int tag) => tag switch
    {
        CmdToggleHideLabels => new MenuRowStyle("Hide Shortcut Names", true, false),
        CmdToggleHideTitle => new MenuRowStyle("Hide Title", true, false),
        TagOcdFormattingHeader => new MenuRowStyle("OCD Formatting", false, false),
        TagFenceDimensionsHeader => new MenuRowStyle("Fence Dimensions", false, true),
        CmdResizeBoth => new MenuRowStyle("Both", false, false),
        CmdResizeLeftRight => new MenuRowStyle("Left/Right", false, false),
        CmdResizeTopDown => new MenuRowStyle("Top/Down", false, false),
        CmdOpenItem => new MenuRowStyle("Open", false, false),
        CmdRenameItem => new MenuRowStyle("Rename", false, false),
        CmdRemoveItem => new MenuRowStyle("Remove From Fence", false, false),
        CmdRename => new MenuRowStyle("Rename", false, false),
        CmdDelete => new MenuRowStyle("Delete Fence", false, false),
        _ => new MenuRowStyle(string.Empty, false, false),
    };

    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    private void MeasureMenuItem(ref MEASUREITEMSTRUCT mis)
    {
        var style = GetMenuRowStyle((int)mis.itemData);
        var size = TextRenderer.MeasureText(style.Text, _font);
        var leftReserve = style.HasCheckbox ? MenuCheckboxSize + MenuTextPadding : 0;
        // No right-side reserve for a submenu arrow - Windows always adds its own fixed arrow
        // margin outside whatever width we report for an MF_POPUP row, so reserving space for one
        // here too just doubled up as a second, hand-drawn arrow next to the native one.
        mis.itemWidth = (uint)(MenuTextPadding + leftReserve + size.Width + MenuTextPadding);
        mis.itemHeight = (uint)Math.Max(size.Height + 8, 22);
    }

    /// <summary>Paints one row of the fence-options dropdown to match the fence's own dark theme,
    /// instead of the native Windows menu look - background, a hand-drawn checkbox (no checkmark
    /// glyph font, to sidestep the encoding issues that bit the cog glyph earlier), and the row's
    /// label text. A submenu row's arrow indicator is left to Windows to draw natively (see
    /// MeasureMenuItem) - drawing our own there duplicated it.
    private void DrawMenuItem(DRAWITEMSTRUCT dis)
    {
        using var g = Graphics.FromHdc(dis.hDC);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Rectangle.FromLTRB(dis.rcItem.Left, dis.rcItem.Top, dis.rcItem.Right, dis.rcItem.Bottom);
        var style = GetMenuRowStyle((int)dis.itemData);
        var selected = !style.IsHeader && (dis.itemState & NativeMethods.ODS_SELECTED) != 0;
        var isChecked = (dis.itemState & NativeMethods.ODS_CHECKED) != 0;

        using (var background = new SolidBrush(selected ? Color.FromArgb(255, 55, 55, 62) : Color.FromArgb(255, 32, 32, 36)))
            g.FillRectangle(background, rect);

        if (style.HasCheckbox)
        {
            var checkRect = new Rectangle(rect.X + MenuTextPadding, rect.Y + (rect.Height - MenuCheckboxSize) / 2, MenuCheckboxSize, MenuCheckboxSize);
            using (var checkPen = new Pen(Color.FromArgb(255, 150, 150, 158)))
                g.DrawRectangle(checkPen, checkRect);

            if (isChecked)
            {
                using var checkMarkPen = new Pen(Color.FromArgb(255, 120, 170, 255), 2);
                g.DrawLine(checkMarkPen, checkRect.X + 2, checkRect.Y + 6, checkRect.X + 5, checkRect.Y + 9);
                g.DrawLine(checkMarkPen, checkRect.X + 5, checkRect.Y + 9, checkRect.X + 10, checkRect.Y + 2);
            }
        }

        var textLeft = rect.X + MenuTextPadding + (style.HasCheckbox ? MenuCheckboxSize + MenuTextPadding : 0);
        var textRect = new Rectangle(textLeft, rect.Y, Math.Max(0, rect.Right - MenuTextPadding - textLeft), rect.Height);
        var textColor = style.IsHeader ? Color.FromArgb(255, 140, 140, 148) : Color.WhiteSmoke;
        TextRenderer.DrawText(g, style.Text, _font, textRect, textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
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
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdToggleHideTitle: ToggleHideTitle(); break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
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

    private void ToggleHideLabels()
    {
        _manager.SetHideLabels(FenceId, !_model.HideLabels);
        RenderAndPresent();
    }

    private void ToggleHideTitle()
    {
        _manager.SetHideTitle(FenceId, !_model.HideTitle);
        RenderAndPresent();
    }

    /// <summary>"OCD Formatting -> Fence Dimensions" - shrinks or grows the fence to trim away
    /// wasted space around its current grid, keeping the top-left corner fixed. Trims to what's
    /// already on screen, not the fence's full contents: height never expands past however many
    /// rows are currently visible, so a fence that's deliberately kept short (scrollable) doesn't
    /// get blown open to reveal everything. adjustWidth/adjustHeight let the three menu entries
    /// (Both/Left-Right/Top-Down) share this one implementation.</summary>
    private void FormatDimensions(bool adjustWidth, bool adjustHeight)
    {
        var contentSize = GetContentSize();
        if (contentSize.Width <= 0 || contentSize.Height <= 0 || _model.Files.Count == 0)
            return;

        var currentColumns = GetColumns(contentSize.Width);
        // Don't keep more column slots than there are icons to fill them - a fence with 2 icons
        // and room for 5 columns is just as untidy as one with extra trailing padding.
        var columns = adjustWidth ? Math.Min(currentColumns, _model.Files.Count) : currentColumns;

        var availableHeight = Math.Max(0, contentSize.Height - GridTop - GridPadding * 2);
        var currentVisibleRows = Math.Max(1, availableHeight / EffectiveCellHeight);
        var totalRowsNeeded = (_model.Files.Count + columns - 1) / columns;
        var finalRows = adjustHeight ? Math.Min(currentVisibleRows, totalRowsNeeded) : currentVisibleRows;

        var newBounds = _model.Bounds;

        if (adjustWidth)
        {
            newBounds.Width = GridPadding * 2 + columns * CellWidth;

            // A fence that still won't show every row after this needs its own reserved strip for
            // the scrollbar - GridPadding is just breathing room around the grid, not real estate
            // set aside for it, so without this the scrollbar would have nowhere to go but
            // overlapping the last column's icons.
            if (finalRows < totalRowsNeeded)
                newBounds.Width += ScrollbarWidth + ScrollbarMargin;
        }

        if (adjustHeight)
            newBounds.Height = GridTop + GridPadding * 2 + finalRows * EffectiveCellHeight;

        if (newBounds == _model.Bounds)
            return;

        // WM_SIZE (already handled in WndProc) re-renders with the new size once this returns -
        // NotifyBoundsChanged just needs to persist it, the same way WM_EXITSIZEMOVE does after an
        // interactive drag-resize.
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0,
            newBounds.Width + OuterMargin * 2, newBounds.Height + OuterMargin * 2,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _manager.NotifyBoundsChanged(FenceId, newBounds);
    }

    private void BeginRename()
    {
        if (_renameBox is not null)
            return;

        var contentWidth = GetContentSize().Width;
        if (contentWidth <= 0)
            return;

        var rect = ToWindow(new Rectangle(6, 3, Math.Max(contentWidth - 12, 0), 20));
        _renameBox = new EditBox(Handle, _model.Name, ToScreen(rect), _font);
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
        var contentSize = GetContentSize();
        if (index < 0 || contentSize.Width <= 0)
            return;

        var columns = GetColumns(contentSize.Width);
        var column = index % columns;
        var row = index / columns;
        var cellX = GridPadding + column * CellWidth;
        var absoluteCellY = GridTop + GridPadding + row * EffectiveCellHeight;

        // Scroll the item's row fully into view first if it's currently scrolled off - otherwise
        // the edit box could end up positioned above the grid top or below the fence entirely.
        var gridTop = GridTop + GridPadding;
        var gridBottom = contentSize.Height - GridPadding;
        if (absoluteCellY - _scrollOffset < gridTop)
            _scrollOffset = Math.Max(0, absoluteCellY - gridTop);
        else if (absoluteCellY + EffectiveCellHeight - _scrollOffset > gridBottom)
            _scrollOffset = Math.Min(GetMaxScroll(contentSize.Width, contentSize.Height), absoluteCellY + EffectiveCellHeight - gridBottom);

        var cellY = absoluteCellY - _scrollOffset;
        var labelRect = ToWindow(new Rectangle(cellX, cellY + IconTopPadding + IconSize + 2, CellWidth, 20));

        _itemRenamePath = path;
        _itemRenameBox = new EditBox(Handle, GetDisplayName(_model.Files[index]), ToScreen(labelRect), _font);
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
