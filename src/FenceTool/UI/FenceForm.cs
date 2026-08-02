using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    // alpha (see MarginFillColor) since alpha 0 would be click-through too, defeating the point.
    private const int OuterMargin = 13;

    // The settings button (see SettingsButtonWidth/Height) sits above the fence, flush with its
    // top-right corner, and doesn't fit inside the plain OuterMargin band (13px) with any breathing
    // room, so the window is extended *only on top* by this much extra - every other edge
    // (left/right/bottom, and their resize-grab bands) stays exactly OuterMargin. See
    // GetSettingsButtonRect, CreateParams, GetContentSize, and every other OuterMargin-on-top site
    // below, all of which use TopMargin instead of OuterMargin for that one edge.
    private const int SettingsButtonOverhang = 17;
    private const int TopMargin = OuterMargin + SettingsButtonOverhang;
    private const int CornerRadius = 22;
    private const float FenceOpacity = 0.85f;

    // Fallback accent (drag-target outline, menu checkmarks, active-fence border) for a fence that
    // hasn't been given its own color (FenceModel.TintColor is null) - see Accent/ShowFenceOptionsMenu's
    // "Fence Color" submenu. Every other neutral-gray chrome color below has the same
    // no-tint/with-tint split via Tint(), keyed off this same DefaultXxx naming.
    private static readonly Color DefaultAccentColor = Color.FromArgb(120, 170, 255);
    private static readonly Color DefaultBodyColor = Color.FromArgb(255, 32, 32, 36);
    private static readonly Color DefaultTitleColor = Color.FromArgb(255, 10, 10, 13);
    private static readonly Color DefaultBorderColor = Color.FromArgb(255, 70, 70, 78);
    private static readonly Color DefaultMenuSelectedColor = Color.FromArgb(255, 55, 55, 62);
    private static readonly Color DefaultCheckboxBorderColor = Color.FromArgb(255, 150, 150, 158);
    private const float ActiveBorderWidth = 8f;

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
    private const int CmdRenameItem = 6;
    private const int CmdToggleHideLabels = 7;
    private const int CmdToggleHideTitle = 8;
    private const int CmdResizeBoth = 9;
    private const int CmdResizeLeftRight = 10;
    private const int CmdResizeTopDown = 11;
    private const int CmdToggleOcdSizing = 12;
    private const int CmdColorDefault = 13;
    private const int CmdColorCustom = 14;
    // A contiguous block reserved for the preset swatches (see ColorPresets) - avoids one named
    // const per swatch the way the other commands have, since these are looked up by index rather
    // than individually referenced anywhere.
    private const int CmdColorPresetBase = 20;

    // Not real WM_COMMAND ids (clicking a submenu-anchor row just expands it, it never fires a
    // command) - these only tag an owner-draw row's itemData so DrawMenuItem/MeasureMenuItem know
    // to render a submenu arrow instead of a checkbox. See ShowFenceOptionsMenu.
    private const int TagOcdFormattingHeader = 1001;
    private const int TagFenceDimensionsHeader = 1002;
    private const int TagColorHeader = 1003;

    /// <summary>Themed presets offered in the "Fence Color" submenu, alongside "Default" (resets to
    /// the plain dark gray) and "Custom..." (opens the system color picker). Muted rather than
    /// fully saturated so the tinted body/title still read as a dark theme - see Tint.</summary>
    private static readonly Color[] ColorPresets =
    {
        Color.FromArgb(200, 80, 80),   // Red
        Color.FromArgb(210, 140, 70),  // Orange
        Color.FromArgb(200, 180, 70),  // Yellow
        Color.FromArgb(90, 170, 100),  // Green
        Color.FromArgb(70, 170, 170),  // Teal
        Color.FromArgb(90, 140, 210),  // Blue
        Color.FromArgb(150, 110, 210), // Purple
        Color.FromArgb(210, 110, 160), // Pink
    };

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;
    private const int ScrollbarWidth = 6;
    private const int ScrollbarMargin = 3;
    private const int SettingsButtonWidth = 64;
    private const int SettingsButtonHeight = 22;
    // Vertical gap between the button's bottom edge and the fence's own top edge (TopMargin above
    // reserves enough extra room for this plus a little more breathing space above the button).
    private const int SettingsButtonGap = 4;
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

    // The settings button only shows once the fence has been clicked (i.e. is the active window) -
    // keeps the title bar quiet until you're actually interacting with that particular fence.
    private bool _isActive;

    // A real child Button control was tried here first, but a window painted via UpdateLayeredWindow
    // (see RenderAndPresent/LayeredWindowPresenter) doesn't compose child windows on top of itself -
    // it just never appeared, clickable or not. So this is drawn like everything else on the fence
    // (see RenderAndPresent) and hit-tested by hand instead: armed on OnMouseDown, fired on the
    // matching OnMouseUp only if the cursor is still over it, mirroring the arm-then-fire pattern
    // used for drag-vs-click elsewhere in this file. Firing on down instead of up was tried too,
    // early in this button's history - opening the dropdown while the mouse button is still
    // physically down raced with TrackPopupMenuEx's own capture and made it flash open and closed.
    private bool _settingsButtonArmed;

    // Backs both the rename EditBox (via WM_CTLCOLOREDIT, see WndProc) and every owner-draw popup
    // menu (fence-options dropdown and right-click context menus) - one shared themed fill, matching
    // ThemedBody, for everything that would otherwise default to a native white/light control
    // background. Recreated on demand (see GetThemeBrush) rather than fixed for the form's whole
    // lifetime, since ThemedBody now depends on the fence's own color and can change at runtime.
    private IntPtr _themeBrush = IntPtr.Zero;
    private Color _themeBrushColor;

    /// <summary>Lazily (re)creates the shared theme brush only when ThemedBody has actually changed
    /// since the last call - both call sites (WM_CTLCOLOREDIT, ApplyDarkMenuTheme) can fire often
    /// enough (every rename-box redraw, every menu open) that recreating a native GDI brush on every
    /// single call would be wasteful.</summary>
    private IntPtr GetThemeBrush()
    {
        var color = ThemedBody;
        if (_themeBrush == IntPtr.Zero || _themeBrushColor != color)
        {
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
            _themeBrush = NativeMethods.CreateSolidBrush(ColorRef(color));
            _themeBrushColor = color;
        }
        return _themeBrush;
    }

    // Whether the drag that's about to start on WM_NCLBUTTONDOWN is a resize (as opposed to a
    // move) - set from that message's own hit-test code, read back on WM_EXITSIZEMOVE to decide
    // whether OcdFenceSizing should auto-run "Both" now that the resize is done. WM_EXITSIZEMOVE
    // fires after a move just as much as a resize, so this is the only reliable way to tell them
    // apart at that point.
    private bool _resizeInProgress;

    // Lazily created native tooltip common control, tracked manually (TTF_TRACK) rather than the
    // control's own automatic hover detection, since it has to follow WM_MENUSELECT on a raw HMENU
    // instead of a real child control's mouse events - see ShowMenuItemTooltip/HideMenuItemTooltip.
    private IntPtr _menuTooltip = IntPtr.Zero;

    // Guards RenderAndPresent against a reentrant repaint triggered mid-teardown - see Dispose's
    // own comment on WM_ACTIVATE firing synchronously from within base.Dispose(disposing).
    private bool _disposing;

    public Guid FenceId => _model.Id;

    /// <summary>The fence's own color (FenceModel.TintColor), or null for the plain default dark
    /// theme - the single source every themed color below (body/title fill, margin, borders,
    /// settings button, settings menu chrome) derives from. See Tint and ShowFenceOptionsMenu's
    /// "Fence Color" submenu.</summary>
    private Color? CurrentTint => _model.TintColor is { } argb ? Color.FromArgb(argb) : null;

    /// <summary>Full-strength version of the fence's tint (falling back to a fixed blue) for
    /// elements that need to read clearly rather than just hint at the theme - the active-fence
    /// border, drag-target outline, settings button, and settings menu checkmarks/selection
    /// ring.</summary>
    private Color Accent => CurrentTint ?? DefaultAccentColor;

    private Color ThemedBody => Tint(DefaultBodyColor, CurrentTint);
    private Color ThemedTitle => Tint(DefaultTitleColor, CurrentTint);
    private Color ThemedBorder => Tint(DefaultBorderColor, CurrentTint);
    private Color ThemedMenuSelected => Tint(DefaultMenuSelectedColor, CurrentTint);
    private Color ThemedCheckboxBorder => Tint(DefaultCheckboxBorderColor, CurrentTint, 0.4);

    // Deliberately never tinted, unlike every other Themed* color - this fill exists purely so
    // Windows doesn't treat the margin as click-through (see RenderAndPresent), not to be seen.
    // Alpha 1 is the practical minimum that still counts as "not fully transparent" to Windows.
    private static readonly Color MarginFillColor = Color.FromArgb(1, 0, 0, 0);

    // Translucent rather than opaque, same as the old fixed silver active-border color it replaces -
    // a fully opaque accent border read as too heavy/saturated against the tinted body beneath it.
    private Color ThemedActiveBorder => Color.FromArgb(220, Accent);

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
            cp.Y = _model.Bounds.Y - TopMargin;
            cp.Width = _model.Bounds.Width + OuterMargin * 2;
            cp.Height = _model.Bounds.Height + TopMargin + OuterMargin;
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

    /// <summary>Where an item dropped at screenPoint (dragged in from a different fence, see
    /// FenceManager.MoveFileToFence) would land in this fence's own grid - appended to the end when
    /// the point isn't over a specific item (e.g. it's in the margin, or past the last row).</summary>
    internal int IndexForExternalDrop(Point screenPoint) =>
        IndexAtGridPosition(ToContent(PointToClient(screenPoint))) ?? _model.Files.Count;

    /// <summary>Repaints after FenceManager mutates this fence's model on behalf of a *different*
    /// fence's drag operation (see MoveFileToFence) - this fence's own drag/drop paths already
    /// re-render themselves directly.</summary>
    internal void RefreshAfterExternalChange() => RenderAndPresent();

    public new void Show() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);

    public void SetVisible(bool visible) =>
        NativeMethods.ShowWindow(Handle, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);

    /// <summary>Re-applies the desktop anchor (e.g. after explorer.exe restarts or a display
    /// change invalidates the previous z-order/parenting). Uses _model.Bounds (our own tracked
    /// absolute screen position), which is authoritative regardless of whatever coordinate
    /// convention the current native parent implies.</summary>
    public void Reanchor() => _anchorStrategy.Apply(Handle, _model.Bounds);

    /// <summary>The visible fence's size, i.e. the actual (padded) window size minus OuterMargin on
    /// the left/right/bottom and TopMargin on top - all grid/hit-test math below is in this
    /// "content" space.</summary>
    private Size GetContentSize()
    {
        NativeMethods.GetClientRect(Handle, out var clientRect);
        return new Size(Math.Max(0, clientRect.Right - OuterMargin * 2), Math.Max(0, clientRect.Bottom - TopMargin - OuterMargin));
    }

    private static Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - TopMargin);

    private static Point ToWindow(Point contentPoint) => new(contentPoint.X + OuterMargin, contentPoint.Y + TopMargin);

    private static Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + TopMargin, contentRect.Width, contentRect.Height);

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

    /// <summary>Content-relative, positioned just outside the visible fence (directly above it,
    /// flush with its top-right corner, in the taller TopMargin band) - works the same whether or
    /// not FenceModel.HideTitle leaves a title bar underneath it. Only meaningful while _isActive
    /// (the button isn't shown otherwise). Y is negative - above content-space y=0 - which is fine
    /// everywhere this is used (hit-testing, painting via ToWindow, menu positioning).</summary>
    private static Rectangle GetSettingsButtonRect(int contentWidth) =>
        new(contentWidth - SettingsButtonWidth, -(SettingsButtonHeight + SettingsButtonGap), SettingsButtonWidth, SettingsButtonHeight);

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
            // Set before anything below actually runs: base.Dispose(disposing) (below) tears down
            // the native window via DestroyWindow, which - as part of the OS's normal
            // deactivate-before-destroy sequence - synchronously delivers WM_ACTIVATE to this same
            // window while our WndProc override is still hooked up, reaching OnDeactivate ->
            // RenderAndPresent -> PaintItems before this call even returns. Without this guard that
            // repaints using _iconCache's Icon objects just disposed a few lines down, which throws
            // (Icon is an ObjectDisposedException-checked handle, same as Control.Handle).
            _disposing = true;

            _renameBox?.Dispose();
            _itemRenameBox?.Dispose();
            _dragGhost?.Dispose();
            _font.Dispose();
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
            if (_menuTooltip != IntPtr.Zero)
                NativeMethods.DestroyWindow(_menuTooltip);
            foreach (var icon in _iconCache.Values)
                icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    // _isActive (settings button + drag-margin visibility) is intentionally NOT driven by OnActivated - that
    // fires for any click that gives the window OS focus, including a plain click on a shortcut
    // just to use it. It's set explicitly instead, only for right-click (anywhere) or a title-bar
    // click (either button) - see WndProc's WM_NCLBUTTONDOWN/WM_NCRBUTTONDOWN handling and
    // ShowContextMenu. Resizing deliberately does NOT activate the fence - HitTest turns the whole
    // margin band into a move handle once already active, so resize and move never contend for the
    // same pixels, but that also means resize has to stay unavailable to the (fence, click) pairs
    // that would otherwise be ambiguous. Losing focus still deactivates unconditionally.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _isActive = false;
        RenderAndPresent();
    }

    private void ActivateFence()
    {
        if (_isActive)
            return;
        _isActive = true;
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

        if (_isActive && GetSettingsButtonRect(contentSize.Width).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
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
        }

        // Moving now happens via the margin band outside the visible fence (see HitTest's move-ring
        // check) rather than by clicking empty content here - that band always exists, regardless of
        // whether there's a title bar or how densely packed the grid is, so there's no more fallback
        // needed at this layer.
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

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (_isActive && GetSettingsButtonRect(GetContentSize().Width).Contains(ToContent(e.Location)))
                ShowFenceOptionsMenu();
            return;
        }

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
        {
            _manager.MoveFile(FenceId, path, IndexAtGridPosition(contentPoint) ?? _model.Files.Count);
        }
        else
        {
            // Not a drop inside this fence's own content - check whether it landed on a *different*
            // fence's window instead of empty desktop, and hand the item over rather than discarding
            // it (the pre-existing behavior for a drop that lands nowhere).
            var screenPoint = PointToScreen(e.Location);
            if (_manager.FindFenceAt(screenPoint, FenceId) is { } targetForm)
                _manager.MoveFileToFence(FenceId, targetForm.FenceId, path, targetForm.IndexForExternalDrop(screenPoint));
            else
                _manager.RemoveFile(FenceId, path);
        }

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
                ActivateFence();
                BeginRename();
                return;

            case NativeMethods.WM_NCLBUTTONDOWN:
                // A left click on the title bar activates the fence - see OnDeactivate's comment
                // for why resize edges deliberately don't. Not returning early: the default proc
                // still needs this message to actually move/resize.
                var ncLButtonHitTest = (int)m.WParam.ToInt64();
                if (ncLButtonHitTest == HTCAPTION)
                    ActivateFence();
                // Remembered for WM_EXITSIZEMOVE, which fires after a move just as much as a
                // resize - OcdFenceSizing should only auto-run following an actual resize.
                _resizeInProgress = IsResizeHitTest(ncLButtonHitTest);
                break;

            case NativeMethods.WM_NCRBUTTONDOWN:
                var ncRButtonHitTest = (int)m.WParam.ToInt64();
                if (ncRButtonHitTest == HTCAPTION)
                {
                    // A real caption's right-click shows the system menu (Restore/Move/Close etc.)
                    // via the default proc - there's no such menu for this custom-drawn title bar,
                    // so this swallows the message either way. The header's own menu (rename) only
                    // pops up when the click landed on the rendered title text itself (see
                    // IsPointOverTitleText), not just anywhere in the caption/move-margin area.
                    ActivateFence();
                    if (IsPointOverTitleText(m.LParam))
                        ShowHeaderContextMenu();
                    return;
                }
                // Right-clicking a resize edge/corner activates too - this only ever fires while
                // inactive (resize hit-test codes stop occurring once active, see HitTest), so it's
                // simply "resize is available right now, and you right-clicked in that area".
                if (IsResizeHitTest(ncRButtonHitTest))
                    ActivateFence();
                break;

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

            case NativeMethods.WM_MENUSELECT:
                HandleMenuSelect(m.WParam, m.LParam);
                return;

            case NativeMethods.WM_CTLCOLOREDIT:
                // Sent by the rename EditBox to its owner (GetParent resolves to us even though
                // it's a top-level WS_POPUP, not a true child - see EditBox's class comment) each
                // time it needs to know what to paint itself with. Recoloring here, rather than in
                // EditBox itself, is the standard way to restyle a plain Edit control - it has no
                // owner-draw hook of its own the way buttons/menus do.
                NativeMethods.SetTextColor(m.WParam, ColorRef(Color.WhiteSmoke));
                NativeMethods.SetBkColor(m.WParam, ColorRef(ThemedBody));
                m.Result = GetThemeBrush();
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
                        rect.Left + OuterMargin, rect.Top + TopMargin, rect.Right - OuterMargin, rect.Bottom - OuterMargin));

                // OCD Fence Sizing: snap to the tightest fit right after a manual resize, on top of
                // whatever size was just dragged to - not after a move, see _resizeInProgress.
                if (_resizeInProgress && _model.OcdFenceSizing)
                    FormatDimensions(adjustWidth: true, adjustHeight: true);
                _resizeInProgress = false;
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                Reanchor();
                break;
        }
    }

    private static bool IsResizeHitTest(int hitTest) =>
        hitTest is HTLEFT or HTRIGHT or HTTOP or HTBOTTOM or HTTOPLEFT or HTTOPRIGHT or HTBOTTOMLEFT or HTBOTTOMRIGHT;

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

        // The settings button lives above the fence, in the taller TopMargin band - check it first
        // so it isn't shadowed by an HTTOP/HTTOPLEFT/HTTOPRIGHT resize result.
        if (_isActive && GetSettingsButtonRect(width - OuterMargin * 2).Contains(ToContent(new Point(x, y))))
            return HTCLIENT;

        int band = OuterMargin + ResizeMargin;
        // The top band is taller than the other three (see TopMargin) to make room for the settings
        // button, so it gets its own resize-grab threshold instead of sharing the plain one above.
        int topBand = TopMargin + ResizeMargin;

        if (_isActive)
        {
            // The margin band is a move handle instead of a resize band while active - the same
            // footprint resize used to claim, just reassigned rather than split into two adjacent
            // rings, so the drag margin can hug the fence's actual edge (see RenderAndPresent's
            // ThemedActiveBorder highlight) without an ambiguous strip where both would apply.
            // Resizing an active fence isn't available until it's deactivated again.
            if (x <= band || x >= width - band || y <= topBand || y >= height - band)
                return HTCAPTION;
        }
        else
        {
            bool left = x <= band;
            bool right = x >= width - band;
            bool top = y <= topBand;
            bool bottom = y >= height - band;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }

        // Empty space within the title bar itself (content-relative, not the margin above) works
        // the same way for a fence that still has one.
        if (!_model.HideTitle && y - TopMargin <= TitleBarHeight)
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
        if (_disposing)
            return;

        if (!NativeMethods.GetWindowRect(Handle, out var windowRect))
            return;

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        int contentWidth = width - OuterMargin * 2;
        int contentHeight = height - TopMargin - OuterMargin;
        if (contentWidth <= 0 || contentHeight <= 0)
            return;

        _scrollOffset = Math.Clamp(_scrollOffset, 0, GetMaxScroll(contentWidth, contentHeight));

        using var buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.Clear(Color.Transparent);

            // OuterMargin needs a non-zero (if faint) alpha - Windows treats fully transparent
            // (alpha 0) pixels of a layered window as click-through, so a truly invisible margin
            // couldn't receive the resize/move hit-testing it exists for. This gets drawn first and
            // the opaque fence body then covers all of it except that outer band.
            using (var marginFill = new SolidBrush(MarginFillColor))
                g.FillRectangle(marginFill, 0, 0, width, height);

            // Not clipped to the content rect here - PaintItems applies its own tighter clip via
            // CombineMode.Intersect before drawing any items (still preventing overflowed items from
            // painting into the near-transparent margin, since the intersection stays at least that
            // tight regardless of what's set here). Clipping this early would instead cut off the
            // outer half of the active border's thick stroke along straight edges while leaving the
            // rounded corners (which curve inward from the clip rect) unclipped - an asymmetry that
            // reads as a square notch at each corner rather than a uniformly thick rounded outline.
            g.SetClip(new Rectangle(0, 0, width, height));
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
            using var bodyFill = new SolidBrush(ThemedBody);
            g.FillPath(bodyFill, body);

            if (!_model.HideTitle)
            {
                using var titleFill = new SolidBrush(ThemedTitle);
                using var titlePath = RoundedRectTop(ToWindow(new Rectangle(0, 0, contentWidth - 1, TitleBarHeight)), CornerRadius);
                g.FillPath(titleFill, titlePath);
            }

            // A brighter, thicker border signals the fence is active - the margin band around it is
            // now a move handle (see HitTest), and this highlight hugs the fence's actual edge
            // directly rather than a separate frame floating out in the margin.
            using var borderPen = new Pen(_isActive ? ThemedActiveBorder : ThemedBorder, _isActive ? ActiveBorderWidth : 1f);
            // Pen.LineJoin defaults to Miter, which squares off the outer edge of a thick stroke at
            // the rounded corners instead of following their curve - Round keeps it hugging the arc.
            borderPen.LineJoin = LineJoin.Round;
            g.DrawPath(borderPen, body);

            if (!_model.HideTitle && _renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Name, _font, ToWindow(new Rectangle(14, 0, contentWidth - 22, TitleBarHeight)),
                    Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            if (_isActive)
            {
                // Filled first so the button reads as fully opaque - it lives in the near-transparent
                // TopMargin band (see MarginFillColor's own comment), and TextRenderer.DrawText below
                // only ever writes RGB, never alpha, so without an opaque backing shape under it the
                // label would inherit the margin's near-zero alpha and vanish once
                // WritePremultipliedPixels scales it down.
                var buttonRect = ToWindow(GetSettingsButtonRect(contentWidth));
                using var buttonPath = RoundedRect(buttonRect, 6);
                using var buttonFill = new SolidBrush(Accent);
                g.FillPath(buttonFill, buttonPath);
                using var buttonBorderPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
                g.DrawPath(buttonBorderPen, buttonPath);

                // GDI+'s DrawString instead of the GDI TextRenderer.DrawText used everywhere else in
                // this method - GDI's own ClearType antialiasing assumes a neutral/opaque background
                // and fringes with visible red/blue "shadow" pixels along each glyph's edge against a
                // saturated color like Accent; GDI+'s AntiAlias hint is plain grayscale, so it doesn't.
                var previousTextHint = g.TextRenderingHint;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var textBrush = new SolidBrush(Color.WhiteSmoke))
                using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Settings", _font, textBrush, buttonRect, textFormat);
                g.TextRenderingHint = previousTextHint;
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

        using var targetPen = new Pen(Color.FromArgb(200, Accent), 2);
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

    /// <summary>Like FileAtGridPosition, but only matches within the item's own label text - not
    /// its icon or the rest of the cell - and never matches at all when FenceModel.HideLabels has
    /// hidden every label. Used to gate right-click-to-rename (see ShowContextMenu) to specifically
    /// the shortcut name, matching the label rect PaintItems actually draws text into.</summary>
    private string? FileAtLabelPosition(Point contentLocation)
    {
        if (_model.HideLabels)
            return null;

        var index = IndexAtGridPosition(contentLocation);
        if (index is not int i)
            return null;

        var columns = GetColumns(GetContentSize().Width);
        var row = i / columns;
        var cellY = GridTop + GridPadding + row * EffectiveCellHeight - _scrollOffset;
        var labelTop = cellY + IconTopPadding + IconSize + 2;
        return contentLocation.Y >= labelTop ? _model.Files[i].Path : null;
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

    /// <summary>Right-click on an item's label text specifically (see FileAtLabelPosition) - not
    /// its icon, not empty grid space. Fence-level actions live elsewhere now: Rename only on the
    /// header (see ShowHeaderContextMenu) and Delete Fence only in the settings dropdown (see
    /// ShowFenceOptionsMenu) - a right-click anywhere else has nothing of its own to offer, so it
    /// just activates the fence (see ActivateFence) without popping up a menu. Open and Remove From
    /// Fence used to live here too; both stayed reachable another way (double-click, drag off the
    /// fence) so removing them from this menu didn't remove the functionality, just this shortcut
    /// to it.</summary>
    private void ShowContextMenu(Point clientPoint)
    {
        ActivateFence();
        _contextItem = FileAtLabelPosition(clientPoint);
        if (_contextItem is null)
            return;

        NativeMethods.GetCursorPos(out var pt);

        var hMenu = NativeMethods.CreatePopupMenu();
        try
        {
            AppendItem(hMenu, CmdRenameItem, false);

            ApplyDarkMenuTheme(hMenu);

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    /// <summary>Whether an NC-message screen point (see WM_NCRBUTTONDOWN) lands specifically on the
    /// fence's own rendered title text - not just anywhere in the caption/move-margin area that
    /// reports HTCAPTION - gating right-click-to-rename to the text itself (see
    /// ShowHeaderContextMenu). Always false with FenceModel.HideTitle set, since there's no title
    /// text drawn anywhere in that case (see RenderAndPresent). Mirrors the actual
    /// TextRenderer.DrawText call there - same rect origin/font - but measured to the text's real
    /// width rather than its full reserved rect, so a click past the end of a short name doesn't
    /// count as "on" it.</summary>
    private bool IsPointOverTitleText(IntPtr lParam)
    {
        if (_model.HideTitle || !NativeMethods.GetWindowRect(Handle, out var rect))
            return false;

        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);
        var content = ToContent(new Point(screenX - rect.Left, screenY - rect.Top));

        var maxWidth = Math.Max(0, GetContentSize().Width - 22);
        var textWidth = Math.Min(maxWidth, TextRenderer.MeasureText(_model.Name, _font).Width);
        return new Rectangle(14, 0, textWidth, TitleBarHeight).Contains(content);
    }

    /// <summary>Right-click on the title text specifically (see IsPointOverTitleText) - the only
    /// fence-level action tied to this specific spot rather than the fence generally.</summary>
    private void ShowHeaderContextMenu()
    {
        NativeMethods.GetCursorPos(out var pt);

        var hMenu = NativeMethods.CreatePopupMenu();
        try
        {
            AppendItem(hMenu, CmdRename, false);

            ApplyDarkMenuTheme(hMenu);

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    /// <summary>Per-fence settings, opened via the settings button that appears once this fence is
    /// active (see OnDeactivate and the settings-button hit-test carve-out). Top level: the three
    /// checkbox toggles, then a separator, then "OCD Formatting" (a submenu whose own "Fence
    /// Dimensions" header - a plain disabled label, not a further nested submenu, see AppendHeader -
    /// sits above its three resize actions), then another separator, then "Delete Fence".
    /// AppendPopup stays available as general infrastructure for a real third level if a future
    /// subcategory needs one. "Delete Fence" lives here rather than any right-click menu now, same
    /// as "Rename" moved to the header's own context menu - see ShowContextMenu/
    /// ShowHeaderContextMenu.</summary>
    private void ShowFenceOptionsMenu()
    {
        var contentSize = GetContentSize();
        var buttonRect = GetSettingsButtonRect(contentSize.Width);
        var menuPoint = PointToScreen(ToWindow(new Point(buttonRect.Right + 2, buttonRect.Y)));

        var hOcdMenu = NativeMethods.CreatePopupMenu();
        var hColorMenu = NativeMethods.CreatePopupMenu();
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

            AppendItem(hColorMenu, CmdColorDefault, _model.TintColor is null);
            for (var i = 0; i < ColorPresets.Length; i++)
                AppendItem(hColorMenu, CmdColorPresetBase + i, _model.TintColor == ColorPresets[i].ToArgb());
            NativeMethods.AppendMenu(hColorMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendItem(hColorMenu, CmdColorCustom, false);

            AppendItem(hMenu, CmdToggleHideLabels, _model.HideLabels);
            AppendItem(hMenu, CmdToggleHideTitle, _model.HideTitle);
            AppendItem(hMenu, CmdToggleOcdSizing, _model.OcdFenceSizing);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendPopup(hMenu, hOcdMenu, TagOcdFormattingHeader);
            AppendPopup(hMenu, hColorMenu, TagColorHeader);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendItem(hMenu, CmdDelete, false);

            ApplyDarkMenuTheme(hMenu);

            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_LEFTBUTTON, menuPoint.X, menuPoint.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu); // recursively destroys the attached submenu too
            HideMenuItemTooltip(); // WM_MENUSELECT's own close notification already does this normally - just a backstop
        }
    }

    /// <summary>WM_DRAWITEM only paints each item's own row - the popup's outer margin/border is
    /// separately filled by the menu's own background brush, which defaults to the system's (light)
    /// COLOR_MENU and shows through as a stray light border around the dark rows unless replaced
    /// here to match. MIM_APPLYTOSUBMENUS cascades this to any attached submenus too.</summary>
    private void ApplyDarkMenuTheme(IntPtr hMenu)
    {
        var menuInfo = new MENUINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUINFO>(),
            fMask = NativeMethods.MIM_BACKGROUND | NativeMethods.MIM_APPLYTOSUBMENUS,
            hbrBack = GetThemeBrush(),
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

    private readonly record struct MenuRowStyle(string Text, bool HasCheckbox, bool IsHeader, Color? Swatch = null);

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
        CmdToggleOcdSizing => new MenuRowStyle("OCD Fence Sizing", true, false),
        TagColorHeader => new MenuRowStyle("Fence Color", false, false),
        CmdColorDefault => new MenuRowStyle("Default", false, false, DefaultBodyColor),
        CmdColorCustom => new MenuRowStyle("Custom...", false, false),
        >= CmdColorPresetBase and < CmdColorPresetBase + 100 =>
            new MenuRowStyle(GetColorPresetName(tag - CmdColorPresetBase), false, false, GetColorPreset(tag - CmdColorPresetBase)),
        CmdRenameItem => new MenuRowStyle("Rename", false, false),
        CmdRename => new MenuRowStyle("Rename", false, false),
        CmdDelete => new MenuRowStyle("Delete Fence", false, false),
        _ => new MenuRowStyle(string.Empty, false, false),
    };

    /// <summary>Preset name shown next to its swatch (e.g. "Red") - index must line up with
    /// ColorPresets, see ShowFenceOptionsMenu/CmdColorPresetBase.</summary>
    private static readonly string[] ColorPresetNames = { "Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Pink" };

    private static Color GetColorPreset(int index) => index >= 0 && index < ColorPresets.Length ? ColorPresets[index] : Color.Empty;
    private static string GetColorPresetName(int index) => index >= 0 && index < ColorPresetNames.Length ? ColorPresetNames[index] : string.Empty;

    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    /// <summary>Blends a user-picked fence color into one of the fixed dark-theme fill colors
    /// (body/title) rather than replacing it outright - keeps the tint recognizable while the fence
    /// still reads as part of the same dark theme even when the picked color is fully saturated
    /// (e.g. a pure ColorDialog pick), since only part of it makes it into the final fill.</summary>
    private static Color Tint(Color baseColor, Color? tint, double amount = 0.55) =>
        tint is not { } t
            ? baseColor
            : Color.FromArgb(255,
                (int)Math.Round(baseColor.R + (t.R - baseColor.R) * amount),
                (int)Math.Round(baseColor.G + (t.G - baseColor.G) * amount),
                (int)Math.Round(baseColor.B + (t.B - baseColor.B) * amount));

    /// <summary>Only rows worth explaining get one - most menu items are self-explanatory from
    /// their label alone.</summary>
    private static string? GetMenuTooltipText(int commandId) => commandId switch
    {
        CmdToggleOcdSizing =>
            "After you resize this fence by hand, automatically snap it to the tightest size that fits its icons (same as OCD Formatting > Both).",
        _ => null,
    };

    /// <summary>WM_MENUSELECT fires as the highlighted item changes in any menu owned by this
    /// window, including nested submenus - used to track a hover tooltip since a raw HMENU has no
    /// hover events of its own the way a real control would.</summary>
    private void HandleMenuSelect(IntPtr wParam, IntPtr lParam)
    {
        var packed = wParam.ToInt64();
        var itemIdOrPosition = (int)(packed & 0xFFFF);
        var flags = (uint)((packed >> 16) & 0xFFFF);

        // itemIdOrPosition is a real command id only for a plain (non-popup) item - for a
        // submenu-anchor row (MF_POPUP set in flags, see AppendPopup) it's that submenu's position
        // within its parent instead, which would collide with unrelated command ids by coincidence.
        // The 0xFFFF/null-lParam pair is Windows' own sentinel for "the menu just closed".
        if (lParam == IntPtr.Zero || itemIdOrPosition == 0xFFFF || (flags & NativeMethods.MF_POPUP_FLAG) != 0)
        {
            HideMenuItemTooltip();
            return;
        }

        var tooltipText = GetMenuTooltipText(itemIdOrPosition);
        if (tooltipText is null)
            HideMenuItemTooltip();
        else
            ShowMenuItemTooltip(tooltipText);
    }

    private void EnsureMenuTooltip()
    {
        if (_menuTooltip != IntPtr.Zero)
            return;

        // WS_EX_TOPMOST here (and reasserted via SetWindowPos in ShowMenuItemTooltip) so the tooltip
        // renders above the currently-tracked popup menu instead of behind it - a plain owned popup
        // isn't guaranteed to stay above a native menu's own (topmost-ish) tracking window.
        _menuTooltip = NativeMethods.CreateWindowEx(NativeMethods.WS_EX_TOPMOST, "tooltips_class32", string.Empty,
            NativeMethods.WS_POPUP | (int)NativeMethods.TTS_ALWAYSTIP | (int)NativeMethods.TTS_NOPREFIX,
            0, 0, 0, 0, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // A themed tooltip draws itself via UxTheme and ignores TTM_SETTIPBKCOLOR/TTM_SETTIPTEXTCOLOR
        // entirely - opting out of theming here is what makes those two calls actually take effect.
        NativeMethods.SetWindowTheme(_menuTooltip, string.Empty, string.Empty);

        var toolInfo = new TOOLINFO
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFO>(),
            uFlags = NativeMethods.TTF_TRACK | NativeMethods.TTF_ABSOLUTE,
            hwnd = Handle,
            uId = (IntPtr)1,
            lpszText = string.Empty,
        };
        NativeMethods.SendMessage(_menuTooltip, NativeMethods.TTM_ADDTOOLW, IntPtr.Zero, ref toolInfo);
    }

    private void ShowMenuItemTooltip(string text)
    {
        EnsureMenuTooltip();

        // Set on every show rather than once at creation (EnsureMenuTooltip only runs the first
        // time) - the fence's own color, and so ThemedBody, can change at runtime via the "Fence
        // Color" submenu this same tooltip is used from.
        NativeMethods.SendMessage(_menuTooltip, (uint)NativeMethods.TTM_SETTIPBKCOLOR, (IntPtr)ColorRef(ThemedBody), IntPtr.Zero);
        NativeMethods.SendMessage(_menuTooltip, (uint)NativeMethods.TTM_SETTIPTEXTCOLOR, (IntPtr)ColorRef(Color.WhiteSmoke), IntPtr.Zero);

        var toolInfo = new TOOLINFO
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFO>(),
            uFlags = NativeMethods.TTF_TRACK | NativeMethods.TTF_ABSOLUTE,
            hwnd = Handle,
            uId = (IntPtr)1,
            lpszText = text,
        };
        NativeMethods.SendMessage(_menuTooltip, NativeMethods.TTM_UPDATETIPTEXTW, IntPtr.Zero, ref toolInfo);

        NativeMethods.GetCursorPos(out var pt);
        // Offset from the cursor so the tooltip doesn't sit directly under it - short casts pack
        // this as signed 16-bit components the same way WM_NCHITTEST's own lParam is unpacked
        // elsewhere, since screen coordinates can be negative on a multi-monitor desktop.
        var x = (short)(pt.X + 18);
        var y = (short)(pt.Y + 22);
        var position = (IntPtr)((int)(ushort)x | ((int)(ushort)y << 16));
        NativeMethods.SendMessage(_menuTooltip, NativeMethods.TTM_TRACKPOSITION, IntPtr.Zero, position);
        NativeMethods.SendMessage(_menuTooltip, NativeMethods.TTM_TRACKACTIVATE, (IntPtr)1, ref toolInfo);

        // The popup menu currently being tracked can still end up above an already-topmost tooltip
        // depending on creation order - reasserting topmost (without stealing activation/focus from
        // the menu) each time keeps the tooltip visibly on top of it instead of hidden behind.
        NativeMethods.SetWindowPos(_menuTooltip, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void HideMenuItemTooltip()
    {
        if (_menuTooltip == IntPtr.Zero)
            return;

        var toolInfo = new TOOLINFO
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFO>(),
            hwnd = Handle,
            uId = (IntPtr)1,
        };
        NativeMethods.SendMessage(_menuTooltip, NativeMethods.TTM_TRACKACTIVATE, IntPtr.Zero, ref toolInfo);
    }

    private void MeasureMenuItem(ref MEASUREITEMSTRUCT mis)
    {
        var style = GetMenuRowStyle((int)mis.itemData);
        var size = TextRenderer.MeasureText(style.Text, _font);
        var leftReserve = style.HasCheckbox || style.Swatch is not null ? MenuCheckboxSize + MenuTextPadding : 0;
        // No right-side reserve for a submenu arrow - Windows always adds its own fixed arrow
        // margin outside whatever width we report for an MF_POPUP row, so reserving space for one
        // here too just doubled up as a second, hand-drawn arrow next to the native one.
        mis.itemWidth = (uint)(MenuTextPadding + leftReserve + size.Width + MenuTextPadding);
        mis.itemHeight = (uint)Math.Max(size.Height + 8, 22);
    }

    /// <summary>Paints one row of the fence-options dropdown to match the fence's own dark theme,
    /// instead of the native Windows menu look - background, a hand-drawn checkbox (no checkmark
    /// glyph font, since an icon font's glyphs aren't guaranteed to be installed/rendering on every
    /// machine - a missing one draws as nothing at all rather than some visible fallback), and the
    /// row's label text. A submenu row's arrow indicator is left to Windows to draw natively (see
    /// MeasureMenuItem) - drawing our own there duplicated it.
    private void DrawMenuItem(DRAWITEMSTRUCT dis)
    {
        using var g = Graphics.FromHdc(dis.hDC);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Rectangle.FromLTRB(dis.rcItem.Left, dis.rcItem.Top, dis.rcItem.Right, dis.rcItem.Bottom);
        var style = GetMenuRowStyle((int)dis.itemData);
        var selected = !style.IsHeader && (dis.itemState & NativeMethods.ODS_SELECTED) != 0;
        var isChecked = (dis.itemState & NativeMethods.ODS_CHECKED) != 0;

        using (var background = new SolidBrush(selected ? ThemedMenuSelected : ThemedBody))
            g.FillRectangle(background, rect);

        if (style.HasCheckbox)
        {
            var checkRect = new Rectangle(rect.X + MenuTextPadding, rect.Y + (rect.Height - MenuCheckboxSize) / 2, MenuCheckboxSize, MenuCheckboxSize);
            using (var checkPen = new Pen(ThemedCheckboxBorder))
                g.DrawRectangle(checkPen, checkRect);

            if (isChecked)
            {
                using var checkMarkPen = new Pen(Accent, 2);
                g.DrawLine(checkMarkPen, checkRect.X + 2, checkRect.Y + 6, checkRect.X + 5, checkRect.Y + 9);
                g.DrawLine(checkMarkPen, checkRect.X + 5, checkRect.Y + 9, checkRect.X + 10, checkRect.Y + 2);
            }
        }
        else if (style.Swatch is { } swatchColor)
        {
            var swatchRect = new Rectangle(rect.X + MenuTextPadding, rect.Y + (rect.Height - MenuCheckboxSize) / 2, MenuCheckboxSize, MenuCheckboxSize);
            using (var swatchBrush = new SolidBrush(swatchColor))
                g.FillEllipse(swatchBrush, swatchRect);

            // The currently-active color gets a bright ring around its swatch instead of a
            // checkbox's checkmark - there's no empty "unchecked" state to draw here either way.
            using var swatchPen = new Pen(isChecked ? Accent : ThemedCheckboxBorder, isChecked ? 2 : 1);
            g.DrawEllipse(swatchPen, swatchRect);
        }

        var textLeft = rect.X + MenuTextPadding + (style.HasCheckbox || style.Swatch is not null ? MenuCheckboxSize + MenuTextPadding : 0);
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
            case CmdRenameItem: BeginRenameItem(_contextItem); break;
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdToggleHideTitle: ToggleHideTitle(); break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
            case CmdToggleOcdSizing: ToggleOcdFenceSizing(); break;
            case CmdColorDefault: SetTintColor(null); break;
            case CmdColorCustom: PickCustomColor(); break;
            case >= CmdColorPresetBase and < CmdColorPresetBase + 100:
                var presetColor = GetColorPreset(id - CmdColorPresetBase);
                if (presetColor != Color.Empty)
                    SetTintColor(presetColor);
                break;
        }
    }

    private void SetTintColor(Color? color)
    {
        _manager.SetTintColor(FenceId, color);
        RenderAndPresent();
    }

    /// <summary>"Fence Color > Custom..." - the settings menu has already closed by the time this runs
    /// (HandleCommand fires from WM_COMMAND after TrackPopupMenuEx returns), so a modal ColorDialog
    /// here doesn't fight it for the message loop.</summary>
    private void PickCustomColor()
    {
        using var dialog = new ColorDialog
        {
            Color = CurrentTint ?? DefaultBodyColor,
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetTintColor(dialog.Color);
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

    private void ToggleOcdFenceSizing()
    {
        _manager.SetOcdFenceSizing(FenceId, !_model.OcdFenceSizing);
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
        // Rounds to the nearest row rather than always truncating down to whatever's fully visible -
        // adding half a row's height before the integer division means a row that's more than half
        // shown counts as shown (the fence grows/keeps enough height for it), not cut off.
        var currentVisibleRows = Math.Max(1, (availableHeight + EffectiveCellHeight / 2) / EffectiveCellHeight);
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
            newBounds.Width + OuterMargin * 2, newBounds.Height + TopMargin + OuterMargin,
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
