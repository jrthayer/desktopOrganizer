using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FenceTool.Native;

namespace FenceTool.Fences.Native;

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
    public static void Present(IntPtr hwnd, Bitmap bitmap, Point screenLocation, float opacity = 1f)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var dibBitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;

        try
        {
            dibBitmap = CreatePremultipliedDib(memDc, bitmap, out var scan0);
            WritePremultipliedPixels(bitmap, scan0, opacity);
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
    /// alpha used to make it, while antialiased edge pixels stay correctly partial.</summary>
    private static void WritePremultipliedPixels(Bitmap source, IntPtr scan0, float opacity)
    {
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bufferSize = data.Stride * source.Height;
            var buffer = new byte[bufferSize];
            Marshal.Copy(data.Scan0, buffer, 0, bufferSize);

            for (var i = 0; i < bufferSize; i += 4)
            {
                var a = (byte)(buffer[i + 3] * opacity);
                buffer[i] = (byte)(buffer[i] * a / 255);
                buffer[i + 1] = (byte)(buffer[i + 1] * a / 255);
                buffer[i + 2] = (byte)(buffer[i + 2] * a / 255);
                buffer[i + 3] = a;
            }

            Marshal.Copy(buffer, 0, scan0, bufferSize);
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
