using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Snapping;
using DesktopTool.Native;

namespace DesktopTool.UI;

/// <summary>
/// Base class for a hand-painted, layered Win32 window (WS_POPUP + WS_EX_LAYERED, no WinForms child
/// controls) that behaves like a Fence: draggable and resizable via the OS's own interactive move/
/// resize loop, snapping against other fences and custom snap lines by default, styled from a live
/// tint/opacity, with a rename-able title row and a Settings button living in a band that flips
/// between the top and bottom depending on how close to a monitor's own top edge the window is
/// sitting. Every one of those - move, resize, snap, rename, the Settings button and its dropdown -
/// works out of the box with no subclass code beyond the small hooks below (what the title text is,
/// what rows the Settings dropdown shows, what a handful of theme colors are); a subclass only needs
/// its own WndProc/geometry for whatever makes it *not* just chrome (a Fence's icon grid, its own
/// resize-vs-move activation rules) - see FenceForm for the fullest example.
/// </summary>
internal abstract class LayeredWidgetForm : Form
{
    protected const int WM_NCHITTEST = 0x0084;
    protected const int WM_NCLBUTTONDBLCLK = 0x00A3;
    protected const int WM_SIZE = 0x0005;
    protected const int WM_RBUTTONUP = 0x0205;
    protected const int WM_ENTERSIZEMOVE = 0x0231;
    protected const int WM_EXITSIZEMOVE = 0x0232;

    protected const int HTCLIENT = 1;
    protected const int HTCAPTION = 2;
    protected const int HTLEFT = 10;
    protected const int HTRIGHT = 11;
    protected const int HTTOP = 12;
    protected const int HTTOPLEFT = 13;
    protected const int HTTOPRIGHT = 14;
    protected const int HTBOTTOM = 15;
    protected const int HTBOTTOMLEFT = 16;
    protected const int HTBOTTOMRIGHT = 17;
    // Non-client, but not a caption - DefWindowProc's own default WM_NCLBUTTONDOWN handling starts a
    // move only for HTCAPTION specifically, so a subclass returning this instead for its title row
    // keeps it non-client for everything else that depends on that (right-click/double-click routing,
    // hover tracking - see this class's own WndProc, none of which require HTCAPTION specifically)
    // while no longer letting a left-button drag from there move the window.
    protected const int HTBORDER = 18;

    // Deliberately never tinted - exists purely so Windows doesn't treat the margin band as
    // click-through (alpha 0 pixels of a layered window are click-through; alpha 1 is the practical
    // minimum that still isn't). Painted first, under everything opaque a subclass draws on top.
    protected static readonly Color MarginFillColor = Color.FromArgb(1, 0, 0, 0);

    protected static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    // Only shows engagement chrome (a settings button, an active-state border) while actually
    // engaged - right-click anywhere, or a title-bar click - see WidgetActivation's own doc comment.
    protected readonly WidgetActivation Activation = new();

    // Eases the render opacity toward TargetOpacity over several ticks rather than jumping there in
    // one repaint - see OpacityAnimator's own doc comment for why a plain Form.Opacity can't do this
    // for a window that pushes its own bitmap via UpdateLayeredWindow. Named RenderOpacity, not just
    // Opacity, so it doesn't shadow Form's own same-named (and differently-typed) property.
    protected readonly OpacityAnimator RenderOpacity;

    // Every fence/snap-line-aware widget in this app shares the one FenceManager instance for its
    // snapping - see ComputeMovedBody/ComputeResizedBody/BeginSnapDrag below, all of which use this
    // directly so snapping works out of the box for any subclass, not just FenceForm.
    protected readonly FenceManager Fences;

    // Covers both an interactive move and an interactive resize - set between WM_ENTERSIZEMOVE and
    // WM_EXITSIZEMOVE.
    protected bool IsMoving { get; set; }

    // Whether the in-progress drag (see IsMoving) is specifically a resize rather than a move - set
    // from OnNcLButtonDown's own default (see its own comment), read back by BeginSnapDrag/a
    // subclass's own OnDragEnd to tell the two apart.
    protected bool IsResizing { get; set; }

    // Together back "Full Opacity When Active" (see IsHovered/TargetOpacity) - split into
    // client/non-client because they're detected two completely different ways: OnMouseEnter/
    // OnMouseLeave for the client half, WM_NCMOUSEMOVE/WM_NCMOUSELEAVE below for the margin band.
    private bool _isClientHovered;
    private bool _isNonClientHovered;
    protected bool IsHovered => _isClientHovered || _isNonClientHovered;

    // Fixed anchor a drag/resize measures against every tick, instead of trusting the OS's own
    // incrementally-proposed rect (drift/stickiness otherwise) - captured once, from GetCurrentBody,
    // right as WM_ENTERSIZEMOVE fires.
    protected Point LeftDragStartScreenPoint { get; set; }
    protected Rectangle DragStartBody { get; private set; }

    // Guards RenderAndPresent against a reentrant repaint triggered mid-teardown - destroying the
    // native window as part of Dispose synchronously delivers WM_ACTIVATE while WndProc is still
    // hooked up, reaching OnDeactivate -> RenderAndPresent before Dispose(true) even returns.
    protected bool IsDisposing { get; private set; }

    // Backs the rename EditBox's WM_CTLCOLOREDIT background and any native owner-draw popup menu a
    // subclass builds - one shared themed native brush, recreated on demand (see GetThemeBrush)
    // rather than fixed for the window's whole lifetime, since the color it themes to can change at
    // runtime.
    private IntPtr _themeBrush = IntPtr.Zero;
    private Color _themeBrushColor;

    // True when the button row currently belongs on the bottom band instead of the top - see
    // ComputeButtonRowAtBottom. Kept in sync wherever a subclass's own position is computed/changed
    // (its own CreateParams, and every tick of a live drag) rather than read fresh on every use.
    protected bool ButtonRowAtBottom { get; set; }

    // Tracks the Settings button's own left/right flip (see ShouldSettingsButtonOpenLeft) through a
    // live move - unlike ButtonRowAtBottom above (whose flip changes the window's own outer bounds,
    // so becomes visible for free as the OS moves it), a left/right flip only changes where the
    // button paints *within* the content, so WM_MOVING needs to explicitly repaint whenever this
    // actually changes rather than relying on the OS to show it. Checked every tick, but only
    // triggers a repaint on an actual change - same restraint as everywhere else that avoids a full
    // repaint on every single mouse-move message. Only relevant during a move, not a resize - resize
    // can only start while inactive (see HitTest), and the Settings button isn't shown then anyway.
    private bool _draggedSettingsButtonOnLeft;

