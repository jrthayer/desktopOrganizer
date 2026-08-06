using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Layout Launcher" widget - a persistent on-screen panel listing every saved layout, styled and
/// behaving like a Fence: tint color, header darkness, opacity, tint strength, "full opacity when
/// active", a hideable title, and the same drag-to-snap-against-other-fences behavior. This is the
/// only entry point into the Layouts feature now - quick-run (click a row), Save Current Layout, and
/// Manage Layouts... all live here rather than a separate tray submenu. Separate from
/// LayoutEditorForm (which edits a layout's programs/placements in detail) - this is the one that
/// stays parked on the desktop.
///
/// A layered, entirely hand-painted window (WS_POPUP + WS_EX_LAYERED, no WinForms child controls),
/// following FenceForm's own architecture instead of hosting real Controls the way this class used
/// to - see RenderAndPresent, WndProc's WM_NCHITTEST/WM_NCLBUTTONDOWN/WM_NCRBUTTONDOWN handling, and
/// EditBox (the shared rename-box popup FenceForm's own BeginRename already used). Genuinely shared
/// with FenceForm rather than a parallel copy: EditBox, LayeredWindowPresenter, RoundedRectPath,
/// OpacityAnimator, WidgetActivation, DropdownMenu, StyleTint/StyleMenuRows, TrayMenuRenderer. What's
/// NOT shared is each class's own geometry/hit-testing (a grid of icons vs. a list of rows) and its
/// WM_MOVING snap-drag math (tied to each one's own bounds-tracking fields) - those follow the same
/// pattern without being literally the same code.
///
/// Unlike a fence this widget is TopMost (WS_EX_TOPMOST baked into CreateParams) rather than
/// desktop-anchored - it's an ordinary always-on-top utility window, not something that needs to sit
/// among desktop icons, so there's no IDesktopAnchorStrategy involved. There's also no resize support
/// (height is always derived from the current layout count - see GetContentHeight - and width never
/// changes at runtime), so there's no left/right resize-grab margin the way FenceForm has - only a
/// ButtonBandHeight reserved for the Settings/close buttons, on whichever of the top/bottom edges
/// currently has room for it (see _buttonRowAtBottom), mirroring FenceForm's own TopBand/BottomBand
/// split between window-space and content-space (see ToWindow/ToContent) just on that one axis
/// instead of all four edges.
///
/// Persistent in the sense that mirrors a Fence: created once at startup (TrayApplicationContext),
/// remembers its position/title/styling/visibility across restarts (LayoutLauncherModel via
/// LayoutLauncherStore), and the "x" button/tray toggle only hide it rather than destroying it - the
/// same instance keeps living for the rest of the process, exactly like a Fence isn't recreated every
/// time "Show/Hide All" brings it back.
/// </summary>
internal sealed class LayoutLauncherWidget : Form
{
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_SIZE = 0x0005;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const int HeaderHeight = 28;
    private const int RowHeight = 24;
    private const int MaxVisibleRows = 8;
    private const int EmptyStateHeight = 100;
    private const int ListInset = 12;
    private const int ButtonHeight = 28;
    private const int ButtonAreaGap = 6;
    private const int ScrollbarWidth = 6;
    private const int ScrollbarMargin = 3;
    private const int CornerRadius = 10;
    private const float ActiveBorderWidth = 3f;

    // A near-invisible band around the visible body's left/right edges (and whichever of top/bottom
    // isn't currently the button band - see TopBand/BottomBand) that, once engaged, doubles as a
    // drag handle the same way FenceForm's own margin does for an active fence - see HitTest's own
    // margin check. FenceForm's own active drag band is actually OuterMargin(13) + ResizeMargin(12)
    // combined - the same footprint that would otherwise be a resize handle while inactive gets
    // reused as extra move-handle width once active, rather than existing as a separately-sized
    // OuterMargin on its own. This widget has no resize to justify that split at all, so it's just
    // one band, but sized to that same 25px combined total - matching only OuterMargin's 13 alone
    // left the drag margin here noticeably thinner than a fence's actual grab area.
    private const int OuterMargin = 25;

    // The settings/close buttons live in their own reserved band outside the header - same
    // "outside the fence body entirely" placement as FenceForm's own Settings button (see
    // GetSettingsButtonRect's own comment), rather than crowded inline with the title the way this
    // widget used to draw them. SettingsButtonWidth/Height match FenceForm's own exactly ("Settings"
    // is the same label at the same size); ButtonBandHeight reserves SettingsButtonGap above AND
    // below the button row so it isn't flush against either the band's own outer edge or the header.
    private const int SettingsButtonWidth = 64;
    private const int SettingsButtonHeight = 22;
    private const int SettingsButtonGap = 6;
    private const int CloseButtonSize = 22;
    private const int ButtonSpacing = 6;
    private const int ButtonBandHeight = SettingsButtonHeight + SettingsButtonGap * 2;

    // Deliberately never tinted, unlike every other Effective*/Themed* color - this fill exists
    // purely so Windows doesn't treat the button band as click-through (see RenderAndPresent), not
    // to be seen. Alpha 1 is the practical minimum that still counts as "not fully transparent" to
    // Windows. Same trick FenceForm's own OuterMargin/TopBand fill uses.
    private static readonly Color MarginFillColor = Color.FromArgb(1, 0, 0, 0);

    private const string EmptyStateText =
        "No layouts saved yet.\nUse \"Save Current Layout\"\nor \"Manage Layouts...\" below\nto create one.";

    private const int CmdToggleFullOpacityOnHover = 1;
    private const int CmdColorDefault = 2;
    private const int CmdColorCustom = 3;
    private const int CmdToggleHideTitle = 4;
    private const int CmdColorEyedrop = 5;
    private const int CmdColorPresetBase = 10;

    private enum RowGlyph { None, Copy, Delete }
    private enum HoverTarget { None, Settings, Close, Save, Manage }

    private readonly LayoutManager _manager;
    private readonly FenceManager _fenceManager;
    private readonly LayoutLauncherModel _model;
    private readonly LayoutLauncherStore _store;
    private readonly ContextMenuStrip _headerContextMenu;
    private readonly OpacityAnimator _opacity;

    // Same shared state machine FenceForm's own settings/new/delete buttons use (see
    // WidgetActivation's own doc comment) - Changed is wired in the constructor to repaint and
    // re-evaluate opacity together.
    private readonly WidgetActivation _activation = new();

    private EditBox? _renameBox;
    private DropdownMenu? _dropdown;

    // Backs the rename EditBox's own WM_CTLCOLOREDIT background (see WndProc) - one shared themed
    // fill, matching EffectiveHeader, for the native Edit control that would otherwise default to a
    // white background. Recreated on demand (see GetThemeBrush) rather than fixed for the widget's
    // whole lifetime, since EffectiveHeader depends on this widget's own tint and can change at
    // runtime.
    private IntPtr _themeBrush = IntPtr.Zero;
    private Color _themeBrushColor;

    private bool _disposing;
    private bool _allowClose;
    private bool _isMoving;
    private bool _isClientHovered;
    private bool _isNonClientHovered;
    private bool IsHovered => _isClientHovered || _isNonClientHovered;

    // True when placing the button band above the widget's current on-screen position would extend
    // above its monitor's own working area - in that case the band (and the Settings/close buttons
    // in it) moves below the widget instead, so it can still sit flush with the very top of the
    // screen without its own buttons going unreachably off-screen. Kept in sync wherever the
    // widget's position is computed/changed (CreateParams at handle-creation time, and WM_MOVING on
    // every tick of a live drag) rather than read fresh on every use - exactly mirrors FenceForm's
    // own _buttonRowAtBottom/TopBand/BottomBand/ComputeButtonRowAtBottom.
    private bool _buttonRowAtBottom;

