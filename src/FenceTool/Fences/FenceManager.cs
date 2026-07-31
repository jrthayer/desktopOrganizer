using FenceTool.Native;
using FenceTool.UI;

namespace FenceTool.Fences;

public sealed class FenceManager : IDisposable
{
    private readonly FenceStore _store = new();
    private readonly DesktopListView _desktopListView = new();
    private readonly IDesktopAnchorStrategy _anchorStrategy;
    private readonly List<FenceModel> _models = new();
    private readonly Dictionary<Guid, FenceForm> _forms = new();
    private readonly System.Windows.Forms.Timer _membershipTimer;

    public FenceManager()
    {
        // EmbeddedDesktopAnchorStrategy's SetParent mechanics work correctly (verified via
        // GetAncestor), but once truly placed behind the icon view, the fence becomes invisible
        // even in empty desktop areas, and mouse input there stops reaching it - the icon view
        // appears to paint an opaque background rather than leaving transparent gaps. See that
        // class's doc comment for details. Using the floating strategy so fences stay visible
        // and interactive; this means dragging a real icon onto a fence still shows the OS's
        // "no drop" cursor rather than passing through.
        _anchorStrategy = new FloatingDesktopAnchorStrategy();
        _desktopListView.ExplorerRestarted += (_, _) => ReanchorAll();
        _desktopListView.AccessDenied += (_, _) => DesktopAccessDenied?.Invoke(this, EventArgs.Empty);

        // Fence create/move/resize already refresh membership on their own, but that doesn't
        // catch the far more common case: the user drags a real desktop icon onto an already-
        // placed, stationary fence. Polling here is what actually makes an icon "go into" a fence.
        _membershipTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _membershipTimer.Tick += (_, _) => DetectIconMovement();
        _membershipTimer.Start();
    }

    /// <summary>Fires when explorer.exe is running at a different privilege level than this app,
    /// so desktop icon management can't work until that's resolved.</summary>
    public event EventHandler? DesktopAccessDenied;

    public void LoadAndShowAll()
    {
        _models.Clear();
        _models.AddRange(_store.Load());
        foreach (var model in _models)
            ShowFence(model);
        RefreshMembership();
    }

    public void CreateFence()
    {
        var model = new FenceModel
        {
            Name = $"Fence {_models.Count + 1}",
            Bounds = NextDefaultBounds(),
        };
        _models.Add(model);
        ShowFence(model);
        RefreshMembership();
        Save();
    }

    public void DeleteFence(Guid id)
    {
        _models.RemoveAll(m => m.Id == id);
        if (_forms.Remove(id, out var form))
            form.Dispose();
        Save();
    }

