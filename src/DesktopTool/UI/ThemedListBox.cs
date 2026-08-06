namespace DesktopTool.UI;

/// <summary>A self-scrolling, entirely custom-painted stand-in for a native ListBox - drawn and
/// scrolled by hand the same way FenceForm's own icon grid is, rather than fighting a native
/// scrollable Win32 control for visual control the way LayoutLauncherWidget's previous native
/// ListBox did. A standard ListBox manages its own vertical-scrollbar window style internally on
/// every content/size change, silently reasserting its native scrollbar regardless of what a
/// caller does to it from outside (both the ShowScrollBar Win32 API and later just clearing the
/// WS_VSCROLL style bit directly got overridden the same way) - the only way to be rid of it for
/// good turned out to be not using a native ListBox at all. Pairs with ThemedScrollBar (this
/// control has no scrollbar of its own, native or otherwise - see LayoutLauncherWidget, this
/// control's first and so far only caller, for how the two are wired together via
/// TopIndex/TopIndexChanged).</summary>
internal sealed class ThemedListBox : Control
{
    public List<string> Items { get; } = new();

    public int ItemHeight { get; set; } = 24;

    private int _topIndex;
    public int TopIndex
    {
        get => _topIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxTopIndex);
            if (clamped == _topIndex)
                return;
            _topIndex = clamped;
            Invalidate();
            TopIndexChanged?.Invoke(_topIndex);
        }
    }

    private int _selectedIndex = -1;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = value < 0 || value >= Items.Count ? -1 : value;
            if (clamped == _selectedIndex)
                return;
            _selectedIndex = clamped;
            Invalidate();
        }
    }

    /// <summary>Fired whenever TopIndex changes for any reason (mouse wheel, arrow keys, or the
    /// host setting it directly) - lets LayoutLauncherWidget keep ThemedScrollBar's own thumb
    /// position in sync without that widget needing to know which of those reasons caused it.</summary>
    public event Action<int>? TopIndexChanged;

    public event DrawItemEventHandler? DrawItem;

    public ThemedListBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
    }

    private int VisibleRowCount => ItemHeight <= 0 ? 0 : Math.Max(1, Height / ItemHeight);
    private int MaxTopIndex => Math.Max(0, Items.Count - VisibleRowCount);

    /// <summary>Replaces the full contents in one step, clamping TopIndex/SelectedIndex back into
    /// range as part of the same operation - callers always want this after a refresh, so there's
    /// no separate Clear()/Add() pair to forget an Invalidate() after (see the raw ListBox.Items
    /// collection this replaces, which needed exactly that at every call site).</summary>
    public void SetItems(IEnumerable<string> items)
    {
        Items.Clear();
        Items.AddRange(items);
        _topIndex = Math.Clamp(_topIndex, 0, MaxTopIndex);
        if (_selectedIndex >= Items.Count)
            _selectedIndex = -1;
        Invalidate();
    }

    public int IndexFromPoint(Point point)
    {
        if (!ClientRectangle.Contains(point))
            return -1;
        var index = TopIndex + point.Y / ItemHeight;
        return index >= 0 && index < Items.Count ? index : -1;
    }

    public Rectangle GetItemRectangle(int index) =>
        new(0, (index - TopIndex) * ItemHeight, Width, ItemHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        for (var index = TopIndex; index < Items.Count; index++)
        {
            var bounds = GetItemRectangle(index);
            if (bounds.Top >= Height)
                break;

            var state = index == SelectedIndex ? DrawItemState.Selected : DrawItemState.Default;
            DrawItem?.Invoke(this, new DrawItemEventArgs(e.Graphics, Font, bounds, index, state, ForeColor, BackColor));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var index = IndexFromPoint(e.Location);
        if (index >= 0)
            SelectedIndex = index;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var notches = e.Delta / 120;
        TopIndex -= notches * Math.Max(1, SystemInformation.MouseWheelScrollLines);
    }

    // Up/Down are ordinarily swallowed by the containing form/dialog navigation instead of
    // reaching a control's own OnKeyDown - claiming them here as "input keys" is what a native
    // ListBox gets for free from its own window class.
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Items.Count == 0)
            return;

        if (e.KeyCode == Keys.Down)
            SelectedIndex = Math.Min(Items.Count - 1, (SelectedIndex < 0 ? -1 : SelectedIndex) + 1);
        else if (e.KeyCode == Keys.Up)
            SelectedIndex = Math.Max(0, (SelectedIndex < 0 ? Items.Count : SelectedIndex) - 1);
        else
            return;

        EnsureVisible(SelectedIndex);
    }

    private void EnsureVisible(int index)
    {
        if (index < TopIndex)
            TopIndex = index;
        else if (index >= TopIndex + VisibleRowCount)
            TopIndex = index - VisibleRowCount + 1;
    }
}