    // The margin band on whichever side currently holds the button row - ButtonBandHeight-sized
    // there, same as always; a plain OuterMargin (like the left/right edges always are) on the
    // other side instead, same relationship as FenceForm's own TopBand/BottomBand.
    private int TopBand => _buttonRowAtBottom ? OuterMargin : ButtonBandHeight;
    private int BottomBand => _buttonRowAtBottom ? ButtonBandHeight : OuterMargin;

    private static bool ComputeButtonRowAtBottom(Point bodyScreenLocation) =>
        bodyScreenLocation.Y - ButtonBandHeight < Screen.FromPoint(bodyScreenLocation).WorkingArea.Top;

    private int _scrollOffset;
    private bool _scrollbarDragging;
    private int _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;

    private bool _settingsButtonArmed;
    private bool _closeButtonArmed;
    private bool _saveButtonArmed;
    private bool _manageButtonArmed;
    private HoverTarget _hoverTarget = HoverTarget.None;
    private int _hoverRowIndex = -1;

    // Fixed anchor a drag measures against every WM_MOVING tick, instead of trusting the OS's own
    // incrementally-proposed rect - see FenceForm.WndProc's WM_MOVING case for why (drift/stickiness
    // otherwise).
    private Point _leftDragStartScreenPoint;
    private Rectangle _dragStartBounds;

    // Guid? carries the freshly-captured profile's Id up from OnSaveCurrentLayout (null from
    // "Manage Layouts...", which just wants whichever profile was already selected/none) so
    // TrayApplicationContext.OpenLayoutEditor can land straight on it, same as the old tray
    // "Save Current Layout" command used to.
    public event EventHandler<Guid?>? ManageLayoutsRequested;

    public LayoutLauncherWidget(LayoutManager manager, FenceManager fenceManager, LayoutLauncherModel model, LayoutLauncherStore store)
    {
        _manager = manager;
        _fenceManager = fenceManager;
        _model = model;
        _store = store;

        _opacity = new OpacityAnimator(_model.Opacity / 100f, () => TargetOpacity, RenderAndPresent);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = AppTheme.Font;

        // Live Func<Color> getters, not a one-time snapshot - same reason OpenSettingsMenu passes
        // its own DropdownMenu instances the same way, so the "Rename" right-click menu stays in
        // sync with whatever tint this widget's own header is currently showing.
        _headerContextMenu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(() => EffectiveField, () => EffectiveHover, () => AppTheme.Text),
            Font = AppTheme.Font,
        };
        _headerContextMenu.Items.Add("Rename", null, (_, _) => BeginRename());

        _activation.Changed += RenderAndPresent;
        _activation.Changed += () => _opacity.BeginIfNeeded();

        Activated += (_, _) => RefreshContent(); // profiles may have changed via the editor while this was in the background

        // Forces handle creation now that every field CreateParams needs is set - see CreateParams'
        // own null-guard comment for why this can't happen any earlier.
        RenderAndPresent();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Control's base constructor probes CreateParams before our own constructor body has
            // run (so _model is still null at that point) - the real, model-driven CreateParams
            // request comes later, when the constructor body first touches Handle (see its own
            // trailing RenderAndPresent call).
            if (_model is null)
                return cp;

