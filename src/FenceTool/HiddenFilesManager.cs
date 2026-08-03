using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FenceTool;

/// <summary>
/// Windows' own "Show hidden files, folders, and drives" Explorer folder option (View > Show >
/// Hidden items, or the same checkbox in Folder Options) - not anything specific to Fence Tool,
/// but exposed from the tray for convenience since fenced items live in a hidden folder (see
/// Native.DesktopIconHider) that's otherwise easy to forget how to reveal. IsEnabled treats the
/// registry value as authoritative rather than persisting a separate flag anywhere, the same
/// approach StartupManager uses for its own tray toggle.
/// </summary>
internal static class HiddenFilesManager
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "Hidden";
    private const int ShowHidden = 1;
    private const int HideHidden = 2; // Windows' own default

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            return key?.GetValue(ValueName) as int? == ShowHidden;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(AdvancedKeyPath);
        key.SetValue(ValueName, enabled ? ShowHidden : HideHidden, RegistryValueKind.DWord);

        // The same broadcast Explorer's own Folder Options dialog sends after applying this exact
        // setting - refreshes ordinary already-open Explorer windows, though notably not the
        // desktop's own icon view (see README's Tray menu limitations - several more targeted
        // notification/repaint/refresh approaches were tried for that specifically and none of
        // them worked either).
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
