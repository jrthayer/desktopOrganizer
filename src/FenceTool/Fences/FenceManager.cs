using FenceTool.UI;

namespace FenceTool.Fences;

public sealed class FenceManager
{
    private readonly FenceStore _store = new();
    private readonly List<FenceModel> _models = new();
    private readonly Dictionary<Guid, FenceForm> _forms = new();

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
        var form = new FenceForm(model, this);
        _forms[model.Id] = form;
        form.Show();
    }

    private void Save() => _store.Save(_models);
}
