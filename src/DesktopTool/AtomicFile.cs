namespace DesktopTool;

/// <summary>
/// Shared by every JSON store (FenceStore, SnapLineStore, LayoutStore, LayoutLauncherStore) - a
/// plain File.WriteAllText(path, json) writes in place, so a process kill mid-write (e.g. Windows
/// forcibly ending the app during shutdown) can leave a truncated/corrupt file on disk. Each
/// store's Load() already treats a corrupt file as "start fresh" rather than crashing, which turns
/// that truncation into silent state loss on the next launch. Writing to a temp file first and
/// then moving it over the real path makes the visible file always either the old complete
/// contents or the new complete contents, never a partial write - File.Move onto an existing
/// destination on the same volume is a single atomic rename at the filesystem level.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }
}
