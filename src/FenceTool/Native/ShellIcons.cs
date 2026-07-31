using System.Runtime.InteropServices;

namespace FenceTool.Native;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

// The shell's system image lists (as opposed to SHGFI_ICON's small/large icons, which are capped
// at 32x32) are what Explorer itself uses to render sharp large icons - this is the only way to
// get a genuinely high-resolution icon for an arbitrary file rather than upscaling a blurry 32px one.
[ComImport]
[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
}

internal static class ShellIcons
{
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int SHIL_JUMBO = 0x4; // 256x256
    private const int SHIL_EXTRALARGE = 0x2; // 48x48
    private const int ILD_TRANSPARENT = 0x1;
    private static readonly Guid IID_IImageList = typeof(IImageList).GUID;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Extracts a large (up to 256x256) shell icon for the given file/folder path. Returns null if
    /// the path is inaccessible or the shell can't produce one - callers should fall back to
    /// Icon.ExtractAssociatedIcon in that case.
    /// </summary>
    public static Icon? ExtractLargeIcon(string path)
    {
        var shfi = new SHFILEINFO();
        var size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, 0, ref shfi, size, SHGFI_SYSICONINDEX) == IntPtr.Zero)
            return null;

        var iid = IID_IImageList;
        if (SHGetImageList(SHIL_JUMBO, ref iid, out var imageList) != 0 &&
            SHGetImageList(SHIL_EXTRALARGE, ref iid, out imageList) != 0)
            return null;

        try
        {
            if (imageList.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            try
            {
                // Icon.FromHandle doesn't take ownership of hIcon - Clone() copies the image data
                // into a managed Icon we can keep, so the original handle can be destroyed here.
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(imageList);
        }
    }
}
