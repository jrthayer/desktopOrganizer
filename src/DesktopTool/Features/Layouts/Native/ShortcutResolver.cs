using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTool.Features.Layouts.Native;

/// <summary>
/// Resolves a .lnk shortcut's own target executable, via the standard IShellLink/IPersistFile COM
/// pair rather than a NuGet dependency - needed because WindowPlacer has to know the *actual* exe
/// name to watch for (a shortcut's own file name routinely has nothing to do with its target, e.g.
/// "Google Chrome.lnk" -> chrome.exe), and matching on the shortcut's own name would silently never
/// find the window it launches.
///
/// Both interfaces below only declare the methods actually called (GetPath / Load) plus whatever
/// precedes them in the real vtable - COM dispatch is purely positional, so a method after the one
/// you call doesn't need to be declared at all, but everything before it does, even unused.
/// </summary>
internal static class ShortcutResolver
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    private const uint STGM_READ = 0;

    /// <summary>Null if lnkPath isn't a real/readable shortcut - callers fall back to the
    /// shortcut's own path in that case (see WindowPlacer.ResolveExeName).</summary>
    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, STGM_READ);

            var buffer = new StringBuilder(260);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
