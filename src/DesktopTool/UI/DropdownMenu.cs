using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>
/// A persistent replacement for the native TrackPopupMenuEx-based fence-options dropdown (see
/// FenceForm.ShowFenceOptionsMenu) - a real Win32 popup menu unconditionally closes itself the
/// instant any item is clicked, with no flag to opt out, so flipping several checkboxes in a row
/// meant reopening the menu every single time. This is a plain WinForms Form instead: clicking a
/// row raises ItemClicked and stays open, and it only closes when it loses activation (see
/// OnDeactivate) - i.e. an actual click outside it, including on the fence that opened it.
///
/// Square corners rather than matching the fence's rounded body - the *native* menu this replaces
/// was square too (Windows draws a plain popup's outer shape; the old owner-draw hook only painted
/// each row's own background), so this isn't a visual downgrade from what was there before.
/// </summary>
internal sealed class DropdownMenu : Form
{
    /// <summary>What to draw inside a Swatch-less grid item (see DrawGridItem) - None for a plain
    /// empty outline, which no current row actually uses (both "pick a new color" cases get their own
    /// glyph instead of reading as just an empty/unset state).</summary>
    public enum GridGlyph { None, Plus, Eyedropper }

    public sealed record Row(
        int Id,
        string Text,
        bool IsHeader = false,
        bool IsSeparator = false,
        bool HasCheckbox = false,
        Color? Swatch = null,
        bool IsGridItem = false,
        Func<bool>? IsChecked = null,
        string? Tooltip = null,
        // Only meaningful when IsGridItem and Swatch is null - which glyph marks this cell as "pick a
        // new color" rather than an existing one (see DrawGridItem).
        GridGlyph Glyph = GridGlyph.None,
        // Non-null turns this row into a flyout opener instead of a command - clicking it toggles a
        // second DropdownMenu built from these rows open/closed (see OnMouseUp/OpenSubmenu) instead
        // of firing ItemClicked. Id is unused for these rows.
        IReadOnlyList<Row>? Submenu = null,
        // IsSlider turns this row into a draggable track+thumb instead of a command row - SliderValue
        // is read fresh on every paint (0.0-1.0, same live-callback pattern as IsChecked) and
        // OnSliderChange fires directly (not through ItemClicked) on mouse-down and while dragging -
        // see OnMouseDown/OnMouseMove/UpdateSliderFromMouseX. Id/Text are unused for these rows; the
        // label lives in a preceding IsHeader row instead (see FenceForm.BuildOptionsMenuRows).
        bool IsSlider = false,
        Func<double>? SliderValue = null,
        Action<double>? OnSliderChange = null,
        // IsStepper turns this row into a "- value +" row: owner-drawn like everything else here
        // (a real embedded NumericUpDown was tried first, but its native spinner chrome doesn't
        // match this menu's own dark theme the way every hand-painted row does) - a plain click on
        // either button steps StepperValue by StepperStep, clamped to StepperMin/Max. StepperValue
        // is read fresh on every paint, same live-callback pattern as SliderValue/IsChecked.
        // Id/Text are unused for these rows; the label lives in a preceding IsHeader row instead.
        bool IsStepper = false,
        Func<int>? StepperValue = null,
        Action<int>? OnStepperChange = null,
        int StepperMin = 0,
        int StepperMax = 100,
        int StepperStep = 1,
        string StepperSuffix = "");

    private const int RowPadding = 8;
    private const int CheckboxSize = 12;
    private const int SeparatorHeight = 9;
    private const int MinRowHeight = 22;
    private const int MinWidth = 120;
    private const int SliderRowHeight = 28;
    private const int SliderTrackHeight = 4;
    private const int SliderThumbSize = 12;
    private const int StepperButtonSize = 20;

    // A run of consecutive IsGridItem rows (see MeasureLayout/FenceForm.ShowFenceOptionsMenu's color
    // rows) lays out as a fixed-column grid of circles instead of one full-width row each - the
    // fence-color picker was exactly GridColumns * 2 items (Default + 8 presets + Custom) for a clean
    // 5x2 block before Eyedropper made it 11; a lopsided last row for one extra item beats forcing a
    // whole different column count just to stay evenly divisible.
    private const int GridColumns = 5;
    private const int GridCellHeight = 32;
    private const int GridCircleSize = 20;

