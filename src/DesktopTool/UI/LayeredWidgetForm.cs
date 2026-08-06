using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DesktopTool.Native;

namespace DesktopTool.UI;

/// <summary>
/// Base class for a hand-painted, layered Win32 window (WS_POPUP + WS_EX_LAYERED, no WinForms child
/// controls) that behaves like a Fence: draggable via the OS's own interactive move loop, styled from
/// a live tint/opacity, with a hideable title row and an activation-gated button row that flips
/// between the top and bottom band depending on how close to a monitor's own top edge it's sitting.
/// Extracted from FenceForm itself - owns everything that isn't specific to a Fence's own icon grid:
/// WndProc's non-client hit-testing/drag-loop plumbing, the layered-window paint/present cycle, and
/// hover/activation/opacity state. Resize, desktop anchoring, and anything content-shaped are
/// deliberately NOT part of this base - they exist only inside FenceForm's own overrides/WndProc
/// additions, which is what keeps them optional for whatever else ends up built on this class.
/// </summary>
internal abstract class LayeredWidgetForm : Form
{
    protected const int WM_NCHITTEST = 0x0084;
    protected const int WM_NCLBUTTONDBLCLK = 0x00A3;
    protected const int WM_SIZE = 0x0005;
    protected const int WM_ENTERSIZEMOVE = 0x0231;
    protected const int WM_EXITSIZEMOVE = 0x0232;

    protected const int HTCLIENT = 1;
    protected const int HTCAPTION = 2;

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

    // Covers both an interactive move and an interactive resize (where a subclass has one) - set
    // between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE.
    protected bool IsMoving { get; set; }

    // Together back "Full Opacity When Active" (see IsHovered/TargetOpacity) - split into
    // client/non-client because they're detected two completely different ways: OnMouseEnter/
    // OnMouseLeave for the client half, WM_NCMOUSEMOVE/WM_NCMOUSELEAVE below for the margin band.
    private bool _isClientHovered;
    private bool _isNonClientHovered;
    protected bool IsHovered => _isClientHovered || _isNonClientHovered;

    // Fixed anchor a drag (and, where a subclass has one, a resize) measures against every tick,
    // instead of trusting the OS's own incrementally-proposed rect - see each subclass's own
    // WM_MOVING/WM_SIZING handling for why (drift/stickiness otherwise).
    protected Point LeftDragStartScreenPoint { get; set; }

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
    // ComputeButtonRowAtBottom. Kept in sync wherever a subclass computes its own position (its own
    // CreateParams, and every tick of a live drag) rather than read fresh on every use.
    protected bool ButtonRowAtBottom { get; set; }