    // The rename EditBox and the title row's own right-click "Rename" menu - both base-owned now
    // (see BeginRename/ShowTitleContextMenu's own defaults) so a subclass gets renaming for free.
    private EditBox? _renameBox;
    private ContextMenuStrip? _titleContextMenu;
    protected bool IsRenaming => _renameBox is not null;

    // The currently-open Settings dropdown, or null - see OpenSettingsMenu.
    protected DropdownMenu? SettingsDropdown { get; private set; }

    protected LayeredWidgetForm(float initialOpacity, FenceManager fences)
    {
        Fences = fences;
        RenderOpacity = new OpacityAnimator(initialOpacity, () => TargetOpacity, RenderAndPresent);
        Activation.Changed += RenderAndPresent;
    }

    /// <summary>Lazily (re)creates the shared native brush only when color has actually changed since
    /// the last call - both the rename EditBox's WM_CTLCOLOREDIT and native owner-draw menu theming
    /// can fire often enough (every redraw, every menu open) that recreating a GDI brush on every
    /// single call would be wasteful.</summary>
    protected IntPtr GetThemeBrush(Color color)
    {
        if (_themeBrush == IntPtr.Zero || _themeBrushColor != color)
        {
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
            _themeBrush = NativeMethods.CreateSolidBrush(ColorRef(color));
            _themeBrushColor = color;
        }
        return _themeBrush;
    }

    /// <summary>bodyScreenLocation is the widget's visible body's own top-left corner in screen
    /// coordinates. True when placing bandHeightAtTop above it would extend above its monitor's own
    /// working area - in that case the button row belongs on the bottom band instead, so the widget
    /// can still sit flush with the very top of the screen without its own buttons going unreachably
    /// off-screen.</summary>
    protected static bool ComputeButtonRowAtBottom(Point bodyScreenLocation, int bandHeightAtTop) =>
        bodyScreenLocation.Y - bandHeightAtTop < Screen.FromPoint(bodyScreenLocation).WorkingArea.Top;

    protected static Point ScreenLParamToWindowPoint(IntPtr lParam, RECT windowRect)
    {
        long l = lParam.ToInt64();
        short screenX = (short)(l & 0xFFFF);
        short screenY = (short)((l >> 16) & 0xFFFF);
        return new Point(screenX - windowRect.Left, screenY - windowRect.Top);
    }

    // The invisible drag/resize-grab band around the visible body (constant on every edge but one),
    // and the margin band on whichever of top/bottom currently holds the button row vs. doesn't - see
    // ButtonRowAtBottom. Left fully to each subclass: FenceForm's own split (TopBand collapsing to 0
    // once flipped, BottomBand flooring at OuterMargin rather than 0) is Fence-specific reasoning
    // about Fence's own margin band, not something to generalize from a single example.
    protected abstract int OuterMargin { get; }
    protected abstract int TopBand { get; }
    protected abstract int BottomBand { get; }

    /// <summary>The band height at the top edge when the button row is NOT flipped to the bottom -
    /// i.e. what TopBand equals when ButtonRowAtBottom is false. Needed as its own hook (distinct
    /// from TopBand, which already reflects whichever state is currently true) so WM_MOVING/
    /// WM_SIZING can decide whether a given tick's new position crosses the flip threshold, the same
    /// way ComputeButtonRowAtBottom always has.</summary>
    protected abstract int MaxTopBand { get; }

    protected Point ToContent(Point windowPoint) => new(windowPoint.X - OuterMargin, windowPoint.Y - TopBand);
    protected Point ToWindow(Point contentPoint) => new(contentPoint.X + OuterMargin, contentPoint.Y + TopBand);
    protected Rectangle ToWindow(Rectangle contentRect) =>
        new(contentRect.X + OuterMargin, contentRect.Y + TopBand, contentRect.Width, contentRect.Height);

    /// <summary>Window-relative (e.g. already run through ToWindow) to screen coordinates - needed
    /// for EditBox, which (unlike everything else drawn here) is a real top-level window rather than
    /// something painted into this window's own layered bitmap.</summary>
    protected Rectangle ToScreen(Rectangle windowRect) => new(PointToScreen(windowRect.Location), windowRect.Size);

    /// <summary>The visible body's own size - the actual (padded) window size minus OuterMargin on
    /// the left/right/non-button-row side and TopBand/BottomBand's button-row side - all content/
    /// hit-test math is in this "content" space.</summary>
    protected Size GetContentSize()
    {
        NativeMethods.GetClientRect(Handle, out var clientRect);
        return new Size(Math.Max(0, clientRect.Right - OuterMargin * 2), Math.Max(0, clientRect.Bottom - TopBand - BottomBand));
    }

    // ---- Style / theme ----
    //
    // Every widget on this base is styled from a live IWidgetStyle (tint color, header darkness,
    // opacity, full-opacity-on-hover, tint strength, snap margin) - a fence's own FenceModel and the
    // Layout Launcher's own LayoutLauncherModel both already implement it. Base owns the derivation
    // (ThemedBody/ThemedTitle/etc below) and the generic Settings-dropdown rows (Hide Title, Full
    // Opacity When Active, the shared color grid/sliders/margin stepper) that follow from it, so a
    // subclass with nothing further to add (Layout Launcher, chrome-only for now) gets a fully
    // working Settings menu for free; one with more to show (a Fence's own Hide Shortcut
    // Names/OCD Sizing) overrides BuildSettingsRows/HandleSettingsCommand entirely rather than
    // patching the default, but can still reuse the same shared Cmd* ids/mutator hooks below for the
    // rows it keeps.

    /// <summary>The subclass's own per-element styling knobs - a fence passes its FenceModel, the
    /// Layout Launcher its LayoutLauncherModel, both already implementing this.</summary>
    protected abstract IWidgetStyle Style { get; }

    /// <summary>Whether the title row's text is currently hidden - a plain settable flag most of a
    /// subclass's own persisted model already has (FenceModel.HideTitle, LayoutLauncherModel.HideTitle),
    /// not part of IWidgetStyle itself since it affects the title row rather than tint/opacity. The
    /// setter is responsible for persisting, the same way Title's own setter is.</summary>
    protected abstract bool HideTitle { get; set; }

    protected virtual bool TitleVisible => !HideTitle;