    private readonly List<Row> _rows;
    private readonly List<Rectangle> _rowRects = new();
    private readonly Font _font;
    private readonly Func<Color> _getBody;
    private readonly Func<Color> _getSelected;
    private readonly Func<Color> _getAccent;
    private readonly Func<Color> _getCheckboxBorder;
    private readonly Func<Color> _getTooltipColor;
    // OwnerDraw, not just BackColor/ForeColor: on a themed (UxTheme) system - i.e. basically always -
    // a plain ToolTip draws itself natively and ignores BackColor/ForeColor entirely, the same reason
    // the old raw native tooltip needed SetWindowTheme(hwnd, "", "") to opt out of theming before its
    // own TTM_SETTIPBKCOLOR/TTM_SETTIPTEXTCOLOR would take effect (see git history). OwnerDraw is
    // this class's equivalent opt-out - see the Draw handler, wired up in the constructor below.
    private readonly ToolTip _toolTip = new() { OwnerDraw = true };
    private int _hoverIndex = -1;
    private int _tooltipRowIndex = -1;
    private bool _preferLeft;
    // Which side this menu actually ended up opening on relative to its own anchor button (not
    // necessarily the same as the preferLeft it was asked for - ComputeBounds falls back to the
    // opposite side, or clamps, when the preferred one doesn't fit) - set alongside Bounds itself, in
    // both the constructor and RepositionRelativeTo. Read by UpdateTooltip, which keeps a row's own
    // tooltip on this same side rather than letting it point some other way on its own - see its own
    // comment for why that's always already correct rather than something decided here.
    private bool _actualLeft;
    // Set only on a submenu instance, pointing back to whichever DropdownMenu opened it (see
    // OpenSubmenu) - forms the chain OnDeactivate walks (via Root/IsInFamily) to tell "focus moved to
    // one of my own flyouts" apart from "focus moved somewhere else entirely, close everything".
    private DropdownMenu? _parent;
    // At most one open at a time - hovering a different row (see UpdateSubmenu) closes whatever was
    // open before opening the next one, so this doesn't need to be a collection.
    private DropdownMenu? _submenu;
    private int _submenuRowIndex = -1;
    // -1 when nothing's being dragged - set on mouse-down over a slider's track/thumb (see
    // OnMouseDown), cleared on the matching mouse-up. While set, OnMouseMove updates the slider
    // instead of hover/tooltip/submenu state.
    private int _sliderDragRowIndex = -1;

    /// <summary>Fired on the matching mouse-up for a click on a non-header, non-separator row - the
    /// menu does not close itself in response; the caller decides what the id means and calls
    /// RefreshChecks() afterward if anything the menu displays (a checkbox, a color ring) changed.</summary>
    public event Action<int>? ItemClicked;

    // Internal rather than private - FenceForm.ShouldSettingsButtonOpenLeft needs the same gap value
    // to decide the button's corner ahead of time, and it must match the constructor's own fit-check
    // exactly or the two could disagree about which side actually has room. A couple pixels more than
    // the bare minimum, same reasoning as FenceForm.SettingsButtonGap - a visible gap between the
    // button and the menu instead of them touching.
    internal const int AnchorGap = 4;

    // How far inset from the working area's own edge a clamped position (see ComputeBounds) stops,
    // instead of landing flush against it - butted right up against the screen/taskbar edge with zero
    // gap reads as the menu being cut off rather than deliberately placed, especially clamped to the
    // bottom with no background visible underneath it at all.
    private const int EdgeMargin = 4;

    /// <summary>anchorScreenRect is the settings button's bounds in screen coordinates - same
    /// convention as the PointToScreen(...) call the old TrackPopupMenuEx-based version used, just a
    /// rect instead of a single point so this can decide which side of the button to open on (see
    /// below) instead of the caller baking that choice in. preferLeft is only true for callers that
    /// want the menu to open leftward whenever it fits, rather than as a last resort - the settings
    /// menu (see FenceForm.ShowFenceOptionsMenu) passes false, since the button sits flush with the
    /// fence's top-right corner and opening right is what keeps the menu off the fence by default;
    /// left only kicks in there once right would run off the screen. The five Func&lt;Color&gt;
    /// callbacks are re-invoked on every repaint rather than snapshotted once, since the fence's own
    /// tint (and so its accent/body/tooltip colors) can change while this is open - picking a color
    /// no longer closes the menu first.</summary>
    public DropdownMenu(IEnumerable<Row> rows, Rectangle anchorScreenRect, bool preferLeft, Font font,
        Func<Color> getBody, Func<Color> getSelected, Func<Color> getAccent, Func<Color> getCheckboxBorder, Func<Color> getTooltipColor)
    {
        _rows = rows.ToList();
        _font = font;
        _getBody = getBody;
        _getSelected = getSelected;
        _getAccent = getAccent;
        _getCheckboxBorder = getCheckboxBorder;
        _getTooltipColor = getTooltipColor;
        _preferLeft = preferLeft;
        _toolTip.Draw += DrawTooltip;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;

        Bounds = ComputeBounds(anchorScreenRect, MeasureLayout(), preferLeft);
        _actualLeft = Bounds.X + Bounds.Width / 2 < anchorScreenRect.X + anchorScreenRect.Width / 2;
    }

