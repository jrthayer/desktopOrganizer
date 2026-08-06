namespace DesktopTool.UI;

/// <summary>A vertical scrollbar drawn entirely in managed code, colored via live Func&lt;Color&gt;
/// getters the same way DropdownMenu's own chrome is - for hosting next to ThemedListBox, which
/// (unlike a native ListBox) has no scrollbar of its own to fight for visual control of (see
/// LayoutLauncherWidget, this control's first caller, for how the two are wired together). Works
/// in "item" units (an index into some list, not pixels) since every caller so far scrolls a
/// fixed-row-height list via its TopIndex property - a pixel-based Value would just need dividing
/// back out by that same row height at every call site.</summary>
internal sealed class ThemedScrollBar : Control
{
    private const int MinThumbHeight = 20;

    private int _itemCount;
    private int _visibleCount;
    private int _value;
    private bool _dragging;
    private int _dragStartMouseY;
    private int _dragStartValue;

    public Func<Color> TrackColor = () => Color.Black;
    public Func<Color> ThumbColor = () => Color.Gray;

    public event Action<int>? ValueChanged;

    public ThemedScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private int MaxValue => Math.Max(0, _itemCount - _visibleCount);

    /// <summary>Recomputes range/visibility from the hosted list's current item count and how many
    /// rows actually fit - call after anything that could change either (items added/removed,
    /// resize). Hides itself (Visible = false) once everything already fits, same as a native
    /// scrollbar would.</summary>
    public void Configure(int itemCount, int visibleCount)
    {
        _itemCount = itemCount;
        _visibleCount = visibleCount;
        var clamped = Math.Clamp(_value, 0, MaxValue);
        if (clamped != _value)
        {
            _value = clamped;
            ValueChanged?.Invoke(_value);
        }
        Visible = MaxValue > 0;
        Invalidate();
    }

    /// <summary>Lets the host push its own external changes to the scroll position (e.g. TopIndex
    /// moving via keyboard/mouse-wheel on the ListBox itself) back into the thumb's drawn position,
    /// without re-firing ValueChanged and bouncing the change straight back at the list.</summary>
    public void SyncValue(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxValue);
        if (clamped == _value)
            return;
        _value = clamped;
        Invalidate();
    }

    private int ThumbHeight => MaxValue <= 0
        ? Height
        : Math.Min(Height, Math.Max(MinThumbHeight, Height * _visibleCount / Math.Max(1, _itemCount)));

    private int MaxThumbTravel => Math.Max(0, Height - ThumbHeight);

    private int ThumbY => MaxValue > 0 ? MaxThumbTravel * _value / MaxValue : 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var trackBrush = new SolidBrush(TrackColor()))
            e.Graphics.FillRectangle(trackBrush, ClientRectangle);

        if (MaxValue <= 0)
            return;

        using var thumbBrush = new SolidBrush(ThumbColor());
        e.Graphics.FillRectangle(thumbBrush, new Rectangle(0, ThumbY, Width, ThumbHeight));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || MaxValue <= 0)
            return;

        var thumbY = ThumbY;
        if (e.Y >= thumbY && e.Y < thumbY + ThumbHeight)
        {
            _dragging = true;
            _dragStartMouseY = e.Y;
            _dragStartValue = _value;
            Capture = true;
            return;
        }

        // Track click outside the thumb - page toward the click, like a normal scrollbar.
        SetValue(_value + (e.Y < thumbY ? -Math.Max(1, _visibleCount - 1) : Math.Max(1, _visibleCount - 1)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        var travel = MaxThumbTravel;
        if (travel <= 0)
            return;

        var dy = e.Y - _dragStartMouseY;
        SetValue(_dragStartValue + (int)Math.Round((double)dy * MaxValue / travel));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    private void SetValue(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxValue);
        if (clamped == _value)
            return;
        _value = clamped;
        Invalidate();
        ValueChanged?.Invoke(_value);
    }
}
