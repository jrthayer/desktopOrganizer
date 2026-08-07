using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Layout Launcher" widget, rebuilt on LayeredWidgetForm - a second, independent proof that the
/// base's own move/resize/snap/rename/title-menu/Settings-button/theme chrome works for something
/// that isn't a Fence. Chrome only for now (header with a rename-able title, full unrestricted
/// resize, drag-to-snap, the base's own default Settings dropdown, and a Close button next to it) -
/// the saved-layout list itself (Run/Copy/Delete rows, Save Current Layout, Manage Layouts...) comes
/// back in its own later step, verified on its own rather than bundled into this one. Everything not
/// genuinely specific to this widget (theme derivation, the Settings dropdown's default rows,
/// button/border/title painting) is LayeredWidgetForm's own now - see its own class comment.
/// </summary>
internal sealed class LayoutLauncherWidget : LayeredWidgetForm
{
    private const int OuterMarginPx = 13;
    private const int HeaderHeight = 28;
    // Same reasoning/value as FenceForm's own SettingsButtonOverhang - just enough extra band above
    // OuterMargin to fully contain a SettingsButtonHeight-tall button plus its SettingsButtonGap
    // breathing room (both base defaults, 22/6), with a little more room to spare.
    private const int ButtonBandOverhang = 19;
    private const int TopMarginWithButtons = OuterMarginPx + ButtonBandOverhang;
    // Used only to seed CreateParams/GetCurrentBody before the widget has ever been resized (see
    // LayoutLauncherModel.Height's own "null until moved/resized once" comment) - a real resize
    // persists over this immediately, the same way a real move persists over the centered X/Y default.
    private const int DefaultBodyHeight = 120;
    private const int CloseButtonSize = 22;
    private const int ButtonSpacing = 4;

    private readonly LayoutManager _manager;
    private readonly FenceManager _fenceManager;
    private readonly LayoutLauncherModel _model;
    private readonly LayoutLauncherStore _store;

    private bool _allowClose;
    private bool _settingsButtonArmed;
    private bool _closeButtonArmed;

    /// <summary>Guid? carries a freshly-captured profile's Id up to TrayApplicationContext.
    /// OpenLayoutEditor - not raised by anything yet (the layout list itself comes back in a later
    /// step), but kept declared so TrayApplicationContext's own subscription still compiles.</summary>
    public event EventHandler<Guid?>? ManageLayoutsRequested;

    protected override int OuterMargin => OuterMarginPx;
    protected override int TopBand => ButtonRowAtBottom ? 0 : TopMarginWithButtons;
    protected override int BottomBand => ButtonRowAtBottom ? TopMarginWithButtons : OuterMargin;
    protected override int MaxTopBand => TopMarginWithButtons;

    /// <summary>Which model LayeredWidgetForm's own theme derivation and default Settings-dropdown
    /// rows read from - LayoutLauncherModel already implements IWidgetStyle.</summary>
    protected override IWidgetStyle Style => _model;

    public LayoutLauncherWidget(LayoutManager manager, FenceManager fenceManager, LayoutLauncherModel model, LayoutLauncherStore store)
        : base(model.Opacity / 100f, fenceManager)
    {
        _manager = manager;
        _fenceManager = fenceManager;
        _model = model;
        _store = store;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // LayeredWidgetForm's own default rename hit-testing/EditBox/title-context-menu/PaintChrome
        // all measure and draw against Control.Font, so this needs setting explicitly (WinForms'
        // own default is Microsoft Sans Serif).
        Font = AppTheme.Font;

        // Forces handle creation now that every field CreateParams needs is set.
        RenderAndPresent();
    }

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

            var bodyX = _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2;
            var bodyY = _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2;
            var bodyHeight = _model.Height ?? DefaultBodyHeight;

            ButtonRowAtBottom = ComputeButtonRowAtBottom(new Point(bodyX, bodyY), TopMarginWithButtons);

            cp.Width = _model.Width + OuterMargin * 2;
            cp.Height = bodyHeight + TopBand + BottomBand;
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

    private void Persist() => _store.Save(_model);

    protected override void DisposeOwnedResources()
    {
        // Nothing owned yet - no icon cache, no drag ghost (both come back with the layout list).
    }

    protected override Rectangle GetCurrentBody() => new(
        _model.X ?? (Screen.PrimaryScreen!.WorkingArea.Width - _model.Width) / 2,
        _model.Y ?? (Screen.PrimaryScreen!.WorkingArea.Height - DefaultBodyHeight) / 2,
        _model.Width,
        _model.Height ?? DefaultBodyHeight);

    protected override Guid SnapExcludeId => Guid.Empty;
    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/BeginSnapDrag/SupportsResize/ResizableEdges all use
    // LayeredWidgetForm's own defaults unchanged - full, unrestricted resize on every edge, snapping
    // against fences/custom snap lines the same as any other widget on this base.

    protected override void OnDragEnd()
    {
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _model.X = rect.Left + OuterMargin;
            _model.Y = rect.Top + TopBand;
            _model.Width = rect.Right - rect.Left - OuterMargin * 2;
            _model.Height = rect.Bottom - rect.Top - TopBand - BottomBand;
            Persist();
        }

