using FenceTool.Snapping;
using FenceTool.UI;

namespace FenceTool.Fences;

/// <summary>
/// Owns the persisted set of custom snap lines, and orchestrates the windows involved: a single
/// SnapGuideOverlay shown only for the duration of a live fence drag (custom lines plus whichever
/// other-fence edges are currently snapped, highlighted) and hidden the instant it ends, and the
/// SnapLineEditOverlay/SnapLinePanel pair for "Manage Snap Lines..." edit mode. Geometry itself
/// lives entirely in the stateless SnapEngine - this class only gathers candidates, persists state,
/// and wires the UI pieces together.
/// </summary>
public sealed class SnapLineManager : IDisposable
{
    private readonly SnapLineStore _store = new();
    private readonly List<SnapLineModel> _lines;
    private readonly HashSet<string> _seededMonitors;

    private SnapGuideOverlay? _guideOverlay;
    private SnapLineEditOverlay? _editOverlay;
    private SnapLinePanel? _editPanel;

    public IReadOnlyList<SnapLineModel> Lines => _lines;

    public event Action? LinesChanged;

    public SnapLineManager()
    {
        var settings = _store.Load();
        _lines = settings.Lines;
        _seededMonitors = settings.SeededMonitors;
        SeedDefaultEdgeLinesForNewMonitors();
    }

