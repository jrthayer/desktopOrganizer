namespace DesktopTool.Features.Snapping;

public enum SnapOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>Which edge(s) of a rect are actively being dragged during a resize - a flat set since a
/// corner drag moves two edges at once. Not meaningful for a move, where all four edges travel
/// together (see SnapEngine.SnapMove).</summary>
[Flags]
public enum SnapEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
}

public readonly record struct SnapResult(
    Rectangle Rect,
    IReadOnlyList<int> SnappedVerticalPositions,
    IReadOnlyList<int> SnappedHorizontalPositions);

/// <summary>
/// Pure, stateless edge-snapping geometry - no dependency on fences, windows, or any particular
/// widget type, so any future draggable/resizable UI element can reuse it the same way FenceForm
/// does. Candidates are plain screen-pixel coordinates (X for vertical lines, Y for horizontal),
/// supplied by the caller - this class never looks up where they come from, whether that's other
/// fences' edges, user-placed guide lines, or something else entirely later.
/// </summary>
public static class SnapEngine
{
    public const int DefaultThresholdPx = 8;

    /// <summary>Snaps a pure translation - width/height must stay identical to proposed, or the
    /// drag would visibly jitter/resize. Left/Right are compared against verticalCandidates and
    /// Top/Bottom against horizontalCandidates independently; whichever edge on each axis lands
    /// closest to a candidate within threshold decides that axis's offset, applied to the whole
    /// rect so it only ever translates.</summary>
    public static SnapResult SnapMove(Rectangle proposed, IReadOnlyList<int> verticalCandidates,
        IReadOnlyList<int> horizontalCandidates, int threshold = DefaultThresholdPx)
    {
        var dx = BestOffset(proposed.Left, proposed.Right, verticalCandidates, threshold, out var snappedV);
        var dy = BestOffset(proposed.Top, proposed.Bottom, horizontalCandidates, threshold, out var snappedH);

        var rect = proposed;
        rect.Offset(dx, dy);
        return new SnapResult(rect, snappedV, snappedH);
    }

    /// <summary>Snaps only the edge(s) named by activeEdges - the opposite edge(s) come back
    /// unchanged, matching how an OS resize drag only ever moves the edge(s) under the cursor. Each
    /// active edge snaps independently against its own axis's candidates; a snap that would push an
    /// edge past its own opposite is skipped rather than emitting an inverted rect, which the OS's
    /// own drag-tracking can't recover from.</summary>
    public static SnapResult SnapResize(Rectangle proposed, SnapEdges activeEdges,
        IReadOnlyList<int> verticalCandidates, IReadOnlyList<int> horizontalCandidates,
        int threshold = DefaultThresholdPx)
    {
        var left = proposed.Left;
        var right = proposed.Right;
        var top = proposed.Top;
        var bottom = proposed.Bottom;

        var snappedV = new List<int>();
        var snappedH = new List<int>();

        if ((activeEdges & SnapEdges.Left) != 0 && TryNearest(left, verticalCandidates, threshold, out var newLeft) && newLeft < right)
        {
            left = newLeft;
            snappedV.Add(newLeft);
        }
        if ((activeEdges & SnapEdges.Right) != 0 && TryNearest(right, verticalCandidates, threshold, out var newRight) && newRight > left)
        {
            right = newRight;
            snappedV.Add(newRight);
        }
        if ((activeEdges & SnapEdges.Top) != 0 && TryNearest(top, horizontalCandidates, threshold, out var newTop) && newTop < bottom)
        {
            top = newTop;
            snappedH.Add(newTop);
        }
        if ((activeEdges & SnapEdges.Bottom) != 0 && TryNearest(bottom, horizontalCandidates, threshold, out var newBottom) && newBottom > top)
        {
            bottom = newBottom;
            snappedH.Add(newBottom);
        }

        return new SnapResult(Rectangle.FromLTRB(left, top, right, bottom), snappedV, snappedH);
    }

    /// <summary>Picks whichever of edgeA/edgeB lands closest to a candidate within threshold (each
    /// checked independently, never summed) and returns the delta to apply to both - a pure move
    /// can't snap them separately without resizing.</summary>
    private static int BestOffset(int edgeA, int edgeB, IReadOnlyList<int> candidates, int threshold, out List<int> snapped)
    {
        snapped = new List<int>();
        var bestDelta = 0;
        var bestAbs = int.MaxValue;
        var bestCandidate = 0;
        var found = false;

        foreach (var candidate in candidates)
        {
            foreach (var edge in new[] { edgeA, edgeB })
            {
                var delta = candidate - edge;
                var abs = Math.Abs(delta);
                if (abs <= threshold && abs < bestAbs)
                {
                    bestAbs = abs;
                    bestDelta = delta;
                    bestCandidate = candidate;
                    found = true;
                }
            }
        }

        if (found)
            snapped.Add(bestCandidate);
        return bestDelta;
    }

    private static bool TryNearest(int edge, IReadOnlyList<int> candidates, int threshold, out int nearest)
    {
        nearest = edge;
        var bestAbs = int.MaxValue;
        var found = false;

        foreach (var candidate in candidates)
        {
            var abs = Math.Abs(candidate - edge);
            if (abs <= threshold && abs < bestAbs)
            {
                bestAbs = abs;
                nearest = candidate;
                found = true;
            }
        }

        return found;
    }
}