        RenderOpacity.BeginIfNeeded();
    }

    // OnResized needs no override of its own - LayeredWidgetForm's own default (repositioning an
    // already-open Settings dropdown after a resize) already covers it.

    protected override int HitTest(IntPtr lParam)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect))
            return HTCLIENT;

        var windowPoint = ScreenLParamToWindowPoint(lParam, rect);
        int x = windowPoint.X;
        int y = windowPoint.Y;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        var contentWidth = width - OuterMargin * 2;
        var contentPoint = ToContent(windowPoint);
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        if (ShowsButtons && (GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint)
            || GetCloseButtonRect(contentWidth, onLeft).Contains(contentPoint)))
            return HTCLIENT;

        if (ShowsButtons)
        {
            // Same "margin becomes a move handle only once activated" pattern as FenceForm.HitTest -
            // see its own comment for why.
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

        // HTBORDER, not HTCAPTION - a left-button drag from the title row itself doesn't move the
        // widget (only the margin does, once active - see above); right-click/double-click/hover
        // still work (see HTBORDER's own comment on LayeredWidgetForm).
        if (!_model.HideTitle && y - TopBand <= HeaderHeight)
            return HTBORDER;

        return HTCLIENT;
    }

    /// <summary>Chained immediately inside the Settings button, same pattern as FenceForm's own
    /// GetNewFenceButtonRect/GetDeleteButtonRect - closes (hides) the widget when clicked.</summary>
    private Rectangle GetCloseButtonRect(int contentWidth, bool onLeft)
    {
        var settingsRect = GetSettingsButtonRect(contentWidth, onLeft);
        var x = onLeft ? settingsRect.Right + ButtonSpacing : settingsRect.X - ButtonSpacing - CloseButtonSize;
        return new Rectangle(x, settingsRect.Y, CloseButtonSize, SettingsButtonHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
            _settingsButtonArmed = true;
        else if (ShowsButtons && GetCloseButtonRect(contentWidth, onLeft).Contains(contentPoint))
            _closeButtonArmed = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(ToContent(e.Location)))
                OpenSettingsMenu();
        }
        else if (_closeButtonArmed)
        {
            _closeButtonArmed = false;
            if (ShowsButtons && GetCloseButtonRect(contentWidth, onLeft).Contains(ToContent(e.Location)))
                HideAndPersist();
        }
    }

    protected override string Title
    {
        get => _model.Title;
        set
        {
            _model.Title = value;
            Persist();
        }
    }

    protected override int TitleRowHeight => HeaderHeight;

    protected override bool HideTitle
    {
        get => _model.HideTitle;
        set
        {
            _model.HideTitle = value;
            Persist();
            RenderAndPresent();
        }
    }

    // BuildSettingsRows/HandleSettingsCommand/TargetOpacity/EditBoxTextColor/EditBoxBackgroundColor/
    // ChromeMenuFieldColor/ChromeMenuHoverColor/SettingsMenu* are all LayeredWidgetForm's own default
    // now - this widget has nothing beyond Hide Title/Full Opacity/the color grid/sliders/margin
    // stepper to add, so the base's own default row list and command dispatch already cover it.

    /// <summary>LayeredWidgetForm's own required mutator hooks - plumbed straight to the model plus
    /// Persist(), the same pattern FenceForm's own overrides use via FenceManager instead.</summary>
    protected override void SetTintColor(Color? color, bool exact)
    {
        _model.TintColor = color?.ToArgb();
        _model.TintIsExact = exact;
        if (!exact)
        {
            _model.HeaderDarkness = LayoutLauncherModel.DefaultHeaderDarkness;
            _model.Opacity = LayoutLauncherModel.DefaultOpacity;
            _model.TintStrength = LayoutLauncherModel.DefaultTintStrength;
        }
        Persist();
    }

    protected override void SetHeaderDarkness(int darkness)
    {
        _model.HeaderDarkness = darkness;
        Persist();
        RenderAndPresent();
    }

    protected override void SetOpacity(int opacity)
    {
        _model.Opacity = Math.Max(5, opacity);
        Persist();
    }

    protected override void SetTintStrength(int strength)
    {
        _model.TintStrength = strength;
        Persist();
        RenderAndPresent();
    }

    protected override void SetMargin(int margin)
    {
        _model.Margin = margin;
        Persist();
        RenderAndPresent();
    }

    protected override void SetCornerRadius(int radius)
    {
        _model.CornerRadius = radius;
        Persist();
        RenderAndPresent();
    }

    protected override void SetTitleFontSize(int size)
    {
        _model.TitleFontSize = size;
        Persist();
        RenderAndPresent();
    }

    protected override void SetTitleAlignment(TitleAlignment alignment)
    {
        _model.TitleAlignment = alignment;
        Persist();
        RenderAndPresent();
    }

    protected override void SetFullOpacityOnHover(bool enabled)
    {
        _model.FullOpacityOnHover = enabled;
        Persist();
    }

    /// <summary>Everything genuinely specific to this widget beyond LayeredWidgetForm's own
    /// PaintChrome (body/title/border/title-text/Settings button): just the Close button chained
    /// off it - the layout list itself comes back in a later step.</summary>
    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight)
    {
        PaintChrome(g, contentWidth, contentHeight);

        if (!ShowsButtons)
            return;

        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);
        var closeRect = ToWindow(GetCloseButtonRect(contentWidth, onLeft));
        using (var closePath = RoundedRectPath.Full(closeRect, 6))
        using (var closeFill = new SolidBrush(ChromeFill))
        {
            g.FillPath(closeFill, closePath);
            using var closeBorderPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
            g.DrawPath(closeBorderPen, closePath);
        }

        using var xPen = new Pen(Color.WhiteSmoke, 1.6f);
        var xCenterX = closeRect.X + closeRect.Width / 2f;
        var xCenterY = closeRect.Y + closeRect.Height / 2f;
        const float xHalfSize = 4.5f;
        g.DrawLine(xPen, xCenterX - xHalfSize, xCenterY - xHalfSize, xCenterX + xHalfSize, xCenterY + xHalfSize);
        g.DrawLine(xPen, xCenterX - xHalfSize, xCenterY + xHalfSize, xCenterX + xHalfSize, xCenterY - xHalfSize);
    }
}