    protected LayeredWidgetForm(float initialOpacity)
    {
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

    // The invisible drag/resize-grab band around the visible body (constant on every edge but one),
    // and the margin band on whichever of top/bottom currently holds the button row vs. doesn't - see
    // ButtonRowAtBottom. Left fully to each subclass: FenceForm's own split (TopBand collapsing to 0
    // once flipped, BottomBand flooring at OuterMargin rather than 0) is Fence-specific reasoning
    // about Fence's own margin band, not something to generalize from a single example.
    protected abstract int OuterMargin { get; }
    protected abstract int TopBand { get; }
    protected abstract int BottomBand { get; }

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

    // ---- Hooks a subclass must (or may) implement ----

    protected abstract int HitTest(IntPtr lParam);

    /// <summary>Whether lParam (a WM_NCLBUTTONDBLCLK/WM_NCRBUTTONDOWN screen point) lands on the
    /// title row specifically, as opposed to anywhere else HitTest's own HTCAPTION covers - gates
    /// rename/the title context menu to just the title text/row, not the whole draggable margin.</summary>
    protected abstract bool IsOverTitleRow(IntPtr lParam);

    protected abstract void BeginRename();
    protected abstract void ShowTitleContextMenu();

    /// <summary>Fires after Activation's own WM_NCLBUTTONDOWN handling, with the same raw hit-test
    /// code HitTest returned for this point - a no-op unless a subclass has something that needs to
    /// know a drag is a resize (as opposed to a move) once it starts. Default no-op covers anything
    /// with no resize concept at all.</summary>
    protected virtual void OnNcLButtonDown(int hitTestCode) { }

    protected abstract Color EditBoxTextColor { get; }
    protected abstract Color EditBoxBackgroundColor { get; }

    /// <summary>WM_ENTERSIZEMOVE's own snap-guide setup - entirely a subclass concern (which lines/
    /// edges to offer as candidates, whether a resize shows different ones than a move) rather than
    /// something this base can generalize from a single implementation.</summary>
    protected abstract void BeginSnapDrag();

    /// <summary>WM_EXITSIZEMOVE in full - snap-guide teardown, persisting the settled position/size,
    /// any subclass-specific "tidy up now that the drag is done" behavior, and z-order restacking are
    /// all entirely up to the override. This one hook is what keeps all of that optional for anything
    /// this base doesn't itself know about.</summary>
    protected abstract void OnDragEnd();

    /// <summary>Fires after WM_SIZE's own repaint - for anything else that needs to track a changed
    /// size (repositioning a rename box, a still-open dropdown). Must not call RenderAndPresent
    /// itself; WM_SIZE's handling already does.</summary>
    protected virtual void OnResized() { }

    /// <summary>Paints everything beyond the near-transparent margin band this base already filled -
    /// body, title, buttons, and whatever content fills the rest. contentWidth/contentHeight are the
    /// visible body's own size (see GetContentSize) - use ToWindow to place anything drawn here.</summary>
    protected abstract void PaintContent(Graphics g, int contentWidth, int contentHeight);

    /// <summary>Where RenderOpacity's own eased Value should end up, not necessarily what's rendered right
    /// now - typically full while "in use" (hovered/dragging/a menu open) if the subclass's own
    /// "full opacity when active" setting is on, its own configured opacity otherwise.</summary>
    protected abstract float TargetOpacity { get; }

    /// <summary>Dispose(true)'s own subclass-specific contents (rename/other EditBoxes, a drag ghost,
    /// an open dropdown, fonts, an icon cache - whatever the subclass owns beyond what this base
    /// already tracks). Called before this base tears down RenderOpacity/the theme brush.</summary>
    protected abstract void DisposeOwnedResources();

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

    /// <summary>Intercepts the OS's own non-client/interactive-move handling to route it through the
    /// hooks above, following FenceForm's own original WndProc structure: messages DefWindowProc has
    /// no default handling worth keeping (WM_NCHITTEST, WM_NCLBUTTONDBLCLK, WM_NCRBUTTONDOWN,
    /// WM_CTLCOLOREDIT) are swallowed and returned early; WM_NCLBUTTONDOWN/WM_NCMOUSEMOVE/
    /// WM_NCMOUSELEAVE still need the default proc afterward, so they fall through instead of
    /// returning. Everything with no shared home (WM_MOVING/WM_SIZING and anything content-specific)
    /// is left to whatever a subclass's own WndProc override does before/after calling this one.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HitTest(m.LParam);
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
            // itself rather than falling through to base.WndProc/DefWindowProc. Gated purely on
            // IsOverTitleRow's own geometry, not the hit-test code the OS reported - a subclass's
            // title row doesn't have to report HTCAPTION specifically (FenceForm's own no longer
            // does, so a left-drag from there can't move the window - see its own HTBORDER comment)
            // for this right-click handling to still apply to it.
            Activation.Activate();
            if (IsOverTitleRow(m.LParam))
                ShowTitleContextMenu();
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
                // See LeftDragStartScreenPoint's own comment - WM_MOVING/WM_SIZING both measure
                // against this fixed point instead of trusting the OS's own incrementally-drifting
                // proposed rect.
                LeftDragStartScreenPoint = Cursor.Position;
                BeginSnapDrag();
                RenderOpacity.BeginIfNeeded();
                break;

            case WM_EXITSIZEMOVE:
                OnDragEnd();
                break;
        }
    }
}
