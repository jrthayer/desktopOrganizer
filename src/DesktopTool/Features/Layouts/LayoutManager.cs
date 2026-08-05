using DesktopTool.Features.Layouts.Native;

namespace DesktopTool.Features.Layouts;

/// <summary>Owns every saved LayoutProfile and persists them, the same relationship FenceManager
/// has to FenceModel/FenceStore. No live Form per profile the way a fence gets one - a layout has
/// nothing to show until it's actually run (see RunLayoutAsync/WindowPlacer), so there's no
/// equivalent of FenceManager's _forms dictionary here.</summary>
public sealed class LayoutManager
{
    private readonly LayoutStore _store = new();
    private readonly List<LayoutProfile> _profiles = new();

    public IReadOnlyList<LayoutProfile> Profiles => _profiles;

    public void Load()
    {
        _profiles.Clear();
        _profiles.AddRange(_store.Load());
    }

    public LayoutProfile CreateLayout(string name)
    {
        var profile = new LayoutProfile { Name = name };
        _profiles.Add(profile);
        Save();
        return profile;
    }

    /// <summary>"Save Current Layout" - a new profile pre-populated from whatever's actually open
    /// and where it's actually sitting right now (see WindowPlacer.CaptureCurrentLayout), instead of
    /// building one program-by-program through the editor.</summary>
    public LayoutProfile CaptureCurrentLayout(string name)
    {
        var profile = new LayoutProfile { Name = name, Entries = WindowPlacer.CaptureCurrentLayout() };
        _profiles.Add(profile);
        Save();
        return profile;
    }

    public void UpdateLayout(LayoutProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            _profiles[index] = profile;
        Save();
    }

    public void DeleteLayout(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        Save();
    }

    /// <summary>"Copy" in the launcher widget - a fully independent clone (fresh Id from
    /// LayoutProfile's own default, entries deep-copied so editing the copy's placements never
    /// mutates the source) named "{source.Name} (Copy)", inserted directly after the source rather
    /// than appended at the end so the duplicate shows up right next to what it was copied from.
    /// Null if id no longer matches anything - the profile could have been deleted from elsewhere
    /// (the editor, say) while the caller was still looking at a now-stale list.</summary>
    public LayoutProfile? DuplicateLayout(Guid id)
    {
        var index = _profiles.FindIndex(p => p.Id == id);
        if (index < 0)
            return null;

        var source = _profiles[index];
        var copy = new LayoutProfile
        {
            Name = $"{source.Name} (Copy)",
            Entries = source.Entries.Select(CloneEntry).ToList(),
        };
        _profiles.Insert(index + 1, copy);
        Save();
        return copy;
    }

    private static LayoutEntry CloneEntry(LayoutEntry entry) => new()
    {
        ProgramPath = entry.ProgramPath,
        Arguments = entry.Arguments,
        WindowTitleHint = entry.WindowTitleHint,
        Url = entry.Url,
        Command = entry.Command,
        TerminalShellExe = entry.TerminalShellExe,
        TargetMonitor = entry.TargetMonitor,
        Placement = entry.Placement,
        Minimized = entry.Minimized,
        CustomX = entry.CustomX,
        CustomY = entry.CustomY,
        CustomWidth = entry.CustomWidth,
        CustomHeight = entry.CustomHeight,
    };

    /// <summary>Fire-and-forget from the caller's perspective (a tray click handler) - errors from
    /// an individual entry are already swallowed inside WindowPlacer itself (one bad program in a
    /// layout shouldn't block the rest), so there's nothing further to catch here.</summary>
    public Task RunLayoutAsync(Guid id)
    {
        var profile = _profiles.Find(p => p.Id == id);
        return profile is null ? Task.CompletedTask : WindowPlacer.RunAsync(profile.Entries);
    }

    private void Save() => _store.Save(_profiles);
}
