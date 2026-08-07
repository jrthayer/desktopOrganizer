using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Fences.Native;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Fences.UI;

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
/// drag-and-drop model is based on (see README's Credits section). Dropping a file here just adds
/// a reference to it; if that file lives directly on the real desktop, its real icon gets moved
/// into a hidden folder so it isn't visible twice (see FenceManager.AddFiles and DesktopIconHider) -
/// anything dragged in from elsewhere is left completely alone.
///
/// Rendering is pushed via UpdateLayeredWindow (see LayeredWindowPresenter) rather than drawn in
/// response to WM_PAINT with a SetWindowRgn-clipped shape. The region approach was tried first and
/// works, but a GDI region is a hard-edged, non-antialiased mask, so the rounded corners always
/// came out as a visible pixel staircase no matter the radius. Per-pixel alpha draws a genuinely
/// smooth edge, and Windows uses that same alpha for hit-testing, so fully-transparent pixels
/// (outside the rounded corner) are naturally click-through with no region needed at all.
///
/// Move, resize, snap, rename, and the Settings button/dropdown are all LayeredWidgetForm's own now -
/// this class only supplies the small hooks those need (GetCurrentBody, Title, BuildSettingsRows,
/// etc.) plus everything genuinely fence-specific: the icon grid itself, OCD Fence Sizing, and the
/// z-order restack to the bottom (see OnDragEnd).
/// </summary>
internal sealed class FenceForm : LayeredWidgetForm
{
    internal const int TitleBarHeight = 26;
    // Extra invisible band around the visible fence, purely so the resize cursor is easier to
    // grab - only possible now that per-pixel alpha (not SetWindowRgn) defines the window's shape,
    // since Windows treats fully-transparent pixels as click-through; a hard region couldn't do
    // this at all (you can't hit-test past a window's own rectangle). Painted at a barely-non-zero
    // alpha (see MarginFillColor) since alpha 0 would be click-through too, defeating the point.
    private const int OuterMarginPx = 13;

    // The settings button sits above the fence, flush with its top-right corner, and doesn't fit
    // inside the plain OuterMargin band (13px) with any breathing room, so the window is extended
    // *only on top* by this much extra - every other edge (left/right/bottom, and their resize-grab
    // bands) stays exactly OuterMargin. Grown by the same +2 as SettingsButtonGap, so the breathing
    // room above the button row (between it and this window's own top edge) stays what it was
    // before that gap grew.
    private const int SettingsButtonOverhang = 19;
    private const int TopMargin = OuterMarginPx + SettingsButtonOverhang;
    private const int CornerRadius = 22;

    // LayeredWidgetForm's own OuterMargin/TopBand/BottomBand/MaxTopBand contract, left entirely to
    // this override rather than generalized in the base - this fence's own split is asymmetric
    // (TopBand collapses to 0 once flipped rather than mirroring OuterMargin/TopMargin the way
    // BottomBand does, see its own comment for why), which is Fence-specific reasoning about
    // Fence's own margin band, not something worth generalizing from a single example.
    protected override int OuterMargin => OuterMarginPx;

    /// <summary>The margin band on whichever side currently holds the button row - see
    /// ButtonRowAtBottom. TopMargin-sized there, same as always; zero on the top side once flipped
    /// (see BottomBand below for why).</summary>
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMargin;

    /// <summary>The margin band on whichever side does NOT currently hold the button row - see
    /// ButtonRowAtBottom. Normally a plain OuterMargin, like the left/right/bottom edges always
    /// are - except once flipped, when TopBand above goes to 0 instead: whatever keeps this app's
    /// own drag loop from letting the fence's edge fully reach the screen's own edge (observed
    /// settling exactly OuterMargin short of it, every time, even after the flip first shrank it
    /// from TopMargin down to OuterMargin) reacts to any nonzero margin there at all, not just a
    /// wide one - only removing it outright lets the fence sit flush with the very top of the
    /// screen. The resize-grab hit-test zone on that side still isn't literally zero-width (see
    /// ResizeHitTest's own ResizeMargin addition), just without this extra invisible cushion beyond
    /// the body's own edge.</summary>
    protected override int BottomBand => ButtonRowAtBottom ? TopMargin : OuterMargin;

    protected override int MaxTopBand => TopMargin;

    // Every WM_*/HT* message/hit-test code with a shared home (move/resize/rename/caption codes) is
    // LayeredWidgetForm's own now - only the messages with no shared home stay declared here.
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_COMMAND = 0x0111;

    // CmdToggleHideTitle/CmdToggleFullOpacityOnHover/CmdColorDefault/CmdColorCustom/CmdColorEyedrop/
    // CmdColorPresetBase are LayeredWidgetForm's own now (negative ids - see its own comment for why
    // that range can never collide with these).
    private const int CmdRenameItem = 6;
    private const int CmdToggleHideLabels = 7;
    private const int CmdResizeBoth = 9;
    private const int CmdResizeLeftRight = 10;
    private const int CmdResizeTopDown = 11;
    private const int CmdToggleOcdSizing = 12;

    // Not real WM_COMMAND ids - just Row.Id values for the non-clickable section headers in
    // BuildSettingsRows' dropdown (DropdownMenu.Row.IsHeader rows don't dispatch a command either
    // way, so these only need to be distinct from real command ids, never looked up).
    private const int TagFenceDimensionsHeader = 1004;
    private const int TagHeaderDarknessHeader = 1005;
    private const int TagFenceOpacityHeader = 1006;
    private const int TagTintStrengthHeader = 1007;
    private const int TagMarginHeader = 1008;

    private const int IconSize = 48;
    private const int GridPadding = 8;
    private const int IconTopPadding = 8;
    private const int CellWidth = 84;
    private const int CellHeight = 94;
    private const int ScrollbarWidth = 6;
    private const int ScrollbarMargin = 3;
    // SettingsButtonGap (the vertical gap between the button row's bottom edge and the fence's own
    // top edge) is LayeredWidgetForm's own default (6) now, unchanged from what this used to declare
    // itself - TopMargin's own extra room above OuterMargin is still sized for it.
    // Shared size for the "+" (copy-this-fence's-settings) and "x" (delete-fence) buttons - both
    // square, same height as Settings, and chained immediately adjacent to it and each other (see
    // GetNewFenceButtonRect/GetDeleteButtonRect) rather than anchored to their own corners.
    private const int SmallButtonSize = 22;
    private const int ButtonSpacing = 4;
    private const int MenuCheckboxSize = 12;
    private const int MenuTextPadding = 8;

