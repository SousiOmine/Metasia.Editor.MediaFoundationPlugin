using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MediaFoundationPlugin.Encoding;

internal static class Nv12Converter
{
    private const int ParallelPixelThreshold = 1_000_000;
    private const int ParallelRowPairChunkSize = 64;
    private static readonly int[] YR = CreateTable(66, 0);
    private static readonly int[] YG = CreateTable(129, 0);
    private static readonly int[] YB = CreateTable(25, 128 + (16 << 8));
    private static readonly int[] UR = CreateTable(-38, 0);
    private static readonly int[] UG = CreateTable(-74, 0);
    private static readonly int[] UB = CreateTable(112, 128 + (128 << 8));
    private static readonly int[] VR = CreateTable(112, 0);
    private static readonly int[] VG = CreateTable(-94, 0);
    private static readonly int[] VB = CreateTable(-18, 128 + (128 << 8));

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

        Parallel.ForEach(
            Partitioner.Create(0, height / 2, ParallelRowPairChunkSize),
            range => ConvertRows((byte*)srcAddress, (byte*)yAddress, (byte*)uvAddress, width, height, rowBytes, rgba, range.Item1, range.Item2));
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

            if (rgba)
            {
                ConvertYAndUvRowRgba(row0, yRow0, uvRow, width);
                ConvertYRowRgba(row1, yRow1, width);
                continue;
            }

            ConvertYAndUvRowBgra(row0, yRow0, uvRow, width);
            ConvertYRowBgra(row1, yRow1, width);
        }
    }

    private static unsafe void ConvertYAndUvRowBgra(byte* src, byte* yDst, byte* uvDst, int width)
    {
        for (int x = 0; x < width; x += 2)
        {
            byte* pixel0 = src + x * 4;
            byte b = pixel0[0];
            byte g = pixel0[1];
            byte r = pixel0[2];
            yDst[x] = ToY(r, g, b);
            uvDst[x] = ToU(r, g, b);
            uvDst[x + 1] = ToV(r, g, b);

            byte* pixel1 = pixel0 + 4;
            yDst[x + 1] = ToY(pixel1[2], pixel1[1], pixel1[0]);
        }
    }

    private static unsafe void ConvertYAndUvRowRgba(byte* src, byte* yDst, byte* uvDst, int width)
    {
        for (int x = 0; x < width; x += 2)
        {
            byte* pixel0 = src + x * 4;
            byte r = pixel0[0];
            byte g = pixel0[1];
            byte b = pixel0[2];
            yDst[x] = ToY(r, g, b);
            uvDst[x] = ToU(r, g, b);
            uvDst[x + 1] = ToV(r, g, b);

            byte* pixel1 = pixel0 + 4;
            yDst[x + 1] = ToY(pixel1[0], pixel1[1], pixel1[2]);
        }
    }

    private static unsafe void ConvertYRowBgra(byte* src, byte* dst, int width)
    {
        for (int x = 0; x < width; x++)
        {
            byte* pixel = src + x * 4;
            dst[x] = ToY(pixel[2], pixel[1], pixel[0]);
        }
    }

    private static unsafe void ConvertYRowRgba(byte* src, byte* dst, int width)
    {
        for (int x = 0; x < width; x++)
        {
            byte* pixel = src + x * 4;
            dst[x] = ToY(pixel[0], pixel[1], pixel[2]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToY(byte r, byte g, byte b)
    {
        return (byte)((YR[r] + YG[g] + YB[b]) >> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToU(byte r, byte g, byte b)
    {
        return (byte)((UR[r] + UG[g] + UB[b]) >> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToV(byte r, byte g, byte b)
    {
        return (byte)((VR[r] + VG[g] + VB[b]) >> 8);
    }

    private static int[] CreateTable(int multiplier, int offset)
    {
        int[] table = new int[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = multiplier * i + offset;
        }

        return table;
    }
}