    /// <summary>Re-anchors an already-open menu to its button - used when the fence's own window
    /// moves or resizes out from under it without this menu otherwise knowing (see
    /// FenceForm.RepositionDropdown - e.g. an OCD flyout resize command changes the fence's bounds,
    /// which normally has nothing to do with this menu at all). preferLeft is passed fresh each time
    /// rather than reusing the stored value, since a resize can change which corner the settings
    /// button itself prefers (see FenceForm.ShouldSettingsButtonOpenLeft) - staying in sync with that
    /// keeps the button and the menu on the same side the way they started. Cascades to an open
    /// submenu too, so it stays anchored to its own (also now-moved) opener row.</summary>
    public void RepositionRelativeTo(Rectangle anchorScreenRect, bool preferLeft)
    {
        _preferLeft = preferLeft;
        Bounds = ComputeBounds(anchorScreenRect, MeasureLayout(), preferLeft);
        _actualLeft = Bounds.X + Bounds.Width / 2 < anchorScreenRect.X + anchorScreenRect.Width / 2;
        if (_submenu is { IsDisposed: false } submenu)
            submenu.RepositionRelativeTo(RectangleToScreen(_rowRects[_submenuRowIndex]), preferLeft);
    }

    private static Rectangle ComputeBounds(Rectangle anchorScreenRect, Size size, bool preferLeft)
    {
        var workingArea = Screen.FromRectangle(anchorScreenRect).WorkingArea;

        // Try the preferred side first, falling back to the opposite side if it doesn't have room -
        // e.g. a fence near the left edge of the screen still needs the menu to flip back to the
        // right even though left is normally preferred. If neither side fully fits (a very narrow
        // screen), fall back to clamping within the working area same as the vertical axis.
        bool LeftFits() => anchorScreenRect.Left - AnchorGap - size.Width >= workingArea.Left;
        bool RightFits() => anchorScreenRect.Right + AnchorGap + size.Width <= workingArea.Right;
        int LeftX() => anchorScreenRect.Left - AnchorGap - size.Width;
        int RightX() => anchorScreenRect.Right + AnchorGap;

        int x;
        if (preferLeft)
            x = LeftFits() ? LeftX() : RightFits() ? RightX() : Math.Max(workingArea.Left + EdgeMargin, workingArea.Right - size.Width - EdgeMargin);
        else
            x = RightFits() ? RightX() : LeftFits() ? LeftX() : Math.Max(workingArea.Left + EdgeMargin, workingArea.Right - size.Width - EdgeMargin);

        var y = Math.Max(workingArea.Top + EdgeMargin, Math.Min(anchorScreenRect.Y, workingArea.Bottom - size.Height - EdgeMargin));
        return new Rectangle(x, y, size.Width, size.Height);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab
            return cp;
        }
    }

    /// <summary>Losing activation means a click (or some other window taking focus) landed outside
    /// this menu - including on the fence itself, since it's a separate top-level window from this
    /// one - UNLESS that focus moved to one of this menu's own flyout submenus (see Root/IsInFamily),
    /// which is expected and shouldn't close anything. Deferred via BeginInvoke rather than closing
    /// inline: a row's own click handler can synchronously show a modal dialog (Custom... color
    /// picker, the Delete Fence confirmation), which deactivates this menu *while that same handler
    /// is still running* - closing/disposing this Form reentrantly out from under its own
    /// still-executing OnMouseUp would be the same hazard FenceManager.DeleteFence already works
    /// around for FenceForm itself. It also gives whatever just got activated time to actually
    /// register as ActiveForm before IsInFamily checks it.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (IsDisposed)
            return;
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && !IsInFamily(ActiveForm))
                Root.CloseFamily();
        }));
    }

    /// <summary>Walks up through _parent to the top-level menu FenceForm actually opened - the one
    /// whose whole flyout chain should live or die together.</summary>
    private DropdownMenu Root => _parent?.Root ?? this;

    /// <summary>True if form is this menu's root, or anywhere along the root's chain of open
    /// submenus - i.e. still part of the same cascading menu, even though each level is a separate
    /// top-level Form with its own activation.</summary>
    private bool IsInFamily(Form? form)
    {
        for (var node = Root; node is not null; node = node._submenu)
            if (ReferenceEquals(node, form))
                return true;
        return false;
    }

    /// <summary>Closes this menu and, first, whatever submenu it has open - deepest first, so a
    /// FormClosed handler further up never runs against an already-disposed child.</summary>
    private void CloseFamily()
    {
        _submenu?.CloseFamily();
        if (!IsDisposed)
            Close();
    }

    /// <summary>Repaints to reflect any checkbox/slider/color-ring state a just-handled ItemClicked
    /// may have changed - the menu doesn't know what a given id means, so it can't tell on its own.
    /// Update(), not just Invalidate() - this runs synchronously inside the same OnMouseUp that fired
    /// ItemClicked (see its caller), and FenceForm's own handler for that event goes on to do real
    /// work of its own (writing to disk, re-rendering the fence) before returning - forcing the
    /// repaint through right here keeps this menu's own pixels from visibly lagging behind whatever
    /// just changed instead of waiting for the message loop to get back around to this window's
    /// queued WM_PAINT on its own. Cascades to an open submenu too, in case it displays state that
    /// changed as well.</summary>
    public void RefreshChecks()
    {
        Invalidate();
        Update();
        _submenu?.RefreshChecks();
    }

    /// <summary>Measures what a menu built from these rows would be sized, without actually
    /// constructing one - used by FenceForm.ShouldSettingsButtonOpenLeft to decide which corner the
    /// settings button belongs in before the real DropdownMenu exists to measure.</summary>
    public static Size Measure(IEnumerable<Row> rows, Font font) => LayoutRows(rows.ToList(), font).Size;

    /// <summary>The widest a row's own tooltip pill would render (same +16 padding UpdateTooltip
    /// itself adds), across every row that has one - 0 if none do. Also used by
    /// FenceForm.ShouldSettingsButtonOpenLeft, alongside Measure, so the button/menu side is decided
    /// with the widest tooltip already accounted for before anything actually opens, rather than
    /// discovering mid-hover that a particular tooltip needs more room than the menu alone did and
    /// having to flip everything live at that point (which read as a jarring on-screen jump).</summary>
    public static int MaxTooltipWidth(IEnumerable<Row> rows, Font font)
    {
        var max = 0;
        foreach (var row in rows)
        {
            if (row.Tooltip is not { } text)
                continue;
            max = Math.Max(max, TextRenderer.MeasureText(text, font).Width + 16);
        }
        return max;
    }

    private Size MeasureLayout()
    {
        var (size, rowRects) = LayoutRows(_rows, _font);
        _rowRects.Clear();
        _rowRects.AddRange(rowRects);
        return size;
    }

    private static (Size Size, List<Rectangle> RowRects) LayoutRows(List<Row> rows, Font font)
    {
        // Grid items don't factor into the width pass below (their own cells just divide up
        // whatever width the regular rows end up needing) - only a floor to keep GridColumns
        // circles from ever being cramped narrower than they'd need even if every other row's text
        // happened to be short.
        var maxWidth = GridColumns * (GridCircleSize + RowPadding);

        foreach (var row in rows)
        {
            if (row.IsGridItem || row.IsSeparator)
                continue;
            var textSize = TextRenderer.MeasureText(row.Text, font);
            var leftReserve = row.HasCheckbox || row.Swatch is not null ? CheckboxSize + RowPadding : 0;
            var rightReserve = row.Submenu is not null ? CheckboxSize + RowPadding : 0;
            maxWidth = Math.Max(maxWidth, RowPadding + leftReserve + textSize.Width + rightReserve + RowPadding);
        }

        var width = Math.Max(MinWidth, maxWidth) + 2; // + left/right 1px borders

        var rowRects = new List<Rectangle>();
        int y = 1; // 1px top border
        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].IsGridItem)
            {
                var start = i;
                while (i < rows.Count && rows[i].IsGridItem)
                    i++;
                var count = i - start;
                var cellWidth = (width - 2) / GridColumns;
                for (var j = 0; j < count; j++)
                {
                    var col = j % GridColumns;
                    var gridRow = j / GridColumns;
                    rowRects.Add(new Rectangle(1 + col * cellWidth, y + gridRow * GridCellHeight, cellWidth, GridCellHeight));
                }
                y += ((count + GridColumns - 1) / GridColumns) * GridCellHeight;
                continue;
            }

            var row = rows[i];
            var height = row.IsSeparator ? SeparatorHeight
                : row.IsSlider || row.IsStepper ? SliderRowHeight
                : Math.Max(TextRenderer.MeasureText(row.Text, font).Height + 8, MinRowHeight);
            rowRects.Add(new Rectangle(1, y, width - 2, height));
            y += height;
            i++;
        }

        return (new Size(width, y + 1), rowRects); // + bottom border
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var background = new SolidBrush(_getBody()))
            g.FillRectangle(background, ClientRectangle);
        using (var borderPen = new Pen(Color.FromArgb(255, 20, 20, 24)))
            g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        for (var i = 0; i < _rows.Count; i++)
            DrawRow(g, i);
    }

    private void DrawRow(Graphics g, int index)
    {
        var row = _rows[index];
        var rect = _rowRects[index];

        if (row.IsGridItem)
        {
            DrawGridItem(g, row, rect, index == _hoverIndex);
            return;
        }

        if (row.IsSeparator)
        {
            using var pen = new Pen(Color.FromArgb(60, 255, 255, 255));
            var midY = rect.Y + rect.Height / 2;
            g.DrawLine(pen, rect.X + RowPadding, midY, rect.Right - RowPadding, midY);
            return;
        }

        if (row.IsStepper)
        {
            DrawStepper(g, row, rect);
            return;
        }

        if (row.IsSlider)
        {
            DrawSlider(g, row, rect);
            return;
        }

        var selected = !row.IsHeader && index == _hoverIndex;
        using (var background = new SolidBrush(selected ? _getSelected() : _getBody()))
            g.FillRectangle(background, rect);

        var isChecked = row.IsChecked?.Invoke() ?? false;

        if (row.HasCheckbox)
        {
            var checkRect = new Rectangle(rect.X + RowPadding, rect.Y + (rect.Height - CheckboxSize) / 2, CheckboxSize, CheckboxSize);
            using (var checkPen = new Pen(_getCheckboxBorder()))
                g.DrawRectangle(checkPen, checkRect);

            if (isChecked)
            {
                using var checkMarkPen = new Pen(_getAccent(), 2);
                g.DrawLine(checkMarkPen, checkRect.X + 2, checkRect.Y + 6, checkRect.X + 5, checkRect.Y + 9);
                g.DrawLine(checkMarkPen, checkRect.X + 5, checkRect.Y + 9, checkRect.X + 10, checkRect.Y + 2);
            }
        }
        else if (row.Swatch is { } swatchColor)
        {
            var swatchRect = new Rectangle(rect.X + RowPadding, rect.Y + (rect.Height - CheckboxSize) / 2, CheckboxSize, CheckboxSize);
            using (var swatchBrush = new SolidBrush(swatchColor))
                g.FillEllipse(swatchBrush, swatchRect);

            using var swatchPen = new Pen(isChecked ? _getAccent() : _getCheckboxBorder(), isChecked ? 2 : 1);
            g.DrawEllipse(swatchPen, swatchRect);
        }

        var textLeft = rect.X + RowPadding + (row.HasCheckbox || row.Swatch is not null ? CheckboxSize + RowPadding : 0);
        var rightReserve = row.Submenu is not null ? CheckboxSize + RowPadding : RowPadding;
        var textRect = new Rectangle(textLeft, rect.Y, Math.Max(0, rect.Right - rightReserve - textLeft), rect.Height);
        // Same WhiteSmoke and plain weight as every other row - a header is set apart by a following
        // separator row (see FenceForm.BuildOptionsMenuRows) rather than its own font styling.
        TextRenderer.DrawText(g, row.Text, _font, textRect, Color.WhiteSmoke,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

        if (row.Submenu is not null)
        {
            // A plain right-pointing triangle - same "there's more over here" convention as a native
            // menu's submenu arrow, just hand-drawn since this row isn't a real MENUITEMINFO.
            var cx = rect.Right - RowPadding - CheckboxSize / 2;
            var cy = rect.Y + rect.Height / 2;
            using var arrowBrush = new SolidBrush(Color.FromArgb(255, 190, 190, 196));
            g.FillPolygon(arrowBrush, new[]
            {
                new Point(cx - 3, cy - 4),
                new Point(cx - 3, cy + 4),
                new Point(cx + 3, cy),
            });
        }
    }

    /// <summary>A single cell in the color grid - a filled, outlined circle for a real color, or (see
    /// Row.Swatch being null, e.g. "Custom..."/"Eyedropper") just the outline with Row.Glyph drawn
    /// inside instead of a fill, marking it as "pick a new one" rather than reading as just another
    /// empty/unset state the way the checkbox rows above it do.</summary>
    private void DrawGridItem(Graphics g, Row row, Rectangle rect, bool hovered)
    {
        if (hovered)
            using (var hoverBrush = new SolidBrush(_getSelected()))
                g.FillRectangle(hoverBrush, rect);

        var circleRect = new Rectangle(rect.X + (rect.Width - GridCircleSize) / 2, rect.Y + (rect.Height - GridCircleSize) / 2,
            GridCircleSize, GridCircleSize);

        if (row.Swatch is { } swatchColor)
        {
            using var swatchBrush = new SolidBrush(swatchColor);
            g.FillEllipse(swatchBrush, circleRect);
        }
        else if (row.Glyph == GridGlyph.Plus)
        {
            var cx = circleRect.X + circleRect.Width / 2f;
            var cy = circleRect.Y + circleRect.Height / 2f;
            const float halfLength = 4.5f;
            using var plusPen = new Pen(_getCheckboxBorder(), 1.5f);
            g.DrawLine(plusPen, cx - halfLength, cy, cx + halfLength, cy);
            g.DrawLine(plusPen, cx, cy - halfLength, cx, cy + halfLength);
        }
        else if (row.Glyph == GridGlyph.Eyedropper)
        {
            // A simplified pipette: a diagonal shaft from the circle's upper-right down to a filled
            // tip at the lower-left, the same "drop" a real eyedropper leaves.
            using var dropperPen = new Pen(_getCheckboxBorder(), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var x1 = circleRect.Right - 5f;
            var y1 = circleRect.Y + 5f;
            var x2 = circleRect.X + 6f;
            var y2 = circleRect.Bottom - 6f;
            g.DrawLine(dropperPen, x1, y1, x2, y2);
            using var tipBrush = new SolidBrush(_getCheckboxBorder());
            g.FillEllipse(tipBrush, x2 - 1.5f, y2 - 1.5f, 3f, 3f);
        }

        var isChecked = row.IsChecked?.Invoke() ?? false;
        using var pen = new Pen(isChecked ? _getAccent() : _getCheckboxBorder(), isChecked ? 2 : 1);
        g.DrawEllipse(pen, circleRect);
    }

    /// <summary>A horizontal track (see SliderTrack) with an Accent-filled portion up to the current
    /// value and a thumb circle at that position - same shape/weight as a swatch circle (DrawGridItem)
    /// for a consistent look. SliderValue is read fresh here every repaint, same live-callback pattern
    /// as IsChecked, so it reflects a change made via a different control instantly.</summary>
    private void DrawSlider(Graphics g, Row row, Rectangle rect)
    {
        var value = Math.Clamp(row.SliderValue?.Invoke() ?? 0.0, 0.0, 1.0);
        var track = SliderTrack(rect);

        using (var trackBrush = new SolidBrush(_getCheckboxBorder()))
            g.FillRectangle(trackBrush, track);

        var fillWidth = (int)Math.Round(track.Width * value);
        if (fillWidth > 0)
            using (var fillBrush = new SolidBrush(_getAccent()))
                g.FillRectangle(fillBrush, new Rectangle(track.X, track.Y, fillWidth, track.Height));

        var thumbRect = new Rectangle(track.X + fillWidth - SliderThumbSize / 2, rect.Y + (rect.Height - SliderThumbSize) / 2,
            SliderThumbSize, SliderThumbSize);
        using (var thumbBrush = new SolidBrush(_getAccent()))
            g.FillEllipse(thumbBrush, thumbRect);
        using var thumbPen = new Pen(Color.FromArgb(255, 20, 20, 24), 1f);
        g.DrawEllipse(thumbPen, thumbRect);
    }

    /// <summary>The draggable horizontal extent of a slider row, shared between DrawSlider and
    /// UpdateSliderFromMouseX so the visual track and the hit-tested/drag-mapped one are always the
    /// exact same rectangle.</summary>
    private static Rectangle SliderTrack(Rectangle rowRect) =>
        new(rowRect.X + RowPadding, rowRect.Y + (rowRect.Height - SliderTrackHeight) / 2, rowRect.Width - RowPadding * 2, SliderTrackHeight);

    /// <summary>"- value +": a minus button flush left, a plus button flush right, the current value
    /// centered between them - StepperValue read fresh here every repaint, same live-callback
    /// pattern as SliderValue/IsChecked.</summary>
    private void DrawStepper(Graphics g, Row row, Rectangle rect)
    {
        var (minusRect, plusRect) = StepperButtonRects(rect);
        DrawStepperButton(g, minusRect, isPlus: false);
        DrawStepperButton(g, plusRect, isPlus: true);

        var value = Math.Clamp(row.StepperValue?.Invoke() ?? 0, row.StepperMin, row.StepperMax);
        var textRect = new Rectangle(minusRect.Right, rect.Y, plusRect.Left - minusRect.Right, rect.Height);
        TextRenderer.DrawText(g, $"{value}{row.StepperSuffix}", _font, textRect, Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>A small outlined square with a +/- glyph - same crossed-line construction as
    /// DrawGridItem's Plus glyph, just without needing a whole circle around it.</summary>
    private void DrawStepperButton(Graphics g, Rectangle rect, bool isPlus) =>
        DrawPlusMinusGlyph(g, rect, isPlus, _getCheckboxBorder(), _getAccent(), 4.5f);

    /// <summary>Shared with SnapLinePanel's own numeric field spinner - same outlined-square-plus-glyph
    /// construction, just themed from different color sources (this menu's live getters vs.
    /// SnapLinePanel's fixed AppTheme colors) and a slightly different glyph size.</summary>
    internal static void DrawPlusMinusGlyph(Graphics g, Rectangle rect, bool isPlus, Color borderColor, Color glyphColor, float halfLength)
    {
        using (var pen = new Pen(borderColor))
            g.DrawRectangle(pen, rect);

        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        using var glyphPen = new Pen(glyphColor, 1.5f);
        g.DrawLine(glyphPen, cx - halfLength, cy, cx + halfLength, cy);
        if (isPlus)
            g.DrawLine(glyphPen, cx, cy - halfLength, cx, cy + halfLength);
    }

    /// <summary>Shared between DrawStepper and the click hit-testing in OnMouseDown, so the painted
    /// buttons and the clickable area are always the exact same rectangles.</summary>
    private static (Rectangle Minus, Rectangle Plus) StepperButtonRects(Rectangle rowRect)
    {
        var y = rowRect.Y + (rowRect.Height - StepperButtonSize) / 2;
        var minus = new Rectangle(rowRect.X + RowPadding, y, StepperButtonSize, StepperButtonSize);
        var plus = new Rectangle(rowRect.Right - RowPadding - StepperButtonSize, y, StepperButtonSize, StepperButtonSize);
        return (minus, plus);
    }

    private int RowAt(Point clientPoint)
    {
        for (var i = 0; i < _rowRects.Count; i++)
            if (!_rows[i].IsSeparator && !_rows[i].IsHeader && _rowRects[i].Contains(clientPoint))
                return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var index = RowAt(e.Location);
        if (index < 0)
            return;

        if (_rows[index].IsSlider)
        {
            _sliderDragRowIndex = index;
            // Keeps receiving MouseMove even once the cursor drags outside this row (or this whole
            // popup's bounds) - UpdateSliderFromMouseX already clamps, so there's no bound past which
            // dragging further has no effect, same feel as a native slider/scrollbar drag.
            Capture = true;
            UpdateSliderFromMouseX(index, e.X);
        }
        else if (_rows[index].IsStepper)
        {
            AdjustStepper(index, e.Location);
        }
    }

    /// <summary>A plain click-per-step on whichever button (if either) the click actually landed on -
    /// no press-and-hold repeat, same one-shot feel as every other row's click.</summary>
    private void AdjustStepper(int index, Point clientPoint)
    {
        var row = _rows[index];
        var (minusRect, plusRect) = StepperButtonRects(_rowRects[index]);
        var current = Math.Clamp(row.StepperValue?.Invoke() ?? 0, row.StepperMin, row.StepperMax);

        if (minusRect.Contains(clientPoint))
            row.OnStepperChange?.Invoke(Math.Max(row.StepperMin, current - row.StepperStep));
        else if (plusRect.Contains(clientPoint))
            row.OnStepperChange?.Invoke(Math.Min(row.StepperMax, current + row.StepperStep));
        else
            return;

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_sliderDragRowIndex >= 0)
        {
            UpdateSliderFromMouseX(_sliderDragRowIndex, e.X);
            return;
        }

        var index = RowAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
        UpdateTooltip(index);
    }

    /// <summary>Maps a mouse x-coordinate to a 0.0-1.0 slider value against SliderTrack's own extent,
    /// and fires the row's OnSliderChange with it - shared by the initial mouse-down (which also
    /// jumps straight to wherever was clicked, standard slider behavior) and every subsequent
    /// mouse-move while dragging.</summary>
    private void UpdateSliderFromMouseX(int index, int mouseX)
    {
        var track = SliderTrack(_rowRects[index]);
        var value = track.Width <= 0 ? 0.0 : Math.Clamp((mouseX - track.X) / (double)track.Width, 0.0, 1.0);
        _rows[index].OnSliderChange?.Invoke(value);
        Invalidate();
    }

    /// <summary>Mouse-leave fires as soon as the cursor crosses into the submenu popup itself (a
    /// separate HWND positioned right next to this row) - that's not "moved away from the submenu",
    /// it's "moved into it", so the opener row's highlight (and the submenu itself) both need to
    /// survive this, not just get cleared like every other row would.</summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var newHover = _submenu is { IsDisposed: false } ? _submenuRowIndex : -1;
        if (_hoverIndex != newHover)
        {
            _hoverIndex = newHover;
            Invalidate();
        }
        UpdateTooltip(-1);
    }

    private void OpenSubmenu(int rowIndex, IReadOnlyList<Row> submenuRows)
    {
        CloseSubmenu();

        var anchorScreenRect = RectangleToScreen(_rowRects[rowIndex]);
        var submenu = new DropdownMenu(submenuRows, anchorScreenRect, _preferLeft, _font,
            _getBody, _getSelected, _getAccent, _getCheckboxBorder, _getTooltipColor) { _parent = this };
        _submenu = submenu;
        _submenuRowIndex = rowIndex;
        // Bubbles both up an arbitrary chain (a submenu's own submenu, if this ever nests deeper) and
        // out to whatever FenceForm attached to the root's ItemClicked.
        submenu.ItemClicked += id => ItemClicked?.Invoke(id);
        submenu.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_submenu, submenu))
            {
                _submenu = null;
                _submenuRowIndex = -1;
            }
        };
        submenu.Show(this);
    }

    private void CloseSubmenu()
    {
        if (_submenu is { IsDisposed: false } submenu)
            submenu.Close();
        _submenu = null;
        _submenuRowIndex = -1;
    }

    private void UpdateTooltip(int index)
    {
        if (index == _tooltipRowIndex)
            return;
        _tooltipRowIndex = index;

        var text = index >= 0 ? _rows[index].Tooltip : null;
        if (text is null)
        {
            _toolTip.Hide(this);
            return;
        }

        // A row's own tooltip always extends the same direction this menu itself is open (see
        // _actualLeft) - kept visually attached rather than pointing some other way on its own.
        // Whether that side actually has room for the widest tooltip among these rows is decided
        // before any of this ever opens - FenceForm.ShouldSettingsButtonOpenLeft factors
        // DropdownMenu.MaxTooltipWidth into the same button/menu side it already picks, so the whole
        // button-and-buttons group, the menu, and every row's tooltip all end up on the correct side
        // together from the very first frame, rather than flipping live the moment a wide-tooltip row
        // happens to get hovered (which read as a jarring on-screen jump). What's left here is just a
        // defensive clamp for whatever that precalculation couldn't foresee (rows built or reworded
        // after the button/menu side was already locked in, or a genuinely too-narrow monitor) -
        // keeps the tooltip fully on-screen even then, which can mean overlapping the fence, but never
        // the edge of the monitor itself. Also sidesteps the flicker loop letting the *native*
        // tooltip control's own automatic on-screen repositioning handle it was causing (see git
        // history) - that was landing the relocated tooltip on top of the cursor, which this menu's
        // own hover tracking read as the cursor having left, hiding it, then immediately showing it
        // again next frame since the cursor was still right there.
        var rowRect = _rowRects[index];
        var tooltipWidth = TextRenderer.MeasureText(text, _font).Width + 16;
        var x = _actualLeft ? rowRect.Left - 4 - tooltipWidth : rowRect.Right + 4;

        var workingArea = Screen.FromControl(this).WorkingArea;
        var screenOriginX = PointToScreen(Point.Empty).X;
        var minX = workingArea.Left - screenOriginX;
        var maxX = Math.Max(minX, workingArea.Right - screenOriginX - tooltipWidth);
        x = Math.Clamp(x, minX, maxX);

        _toolTip.Show(text, this, x, rowRect.Y);
    }

    /// <summary>OwnerDraw's paint hook (see _toolTip's own field comment for why this is needed at
    /// all) - fetches _getTooltipColor() fresh on every draw rather than once, same live-theme
    /// reasoning as everything else this menu paints.</summary>
    private void DrawTooltip(object? sender, DrawToolTipEventArgs e)
    {
        using (var background = new SolidBrush(_getTooltipColor()))
            e.Graphics.FillRectangle(background, e.Bounds);
        using (var borderPen = new Pen(Color.FromArgb(255, 20, 20, 24)))
            e.Graphics.DrawRectangle(borderPen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, _font, e.Bounds, Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (_sliderDragRowIndex >= 0)
        {
            _sliderDragRowIndex = -1;
            Capture = false;
            return;
        }

        var index = RowAt(e.Location);
        if (index < 0)
            return;

        if (_rows[index].IsSlider || _rows[index].IsStepper)
            return;

        if (_rows[index].Submenu is { } submenuRows)
        {
            // A click toggles the flyout instead of dispatching a command (Row.Id is unused for these
            // rows) - clicking an already-open opener again closes it, rather than needing a click
            // elsewhere first.
            if (_submenuRowIndex == index && _submenu is { IsDisposed: false })
                CloseSubmenu();
            else
                OpenSubmenu(index, submenuRows);
            return;
        }

        ItemClicked?.Invoke(_rows[index].Id);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _submenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
