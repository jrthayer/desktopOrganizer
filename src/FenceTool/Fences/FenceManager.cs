using FenceTool.Native;
using FenceTool.UI;

namespace FenceTool.Fences;

public sealed class FenceManager : IDisposable
{
    private readonly FenceStore _store = new();
    private readonly DesktopListView _desktopListView = new();
    private readonly DesktopIconHider _iconHider;
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
        _iconHider = new DesktopIconHider(_desktopListView);
        // explorer.exe restarting can reset its icons to their normal (visible) layout, so whatever
        // is still fenced needs re-hiding on top of the existing Reanchor - not just once at startup.
        _desktopListView.ExplorerRestarted += (_, _) => { ReanchorAll(); HideAllFencedIcons(); };
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

        // Re-establishes hidden-icon state for every already-fenced shortcut - the icons themselves
        // are only ever restored on a clean exit (see Dispose), so on a normal launch this re-hides
        // them; after a crash it's a harmless no-op since they're already hidden.
        HideAllFencedIcons();
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
        var model = _models.Find(m => m.Id == id);
        var paths = model?.Files.Select(f => f.Path).ToList() ?? new List<string>();

        _models.RemoveAll(m => m.Id == id);
        if (_forms.Remove(id, out var form))
            // Deferred rather than disposed right here: this runs from deep inside the very form's
            // own WM_COMMAND handling (Delete Fence, clicked from its cog menu), so disposing it
            // immediately pulls the handle out from under code further up that same call stack
            // (TrackPopupMenuEx's owner-draw cleanup, OnMouseDown's post-processing) once it
            // unwinds - which throws ObjectDisposedException reading Handle. BeginInvoke defers the
            // actual Dispose to its own turn on the message loop, after all of that has unwound.
            form.BeginInvoke(new Action(form.Dispose));

        // The fence carrying these paths is gone, but they might still be sitting in another one.
        foreach (var path in paths)
            if (!IsReferencedByAnyFence(path))
                _iconHider.Restore(path);

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
        // Quitting Fence Tool should always leave an ordinary, fully-visible desktop behind, whether
        // or not anything is still fenced - LoadAndShowAll re-hides everything on the next launch.
        _iconHider.RestoreAll();
        _desktopListView.Dispose();
    }

    /// <summary>
    /// Adds dropped files to a fence's own contents - these are just paths the fence remembers and
    /// draws its own icon+label for (via FenceForm's paint logic), the same way NoFences and similar
    /// tools work; the underlying file/shortcut itself is never touched. Its real desktop icon (if
    /// it has one) is hidden so it doesn't sit doubled-up behind the fence's own drawing of it - see
    /// DesktopIconHider. Paths that don't exist or are already in this fence are silently skipped.
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
            _iconHider.Hide(path);
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

        // Only bring the real desktop icon back once no other fence holds this same path anymore.
        if (!IsReferencedByAnyFence(path))
            _iconHider.Restore(path);

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

    /// <summary>Finds the fence window (other than excludeId) whose window rect contains
    /// screenPoint - used when an item drag started in one fence is released over another one, to
    /// tell whether it landed on a fence at all and which.</summary>
    public FenceForm? FindFenceAt(Point screenPoint, Guid excludeId)
    {
        foreach (var (id, form) in _forms)
        {
            if (id != excludeId && form.Bounds.Contains(screenPoint))
                return form;
        }
        return null;
    }

    /// <summary>Moves an item from one fence to another - unlike MoveFile (reorder within a single
    /// fence's own grid), this removes the item from its source fence's model and inserts it into
    /// the target fence's model, preserving its DisplayName. Silently dropped if the item can't be
    /// found in the source, or the target fence already holds this path (mirrors AddFiles' own
    /// silent-skip-on-duplicate behavior).</summary>
    public void MoveFileToFence(Guid sourceFenceId, Guid targetFenceId, string path, int targetIndex)
    {
        if (sourceFenceId == targetFenceId)
            return;

        var sourceModel = _models.Find(m => m.Id == sourceFenceId);
        var targetModel = _models.Find(m => m.Id == targetFenceId);
        var item = sourceModel?.Files.Find(f => f.Path == path);
        if (sourceModel is null || targetModel is null || item is null)
            return;

        sourceModel.Files.Remove(item);
        if (!targetModel.Files.Any(f => f.Path == path))
            targetModel.Files.Insert(Math.Clamp(targetIndex, 0, targetModel.Files.Count), item);

        Save();

        if (_forms.TryGetValue(targetFenceId, out var targetForm))
            targetForm.RefreshAfterExternalChange();
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

    public void SetHideLabels(Guid id, bool hide)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null || model.HideLabels == hide)
            return;
        model.HideLabels = hide;
        Save();
    }

    public void SetHideTitle(Guid id, bool hide)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null || model.HideTitle == hide)
            return;
        model.HideTitle = hide;
        Save();
    }

    public void SetOcdFenceSizing(Guid id, bool enabled)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null || model.OcdFenceSizing == enabled)
            return;
        model.OcdFenceSizing = enabled;
        Save();
    }

    public void SetTintColor(Guid id, Color? color)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        var argb = color?.ToArgb();
        if (model.TintColor == argb)
            return;
        model.TintColor = argb;
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

    private void HideAllFencedIcons()
    {
        foreach (var model in _models)
            foreach (var file in model.Files)
                _iconHider.Hide(file.Path);
    }

    private bool IsReferencedByAnyFence(string path) => _models.Any(m => m.Files.Any(f => f.Path == path));

    private void Save() => _store.Save(_models);
}
