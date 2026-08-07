using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopTool.Native;

/// <summary>
/// Presents a GDI+ bitmap (with a real alpha channel, e.g. antialiased rounded corners) onto a
/// WS_EX_LAYERED window via UpdateLayeredWindow, instead of SetWindowRgn + a single whole-window
/// opacity. SetWindowRgn's region is a hard-edged, non-antialiased GDI mask, so rounded corners
/// drawn under it always come out as a pixel staircase no matter how smoothly they're painted;
/// per-pixel alpha is the only way to get a genuinely smooth edge. As a side effect, Windows also
/// uses the alpha channel for hit-testing, so fully-transparent pixels (e.g. outside the rounded
/// corner) are naturally click-through - no separate region needed at all.
/// </summary>
internal static class LayeredWindowPresenter
{
    public static void Present(IntPtr hwnd, Bitmap bitmap, Point screenLocation, float opacity = 1f,
        IReadOnlyList<Rectangle>? fullOpacityRegions = null)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var dibBitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;

        try
        {
            dibBitmap = CreatePremultipliedDib(memDc, bitmap, out var scan0);
            WritePremultipliedPixels(bitmap, scan0, opacity, fullOpacityRegions);
            previousBitmap = NativeMethods.SelectObject(memDc, dibBitmap);

            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var sourceOrigin = new POINT { X = 0, Y = 0 };
            var destOrigin = new POINT { X = screenLocation.X, Y = screenLocation.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(hwnd, screenDc, ref destOrigin, ref size, memDc, ref sourceOrigin,
                0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero)
                NativeMethods.SelectObject(memDc, previousBitmap);
            if (dibBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(dibBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static IntPtr CreatePremultipliedDib(IntPtr memDc, Bitmap source, out IntPtr scan0)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = source.Width,
            biHeight = -source.Height, // negative = top-down, matching GDI+'s row order
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        return NativeMethods.CreateDIBSection(memDc, ref header, NativeMethods.DIB_RGB_COLORS, out scan0, IntPtr.Zero, 0);
    }

    /// <summary>UpdateLayeredWindow (with AC_SRC_ALPHA) requires premultiplied alpha - each color
    /// channel already scaled by alpha/255 - which GDI+'s normal drawing does not produce, so this
    /// converts while copying from the source bitmap into the DIB's pixel buffer. opacity applies
    /// an extra blanket scale on top of each pixel's own alpha, e.g. so a fully-opaque-drawn fence
    /// still ends up translucent overall the way the old whole-window SetLayeredWindowAttributes
    /// alpha used to make it, while antialiased edge pixels stay correctly partial. A pixel inside
    /// fullOpacityRegions (bitmap-space, e.g. LayeredWidgetForm's own Settings/ChromeButton rects)
    /// ignores opacity entirely instead - the same "always fully visible no matter the widget's own
    /// Opacity slider" treatment the Settings dropdown already gets for free by being a separate
    /// window, just applied here since chrome buttons are drawn into this same bitmap.</summary>
    private static void WritePremultipliedPixels(Bitmap source, IntPtr scan0, float opacity,
        IReadOnlyList<Rectangle>? fullOpacityRegions)
    {
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bufferSize = stride * source.Height;
            var buffer = new byte[bufferSize];
            Marshal.Copy(data.Scan0, buffer, 0, bufferSize);

            var hasRegions = fullOpacityRegions is { Count: > 0 };

            for (var y = 0; y < source.Height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < source.Width; x++)
                {
                    var pixelOpacity = opacity;
                    if (hasRegions)
                    {
                        for (var r = 0; r < fullOpacityRegions!.Count; r++)
                        {
                            if (fullOpacityRegions[r].Contains(x, y))
                            {
                                pixelOpacity = 1f;
                                break;
                            }
                        }
                    }

                    var i = rowOffset + x * 4;
                    var a = (byte)(buffer[i + 3] * pixelOpacity);
                    buffer[i] = (byte)(buffer[i] * a / 255);
                    buffer[i + 1] = (byte)(buffer[i + 1] * a / 255);
                    buffer[i + 2] = (byte)(buffer[i + 2] * a / 255);
                    buffer[i + 3] = a;
                }
            }

            Marshal.Copy(buffer, 0, scan0, bufferSize);
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