            // _model.X/Y/Width is the visible body's own top-left/width (matching FenceForm's own
            // _model.Bounds convention) - stable regardless of which side the button band is
            // currently on, rather than the window's own top-left, which shifts as TopBand/
            // BottomBand swap sides (see ComputeButtonRowAtBottom and OnLocationChanged, the
            // only other place that converts between the two). The window itself is OuterMargin
            // wider/taller on every edge than the body, same margin band FenceForm's own
            // OuterMargin reserves (see HitTest's own drag-margin check).
            var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
            var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - GetContentHeight()) / 2;
            _buttonRowAtBottom = ComputeButtonRowAtBottom(new Point(bodyX, bodyY));

            cp.Width = _model.Width + OuterMargin * 2;
            cp.Height = GetContentHeight() + TopBand + BottomBand;
            // No WS_VISIBLE here, unlike FenceForm - this widget's startup visibility depends on
            // LayoutLauncherModel.Visible (whether it was left open last session), decided by
            // TrayApplicationContext calling the ordinary WinForms Show()/Hide() afterward, not
            // baked into window creation the way a fence's is.
            cp.Style = NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN;
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
            cp.X = bodyX - OuterMargin;
            cp.Y = bodyY - TopBand;
            return cp;
        }
    }

    /// <summary>Shows (persisting Visible) if currently hidden, hides (persisting Visible) if
    /// currently shown - what the tray's "Layout Launcher" checkbox toggles.</summary>
    public void ToggleVisible()
    {
        if (Visible)
            HideAndPersist();
        else
            ShowAndPersist();
    }

    private void ShowAndPersist()
    {
        Show();
        Activate();
        _model.Visible = true;
        Persist();
    }

    private void HideAndPersist()
    {
        Hide();
        _model.Visible = false;
        Persist();
    }

    /// <summary>Real disposal, for actual app shutdown (TrayApplicationContext.OnExit) - the only
    /// caller allowed to bypass OnFormClosing's cancel-and-hide below.</summary>
    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Covers both the "x" button (which calls HideAndPersist directly and never reaches
    /// here) and anything else that might ask this window to close - Alt+F4 while it has focus,
    /// chiefly - so the widget survives everything except an explicit Shutdown() the same way a
    /// Fence survives everything except "Delete Fence". Windows logging off/shutting down (or Task
    /// Manager ending the process) is the one other case this must NOT cancel - e.Cancel = true
    /// there answers the OS's WM_QUERYENDSESSION with "this app refuses to close", which is exactly
    /// what left this window blocking shutdown until it got force-killed, corrupting whatever store
    /// write was in flight at the time instead of exiting (and saving) cleanly.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall or CloseReason.FormOwnerClosing))
        {
            e.Cancel = true;
            HideAndPersist();
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>Same "losing focus always deactivates" rule as FenceForm.OnDeactivate - fires even
    /// though opening the settings dropdown or starting a rename (both separate top-level windows)
    /// also trigger this; see WidgetActivation's own doc comment for why MenuOpen has to stay
    /// tracked separately from IsActive for the dropdown case. Renaming needs no such carve-out - a
    /// rename box has nothing worth keeping the gear/close buttons visible for, so it's fine (in
    /// fact wanted - see EditBox's own doc comment) for this to just deactivate for real.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _activation.Deactivate();
    }

    /// <summary>Location is the window's own top-left (shifts as TopBand/BottomBand swap sides -
    /// see _buttonRowAtBottom), but _model.X/Y persists the visible body's own top-left instead (see
    /// CreateParams' own comment on why), so this converts back the same way CreateParams converts
    /// forward.</summary>
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!IsHandleCreated)
            return;
        _model.X = Location.X + OuterMargin;
        _model.Y = Location.Y + TopBand;
        Persist();
    }

    private void Persist() => _store.Save(_model);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Set before anything below runs - see FenceForm.Dispose's own comment on why
            // (destroying the handle synchronously delivers WM_ACTIVATE, reaching OnDeactivate ->
            // RenderAndPresent while teardown is still in progress).
            _disposing = true;
            _renameBox?.Dispose();
            _dropdown?.Dispose();
            _opacity.Dispose();
            _headerContextMenu.Dispose();
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
        }
        base.Dispose(disposing);
    }

    /// <summary>Lazily (re)creates the shared theme brush only when EffectiveHeader has actually
    /// changed since the last call - WM_CTLCOLOREDIT can fire often enough (every rename-box redraw)
    /// that recreating a native GDI brush on every single call would be wasteful.</summary>
    private IntPtr GetThemeBrush()
    {
        var color = EffectiveHeader;
        if (_themeBrush == IntPtr.Zero || _themeBrushColor != color)
        {
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
            _themeBrush = NativeMethods.CreateSolidBrush(ColorRef(color));
            _themeBrushColor = color;
        }
        return _themeBrush;
    }

    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    /// <summary>Intercepts the OS's own interactive-move loop (already running by the time this
    /// arrives - a real WM_NCLBUTTONDOWN on the HTCAPTION band HitTest reports, same as a real
    /// caption would generate) to snap against other fences' edges and the app's custom snap lines,
    /// following FenceForm's own WM_MOVING handling (not literally shared code - each tracks its own
    /// bounds fields - but the same fixed-drag-start-anchor technique, see that class's own comment
    /// on why the proposed rect is recomputed fresh from a fixed anchor every tick instead of
    /// trusting the RECT the OS proposes).</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HitTest(m.LParam);
            return;
        }

        if (m.Msg == NativeMethods.WM_MOVING)
        {
            var currentScreenPoint = Cursor.Position;
            var body = new Rectangle(
                _dragStartBounds.X + (currentScreenPoint.X - _leftDragStartScreenPoint.X),
                _dragStartBounds.Y + (currentScreenPoint.Y - _leftDragStartScreenPoint.Y),
                _dragStartBounds.Width, _dragStartBounds.Height);

            IReadOnlyList<int> vCandidates = Array.Empty<int>();
            IReadOnlyList<int> hCandidates = Array.Empty<int>();
            if ((MouseButtons & MouseButtons.Right) == 0)
                (vCandidates, hCandidates) = _fenceManager.GetOtherFenceEdges(Guid.Empty, _model.Margin);
            var result = _fenceManager.SnapLines.SnapMove(body, vCandidates, hCandidates, _model.Margin);
            // Re-decided against the proposed rect's own new position - a drag that crosses the
            // "would go off the top of the screen" threshold mid-tick flips right here, so
            // WriteBackWindowRect (next) already inflates using whichever side the button band
            // belongs on now, not wherever it was a moment ago. RenderAndPresent since a flip moves
            // the buttons to the opposite edge, which needs a repaint - a pure position change
            // doesn't (the OS moves the already-rendered bitmap for free), so this only actually
            // costs anything on the rare tick where the flip itself happens... except there's no
            // cheap way to know that without computing it, so it's called unconditionally instead.
            _buttonRowAtBottom = ComputeButtonRowAtBottom(result.Rect.Location);
            WriteBackWindowRect(m.LParam, result.Rect);
            RenderAndPresent();
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_NCLBUTTONDBLCLK)
        {
            // HitTest reports HTCAPTION for the whole top band + header row now, not just the
            // rendered title text - but renaming (like FenceForm's own BeginRename) should only
            // trigger over the header row specifically, not the top band above it (just the
            // Settings/close buttons up there, no title to rename) or empty header space either
            // side of the text. Anywhere else in this non-client area, do nothing rather than
            // letting the default proc maximize the window (its usual caption double-click behavior).
            _activation.Activate();
            if (IsOverHeaderRow(m.LParam))
                BeginRename();
            return;
        }

        if (m.Msg == NativeMethods.WM_NCRBUTTONDOWN)
        {
            // A real caption's right-click would show the system menu (Restore/Move/Close etc.) via
            // the default proc - there's no such menu for this custom-drawn header, so this always
            // swallows the message itself rather than falling through to base.WndProc/DefWindowProc.
            _activation.Activate();
            if (IsOverHeaderRow(m.LParam))
                ShowHeaderRenameMenu(m.LParam);
            return;
        }

        if (m.Msg == NativeMethods.WM_CTLCOLOREDIT)
        {
            // Sent by the rename EditBox to its owner (GetParent resolves to us even though it's a
            // top-level WS_POPUP, not a true child - see EditBox's own class comment) each time it
            // needs to know what to paint itself with. Recoloring here, rather than in EditBox
            // itself, is the standard way to restyle a plain native Edit control - it has no
            // owner-draw hook of its own the way buttons/menus do, so left alone it paints the
            // stock white-background/black-text look. Matches the header's own color, same as the
            // old child-control TextBox version did (it sat in that exact strip, not the plain
            // body).
            NativeMethods.SetTextColor(m.WParam, ColorRef(AppTheme.Text));
            NativeMethods.SetBkColor(m.WParam, ColorRef(EffectiveHeader));
            m.Result = GetThemeBrush();
            return;
        }

        // A left click on the header activates the widget - not returning early: the default proc
        // still needs this message to actually start the drag.
        if (m.Msg == NativeMethods.WM_NCLBUTTONDOWN && (int)m.WParam.ToInt64() == HTCAPTION)
            _activation.Activate();

        // WinForms' own client-area hover tracking (OnMouseEnter/OnMouseLeave) doesn't cover the
        // header band - it reports HTCAPTION (see HitTest), so the OS treats it as non-client and
        // never raises the client mouse events those hook. TrackMouseEvent needs re-arming on every
        // WM_NCMOUSEMOVE (Windows disarms it after firing once), not just the first - but only
        // bother once per hover session since _isNonClientHovered already being true means it's
        // still armed from last time.
        if (m.Msg == NativeMethods.WM_NCMOUSEMOVE)
        {
            if (!_isNonClientHovered)
            {
                _isNonClientHovered = true;
                _opacity.BeginIfNeeded();
            }
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TME_LEAVE | NativeMethods.TME_NONCLIENT,
                hwndTrack = Handle,
            };
            NativeMethods.TrackMouseEvent(ref tme);
        }
        else if (m.Msg == NativeMethods.WM_NCMOUSELEAVE)
        {
            _isNonClientHovered = false;
            _opacity.BeginIfNeeded();
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_SIZE:
                RenderAndPresent();
                break;

            case WM_ENTERSIZEMOVE:
                _isMoving = true;
                // Body-relative (matching FenceForm's own _model.Bounds convention - see
                // WriteBackWindowRect's own comment), not Bounds directly - snapping needs to
                // measure against the widget's actual VISIBLE edges, not the OuterMargin/button
                // band padding every side of the real window rect.
                _dragStartBounds = new Rectangle(Bounds.X + OuterMargin, Bounds.Y + TopBand, _model.Width, GetContentHeight());
                _leftDragStartScreenPoint = Cursor.Position;
                // Same "right already held at the very first frame" check WM_MOVING itself does on
                // every later tick - without this the drag-start guide overlay would show fence
                // edges for one frame even when right was already down before the drag began.
                if ((MouseButtons & MouseButtons.Right) == 0)
                {
                    var (vGuides, hGuides) = _fenceManager.GetOtherFenceEdges(Guid.Empty, _model.Margin);
                    var monitor = Screen.FromRectangle(Bounds).Bounds;
                    _fenceManager.SnapLines.BeginDrag(includeCustomLines: true, vGuides, hGuides, monitor);
                }
                else
                {
                    _fenceManager.SnapLines.BeginDrag();
                }
                _opacity.BeginIfNeeded();
                break;

            case WM_EXITSIZEMOVE:
                _fenceManager.SnapLines.EndDrag();
                _isMoving = false;
                _opacity.BeginIfNeeded();
                RenderAndPresent();
                break;
        }
    }

    /// <summary>Content-relative (0,0) is the visible body's own top-left, same reference point
    /// FenceForm.ToContent/ToWindow use - OuterMargin/TopBand (window-space; TopBand shrinks to a
    /// plain OuterMargin once the button band flips to the bottom - see _buttonRowAtBottom) is
    /// content-space X/Y &lt; 0, so the Settings/close button rects are defined with negative Y the
    /// same way FenceForm's own GetSettingsButtonRect is.</summary>
    private Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - TopBand);

    private Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + TopBand, contentRect.Width, contentRect.Height);

    /// <summary>body here is already a content-relative (visible-body) rect - WM_MOVING's own local
    /// "body" variable is built from _dragStartBounds, which WM_ENTERSIZEMOVE deliberately sets to
    /// the body's own rect rather than Bounds directly, so snapping measures against the widget's
    /// actual visible edges instead of the OuterMargin/button band padding every side of the real
    /// window rect. So, same as FenceForm.WriteBackWindowRect, this pads OuterMargin/TopBand/
    /// BottomBand back on to recover the real window rect the OS is expecting at lParam.</summary>
    private void WriteBackWindowRect(IntPtr lParam, Rectangle body)
    {
        var rect = new RECT
        {
            Left = body.Left - OuterMargin,
            Top = body.Top - TopBand,
            Right = body.Right + OuterMargin,
            Bottom = body.Bottom + BottomBand,
        };
        Marshal.StructureToPtr(rect, lParam, false);
    }

    /// <summary>Same technique as FenceForm.HitTest: carve the Settings/close button rects out to
    /// HTCLIENT first (so they stay ordinary clickable buttons instead of being swallowed by the
    /// surrounding HTCAPTION), then HTCAPTION for the header row (always - a real caption always
    /// drags too) and, once engaged, the OuterMargin/button-band margin running around the rest of
    /// the visible body - the same "click anywhere in the margin to move it" convenience an active
    /// fence's own OuterMargin gives, minus the resize-vs-move split FenceForm's inactive case adds
    /// (this widget has no resize to reserve that footprint for, so the margin is simply inert
    /// until engaged instead of a resize handle before then).</summary>
    private int HitTest(IntPtr lParam)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(lParam, rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);

        if (_activation.ShouldShow)
        {
            var onLeft = ShouldSettingsButtonOpenLeft();
            if (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint) || GetCloseButtonRect(contentWidth, onLeft).Contains(contentPoint))
                return HTCLIENT;
        }

        // The header row itself always drags, active or not - same as a real caption always would.
        if (contentPoint.Y >= 0 && contentPoint.Y < HeaderHeight)
            return HTCAPTION;

        // The margin band - left/right always, whichever of top/bottom isn't currently the button
        // band (see TopBand/BottomBand, both already OuterMargin-or-bigger on every edge) - drags
        // unconditionally, active or not, same as the header above. Unlike FenceForm's own margin,
        // which only becomes a move handle once ShowsSettingsButton (it's a resize handle the rest of
        // the time, and gating avoids treating the same drag as both at once), this widget has no
        // resize at all - nothing else the margin could ambiguously mean - so there's no reason to
        // withhold it until engaged.
        if (windowPoint.X <= OuterMargin || windowPoint.X >= width - OuterMargin
            || windowPoint.Y <= TopBand || windowPoint.Y >= height - BottomBand)
            return HTCAPTION;

        return HTCLIENT;
    }

    private static Point ScreenLParamToWindowPoint(IntPtr lParam, RECT windowRect)
    {
        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);
        return new Point(screenX - windowRect.Left, screenY - windowRect.Top);
    }

    /// <summary>Right-click/double-click-to-rename are gated to the header row specifically (see
    /// WM_NCLBUTTONDBLCLK/WM_NCRBUTTONDOWN) - HitTest's own HTCAPTION result can't distinguish top
    /// band from header row (both drag the same way), so this re-derives content-space Y
    /// independently rather than trusting the hit-test code carried in wParam.</summary>
    private bool IsOverHeaderRow(IntPtr lParam)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return false;
        var contentPoint = ToContent(ScreenLParamToWindowPoint(lParam, rect));
        return contentPoint.Y >= 0 && contentPoint.Y < HeaderHeight;
    }

    private void ShowHeaderRenameMenu(IntPtr lParam)
    {
        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);
        _headerContextMenu.Show(this, PointToClient(new Point(screenX, screenY)));
    }

    /// <summary>Measures the actual options menu (BuildSettingsRows) against the screen this widget
    /// is currently on, using the button's default top-right placement as the anchor - i.e. "would
    /// the menu fit opening to the right of a right-corner button". Same shared overflow math
    /// FenceForm.ShouldSettingsButtonOpenLeft uses too (StyleMenuRows.ShouldOpenLeft).</summary>
    private bool ShouldSettingsButtonOpenLeft()
    {
        var rightAligned = ToWindow(GetSettingsButtonRect(ContentWidth, onLeft: false));
        var buttonScreenRect = new Rectangle(PointToScreen(rightAligned.Location), rightAligned.Size);
        return StyleMenuRows.ShouldOpenLeft(buttonScreenRect, BuildSettingsRows(), Font);
    }

    /// <summary>ClientSize.Width minus the OuterMargin band on both sides - the visible body's own
    /// width, same "window is wider than content" relationship FenceForm.GetContentSize has to its
    /// own ClientSize, just pre-computed as a property here since this widget's width never changes
    /// at runtime (no separate GetContentSize() call needed to also account for a live resize).</summary>
    private int ContentWidth => Math.Max(0, ClientSize.Width - OuterMargin * 2);

    /// <summary>Content-relative, positioned just outside the visible body, in the reserved
    /// ButtonBandHeight band - same "lives outside the visible body entirely" placement as
    /// FenceForm's own GetSettingsButtonRect, right down to the Y formula (negative - above content
    /// Y=0, the header's own top edge - normally, or GetContentHeight() below the body's own bottom
    /// edge instead once _buttonRowAtBottom flips there). Flush with the top-right corner by
    /// default; flipped to the top-left (see ShouldSettingsButtonOpenLeft) whenever the options
    /// dropdown wouldn't fit opening rightward from there.</summary>
    private Rectangle GetSettingsButtonRect(int width, bool onLeft)
    {
        var y = _buttonRowAtBottom ? GetContentHeight() + SettingsButtonGap : -(SettingsButtonHeight + SettingsButtonGap);
        return onLeft
            ? new Rectangle(0, y, SettingsButtonWidth, SettingsButtonHeight)
            : new Rectangle(width - SettingsButtonWidth, y, SettingsButtonWidth, SettingsButtonHeight);
    }

    /// <summary>Immediately inside the Settings button (i.e. between it and the body) rather than
    /// anchored to its own corner - moves and flips sides (left/right, see onLeft, and top/bottom,
    /// see GetSettingsButtonRect's own Y) together with GetSettingsButtonRect as a pair, always
    /// adjacent to it, same relationship FenceForm's own GetNewFenceButtonRect has to
    /// GetSettingsButtonRect.</summary>
    private Rectangle GetCloseButtonRect(int width, bool onLeft)
    {
        var settings = GetSettingsButtonRect(width, onLeft);
        var x = onLeft ? settings.Right + ButtonSpacing : settings.X - ButtonSpacing - CloseButtonSize;
        return new Rectangle(x, settings.Y, CloseButtonSize, SettingsButtonHeight);
    }

    // No longer needs an onLeft split - the Settings/close buttons moved out of the header
    // entirely (see GetSettingsButtonRect), so the title has the full header width to itself
    // regardless of which corner they're flipped to.
    private static Rectangle GetTitleRect(int width) => new(8, 0, Math.Max(0, width - 16), HeaderHeight);

    private static int GetListAreaHeight(int count) => count == 0 ? EmptyStateHeight : Math.Min(count, MaxVisibleRows) * RowHeight;

    private static Rectangle GetListRect(int width, int listAreaHeight) =>
        new(ListInset, HeaderHeight + 13, width - ListInset * 2, listAreaHeight);

    private static Rectangle GetSaveButtonRect(int width, int listAreaHeight) =>
        new(ListInset, HeaderHeight + 13 + listAreaHeight + ButtonAreaGap, width - ListInset * 2, ButtonHeight);

    private static Rectangle GetManageButtonRect(int width, int listAreaHeight)
    {
        var save = GetSaveButtonRect(width, listAreaHeight);
        return new Rectangle(save.X, save.Bottom + ButtonAreaGap, save.Width, ButtonHeight);
    }

    private static Rectangle GetDeleteGlyphRect(Rectangle rowBounds) =>
        new(rowBounds.Right - 24, rowBounds.Top, 24, rowBounds.Height);

    private static Rectangle GetCopyGlyphRect(Rectangle rowBounds) =>
        new(rowBounds.Right - 24 - 40, rowBounds.Top, 40, rowBounds.Height);

    private Rectangle GetRowRect(int index, Rectangle listRect) =>
        new(listRect.X, listRect.Y + index * RowHeight - _scrollOffset, listRect.Width, RowHeight);

    private int? IndexAtRowPosition(Point clientPoint, Rectangle listRect)
    {
        if (!listRect.Contains(clientPoint))
            return null;
        var index = (clientPoint.Y - listRect.Y + _scrollOffset) / RowHeight;
        return index >= 0 && index < _manager.Profiles.Count ? index : null;
    }

    private int GetMaxScroll(int listAreaHeight) => Math.Max(0, _manager.Profiles.Count * RowHeight - listAreaHeight);

    /// <summary>The visible body's own height - header + list/empty-state + Save/Manage buttons -
    /// with no button band included, same "content" meaning as FenceForm.GetContentSize (just a
    /// single height rather than a Size, since width never changes at runtime here). Doesn't depend
    /// on _buttonRowAtBottom at all - unlike the band, how many layouts are saved has no bearing on
    /// which side of the screen the widget happens to be sitting near.</summary>
    private int GetContentHeight()
    {
        var listAreaHeight = GetListAreaHeight(_manager.Profiles.Count);
        var manage = GetManageButtonRect(_model.Width, listAreaHeight);
        return manage.Bottom + 12;
    }

    /// <summary>Resizes the window so its height always matches how many layouts there currently
    /// are (capped at MaxVisibleRows, past which the list scrolls instead of the window growing
    /// forever), then repaints - called after every content change (Save Current Layout, Copy,
    /// Delete, or returning from the editor) so those changes are reflected immediately rather than
    /// leaving dead space or a cramped scrollbar. Only ever grows/shrinks from the window's current
    /// top-left, same as ClientSize always does - doesn't touch _buttonRowAtBottom, since how many
    /// layouts are saved has no bearing on which side of the screen the button band belongs on.</summary>
    private void RefreshContent()
    {
        ClientSize = new Size(_model.Width + OuterMargin * 2, GetContentHeight() + TopBand + BottomBand);
        RenderAndPresent();
    }

    private void BeginRename()
    {
        if (_renameBox is not null)
            return;

        var titleRect = GetTitleRect(ContentWidth);
        var rect = ToWindow(new Rectangle(titleRect.X - 2, 3, titleRect.Width, 22));
        _renameBox = new EditBox(Handle, _model.Title, RectangleToScreen(rect), Font);
        _renameBox.Commit += OnRenameCommit;
        _renameBox.Cancel += OnRenameCancel;
    }

    private void OnRenameCommit(string newName)
    {
        _renameBox?.Dispose();
        _renameBox = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != _model.Title)
        {
            _model.Title = newName;
            Persist();
        }

        RenderAndPresent();
    }

    private void OnRenameCancel()
    {
        _renameBox?.Dispose();
        _renameBox = null;
        RenderAndPresent();
    }

    /// <summary>"Save Current Layout" - a new profile pre-populated from whatever's actually open
    /// and where it's sitting right now (see LayoutManager.CaptureCurrentLayout), instead of
    /// building one program-by-program through the editor. Opens straight into the editor on the
    /// new profile afterward (via ManageLayoutsRequested) so it's immediately visible and
    /// renamable rather than just silently appearing in this list.</summary>
    private void OnSaveCurrentLayout()
    {
        var profile = _manager.CaptureCurrentLayout($"Layout {_manager.Profiles.Count + 1}");
        RefreshContent();
        ManageLayoutsRequested?.Invoke(this, profile.Id);
    }

    private void ConfirmAndDelete(LayoutProfile profile)
    {
        var result = MessageBox.Show(this, $"Delete \"{profile.Name}\"?", "Delete Layout",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _manager.DeleteLayout(profile.Id);
        RefreshContent();
    }

    /// <summary>Delete/Copy glyphs are hit-tested first (same right-to-left priority order they're
    /// drawn in) - anything else on the row runs that layout immediately, no confirmation. Delete
    /// is the one exception that does confirm (see ConfirmAndDelete) - unlike removing a program
    /// from inside the editor, this throws away an entire saved layout.</summary>
    private void OnRowClicked(int index, Rectangle rowRect, Point location)
    {
        var profile = _manager.Profiles[index];

        if (GetDeleteGlyphRect(rowRect).Contains(location))
        {
            ConfirmAndDelete(profile);
            return;
        }

        if (GetCopyGlyphRect(rowRect).Contains(location))
        {
            _manager.DuplicateLayout(profile.Id);
            RefreshContent();
            return;
        }

        _ = _manager.RunLayoutAsync(profile.Id);
    }

    /// <summary>Same rows/shape as FenceForm.BuildOptionsMenuRows minus everything specific to an
    /// icon-grid fence (Hide Shortcut Names, OCD Fence Sizing, the OCD dimensions flyout) - Hide
    /// Title, the color grid, and all three sliders (Header Darkness/Opacity/Tint Strength) plus the
    /// Margin stepper all carry over with the same meaning (see LayoutLauncherModel's own doc
    /// comments).</summary>
    private List<DropdownMenu.Row> BuildSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(CmdToggleHideTitle, "Hide Title", HasCheckbox: true, IsChecked: () => _model.HideTitle),
            new(CmdToggleFullOpacityOnHover, "Full Opacity When Active", HasCheckbox: true,
                IsChecked: () => _model.FullOpacityOnHover,
                Tooltip: "Full opacity while hovered, dragged, or this menu is open"),
            new(0, string.Empty, IsSeparator: true),
        };
        // The color grid + Header Darkness/Opacity/Tint Strength sliders + Margin stepper - shared
        // with FenceForm's own options menu (its color grid specifically - see BuildColorGrid's own
        // comment for why the rest of its menu still builds its sliders/margin/OCD rows separately),
        // so this widget never has its own slightly-different copy to drift out of sync or re-debug.
        rows.AddRange(StyleMenuRows.Build(_model, AppTheme.Body, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
            SetHeaderDarkness, SetOpacity, SetTintStrength, SetMargin));
        return rows;
    }

    private void OpenSettingsMenu()
    {
        var width = ContentWidth;
        var onLeft = ShouldSettingsButtonOpenLeft();
        var buttonScreenRect = RectangleToScreen(ToWindow(GetSettingsButtonRect(width, onLeft)));
        var dropdown = new DropdownMenu(BuildSettingsRows(), buttonScreenRect, onLeft, Font,
            () => EffectiveField, () => EffectiveHover, () => EffectiveAccent, () => EffectiveBorder, () => EffectiveField);
        _dropdown = dropdown;
        dropdown.ItemClicked += id =>
        {
            HandleCommand(id);
            dropdown.RefreshChecks();
        };
        // Changed (wired in the constructor) handles the repaint/opacity re-check for both of
        // these - no need to call either explicitly here.
        _activation.MenuOpen = true;
        dropdown.FormClosed += (_, _) =>
        {
            _dropdown = null;
            _activation.MenuOpen = false;
        };
        dropdown.Show(this);
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdToggleFullOpacityOnHover:
                _model.FullOpacityOnHover = !_model.FullOpacityOnHover;
                Persist();
                _opacity.BeginIfNeeded();
                break;
            case CmdToggleHideTitle:
                _model.HideTitle = !_model.HideTitle;
                Persist();
                RenderAndPresent();
                break;
            default:
                // Default/preset/Custom.../Eyedropper - same shared handling FenceForm's own color
                // rows use too (see StyleMenuRows' own doc comment). The Eyedropper pick also resets
                // Opacity/Tint Strength so it starts out pixel-exact, same as FenceForm's own
                // PickEyedropperColor does.
                StyleMenuRows.TryHandleColorCommand(id, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
                    AppTheme.Body, this, TintColorOrNull, color => SetTintColor(color), color =>
                    {
                        SetTintColor(color, exact: true);
                        SetOpacity(100);
                        SetTintStrength(0);
                    });
                break;
        }
    }

    /// <summary>exact is only ever true from the Eyedropper (see HandleCommand's own callback into
    /// StyleMenuRows.TryHandleColorCommand) - see IWidgetStyle.TintIsExact's own doc comment.
    /// Mirrors FenceManager.SetTintColor exactly (both branches): picking a Default/preset/
    /// Custom... color (the non-exact branch) also resets Header Darkness/Opacity/Tint Strength
    /// back to their defaults - every preset is meant to look the same predictable way each time
    /// it's picked, not "whatever this tint blends into on top of whatever the sliders already
    /// happened to be left at" - while an Eyedropper pick leaves them alone (SetOpacity(100)/
    /// SetTintStrength(0) in that same caller handle that separately, as its own deliberate "start
    /// pixel-exact" reset rather than this one's "back to the fixed default" reset). Both branches
    /// guard against re-persisting (and re-triggering an opacity snap for) a no-op re-click of
    /// whatever's already active - same guard FenceManager.SetTintColor uses.</summary>
    private void SetTintColor(Color? color, bool exact = false)
    {
        var argb = color?.ToArgb();
        var effectiveExact = color is not null && exact;

        if (!effectiveExact)
        {
            var alreadyDefault = _model.TintColor == argb && _model.TintIsExact == effectiveExact
                && _model.HeaderDarkness == LayoutLauncherModel.DefaultHeaderDarkness
                && _model.Opacity == LayoutLauncherModel.DefaultOpacity
                && _model.TintStrength == LayoutLauncherModel.DefaultTintStrength;
            if (alreadyDefault)
                return;

            _model.TintColor = argb;
            _model.TintIsExact = false;
            _model.HeaderDarkness = LayoutLauncherModel.DefaultHeaderDarkness;
            _model.Opacity = LayoutLauncherModel.DefaultOpacity;
            _model.TintStrength = LayoutLauncherModel.DefaultTintStrength;
            Persist();
            // Opacity may have just changed - same "snap, don't ease" reasoning as SetOpacity's own
            // slider-drag case, so this doesn't render at the old opacity until something else
            // (hover, drag) happens to notice the mismatch.
            _opacity.SnapToTarget();
            RenderAndPresent();
            return;
        }

        if (_model.TintColor == argb && _model.TintIsExact == effectiveExact)
            return;

        _model.TintColor = argb;
        _model.TintIsExact = effectiveExact;
        Persist();
        RenderAndPresent();
    }

    private void SetHeaderDarkness(int darkness)
    {
        _model.HeaderDarkness = Math.Clamp(darkness, 0, 100);
        Persist();
        RenderAndPresent();
    }

    /// <summary>"Layout Launcher Opacity" slider - called on mouse-down and every subsequent
    /// mouse-move while dragging, so the widget repaints continuously as it's dragged rather than
    /// only once on release. Snaps _opacity straight to the new target instead of easing (see
    /// OpacityAnimator.SnapToTarget) - a slider drag needs to track the cursor immediately, an
    /// animated lag here would feel unresponsive.</summary>
    private void SetOpacity(int opacity)
    {
        _model.Opacity = Math.Clamp(opacity, 15, 100);
        Persist();
        _opacity.SnapToTarget();
        RenderAndPresent();
    }

    private void SetTintStrength(int strength)
    {
        _model.TintStrength = Math.Clamp(strength, 0, 100);
        Persist();
        RenderAndPresent();
    }

    private void SetMargin(int margin)
    {
        _model.Margin = Math.Clamp(margin, 0, 100);
        Persist();
    }

    private Color? TintColorOrNull => _model.TintColor is { } argb ? Color.FromArgb(argb) : null;
    private double TintFraction => _model.TintStrength / 100.0;

    /// <summary>Diluted-exact-tint treatment (see IWidgetStyle.TintIsExact) only applies to this and
    /// EffectiveHeader - the widget's two dominant fill areas - same scope FenceForm.ThemedBody/
    /// ThemedTitle use it for. EffectiveBorder below stays plain Tint() at the adjustable
    /// TintFraction regardless of TintIsExact, matching FenceForm's own ThemedBorder.</summary>
    private Color EffectiveBody => _model.TintIsExact && TintColorOrNull is { } exactBody
        ? StyleTint.DilutedExact(exactBody, AppTheme.Body, TintFraction)
        : StyleTint.Tint(AppTheme.Body, TintColorOrNull, TintFraction);
    private Color EffectiveBorder => StyleTint.Tint(AppTheme.Border, TintColorOrNull, TintFraction);

    // Fixed StyleTint.SafeChromeBlend, not the adjustable TintFraction - matches FenceForm's own
    // ChromeFill/ThemedMenuSelected (see SafeChromeBlend's own doc comment for why: this widget's
    // buttons, list rows, and settings/rename menus all pull from these two/EffectiveFieldDark
    // below, and an Eyedropper pick resets TintFraction to 0 so EffectiveBody starts pixel-exact -
    // without this fixed floor, that same reset would leave every one of those secondary surfaces
    // looking completely untinted right after the very pick that was supposed to color them).
    private Color EffectiveField => StyleTint.Tint(AppTheme.Field, TintColorOrNull, StyleTint.SafeChromeBlend);
    private Color EffectiveHover => StyleTint.Tint(AppTheme.Hover, TintColorOrNull, StyleTint.SafeChromeBlend);

    // AppTheme.FieldDark, not Field - the buttons and list field read as washed-out/low-contrast
    // against Body at Field's own lighter tone once it started blending through SafeChromeBlend
    // instead of the old fixed per-control BackColor. Only these two surfaces use it; the settings/
    // rename menus keep the lighter EffectiveField above.
    private Color EffectiveFieldDark => StyleTint.Tint(AppTheme.FieldDark, TintColorOrNull, StyleTint.SafeChromeBlend);

    // Same "goes the exact chosen color, not just a diluted shift toward it" rule FenceForm.Accent
    // uses for its own glyphs/pressed-button state - a blended-toward-grey accent reads muddy at
    // small glyph sizes (the row's Copy/× text, a button's press flash), so this skips TintFraction
    // and either is the tint outright or, with no tint picked, the same neutral gray AppTheme.Accent
    // every other untinted control already uses.
    private Color EffectiveAccent => TintColorOrNull ?? AppTheme.Accent;

    private Color ActiveBorderColor => Color.FromArgb(220, EffectiveAccent);

    // Same relationship as FenceForm.HeaderBaseColor/ThemedTitle - darkened toward black by
    // HeaderDarkness first, and tint blends into what's left of that at a fraction that shrinks
    // toward zero as darkness approaches 100% (a fully-blackened header has nothing left for a tint
    // to visibly shift). An exact Eyedropper pick darkens its own diluted-exact color (EffectiveBody's
    // result, not the raw pick) by that same HeaderDarkness amount instead, so Tint Strength affects
    // the header the same way it does the body - see FenceForm.ThemedTitle's own exact-tint case.
    private Color HeaderBaseColor => StyleTint.DarkenTowardBlack(AppTheme.Body, _model.HeaderDarkness / 100.0);
    private Color EffectiveHeader
    {
        get
        {
            var darkness = _model.HeaderDarkness / 100.0;
            if (_model.TintIsExact && TintColorOrNull is { } exactHeader)
                return StyleTint.DarkenTowardBlack(StyleTint.DilutedExact(exactHeader, AppTheme.Body, TintFraction), darkness);
            return StyleTint.Tint(HeaderBaseColor, TintColorOrNull, TintFraction * (1 - darkness));
        }
    }

    /// <summary>_model.Opacity (0-100%) as the 0.0-1.0 fraction RenderAndPresent's
    /// LayeredWindowPresenter.Present call needs - fully opaque instead whenever FullOpacityOnHover
    /// is on and this widget is "in use": hovered (IsHovered), being dragged (_isMoving), or has its
    /// settings dropdown open (_activation.MenuOpen). Where _opacity.Value should end up, not
    /// necessarily what's rendered right now - RenderAndPresent reads _opacity.Value itself, eased
    /// toward this by OpacityAnimator.</summary>
    private float TargetOpacity => _model.FullOpacityOnHover && (IsHovered || _isMoving || _activation.MenuOpen) ? 1f : _model.Opacity / 100f;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isClientHovered = true;
        _opacity.BeginIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isClientHovered = false;
        _hoverTarget = HoverTarget.None;
        _hoverRowIndex = -1;
        _opacity.BeginIfNeeded();
        RenderAndPresent();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // Right-click anywhere activates (see WidgetActivation's own doc comment) - this covers the
        // client area (rows, buttons, empty space); a right-click on the header band itself never
        // reaches here at all, since HitTest reports that as non-client (see WM_NCRBUTTONDOWN in
        // WndProc, which activates separately for that case).
        if (e.Button == MouseButtons.Right)
        {
            _activation.Activate();
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        var width = ContentWidth;
        var onLeft = ShouldSettingsButtonOpenLeft();
        var contentPoint = ToContent(e.Location);

        if (_activation.ShouldShow && GetSettingsButtonRect(width, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }

        if (_activation.ShouldShow && GetCloseButtonRect(width, onLeft).Contains(contentPoint))
        {
            _closeButtonArmed = true;
            return;
        }

        var count = _manager.Profiles.Count;
        var listAreaHeight = GetListAreaHeight(count);
        var listRect = GetListRect(width, listAreaHeight);

        if (count > 0 && GetScrollbarGeometry(listRect) is { } sb)
        {
            // A little slack around the thin thumb/track makes it easier to grab.
            var thumbRect = new Rectangle(sb.TrackX - 2, sb.ThumbY, ScrollbarWidth + 4, sb.ThumbHeight);
            if (thumbRect.Contains(contentPoint))
            {
                _scrollbarDragging = true;
                _scrollbarDragStartY = contentPoint.Y;
                _scrollbarDragStartOffset = _scrollOffset;
                Capture = true;
                return;
            }

            var trackRect = new Rectangle(sb.TrackX - 2, sb.TrackTop, ScrollbarWidth + 4, sb.TrackHeight);
            if (trackRect.Contains(contentPoint))
            {
                // Clicking the track outside the thumb pages toward the click, like a normal scrollbar.
                var page = Math.Max(RowHeight, sb.TrackHeight - RowHeight);
                _scrollOffset = Math.Clamp(_scrollOffset + (contentPoint.Y < sb.ThumbY ? -page : page), 0, GetMaxScroll(listAreaHeight));
                RenderAndPresent();
                return;
            }
        }

        if (IndexAtRowPosition(contentPoint, listRect) is int rowIndex)
        {
            OnRowClicked(rowIndex, GetRowRect(rowIndex, listRect), contentPoint);
            return;
        }

        if (GetSaveButtonRect(width, listAreaHeight).Contains(contentPoint))
        {
            _saveButtonArmed = true;
            return;
        }

        if (GetManageButtonRect(width, listAreaHeight).Contains(contentPoint))
            _manageButtonArmed = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            var onLeft = ShouldSettingsButtonOpenLeft();
            if (_activation.ShouldShow && GetSettingsButtonRect(ContentWidth, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        if (_closeButtonArmed)
        {
            _closeButtonArmed = false;
            var onLeft = ShouldSettingsButtonOpenLeft();
            if (_activation.ShouldShow && GetCloseButtonRect(ContentWidth, onLeft).Contains(contentPoint))
                HideAndPersist();
            return;
        }

        if (_scrollbarDragging)
        {
            _scrollbarDragging = false;
            Capture = false;
            return;
        }

        if (_saveButtonArmed)
        {
            _saveButtonArmed = false;
            var listAreaHeight = GetListAreaHeight(_manager.Profiles.Count);
            if (GetSaveButtonRect(ContentWidth, listAreaHeight).Contains(contentPoint))
                OnSaveCurrentLayout();
            return;
        }

        if (_manageButtonArmed)
        {
            _manageButtonArmed = false;
            var listAreaHeight = GetListAreaHeight(_manager.Profiles.Count);
            if (GetManageButtonRect(ContentWidth, listAreaHeight).Contains(contentPoint))
                ManageLayoutsRequested?.Invoke(this, null);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var width = ContentWidth;
        var count = _manager.Profiles.Count;
        var listAreaHeight = GetListAreaHeight(count);
        var contentPoint = ToContent(e.Location);

        if (_scrollbarDragging)
        {
            var listRect = GetListRect(width, listAreaHeight);
            if (GetScrollbarGeometry(listRect) is { } sb && sb.TrackHeight > sb.ThumbHeight)
            {
                var maxScroll = GetMaxScroll(listAreaHeight);
                var maxThumbTravel = sb.TrackHeight - sb.ThumbHeight;
                var dy = contentPoint.Y - _scrollbarDragStartY;
                var deltaScroll = (int)((long)dy * maxScroll / maxThumbTravel);
                _scrollOffset = Math.Clamp(_scrollbarDragStartOffset + deltaScroll, 0, maxScroll);
                RenderAndPresent();
            }
            return;
        }

        var onLeft = ShouldSettingsButtonOpenLeft();
        var newHoverTarget = HoverTarget.None;
        var newHoverRow = -1;

        if (_activation.ShouldShow && GetSettingsButtonRect(width, onLeft).Contains(contentPoint))
            newHoverTarget = HoverTarget.Settings;
        else if (_activation.ShouldShow && GetCloseButtonRect(width, onLeft).Contains(contentPoint))
            newHoverTarget = HoverTarget.Close;
        else if (GetSaveButtonRect(width, listAreaHeight).Contains(contentPoint))
            newHoverTarget = HoverTarget.Save;
        else if (GetManageButtonRect(width, listAreaHeight).Contains(contentPoint))
            newHoverTarget = HoverTarget.Manage;
        else if (IndexAtRowPosition(contentPoint, GetListRect(width, listAreaHeight)) is int rowIndex)
            newHoverRow = rowIndex;

        if (newHoverTarget != _hoverTarget || newHoverRow != _hoverRowIndex)
        {
            _hoverTarget = newHoverTarget;
            _hoverRowIndex = newHoverRow;
            RenderAndPresent();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        var listAreaHeight = GetListAreaHeight(_manager.Profiles.Count);
        var maxScroll = GetMaxScroll(listAreaHeight);
        if (maxScroll <= 0)
            return;

        _scrollOffset = Math.Clamp(_scrollOffset - e.Delta / 120 * RowHeight, 0, maxScroll);
        RenderAndPresent();
    }

    private readonly record struct ScrollbarGeometry(int TrackX, int TrackTop, int TrackHeight, int ThumbY, int ThumbHeight);

    /// <summary>Null when the list doesn't need to scroll (no scrollbar to draw or hit-test). Same
    /// geometry approach as FenceForm.GetScrollbarGeometry. Purely relative math, so it doesn't care
    /// whether listRect is content-space or already window-space (see ToContent/ToWindow) - the
    /// result just inherits whichever one the caller passed in, which is why RenderAndPresent (own
    /// space: window) and OnMouseDown/Up/Move (own space: content) can each call this directly with
    /// their own listRect instead of a separate ToWindow overload for the result.</summary>
    private ScrollbarGeometry? GetScrollbarGeometry(Rectangle listRect)
    {
        var maxScroll = GetMaxScroll(listRect.Height);
        if (maxScroll <= 0)
            return null;

        var trackX = listRect.Right - ScrollbarWidth - ScrollbarMargin;
        var totalHeight = listRect.Height + maxScroll;
        var thumbHeight = Math.Min(listRect.Height, Math.Max(20, (int)((long)listRect.Height * listRect.Height / Math.Max(1, totalHeight))));
        var maxThumbTravel = Math.Max(0, listRect.Height - thumbHeight);
        var thumbY = listRect.Y + (maxThumbTravel > 0 ? (int)((long)_scrollOffset * maxThumbTravel / maxScroll) : 0);

        return new ScrollbarGeometry(trackX, listRect.Y, listRect.Height, thumbY, thumbHeight);
    }

    /// <summary>Builds this frame's full appearance (body, header, rows, buttons, scrollbar) into an
    /// off-screen ARGB bitmap and pushes it to the screen via UpdateLayeredWindow, same as
    /// FenceForm.RenderAndPresent - called any time something visible changes (hover, drag, rename,
    /// content) rather than in response to WM_PAINT, since a layered window's content isn't
    /// repainted by Windows itself.</summary>
    private void RenderAndPresent()
    {
        if (_disposing || !IsHandleCreated)
            return;

        if (!NativeMethods.GetWindowRect(Handle, out var windowRect))
            return;

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0)
            return;

        // width/height above stay window-space, for the outer margin-fill and the separator line
        // (which spans the window's own full width) - everything else below is content-space, same
        // "convert once, use everywhere" split ToContent/ToWindow already establish for Y.
        var contentWidth = width - OuterMargin * 2;
        var count = _manager.Profiles.Count;
        var listAreaHeight = GetListAreaHeight(count);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, GetMaxScroll(listAreaHeight));
        var onLeft = ShouldSettingsButtonOpenLeft();

        using var buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.Clear(Color.Transparent);

            // The margin band (OuterMargin on every edge, wider still on whichever of top/bottom
            // currently holds the button row - see _buttonRowAtBottom) needs a non-zero (if faint)
            // alpha - Windows treats fully transparent (alpha 0) pixels of a layered window as
            // click-through, so a truly invisible margin couldn't receive the drag/button
            // hit-testing it exists for (see HitTest). This gets drawn first and the opaque
            // body/header/buttons then cover all of it except the margin itself. Same MarginFillColor
            // trick FenceForm's own OuterMargin/TopBand fill uses.
            using (var marginFill = new SolidBrush(MarginFillColor))
                g.FillRectangle(marginFill, 0, 0, width, height);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            var bodyRect = ToWindow(new Rectangle(0, 0, contentWidth - 1, height - TopBand - BottomBand - 1));
            using (var body = RoundedRectPath.Full(bodyRect, CornerRadius))
            {
                using var bodyFill = new SolidBrush(EffectiveBody);
                g.FillPath(bodyFill, body);

                var showActiveBorder = _activation.ShouldShow;
                using var borderPen = new Pen(showActiveBorder ? ActiveBorderColor : EffectiveBorder, showActiveBorder ? ActiveBorderWidth : 1f)
                {
                    LineJoin = LineJoin.Round,
                };
                g.DrawPath(borderPen, body);
            }

            using (var header = RoundedRectPath.Top(ToWindow(new Rectangle(0, 0, contentWidth - 1, HeaderHeight)), CornerRadius))
            using (var headerFill = new SolidBrush(EffectiveHeader))
                g.FillPath(headerFill, header);

            using (var separatorPen = new Pen(EffectiveBorder))
            {
                var separatorY = TopBand + HeaderHeight;
                g.DrawLine(separatorPen, OuterMargin, separatorY, OuterMargin + contentWidth, separatorY);
            }

            if (!_model.HideTitle && _renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Title, Font, ToWindow(GetTitleRect(contentWidth)), AppTheme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (_activation.ShouldShow)
            {
                DrawChromeButton(g, ToWindow(GetSettingsButtonRect(contentWidth, onLeft)), "Settings", _settingsButtonArmed, _hoverTarget == HoverTarget.Settings);
                DrawChromeButton(g, ToWindow(GetCloseButtonRect(contentWidth, onLeft)), "×", _closeButtonArmed, _hoverTarget == HoverTarget.Close);
            }

            var listRect = GetListRect(contentWidth, listAreaHeight);
            var windowListRect = ToWindow(listRect);
            if (count == 0)
            {
                TextRenderer.DrawText(g, EmptyStateText, Font, windowListRect, AppTheme.DisabledText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }
            else
            {
                using (var listFill = new SolidBrush(EffectiveFieldDark))
                    g.FillRectangle(listFill, windowListRect);

                var previousClip = g.Clip;
                g.SetClip(windowListRect, CombineMode.Intersect);
                for (var index = 0; index < count; index++)
                {
                    var rowRect = ToWindow(GetRowRect(index, listRect));
                    if (rowRect.Bottom <= windowListRect.Top || rowRect.Top >= windowListRect.Bottom)
                        continue;
                    DrawRow(g, index, rowRect);
                }
                g.Clip = previousClip;

                using (var listBorderPen = new Pen(EffectiveBorder))
                    g.DrawRectangle(listBorderPen, windowListRect);

                // Passed the already window-space rect, not the content-space listRect above - see
                // GetScrollbarGeometry's own comment on why that's enough (its own math is purely
                // relative, so the geometry it returns just inherits whichever space its input was
                // already in - no separate ToWindow overload needed for the result).
                if (GetScrollbarGeometry(windowListRect) is { } sb)
                    PaintScrollbar(g, sb);
            }

            DrawChromeButton(g, ToWindow(GetSaveButtonRect(contentWidth, listAreaHeight)), "Save Current Layout", _saveButtonArmed, _hoverTarget == HoverTarget.Save);
            DrawChromeButton(g, ToWindow(GetManageButtonRect(contentWidth, listAreaHeight)), "Manage Layouts...", _manageButtonArmed, _hoverTarget == HoverTarget.Manage);
        }

        LayeredWindowPresenter.Present(Handle, buffer, new Point(windowRect.Left, windowRect.Top), _opacity.Value);
    }

    /// <summary>Every row reserves two glyph strips at its right edge (delete, then copy, working
    /// inward from the edge) ahead of the profile name - same DrawRemovableListItem-style hit-testable
    /// glyph approach LayoutEditorForm's Programs/URLs lists already use, extended to two glyphs
    /// instead of one since a row here needs both actions.</summary>
    private void DrawRow(Graphics g, int index, Rectangle rowRect)
    {
        using (var background = new SolidBrush(index == _hoverRowIndex ? EffectiveHover : EffectiveFieldDark))
            g.FillRectangle(background, rowRect);

        var deleteRect = GetDeleteGlyphRect(rowRect);
        var copyRect = GetCopyGlyphRect(rowRect);
        var textRect = new Rectangle(rowRect.X + 8, rowRect.Y, copyRect.X - rowRect.X - 8, rowRect.Height);

        TextRenderer.DrawText(g, _manager.Profiles[index].Name, Font, textRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Copy", Font, copyRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "×", Font, deleteRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawChromeButton(Graphics g, Rectangle rect, string text, bool armed, bool hovered)
    {
        using var path = RoundedRectPath.Full(rect, 6);
        using (var fill = new SolidBrush(armed ? EffectiveAccent : hovered ? EffectiveHover : EffectiveFieldDark))
            g.FillPath(fill, path);
        using (var pen = new Pen(EffectiveBorder))
            g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, Font, rect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private void PaintScrollbar(Graphics g, ScrollbarGeometry sb)
    {
        using (var trackBrush = new SolidBrush(EffectiveFieldDark))
            g.FillRectangle(trackBrush, sb.TrackX, sb.TrackTop, ScrollbarWidth, sb.TrackHeight);

        using var thumbPath = RoundedRectPath.Full(new Rectangle(sb.TrackX, sb.ThumbY, ScrollbarWidth, sb.ThumbHeight), ScrollbarWidth / 2);
        using var thumbBrush = new SolidBrush(EffectiveHover);
        g.FillPath(thumbBrush, thumbPath);
    }
}