    public void NotifyBoundsChanged(Guid id, Rectangle bounds)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        model.Bounds = bounds;
        RefreshMembership();
        Save();
    }

    public void Dispose()
    {
        _membershipTimer.Stop();
        _membershipTimer.Dispose();
        _desktopListView.Dispose();
    }

    public void ArrangeFence(Guid id)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;

        ArrangeModel(model);
        Save();
    }

    public void ArrangeAll()
    {
        foreach (var model in _models)
            ArrangeModel(model);
        Save();
    }

    private const int ArrangePadding = 8;

    /// <summary>
    /// Lays out this fence's member icons in a simple row-major grid inside its bounds
    /// (below the title bar), using the user's actual desktop icon spacing so the grid
    /// matches what Explorer would normally use.
    /// </summary>
    private void ArrangeModel(FenceModel model)
    {
        var icons = _desktopListView.EnumerateIcons();
        var members = icons.Where(icon => model.IconNames.Contains(icon.Label)).ToList();
        if (members.Count == 0)
            return;

        var (horizontalSpacing, verticalSpacing) = IconMetrics.GetIconSpacing();

        var placements = new List<(int Index, Point Position)>(members.Count);
        for (int i = 0; i < members.Count; i++)
            placements.Add((members[i].Index, GridSlotPosition(model, i, horizontalSpacing, verticalSpacing)));

        _desktopListView.SetItemPositions(placements);
    }

    private static Point GridSlotPosition(FenceModel model, int slot, int horizontalSpacing, int verticalSpacing)
    {
        var originX = model.Bounds.X + ArrangePadding;
        var originY = model.Bounds.Y + FenceForm.TitleBarHeight + ArrangePadding;
        var availableWidth = Math.Max(model.Bounds.Width - ArrangePadding * 2, horizontalSpacing);
        var columns = Math.Max(1, availableWidth / horizontalSpacing);

        var column = slot % columns;
        var row = slot / columns;
        return new Point(originX + column * horizontalSpacing, originY + row * verticalSpacing);
    }

    /// <summary>
    /// Recomputes which desktop icons fall inside each fence's bounds (by the icon's current
    /// position) and records them by label, without moving anything - used when a fence itself
    /// is created/moved/resized, where snapping whatever now happens to be underneath it into a
    /// grid would feel like an unexpected side effect of resizing.
    /// </summary>
    private void RefreshMembership()
    {
        var icons = _desktopListView.EnumerateIcons();
        if (icons.Count == 0)
            return;

        foreach (var model in _models)
        {
            model.IconNames = icons
                .Where(icon => model.Bounds.Contains(icon.Position))
                .Select(icon => icon.Label)
                .ToList();
        }
    }

    /// <summary>
    /// Detects icons the user has manually dragged onto (or away from) a fence since the last
    /// check, and snaps newly-arrived icons into the next open grid slot immediately - this is
    /// what actually makes dragging an icon onto a fence feel like it "goes into" it. Icons
    /// already tracked as members are left wherever they currently are, so this doesn't fight
    /// the user by continuously re-arranging things they've manually repositioned.
    /// </summary>
    private void DetectIconMovement()
    {
        var icons = _desktopListView.EnumerateIcons();
        if (icons.Count == 0)
            return;

        var changed = false;
        var (horizontalSpacing, verticalSpacing) = IconMetrics.GetIconSpacing();

        foreach (var model in _models)
        {
            var currentMembers = icons.Where(icon => model.Bounds.Contains(icon.Position)).ToList();
            var currentLabels = currentMembers.Select(icon => icon.Label).ToHashSet();
            var previousLabels = model.IconNames.ToHashSet();

            var departed = model.IconNames.Where(name => !currentLabels.Contains(name)).ToList();
            foreach (var name in departed)
            {
                model.IconNames.Remove(name);
                changed = true;
            }

            var arrivals = currentMembers.Where(icon => !previousLabels.Contains(icon.Label)).ToList();
            if (arrivals.Count == 0)
                continue;

            var placements = new List<(int Index, Point Position)>(arrivals.Count);
            foreach (var icon in arrivals)
            {
                placements.Add((icon.Index, GridSlotPosition(model, model.IconNames.Count, horizontalSpacing, verticalSpacing)));
                model.IconNames.Add(icon.Label);
            }

            _desktopListView.SetItemPositions(placements);
            changed = true;
        }

        if (changed)
            Save();
    }

    public void NotifyRenamed(Guid id, string name)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        model.Name = name;
        Save();
    }

    public void SetAllVisible(bool visible)
    {
        foreach (var form in _forms.Values)
            form.SetVisible(visible);
    }

    private Rectangle NextDefaultBounds()
    {
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var offset = (_forms.Count % 8) * 24;
        return new Rectangle(workArea.Left + 80 + offset, workArea.Top + 80 + offset, 240, 200);
    }

    private void ShowFence(FenceModel model)
    {
        var form = new FenceForm(model, this, _anchorStrategy);
        _forms[model.Id] = form;
        form.Show();
        form.Reanchor();
    }

    private void ReanchorAll()
    {
        foreach (var form in _forms.Values)
            form.Reanchor();
    }

    private void Save() => _store.Save(_models);
}
