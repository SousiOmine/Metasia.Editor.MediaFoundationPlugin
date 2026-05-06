using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MediaFoundationPlugin.Encoding;

internal static class Nv12Converter
{
    private const int ParallelPixelThreshold = 1_000_000;

    public static IntPtr ConvertBgraToNv12(SKBitmap bitmap)
    {
        int totalSize = CalculateNv12BufferSize(bitmap.Width, bitmap.Height);
        IntPtr nv12Buffer = Marshal.AllocHGlobal(totalSize);
        try
        {
            ConvertToNv12InPlace(bitmap, nv12Buffer);
            return nv12Buffer;
        }
        catch
        {
            Marshal.FreeHGlobal(nv12Buffer);
            throw;
        }
    }

    public static void ConvertBgraToNv12InPlace(SKBitmap bitmap, IntPtr nv12Buffer)
    {
        ConvertToNv12InPlace(bitmap, nv12Buffer);
    }

    public static unsafe void ConvertToNv12InPlace(SKBitmap bitmap, IntPtr nv12Buffer)
    {
        using SKPixmap pixmap = bitmap.PeekPixels();
        if (pixmap is null)
        {
            throw new InvalidOperationException("SKBitmapのピクセルデータを取得できませんでした。");
        }

        ConvertToNv12InPlace(
            pixmap.GetPixels(),
            pixmap.Width,
            pixmap.Height,
            pixmap.RowBytes,
            pixmap.ColorType,
            nv12Buffer);
    }

    public static unsafe void ConvertToNv12InPlace(IntPtr pixels, int width, int height, int rowBytes, SKColorType colorType, IntPtr nv12Buffer)
    {
        if (pixels == IntPtr.Zero)
        {
            throw new ArgumentException("ピクセルデータが空です。", nameof(pixels));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "幅と高さは0より大きい必要があります。");
        }

        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("NV12変換には偶数の幅と高さが必要です。");
        }

        if (rowBytes < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(rowBytes), "rowBytesが画像幅に対して不足しています。");
        }

        bool rgba = colorType switch
        {
            SKColorType.Bgra8888 => false,
            SKColorType.Rgba8888 => true,
            _ => throw new NotSupportedException($"未対応のピクセル形式です: {colorType}"),
        };

        byte* src = (byte*)pixels;
        byte* yPlane = (byte*)nv12Buffer;
        byte* uvPlane = yPlane + width * height;

        if (width * height >= ParallelPixelThreshold)
        {
            ConvertRowsParallel(src, yPlane, uvPlane, width, height, rowBytes, rgba);
            return;
        }

        ConvertRows(src, yPlane, uvPlane, width, height, rowBytes, rgba, 0, height / 2);
    }

    public static int CalculateNv12BufferSize(int width, int height)
    {
        return checked(width * height + width * height / 2);
    }

    private static unsafe void ConvertRowsParallel(byte* src, byte* yPlane, byte* uvPlane, int width, int height, int rowBytes, bool rgba)
    {
        nint srcAddress = (nint)src;
        nint yAddress = (nint)yPlane;
        nint uvAddress = (nint)uvPlane;

        Parallel.For(
            0,
            height / 2,
            pairY => ConvertRows((byte*)srcAddress, (byte*)yAddress, (byte*)uvAddress, width, height, rowBytes, rgba, pairY, pairY + 1));
    }

    private static unsafe void ConvertRows(byte* src, byte* yPlane, byte* uvPlane, int width, int height, int rowBytes, bool rgba, int pairStart, int pairEnd)
    {
        for (int pairY = pairStart; pairY < pairEnd; pairY++)
        {
            int y = pairY * 2;
            byte* row0 = src + y * rowBytes;
            byte* row1 = y + 1 < height ? row0 + rowBytes : row0;
            byte* yRow0 = yPlane + y * width;
            byte* yRow1 = yRow0 + width;
            byte* uvRow = uvPlane + pairY * width;

            ConvertYRow(row0, yRow0, width, rgba);
            ConvertYRow(row1, yRow1, width, rgba);
            ConvertUvRow(row0, uvRow, width, rgba);
        }
    }

    private static unsafe void ConvertYRow(byte* src, byte* dst, int width, bool rgba)
    {
        for (int x = 0; x < width; x++)
        {
            ReadRgb(src + x * 4, rgba, out int r, out int g, out int b);
            dst[x] = ToY(r, g, b);
        }
    }

    private static unsafe void ConvertUvRow(byte* src, byte* dst, int width, bool rgba)
    {
        for (int x = 0; x < width; x += 2)
        {
            ReadRgb(src + x * 4, rgba, out int r, out int g, out int b);
            dst[x] = ToU(r, g, b);
            dst[x + 1] = ToV(r, g, b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ReadRgb(byte* pixel, bool rgba, out int r, out int g, out int b)
    {
        if (rgba)
        {
            r = pixel[0];
            g = pixel[1];
            b = pixel[2];
            return;
        }

        b = pixel[0];
        g = pixel[1];
        r = pixel[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToY(int r, int g, int b)
    {
        return (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToU(int r, int g, int b)
    {
        return (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToV(int r, int g, int b)
    {
        return (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
    }
}