    // Fallback palette for an untinted element - virtual so a future subclass wanting a genuinely
    // different base look could override one, but both current subclasses share these exact values
    // (an intentional part of the styling-unification effort, not a coincidence).
    protected virtual Color DefaultBodyColor => Color.FromArgb(255, 32, 32, 36);
    protected virtual Color DefaultBorderColor => Color.FromArgb(255, 70, 70, 78);
    protected virtual Color DefaultAccentColor => Color.FromArgb(255, 190, 190, 195);
    protected virtual Color DefaultMenuSelectedColor => Color.FromArgb(255, 55, 55, 62);
    protected virtual Color DefaultCheckboxBorderColor => Color.FromArgb(255, 150, 150, 158);

    protected Color? CurrentTint => Style.TintColor is { } argb ? Color.FromArgb(argb) : null;

    /// <summary>Full-strength version of the element's own tint (falling back to DefaultAccentColor)
    /// for anything that needs to read clearly rather than just hint at the theme - the active-state
    /// border, the Settings button, and the Settings dropdown's own checkmarks/selection ring.</summary>
    protected Color Accent => CurrentTint ?? DefaultAccentColor;

    /// <summary>Style.TintStrength (0-100%) as the 0.0-1.0 fraction StyleTint.Tint's amount parameter
    /// needs.</summary>
    protected double TintAmount => Style.TintStrength / 100.0;

    /// <summary>Only meaningful when Style.TintIsExact - dilutes an Eyedropper pick back toward
    /// DefaultBodyColor by TintAmount, the *reverse* direction from the regular Tint(base, tint,
    /// amount) call (there, amount=0 means "ignore the pick"; here, amount=0 means "keep the pick
    /// exact").</summary>
    protected Color DilutedExactTint(Color exact) => StyleTint.Tint(exact, DefaultBodyColor, TintAmount);

    /// <summary>Blends a preset/Custom... pick into DefaultBodyColor at TintAmount same as always; an
    /// Eyedropper pick (TintIsExact) instead starts from the exact color and dilutes it toward
    /// DefaultBodyColor by that same TintAmount (see DilutedExactTint) - both directions read as "how
    /// much of the picked color survives" even though the blend runs opposite ways under the hood.</summary>
    protected Color ThemedBody => Style.TintIsExact && CurrentTint is { } exactBodyTint
        ? DilutedExactTint(exactBodyTint)
        : StyleTint.Tint(DefaultBodyColor, CurrentTint, TintAmount);

    /// <summary>Always StyleTint.SafeChromeBlend, even when Style.TintIsExact or Tint Strength is
    /// turned all the way up - unlike ThemedBody/Accent, anything drawing fixed WhiteSmoke text or
    /// glyphs on top of a fill (the Settings dropdown, its tooltips, the Settings button) needs to
    /// stay readable no matter how light/bright the element's own picked color is.</summary>
    protected Color ChromeFill => StyleTint.Tint(DefaultBodyColor, CurrentTint, StyleTint.SafeChromeBlend);

    private Color HeaderBaseColor => StyleTint.DarkenTowardBlack(DefaultBodyColor, Style.HeaderDarkness / 100.0);

    /// <summary>Tints HeaderBaseColor same as every other Themed* color, but with TintAmount's own
    /// strength shrinking as HeaderDarkness rises, reaching true black at 100% darkness regardless of
    /// tint - see FenceForm's original doc comment on this (now-shared) formula for the full
    /// reasoning. An exact Eyedropper pick darkens its own already-diluted ThemedBody color instead.</summary>
    protected Color ThemedTitle
    {
        get
        {
            var darkness = Style.HeaderDarkness / 100.0;
            if (Style.TintIsExact && CurrentTint is { } exactTitleTint)
                return StyleTint.DarkenTowardBlack(DilutedExactTint(exactTitleTint), darkness);
            return StyleTint.Tint(HeaderBaseColor, CurrentTint, TintAmount * (1 - darkness));
        }
    }

    protected Color ThemedBorder => StyleTint.Tint(DefaultBorderColor, CurrentTint, TintAmount);

    // SafeChromeBlend, not TintAmount - same fixed-WhiteSmoke-text-needs-to-stay-readable reasoning
    // as ChromeFill.
    protected Color ThemedMenuSelected => StyleTint.Tint(DefaultMenuSelectedColor, CurrentTint, StyleTint.SafeChromeBlend);
    protected Color ThemedCheckboxBorder => StyleTint.Tint(DefaultCheckboxBorderColor, CurrentTint, 0.4);

    // Translucent rather than opaque - a fully opaque accent border reads as too heavy/saturated
    // against a tinted body beneath it.
    protected Color ThemedActiveBorder => Color.FromArgb(220, Accent);

    /// <summary>Border width/visibility while the widget is engaged (see ShowsButtons) - a plain
    /// 1px ThemedBorder otherwise. Virtual so a subclass could tune it, though both current ones use
    /// the same value.</summary>
    protected virtual float ActiveBorderWidth => 8f;

    /// <summary>Only shows engagement chrome (the Settings button, an active-state border) while
    /// actually engaged - see WidgetActivation's own doc comment for why right-click/title-click
    /// specifically, not plain OS focus.</summary>
    protected bool ShowsButtons => Activation.ShouldShow;

    protected virtual float TargetOpacity =>
        Style.FullOpacityOnHover && (IsHovered || IsMoving || SettingsDropdown is not null) ? 1f : Style.Opacity / 100f;

    protected virtual Color EditBoxTextColor => Color.WhiteSmoke;
    protected virtual Color EditBoxBackgroundColor => ThemedBody;
    protected virtual Color ChromeMenuFieldColor => ChromeFill;
    protected virtual Color ChromeMenuHoverColor => ThemedMenuSelected;

    protected virtual Color SettingsMenuFieldColor => ChromeFill;
    protected virtual Color SettingsMenuHoverColor => ThemedMenuSelected;
    protected virtual Color SettingsMenuAccentColor => Accent;
    protected virtual Color SettingsMenuBorderColor => ThemedCheckboxBorder;

    // Blended from black rather than DefaultBodyColor (unlike every other Themed* color) - black for
    // an untinted element, and leaning more visibly toward a tinted one's own color at the same blend
    // amount than starting from dark gray would, since there's more contrast for Tint() to work with.
    protected virtual Color SettingsMenuTooltipColor => StyleTint.Tint(Color.Black, CurrentTint, StyleTint.SafeChromeBlend);

