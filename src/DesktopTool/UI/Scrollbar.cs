namespace DesktopTool.UI;

/// <summary>A generic vertical scrollbar - the thumb-drag/track-paging/wheel interaction and
/// geometry math shared between FenceForm's own icon grid and LayeredWidgetForm's generic list (see
/// LayeredWidgetForm.GetListArea/PaintList) - originally duplicated almost verbatim between the two,
/// now the single copy both scroll against. Owns nothing about what's actually being scrolled (row
/// height, column count, item count, ...) - a caller supplies its own viewport rect and current max
/// scroll each time and reads/writes Offset directly, the same way both callers already tracked their
/// own scroll-offset field before this existed (FenceForm still does - its grid math reads/writes
/// Offset in several more places than just the scrollbar itself, e.g. scrolling a dragged item into
/// view).
///
/// All points passed in (contentPoint below) are content-space - the same space GetContentSize/
/// ToContent already put every other hit-test in - since Geometry's own TrackX/ThumbY etc. are
/// computed in that space too and need to compare directly against it.</summary>
internal sealed class Scrollbar
{
    public const int Width = 6;
    public const int Margin = 3;

    /// <summary>How far scrolled down, in pixels - 0 is the top. Publicly settable (not just via
    /// TryHandleMouseDown/UpdateDrag/HandleWheel below) since a caller's own logic unrelated to the
    /// scrollbar control itself (FenceForm scrolling a dragged item into view, say) needs to move
    /// this directly too.</summary>
    public int Offset { get; set; }

    /// <summary>Whether a thumb-drag is currently in progress - a caller checks this to keep
    /// swallowing mouse-move (and skipping its own other mouse-move handling, item-drag/hover included)
    /// for the whole drag, even on a tick where UpdateDrag itself doesn't change Offset.</summary>
    public bool IsDragging => _dragging;

    private bool _dragging;
    private int _dragStartY;
    private int _dragStartOffset;

    public readonly record struct Geometry(int TrackX, int TrackTop, int TrackHeight, int ThumbY, int ThumbHeight);

    /// <summary>Null when there's nothing to scroll (maxScroll <= 0) - no scrollbar to draw or
    /// hit-test. viewport.Right/Top/Height place the track; viewport.Left/Width aren't used (a
    /// scrollbar always hugs the right edge of whatever viewport it's given).</summary>
    public Geometry? GetGeometry(Rectangle viewport, int maxScroll)
    {
        if (maxScroll <= 0)
            return null;

        var trackX = viewport.Right - Width - Margin;
        var totalHeight = viewport.Height + maxScroll;
        var thumbHeight = Math.Min(viewport.Height, Math.Max(20, (int)((long)viewport.Height * viewport.Height / Math.Max(1, totalHeight))));
        var maxThumbTravel = Math.Max(0, viewport.Height - thumbHeight);
        var thumbY = viewport.Top + (maxThumbTravel > 0 ? (int)((long)Offset * maxThumbTravel / maxScroll) : 0);

        return new Geometry(trackX, viewport.Top, viewport.Height, thumbY, thumbHeight);
    }

    /// <summary>Clamps Offset back into [0, maxScroll] - a caller re-runs this on every paint, since a
    /// resize can shrink maxScroll out from under a scroll position set before it (an offset that was
    /// valid a moment ago can otherwise point past the new, smaller scrollable range).</summary>
    public void ClampToMax(int maxScroll) => Offset = Math.Clamp(Offset, 0, maxScroll);

    /// <summary>A caller's own OnMouseDown calls this - arms thumb-dragging, or pages the track
    /// toward contentPoint like a normal scrollbar's track does. Returns true if the click landed on
    /// the scrollbar at all (thumb or track), so the caller knows not to treat it as anything else.</summary>
    public bool TryHandleMouseDown(Point contentPoint, Rectangle viewport, int maxScroll, int pageSize)
    {
        if (GetGeometry(viewport, maxScroll) is not { } sb)
            return false;

        // A little horizontal slack around the thin thumb/track makes it easier to grab.
        var thumbRect = new Rectangle(sb.TrackX - 2, sb.ThumbY, Width + 4, sb.ThumbHeight);
        if (thumbRect.Contains(contentPoint))
        {
            _dragging = true;
            _dragStartY = contentPoint.Y;
            _dragStartOffset = Offset;
            return true;
        }

        var trackRect = new Rectangle(sb.TrackX - 2, sb.TrackTop, Width + 4, sb.TrackHeight);
        if (trackRect.Contains(contentPoint))
        {
            var page = Math.Max(pageSize, sb.TrackHeight - pageSize);
            Offset = Math.Clamp(Offset + (contentPoint.Y < sb.ThumbY ? -page : page), 0, maxScroll);
            return true;
        }

        return false;
    }

    /// <summary>A caller's own OnMouseMove calls this every tick - a no-op unless TryHandleMouseDown
    /// just armed the thumb. Returns true if Offset actually changed, so the caller knows whether a
    /// repaint is needed.</summary>
    public bool UpdateDrag(Point contentPoint, Rectangle viewport, int maxScroll)
    {
        if (!_dragging)
            return false;
        if (GetGeometry(viewport, maxScroll) is not { } sb || sb.TrackHeight <= sb.ThumbHeight)
            return false;

        var maxThumbTravel = sb.TrackHeight - sb.ThumbHeight;
        var dy = contentPoint.Y - _dragStartY;
        var newOffset = _dragStartOffset + (int)((long)dy * maxScroll / maxThumbTravel);
        var clamped = Math.Clamp(newOffset, 0, maxScroll);
        if (clamped == Offset)
            return false;
        Offset = clamped;
        return true;
    }

    /// <summary>A caller's own OnMouseUp calls this unconditionally - a no-op unless a drag was
    /// actually in progress. Returns true if a drag WAS just ended, so the caller knows to release
    /// its own mouse Capture.</summary>
    public bool EndDrag()
    {
        if (!_dragging)
            return false;
        _dragging = false;
        return true;
    }

    /// <summary>A caller's own OnMouseWheel calls this with e.Delta and its own per-notch step size
    /// (a row/cell height, typically) - returns true if Offset actually changed.</summary>
    public bool HandleWheel(int delta, int step, int maxScroll)
    {
        if (maxScroll <= 0)
            return false;
        var clamped = Math.Clamp(Offset - delta / 120 * step, 0, maxScroll);
        if (clamped == Offset)
            return false;
        Offset = clamped;
        return true;
    }
}