    private readonly FenceManager _manager;
    private readonly FenceModel _model;
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Dictionary<string, Icon?> _iconCache = new();
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

    // A real child Button control was tried here first, but a window painted via UpdateLayeredWindow
    // (see RenderAndPresent/LayeredWindowPresenter) doesn't compose child windows on top of itself -
    // it just never appeared, clickable or not. So this is drawn like everything else on the fence
    // (see PaintContent) and hit-tested by hand instead: armed on OnMouseDown, fired on the matching
    // OnMouseUp only if the cursor is still over it, mirroring the arm-then-fire pattern used for
    // drag-vs-click elsewhere in this file. Firing on down instead of up was tried too, early in
    // this button's history - opening the dropdown while the mouse button is still physically down
    // raced with TrackPopupMenuEx's own capture and made it flash open and closed.
    private bool _settingsButtonArmed;
    // Same arm-then-fire pattern as _settingsButtonArmed above, for the "+"/"x" buttons next to it.
    private bool _newFenceButtonArmed;
    private bool _deleteButtonArmed;
    // OwnerDraw, not just BackColor/ForeColor - same reasoning as DropdownMenu's own _toolTip field
    // comment: a themed (UxTheme) system draws a plain ToolTip natively and ignores those properties
    // entirely.
    private readonly ToolTip _toolTip = new() { OwnerDraw = true };
    // Whichever of the "+"/"x" buttons' tooltip text is currently shown, or null - compared against
    // on every mouse-move (see UpdateButtonTooltips) so ToolTip.Show isn't re-issued (and
    // re-timed/re-flickered) for every pixel of movement while already hovering the same button.
    private string? _visibleButtonTooltip;

    // Whether the drag that's about to start is a resize (as opposed to a move) - LayeredWidgetForm's
    // own IsResizing now (set from OnNcLButtonDown's own base default); read back on OnDragEnd to
    // decide whether OcdFenceSizing should auto-run "Both" now that the resize is done.

    public Guid FenceId => _model.Id;

    /// <summary>Which model LayeredWidgetForm's own theme derivation (ThemedBody/Accent/etc) and
    /// generic Settings-dropdown rows (Hide Title, Full Opacity When Active, the color grid/sliders)
    /// read from - FenceModel already implements IWidgetStyle.</summary>
    protected override IWidgetStyle Style => _model;

    protected override bool HideTitle
    {
        get => _model.HideTitle;
        set
        {
            _manager.SetHideTitle(FenceId, value);
            // Changes GridTop (see its own comment), which OCD Fence Sizing's fit is based on - only
            // height can possibly need to change here, never the columns/width.
            if (_model.OcdFenceSizing)
                FormatDimensions(adjustWidth: false, adjustHeight: true);
            RenderAndPresent();
        }
    }

    /// <summary>Used only for the cross-fence "Move to {name}" drag hint (see ComputeDragHint) -
    /// every other cross-fence reference goes through FenceId/FenceManager instead.</summary>
    internal string FenceName => _model.Name;

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