    // Shared Settings-dropdown command ids - negative, so they can never collide with a subclass's
    // own positive-numbered command ids (a Fence's CmdToggleHideLabels, CmdToggleOcdSizing, etc.)
    // without either side needing to know about the other's numbering.
    protected const int CmdToggleHideTitle = -1;
    protected const int CmdToggleFullOpacityOnHover = -2;
    protected const int CmdColorDefault = -3;
    protected const int CmdColorCustom = -4;
    protected const int CmdColorEyedrop = -5;
    // Reserves -1000..-901 (100 ids) for the color-preset grid.
    protected const int CmdColorPresetBase = -1000;

    // Mutation hooks, not plain Style property setters - persistence (and, for a Fence, notifying
    // FenceManager so it can broadcast/save across the whole collection) differs by subclass, the
    // same reason Title's own setter is abstract rather than a plain auto-property.
    protected abstract void SetHeaderDarkness(int darkness);
    protected abstract void SetOpacity(int opacity);
    protected abstract void SetTintStrength(int strength);
    protected abstract void SetMargin(int margin);
    protected abstract void SetTintColor(Color? color, bool exact);
    protected abstract void SetFullOpacityOnHover(bool enabled);

    /// <summary>The Settings dropdown's default row list - Hide Title, Full Opacity When Active, then
    /// the shared color grid/Header Darkness/Opacity/Tint Strength sliders/Margin stepper
    /// (StyleMenuRows.Build). A subclass with nothing further to add (Layout Launcher) uses this
    /// as-is; one that needs a different shape/order/extra rows (a Fence's own Hide Shortcut
    /// Names/OCD Sizing) overrides this entirely instead of patching it, reusing the Cmd* ids/mutator
    /// hooks above for whichever of these rows it keeps.</summary>
    protected virtual List<DropdownMenu.Row> BuildSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(CmdToggleHideTitle, "Hide Title", HasCheckbox: true, IsChecked: () => HideTitle),
            new(CmdToggleFullOpacityOnHover, "Full Opacity When Active", HasCheckbox: true,
                IsChecked: () => Style.FullOpacityOnHover,
                Tooltip: "Full opacity while hovered, dragged/resized, or this menu is open"),
            new(0, string.Empty, IsSeparator: true),
        };
        rows.AddRange(StyleMenuRows.Build(Style, DefaultBodyColor, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
            SetHeaderDarkness, SetOpacity, SetTintStrength, SetMargin));
        return rows;
    }

    /// <summary>Dispatches BuildSettingsRows' own default row ids - a subclass that overrides
    /// BuildSettingsRows entirely typically overrides this too (rather than calling base first), since
    /// its own row set rarely matches this shape exactly, but can still route its Hide Title/Full
    /// Opacity/color rows through these same Cmd* ids/mutator hooks.</summary>
    protected virtual void HandleSettingsCommand(int id)
    {
        switch (id)
        {
            case CmdToggleHideTitle:
                HideTitle = !HideTitle;
                RenderAndPresent();
                break;
            case CmdToggleFullOpacityOnHover:
                SetFullOpacityOnHover(!Style.FullOpacityOnHover);
                RenderOpacity.SnapToTarget();
                RenderAndPresent();
                break;
            case CmdColorDefault:
            case CmdColorCustom:
            case CmdColorEyedrop:
            case >= CmdColorPresetBase and < CmdColorPresetBase + 100:
                StyleMenuRows.TryHandleColorCommand(id, CmdColorDefault, CmdColorCustom, CmdColorEyedrop, CmdColorPresetBase,
                    DefaultBodyColor, this, CurrentTint,
                    color => { SetTintColor(color, false); RenderOpacity.SnapToTarget(); RenderAndPresent(); },
                    color =>
                    {
                        SetTintColor(color, true);
                        SetOpacity(100);
                        SetTintStrength(0);
                        RenderOpacity.SnapToTarget();
                        RenderAndPresent();
                    });
                break;
        }
    }

    /// <summary>Body/title fill, border (engagement-aware - see ShowsButtons/ThemedActiveBorder),
    /// title text, and the Settings button - the chrome every widget on this base shares. A
    /// subclass's own PaintContent calls this first, then draws whatever's unique to it (a Fence's
    /// item grid and its own extra buttons chained off the Settings button - see
    /// GetSettingsButtonRect) on top.</summary>
    protected void PaintChrome(Graphics g, int contentWidth, int contentHeight, int cornerRadius)
    {
        using var body = RoundedRectPath.Full(ToWindow(new Rectangle(0, 0, contentWidth - 1, contentHeight - 1)), cornerRadius);
        using (var bodyFill = new SolidBrush(ThemedBody))
            g.FillPath(bodyFill, body);

        if (TitleVisible)
        {
            using var titleFill = new SolidBrush(ThemedTitle);
            using var titlePath = RoundedRectPath.Top(ToWindow(new Rectangle(0, 0, contentWidth - 1, TitleRowHeight)), cornerRadius);
            g.FillPath(titleFill, titlePath);
        }

        using (var borderPen = new Pen(ShowsButtons ? ThemedActiveBorder : ThemedBorder, ShowsButtons ? ActiveBorderWidth : 1f))
        {
            borderPen.LineJoin = LineJoin.Round;
            // Inset, not the default Center - a centered stroke needs half its own width to bleed
            // outward into the margin band beyond the body's edge, but TopBand is deliberately 0 on
            // whichever side currently holds the flipped button row (see OuterMargin's own doc
            // comment on why), leaving no margin pixels there for that outward half to render into -
            // it was getting clipped by the window's own bitmap bounds. Inset keeps the whole stroke
            // within the body itself, which needs no such margin on any edge.
            borderPen.Alignment = PenAlignment.Inset;
            g.DrawPath(borderPen, body);
        }

        if (TitleVisible && !IsRenaming)
        {
            TextRenderer.DrawText(g, Title, Font, ToWindow(new Rectangle(8, 0, contentWidth - 16, TitleRowHeight)),
                Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        if (ShowsButtons)
            PaintSettingsButton(g, contentWidth);
    }

    private void PaintSettingsButton(Graphics g, int contentWidth)
    {
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        var buttonRect = ToWindow(GetSettingsButtonRect(contentWidth, onLeft));

        // Filled first so the button reads as fully opaque - it lives in the near-transparent margin
        // band (see MarginFillColor), and TextRenderer.DrawText/DrawString below only ever writes
        // RGB, never alpha, so without an opaque backing shape under it the label would inherit the
        // margin's near-zero alpha and vanish once RenderAndPresent's own alpha-scaling runs.
        using (var buttonPath = RoundedRectPath.Full(buttonRect, 6))
        using (var buttonFill = new SolidBrush(ChromeFill))
        {
            g.FillPath(buttonFill, buttonPath);
            using var buttonBorderPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
            g.DrawPath(buttonBorderPen, buttonPath);
        }

        // GDI+'s DrawString instead of the GDI TextRenderer.DrawText used for the title above - GDI's
        // own ClearType antialiasing assumes a neutral/opaque background and fringes with visible
        // red/blue "shadow" pixels along each glyph's edge against a saturated color like ChromeFill
        // can be; GDI+'s AntiAlias hint is plain grayscale, so it doesn't.
        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("Settings", Font, textBrush, buttonRect, textFormat);
        g.TextRenderingHint = previousTextHint;
    }

    /// <summary>Keeps an already-open Settings dropdown anchored to its button after a resize moves
    /// it out from under it - the default (and, so far, only) OnResized follow-up any subclass needs.</summary>
    protected virtual void OnResized()
    {
        if (SettingsDropdown is null)
            return;
        var contentSize = GetContentSize();
        var onLeft = ShouldSettingsButtonOpenLeft(contentSize.Width);
        var buttonRect = GetSettingsButtonRect(contentSize.Width, onLeft);
        var buttonScreenRect = new Rectangle(PointToScreen(ToWindow(buttonRect.Location)), buttonRect.Size);
        SettingsDropdown.RepositionRelativeTo(buttonScreenRect, preferLeft: onLeft);
    }

    // ---- Move/resize/snap ----

    protected virtual int ResizeMargin => 12;

    /// <summary>Whether this window can be resized at all - true by default (every widget on this
    /// base gets full resize unless it deliberately opts out). See ResizableEdges to restrict which
    /// edges instead of turning resize off entirely.</summary>
    protected virtual bool SupportsResize => true;

    /// <summary>Which edges (see SnapEdges) resize when SupportsResize - all four by default.</summary>
    protected virtual SnapEdges ResizableEdges => SnapEdges.Left | SnapEdges.Right | SnapEdges.Top | SnapEdges.Bottom;

    /// <summary>The subclass's own current body rect (position + size), read fresh once at the start
    /// of every drag/resize (see DragStartBody) - the fixed anchor everything that tick measures
    /// against instead of the OS's own incrementally-drifting proposed rect.</summary>
    protected abstract Rectangle GetCurrentBody();

    /// <summary>Guid to exclude from "other fence" snap candidates - a real fence passes its own
    /// FenceId; anything that isn't a fence itself (and so never appears in that candidate list
    /// anyway) passes Guid.Empty.</summary>
    protected abstract Guid SnapExcludeId { get; }

    /// <summary>This widget's own margin setting (IWidgetStyle.Margin) - how far it prefers to keep
    /// from another fence's edge or a custom snap line while dragging/resizing.</summary>
    protected abstract int SnapMargin { get; }

    private static bool IsResizeHitTestCode(int hitTest) =>
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

    /// <summary>Resize hit-testing for the ResizeMargin-wide band just outside the visible body,
    /// respecting SupportsResize/ResizableEdges - returns the matching HT* edge/corner code, or null
    /// if the point isn't in a resize band (or resize isn't available there at all). windowPoint is
    /// window-relative; width/height are the window's own full current size.</summary>
    protected int? ResizeHitTest(Point windowPoint, int width, int height)
    {
        if (!SupportsResize)
            return null;

        var edges = ResizableEdges;
        var band = OuterMargin + ResizeMargin;
        var left = edges.HasFlag(SnapEdges.Left) && windowPoint.X <= band;
        var right = edges.HasFlag(SnapEdges.Right) && windowPoint.X >= width - band;
        var top = edges.HasFlag(SnapEdges.Top) && windowPoint.Y <= TopBand + ResizeMargin;
        var bottom = edges.HasFlag(SnapEdges.Bottom) && windowPoint.Y >= height - BottomBand - ResizeMargin;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return null;
    }

    /// <summary>Re-inflates a snapped visible-body rect back into raw window coordinates and writes
    /// it into the RECT at lParam for the OS's own move/resize loop to pick up.</summary>
    protected void WriteBackWindowRect(IntPtr lParam, Rectangle body)
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

    /// <summary>Snaps a proposed move against every other fence's edges and this app's custom snap
    /// lines - the default every subclass gets for free. Holding the right mouse button down at the
    /// same time hides the fence-edge candidates for as long as it's held, leaving just the custom
    /// lines (checked live via Control.MouseButtons, not any button-down message, since DefWindowProc's
    /// own modal move loop may never route one to this WndProc at all while it's running).</summary>
    protected virtual Rectangle ComputeMovedBody(Rectangle proposedBody)
    {
        IReadOnlyList<int> vCandidates = Array.Empty<int>();
        IReadOnlyList<int> hCandidates = Array.Empty<int>();
        if ((MouseButtons & MouseButtons.Right) == 0)
            (vCandidates, hCandidates) = Fences.GetOtherFenceEdges(SnapExcludeId, SnapMargin);
        return Fences.SnapLines.SnapMove(proposedBody, vCandidates, hCandidates, SnapMargin).Rect;
    }

    /// <summary>Same idea as ComputeMovedBody, for a resize - always shows both custom lines and
    /// fence edges (WM_SIZING has no right-click modifier the way a move does).</summary>
    protected virtual Rectangle ComputeResizedBody(Rectangle proposedBody, SnapEdges activeEdges)
    {
        var (vCandidates, hCandidates) = Fences.GetOtherFenceEdges(SnapExcludeId, SnapMargin);
        return Fences.SnapLines.SnapResize(proposedBody, activeEdges, vCandidates, hCandidates, SnapMargin).Rect;
    }

    /// <summary>WM_ENTERSIZEMOVE's own snap-guide setup, shown for the guide overlay's whole
    /// lifetime - a resize (see IsResizing) always shows both custom lines and fence edges; a move
    /// shows both too unless right is already held right at the start of the drag (the common case
    /// is checked live every tick inside ComputeMovedBody instead; this is only for the very first
    /// frame, before any movement has happened yet, so the guides don't lag one tick behind).</summary>
    protected virtual void BeginSnapDrag()
    {
        if (IsResizing || (MouseButtons & MouseButtons.Right) == 0)
        {
            var (vGuides, hGuides) = Fences.GetOtherFenceEdges(SnapExcludeId, SnapMargin);
            var monitor = Screen.FromRectangle(DragStartBody).Bounds;
            Fences.SnapLines.BeginDrag(includeCustomLines: true, vGuides, hGuides, monitor);
        }
        else
        {
            Fences.SnapLines.BeginDrag();
        }
    }

    /// <summary>WM_EXITSIZEMOVE's own subclass-specific follow-up: persisting the settled position/
    /// size, any "tidy up now that the drag is done" behavior, z-order restacking - all entirely up
    /// to the override (the snap-guide teardown and IsMoving/IsResizing reset happen automatically
    /// around this call - see WndProc). This one hook is what keeps all of that optional for anything
    /// this base doesn't itself know about.</summary>
    protected abstract void OnDragEnd();

    // ---- Hooks a subclass must (or may) implement ----

    protected abstract int HitTest(IntPtr lParam);

    /// <summary>Fires after Activation's own WM_NCLBUTTONDOWN handling, with the same raw hit-test
    /// code HitTest returned for this point - default sets IsResizing from it (see
    /// IsResizeHitTestCode), which is all FenceForm's own equivalent ever did; override only for
    /// something beyond that.</summary>
    protected virtual void OnNcLButtonDown(int hitTestCode) => IsResizing = IsResizeHitTestCode(hitTestCode);

    /// <summary>Fires after WM_RBUTTONUP's own Activation.Activate() - a right-click landing
    /// somewhere in the plain client body (not the margin/title row, which go through
    /// WM_NCRBUTTONDOWN/ShowTitleContextMenu instead) still needs to activate the widget by default;
    /// this is where a subclass with something worth right-clicking in its own content (a Fence's
    /// items, say) can show its own context menu. contentPoint is already in content space (see
    /// ToContent). Default no-op - right-click still activates either way.</summary>
    protected virtual void OnClientRightClick(Point contentPoint) { }

    /// <summary>Paints everything beyond the near-transparent margin band this base already filled -
    /// body, title, buttons, and whatever content fills the rest. contentWidth/contentHeight are the
    /// visible body's own size (see GetContentSize) - use ToWindow to place anything drawn here.</summary>
    protected abstract void PaintContent(Graphics g, int contentWidth, int contentHeight);

    /// <summary>Dispose(true)'s own subclass-specific contents (a drag ghost, an icon cache -
    /// whatever the subclass owns beyond what this base already tracks, which now includes the
    /// rename box, title context menu, and Settings dropdown). Called before this base tears down
    /// RenderOpacity/the theme brush.</summary>
    protected abstract void DisposeOwnedResources();

    // ---- Title / rename ----

    /// <summary>The rename-able title text itself - get returns what's currently shown/being
    /// renamed; set commits a new value (already trimmed and validated by BeginRename's own commit
    /// handler) and is responsible for persisting it, the same way a subclass persists its other
    /// settings.</summary>
    protected abstract string Title { get; set; }

    /// <summary>Content-space height of the title row.</summary>
    protected abstract int TitleRowHeight { get; }

    /// <summary>Whether lParam lands specifically on the rendered title text - not just anywhere in
    /// the header row - gating right-click-to-rename/double-click-to-rename to the text itself (a
    /// click past the end of a short title doesn't count as "on" it). Mirrors the actual title-text
    /// paint position (see PaintContent's own title-drawing call, which should match this rect).</summary>
    protected virtual bool IsOverTitleRow(IntPtr lParam)
    {
        if (!TitleVisible || !NativeMethods.GetWindowRect(Handle, out var rect))
            return false;

        var content = ToContent(ScreenLParamToWindowPoint(lParam, rect));
        var maxWidth = Math.Max(0, GetContentSize().Width - 16);
        var textWidth = Math.Min(maxWidth, TextRenderer.MeasureText(Title, Font).Width);
        return new Rectangle(8, 0, textWidth, TitleRowHeight).Contains(content);
    }

    protected virtual void BeginRename()
    {
        if (_renameBox is not null || !TitleVisible)
            return;

        var maxWidth = Math.Max(0, GetContentSize().Width - 16);
        var rect = ToWindow(new Rectangle(6, 3, maxWidth, Math.Max(0, TitleRowHeight - 6)));
        _renameBox = new EditBox(Handle, Title, ToScreen(rect), Font);
        _renameBox.Commit += OnRenameCommit;
        _renameBox.Cancel += OnRenameCancel;
    }

    private void OnRenameCommit(string newName)
    {
        _renameBox?.Dispose();
        _renameBox = null;

        newName = newName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != Title)
            Title = newName;

        RenderAndPresent();
    }

    private void OnRenameCancel()
    {
        _renameBox?.Dispose();
        _renameBox = null;
        RenderAndPresent();
    }

    /// <summary>Right-click on the title text specifically (see IsOverTitleRow) - a themed
    /// ContextMenuStrip with a single "Rename" item.</summary>
    protected virtual void ShowTitleContextMenu()
    {
        if (!TitleVisible)
            return;

        _titleContextMenu ??= BuildTitleContextMenu();
        NativeMethods.GetCursorPos(out var pt);
        _titleContextMenu.Show(this, PointToClient(new Point(pt.X, pt.Y)));
    }

    private ContextMenuStrip BuildTitleContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(() => ChromeMenuFieldColor, () => ChromeMenuHoverColor, () => AppTheme.Text),
            Font = Font,
        };
        menu.Items.Add("Rename", null, (_, _) => BeginRename());
        return menu;
    }

    // ---- Settings button/dropdown ----

    protected virtual int SettingsButtonWidth => 64;
    protected virtual int SettingsButtonHeight => 22;
    protected virtual int SettingsButtonGap => 6;

    /// <summary>Content-relative, positioned just outside the visible body, in the reserved button
    /// band - lives outside the visible body entirely, right down to the Y formula (negative - above
    /// content Y=0 normally, or below the body's own bottom edge instead once ButtonRowAtBottom
    /// flips there). Flush with the top-right corner by default; flipped to the top-left when the
    /// options dropdown wouldn't fit opening rightward from there (see ShouldSettingsButtonOpenLeft,
    /// which reuses this same rect's X to decide which side the menu itself opens on, so the two
    /// always agree).</summary>
    protected Rectangle GetSettingsButtonRect(int contentWidth, bool onLeft)
    {
        var y = ButtonRowAtBottom ? GetContentSize().Height + SettingsButtonGap : -(SettingsButtonHeight + SettingsButtonGap);
        return onLeft
            ? new Rectangle(0, y, SettingsButtonWidth, SettingsButtonHeight)
            : new Rectangle(contentWidth - SettingsButtonWidth, y, SettingsButtonWidth, SettingsButtonHeight);
    }

    /// <summary>Measures the actual options menu (BuildSettingsRows) against the screen this window
    /// is currently on, using the button's default top-right placement as the anchor - i.e. "would
    /// the menu fit opening to the right of a right-corner button".</summary>
    protected bool ShouldSettingsButtonOpenLeft(int contentWidth)
    {
        var rightAligned = ToWindow(GetSettingsButtonRect(contentWidth, onLeft: false));
        var buttonScreenRect = new Rectangle(PointToScreen(rightAligned.Location), rightAligned.Size);
        return StyleMenuRows.ShouldOpenLeft(buttonScreenRect, BuildSettingsRows(), Font);
    }

    /// <summary>Opens (or, if one's already open, replaces) the Settings dropdown. Explicit
    /// RenderOpacity.BeginIfNeeded() calls on both open and close - TargetOpacity typically depends
    /// on SettingsDropdown being non-null, so each transition may need to start easing toward/away
    /// from Full Opacity right away rather than waiting for some unrelated repaint to notice.</summary>
    protected void OpenSettingsMenu()
    {
        SettingsDropdown?.Dispose();

        var width = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(width);
        var buttonScreenRect = RectangleToScreen(ToWindow(GetSettingsButtonRect(width, onLeft)));
        var dropdown = new DropdownMenu(BuildSettingsRows(), buttonScreenRect, onLeft, Font,
            () => SettingsMenuFieldColor, () => SettingsMenuHoverColor, () => SettingsMenuAccentColor,
            () => SettingsMenuBorderColor, () => SettingsMenuTooltipColor);
        SettingsDropdown = dropdown;
        dropdown.ItemClicked += id =>
        {
            HandleSettingsCommand(id);
            dropdown.RefreshChecks();
        };
        Activation.MenuOpen = true;
        dropdown.FormClosed += (_, _) =>
        {
            SettingsDropdown = null;
            Activation.MenuOpen = false;
            RenderOpacity.BeginIfNeeded();
        };
        dropdown.Show(this);
        RenderOpacity.BeginIfNeeded();
    }

    // ---- Base-owned behavior ----

    /// <summary>Same "losing focus always deactivates" rule regardless of what's currently showing -
    /// see WidgetActivation's own doc comment for why activation itself is never driven by the
    /// Control's own Activated/OnActivated instead.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Activation.Deactivate();
    }

    /// <summary>Tracks whether the cursor is over this window's client area, for "Full Opacity When
    /// Active" (see IsHovered/TargetOpacity) - client-area only; the margin/resize band is covered
    /// separately by WM_NCMOUSEMOVE/WM_NCMOUSELEAVE below.</summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isClientHovered = true;
        RenderOpacity.BeginIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isClientHovered = false;
        RenderOpacity.BeginIfNeeded();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Set before anything below runs - see IsDisposing's own field comment.
            IsDisposing = true;
            _renameBox?.Dispose();
            _titleContextMenu?.Dispose();
            SettingsDropdown?.Dispose();
            DisposeOwnedResources();
            RenderOpacity.Dispose();
            if (_themeBrush != IntPtr.Zero)
                NativeMethods.DeleteObject(_themeBrush);
        }
        base.Dispose(disposing);
    }

    /// <summary>Builds this window's full appearance into an off-screen ARGB bitmap and pushes it via
    /// UpdateLayeredWindow. Called any time something visible changes (hover, drag, rename, content)
    /// rather than in response to WM_PAINT, since a layered window's content isn't repainted by
    /// Windows itself.</summary>
    protected void RenderAndPresent()
    {
        if (IsDisposing)
            return;

        if (!NativeMethods.GetWindowRect(Handle, out var windowRect))
            return;

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var contentWidth = width - OuterMargin * 2;
        var contentHeight = height - TopBand - BottomBand;
        if (contentWidth <= 0 || contentHeight <= 0)
            return;

        using var buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.Clear(Color.Transparent);

            // Needs a non-zero (if faint) alpha - Windows treats fully transparent (alpha 0) pixels
            // of a layered window as click-through, so a truly invisible margin couldn't receive the
            // drag/resize hit-testing it exists for. Drawn first; PaintContent's own opaque body then
            // covers all of it except the margin itself.
            using (var marginFill = new SolidBrush(MarginFillColor))
                g.FillRectangle(marginFill, 0, 0, width, height);

            g.SetClip(new Rectangle(0, 0, width, height));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // DrawIcon's native GDI stretch looks jagged when scaling a source icon down - drawing
            // icons as bitmaps under high-quality interpolation instead avoids that.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            PaintContent(g, contentWidth, contentHeight);
        }

        LayeredWindowPresenter.Present(Handle, buffer, new Point(windowRect.Left, windowRect.Top), RenderOpacity.Value);
    }

    /// <summary>Intercepts the OS's own non-client/interactive-move/resize handling. WM_MOVING/
    /// WM_SIZING are handled directly here now (see ComputeMovedBody/ComputeResizedBody) - DefWindowProc
    /// has no default handling for either, so mutating the RECT at lParam and returning is enough;
    /// the outer drag loop (not DefWindowProc) is what reads it back. Everything else follows
    /// FenceForm's own original WndProc structure: messages DefWindowProc has no default handling
    /// worth keeping (WM_NCHITTEST, WM_NCLBUTTONDBLCLK, WM_NCRBUTTONDOWN, WM_CTLCOLOREDIT) are
    /// swallowed and returned early; WM_NCLBUTTONDOWN/WM_NCMOUSEMOVE/WM_NCMOUSELEAVE still need the
    /// default proc afterward, so they fall through instead of returning.</summary>
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
            var proposed = new Rectangle(
                DragStartBody.X + (currentScreenPoint.X - LeftDragStartScreenPoint.X),
                DragStartBody.Y + (currentScreenPoint.Y - LeftDragStartScreenPoint.Y),
                DragStartBody.Width, DragStartBody.Height);
            var body = ComputeMovedBody(proposed);
            // Re-decided against the proposed rect's own new position - a drag that crosses the
            // "would go off the top of the screen" threshold mid-tick flips right here, so
            // WriteBackWindowRect (next) already inflates using whichever side the button band
            // belongs on now, not wherever it was a moment ago.
            ButtonRowAtBottom = ComputeButtonRowAtBottom(body.Location, MaxTopBand);
            WriteBackWindowRect(m.LParam, body);
            m.Result = (IntPtr)1;

            // See _draggedSettingsButtonOnLeft's own comment - this is the one piece of a live move
            // that doesn't otherwise become visible for free, so it gets its own explicit (but
            // change-gated) repaint.
            if (ShowsButtons)
            {
                var onLeft = ShouldSettingsButtonOpenLeft(body.Width);
                if (onLeft != _draggedSettingsButtonOnLeft)
                {
                    _draggedSettingsButtonOnLeft = onLeft;
                    RenderAndPresent();
                }
            }
            return;
        }

        if (m.Msg == NativeMethods.WM_SIZING)
        {
            // Same fixed-anchor reasoning as WM_MOVING above, just per-edge: whichever edges this
            // particular resize handle doesn't control stay pinned exactly where the drag started
            // (DragStartBody, unchanging for the whole drag), and only the active ones move by the
            // cursor's total delta since then.
            var edges = SnapEdgesFromWmSz((int)m.WParam.ToInt64());
            var currentScreenPoint = Cursor.Position;
            var dx = currentScreenPoint.X - LeftDragStartScreenPoint.X;
            var dy = currentScreenPoint.Y - LeftDragStartScreenPoint.Y;
            var start = DragStartBody;
            var proposed = Rectangle.FromLTRB(
                (edges & SnapEdges.Left) != 0 ? start.Left + dx : start.Left,
                (edges & SnapEdges.Top) != 0 ? start.Top + dy : start.Top,
                (edges & SnapEdges.Right) != 0 ? start.Right + dx : start.Right,
                (edges & SnapEdges.Bottom) != 0 ? start.Bottom + dy : start.Bottom);
            var body = ComputeResizedBody(proposed, edges);
            ButtonRowAtBottom = ComputeButtonRowAtBottom(body.Location, MaxTopBand);
            WriteBackWindowRect(m.LParam, body);
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_NCLBUTTONDBLCLK)
        {
            // HitTest's own HTCAPTION covers the whole draggable margin/title row, but a
            // double-click should only trigger a rename over the title row itself - anywhere else in
            // this non-client area, do nothing rather than letting the default proc maximize the
            // window (its usual caption double-click behavior).
            Activation.Activate();
            if (IsOverTitleRow(m.LParam))
                BeginRename();
            return;
        }

        if (m.Msg == NativeMethods.WM_NCRBUTTONDOWN)
        {
            // A real caption's right-click would show the system menu via the default proc - there's
            // no such menu for this custom-drawn title row, so this always swallows the message
            // itself rather than falling through to base.WndProc/DefWindowProc.
            Activation.Activate();
            if (IsOverTitleRow(m.LParam))
                ShowTitleContextMenu();
            return;
        }

        if (m.Msg == WM_RBUTTONUP)
        {
            // The client-body counterpart to WM_NCRBUTTONDOWN above - a right-click anywhere on the
            // widget activates it, not just the margin/title row. Always swallowed here too (same
            // "DefWindowProc's own default handling never gets a chance to run" reasoning, including
            // the defensive Capture release - see WM_NCRBUTTONDOWN's own comment) rather than falling
            // through, so a subclass's own OnClientRightClick is the only thing that runs afterward.
            Capture = false;
            Activation.Activate();
            var l = m.LParam.ToInt64();
            var contentPoint = ToContent(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
            OnClientRightClick(contentPoint);
            return;
        }

        if (m.Msg == NativeMethods.WM_CTLCOLOREDIT)
        {
            // Sent by a rename EditBox to its owner (GetParent resolves here even though it's a
            // top-level WS_POPUP, not a true child - see EditBox's own class comment) each time it
            // needs to know what to paint itself with.
            NativeMethods.SetTextColor(m.WParam, ColorRef(EditBoxTextColor));
            NativeMethods.SetBkColor(m.WParam, ColorRef(EditBoxBackgroundColor));
            m.Result = GetThemeBrush(EditBoxBackgroundColor);
            return;
        }

        if (m.Msg == NativeMethods.WM_NCLBUTTONDOWN)
        {
            // A left click on the title row activates the window - not returning early: the default
            // proc still needs this message to actually start the move/resize.
            var hitTestCode = (int)m.WParam.ToInt64();
            if (hitTestCode == HTCAPTION)
                Activation.Activate();
            OnNcLButtonDown(hitTestCode);
        }
        else if (m.Msg == NativeMethods.WM_NCMOUSEMOVE)
        {
            // WinForms' own client-area hover tracking (OnMouseEnter/OnMouseLeave) doesn't cover
            // this - the margin/resize band reports HTLEFT/HTCAPTION/etc. (see HitTest), so the OS
            // treats it as non-client and never raises the client mouse events those hook.
            // TrackMouseEvent needs re-arming on every WM_NCMOUSEMOVE (Windows disarms it after
            // firing once), not just the first - but only bother once per hover session since
            // _isNonClientHovered already being true means it's still armed from last time.
            if (!_isNonClientHovered)
            {
                _isNonClientHovered = true;
                RenderOpacity.BeginIfNeeded();
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
            RenderOpacity.BeginIfNeeded();
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_SIZE:
                RenderAndPresent();
                OnResized();
                break;

            case WM_ENTERSIZEMOVE:
                IsMoving = true;
                DragStartBody = GetCurrentBody();
                LeftDragStartScreenPoint = Cursor.Position;
                _draggedSettingsButtonOnLeft = ShouldSettingsButtonOpenLeft(DragStartBody.Width);
                BeginSnapDrag();
                RenderOpacity.BeginIfNeeded();
                break;

            case WM_EXITSIZEMOVE:
                Fences.SnapLines.EndDrag();
                OnDragEnd();
                IsMoving = false;
                IsResizing = false;
                RenderOpacity.BeginIfNeeded();
                // A pure move (no resize) never otherwise triggers a repaint - WM_SIZE above already
                // covers the resize case - but the Settings button's own corner (see
                // ShouldSettingsButtonOpenLeft) depends on this widget's absolute screen position, so
                // a move that crosses the point where the button should flip corners would otherwise
                // leave the stale side drawn (and hit-tested wrongly, since HitTest recomputes fresh
                // on the next click) until some unrelated repaint happened to notice. Only worth doing
                // while the button's actually showing.
                if (ShowsButtons)
                    RenderAndPresent();
                break;
        }
    }
}
