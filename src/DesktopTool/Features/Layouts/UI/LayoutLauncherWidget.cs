using System.Drawing.Text;
using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Layout Launcher" widget, rebuilt on LayeredWidgetForm - a second, independent proof that the
/// base's own move/resize/snap/rename/title-menu/Settings-button/theme/extra-button/content-button/
/// list chrome works for something that isn't a Fence. Chrome plus a Close button (chained off
/// Settings in the margin band, like a Fence's own close), a Manage Layouts.../Save Current Layout row
/// (drawn inside the body itself, pinned to its bottom edge, since they're this widget's own primary
/// actions rather than chrome controls - see GetContentButtons), and a scrollable list of every saved
/// profile's name filling the rest of the body (see GetListArea/PaintListRow) - Run/Copy/Delete per
/// row comes back in its own later step, verified on its own rather than bundled into this one.
/// Everything not genuinely specific to this widget (theme derivation, the Settings dropdown's default
/// rows, button/border/title/list painting) is LayeredWidgetForm's own now - see its own class comment.
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

    private readonly LayoutManager _manager;
    private readonly LayoutLauncherModel _model;
    private readonly LayoutLauncherStore _store;

    private bool _allowClose;
    private bool _settingsButtonArmed;

    /// <summary>Close (× - hides, same as the "x" a Fence's own delete button uses, but this widget
    /// is hidden rather than destroyed - see HideAndPersist) - LayeredWidgetForm's own ChromeButton
    /// mechanism instead of hand-rolled rect-chaining/paint/hit-test/arm-fire code. Built once rather
    /// than a fresh list/delegate pair on every paint/hit-test call.</summary>
    protected override IReadOnlyList<ChromeButton> ExtraButtons { get; }

    /// <summary>Guid? carries a freshly-captured profile's Id up to TrayApplicationContext.
    /// OpenLayoutEditor - null from the Manage Layouts... button itself (there's no specific profile
    /// to jump to yet, just "open the editor"); a non-null value is for a future Save Current Layout
    /// button to jump straight to the profile it just captured.</summary>
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
        _model = model;
        _store = store;

        ExtraButtons = new List<ChromeButton>
        {
            new("×", 22, HideAndPersist),
        };

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
            // Not WS_EX_TOPMOST - same ordinary (non-always-on-top) window style as FenceForm's own
            // CreateParams, so this doesn't sit above every other app's window on screen forever; it
            // just behaves like any other normal top-level window (still WS_EX_TOOLWINDOW, so it has
            // no taskbar button/Alt-Tab entry of its own, matching a Fence).
            cp.ExStyle = 0x00000080 /* WS_EX_TOOLWINDOW */ | NativeMethods.WS_EX_LAYERED;
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

    protected override int SnapMargin => _model.Margin;

    // ComputeMovedBody/ComputeResizedBody/BeginSnapDrag/SupportsResize/ResizableEdges all use
    // LayeredWidgetForm's own defaults unchanged - full, unrestricted resize on every edge, snapping
    // against every other live widget's edges (fences, any future widget) and custom snap lines the
    // same as any other widget on this base (see LayeredWidgetForm.GetOtherWidgetEdges).

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
            || TryGetExtraButtonAt(contentWidth, onLeft, contentPoint, out _)))
            return HTCLIENT;

        // Manage Layouts... lives inside the body itself (see GetContentButtons) - already inside the
        // ordinary HTCLIENT territory below, so no extra carve-out is needed here, unlike the margin-
        // band Settings/extra buttons above.

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
        {
            _settingsButtonArmed = true;
            return;
        }
        if (ShowsButtons && TryArmExtraButton(contentPoint))
            return;
        if (TryArmContentButton(contentPoint))
            return;
        TryHandleListMouseDown(contentPoint);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateListScrollDrag(ToContent(e.Location));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleListMouseWheel(e.Delta);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var contentPoint = ToContent(e.Location);
        var contentWidth = GetContentSize().Width;
        var onLeft = ShouldSettingsButtonOpenLeft(contentWidth);

        if (_settingsButtonArmed)
        {
            _settingsButtonArmed = false;
            if (ShowsButtons && GetSettingsButtonRect(contentWidth, onLeft).Contains(contentPoint))
                OpenSettingsMenu();
            return;
        }

        FireArmedExtraButton(contentPoint);
        FireArmedContentButton(contentPoint);
        EndListScrollDrag();
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

    // TargetOpacity/EditBoxTextColor/EditBoxBackgroundColor/ChromeMenuFieldColor/ChromeMenuHoverColor/
    // SettingsMenu* are all LayeredWidgetForm's own default now. Every IWidgetStyle property (color,
    // Header Darkness, Opacity, Full Opacity When Active, Tint Strength, Margin, Corner Radius, Font
    // Size, Align, Header Border Mode) is mutated directly against Style (== _model) by the base
    // itself now - this widget doesn't need a dedicated SetHeaderDarkness/SetOpacity/etc. override of
    // its own for any of them, just this one persistence hook.
    protected override void PersistStyle() => Persist();

    /// <summary>The only thing genuinely specific to this widget beyond the base's own default rows -
    /// Rows Shown only means anything for a widget with a row list, so it doesn't belong in
    /// LayeredWidgetForm's own shared "Base" flyout. A stepper (same interface as Margin/Corner
    /// Radius/Font Size) rather than a checkbox - see LayoutLauncherModel.RowsShown's own doc comment
    /// for why this replaced the resize-drag row-snapping it used to be.</summary>
    protected override IReadOnlyList<DropdownMenu.Row>? BuildAdditionalSettingsRows() => new List<DropdownMenu.Row>
    {
        new(0, "Rows Shown", IsHeader: true),
        new(0, string.Empty, IsStepper: true,
            StepperValue: () => _model.RowsShown,
            OnStepperChange: rows =>
            {
                _model.RowsShown = Math.Clamp(rows, 1, 20);
                Persist();
                RenderAndPresent();
            },
            StepperMin: 1, StepperMax: 20, StepperStep: 1, StepperSuffix: ""),
    };

    /// <summary>Manage Layouts.../Save Current Layout sit inside the body itself, pinned to its bottom
    /// edge, rather than chained off Settings in the margin band - they're this widget's own primary
    /// actions (the whole point of the launcher), not chrome controls like Close/Settings, so they
    /// should read as part of the widget's own surface and stay visible/clickable regardless of
    /// activation state. Side by side via LayeredWidgetForm.LayoutRow when they both fit, stacked
    /// otherwise (a narrow widget) - stacking grows upward from the bottom so the row still reads as
    /// anchored there either way. Bottom-pinned rather than centered so the real saved-layout list
    /// (coming back in a later step) has the rest of the body, above this row, to itself.</summary>
    // Shared by GetContentButtons (the row itself) and GetListArea (which needs to know how tall
    // that row turns out so the list can stop just above it) - kept together so the two can never
    // drift out of sync with each other.
    private const int BottomRowHeight = 26;
    private const int BottomRowGap = 8;
    private const int BottomRowBottomPadding = 12;
    private static readonly int[] BottomRowWidths = { 120, 150 };

    protected override IReadOnlyList<ContentButton> GetContentButtons(int contentWidth, int contentHeight)
    {
        var top = contentHeight - BottomRowBottomPadding - RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);
        var rects = LayoutRow(contentWidth, top, BottomRowHeight, BottomRowGap, BottomRowWidths);

        return new[]
        {
            new ContentButton("Manage Layouts...", rects[0], () => ManageLayoutsRequested?.Invoke(this, null)),
            new ContentButton("Save Current Layout", rects[1], SaveCurrentLayout),
        };
    }

    /// <summary>Captures whatever's actually open and where it's actually sitting right now (see
    /// LayoutManager.CaptureCurrentLayout) into a freshly named profile, then opens the editor jumped
    /// straight to it - same ManageLayoutsRequested event Manage Layouts... itself fires, just with
    /// the new profile's Id instead of null (see the event's own doc comment).</summary>
    private void SaveCurrentLayout()
    {
        var profile = _manager.CaptureCurrentLayout($"Layout {_manager.Profiles.Count + 1}");
        ManageLayoutsRequested?.Invoke(this, profile.Id);
    }

    private const int ListVerticalPadding = 12;
    private const int ListHorizontalPadding = 10;

    /// <summary>Everything in the body that isn't the list's own rows - header (if shown), the list's
    /// own top/bottom padding, and the Manage Layouts.../Save Current Layout row below it. Shared with
    /// GetListArea so the two formulas can never drift out of sync with each other.</summary>
    private int NonListOverhead(int contentWidth) =>
        (_model.HideTitle ? 0 : HeaderHeight) + ListVerticalPadding * 2
        + BottomRowBottomPadding + RowHeight(contentWidth, BottomRowHeight, BottomRowGap, BottomRowWidths);

    /// <summary>Everything between the header and the Manage Layouts.../Save Current Layout row - the
    /// list itself never grows taller than min(RowsShown, actual saved profile count) rows (see
    /// LayoutLauncherModel.RowsShown), so it neither wastes space showing fewer profiles than that nor
    /// grows past the user's own chosen viewport size; a taller body than that just leaves blank space
    /// below the list rather than stretching it, and more profiles than RowsShown scrolls instead of
    /// growing further.</summary>
    protected override Rectangle GetListArea(int contentWidth, int contentHeight)
    {
        var top = (_model.HideTitle ? 0 : HeaderHeight) + ListVerticalPadding;
        var available = contentHeight - NonListOverhead(contentWidth);
        var wanted = Math.Min(_model.RowsShown, ListRowCount) * ListRowHeight;
        var height = Math.Max(0, Math.Min(available, wanted));
        return new Rectangle(ListHorizontalPadding, top, contentWidth - ListHorizontalPadding * 2, height);
    }

    protected override int ListRowCount => _manager.Profiles.Count;
    protected override int ListRowHeight => 24;

    /// <summary>Just the name for now - Run/Copy/Delete per row comes back in a later step, verified
    /// on its own rather than bundled into the list mechanism itself. Alternates ThemedField/
    /// ThemedFieldDark by index so rows read as banded rather than one flat surface.</summary>
    protected override void PaintListRow(Graphics g, int index, Rectangle rowRect)
    {
        using (var rowFill = new SolidBrush(index % 2 == 0 ? ThemedField : ThemedFieldDark))
            g.FillRectangle(rowFill, rowRect);

        var previousTextHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var textBrush = new SolidBrush(Color.WhiteSmoke))
        using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            var textRect = new RectangleF(rowRect.X + 8, rowRect.Y, rowRect.Width - 16, rowRect.Height);
            g.DrawString(_manager.Profiles[index].Name, Font, textBrush, textRect, textFormat);
        }
        g.TextRenderingHint = previousTextHint;
    }

    /// <summary>Nothing genuinely specific to this widget left to paint on top - body/title/border/
    /// Settings/Close/Manage Layouts.../Save Current Layout/the list itself are all LayeredWidgetForm's
    /// own PaintChrome now (see ExtraButtons/GetContentButtons/GetListArea).</summary>
    protected override void PaintContent(Graphics g, int contentWidth, int contentHeight) =>
        PaintChrome(g, contentWidth, contentHeight);
}
