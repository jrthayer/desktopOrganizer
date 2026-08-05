using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Fences.Native;
using DesktopTool.Native;
using DesktopTool.Features.Snapping;

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
    // below, all of which use TopMargin instead of OuterMargin for that one edge. Grown by the same
    // +2 as SettingsButtonGap below, so the breathing room above the button row (between it and this
    // window's own top edge) stays what it was before that gap grew.
    private const int SettingsButtonOverhang = 19;
    private const int TopMargin = OuterMargin + SettingsButtonOverhang;
    private const int CornerRadius = 22;

    // True when placing the wider TopMargin band above the fence's current on-screen position would
    // extend above its monitor's own working area - in that case the extra band (and the settings/
    // "+"/"x" button row within it) moves below the fence instead, so the fence can still sit flush
    // with the very top of the screen without its own button row going unreachably off-screen. Kept
    // in sync wherever the fence's position is computed/changed (CreateParams at handle-creation
    // time, and WM_MOVING/WM_SIZING on every tick of a live drag/resize) rather than read fresh on
    // every use, since CreateParams itself is only ever consulted once, before the window - and so
    // TopBand/BottomBand below - exist at all.
    private bool _buttonRowAtBottom;

    /// <summary>The margin band on whichever side currently holds the button row - see
    /// _buttonRowAtBottom. TopMargin-sized there, same as always; zero on the top side once flipped
    /// (see BottomBand below for why).</summary>
    private int TopBand => _buttonRowAtBottom ? 0 : TopMargin;

    /// <summary>The margin band on whichever side does NOT currently hold the button row - see
    /// _buttonRowAtBottom. Normally a plain OuterMargin, like the left/right/bottom edges always
    /// are - except once flipped, when TopBand above goes to 0 instead: whatever keeps this app's
    /// own drag loop from letting the fence's edge fully reach the screen's own edge (observed
    /// settling exactly OuterMargin short of it, every time, even after the flip first shrank it
    /// from TopMargin down to OuterMargin) reacts to any nonzero margin there at all, not just a
    /// wide one - only removing it outright lets the fence sit flush with the very top of the
    /// screen. The resize-grab hit-test zone on that side still isn't literally zero-width (see
    /// HitTest's own ResizeMargin addition), just without this extra invisible cushion beyond the
    /// body's own edge.</summary>
    private int BottomBand => _buttonRowAtBottom ? TopMargin : OuterMargin;

    /// <summary>bodyScreenLocation is the fence's visible body's own top-left corner in screen
    /// coordinates (FenceModel.Bounds' convention, or a live candidate replacement for it mid-drag).</summary>
    private static bool ComputeButtonRowAtBottom(Point bodyScreenLocation) =>
        bodyScreenLocation.Y - TopMargin < Screen.FromPoint(bodyScreenLocation).WorkingArea.Top;

    // Fallback accent (drag-target outline, menu checkmarks, settings button, active-fence border)
    // for a fence that hasn't been given its own color (FenceModel.TintColor is null) - see
    // Accent/ShowFenceOptionsMenu's "Fence Color" grid. Plain grayscale rather than the blue this
    // used to be, matching the rest of the untinted theme's own black/gray palette (DefaultBodyColor
    // etc. below) instead of standing out as the one accented color in an otherwise colorless
    // default. Light enough to still read clearly against ThemedBody's near-black fill.
    private static readonly Color DefaultAccentColor = Color.FromArgb(190, 190, 195);
    private static readonly Color DefaultBodyColor = Color.FromArgb(255, 32, 32, 36);
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
    private const int WM_ENTERSIZEMOVE = 0x0231;
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
    private const int CmdRenameItem = 6;
    private const int CmdToggleHideLabels = 7;
    private const int CmdToggleHideTitle = 8;
    private const int CmdResizeBoth = 9;
    private const int CmdResizeLeftRight = 10;
    private const int CmdResizeTopDown = 11;
    private const int CmdToggleOcdSizing = 12;
    private const int CmdColorDefault = 13;
    private const int CmdColorCustom = 14;
    private const int CmdColorEyedrop = 15;
    private const int CmdToggleFullOpacityOnHover = 16;
    // A contiguous block reserved for the preset swatches (see ColorPresets) - avoids one named
    // const per swatch the way the other commands have, since these are looked up by index rather
    // than individually referenced anywhere.
    private const int CmdColorPresetBase = 20;

    // Not real WM_COMMAND ids - just Row.Id values for the non-clickable section headers in
    // ShowFenceOptionsMenu's dropdown (DropdownMenu.Row.IsHeader rows don't dispatch a command either
    // way, so these only need to be distinct from real command ids, never looked up).
    private const int TagColorHeader = 1003;
    private const int TagFenceDimensionsHeader = 1004;
    private const int TagHeaderDarknessHeader = 1005;
    private const int TagFenceOpacityHeader = 1006;
    private const int TagTintStrengthHeader = 1007;
    private const int TagMarginHeader = 1008;

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
    // Vertical gap between the button row's bottom edge and the fence's own top edge (TopMargin
    // above reserves enough extra room for this plus a little more breathing space above the
    // buttons) - a couple pixels more than the bare minimum so there's clearly separate room to grab
    // for a drag right at the fence's edge, instead of that margin butting straight up against the
    // buttons themselves.
    private const int SettingsButtonGap = 6;
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
    private EditBox? _renameBox;
    private EditBox? _itemRenameBox;
    private string? _itemRenamePath;
    private string? _contextItem;
    private int _hoverIndex = -1;
    // Together back "Full Opacity When Active" (see IsHovered/TargetOpacity) - split into
    // client/non-client because they're detected two completely different ways (see
    // OnMouseEnter/OnMouseLeave for the client half, WM_NCMOUSEMOVE/WM_NCMOUSELEAVE in WndProc for
    // the margin/resize band).
    private bool _isClientHovered;
    private bool _isNonClientHovered;
    private bool IsHovered => _isClientHovered || _isNonClientHovered;
    // Set between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE (see WndProc) - covers both an interactive
    // move and an interactive resize, same as _resizeInProgress's own window.
    private bool _isMoving;

    // The opacity actually being rendered right now (see EffectiveOpacity) - separate from
    // TargetOpacity (what it should end up at) so a hover/drag/settings-open-triggered change can
    // animate smoothly toward the target over several ticks instead of jumping there in one repaint.
    // A direct settings change (the Opacity slider, toggling Full Opacity When Active) snaps this
    // straight to the target instead - see SetOpacity/ToggleFullOpacityOnHover - since a slider drag
    // needs to track the cursor immediately, not lag behind it.
    private float _displayOpacity;
    private readonly System.Windows.Forms.Timer _opacityAnimTimer;
    private const float OpacityAnimStep = 0.06f;

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

    // Cursor screen position at WM_ENTERSIZEMOVE - lets WM_MOVING/WM_SIZING compute the proposed
    // rect as _model.Bounds (fixed for the whole drag) plus the *total* cursor delta since the drag
    // started, instead of trusting the RECT the OS's own loop hands us in lParam directly. That RECT
    // tracks INCREMENTALLY, not absolutely - once this code writes back a snapped rect that differs
    // from what was proposed, the OS's internal drag state adopts that snapped rect as its new
    // baseline, and the next tick's proposal is built from *that* plus only the latest incremental
    // mouse movement. Every individual snap during a drag permanently bakes its own clamp into that
    // baseline with no way to undo it, so across a drag that snaps more than once the cursor and the
    // fence drift further and further apart, compounding with each snap rather than resetting once
    // you pull free of one. Recomputing the proposal from a fixed start point every tick sidesteps
    // the OS's own drifted baseline entirely - our snap decisions are always made against where the
    // cursor truly is relative to where the drag began, so leaving a snap zone snaps the fence right
    // back to tracking the cursor exactly, with nothing carried over from any earlier snap.
    private Point _leftDragStartScreenPoint;

    // The currently-open fence-options dropdown (see ShowFenceOptionsMenu/DropdownMenu), or null
    // when none is open. Tracked so a second click on the settings button while one is already open
    // can dispose the stale instance first, and so Dispose can tear it down along with everything
    // else if the fence itself goes away while it's still open (e.g. Delete Fence, clicked from
    // within this very dropdown).
    private DropdownMenu? _dropdown;

    /// <summary>Whether the settings button should be visible/clickable/drawn as active right now -
    /// _isActive alone used to be enough, but DropdownMenu is a real WinForms Form, and showing one
    /// steals OS activation from this fence exactly like any other window would (a native
    /// TrackPopupMenuEx menu never did that, which is why this wasn't needed before it). Without
    /// this OR, the instant the dropdown opened, OnDeactivate would flip _isActive back to false and
    /// the button/active border would vanish right out from under the menu that's still open. This
    /// intentionally does NOT touch OnDeactivate itself to compensate (e.g. checking whether the
    /// newly-active window is our own dropdown there) - an earlier version of this fence tried
    /// exactly that kind of "inspect who's now active" fixup inside OnDeactivate for a similar
    /// popup-vs-activation conflict and it was racy across multiple fences during activation
    /// handoff. This is a plain OR'd flag instead, driven only by our own deterministic
    /// open/close calls (ShowFenceOptionsMenu sets _dropdown; DropdownMenu.FormClosed clears it).</summary>
    private bool ShowsSettingsButton => _isActive || _dropdown is not null;

    // Guards RenderAndPresent against a reentrant repaint triggered mid-teardown - see Dispose's
    // own comment on WM_ACTIVATE firing synchronously from within base.Dispose(disposing).
    private bool _disposing;

    public Guid FenceId => _model.Id;

    /// <summary>Used only for the cross-fence "Move to {name}" drag hint (see ComputeDragHint) -
    /// every other cross-fence reference goes through FenceId/FenceManager instead.</summary>
    internal string FenceName => _model.Name;

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

    /// <summary>_model.TintStrength (0-100%) as the 0.0-1.0 fraction Tint's amount parameter needs -
    /// how strongly a preset/Custom... pick blends into the fence's own body/border/title, adjustable
    /// via the "Tint Strength" slider (see ShowFenceOptionsMenu). Never used for menu/button chrome -
    /// see SafeChromeBlend.</summary>
    private double TintAmount => _model.TintStrength / 100.0;

    /// <summary>A fixed blend amount, deliberately NOT tied to TintAmount - used anywhere fixed
    /// WhiteSmoke text or glyphs get drawn on top of a tinted fill (the settings dropdown, its
    /// tooltips, and the Settings/"+"/"x" buttons - see ChromeFill/ThemedMenuSelected and both
    /// DrawTooltip call sites). If this moved with TintAmount, dragging Tint Strength toward 100%
    /// with a light color would make that fixed text unreadable, the exact bug ChromeFill was added
    /// to fix in the first place - this pins it back to that same safe level regardless.</summary>
    private const double SafeChromeBlend = 0.55;

    /// <summary>Only meaningful when TintIsExact - dilutes an Eyedropper pick back toward
    /// DefaultBodyColor by TintAmount, the *reverse* direction from the regular Tint(base, tint,
    /// amount) call (there, amount=0 means "ignore the pick"; here, amount=0 means "keep the pick
    /// exact"). PickEyedropperColor sets TintStrength to 0 at the moment of picking for exactly that
    /// reason - a fresh pick starts pixel-exact, and dragging Tint Strength up from there is how you
    /// deliberately mute it back toward the plain theme instead.</summary>
    private Color DilutedExactTint(Color exact) => Tint(exact, DefaultBodyColor, TintAmount);

    /// <summary>ThemedBody blends a preset/Custom... pick into DefaultBodyColor at TintAmount same as
    /// always; an Eyedropper pick (TintIsExact) instead starts from the exact color and dilutes it
    /// toward DefaultBodyColor by that same TintAmount (see DilutedExactTint) - both directions read
    /// as "how much of the fence's own picked color survives" even though the blend runs opposite
    /// ways under the hood.</summary>
    private Color ThemedBody => _model.TintIsExact && CurrentTint is { } exactBodyTint ? DilutedExactTint(exactBodyTint) : Tint(DefaultBodyColor, CurrentTint, TintAmount);

    /// <summary>Always SafeChromeBlend, even when TintIsExact, the pick is just a light preset/Custom...
    /// color, or Tint Strength is turned all the way up - unlike ThemedBody/Accent, anything drawing
    /// fixed WhiteSmoke text or glyphs on top of a fill needs to stay readable no matter how
    /// light/bright the fence's own color is. Used for the settings dropdown's background
    /// (ShowFenceOptionsMenu) and the Settings/"+"/"x" button fills (RenderAndPresent) instead of
    /// ThemedBody/Accent for this reason.</summary>
    private Color ChromeFill => Tint(DefaultBodyColor, CurrentTint, SafeChromeBlend);

    /// <summary>_model.Opacity (0-100%) as the 0.0-1.0 fraction RenderAndPresent's
    /// LayeredWindowPresenter.Present call needs - fully opaque instead whenever FullOpacityOnHover is
    /// on and this fence is "in use": hovered (IsHovered), being dragged/resized (_isMoving), or has
    /// its settings dropdown open (_dropdown is not null) - see the field/BeginOpacityAnimationIfNeeded
    /// call sites for each of those three. Not forced to 100% for TintIsExact - PickEyedropperColor
    /// sets Opacity to 100 at the moment of picking instead, so a fresh Eyedropper pick still starts
    /// pixel-exact, but the user can deliberately trade that exactness away afterward via the Fence
    /// Opacity slider (see its own row) the same as any other fence. Where _displayOpacity should end
    /// up, not necessarily what's rendered right now - see EffectiveOpacity.</summary>
    private float TargetOpacity => _model.FullOpacityOnHover && (IsHovered || _isMoving || _dropdown is not null) ? 1f : _model.Opacity / 100f;

    /// <summary>What Present actually renders with - _displayOpacity, animated toward TargetOpacity
    /// rather than reading it directly (see _displayOpacity's own field comment).</summary>
    private float EffectiveOpacity => _displayOpacity;

    /// <summary>color blended toward black by amount (0.0-1.0) - shared by HeaderBaseColor (starting
    /// from the fixed default body color) and ThemedTitle's exact-tint case (starting from the
    /// Eyedropper's own picked color instead).</summary>
    private static Color DarkenTowardBlack(Color color, double amount) => Color.FromArgb(255,
        (int)Math.Round(color.R * (1 - amount)),
        (int)Math.Round(color.G * (1 - amount)),
        (int)Math.Round(color.B * (1 - amount)));

    /// <summary>DefaultBodyColor blended toward black by _model.HeaderDarkness (0-100%) - the title
    /// bar's own base color before CurrentTint's separate blend on top (see ThemedTitle), for every
    /// tint source except an exact Eyedropper pick (which darkens its own exact color instead - see
    /// ThemedTitle). Used to be a fixed near-black constant until "Header Darkness" made it
    /// user-adjustable - see ShowFenceOptionsMenu's slider row.</summary>
    private Color HeaderBaseColor => DarkenTowardBlack(DefaultBodyColor, _model.HeaderDarkness / 100.0);

    /// <summary>Tints HeaderBaseColor same as every other Themed* color, but with TintAmount's own
    /// strength shrinking as HeaderDarkness rises - otherwise a strongly tinted fence could never get
    /// very dark even at 100% darkness, since the tint would keep pulling the result back toward the
    /// (likely much brighter) tint color regardless of how dark the base started. At 100% darkness
    /// the tint's influence drops to 0, reaching true black. An exact Eyedropper pick (see ThemedBody)
    /// darkens its own ThemedBody color (DilutedExactTint's result, not the raw pick) by the same
    /// HeaderDarkness amount instead, so Tint Strength affects the title the same way it does the
    /// body.</summary>
    private Color ThemedTitle
    {
        get
        {
            var darkness = _model.HeaderDarkness / 100.0;
            if (_model.TintIsExact && CurrentTint is { } exactTitleTint)
                return DarkenTowardBlack(DilutedExactTint(exactTitleTint), darkness);
            return Tint(HeaderBaseColor, CurrentTint, TintAmount * (1 - darkness));
        }
    }
    private Color ThemedBorder => Tint(DefaultBorderColor, CurrentTint, TintAmount);
    // SafeChromeBlend, not TintAmount - hover-highlighted rows (this fence's own DropdownMenu, and
    // the native right-click context menus via DrawMenuItem) draw fixed WhiteSmoke text on top, same
    // readability reasoning as ChromeFill.
    private Color ThemedMenuSelected => Tint(DefaultMenuSelectedColor, CurrentTint, SafeChromeBlend);
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

            _buttonRowAtBottom = ComputeButtonRowAtBottom(_model.Bounds.Location);

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
    {
        _model = model;
        _manager = manager;
        _anchorStrategy = anchorStrategy;
        _displayOpacity = _model.Opacity / 100f;
        _opacityAnimTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _opacityAnimTimer.Tick += (_, _) => StepOpacityAnimation();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
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

    /// <summary>The visible fence's size, i.e. the actual (padded) window size minus OuterMargin on
    /// the left/right/bottom-band side and TopMargin on the button-row side (see _buttonRowAtBottom)
    /// - all grid/hit-test math below is in this "content" space.</summary>
    private Size GetContentSize()
    {
        NativeMethods.GetClientRect(Handle, out var clientRect);
        return new Size(Math.Max(0, clientRect.Right - OuterMargin * 2), Math.Max(0, clientRect.Bottom - TopBand - BottomBand));
    }

    private Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - TopBand);

    private Point ToWindow(Point contentPoint) => new(contentPoint.X + OuterMargin, contentPoint.Y + TopBand);

    private Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + TopBand, contentRect.Width, contentRect.Height);

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

    /// <summary>Content-relative, positioned just outside the visible fence, in the taller band -
    /// normally directly above it, but below instead once _buttonRowAtBottom flips there (see its
    /// own comment) so the row stays reachable when the fence is flush with the top of the screen.
    /// Works the same whether or not FenceModel.HideTitle leaves a title bar underneath it. Only
    /// meaningful while _isActive (the button isn't shown otherwise). Y is negative when above
    /// content-space y=0, which is fine everywhere this is used (hit-testing, painting via ToWindow,
    /// menu positioning) - painting the "below" case the same way just needs a positive Y past the
    /// content's own bottom instead. Flush with the top-right corner by default; flipped to the
    /// top-left corner instead when ShouldSettingsButtonOpenLeft says the options menu wouldn't fit
    /// opening rightward from the right corner - see ShowFenceOptionsMenu, which reuses this same
    /// rect's X to decide which side the menu itself opens on, so the two always agree.</summary>
    private Rectangle GetSettingsButtonRect(int contentWidth)
    {
        var x = ShouldSettingsButtonOpenLeft(contentWidth) ? 0 : contentWidth - SettingsButtonWidth;
        var y = _buttonRowAtBottom ? GetContentSize().Height + SettingsButtonGap : -(SettingsButtonHeight + SettingsButtonGap);
        return new Rectangle(x, y, SettingsButtonWidth, SettingsButtonHeight);
    }

    /// <summary>Measures the actual options menu (see BuildOptionsMenuRows/DropdownMenu.Measure)
    /// against the screen the fence is currently on, using the button's default top-right placement
    /// as the anchor - i.e. "would the menu fit opening to the right of a right-corner button". Also
    /// factors in the widest row tooltip (DropdownMenu.MaxTooltipWidth) - a row's own tooltip always
    /// extends the same direction the menu itself opened (see DropdownMenu's _actualLeft), so it
    /// reaches even further right than the menu's own edge, and needs accounting for here too or the
    /// button/menu could end up correctly on-screen while a wide tooltip still didn't fit, only to be
    /// discovered - and only fixable - by flipping everything live the moment that row got hovered.
    /// Deciding it all here instead, before the button/menu/tooltip ever open, means they're already
    /// on the correct corner together from the very first frame. Only true near the right edge of a
    /// monitor, same trigger as the menu's own fallback flip inside DropdownMenu.ComputeBounds (a
    /// second, independent safety net for whatever this couldn't foresee - see DropdownMenu.
    /// UpdateTooltip's own defensive clamp).</summary>
    private bool ShouldSettingsButtonOpenLeft(int contentWidth)
    {
        var rightAligned = new Rectangle(contentWidth - SettingsButtonWidth, -(SettingsButtonHeight + SettingsButtonGap),
            SettingsButtonWidth, SettingsButtonHeight);
        var buttonScreenRect = new Rectangle(PointToScreen(ToWindow(rightAligned.Location)), rightAligned.Size);
        var workingArea = Screen.FromRectangle(buttonScreenRect).WorkingArea;
        var rows = BuildOptionsMenuRows();
        var menuSize = DropdownMenu.Measure(rows, _font);
        var maxTooltipWidth = DropdownMenu.MaxTooltipWidth(rows, _font);
        var tooltipReach = maxTooltipWidth > 0 ? DropdownMenu.AnchorGap + maxTooltipWidth : 0;
        return buttonScreenRect.Right + DropdownMenu.AnchorGap + menuSize.Width + tooltipReach > workingArea.Right;
    }

    /// <summary>Immediately inside the settings button (i.e. between it and the fence body) rather
    /// than anchored to its own corner - moves and flips sides together with GetSettingsButtonRect as
    /// a pair, always adjacent to it. Duplicates this fence's settings into a new, empty fence (see
    /// FenceManager.CreateFenceLike) when clicked.</summary>
    private Rectangle GetNewFenceButtonRect(int contentWidth)
    {
        var settingsRect = GetSettingsButtonRect(contentWidth);
        var onLeft = settingsRect.X == 0;
        var x = onLeft ? settingsRect.Right + ButtonSpacing : settingsRect.X - ButtonSpacing - SmallButtonSize;
        return new Rectangle(x, settingsRect.Y, SmallButtonSize, SettingsButtonHeight);
    }

    /// <summary>Chained off GetNewFenceButtonRect the same way that one chains off
    /// GetSettingsButtonRect - the three buttons move/flip together as one group, always in the same
    /// relative order (Settings outermost, then "+", then this one, innermost/closest to the fence
    /// body). Deletes the fence (with confirmation - see ConfirmDelete) when clicked; this replaces
    /// "Delete Fence" as a row inside the settings dropdown, which no longer has one.</summary>
    private Rectangle GetDeleteButtonRect(int contentWidth)
    {
        var newFenceRect = GetNewFenceButtonRect(contentWidth);
        var onLeft = GetSettingsButtonRect(contentWidth).X == 0;
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
            _dropdown?.Dispose();
            _toolTip.Dispose();
            _opacityAnimTimer.Dispose();
            _font.Dispose();
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
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

        // Rename is only reachable via the title text itself (double-click or right-click, see
        // WM_NCLBUTTONDBLCLK/WM_NCRBUTTONDOWN - both gated on IsPointOverTitleText) - no fallback
        // here when FenceModel.HideTitle leaves no title bar to click at all; renaming just isn't
        // reachable that way then, rather than an empty double-click anywhere substituting for it.
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

        if (ShowsSettingsButton && GetSettingsButtonRect(contentSize.Width).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }

        if (ShowsSettingsButton && GetNewFenceButtonRect(contentSize.Width).Contains(contentPoint))
        {
            _newFenceButtonArmed = true;
            return;
        }

        if (ShowsSettingsButton && GetDeleteButtonRect(contentSize.Width).Contains(contentPoint))
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
    /// meaningful while they're actually visible (ShowsSettingsButton), and only re-issued on an
    /// actual change of which button (if any) is hovered, rather than on every mouse-move, so
    /// ToolTip.Show isn't re-triggered (and re-timed/re-flickered) for every pixel of movement while
    /// already hovering the same one.</summary>
    private void UpdateButtonTooltips(Point windowLocation)
    {
        var contentSize = GetContentSize();
        var contentPoint = ToContent(windowLocation);

        string? text = null;
        Rectangle buttonRect = default;
        if (ShowsSettingsButton)
        {
            if (GetNewFenceButtonRect(contentSize.Width) is var newFenceRect && newFenceRect.Contains(contentPoint))
            {
                text = "Copy Fence";
                buttonRect = newFenceRect;
            }
            else if (GetDeleteButtonRect(contentSize.Width) is var deleteRect && deleteRect.Contains(contentPoint))
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
    /// tooltip's white/light default. SafeChromeBlend, not TintAmount - same fixed-WhiteSmoke-text
    /// reasoning as ChromeFill.</summary>
    private void DrawTooltip(object? sender, DrawToolTipEventArgs e)
    {
        using (var background = new SolidBrush(Tint(Color.Black, CurrentTint, SafeChromeBlend)))
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
                return "Move to Recycle Bin →";
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
                ? "Move to Recycle Bin →"
                : $"Move to {targetForm.FenceName} →";
        }

        return "Remove from Fence";
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsSettingsButton && GetSettingsButtonRect(GetContentSize().Width).Contains(ToContent(e.Location)))
                ShowFenceOptionsMenu();
            return;
        }

        if (_newFenceButtonArmed)
        {
            _newFenceButtonArmed = false;
            if (ShowsSettingsButton && GetNewFenceButtonRect(GetContentSize().Width).Contains(ToContent(e.Location)))
                _manager.CreateFenceLike(FenceId);
            return;
        }

        if (_deleteButtonArmed)
        {
            _deleteButtonArmed = false;
            if (ShowsSettingsButton && GetDeleteButtonRect(GetContentSize().Width).Contains(ToContent(e.Location)))
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

    /// <summary>Tracks whether the cursor is over this fence's client area, for "Full Opacity On
    /// Hover" (see IsHovered/TargetOpacity) - not the same as _hoverIndex (which icon, if any, is
    /// hovered) or ShowsSettingsButton's _isActive. Client-area only; the margin/resize band is
    /// covered separately by _isNonClientHovered (see WM_NCMOUSEMOVE/WM_NCMOUSELEAVE in WndProc).</summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isClientHovered = true;
        BeginOpacityAnimationIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isClientHovered = false;
        BeginOpacityAnimationIfNeeded();
        SetHoverIndex(-1);
        if (_visibleButtonTooltip is not null)
        {
            _visibleButtonTooltip = null;
            _toolTip.Hide(this);
        }
    }

    /// <summary>Starts (if not already running) the tick loop that eases _displayOpacity toward
    /// TargetOpacity - a no-op if they already match (Full Opacity When Active off, or already at the
    /// target) so this can be called unconditionally from every hover/drag/settings-open state change
    /// without checking FullOpacityOnHover itself first.</summary>
    private void BeginOpacityAnimationIfNeeded()
    {
        if (!_opacityAnimTimer.Enabled && Math.Abs(_displayOpacity - TargetOpacity) > 0.001f)
            _opacityAnimTimer.Start();
    }

    private void StepOpacityAnimation()
    {
        var target = TargetOpacity;
        var delta = target - _displayOpacity;
        if (Math.Abs(delta) <= OpacityAnimStep)
        {
            _displayOpacity = target;
            _opacityAnimTimer.Stop();
        }
        else
        {
            _displayOpacity += Math.Sign(delta) * OpacityAnimStep;
        }
        RenderAndPresent();
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

            // Sent repeatedly by the OS's own interactive move/resize loop (already running by the
            // time either of these arrive - see WM_ENTERSIZEMOVE/HitTest); DefWindowProc has no
            // default handling for either message, so unlike messages that need base.WndProc's
            // processing afterward, mutating the RECT at lParam and returning here is enough - the
            // outer loop (not DefWindowProc) is what reads it back. Deliberately NOT using that RECT
            // as the basis for where the fence should propose to go, though (see
            // _leftDragStartScreenPoint's own comment on why - it tracks incrementally off whatever
            // this code last wrote back, not absolutely off the cursor) - body is instead computed
            // fresh from the fixed drag-start anchor every single tick, and only ever re-inflated
            // back into a raw window RECT (OuterMargin/TopBand padding added back on) once, right at
            // the end, for the write-back.
            case NativeMethods.WM_MOVING:
            {
                var currentScreenPoint = Cursor.Position;
                var body = new Rectangle(
                    _model.Bounds.X + (currentScreenPoint.X - _leftDragStartScreenPoint.X),
                    _model.Bounds.Y + (currentScreenPoint.Y - _leftDragStartScreenPoint.Y),
                    _model.Bounds.Width, _model.Bounds.Height);
                // Both candidate sources by default - this fence's own custom lines (see SnapMove's
                // default includeCustomLines: true) and every other fence's edges. Holding the right
                // button down at the same time hides the fence-edge candidates for as long as it's
                // held, leaving just the custom lines - checked live via Control.MouseButtons (a
                // physical-state poll, not tied to any message actually having been dispatched for
                // that button's own down-press, which DefWindowProc's own modal SC_MOVE loop may
                // never route to this WndProc at all while it's running) rather than any button-down
                // message. Used to be the reverse (fence edges only opted into by holding right, off
                // by default) from when right-click was its own separate way to drag the fence, back
                // when merging both by default made dragging feel "sticky" - now that that stickiness
                // turned out to actually be drift from the OS's own incrementally-proposed rect (see
                // _leftDragStartScreenPoint's comment) rather than the candidate set itself, merging
                // both by default is fine again, and right-click is back to being a plain hide-the-
                // fence-lines modifier instead of a whole separate drag mechanism.
                IReadOnlyList<int> vCandidates = Array.Empty<int>();
                IReadOnlyList<int> hCandidates = Array.Empty<int>();
                if ((MouseButtons & MouseButtons.Right) == 0)
                    (vCandidates, hCandidates) = _manager.GetOtherFenceEdges(FenceId);
                var result = _manager.SnapLines.SnapMove(body, vCandidates, hCandidates, _model.Margin);
                // Re-decided against the proposed rect's own new position - a drag that crosses the
                // "would go off the top of the screen" threshold mid-tick flips right here, so
                // WriteBackWindowRect (next) already inflates using whichever side the button row
                // belongs on now, not wherever it was a moment ago.
                _buttonRowAtBottom = ComputeButtonRowAtBottom(result.Rect.Location);
                WriteBackWindowRect(m.LParam, result.Rect);
                m.Result = (IntPtr)1;
                return;
            }

            case NativeMethods.WM_SIZING:
            {
                // Same fixed-anchor reasoning as WM_MOVING above, just per-edge: whichever edges
                // this particular resize handle doesn't control stay pinned exactly where the drag
                // started (_model.Bounds, unchanging for the whole drag), and only the active ones
                // move by the cursor's total delta since then.
                var edges = SnapEdgesFromWmSz((int)m.WParam.ToInt64());
                var currentScreenPoint = Cursor.Position;
                var dx = currentScreenPoint.X - _leftDragStartScreenPoint.X;
                var dy = currentScreenPoint.Y - _leftDragStartScreenPoint.Y;
                var start = _model.Bounds;
                var body = Rectangle.FromLTRB(
                    (edges & SnapEdges.Left) != 0 ? start.Left + dx : start.Left,
                    (edges & SnapEdges.Top) != 0 ? start.Top + dy : start.Top,
                    (edges & SnapEdges.Right) != 0 ? start.Right + dx : start.Right,
                    (edges & SnapEdges.Bottom) != 0 ? start.Bottom + dy : start.Bottom);
                var (vCandidates, hCandidates) = _manager.GetOtherFenceEdges(FenceId);
                var result = _manager.SnapLines.SnapResize(body, edges, vCandidates, hCandidates, _model.Margin);
                _buttonRowAtBottom = ComputeButtonRowAtBottom(result.Rect.Location);
                WriteBackWindowRect(m.LParam, result.Rect);
                m.Result = (IntPtr)1;
                return;
            }

            case WM_NCLBUTTONDBLCLK:
                // HitTest reports HTCAPTION for the whole title bar/margin area, not just the
                // rendered title text - but renaming should only trigger for a double-click on the
                // text itself (see IsPointOverTitleText), same scoping WM_NCRBUTTONDOWN already
                // applies for the right-click case below. Anywhere else in this non-client area, do
                // nothing rather than letting the default proc maximize the window (its usual caption
                // double-click behavior).
                ActivateFence();
                if (IsPointOverTitleText(m.LParam))
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
                // A real caption's right-click would show the system menu (Restore/Move/Close etc.)
                // via the default proc - there's no such menu for this custom-drawn title bar, so
                // this always swallows the message itself (return, never falling through to
                // base.WndProc/DefWindowProc - see the note on that in WM_RBUTTONUP below, this is
                // also what stopped a stray mouse-capture DefWindowProc used to leave dangling here).
                // Right-clicking the caption/margin area (including a resize edge/corner, which only
                // occurs while inactive - see HitTest) just activates the fence, same as a resize
                // edge always has - it doesn't drag anything itself. Holding right WHILE separately
                // left-click-dragging (see MouseButtons.Right checks in WM_ENTERSIZEMOVE/WM_MOVING)
                // is what controls snapping now - it hides the fence-edge snap lines for that drag,
                // leaving only this fence's own custom lines active - rather than right-click being
                // its own separate way to move the fence, which is what this used to do.
                var ncRButtonHitTest = (int)m.WParam.ToInt64();
                ActivateFence();
                if (ncRButtonHitTest == HTCAPTION && IsPointOverTitleText(m.LParam))
                    ShowHeaderContextMenu();
                return;

            case NativeMethods.WM_NCMOUSEMOVE:
                // WinForms' own client-area hover tracking (OnMouseEnter/OnMouseLeave) doesn't cover
                // this - the margin/resize band reports HTLEFT/HTCAPTION/etc. (see HitTest), so the
                // OS treats it as non-client and never raises the client mouse events those hook.
                // TrackMouseEvent needs re-arming on every WM_NCMOUSEMOVE (Windows disarms it after
                // firing once), not just the first - but only bother once per hover session since
                // _isNonClientHovered already being true means it's still armed from last time.
                if (!_isNonClientHovered)
                {
                    _isNonClientHovered = true;
                    BeginOpacityAnimationIfNeeded();
                }
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = NativeMethods.TME_LEAVE | NativeMethods.TME_NONCLIENT,
                    hwndTrack = Handle,
                };
                NativeMethods.TrackMouseEvent(ref tme);
                break;

            case NativeMethods.WM_NCMOUSELEAVE:
                _isNonClientHovered = false;
                BeginOpacityAnimationIfNeeded();
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
                // WM_NCRBUTTONDOWN always swallows itself (see that case above) rather than falling
                // through to base.WndProc/DefWindowProc, so DefWindowProc's own default handling -
                // which used to answer a resize-hit-test right-click by capturing the mouse, normally
                // released again once DefWindowProc saw the matching button-up - never runs anymore
                // either, and can't leave that capture dangling the way it used to (see the fix for
                // that). Still releasing here defensively (a harmless no-op if nothing is actually
                // captured) rather than relying on that root cause staying fixed.
                Capture = false;
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
                RepositionDropdown();
                break;

            case WM_ENTERSIZEMOVE:
                _isMoving = true;
                // See _leftDragStartScreenPoint's own comment - WM_MOVING/WM_SIZING both measure
                // against this fixed point (and _model.Bounds, equally fixed for the whole drag)
                // instead of trusting the OS's own incrementally-drifting proposed rect.
                _leftDragStartScreenPoint = Cursor.Position;
                // Fires for both a move and a resize (see _resizeInProgress's own comment) - a
                // resize always shows both custom lines and fence edges (WM_SIZING has no
                // right-click modifier, unlike WM_MOVING - see its own comment), and a move shows
                // both too unless right is already held right at the start of the drag (the common
                // case is checked live every tick in WM_MOVING instead; this is only for the very
                // first frame, before any movement has happened yet, so the guides don't lag one
                // tick behind).
                if (_resizeInProgress || (MouseButtons & MouseButtons.Right) == 0)
                {
                    var (vGuides, hGuides) = _manager.GetOtherFenceEdges(FenceId);
                    var monitor = Screen.FromRectangle(_model.Bounds).Bounds;
                    _manager.SnapLines.BeginDrag(includeCustomLines: true, vGuides, hGuides, monitor);
                }
                else
                {
                    _manager.SnapLines.BeginDrag();
                }
                BeginOpacityAnimationIfNeeded();
                break;

            case WM_EXITSIZEMOVE:
                _manager.SnapLines.EndDrag();

                if (NativeMethods.GetWindowRect(Handle, out var rect))
                    _manager.NotifyBoundsChanged(FenceId, Rectangle.FromLTRB(
                        rect.Left + OuterMargin, rect.Top + TopBand, rect.Right - OuterMargin, rect.Bottom - BottomBand));

                // OCD Fence Sizing: snap to the tightest fit right after a manual resize, on top of
                // whatever size was just dragged to - not after a move, see _resizeInProgress. Done
                // before the HWND_BOTTOM restack below (rather than after) so that restack is always
                // the last z-order-relevant call in this handler - FormatDimensions makes its own
                // SetWindowPos call (SWP_NOZORDER, meant to leave z-order untouched), but a resize
                // followed by a move was still landing behind other fences with the restack first,
                // so the z-order push now unconditionally comes last regardless of what ran before it.
                if (_resizeInProgress && _model.OcdFenceSizing)
                    FormatDimensions(adjustWidth: true, adjustHeight: true);
                _resizeInProgress = false;

                // Dragging a fence via its caption (see HTCAPTION/WM_NCLBUTTONDOWN) goes through the
                // OS's own window-move loop, which activates it like any normal window drag would -
                // left alone, it'd then stay stacked on top of whatever window it was just dragged
                // over, contradicting the whole point of a fence (a desktop-level widget that never
                // covers what you're actually working in). Dropping it to the bottom of the z-order
                // here restores that even though it was just OS-activated; SWP_NOACTIVATE keeps this
                // restack itself from stealing focus back.
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

                _isMoving = false;
                BeginOpacityAnimationIfNeeded();

                // A pure move (no resize) never otherwise triggers a re-render - WM_SIZE already
                // covers the resize case - but GetSettingsButtonRect now depends on the fence's
                // absolute screen position (see ShouldSettingsButtonOpenLeft), so dragging a fence
                // across the point where the button should flip corners left the old render on
                // screen with the button drawn on its old side, while hit-testing (recomputed fresh
                // on the next click) already expected the new side - a click on the visibly-drawn
                // button landed nowhere. Re-rendering here keeps what's drawn and what's hit-tested
                // in sync again once the move settles.
                if (ShowsSettingsButton)
                    RenderAndPresent();
                break;

            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                Reanchor();
                break;
        }
    }

    private static bool IsResizeHitTest(int hitTest) =>
        hitTest is HTLEFT or HTRIGHT or HTTOP or HTBOTTOM or HTTOPLEFT or HTTOPRIGHT or HTBOTTOMLEFT or HTBOTTOMRIGHT;

    /// <summary>WM_SIZING's wParam - a flat enumeration, not a bitfield (the four corner values
    /// don't decompose into their two edges by combining the single-edge values).</summary>
    private static SnapEdges SnapEdgesFromWmSz(int wmsz) => wmsz switch
    {
        NativeMethods.WMSZ_LEFT => SnapEdges.Left,
        NativeMethods.WMSZ_RIGHT => SnapEdges.Right,
        NativeMethods.WMSZ_TOP => SnapEdges.Top,
        NativeMethods.WMSZ_TOPLEFT => SnapEdges.Top | SnapEdges.Left,
        NativeMethods.WMSZ_TOPRIGHT => SnapEdges.Top | SnapEdges.Right,
        NativeMethods.WMSZ_BOTTOM => SnapEdges.Bottom,
        NativeMethods.WMSZ_BOTTOMLEFT => SnapEdges.Bottom | SnapEdges.Left,
        NativeMethods.WMSZ_BOTTOMRIGHT => SnapEdges.Bottom | SnapEdges.Right,
        _ => SnapEdges.None,
    };

    /// <summary>Re-inflates a snapped visible-body rect back into raw window coordinates (the
    /// inverse of WM_MOVING/WM_SIZING's own body conversion above) and writes it into the RECT at
    /// lParam for the OS's own move/resize loop to pick up.</summary>
    private void WriteBackWindowRect(IntPtr lParam, Rectangle body)
    {
        var snapped = new RECT
        {
            Left = body.Left - OuterMargin,
            Top = body.Top - TopBand,
            Right = body.Right + OuterMargin,
            Bottom = body.Bottom + BottomBand,
        };
        Marshal.StructureToPtr(snapped, lParam, false);
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

        // The settings button (and the "+"/"x" buttons beside it) live above the fence, in the taller
        // TopMargin band - check them first so none is shadowed by an HTTOP/HTTOPLEFT/HTTOPRIGHT
        // resize result.
        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(new Point(x, y));
        if (ShowsSettingsButton && (GetSettingsButtonRect(contentWidth).Contains(contentPoint)
            || GetNewFenceButtonRect(contentWidth).Contains(contentPoint)
            || GetDeleteButtonRect(contentWidth).Contains(contentPoint)))
            return HTCLIENT;

        int band = OuterMargin + ResizeMargin;
        // Whichever side currently holds the button row is the taller one (see TopBand/
        // _buttonRowAtBottom), so it's this pair - not always "top"/"bottom" - that gets the wider
        // resize-grab threshold instead of sharing the plain one above.
        int topZone = TopBand + ResizeMargin;
        int bottomZone = BottomBand + ResizeMargin;

        if (ShowsSettingsButton)
        {
            // ShowsSettingsButton, not just _isActive - opening the settings dropdown steals OS
            // activation from the fence (it's a separate top-level Form), which flips _isActive false
            // via OnDeactivate even though the button/active border deliberately stay showing (see
            // ShowsSettingsButton's own comment). Gating on _isActive alone let the resize hit-test
            // codes below fire while the dropdown was still open, so dragging an edge resized the
            // fence out from under its own still-open menu.
            //
            // The margin band is a move handle instead of a resize band while active - the same
            // footprint resize used to claim, just reassigned rather than split into two adjacent
            // rings, so the drag margin can hug the fence's actual edge (see RenderAndPresent's
            // ThemedActiveBorder highlight) without an ambiguous strip where both would apply.
            // Resizing an active fence isn't available until it's deactivated again.
            if (x <= band || x >= width - band || y <= topZone || y >= height - bottomZone)
                return HTCAPTION;
        }
        else
        {
            bool left = x <= band;
            bool right = x >= width - band;
            bool top = y <= topZone;
            bool bottom = y >= height - bottomZone;

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
        if (!_model.HideTitle && y - TopBand <= TitleBarHeight)
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
        int contentHeight = height - TopBand - BottomBand;
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

            // A brighter, thicker border signals the fence is active (or its settings dropdown is
            // still open, see ShowsSettingsButton) - the margin band around it is now a move handle
            // while genuinely _isActive (see HitTest), and this highlight hugs the fence's actual
            // edge directly rather than a separate frame floating out in the margin.
            using var borderPen = new Pen(ShowsSettingsButton ? ThemedActiveBorder : ThemedBorder, ShowsSettingsButton ? ActiveBorderWidth : 1f);
            // Pen.LineJoin defaults to Miter, which squares off the outer edge of a thick stroke at
            // the rounded corners instead of following their curve - Round keeps it hugging the arc.
            borderPen.LineJoin = LineJoin.Round;
            g.DrawPath(borderPen, body);

            if (!_model.HideTitle && _renameBox is null)
            {
                TextRenderer.DrawText(g, _model.Name, _font, ToWindow(new Rectangle(14, 0, contentWidth - 22, TitleBarHeight)),
                    Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            if (ShowsSettingsButton)
            {
                // Filled first so the button reads as fully opaque - it lives in the near-transparent
                // TopMargin band (see MarginFillColor's own comment), and TextRenderer.DrawText below
                // only ever writes RGB, never alpha, so without an opaque backing shape under it the
                // label would inherit the margin's near-zero alpha and vanish once
                // WritePremultipliedPixels scales it down.
                var buttonRect = ToWindow(GetSettingsButtonRect(contentWidth));
                using var buttonPath = RoundedRect(buttonRect, 6);
                // ChromeFill, not Accent - same fixed-WhiteSmoke-text-needs-to-stay-readable reasoning
                // as the dropdown's own background (see ChromeFill's doc comment).
                using var buttonFill = new SolidBrush(ChromeFill);
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

                // Same opaque-backing reasoning as the Settings button above - filled before the copy
                // glyph is stroked on top.
                var newFenceRect = ToWindow(GetNewFenceButtonRect(contentWidth));
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
                var deleteRect = ToWindow(GetDeleteButtonRect(contentWidth));
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

        LayeredWindowPresenter.Present(Handle, buffer, new Point(windowRect.Left, windowRect.Top), EffectiveOpacity);
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

    /// <summary>Right-click on an item's label text specifically (see FileAtLabelPosition) - not
    /// its icon, not empty grid space. Fence-level actions live elsewhere now: Rename only on the
    /// header (see ShowHeaderContextMenu) and Delete Fence only as the "x" button next to Settings
    /// (see GetDeleteButtonRect/ConfirmDelete) - a right-click anywhere else has nothing of its own to
    /// offer, so it just activates the fence (see ActivateFence) without popping up a menu. Open and
    /// Remove From Fence used to live here too; both stayed reachable another way (double-click, drag
    /// off the fence) so removing them from this menu didn't remove the functionality, just this
    /// shortcut to it.</summary>
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
    /// active (see OnDeactivate and the settings-button hit-test carve-out). A DropdownMenu (see its
    /// own class comment for why this isn't a native popup menu) - "Fence Color" is inlined directly
    /// as a flat group (its own header already says what it is, without needing an outer flyout-
    /// anchor row to name it too), while "Fence Dimensions" is nested behind an "OCD" flyout (see
    /// DropdownMenu.Row.Submenu) instead. "Delete Fence" isn't a row here - it's the "x" button next
    /// to Settings (see GetDeleteButtonRect/ConfirmDelete), same as "Rename" lives in the header's own
    /// context menu rather than here - see ShowContextMenu/ShowHeaderContextMenu.</summary>
    private void ShowFenceOptionsMenu()
    {
        _dropdown?.Dispose();

        var contentSize = GetContentSize();
        var buttonRect = GetSettingsButtonRect(contentSize.Width);
        var buttonScreenRect = new Rectangle(PointToScreen(ToWindow(buttonRect.Location)), buttonRect.Size);
        // GetSettingsButtonRect already picked whichever corner has room for the menu (see
        // ShouldSettingsButtonOpenLeft) - reuse that same decision here instead of re-deriving it, so
        // the button and the menu it opens always agree on which side they're on.
        var preferLeft = buttonRect.X == 0;

        var rows = BuildOptionsMenuRows();

        // Tooltip background blends from black rather than DefaultBodyColor (unlike every other
        // ThemedXxx color) - black for an untinted fence, and leaning more visibly toward a tinted
        // fence's own color at the same blend amount than starting from dark gray would, since
        // there's more contrast for Tint() to work with between black and a bright pick.
        // SafeChromeBlend, not TintAmount - same fixed-WhiteSmoke-text reasoning as ChromeFill.
        _dropdown = new DropdownMenu(rows, buttonScreenRect, preferLeft, _font, () => ChromeFill, () => ThemedMenuSelected, () => Accent, () => ThemedCheckboxBorder,
            () => Tint(Color.Black, CurrentTint, SafeChromeBlend));
        _dropdown.ItemClicked += id =>
        {
            HandleCommand(id);
            _dropdown?.RefreshChecks();
        };
        _dropdown.FormClosed += (_, _) =>
        {
            _dropdown = null;
            // ShowsSettingsButton depends on _dropdown - now that it's gone, the button/active
            // border need to actually disappear if _isActive had already gone false while it was
            // still open, instead of staying stuck looking active until some other render trigger.
            // RenderAndPresent already no-ops via _disposing if the fence itself is going away too.
            RenderAndPresent();
            // TargetOpacity depends on _dropdown being non-null - now that it's closing, ease back
            // down off Full Opacity if nothing else (hover, a drag) is still keeping it up.
            BeginOpacityAnimationIfNeeded();
        };
        _dropdown.Show(this);
        // TargetOpacity depends on _dropdown being non-null - opening it may need to start easing
        // toward Full Opacity right away, not wait for some unrelated repaint to notice.
        BeginOpacityAnimationIfNeeded();
    }

    /// <summary>Keeps an already-open settings dropdown anchored to its button after the fence's own
    /// window moves or resizes out from under it (see WM_SIZE) - most obviously the OCD flyout's own
    /// resize commands (FormatDimensions), which change this fence's bounds via SetWindowPos without
    /// otherwise touching the dropdown at all. Without this the menu was left floating wherever the
    /// button used to be instead of following it.</summary>
    private void RepositionDropdown()
    {
        if (_dropdown is null)
            return;
        var contentSize = GetContentSize();
        var buttonRect = GetSettingsButtonRect(contentSize.Width);
        var buttonScreenRect = new Rectangle(PointToScreen(ToWindow(buttonRect.Location)), buttonRect.Size);
        _dropdown.RepositionRelativeTo(buttonScreenRect, preferLeft: buttonRect.X == 0);
    }

    /// <summary>The settings dropdown's row list, factored out of ShowFenceOptionsMenu so
    /// ShouldSettingsButtonOpenLeft can measure it (via DropdownMenu.Measure) to decide which corner
    /// the button itself belongs in before the menu exists to measure.</summary>
    private List<DropdownMenu.Row> BuildOptionsMenuRows()
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
            new(TagColorHeader, "Fence Color", IsHeader: true),
            new(CmdColorDefault, string.Empty, IsGridItem: true, Swatch: DefaultBodyColor,
                IsChecked: () => _model.TintColor is null, Tooltip: "Black"),
        };
        for (var i = 0; i < ColorPresets.Length; i++)
        {
            var presetArgb = ColorPresets[i].ToArgb();
            rows.Add(new DropdownMenu.Row(CmdColorPresetBase + i, string.Empty, IsGridItem: true, Swatch: ColorPresets[i],
                IsChecked: () => _model.TintColor == presetArgb, Tooltip: GetColorPresetName(i)));
        }
        // Swatch left null - an empty (outline-only) circle, distinct from every real color, rather
        // than a text row - see DropdownMenu.DrawGridItem.
        rows.Add(new DropdownMenu.Row(CmdColorCustom, string.Empty, IsGridItem: true,
            Glyph: DropdownMenu.GridGlyph.Plus, Tooltip: "Custom..."));
        rows.Add(new DropdownMenu.Row(CmdColorEyedrop, string.Empty, IsGridItem: true,
            Glyph: DropdownMenu.GridGlyph.Eyedropper, Tooltip: "Eyedropper"));
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

    private readonly record struct MenuRowStyle(string Text, bool HasCheckbox, bool IsHeader, Color? Swatch = null);

    /// <summary>Every owner-draw row's label, keyed by the item id carried in its itemData (see
    /// AppendItem) - only "Rename" for the two single-item native menus this still serves
    /// (ShowContextMenu/ShowHeaderContextMenu) now that ShowFenceOptionsMenu's own dropdown draws
    /// itself directly from its Row list instead of going through this lookup.</summary>
    private static MenuRowStyle GetMenuRowStyle(int tag) => tag switch
    {
        CmdRenameItem => new MenuRowStyle("Rename", false, false),
        CmdRename => new MenuRowStyle("Rename", false, false),
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
    /// (e.g. a pure ColorDialog pick), since only part of it makes it into the final fill. amount has
    /// no default on purpose - every call site below deliberately picks either TintAmount (the fence's
    /// own adjustable look) or SafeChromeBlend (menu/tooltip/button chrome, pinned regardless of
    /// TintStrength so fixed WhiteSmoke text/icons on top always stay readable).</summary>
    private static Color Tint(Color baseColor, Color? tint, double amount) =>
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

    /// <summary>WM_MEASUREITEM handler for the two remaining native single-item menus (Rename, see
    /// ShowContextMenu/ShowHeaderContextMenu) - ShowFenceOptionsMenu's own dropdown measures its rows
    /// directly (see DropdownMenu.MeasureLayout) instead of going through this.</summary>
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

    /// <summary>WM_DRAWITEM handler for the two remaining native single-item menus (see
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

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdRename: BeginRename(); break;
            case CmdRenameItem: BeginRenameItem(_contextItem); break;
            case CmdToggleHideLabels: ToggleHideLabels(); break;
            case CmdToggleHideTitle: ToggleHideTitle(); break;
            case CmdResizeBoth: FormatDimensions(adjustWidth: true, adjustHeight: true); break;
            case CmdResizeLeftRight: FormatDimensions(adjustWidth: true, adjustHeight: false); break;
            case CmdResizeTopDown: FormatDimensions(adjustWidth: false, adjustHeight: true); break;
            case CmdToggleOcdSizing: ToggleOcdFenceSizing(); break;
            case CmdColorDefault: SetTintColor(null); break;
            case CmdColorCustom: PickCustomColor(); break;
            case CmdColorEyedrop: PickEyedropperColor(); break;
            case CmdToggleFullOpacityOnHover: ToggleFullOpacityOnHover(); break;
            case >= CmdColorPresetBase and < CmdColorPresetBase + 100:
                var presetColor = GetColorPreset(id - CmdColorPresetBase);
                if (presetColor != Color.Empty)
                    SetTintColor(presetColor);
                break;
        }
    }

    /// <summary>exact is only ever true from PickEyedropperColor - see FenceModel.TintIsExact. A
    /// non-exact pick also resets Opacity back to its default as a side effect (see
    /// FenceManager.SetTintColor) - _displayOpacity needs to snap to match immediately, the same
    /// reasoning as SetOpacity's own snap, or the fence would keep rendering at whatever opacity it
    /// was at right before this pick until something else (hover, the dropdown closing) happened to
    /// notice the mismatch.</summary>
    private void SetTintColor(Color? color, bool exact = false)
    {
        _manager.SetTintColor(FenceId, color, exact);
        _opacityAnimTimer.Stop();
        _displayOpacity = TargetOpacity;
        RenderAndPresent();
    }

    /// <summary>"Header Darkness" slider - called directly from DropdownMenu.Row.OnSliderChange
    /// (not routed through HandleCommand/ItemClicked the way every other row is, since a slider needs
    /// a live value rather than a single command id) on mouse-down and on every subsequent mouse-move
    /// while dragging, so the header repaints continuously as it's dragged rather than only once on
    /// release.</summary>
    private void SetHeaderDarkness(int darkness)
    {
        _manager.SetHeaderDarkness(FenceId, darkness);
        RenderAndPresent();
    }

    /// <summary>"Fence Opacity" slider - same live-drag pattern as SetHeaderDarkness above.
    /// FenceManager.SetOpacity enforces a safe minimum, so a value dragged below it snaps back on the
    /// next repaint rather than the fence actually going invisible. Snaps _displayOpacity straight to
    /// the new TargetOpacity instead of animating (see its own field comment) - a slider drag needs
    /// to track the cursor immediately, an animated lag here would feel unresponsive.</summary>
    private void SetOpacity(int opacity)
    {
        _manager.SetOpacity(FenceId, opacity);
        _opacityAnimTimer.Stop();
        _displayOpacity = TargetOpacity;
        RenderAndPresent();
    }

    /// <summary>"Tint Strength" slider - same live-drag pattern as SetHeaderDarkness/SetOpacity above.
    /// Affects both a preset/Custom... pick (TintAmount) and an Eyedropper's exact pick
    /// (DilutedExactTint), just in opposite directions - see either one's own doc comment.</summary>
    private void SetTintStrength(int strength)
    {
        _manager.SetTintStrength(FenceId, strength);
        RenderAndPresent();
    }

    /// <summary>"Fence Margin" numeric input. Doesn't need a repaint of its own (unlike the sliders
    /// above, nothing this fence draws depends on its own Margin value - it only affects candidates
    /// offered to OTHER fences' drags via FenceManager.GetOtherFenceEdges) but RenderAndPresent
    /// stays for consistency and to keep anything else the dropdown reflects in sync.</summary>
    private void SetMargin(int margin)
    {
        _manager.SetMargin(FenceId, margin);
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

    /// <summary>"Fence Color > Eyedropper" - shows a full-virtual-screen overlay (see
    /// EyedropperOverlay) that lets the user click any pixel anywhere on screen, even outside this
    /// app, to sample its color as this fence's new tint. Not modal like PickCustomColor's
    /// ColorDialog, but showing it still steals activation from the settings dropdown the same way,
    /// which closes it via DropdownMenu.OnDeactivate same as a modal dialog would. Also resets Opacity
    /// to 100 and TintStrength to 0 (see EffectiveOpacity/DilutedExactTint) so the pick starts out
    /// pixel-exact - neither is forced to stay there, just where a fresh pick starts; both sliders can
    /// move it from there same as any other fence, trading exactness away deliberately rather than
    /// never having the choice.</summary>
    private void PickEyedropperColor()
    {
        var overlay = new EyedropperOverlay();
        overlay.ColorPicked += color =>
        {
            SetTintColor(color, exact: true);
            SetOpacity(100);
            SetTintStrength(0);
        };
        overlay.FormClosed += (_, _) => overlay.Dispose();
        overlay.Show();
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
        // Changes EffectiveCellHeight (see its own comment), which OCD Fence Sizing's fit is based
        // on - only height can possibly need to change here, never the columns/width.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: false, adjustHeight: true);
        RenderAndPresent();
    }

    private void ToggleHideTitle()
    {
        _manager.SetHideTitle(FenceId, !_model.HideTitle);
        // Changes GridTop (see its own comment), which OCD Fence Sizing's fit is based on - only
        // height can possibly need to change here, never the columns/width.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: false, adjustHeight: true);
        RenderAndPresent();
    }

    private void ToggleOcdFenceSizing()
    {
        _manager.SetOcdFenceSizing(FenceId, !_model.OcdFenceSizing);
        // Otherwise this only ever takes effect after the next manual resize (see WM_EXITSIZEMOVE) -
        // turning it on should tidy up the fence right away instead of waiting for that.
        if (_model.OcdFenceSizing)
            FormatDimensions(adjustWidth: true, adjustHeight: true);
        RenderAndPresent();
    }

    private void ToggleFullOpacityOnHover()
    {
        _manager.SetFullOpacityOnHover(FenceId, !_model.FullOpacityOnHover);
        _opacityAnimTimer.Stop();
        _displayOpacity = TargetOpacity;
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
            newBounds.Width + OuterMargin * 2, newBounds.Height + TopBand + BottomBand,
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
