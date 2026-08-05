using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>A closed-state combo box face (current selection + dropdown arrow) that opens
/// DropdownMenu instead of a native combo popup - a plain ComboBox's dropdown list is rendered by
/// the OS using visual styles that bake in a light-theme background no BackColor/ForeColor override
/// reaches (same wall DropdownMenu's own tooltip ran into - see its class comment). Originally a
/// private nested type inside SnapLinePanel; promoted here once the Layouts feature's editor wanted
/// the same "pick one of N options" picker.
///
/// Only ever holds plain strings - callers keep their own parallel list mapping a SelectedIndex back
/// to the real value, the same relationship ComboBox.Items/SelectedItem used to have.</summary>
internal sealed class ComboButton : Control
{
    private const int ArrowSize = 8;
    private List<string> _items = new();

    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Fired both for a real user pick (OpenDropdown's ItemClicked) and for a
    /// programmatic SetSelectedIndex call - same as ComboBox.SelectedIndexChanged, which fires
    /// either way too; callers that bulk-repopulate via SetItems (silent, see its own comment) are
    /// the ones expected to guard against reacting to their own repopulation if needed.</summary>
    public event Action<int>? SelectedIndexChanged;

    public ComboButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Field;
        ForeColor = AppTheme.Text;
        Cursor = Cursors.Hand;
    }

    /// <summary>A full repopulate (new item list is a different set of choices, not just a new pick
    /// among the existing ones) - deliberately silent, unlike SetSelectedIndex, so callers that
    /// rebuild the whole list don't also have to guard against reacting to their own repopulation.</summary>
    public void SetItems(IReadOnlyList<string> items, int selectedIndex)
    {
        _items = items.ToList();
        SelectedIndex = selectedIndex;
        Invalidate();
    }

    public void SetSelectedIndex(int index)
    {
        SelectedIndex = index;
        Invalidate();
        SelectedIndexChanged?.Invoke(index);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(AppTheme.Field))
            e.Graphics.FillRectangle(background, ClientRectangle);
        using (var borderPen = new Pen(AppTheme.Border))
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        var text = SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : string.Empty;
        var textRect = new Rectangle(8, 0, Math.Max(0, Width - 8 - ArrowSize - 16), Height);
        // Grey rather than the usual white when disabled - a UserPaint control like this one gets
        // no automatic disabled-state rendering from WinForms/uxtheme the way a native TextBox or
        // Button does, so without this branch the text would just stay full-brightness white and
        // give no visual indication the control isn't interactive.
        TextRenderer.DrawText(e.Graphics, text, Font, textRect, Enabled ? AppTheme.Text : AppTheme.DisabledText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        var cx = Width - 8 - ArrowSize / 2;
        var cy = Height / 2;
        using var arrowBrush = new SolidBrush(Color.FromArgb(255, 190, 190, 196));
        e.Graphics.FillPolygon(arrowBrush, new[]
        {
            new Point(cx - ArrowSize / 2, cy - 2),
            new Point(cx + ArrowSize / 2, cy - 2),
            new Point(cx, cy + 3),
        });
    }

    /// <summary>UserPaint controls don't repaint themselves on an Enabled toggle by default -
    /// without this, OnPaint's disabled-color branch above would be correct but never actually get
    /// a chance to run until something else happened to trigger a redraw.</summary>
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
            OpenDropdown();
    }

    private void OpenDropdown()
    {
        if (_items.Count == 0)
            return;

        var rows = new List<DropdownMenu.Row>();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i; // captured per-row, not the loop variable itself
            rows.Add(new DropdownMenu.Row(index, _items[index], HasCheckbox: true, IsChecked: () => index == SelectedIndex));
        }

        var menu = new DropdownMenu(rows, RectangleToScreen(ClientRectangle), preferLeft: false, Font,
            () => AppTheme.Field, () => AppTheme.Hover, () => AppTheme.Accent, () => AppTheme.Border, () => AppTheme.Field);
        menu.ItemClicked += id =>
        {
            SetSelectedIndex(id);
            menu.Close();
        };
        menu.Show(FindForm());
    }
}
