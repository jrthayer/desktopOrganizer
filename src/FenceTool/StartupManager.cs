using Microsoft.Win32;

namespace FenceTool;

/// <summary>
/// "Start with Windows" via the standard per-user Run key (HKCU, not HKLM) - no admin rights
/// needed, and it only affects the current user, matching a tray app that already only manages
/// this user's own desktop. IsEnabled treats the value's mere presence as authoritative rather
/// than persisting a separate on/off flag anywhere - the registry key already is the single
/// source of truth, so there's nothing to fall out of sync with it.
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FenceTool";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    /// <summary>Environment.ProcessPath is the real FenceTool.exe apphost, not dotnet.exe - a
    /// framework-dependent net8.0-windows WinExe still gets its own native apphost by default, so
    /// this is safe to point the Run key straight at without needing "dotnet FenceTool.dll".
    /// Quoted since it could contain spaces (e.g. "Program Files").</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null)
                return;
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
