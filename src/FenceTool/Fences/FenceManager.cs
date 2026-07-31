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

    public FenceManager()
    {
        _anchorStrategy = new EmbeddedDesktopAnchorStrategy(_desktopListView);
        _desktopListView.ExplorerRestarted += (_, _) => ReanchorAll();
        _desktopListView.AccessDenied += (_, _) => DesktopAccessDenied?.Invoke(this, EventArgs.Empty);
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

    public void Dispose() => _desktopListView.Dispose();

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

        var originX = model.Bounds.X + ArrangePadding;
        var originY = model.Bounds.Y + FenceForm.TitleBarHeight + ArrangePadding;
        var availableWidth = Math.Max(model.Bounds.Width - ArrangePadding * 2, horizontalSpacing);
        var columns = Math.Max(1, availableWidth / horizontalSpacing);

        var placements = new List<(int Index, Point Position)>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var position = new Point(originX + column * horizontalSpacing, originY + row * verticalSpacing);
            placements.Add((members[i].Index, position));
        }

        _desktopListView.SetItemPositions(placements);
    }

    /// <summary>
    /// Recomputes which desktop icons fall inside each fence's bounds (by the icon's current
    /// position) and records them by label. Read-only for now - icons aren't actually moved
    /// until the write path lands, so this just keeps each fence's membership list current.
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
            form.Visible = visible;
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
    }

    private void ReanchorAll()
    {
        foreach (var form in _forms.Values)
            form.Reanchor();
    }

    private void Save() => _store.Save(_models);
}
