using System.Runtime.InteropServices;

namespace FenceTool.Native;

/// <summary>
/// Reads the user's current desktop icon grid spacing (Settings > display scaling and icon
/// size affect this) via SPI_GETICONMETRICS, so grid-arrange math doesn't hardcode spacing
/// that would be wrong on other DPI/icon-size configurations.
/// </summary>
internal static class IconMetrics
{
    // sizeof(ICONMETRICSW): UINT cbSize + 3 ints + LOGFONTW (28-byte fixed fields + 32 WCHAR face name).
    private const int IconMetricsSize = 4 + 4 + 4 + 4 + 92;

    public static (int HorizontalSpacing, int VerticalSpacing) GetIconSpacing()
    {
        var buffer = new byte[IconMetricsSize];
        BitConverter.GetBytes(IconMetricsSize).CopyTo(buffer, 0);

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETICONMETRICS,
                    (uint)IconMetricsSize, handle.AddrOfPinnedObject(), 0))
            {
                var horizontal = BitConverter.ToInt32(buffer, 4);
                var vertical = BitConverter.ToInt32(buffer, 8);
                return (horizontal, vertical);
            }
        }
        finally
        {
            handle.Free();
        }

        return (75, 75); // Windows' documented default fallback
    }
}