            ButtonRowAtBottom = ComputeButtonRowAtBottom(_model.Bounds.Location, TopMargin);

            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
            cp.X = _model.Bounds.X - OuterMargin;
            cp.Y = _model.Bounds.Y - TopBand;
            cp.Width = _model.Bounds.Width + OuterMargin * 2;
            cp.Height = _model.Bounds.Height + TopBand + BottomBand;
            return cp;
        }
    }

    public FenceForm(FenceModel model, FenceManager manager, IDesktopAnchorStrategy anchorStrategy)
        : base(model.Opacity / 100f, manager)
    {
        _model = model;
        _manager = manager;
        _anchorStrategy = anchorStrategy;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        // LayeredWidgetForm's own default rename hit-testing/EditBox/title-context-menu (IsOverTitleRow,
        // BeginRename, BuildTitleContextMenu) all measure against Control.Font - without this, they'd
        // measure against the WinForms default (Microsoft Sans Serif) while PaintContent actually draws
        // the title with _font (Segoe UI 9), so the rename hit-rect/box would be sized wrong.
        Font = _font;
        _toolTip.Draw += DrawTooltip;

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

    // GetContentSize/ToContent/ToWindow/ToScreen are LayeredWidgetForm's own now - all grid/
    // hit-test math below is in "content" space (the visible fence's size minus OuterMargin on the
    // left/right/non-button-row side and TopBand/BottomBand's button-row side - see
    // ButtonRowAtBottom).

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

    /// <summary>Immediately inside the settings button (i.e. between it and the fence body) rather
    /// than anchored to its own corner - moves and flips sides together with LayeredWidgetForm's own
    /// GetSettingsButtonRect as a pair, always adjacent to it. Duplicates this fence's settings into
    /// a new, empty fence (see FenceManager.CreateFenceLike) when clicked.</summary>
    private Rectangle GetNewFenceButtonRect(int contentWidth, bool onLeft)
    {
        var settingsRect = GetSettingsButtonRect(contentWidth, onLeft);
        var x = onLeft ? settingsRect.Right + ButtonSpacing : settingsRect.X - ButtonSpacing - SmallButtonSize;
        return new Rectangle(x, settingsRect.Y, SmallButtonSize, SettingsButtonHeight);
    }

    /// <summary>Chained off GetNewFenceButtonRect the same way that one chains off
    /// GetSettingsButtonRect - the three buttons move/flip together as one group, always in the same
    /// relative order (Settings outermost, then "+", then this one, innermost/closest to the fence
    /// body). Deletes the fence (with confirmation - see ConfirmDelete) when clicked; this replaces
    /// "Delete Fence" as a row inside the settings dropdown, which no longer has one.</summary>
    private Rectangle GetDeleteButtonRect(int contentWidth, bool onLeft)
    {
        var newFenceRect = GetNewFenceButtonRect(contentWidth, onLeft);
        var x = onLeft ? newFenceRect.Right + ButtonSpacing : newFenceRect.X - ButtonSpacing - SmallButtonSize;
        return new Rectangle(x, newFenceRect.Y, SmallButtonSize, SettingsButtonHeight);
    }

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

    /// <summary>LayeredWidgetForm's own Dispose(bool) calls this (having already set IsDisposing=true
    /// and torn down the rename box/title menu/settings dropdown it now owns) before disposing
    /// RenderOpacity/the theme brush itself. Destroying the native window via DestroyWindow, as part
    /// of the OS's normal deactivate-before-destroy sequence, synchronously delivers WM_ACTIVATE to
    /// this same window while WndProc is still hooked up, reaching OnDeactivate -> RenderAndPresent ->
    /// PaintItems before this call even returns - without IsDisposing already set, that repaint would
    /// use _iconCache's Icon objects just disposed a few lines down, which throws (Icon is an
    /// ObjectDisposedException-checked handle, same as Control.Handle).</summary>
    protected override void DisposeOwnedResources()
    {
        _itemRenameBox?.Dispose();
        _dragGhost?.Dispose();
        _toolTip.Dispose();
        _font.Dispose();
        foreach (var icon in _iconCache.Values)
            icon?.Dispose();
    }

    // Activation (settings button + drag-margin visibility, see WidgetActivation) is intentionally
    // NOT driven by OnActivated - that fires for any click that gives the window OS focus, including
    // a plain click on a shortcut just to use it. It's set explicitly instead, only for right-click
    // (anywhere) or a title-bar click (either button) - see LayeredWidgetForm's own WM_NCLBUTTONDOWN/
    // WM_NCRBUTTONDOWN handling and ShowContextMenu. Resizing deliberately does NOT activate the
    // fence - HitTest turns the whole margin band into a move handle once already active, so resize
    // and move never contend for the same pixels, but that also means resize has to stay unavailable
    // to the (fence, click) pairs that would otherwise be ambiguous. Losing focus still deactivates
    // unconditionally - see LayeredWidgetForm's own OnDeactivate, which now handles that.

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Move;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        // e.X/e.Y are screen coordinates (unlike MouseEventArgs.Location) - PointToClient first to
        // land in the same window-relative space ToContent/IndexAtGridPosition expect elsewhere.
        var contentPoint = ToContent(PointToClient(new Point(e.X, e.Y)));
        if (IndexAtGridPosition(contentPoint) is int index && _manager.IsRecycleBinAt(FenceId, index))
            _manager.DeletePaths(paths, Handle);
        else
            _manager.AddFiles(FenceId, paths);
        RenderAndPresent();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // Rename is only reachable via the title text itself (double-click or right-click, gated on
        // IsOverTitleRow - now LayeredWidgetForm's own) - no fallback here when FenceModel.HideTitle
        // leaves no title bar to click at all; renaming just isn't reachable that way then, rather
        // than an empty double-click anywhere substituting for it.
        if (IndexAtGridPosition(ToContent(e.Location)) is not int index)
            return;
        var item = _model.Files[index];
        // FenceItem.Path is the Recycle Bin's shell-namespace CLSID string for icon-extraction
        // purposes only (see FenceItem.IsRecycleBin) - opening it needs the "shell:" alias instead,
        // a different shell path grammar OpenItem's ShellExecute still resolves the same way.
        OpenItem(item.IsRecycleBin ? "shell:RecycleBinFolder" : item.Path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentSize = GetContentSize();
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);

        if (ShowsButtons && GetSettingsButtonRect(contentSize.Width, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }

        if (ShowsButtons && GetNewFenceButtonRect(contentSize.Width, onLeft).Contains(contentPoint))
        {
            _newFenceButtonArmed = true;
            return;
        }

        if (ShowsButtons && GetDeleteButtonRect(contentSize.Width, onLeft).Contains(contentPoint))
        {
            _deleteButtonArmed = true;
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
            _dragGhost?.SetHint(ComputeDragHint(e.Location));
            _dragGhost?.MoveTo(PointToScreen(e.Location));
            RenderAndPresent();
            return;
        }

        SetHoverIndex(IndexAtGridPosition(ToContent(e.Location)) ?? -1);
        UpdateButtonTooltips(e.Location);
    }

    /// <summary>Shows/hides the "Copy Fence"/"Delete Fence" tooltip over the "+"/"x" buttons - only
    /// meaningful while they're actually visible (ShowsButtons), and only re-issued on an
    /// actual change of which button (if any) is hovered, rather than on every mouse-move, so
    /// ToolTip.Show isn't re-triggered (and re-timed/re-flickered) for every pixel of movement while
    /// already hovering the same one.</summary>
    private void UpdateButtonTooltips(Point windowLocation)
    {
        var contentSize = GetContentSize();
        var contentPoint = ToContent(windowLocation);
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);

        string? text = null;
        Rectangle buttonRect = default;
        if (ShowsButtons)
        {
            if (GetNewFenceButtonRect(contentSize.Width, onLeft) is var newFenceRect && newFenceRect.Contains(contentPoint))
            {
                text = "Copy Fence";
                buttonRect = newFenceRect;
            }
            else if (GetDeleteButtonRect(contentSize.Width, onLeft) is var deleteRect && deleteRect.Contains(contentPoint))
            {
                text = "Delete Fence";
                buttonRect = deleteRect;
            }
        }

        if (text == _visibleButtonTooltip)
            return;
        _visibleButtonTooltip = text;

        if (text is not null)
        {
            var windowRect = ToWindow(buttonRect);

            // Anchoring the tooltip's left edge to the button's left edge, as below, is fine almost
            // everywhere - but for a fence sitting close enough to the right edge of its monitor,
            // the tooltip (extending further right from there, past the button itself) could
            // overflow off-screen. Left to the native tooltip control's own automatic "keep me on
            // screen" repositioning, the relocated tooltip ended up landing right on top of the
            // cursor - this window's own hover-tracking then saw a different top-level window now
            // covering that exact point, treated it as the cursor having left, hid the tooltip,
            // immediately saw the cursor was still right there and showed it again - a tight
            // show/hide flicker loop. Computing a safe position ourselves up front (right-aligning
            // to the button's right edge instead, only when actually needed) avoids that native
            // reposition ever having a reason to kick in.
            var formScreenOrigin = PointToScreen(Point.Empty);
            var workingArea = Screen.FromControl(this).WorkingArea;
            var tooltipWidth = TextRenderer.MeasureText(text, _font).Width + 16;
            var x = formScreenOrigin.X + windowRect.Left + tooltipWidth > workingArea.Right
                ? windowRect.Right - tooltipWidth
                : windowRect.Left;

            _toolTip.Show(text, this, x, windowRect.Bottom + 4);
        }
        else
        {
            _toolTip.Hide(this);
        }
    }

    /// <summary>OwnerDraw's paint hook (see _toolTip's own field comment for why this is needed at
    /// all) - dark background/border matching the rest of this fence's theme instead of a native
    /// tooltip's white/light default. SettingsMenuTooltipColor (LayeredWidgetForm's own), not
    /// ThemedBody - same fixed-WhiteSmoke-text reasoning as ChromeFill.</summary>
    private void DrawTooltip(object? sender, DrawToolTipEventArgs e)
    {
        using (var background = new SolidBrush(SettingsMenuTooltipColor))
            e.Graphics.FillRectangle(background, e.Bounds);
        using (var borderPen = new Pen(Color.FromArgb(255, 20, 20, 24)))
            e.Graphics.DrawRectangle(borderPen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, _font, e.Bounds, Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Live drop-target hint for an in-app item drag (see _draggingIndex), shown in the
    /// pill below the drag ghost - mirrors the tooltip Windows itself shows while dragging a file
    /// over a folder or the desktop Recycle Bin icon. Mirrors OnMouseUp's own same-fence/cross-
    /// fence/neither resolution exactly (including the recycle-bin sub-case), just read-only (no
    /// mutation) and re-run on every mouse-move rather than only at drop time. Never returns a hint
    /// while the trash item itself is what's being dragged - same reasoning as OnMouseUp's own
    /// isSourceTrash guard, repositioning the trash icon onto its own cell is never a delete, and
    /// dragging it to another fence or off onto the desktop isn't really a "move"/"remove" either
    /// since it always stays exactly one Recycle Bin, just relocated.</summary>
    private string? ComputeDragHint(Point windowLocation)
    {
        if (_draggingIndex is not int sourceIndex || _model.Files[sourceIndex].IsRecycleBin)
            return null;

        var contentPoint = ToContent(windowLocation);
        if (new Rectangle(Point.Empty, GetContentSize()).Contains(contentPoint))
        {
            var index = IndexAtGridPosition(contentPoint) ?? _model.Files.Count;
            if (_manager.IsRecycleBinAt(FenceId, index))
                return "Move to Recycle Bin â†’";
            // Landing back on (or adjacent to) its own starting cell isn't really a reorder, but
            // there's no cheap way to tell "would actually move" from "would land right back where
            // it started" here without duplicating MoveFile's own index-shift math - and showing the
            // hint a little early/late right at the source cell is harmless, unlike misreporting a
            // Recycle Bin/cross-fence target.
            return "Change Position";
        }

        var screenPoint = PointToScreen(windowLocation);
        if (_manager.FindFenceAt(screenPoint, FenceId) is { } targetForm)
        {
            var index = targetForm.IndexForExternalDrop(screenPoint);
            return _manager.IsRecycleBinAt(targetForm.FenceId, index)
                ? "Move to Recycle Bin â†’"
                : $"Move to {targetForm.FenceName} â†’";
        }

        return "Remove from Fence";
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var onLeft = ShouldSettingsButtonOpenLeft(GetContentSize().Width);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(GetContentSize().Width, onLeft).Contains(ToContent(e.Location)))
                OpenSettingsMenu();
            return;
        }

        if (_newFenceButtonArmed)
        {
            _newFenceButtonArmed = false;
            if (ShowsButtons && GetNewFenceButtonRect(GetContentSize().Width, onLeft).Contains(ToContent(e.Location)))
                _manager.CreateFenceLike(FenceId);
            return;
        }

        if (_deleteButtonArmed)
        {
            _deleteButtonArmed = false;
            if (ShowsButtons && GetDeleteButtonRect(GetContentSize().Width, onLeft).Contains(ToContent(e.Location)))
                ConfirmDelete();
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
        // The trash item being dragged (repositioned, or dropped back near its own cell) is never
        // itself "dropped onto the trash" - only some *other* item landing on the trash cell means
        // delete. Checked once up front rather than at each landing-spot branch below.
        var isSourceTrash = _model.Files[sourceIndex].IsRecycleBin;
        if (new Rectangle(Point.Empty, GetContentSize()).Contains(contentPoint))
        {
            var targetIndex = IndexAtGridPosition(contentPoint) ?? _model.Files.Count;
            if (!isSourceTrash && _manager.IsRecycleBinAt(FenceId, targetIndex))
                _manager.DeleteFencedItem(FenceId, path, Handle);
            else
                _manager.MoveFile(FenceId, path, targetIndex);
        }
        else
        {
            // Not a drop inside this fence's own content - check whether it landed on a *different*
            // fence's window instead of empty desktop, and hand the item over rather than discarding
            // it (the pre-existing behavior for a drop that lands nowhere).
            var screenPoint = PointToScreen(e.Location);
            if (_manager.FindFenceAt(screenPoint, FenceId) is { } targetForm)
            {
                var targetIndex = targetForm.IndexForExternalDrop(screenPoint);
                if (!isSourceTrash && _manager.IsRecycleBinAt(targetForm.FenceId, targetIndex))
                    _manager.DeleteFencedItem(FenceId, path, Handle);
                else
                    _manager.MoveFileToFence(FenceId, targetForm.FenceId, path, targetIndex);
            }
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

    // OnMouseEnter needs no override of its own anymore - LayeredWidgetForm's own already does
    // exactly what this used to (track client-area hover, begin easing opacity). OnMouseLeave still
    // needs one, for the two things below on top of that same base behavior.
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
        if (_visibleButtonTooltip is not null)
        {
            _visibleButtonTooltip = null;
            _toolTip.Hide(this);
        }
    }

    private void SetHoverIndex(int index)
    {
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        RenderAndPresent();
    }

    /// <summary>Everything with no shared home in LayeredWidgetForm: WM_ERASEBKGND/WM_PAINT
    /// (layered-window painting is pushed via UpdateLayeredWindow, not WM_PAINT), WM_COMMAND (only
    /// CmdRenameItem now - title rename no longer routes through a native menu, see Title/
    /// ChromeMenuFieldColor below), and the native owner-draw menu machinery (WM_MEASUREITEM/
    /// WM_DRAWITEM, still needed for the item-rename context menu). A right-click anywhere in the
    /// item grid shows the item context menu via OnClientRightClick instead (LayeredWidgetForm's own
    /// WM_RBUTTONUP handling calls it after activating). Everything else goes through base.WndProc,
    /// which is where move/resize/snap/rename/hover/the Settings dropdown all live now.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_PAINT)
        {
            // Content is pushed via UpdateLayeredWindow (RenderAndPresent), not drawn in
            // response to WM_PAINT - just clear the update region so Windows stops re-posting it.
            NativeMethods.BeginPaint(Handle, out var ps);
            NativeMethods.EndPaint(Handle, ref ps);
            return;
        }

        if (m.Msg == WM_COMMAND)
        {
            HandleCommand(m.WParam.ToInt32() & 0xFFFF);
            return;
        }

        if (m.Msg == NativeMethods.WM_MEASUREITEM)
        {
            var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(m.LParam);
            if (mis.CtlType == NativeMethods.ODT_MENU)
            {
                MeasureMenuItem(ref mis);
                Marshal.StructureToPtr(mis, m.LParam, false);
            }
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == NativeMethods.WM_DRAWITEM)
        {
            var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(m.LParam);
            if (dis.CtlType == NativeMethods.ODT_MENU)
                DrawMenuItem(dis);
            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE || m.Msg == NativeMethods.WM_DPICHANGED)
            Reanchor();
    }

    /// <summary>The fixed anchor LayeredWidgetForm's own WM_MOVING/WM_SIZING measure every tick
    /// against - _model.Bounds itself, same as always.</summary>
    protected override Rectangle GetCurrentBody() => _model.Bounds;

    protected override Guid SnapExcludeId => FenceId;
    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/BeginSnapDrag all use LayeredWidgetForm's own defaults
    // unchanged - this fence's own snapping (against other fences' edges and custom snap lines) is
    // exactly what those defaults already do via SnapExcludeId/SnapMargin/Fences above.

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
            _manager.NotifyBoundsChanged(FenceId, Rectangle.FromLTRB(
                rect.Left + OuterMargin, rect.Top + TopBand, rect.Right - OuterMargin, rect.Bottom - BottomBand));

        // OCD Fence Sizing: snap to the tightest fit right after a manual resize, on top of
        // whatever size was just dragged to - not after a move, see IsResizing. Done before the
        // HWND_BOTTOM restack below (rather than after) so that restack is always the last
        // z-order-relevant call in this handler - FormatDimensions makes its own SetWindowPos call
        // (SWP_NOZORDER, meant to leave z-order untouched), but a resize followed by a move was
        // still landing behind other fences with the restack first, so the z-order push now
        // unconditionally comes last regardless of what ran before it.
        if (IsResizing && _model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);

        // Dragging a fence via its caption goes through the OS's own window-move loop, which
        // activates it like any normal window drag would - left alone, it'd then stay stacked on
        // top of whatever window it was just dragged over, contradicting the whole point of a fence
        // (a desktop-level widget that never covers what you're actually working in). Dropping it
        // to the bottom of the z-order here restores that even though it was just OS-activated;
        // SWP_NOACTIVATE keeps this restack itself from stealing focus back.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        // HWND_BOTTOM above means "underneath literally everything, including every other fence" -
        // left at just that, an actively-dragged (and still visually highlighted, see
        // ShowsButtons/ThemedActiveBorder) fence would disappear behind any other fence it
        // overlaps. Pushing every OTHER fence to the same HWND_BOTTOM afterward settles this one
        // back on top of its siblings without ever elevating any fence above a real window - see
        // RestackOtherFencesBehind's own comment for why order-of-calls is what makes that work.
        _manager.RestackOtherFencesBehind(FenceId);

        // RenderOpacity.BeginIfNeeded() and the Settings-button-corner repaint a pure move otherwise
        // needs (see ShouldSettingsButtonOpenLeft) are both LayeredWidgetForm's own now - it calls
        // both right after this method returns.
    }

    // OnResized needs no override of its own - LayeredWidgetForm's own default (repositioning an
    // already-open Settings dropdown after a resize) already covers the OCD flyout's own resize
    // commands (FormatDimensions), the only thing that used to need this.

    protected override int HitTest(IntPtr lParam)
    {
        var rectPoint = lParam;
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(rectPoint, rect);
        int x = windowPoint.X;
        int y = windowPoint.Y;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // The settings button (and the "+"/"x" buttons beside it) live above the fence, in the taller
        // TopMargin band - check them first so none is shadowed by a resize-band result.
        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (ShowsButtons && (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || GetNewFenceButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || GetDeleteButtonRect(contentWidth, onLeft).Contains(contentPoint)))
            return HTCLIENT;

        if (ShowsButtons)
        {
            // ShowsButtons (IsActive || MenuOpen), not just whether this fence is the active
            // window - opening the settings dropdown steals OS activation from the fence (it's a
            // separate top-level Form), which deactivates it via OnDeactivate even though the
            // button/active border deliberately stay showing (see ShowsButtons's own
            // comment). Gating on plain activation alone let the resize hit-test codes below fire
            // while the dropdown was still open, so dragging an edge resized the fence out from
            // under its own still-open menu.
            //
            // The margin band is a move handle instead of a resize band while active - the same
            // footprint resize used to claim, just reassigned rather than split into two adjacent
            // rings, so the drag margin can hug the fence's actual edge (see PaintContent's own
            // ThemedActiveBorder highlight) without an ambiguous strip where both would apply.
            // Resizing an active fence isn't available until it's deactivated again.
            var band = OuterMargin + ResizeMargin;
            var topZone = TopBand + ResizeMargin;
            var bottomZone = BottomBand + ResizeMargin;
            if (x <= band || x >= width - band || y <= topZone || y >= height - bottomZone)
                return HTCAPTION;
        }
        else if (ResizeHitTest(windowPoint, width, height) is int resizeCode)
        {
            return resizeCode;
        }

        // Empty space within the title bar itself (content-relative, not the margin above), for a
        // fence that still has one - HTBORDER, not HTCAPTION, so a left-button drag from here no
        // longer moves the fence (only the margin does, and only once already active - see the
        // ShowsButtons branch above). Right-click/double-click (rename) and hover still work
        // the same as any other non-client area - see HTBORDER's own comment.
        if (!_model.HideTitle && y - TopBand <= TitleBarHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    /// <summary>
    /// Everything LayeredWidgetForm's own RenderAndPresent doesn't already handle: body, title bar,
    /// Settings/"+"/"x" buttons, and this fence's own items (see PaintItems).
    /// </summary>
    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, GetMaxScroll(contentWidth, contentHeight));
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        // Body/title fill, border, title text, and the Settings button itself are all
        // LayeredWidgetForm's own now - this only draws what's genuinely fence-specific: the "+"/"x"
        // buttons chained off Settings, and the item grid (see PaintItems).
        PaintChrome(g, contentWidth, contentHeight, CornerRadius);

        if (ShowsButtons)
        {
            // Same opaque-backing reasoning as PaintChrome's own Settings button - filled before the
            // copy glyph is stroked on top (see MarginFillColor's own comment).
            var newFenceRect = ToWindow(GetNewFenceButtonRect(contentWidth, onLeft));
            using var newFencePath = RoundedRect(newFenceRect, 6);
            using var newFenceFill = new SolidBrush(ChromeFill);
            g.FillPath(newFenceFill, newFencePath);
            using var newFenceBorderPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
            g.DrawPath(newFenceBorderPen, newFencePath);

            // The classic two-overlapping-squares "duplicate" glyph, hand-drawn like everything
            // else here rather than pulled from an icon font - this app has no icon asset library
            // (see FenceForm's own class comment on hand-painting UI). The front square's corner
            // is punched out of the back square first (filled with the button's own ChromeFill
            // color) so it reads as sitting on top instead of two crossing outlines.
            var cx = newFenceRect.X + newFenceRect.Width / 2f;
            var cy = newFenceRect.Y + newFenceRect.Height / 2f;
            const float iconSize = 9f;
            const float iconOffset = 3f;
            var backRect = new RectangleF(cx - iconSize / 2f + iconOffset / 2f, cy - iconSize / 2f - iconOffset / 2f, iconSize, iconSize);
            var frontRect = new RectangleF(cx - iconSize / 2f - iconOffset / 2f, cy - iconSize / 2f + iconOffset / 2f, iconSize, iconSize);

            using var copyPen = new Pen(Color.WhiteSmoke, 1.3f);
            g.DrawRectangle(copyPen, backRect.X, backRect.Y, backRect.Width, backRect.Height);
            g.FillRectangle(newFenceFill, frontRect);
            g.DrawRectangle(copyPen, frontRect.X, frontRect.Y, frontRect.Width, frontRect.Height);

            // ChromeFill (via the same newFenceFill brush), same as Settings/"+" - matches this
            // fence's own color theme instead of a fixed color, while staying readable against
            // fixed WhiteSmoke; the "x" glyph itself already reads as destructive without needing
            // a separate warning color too.
            var deleteRect = ToWindow(GetDeleteButtonRect(contentWidth, onLeft));
            using var deletePath = RoundedRect(deleteRect, 6);
            g.FillPath(newFenceFill, deletePath);
            using var deleteBorderPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
            g.DrawPath(deleteBorderPen, deletePath);

            using var xPen = new Pen(Color.WhiteSmoke, 1.6f);
            var xCenterX = deleteRect.X + deleteRect.Width / 2f;
            var xCenterY = deleteRect.Y + deleteRect.Height / 2f;
            const float xHalfSize = 4.5f;
            g.DrawLine(xPen, xCenterX - xHalfSize, xCenterY - xHalfSize, xCenterX + xHalfSize, xCenterY + xHalfSize);
            g.DrawLine(xPen, xCenterX - xHalfSize, xCenterY + xHalfSize, xCenterX + xHalfSize, xCenterY - xHalfSize);
        }

        PaintItems(g, contentWidth, contentHeight);
    }

    /// <summary>
    /// Draws this fence's own icon+label for each file it holds, in a simple grid below the title
    /// bar - a real desktop file's own icon is moved into a hidden folder while it's fenced (see
    /// FenceManager.AddFiles), so this is the only place it's actually represented on screen.
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
        // Only successes are cached - a failed extraction isn't necessarily permanent (e.g. the
        // file was mid-move via DesktopIconHider, or briefly locked by another process), and
        // caching null here would otherwise wedge the item icon-less for the rest of this fence's
        // lifetime even once the file becomes readable again. Every repaint just retries instead.
        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        Icon? icon = null;
        try
        {
            // The Recycle Bin's shell-namespace CLSID string (FenceItem.IsRecycleBin) isn't a real
            // path - ExtractLargeIcon's path-based SHGetFileInfo call doesn't resolve it, so this
            // needs the special-folder-PIDL route instead. Icon.ExtractAssociatedIcon would just
            // throw for it too (already caught below), so it's skipped entirely for this path.
            //
            // The shell's large image list gives a genuinely high-resolution icon (crisp at
            // IconSize) rather than the ~32px one Icon.ExtractAssociatedIcon returns, which looks
            // blurry once drawn at a larger size - only fall back to it if the shell lookup fails.
            icon = path == FenceManager.RecycleBinPath
                ? ShellIcons.ExtractRecycleBinIcon()
                : ShellIcons.ExtractLargeIcon(path) ?? Icon.ExtractAssociatedIcon(path);
        }
        catch (IOException)
        {
            // File may have been moved/deleted since it was dropped here.
        }
        catch (System.Security.SecurityException)
        {
        }

        if (icon is not null)
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

    /// <summary>LayeredWidgetForm's own WM_RBUTTONUP handling has already activated the fence by the
    /// time this runs (see its own comment) - only a right-click on an item's label text specifically
    /// (see FileAtLabelPosition) has anything further to show. Not its icon, not empty grid space.
    /// Fence-level actions live elsewhere now: Rename only on the header (see LayeredWidgetForm's own
    /// title-rename) and Delete Fence only as the "x" button next to Settings (see
    /// GetDeleteButtonRect/ConfirmDelete) - a right-click anywhere else has nothing of its own to
    /// offer, so it just activates the fence without popping up a menu. Open and Remove From Fence
    /// used to live here too; both stayed reachable another way (double-click, drag off the fence) so
    /// removing them from this menu didn't remove the functionality, just this shortcut to it.</summary>
    protected override void OnClientRightClick(Point contentPoint)
    {
        _contextItem = FileAtLabelPosition(contentPoint);
        if (_contextItem is null)
            return;
        ShowContextMenu(contentPoint);
    }

    private void ShowContextMenu(Point contentPoint)
    {
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

    // Title/TitleRowHeight below are the only rename-related hooks left with a fence-specific
    // answer - TitleVisible (derived from HideTitle), ChromeMenuFieldColor/HoverColor, and
    // EditBoxTextColor/BackgroundColor are all LayeredWidgetForm's own defaults now (ChromeFill/
    // ThemedMenuSelected/ThemedBody, exactly what this used to override them to).

    protected override string Title
    {
        get => _model.Name;
        set => _manager.NotifyRenamed(FenceId, value);
    }

    protected override int TitleRowHeight => TitleBarHeight;

    /// <summary>Per-fence settings, opened via LayeredWidgetForm's own OpenSettingsMenu once this
    /// fence is active (see OnDeactivate and the settings-button hit-test carve-out). "Fence Color"
    /// is inlined directly as a flat group (its own header already says what it is, without needing
    /// an outer flyout-anchor row to name it too), while "Fence Dimensions" is nested behind an
    /// "OCD" flyout (see DropdownMenu.Row.Submenu) instead. "Delete Fence" isn't a row here - it's
    /// the "x" button next to Settings (see GetDeleteButtonRect/ConfirmDelete), same as "Rename"
    /// lives in the header's own context menu rather than here.</summary>
    protected override List<DropdownMenu.Row> BuildSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(CmdToggleHideLabels, "Hide Shortcut Names", HasCheckbox: true, IsChecked: () => _model.HideLabels),
            new(CmdToggleHideTitle, "Hide Title", HasCheckbox: true, IsChecked: () => _model.HideTitle),
            new(CmdToggleOcdSizing, "OCD Fence Sizing", HasCheckbox: true, IsChecked: () => _model.OcdFenceSizing,
                Tooltip: GetMenuTooltipText(CmdToggleOcdSizing)),
            new(CmdToggleFullOpacityOnHover, "Full Opacity When Active", HasCheckbox: true,
                IsChecked: () => _model.FullOpacityOnHover,
                Tooltip: "Full opacity while hovered, dragged/resized, or this menu is open"),
            new(0, string.Empty, IsSeparator: true),
        };
        // The color grid (Default + presets + Custom... + Eyedropper) - shared with
        // LayoutLauncherWidget's own options menu now (see StyleMenuRows.BuildColorGrid's own
        // comment for why this fence's remaining rows below - sliders/margin/OCD sizing - still
        // build separately instead of also going through Build's fixed shape). Only the header
        // wording ("Fence Color" instead of "Color") is fence-specific.
        rows.AddRange(StyleMenuRows.BuildColorGrid(_model, DefaultBodyColor, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase, "Fence Color"));
        // No separator before this header - it's still part of the "Fence Color" category (how the
        // fence's own colors look), just its own labeled control rather than lumped under that header.
        rows.Add(new DropdownMenu.Row(TagHeaderDarknessHeader, "Header Darkness", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => _model.HeaderDarkness / 100.0,
            OnSliderChange: value => SetHeaderDarkness((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(TagFenceOpacityHeader, "Fence Opacity", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => _model.Opacity / 100.0,
            OnSliderChange: value => SetOpacity((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(TagTintStrengthHeader, "Tint Strength", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSlider: true,
            SliderValue: () => _model.TintStrength / 100.0,
            OnSliderChange: value => SetTintStrength((int)Math.Round(value * 100))));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        // How far this fence wants to sit from another fence's edge when it snaps against one (see
        // FenceManager.GetOtherFenceEdges) - this fence's own value, not the other one's, the same
        // way OcdFenceSizing/HeaderDarkness/etc. above are all per-fence rather than app-wide. A
        // typed number with +/- steppers rather than a slider like the others above - a pixel count
        // is exact-value-driven (you want e.g. "10", not "whatever a slider drag landed near").
        rows.Add(new DropdownMenu.Row(TagMarginHeader, "Fence Margin", IsHeader: true));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsStepper: true,
            StepperValue: () => _model.Margin, OnStepperChange: SetMargin,
            StepperMin: 0, StepperMax: 100, StepperStep: 5, StepperSuffix: "px"));
        rows.Add(new DropdownMenu.Row(0, string.Empty, IsSeparator: true));
        // A flyout instead of an inline "Fence Dimensions" header/group (see DropdownMenu.Row.Submenu)
        // - one fewer always-visible row, and "OCD" doubles as a nod to "OCD Fence Sizing" above. The
        // header now lives inside the flyout itself instead, same as "Fence Color" above.
        rows.Add(new DropdownMenu.Row(0, "OCD", Submenu: new List<DropdownMenu.Row>
        {
            new(TagFenceDimensionsHeader, "Fence Dimensions", IsHeader: true),
            new(0, string.Empty, IsSeparator: true),
            new(CmdResizeBoth, "Both"),
            new(CmdResizeLeftRight, "Left/Right"),
            new(CmdResizeTopDown, "Top/Down"),
        }));
        return rows;
    }

    // SettingsMenuFieldColor/HoverColor/AccentColor/BorderColor/TooltipColor and the dropdown's own
    // reposition-on-resize are all LayeredWidgetForm's own defaults now (ChromeFill/ThemedMenuSelected/
    // Accent/ThemedCheckboxBorder, and OnResized - exactly what this used to override them to).

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
            hbrBack = GetThemeBrush(ThemedBody),
        };
        NativeMethods.SetMenuInfo(hMenu, ref menuInfo);
    }

    private static void AppendItem(IntPtr hMenu, int commandId, bool isChecked)
    {
        var flags = NativeMethods.MF_OWNERDRAW | (isChecked ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED);
        NativeMethods.AppendMenu(hMenu, flags, (IntPtr)commandId, (IntPtr)commandId);
    }

    private readonly record struct MenuRowStyle(string Text, bool HasCheckbox, bool IsHeader, Color? Swatch = null);

    /// <summary>Every owner-draw row's label, keyed by the item id carried in its itemData (see
    /// AppendItem) - only "Rename" for the one single-item native menu this still serves
    /// (ShowContextMenu's item-rename) now that the Settings dropdown draws itself directly from its
    /// Row list, and title-rename uses a plain ContextMenuStrip instead of a native menu.</summary>
    private static MenuRowStyle GetMenuRowStyle(int tag) => tag switch
    {
        CmdRenameItem => new MenuRowStyle("Rename", false, false),
        _ => new MenuRowStyle(string.Empty, false, false),
    };

    // ColorRef/Tint/DarkenTowardBlack/SafeChromeBlend are all LayeredWidgetForm's own now.

    /// <summary>Only rows worth explaining get one - most menu items are self-explanatory from
    /// their label alone.</summary>
    private static string? GetMenuTooltipText(int commandId) => commandId switch
    {
        CmdToggleOcdSizing =>
            "After you resize this fence by hand, automatically snap it to the tightest size that fits its icons (same as OCD Formatting > Both).",
        _ => null,
    };

    /// <summary>WM_MEASUREITEM handler for the one remaining native single-item menu (item-rename,
    /// see ShowContextMenu) - the Settings dropdown measures its own rows directly (see
    /// DropdownMenu.MeasureLayout) instead of going through this.</summary>
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

    /// <summary>WM_DRAWITEM handler for the one remaining native single-item menu (see
    /// MeasureMenuItem's own comment) - paints a row to match the fence's own dark theme instead of
    /// the native Windows menu look.</summary>
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

    /// <summary>WM_COMMAND dispatch for the one remaining native single-item menu (item-rename, see
    /// ShowContextMenu) - everything else (the Settings dropdown's rows, title-rename) is handled
    /// directly by LayeredWidgetForm now (see HandleSettingsCommand for the dropdown's own commands),
    /// never routed through WM_COMMAND at all.</summary>
    private void HandleCommand(int id)
    {
        if (id == CmdRenameItem)
            BeginRenameItem(_contextItem);
    }

    /// <summary>Dispatches a clicked Settings-dropdown row id - see LayeredWidgetForm.OpenSettingsMenu,
    /// which calls this directly (not via WM_COMMAND).</summary>
    protected override void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdToggleHideTitle: HideTitle = !HideTitle; break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
            case CmdToggleOcdSizing: ToggleOcdFenceSizing(); break;
            case CmdToggleFullOpacityOnHover:
                SetFullOpacityOnHover(!_model.FullOpacityOnHover);
                RenderOpacity.SnapToTarget();
                RenderAndPresent();
                break;
            case CmdColorDefault:
            case CmdColorCustom:
            case CmdColorEyedrop:
            case >= CmdColorPresetBase and < CmdColorPresetBase + 100:
                // Default/preset/Custom.../Eyedropper - same shared handling
                // LayoutLauncherWidget's own color rows use too (see StyleMenuRows' own doc
                // comment). The Eyedropper pick also resets Opacity/Tint Strength so it starts out
                // pixel-exact, same as this used to do inline via PickEyedropperColor.
                StyleMenuRows.TryHandleColorCommand(id, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
                    DefaultBodyColor, this, CurrentTint, color => SetTintColor(color, exact: false), color =>
                    {
                        SetTintColor(color, exact: true);
                        SetOpacity(100);
                        SetTintStrength(0);
                    });
                break;
        }
    }

    /// <summary>exact is only ever true from PickEyedropperColor - see FenceModel.TintIsExact. A
    /// non-exact pick also resets Opacity back to its default as a side effect (see
    /// FenceManager.SetTintColor) - RenderOpacity needs to snap to match immediately, the same
    /// reasoning as SetOpacity's own snap, or the fence would keep rendering at whatever opacity it
    /// was at right before this pick until something else (hover, the dropdown closing) happened to
    /// notice the mismatch.</summary>
    protected override void SetTintColor(Color? color, bool exact)
    {
        _manager.SetTintColor(FenceId, color, exact);
        RenderOpacity.SnapToTarget();
        RenderAndPresent();
    }

    /// <summary>"Header Darkness" slider - called directly from DropdownMenu.Row.OnSliderChange
    /// (not routed through HandleSettingsCommand/ItemClicked the way every other row is, since a
    /// slider needs a live value rather than a single command id) on mouse-down and on every
    /// subsequent mouse-move while dragging, so the header repaints continuously as it's dragged
    /// rather than only once on release.</summary>
    protected override void SetHeaderDarkness(int darkness)
    {
        _manager.SetHeaderDarkness(FenceId, darkness);
        RenderAndPresent();
    }

    /// <summary>"Fence Opacity" slider - same live-drag pattern as SetHeaderDarkness above.
    /// FenceManager.SetOpacity enforces a safe minimum, so a value dragged below it snaps back on the
    /// next repaint rather than the fence actually going invisible. Snaps RenderOpacity straight to
    /// the new TargetOpacity instead of animating - a slider drag needs to track the cursor
    /// immediately, an animated lag here would feel unresponsive.</summary>
    protected override void SetOpacity(int opacity)
    {
        _manager.SetOpacity(FenceId, opacity);
        RenderOpacity.SnapToTarget();
        RenderAndPresent();
    }

    /// <summary>"Tint Strength" slider - same live-drag pattern as SetHeaderDarkness/SetOpacity above.
    /// Affects both a preset/Custom... pick (TintAmount) and an Eyedropper's exact pick
    /// (DilutedExactTint), just in opposite directions - see either one's own doc comment.</summary>
    protected override void SetTintStrength(int strength)
    {
        _manager.SetTintStrength(FenceId, strength);
        RenderAndPresent();
    }

    /// <summary>"Fence Margin" numeric input. Doesn't need a repaint of its own (unlike the sliders
    /// above, nothing this fence draws depends on its own Margin value - it only affects candidates
    /// offered to OTHER fences' drags via FenceManager.GetOtherFenceEdges) but RenderAndPresent
    /// stays for consistency and to keep anything else the dropdown reflects in sync.</summary>
    protected override void SetMargin(int margin)
    {
        _manager.SetMargin(FenceId, margin);
        RenderAndPresent();
    }

    /// <summary>LayeredWidgetForm's own required mutator hook - plumbed straight through to
    /// FenceManager, same as the sliders above; the Render/opacity side effects live at each call
    /// site instead (see HandleSettingsCommand's own CmdToggleFullOpacityOnHover case) since this is
    /// also reused, unmodified, by nothing else.</summary>
    protected override void SetFullOpacityOnHover(bool enabled) => _manager.SetFullOpacityOnHover(FenceId, enabled);

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
        // Changes EffectiveCellHeight (see its own comment), which OCD Fence Sizing's fit is based
        // on - only height can possibly need to change here, never the columns/width.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: false, adjustHeight: true);
        RenderAndPresent();
    }

    private void ToggleOcdFenceSizing()
    {
        _manager.SetOcdFenceSizing(FenceId, !_model.OcdFenceSizing);
        // Otherwise this only ever takes effect after the next manual resize (see OnDragEnd) -
        // turning it on should tidy up the fence right away instead of waiting for that.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);
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
        // NotifyBoundsChanged just needs to persist it, the same way OnDragEnd does after an
        // interactive drag-resize.
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0,
            newBounds.Width + OuterMargin * 2, newBounds.Height + TopBand + BottomBand,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _manager.NotifyBoundsChanged(FenceId, newBounds);
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

    // Thin wrapper kept under this file's own name/call sites rather than switching every one of
    // them to RoundedRectPath.Full directly - same behavior, smaller diff.
    private static GraphicsPath RoundedRect(Rectangle bounds, int radius) => RoundedRectPath.Full(bounds, radius);
}
