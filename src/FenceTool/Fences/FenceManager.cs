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
    }

    public void LoadAndShowAll()
    {
        _models.Clear();
        _models.AddRange(_store.Load());
        foreach (var model in _models)
            ShowFence(model);

        // Re-establishes hidden state for every already-fenced shortcut - the real files are only
        // ever restored on a clean exit (see Dispose), so on a normal launch this re-hides them
        // (and, for anyone upgrading from an older scheme, migrates them to the current one - see
        // DesktopIconHider.Hide); after a crash it's a harmless no-op since they're already hidden.
        // Hide can mutate Path/RealDesktopPath during that migration, so this needs its own Save.
        // Ignores Hide's own result here - a startup pass silently re-trying (and re-warning about)
        // something that's permanently un-hideable (e.g. a folder containing FenceTool's own
        // running executable) on every single launch would be far more annoying than useful; see
        // AddFiles, where the same failure is worth surfacing once, at the moment it's added.
        foreach (var model in _models)
            foreach (var file in model.Files)
                _iconHider.Hide(file);
        Save();
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

    /// <summary>Same idea as CreateFence, but seeded from an existing fence's settings (color,
    /// HideTitle/HideLabels, OCD sizing) instead of the defaults - see FenceForm's "+" button next to
    /// Settings. Deliberately doesn't copy Files: this is "another fence styled the same way", not a
    /// clone of its contents.</summary>
    public void CreateFenceLike(Guid sourceId)
    {
        var source = _models.Find(m => m.Id == sourceId);
        if (source is null)
            return;

        var model = new FenceModel
        {
            Name = $"Fence {_models.Count + 1}",
            Bounds = NextDefaultBounds(),
            HideLabels = source.HideLabels,
            HideTitle = source.HideTitle,
            OcdFenceSizing = source.OcdFenceSizing,
            TintColor = source.TintColor,
        };
        _models.Add(model);
        ShowFence(model);
        Save();
    }

    public void DeleteFence(Guid id)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        var items = model.Files.ToList();

        // Removed up front so IsReferencedByAnyFence below doesn't just match this same fence: a
        // file only referenced here should restore, one still held by another fence shouldn't. If
        // moving any item back to the real desktop fails - and every fallback destination for that
        // also fails (see DesktopIconHider.Restore) - the model goes right back in rather than
        // deleting the fence anyway, so this never silently contradicts ConfirmDelete's "the files
        // inside it won't be deleted".
        _models.Remove(model);

        var stuck = items.Count(item => !IsReferencedByAnyFence(item.Path) && !_iconHider.Restore(item));
        if (stuck > 0)
        {
            _models.Add(model);
            MessageBox.Show(
                $"\"{model.Name}\" wasn't deleted: {stuck} file(s) in it couldn't be restored to " +
                "the desktop. Nothing was lost - check the hidden \"hiddenDesktop\" folder on your " +
                "desktop and your Explorer folder permissions, then try again.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_forms.Remove(id, out var form))
            // Deferred rather than disposed right here: this runs from deep inside the very form's
            // own WM_COMMAND handling (Delete Fence, clicked from its cog menu), so disposing it
            // immediately pulls the handle out from under code further up that same call stack
            // (TrackPopupMenuEx's owner-draw cleanup, OnMouseDown's post-processing) once it
            // unwinds - which throws ObjectDisposedException reading Handle. BeginInvoke defers the
            // actual Dispose to its own turn on the message loop, after all of that has unwound.
            form.BeginInvoke(new Action(form.Dispose));

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
        // or not anything is still fenced - LoadAndShowAll's own Hide pass re-derives and re-hides
        // whatever's still fenced on the next launch, so this deliberately doesn't Save afterward.
        foreach (var model in _models)
            foreach (var file in model.Files)
                _iconHider.Restore(file);
        _desktopListView.Dispose();
    }

    /// <summary>
    /// Adds dropped files to a fence's own contents - these are just paths the fence remembers and
    /// draws its own icon+label for (via FenceForm's paint logic), the same way NoFences and similar
    /// tools work. If a file lives directly on the real desktop, it's moved into a hidden folder so
    /// it doesn't sit doubled-up behind the fence's own drawing of it - see DesktopIconHider;
    /// anything dragged in from elsewhere is never touched on disk. Paths that don't exist or are
    /// already in this fence are silently skipped.
    /// </summary>
    public void AddFiles(Guid fenceId, IReadOnlyList<string> filePaths)
    {
        var model = _models.Find(m => m.Id == fenceId);
        if (model is null)
            return;

        var added = false;
        var stillVisible = new List<string>();
        foreach (var path in filePaths)
        {
            if (model.Files.Any(f => f.Path == path) || (!File.Exists(path) && !Directory.Exists(path)))
                continue;
            var item = new FenceItem { Path = path };
            model.Files.Add(item);
            if (!_iconHider.Hide(item))
                stillVisible.Add(Path.GetFileName(path));
            added = true;
        }

        if (stillVisible.Count > 0)
            MessageBox.Show(
                $"Added, but couldn't hide the real desktop icon for: {string.Join(", ", stillVisible)}. " +
                "It'll still show up doubled - once behind the fence's own drawing of it, and once on " +
                "the desktop - likely because it's in use or locked right now.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (added)
            Save();
    }

    public void RemoveFile(Guid fenceId, string path)
    {
        var model = _models.Find(m => m.Id == fenceId);
        var item = model?.Files.Find(f => f.Path == path);
        if (model is null || item is null || !model.Files.Remove(item))
            return;

        // Only bring the real desktop icon back once no other fence holds this same path anymore -
        // and if that restore fails, put it right back rather than let this fence's removal
        // silently discard tracking of it.
        if (!IsReferencedByAnyFence(path) && !_iconHider.Restore(item))
        {
            model.Files.Add(item);
            var name = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : Path.GetFileNameWithoutExtension(item.Path);
            MessageBox.Show(
                $"Couldn't restore \"{name}\" to the desktop, so it's staying in this fence " +
                "instead. Check the hidden \"hiddenDesktop\" folder on your desktop and your " +
                "Explorer folder permissions if you'd rather place it yourself.",
                "Fence Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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

    public void SetFullOpacityOnHover(Guid id, bool enabled)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null || model.FullOpacityOnHover == enabled)
            return;
        model.FullOpacityOnHover = enabled;
        Save();
    }

    /// <summary>exact marks color as an Eyedropper pick (see FenceModel.TintIsExact) rather than a
    /// preset/Custom... dialog result - false for every caller except FenceForm.PickEyedropperColor.
    /// Meaningless (and forced false) alongside a null color, since there's no tint left to apply
    /// exactly or otherwise.
    ///
    /// Every non-exact pick (Default, a preset, or a Custom... dialog result) resets
    /// HeaderDarkness/Opacity/TintStrength back to their defaults, even re-clicking the color that's
    /// already selected - the sliders are meant as a per-pick tweak, not a setting that carries over
    /// once you've moved on to a different (or the same) swatch. An Eyedropper pick has its own reset
    /// instead (PickEyedropperColor always sets Opacity to 100 and TintStrength to 0 on every pick).</summary>
    public void SetTintColor(Guid id, Color? color, bool exact = false)
    {
        var model = _models.Find(m => m.Id == id);
        if (model is null)
            return;
        var argb = color?.ToArgb();
        var effectiveExact = color is not null && exact;

        if (!effectiveExact)
        {
            var alreadyDefault = model.TintColor == argb && model.TintIsExact == effectiveExact
                && model.HeaderDarkness == FenceModel.DefaultHeaderDarkness && model.Opacity == FenceModel.DefaultOpacity
                && model.TintStrength == FenceModel.DefaultTintStrength;
            if (alreadyDefault)
                return;
            model.TintColor = argb;
            model.TintIsExact = false;
            model.HeaderDarkness = FenceModel.DefaultHeaderDarkness;
            model.Opacity = FenceModel.DefaultOpacity;
            model.TintStrength = FenceModel.DefaultTintStrength;
            Save();
            return;
        }

        if (model.TintColor == argb && model.TintIsExact == effectiveExact)
            return;
        model.TintColor = argb;
        model.TintIsExact = effectiveExact;
        Save();
    }

    public void SetHeaderDarkness(Guid id, int darkness)
    {
        var model = _models.Find(m => m.Id == id);
        var clamped = Math.Clamp(darkness, 0, 100);
        if (model is null || model.HeaderDarkness == clamped)
            return;
        model.HeaderDarkness = clamped;
        Save();
    }

    // A fence dragged all the way to 0% opacity would be both invisible and (per
    // LayeredWindowPresenter's own doc comment) click-through, with no way to get it back short of
    // editing fences.json by hand - this floor keeps at least a faint, still-clickable trace visible.
    private const int MinOpacity = 15;

    public void SetOpacity(Guid id, int opacity)
    {
        var model = _models.Find(m => m.Id == id);
        var clamped = Math.Clamp(opacity, MinOpacity, 100);
        if (model is null || model.Opacity == clamped)
            return;
        model.Opacity = clamped;
        Save();
    }

    public void SetTintStrength(Guid id, int strength)
    {
        var model = _models.Find(m => m.Id == id);
        var clamped = Math.Clamp(strength, 0, 100);
        if (model is null || model.TintStrength == clamped)
            return;
        model.TintStrength = clamped;
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

    private bool IsReferencedByAnyFence(string path) => _models.Any(m => m.Files.Any(f => f.Path == path));

    private void Save() => _store.Save(_models);
}
