using System.Drawing.Drawing2D;

namespace FenceTool.UI;

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
    public sealed record Row(
        int Id,
        string Text,
        bool IsHeader = false,
        bool IsSeparator = false,
        bool HasCheckbox = false,
        Color? Swatch = null,
        bool IsGridItem = false,
        Func<bool>? IsChecked = null,
        string? Tooltip = null);

    private const int RowPadding = 8;
    private const int CheckboxSize = 12;
    private const int SeparatorHeight = 9;
    private const int MinRowHeight = 22;
    private const int MinWidth = 120;

    // A run of consecutive IsGridItem rows (see MeasureLayout/FenceForm.ShowFenceOptionsMenu's color
    // rows) lays out as a fixed-column grid of circles instead of one full-width row each - the
    // fence-color picker is exactly GridColumns * 2 items (Default + 8 presets + Custom), so this
    // always produces a clean 5x2 block rather than a lopsided last row.
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

    /// <summary>Fired on the matching mouse-up for a click on a non-header, non-separator row - the
    /// menu does not close itself in response; the caller decides what the id means and calls
    /// RefreshChecks() afterward if anything the menu displays (a checkbox, a color ring) changed.</summary>
    public event Action<int>? ItemClicked;

    /// <summary>screenLocation is where the menu's top-left corner should appear, in screen
    /// coordinates - same convention as the PointToScreen(...) call the old TrackPopupMenuEx-based
    /// version used. The five Func&lt;Color&gt; callbacks are re-invoked on every repaint rather than
    /// snapshotted once, since the fence's own tint (and so its accent/body/tooltip colors) can
    /// change while this is open - picking a color no longer closes the menu first.</summary>
    public DropdownMenu(IEnumerable<Row> rows, Point screenLocation, Font font,
        Func<Color> getBody, Func<Color> getSelected, Func<Color> getAccent, Func<Color> getCheckboxBorder, Func<Color> getTooltipColor)
    {
        _rows = rows.ToList();
        _font = font;
        _getBody = getBody;
        _getSelected = getSelected;
        _getAccent = getAccent;
        _getCheckboxBorder = getCheckboxBorder;
        _getTooltipColor = getTooltipColor;
        _toolTip.Draw += DrawTooltip;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;

        var size = MeasureLayout();
        Bounds = new Rectangle(screenLocation, size);
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
    /// one. Deferred via BeginInvoke rather than closing inline: a row's own click handler can
    /// synchronously show a modal dialog (Custom... color picker, the Delete Fence confirmation),
    /// which deactivates this menu *while that same handler is still running* - closing/disposing
    /// this Form reentrantly out from under its own still-executing OnMouseUp would be the same
    /// hazard FenceManager.DeleteFence already works around for FenceForm itself.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!IsDisposed)
            BeginInvoke(new Action(() => { if (!IsDisposed) Close(); }));
    }

    /// <summary>Repaints to reflect any checkbox/color-ring state a just-handled ItemClicked may have
    /// changed - the menu doesn't know what a given id means, so it can't tell on its own.</summary>
    public void RefreshChecks() => Invalidate();

    private Size MeasureLayout()
    {
        // Grid items don't factor into the width pass below (their own cells just divide up
        // whatever width the regular rows end up needing) - only a floor to keep GridColumns
        // circles from ever being cramped narrower than they'd need even if every other row's text
        // happened to be short.
        var maxWidth = GridColumns * (GridCircleSize + RowPadding);

        foreach (var row in _rows)
        {
            if (row.IsGridItem || row.IsSeparator)
                continue;
            var textSize = TextRenderer.MeasureText(row.Text, _font);
            var leftReserve = row.HasCheckbox || row.Swatch is not null ? CheckboxSize + RowPadding : 0;
            maxWidth = Math.Max(maxWidth, RowPadding + leftReserve + textSize.Width + RowPadding);
        }

        var width = Math.Max(MinWidth, maxWidth) + 2; // + left/right 1px borders

        _rowRects.Clear();
        int y = 1; // 1px top border
        int i = 0;
        while (i < _rows.Count)
        {
            if (_rows[i].IsGridItem)
            {
                var start = i;
                while (i < _rows.Count && _rows[i].IsGridItem)
                    i++;
                var count = i - start;
                var cellWidth = (width - 2) / GridColumns;
                for (var j = 0; j < count; j++)
                {
                    var col = j % GridColumns;
                    var gridRow = j / GridColumns;
                    _rowRects.Add(new Rectangle(1 + col * cellWidth, y + gridRow * GridCellHeight, cellWidth, GridCellHeight));
                }
                y += ((count + GridColumns - 1) / GridColumns) * GridCellHeight;
                continue;
            }

            var row = _rows[i];
            var height = row.IsSeparator ? SeparatorHeight : Math.Max(TextRenderer.MeasureText(row.Text, _font).Height + 8, MinRowHeight);
            _rowRects.Add(new Rectangle(1, y, width - 2, height));
            y += height;
            i++;
        }

        return new Size(width, y + 1); // + bottom border
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
        var textRect = new Rectangle(textLeft, rect.Y, Math.Max(0, rect.Right - RowPadding - textLeft), rect.Height);
        var textColor = row.IsHeader ? Color.FromArgb(255, 140, 140, 148) : Color.WhiteSmoke;
        TextRenderer.DrawText(g, row.Text, _font, textRect, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    /// <summary>A single cell in the color grid - a filled, outlined circle for a real color, or (see
    /// Row.Swatch being null, e.g. "Custom...") just the outline with nothing filled in, same as
    /// "empty" reads for the checkbox rows above it having nothing checked inside them.</summary>
    private void DrawGridItem(Graphics g, Row row, Rectangle rect, bool hovered)
    {
        if (hovered)
            using (var hoverBrush = new SolidBrush(_getSelected()))
                g.FillRectangle(hoverBrush, rect);

        var circleRect = new Rectangle(rect.X + (rect.Width - GridCircleSize) / 2, rect.Y + (rect.Height - GridCircleSize) / 2,
            GridCircleSize, GridCircleSize);

        if (row.Swatch is { } swatchColor)
            using (var swatchBrush = new SolidBrush(swatchColor))
                g.FillEllipse(swatchBrush, circleRect);

        var isChecked = row.IsChecked?.Invoke() ?? false;
        using var pen = new Pen(isChecked ? _getAccent() : _getCheckboxBorder(), isChecked ? 2 : 1);
        g.DrawEllipse(pen, circleRect);
    }

    private int RowAt(Point clientPoint)
    {
        for (var i = 0; i < _rowRects.Count; i++)
            if (!_rows[i].IsSeparator && !_rows[i].IsHeader && _rowRects[i].Contains(clientPoint))
                return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = RowAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
        UpdateTooltip(index);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
        UpdateTooltip(-1);
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

        _toolTip.Show(text, this, _rowRects[index].Right + 4, _rowRects[index].Y);
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

        var index = RowAt(e.Location);
        if (index >= 0)
            ItemClicked?.Invoke(_rows[index].Id);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
