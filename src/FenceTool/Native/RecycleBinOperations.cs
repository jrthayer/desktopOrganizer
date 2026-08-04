using System.Runtime.InteropServices;

namespace FenceTool.Native;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEOPSTRUCTW
{
    public IntPtr hwnd;
    public uint wFunc;
    public IntPtr pFrom;
    public IntPtr pTo;
    // A WORD in the native struct, not a DWORD - declaring this int/uint misaligns every field
    // after it (fAnyOperationsAborted/hNameMappings/lpszProgressTitle) and silently corrupts them.
    public ushort fFlags;
    [MarshalAs(UnmanagedType.Bool)]
    public bool fAnyOperationsAborted;
    public IntPtr hNameMappings;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpszProgressTitle;
}

/// <summary>
/// Sends files to the real Windows Recycle Bin (soft delete, undoable) - backs the fence trash
/// item's drop-to-delete behavior. Uses the older SHFileOperationW rather than the modern
/// IFileOperation COM interface: a single P/Invoke call with one struct, no COM interop/apartment
/// threading to reason about, matching this codebase's existing preference for the simplest
/// mechanism that actually works (see ShellIcons.cs's own IImageList for the one place this
/// project *does* need COM, where there's no simpler alternative).
/// </summary>
internal static class RecycleBinOperations
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);

    /// <summary>Sends every path to the Recycle Bin in a single batched operation (one confirmation
    /// dialog covering all of them, same as dragging multiple files onto the real Recycle Bin at
    /// once). Deliberately doesn't set FOF_NOCONFIRMATION/FOF_NOERRORUI/FOF_SILENT - this should
    /// behave exactly like a real drag-to-Recycle-Bin gesture, respecting the user's own Explorer
    /// delete-confirmation preference and showing Explorer's own error UI for a locked file.
    /// Returns false if nothing was actually deleted - either a real failure, or the user declined
    /// the confirmation dialog (SHFileOperationW still returns 0 in that case, only
    /// fAnyOperationsAborted distinguishes it).</summary>
    public static bool SendToRecycleBin(IntPtr ownerHwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;

        // pFrom needs a DOUBLE-null-terminated multi-string buffer - a struct-level
        // [MarshalAs(UnmanagedType.LPWStr)] string field would only marshal the first path and
        // silently drop the rest, so this is built and owned manually instead.
        var multiString = string.Join('\0', paths) + "\0\0";
        var pFrom = Marshal.StringToHGlobalUni(multiString);
        try
        {
            var fileOp = new SHFILEOPSTRUCTW
            {
                hwnd = ownerHwnd,
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR,
            };

            // SHFileOperationW pumps its own message loop for the confirm/progress/error UI, the
            // same way a modal ColorDialog.ShowDialog already blocks safely on this thread
            // elsewhere - safe to call synchronously here.
            var result = SHFileOperationW(ref fileOp);
            return result == 0 && !fileOp.fAnyOperationsAborted;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }
}
