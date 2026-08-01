namespace FenceTool.Native;

/// <summary>
/// Hides/restores the real desktop icon behind a fenced shortcut, so it doesn't sit doubled-up
/// underneath the fence's own drawn icon (see FenceForm.PaintItems). SysListView32 has no supported
/// per-item "hidden" state - the only lever DesktopListView exposes is repositioning (see its own
/// doc comment on LVM_SETITEMPOSITION32), so "hidden" here just means "moved far enough off-screen
/// that no monitor arrangement could ever show it", with its real position remembered for later.
///
/// Only applies to paths that live directly in the user's or the public desktop folder - anything
/// else was dragged in from elsewhere and never had a real desktop icon to begin with. Matching a
/// path to its listview item is done by display label (filename without extension, which is what
/// Explorer shows for a known file type by default) since the listview otherwise only exposes
/// label/position, not the underlying shell item's path - two different real desktop files that
/// happen to share a display name (e.g. "Notes.txt" and "Notes.docx" with extensions hidden) can't
/// be told apart this way, a known limitation rather than an oversight.
/// </summary>
public sealed class DesktopIconHider
{
    // Comfortably outside any real monitor arrangement (virtual-screen coordinates can run negative
    // too, see DesktopListView.GetListViewOrigin, so this needs headroom on both sides) while still
    // a plain in-range int for LVM_SETITEMPOSITION32.
    private const int HiddenCoordinate = -20000;

    private readonly DesktopListView _listView;
    private readonly DesktopIconVisibilityStore _store;
    private readonly Dictionary<string, Point> _originalPositions;

    public DesktopIconHider(DesktopListView listView)
    {
        _listView = listView;
        _store = new DesktopIconVisibilityStore();
        _originalPositions = _store.Load();
    }

    /// <summary>Moves path's real desktop icon off-screen, if it has one. Idempotent and safe to
    /// call for a path that's already hidden (e.g. re-asserted after explorer.exe restarts) or one
    /// that was never on the real desktop at all - the icon's original position is only captured
    /// (and persisted) the first time.</summary>
    public void Hide(string path)
    {
        if (!IsOnRealDesktop(path))
            return;

        if (!_originalPositions.ContainsKey(path))
        {
            var icon = FindIcon(path);
            if (icon is null)
                return;
            _originalPositions[path] = icon.Position;
            _store.Save(_originalPositions);
        }

        MoveIconTo(path, new Point(HiddenCoordinate, HiddenCoordinate));
    }

    /// <summary>Moves path's real desktop icon back to where it sat before it was hidden. No-op if
    /// it isn't currently tracked as hidden.</summary>
    public void Restore(string path)
    {
        if (!_originalPositions.Remove(path, out var original))
            return;
        _store.Save(_originalPositions);
        MoveIconTo(path, original);
    }

    /// <summary>Restores every currently-hidden icon, regardless of what still references its path -
    /// called on app shutdown so quitting Fence Tool always leaves an ordinary, fully-visible desktop
    /// behind. FenceManager re-hides whatever's still fenced on the next launch.</summary>
    public void RestoreAll()
    {
        foreach (var path in _originalPositions.Keys.ToList())
            Restore(path);
    }

    private void MoveIconTo(string path, Point position)
    {
        var icon = FindIcon(path);
        if (icon is not null)
            _listView.SetItemPositions(new[] { (icon.Index, position) });
    }

    private DesktopIcon? FindIcon(string path)
    {
        // Explorer always hides the extension on a .lnk shortcut regardless of the user's "show
        // file extensions" setting (the common case for a desktop icon), but a plain file with that
        // setting on displays its full name including extension - check both.
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var fileName = Path.GetFileName(path);
        var icons = _listView.EnumerateIcons();
        return icons.FirstOrDefault(icon => string.Equals(icon.Label, nameWithoutExtension, StringComparison.OrdinalIgnoreCase))
            ?? icons.FirstOrDefault(icon => string.Equals(icon.Label, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOnRealDesktop(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return false;

        return IsSameDirectory(directory, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) ||
               IsSameDirectory(directory, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
    }

    private static bool IsSameDirectory(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
