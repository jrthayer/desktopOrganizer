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
        // EmbeddedDesktopAnchorStrategy's SetParent mechanics work correctly (verified via
        // GetAncestor), but once truly placed behind the icon view, the fence becomes invisible
        // even in empty desktop areas, and mouse input there stops reaching it - the icon view
        // appears to paint an opaque background rather than leaving transparent gaps. See that
        // class's doc comment for details. Using the floating strategy so fences stay visible
        // and interactive.
        _anchorStrategy = new FloatingDesktopAnchorStrategy();
        _desktopListView.ExplorerRestarted += (_, _) => ReanchorAll();
        _desktopListView.AccessDenied += (_, _) => DesktopAccessDenied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fires when explorer.exe is running at a different privilege level than this app,
    /// so the desktop anchor can't be applied until that's resolved.</summary>
    public event EventHandler? DesktopAccessDenied;

    public void LoadAndShowAll()
    {
        _models.Clear();
        _models.AddRange(_store.Load());
        foreach (var model in _models)
            ShowFence(model);
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
        Save();
    }

    public void Dispose()
    {
        _desktopListView.Dispose();
    }

    /// <summary>
    /// Adds dropped files to a fence's own contents - unlike the desktop's real icons, these are
    /// just paths the fence remembers and draws its own icon+label for (via FenceForm's paint
    /// logic), the same way NoFences and similar tools work. The original file/shortcut on the
    /// desktop (if any) is left completely alone; nothing about the real desktop icon layout is
    /// touched. Paths that don't exist or are already in this fence are silently skipped.
    /// </summary>
    public void AddFiles(Guid fenceId, IReadOnlyList<string> filePaths)
    {
        var model = _models.Find(m => m.Id == fenceId);
        if (model is null)
            return;

        var added = false;
        foreach (var path in filePaths)
        {
            if (model.Files.Any(f => f.Path == path) || (!File.Exists(path) && !Directory.Exists(path)))
                continue;
            model.Files.Add(new FenceItem { Path = path });
            added = true;
        }

        if (added)
            Save();
    }

    public void RemoveFile(Guid fenceId, string path)
    {
        var model = _models.Find(m => m.Id == fenceId);
        if (model is null || model.Files.RemoveAll(f => f.Path == path) == 0)
            return;
        Save();
    }

    /// <summary>Reorders an item within its fence's own grid - dragging within the same fence,
    /// not a real desktop icon operation. newIndex is clamped to the valid range.</summary>
    public void MoveFile(Guid fenceId, string path, int newIndex)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (model is null || item is null)
            return;

        model.Files.Remove(item);
        model.Files.Insert(Math.Clamp(newIndex, 0, model.Files.Count), item);
        Save();
    }

    /// <summary>Sets an item's display name within this fence only - never renames the real file.</summary>
    public void RenameFile(Guid fenceId, string path, string displayName)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (item is null)
            return;
        item.DisplayName = displayName;
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
