using System.Runtime.InteropServices;
using FenceTool.Fences;

namespace FenceTool.Fences.Native;

/// <summary>
/// Hides/restores the real desktop icon behind a fenced shortcut, so it doesn't sit doubled-up
/// underneath the fence's own drawn icon (see FenceForm.PaintItems). Works by moving the real file
/// into a hidden folder living directly on the user's own desktop (StoreRoot) - Explorer's desktop
/// view only shows items directly inside the merged Desktop/Public Desktop root, not a subfolder's
/// contents, so this makes the item disappear the same way moving it anywhere else would, while
/// keeping it easy to find by hand (just un-hide StoreRoot itself in Explorer) rather than buried
/// in an app-data folder. Two earlier approaches were tried and discarded first:
/// - Moving the icon's SysListView32 row off-screen: Explorer's own desktop icon view
///   independently reflows off-screen icons back into view under conditions this process has no
///   reliable event to key off of (observed after full-screening a browser on a multi-monitor
///   setup, with no WM_DISPLAYCHANGE or other broadcast to react to).
/// - Setting/clearing the Hidden file attribute in place instead of moving: this avoided ever
///   needing folder-level move permissions, but silently could never work at all for a file whose
///   own ACL blocks FILE_WRITE_ATTRIBUTES outright (observed on two shortcuts originally installed
///   onto the Public Desktop by an elevated installer, which kept that restrictive ACL after being
///   moved elsewhere - a same-volume move preserves a file's own ACL rather than re-inheriting the
///   destination folder's). Moving is the one operation such a file's ACL does still allow.
///
/// Only applies to paths that live directly in the user's or the public desktop folder - anything
/// else was dragged in from elsewhere and never had a real desktop icon to begin with.
/// </summary>
public sealed class DesktopIconHider
{
    private static readonly string StoreRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "hiddenDesktop");

    // A plain Move doesn't tell the shell anything on its own - notifying it directly, plus
    // forcing a repaint below, is what makes Explorer's desktop view update as soon as the call
    // returns instead of whenever its own next paint cycle lands.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, string? dwItem2);

    private const uint SHCNE_CREATE = 0x00000002;
    private const uint SHCNE_DELETE = 0x00000004;
    private const uint SHCNE_MKDIR = 0x00000008;
    private const uint SHCNE_RMDIR = 0x00000010;
    private const uint SHCNF_PATHW = 0x0005;

    private readonly DesktopListView _listView;

    public DesktopIconHider(DesktopListView listView)
    {
        _listView = listView;
    }

    /// <summary>Hides item's real desktop file, if it has one. Idempotent and safe to call
    /// repeatedly. Returns true if the item is no longer visibly duplicated on the real desktop by
    /// the time this returns - either it was never a real desktop item to begin with (nothing to
    /// hide, not a failure), or hiding it succeeded. False means it's a real desktop item that's
    /// still sitting there, doubled up behind the fence's own drawing of it - observed for a folder
    /// containing the very FenceTool.exe currently running from inside it, which Windows won't let
    /// any process move while it's loaded; more generally, anything the move could throw for (see
    /// TryMove).</summary>
    public bool Hide(FenceItem item)
    {
        if (item.RealDesktopPath is not null)
        {
            // Already relocated - nothing more to do as long as the file's still actually there.
            if (Exists(item.Path))
                return true;

            // Something put it back at its real location without going through Restore (e.g. a
            // clean shutdown's Restore pass ran, but this run's state predates that) - treat it
            // like a fresh hide from there.
            item.Path = item.RealDesktopPath;
            item.RealDesktopPath = null;
        }

        if (!IsOnRealDesktop(item.Path))
            return true;

        var source = item.Path;
        Directory.CreateDirectory(StoreRoot);
        SetHiddenAttributeIfNeeded(StoreRoot);

        // Two different files sharing a name (added at different times, one already relocated by
        // the time the other's dropped on a fence) would otherwise target the same store slot.
        var destination = GetAvailableDestination(Path.Combine(StoreRoot, Path.GetFileName(source)));
        if (!TryMove(source, destination))
            return false;

        item.RealDesktopPath = source;
        item.Path = destination;
        return true;
    }

    /// <summary>Un-hides item's real desktop file. Returns true if the item is no longer relocated
    /// by the time this returns - either it wasn't tracked as relocated to begin with (nothing to
    /// do), or the move succeeded. False means the file is still sitting in the hidden folder and
    /// callers must not discard their only reference to item - see the fallback below for why this
    /// can happen even on an otherwise-healthy machine.</summary>
    public bool Restore(FenceItem item)
    {
        if (item.RealDesktopPath is null)
            return true;

        if (TryRestoreTo(item, item.RealDesktopPath))
            return true;

        // Moving OUT of the Public Desktop only needs delete rights on that one entry, but moving
        // a new file INTO it needs create rights on the folder itself - some machines grant the
        // former to ordinary users but not the latter. The user's own Desktop is always inside
        // their own profile, so it's virtually always writable - falling back to it beats leaving
        // the file stranded in the hidden folder with its tracking discarded.
        var ownDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var fallback = Path.Combine(ownDesktop, Path.GetFileName(item.RealDesktopPath));
        return TryRestoreTo(item, fallback);
    }

    private bool TryRestoreTo(FenceItem item, string desiredPath)
    {
        var destination = GetAvailableDestination(desiredPath);
        if (!TryMove(item.Path, destination))
            return false;

        item.Path = destination;
        item.RealDesktopPath = null;
        return true;
    }

    private static void SetHiddenAttributeIfNeeded(string directory)
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.Hidden) == 0)
            File.SetAttributes(directory, attributes | FileAttributes.Hidden);
    }

    private bool TryMove(string source, string destination)
    {
        // Capture this before the move - once source is gone, Directory.Exists(source) can no
        // longer tell a moved folder apart from a moved file.
        var isDirectory = Directory.Exists(source);

        try
        {
            if (isDirectory)
                Directory.Move(source, destination);
            else
                File.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, in use, or some other reason the move can't happen right now - leave the
            // item wherever it currently is rather than throwing.
            return false;
        }

        // Only the side that's actually the real Desktop needs telling - the hidden folder itself
        // isn't part of Explorer's merged desktop view, so there's no icon-list entry for it.
        NotifyDesktopIfRelevant(source, isDirectory, created: false);
        NotifyDesktopIfRelevant(destination, isDirectory, created: true);
        return true;
    }

    private void NotifyDesktopIfRelevant(string path, bool isDirectory, bool created)
    {
        if (!IsOnRealDesktop(path))
            return;

        var eventId = created
            ? (isDirectory ? SHCNE_MKDIR : SHCNE_CREATE)
            : (isDirectory ? SHCNE_RMDIR : SHCNE_DELETE);
        SHChangeNotify(eventId, SHCNF_PATHW, path, null);
        _listView.ForceRepaint();
    }

    private static string GetAvailableDestination(string desiredPath)
    {
        if (!Exists(desiredPath))
            return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!Exists(candidate))
                return candidate;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

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
