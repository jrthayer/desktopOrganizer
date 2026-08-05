using System.Drawing.Drawing2D;
using DesktopTool.Features.Fences;
using DesktopTool.Features.Snapping;

namespace DesktopTool.Features.Fences.UI;

/// <summary>
/// Interactive "snap line edit mode" overlay - entered via the tray's "Manage Snap Lines..." item
/// (see SnapLineManager.EnterEditMode). Lets a custom snap line be created by dragging out from a
/// per-monitor screen-edge "ruler", repositioned by dragging it directly, or removed by
/// right-clicking it. Never keeps its own copy of line state - always reads SnapLineManager.Lines
/// and repaints whenever LinesChanged fires, so a position typed into the companion SnapLinePanel
/// and a position dragged directly on the line converge through the same source of truth instead of
/// two views drifting apart.
/// </summary>
internal sealed class SnapLineEditOverlay : Form
{
    private const int RulerThicknessPx = 6;
    private const int HitTestThresholdPx = 4;

    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);
    private static readonly Color LineColor = Color.FromArgb(200, 120, 200, 255);
    private static readonly Color HoverColor = Color.FromArgb(230, 255, 90, 90);
    private static readonly Color PreviewColor = Color.FromArgb(180, 255, 255, 255);

    private readonly SnapLineManager _manager;

    private Guid? _draggingLineId;
    // Offset (in screen pixels) between where the cursor actually landed within the hit-test
    // tolerance band and the line's own exact position at the moment the drag started - subtracted
    // back out on every move so the line tracks the cursor's movement rather than jumping to align
    // exactly with it (which would visibly nudge the line by however many pixels off-center the
    // initial click happened to land within that tolerance).
    private int _draggingLineOffset;
    private SnapOrientation? _draggingNewOrientation;
    private int _dragPreviewPosition;
    private Point _dragPreviewScreenPoint;
    private Guid? _hoverLineId;

    /// <summary>The line currently populating the companion SnapLinePanel - stays highlighted red
    /// until a different line is selected or this one is deleted, independent of _hoverLineId
    /// (which only lasts while the cursor is actually over it) and _draggingLineId (which only
    /// lasts for the duration of an active drag).</summary>
    private Guid? _selectedLineId;

    /// <summary>Fires on mouse-down over an existing line (both to begin a reposition-drag and to
    /// report the click as a "selection" - a plain click without moving the mouse is just a drag
    /// that never changes position) and again on every subsequent move while that drag continues,
    /// each time with whichever monitor the cursor is over right now - dragging a line onto a
    /// different monitor re-homes it there.</summary>
    public event Action<Guid, int>? LineSelected;
    public event Action<Guid, int, Rectangle>? LineDragged;
    public event Action<Guid>? LineDeleteRequested;
    public event Action<SnapOrientation, int, Rectangle>? NewLineCommitted;
    public event Action? CloseRequested;

    public SnapLineEditOverlay(SnapLineManager manager)
    {
        _manager = manager;
        _manager.LinesChanged += OnLinesChanged;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    /// <summary>Drops the persistent-red highlight without touching any line's actual data - called
    /// when the companion SnapLinePanel's own selection is cleared from outside a delete (its "New
    /// Line" button), so the two stay in sync.</summary>
    public void ClearSelection()
    {
        _selectedLineId = null;
        Invalidate();
    }

    private void OnLinesChanged()
    {
        // A deleted line can no longer stay "selected" - without this, its Guid would linger in
        // _selectedLineId forever (harmlessly, since the highlight check below just never matches
        // anything again, but leaving it clears the field explicitly instead of relying on that).
        if (_selectedLineId is { } id && !_manager.Lines.Any(l => l.Id == id))
            _selectedLineId = null;
        Invalidate();
    }

    /// <summary>Cuts screenRect out of this window's own region entirely - not just visually
    /// (TransparencyKey already hides pixels there), but for hit-testing too: a point outside a
    /// window's region is treated as outside the window altogether, so mouse input there falls
    /// through to whatever's actually behind. Without this, this same-size, non-click-through
    /// overlay sits in front of the companion SnapLinePanel and swallows every click meant for its
    /// buttons - color-key transparency alone doesn't affect hit-testing, only WS_EX_TRANSPARENT
    /// does, and this overlay can't be click-through everywhere since it also needs real mouse input
    /// for dragging/selecting lines.</summary>
    public void ExcludeScreenRect(Rectangle screenRect)
    {
        var local = new Rectangle(PointToClient(screenRect.Location), screenRect.Size);
        var region = new Region(ClientRectangle);
        region.Exclude(local);
        Region?.Dispose();
        Region = region;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var screenPoint = PointToScreen(e.Location);

        if (_draggingLineId is { } draggingId)
        {
            var line = _manager.Lines.FirstOrDefault(l => l.Id == draggingId);
            if (line is not null)
            {
                var cursor = line.Orientation == SnapOrientation.Horizontal ? screenPoint.Y : screenPoint.X;
                LineDragged?.Invoke(draggingId, cursor - _draggingLineOffset, Screen.FromPoint(screenPoint).Bounds);
            }
            return;
        }

        if (_draggingNewOrientation is { } orientation)
        {
            _dragPreviewPosition = orientation == SnapOrientation.Horizontal ? screenPoint.Y : screenPoint.X;
            _dragPreviewScreenPoint = screenPoint;
            Invalidate();
            return;
        }

        var hit = FindLineNear(screenPoint);
        if (hit?.Id != _hoverLineId)
        {
            _hoverLineId = hit?.Id;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var screenPoint = PointToScreen(e.Location);
        var hit = FindLineNear(screenPoint);

        if (e.Button == MouseButtons.Right)
        {
            if (hit is not null)
                LineDeleteRequested?.Invoke(hit.Id);
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        if (hit is not null)
        {
            _draggingLineId = hit.Id;
            var cursor = hit.Orientation == SnapOrientation.Horizontal ? screenPoint.Y : screenPoint.X;
            _draggingLineOffset = cursor - hit.Position;
            _selectedLineId = hit.Id;
            LineSelected?.Invoke(hit.Id, hit.Position);
            Invalidate();
            return;
        }

        var rulerOrientation = GetRulerOrientation(screenPoint);
        if (rulerOrientation is { } orientation)
        {
            _draggingNewOrientation = orientation;
            _dragPreviewPosition = orientation == SnapOrientation.Horizontal ? screenPoint.Y : screenPoint.X;
            _dragPreviewScreenPoint = screenPoint;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_draggingNewOrientation is { } orientation)
        {
            NewLineCommitted?.Invoke(orientation, _dragPreviewPosition, Screen.FromPoint(_dragPreviewScreenPoint).Bounds);
            _draggingNewOrientation = null;
        }

        _draggingLineId = null;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            CloseRequested?.Invoke();
    }

    private SnapLineModel? FindLineNear(Point screenPoint)
    {
        SnapLineModel? best = null;
        var bestDistance = HitTestThresholdPx + 1;

        foreach (var line in _manager.Lines)
        {
            var distance = Math.Abs(line.Position - (line.Orientation == SnapOrientation.Horizontal ? screenPoint.Y : screenPoint.X));
            if (distance <= HitTestThresholdPx && distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        return best;
    }

    /// <summary>Which ruler (if any) screenPoint is hovering, based on the monitor it's actually on -
    /// so dragging a new guide out works the same way on every monitor in a multi-monitor setup, not
    /// just relative to the outer edge of the whole virtual desktop.</summary>
    private static SnapOrientation? GetRulerOrientation(Point screenPoint)
    {
        var bounds = Screen.FromPoint(screenPoint).Bounds;
        if (screenPoint.Y - bounds.Top <= RulerThicknessPx)
            return SnapOrientation.Horizontal;
        if (screenPoint.X - bounds.Left <= RulerThicknessPx)
            return SnapOrientation.Vertical;
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        foreach (var line in _manager.Lines)
        {
            var highlighted = line.Id == _hoverLineId || line.Id == _draggingLineId || line.Id == _selectedLineId;
            using var pen = new Pen(highlighted ? HoverColor : LineColor, highlighted ? 2f : 1f);
            DrawLine(e.Graphics, pen, line.Orientation, line.Position, MonitorSpanOf(line));
        }

        if (_draggingNewOrientation is { } orientation)
        {
            using var pen = new Pen(PreviewColor, 1f) { DashStyle = DashStyle.Dash };
            DrawLine(e.Graphics, pen, orientation, _dragPreviewPosition, Screen.FromPoint(_dragPreviewScreenPoint).Bounds);
        }
    }

    /// <summary>A line saved before per-monitor scoping existed has a zero-size MonitorBounds -
    /// treat that as unscoped (full virtual screen) rather than an invisible zero-width line.</summary>
    private Rectangle MonitorSpanOf(SnapLineModel line)
    {
        var bounds = line.MonitorBounds;
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : Bounds;
    }

    private void DrawLine(Graphics g, Pen pen, SnapOrientation orientation, int position, Rectangle span)
    {
        if (orientation == SnapOrientation.Horizontal)
        {
            var y = PointToClient(new Point(0, position)).Y;
            var left = PointToClient(new Point(span.Left, 0)).X;
            var right = PointToClient(new Point(span.Right, 0)).X;
            g.DrawLine(pen, left, y, right, y);
        }
        else
        {
            var x = PointToClient(new Point(position, 0)).X;
            var top = PointToClient(new Point(0, span.Top)).Y;
            var bottom = PointToClient(new Point(0, span.Bottom)).Y;
            g.DrawLine(pen, x, top, x, bottom);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _manager.LinesChanged -= OnLinesChanged;
        base.OnFormClosed(e);
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
}