    /// <summary>Gives every monitor that's never been seeded before (a first-ever launch, or a
    /// monitor connected for the first time since) a default Top/Bottom/Left/Right line flush with
    /// its own working-area edges (excluding the taskbar, same reasoning as SnapLinePanel's own
    /// Position field) - a ready-to-use baseline without the user having to draw them out manually.
    /// Never re-seeds a monitor it's already given the chance to (tracked in _seededMonitors,
    /// regardless of whether the user went on to delete some or all of them), so a deletion always
    /// sticks.</summary>
    private void SeedDefaultEdgeLinesForNewMonitors()
    {
        var added = false;
        foreach (var screen in Screen.AllScreens)
        {
            if (!_seededMonitors.Add(screen.DeviceName))
                continue;

            var area = screen.WorkingArea;
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Horizontal, Position = area.Top, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Horizontal, Position = area.Bottom, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Vertical, Position = area.Left, MonitorBounds = screen.Bounds });
            _lines.Add(new SnapLineModel { Orientation = SnapOrientation.Vertical, Position = area.Right, MonitorBounds = screen.Bounds });
            added = true;
        }

        if (added)
            Save();
    }

    public SnapLineModel Add(SnapOrientation orientation, int position, Rectangle monitorBounds)
    {
        var line = new SnapLineModel { Orientation = orientation, Position = position, MonitorBounds = monitorBounds };
        _lines.Add(line);
        Save();
        LinesChanged?.Invoke();
        return line;
    }

    /// <summary>monitorBounds/orientation are left null for a plain position edit (e.g. dragging
    /// the line directly only ever changes its position, never its orientation) - only the corner
    /// box's Update passes both, since its Screen combo and orientation radios can each be changed
    /// independently of the position field.</summary>
    public void Update(Guid id, int position, Rectangle? monitorBounds = null, SnapOrientation? orientation = null)
    {
        var line = _lines.FirstOrDefault(l => l.Id == id);
        if (line is null)
            return;
        line.Position = position;
        if (monitorBounds is { } bounds)
            line.MonitorBounds = bounds;
        if (orientation is { } newOrientation)
            line.Orientation = newOrientation;
        Save();
        LinesChanged?.Invoke();
    }

    public void Delete(Guid id)
    {
        var line = _lines.FirstOrDefault(l => l.Id == id);
        if (line is null || !_lines.Remove(line))
            return;
        Save();
        LinesChanged?.Invoke();
    }

    /// <summary>Shows the guide overlay for the duration of a drag, starting with just the plain
    /// (nothing highlighted yet) custom lines - SnapMove/SnapResize update it from there as
    /// candidates are actually snapped to.</summary>
    public void BeginDrag()
    {
        _guideOverlay ??= new SnapGuideOverlay();
        _guideOverlay.SetLines(_lines.Select(l => (l.Orientation, l.Position, Highlighted: false, Span: MonitorSpanOf(l))).ToList());
        _guideOverlay.Show();
    }

    /// <summary>margin is the dragged fence's own FenceModel.Margin - applied to custom line
    /// candidates the exact same way FenceManager.GetOtherFenceEdges already applies it to other
    /// fences' edges, so a fence with a margin set keeps that same gap away from a custom snap line
    /// too, not just from other fences.</summary>
    public SnapResult SnapMove(Rectangle proposedBody, IReadOnlyList<int> verticalCandidates, IReadOnlyList<int> horizontalCandidates, int margin)
    {
        var monitor = Screen.FromRectangle(proposedBody).Bounds;
        var (vCandidates, hCandidates) = MergeCandidates(monitor, margin, verticalCandidates, horizontalCandidates);
        var result = SnapEngine.SnapMove(proposedBody, vCandidates, hCandidates);
        UpdateDragOverlay(result, monitor);
        return result;
    }

    public SnapResult SnapResize(Rectangle proposedBody, SnapEdges activeEdges, IReadOnlyList<int> verticalCandidates, IReadOnlyList<int> horizontalCandidates, int margin)
    {
        var monitor = Screen.FromRectangle(proposedBody).Bounds;
        var (vCandidates, hCandidates) = MergeCandidates(monitor, margin, verticalCandidates, horizontalCandidates);
        var result = SnapEngine.SnapResize(proposedBody, activeEdges, vCandidates, hCandidates);
        UpdateDragOverlay(result, monitor);
        return result;
    }

    public void EndDrag() => _guideOverlay?.Hide();

    public void EnterEditMode()
    {
        if (_editOverlay is not null)
        {
            _editPanel?.Activate();
            return;
        }

        var overlay = new SnapLineEditOverlay(this);
        var panel = new SnapLinePanel();
        _editOverlay = overlay;
        _editPanel = panel;

        overlay.LineSelected += (id, _) =>
        {
            var line = _lines.FirstOrDefault(l => l.Id == id);
            if (line is not null)
                panel.PopulateFrom(line);
        };
        overlay.LineDragged += (id, position, monitorBounds) =>
        {
            Update(id, position, monitorBounds);
            var line = _lines.FirstOrDefault(l => l.Id == id);
            if (line is not null)
                panel.PopulateFrom(line); // keep the box's field live as the line is dragged directly
        };
        overlay.LineDeleteRequested += id =>
        {
            Delete(id);
            panel.ClearSelection();
        };
        overlay.NewLineCommitted += (orientation, position, monitorBounds) => Add(orientation, position, monitorBounds);
        overlay.CloseRequested += ExitEditMode;

        // The corner box now has its own explicit Screen field (defaulting to the primary
        // monitor), so both Add and Update pass along whatever screen is currently selected there.
        panel.AddRequested += (orientation, position, monitorBounds) => Add(orientation, position, monitorBounds);
        panel.UpdateRequested += (id, orientation, position, monitorBounds) => Update(id, position, monitorBounds, orientation);
        panel.DeleteRequested += id =>
        {
            Delete(id);
            panel.ClearSelection();
        };
        panel.NewLineRequested += overlay.ClearSelection;
        panel.CloseRequested += ExitEditMode;

        // The panel is a normal, user-draggable window (FixedToolWindow, not locked in place) - the
        // overlay's excluded region (see SnapLineEditOverlay.ExcludeScreenRect) has to keep tracking
        // wherever it currently is, or the panel becomes unclickable again as soon as it's moved
        // away from where it was first shown. The _editOverlay == overlay check guards against a
        // stray LocationChanged firing during this exact overlay/panel pair's own teardown in
        // ExitEditMode, which nulls _editOverlay before closing either window.
        panel.LocationChanged += (_, _) =>
        {
            if (_editOverlay == overlay)
                overlay.ExcludeScreenRect(panel.Bounds);
        };

        overlay.Show();
        panel.PositionTopRight();
        panel.Show();
        overlay.ExcludeScreenRect(panel.Bounds);
    }

    public void ExitEditMode()
    {
        if (_editOverlay is null)
            return;

        // Nulled out before closing - Close() synchronously re-enters here via each window's own
        // CloseRequested (SnapLinePanel's native caption close button, SnapLineEditOverlay's
        // Escape), and this guard is what stops that from double-disposing.
        var overlay = _editOverlay;
        var panel = _editPanel;
        _editOverlay = null;
        _editPanel = null;

        overlay!.Close();
        overlay.Dispose();
        panel!.Close();
        panel.Dispose();
    }

    public void Dispose()
    {
        ExitEditMode();
        _guideOverlay?.Close();
        _guideOverlay?.Dispose();
    }

    private void UpdateDragOverlay(SnapResult result, Rectangle monitor)
    {
        _guideOverlay ??= new SnapGuideOverlay();

        var vSnapped = new HashSet<int>(result.SnappedVerticalPositions);
        var hSnapped = new HashSet<int>(result.SnappedHorizontalPositions);

        var lines = _lines.Select(l => (l.Orientation, l.Position,
            Highlighted: l.Orientation == SnapOrientation.Horizontal ? hSnapped.Contains(l.Position) : vSnapped.Contains(l.Position),
            Span: MonitorSpanOf(l)))
            .ToList();

        // Any snapped position that isn't already one of the custom lines came from another
        // fence's edge instead - draw an ad-hoc highlighted line for it too, spanning the monitor
        // the drag is currently on, so the live-drag guide covers both sources uniformly.
        foreach (var position in vSnapped)
            if (!_lines.Any(l => l.Orientation == SnapOrientation.Vertical && l.Position == position))
                lines.Add((SnapOrientation.Vertical, position, true, monitor));
        foreach (var position in hSnapped)
            if (!_lines.Any(l => l.Orientation == SnapOrientation.Horizontal && l.Position == position))
                lines.Add((SnapOrientation.Horizontal, position, true, monitor));

        _guideOverlay.SetLines(lines);
    }

    /// <summary>A line saved before per-monitor scoping existed has a zero-size MonitorBounds -
    /// treat that as unscoped (full virtual screen) rather than an invisible zero-width line.</summary>
    private static Rectangle MonitorSpanOf(SnapLineModel line)
    {
        var bounds = line.MonitorBounds;
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : SystemInformation.VirtualScreen;
    }

    /// <summary>Only a custom line whose own monitor matches the one currently being dragged over is
    /// offered as a snap candidate - a line drawn on monitor A shouldn't reach across and snap a
    /// fence being moved on monitor B. A legacy zero-size (pre-scoping) line still applies
    /// everywhere, matching its old unscoped behavior.
    ///
    /// When margin is set, each line also contributes two candidates offset by that amount on
    /// either side (line.Position - margin and + margin) alongside the flush one - unlike a fence's
    /// own edge (which only makes sense padded outward, away from its own span - see
    /// FenceManager.GetOtherFenceEdges), a standalone line has no "interior" to avoid overlapping,
    /// so both directions are equally valid depending on which side the fence approaches from.</summary>
    private (List<int> Vertical, List<int> Horizontal) MergeCandidates(Rectangle monitor, int margin, IReadOnlyList<int> extraVertical, IReadOnlyList<int> extraHorizontal)
    {
        var vertical = new List<int>(extraVertical);
        var horizontal = new List<int>(extraHorizontal);

        foreach (var line in _lines)
        {
            var bounds = line.MonitorBounds;
            var isScoped = bounds.Width > 0 && bounds.Height > 0;
            if (isScoped && bounds != monitor)
                continue;

            var target = line.Orientation == SnapOrientation.Vertical ? vertical : horizontal;
            target.Add(line.Position);
            if (margin > 0)
            {
                target.Add(line.Position - margin);
                target.Add(line.Position + margin);
            }
        }

        return (vertical, horizontal);
    }

    private void Save() => _store.Save(new SnapLineSettings { Lines = _lines, SeededMonitors = _seededMonitors });
}
