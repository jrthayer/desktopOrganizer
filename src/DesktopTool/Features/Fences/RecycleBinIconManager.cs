using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopTool.Features.Fences;

/// <summary>
/// Windows' own "Show Recycle Bin" desktop icon setting (Desktop Icon Settings > Recycle Bin
/// checkbox) - hides the real desktop icon once a fence has its own synthetic Recycle Bin item
/// (see FenceManager.AddRecycleBin), so it doesn't sit doubled-up: once on the real desktop, once
/// drawn by the fence. IsHidden treats the registry value as authoritative rather than persisting a
/// separate flag anywhere, the same approach StartupManager/HiddenFilesManager use for their own
/// settings.
/// </summary>
internal static class RecycleBinIconManager
{
    private const string HideDesktopIconsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    // The Recycle Bin's own well-known CLSID - this is the same value name Windows' own Desktop
    // Icon Settings dialog writes to when its "Recycle Bin" checkbox is unticked.
    private const string ValueName = "{645FF040-5081-101B-9F08-00AA002F954E}";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    public static bool IsHidden
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(HideDesktopIconsKeyPath, writable: false);
            return key?.GetValue(ValueName) as int? == 1;
        }
    }

    public static void SetHidden(bool hidden)
    {
        using var key = Registry.CurrentUser.OpenSubKey(HideDesktopIconsKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(HideDesktopIconsKeyPath);
        key.SetValue(ValueName, hidden ? 1 : 0, RegistryValueKind.DWord);

        // Same broadcast HiddenFilesManager sends after its own Explorer-setting change - refreshes
        // ordinary Explorer windows reliably, but per that class's own comment, historically does
        // NOT reliably refresh the desktop's own icon view. Flipping this setting may need a manual
        // desktop refresh (F5) or an Explorer restart to actually take visible effect.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
