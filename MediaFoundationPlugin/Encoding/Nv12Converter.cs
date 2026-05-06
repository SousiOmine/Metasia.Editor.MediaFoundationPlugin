using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SkiaSharp;

namespace MediaFoundationPlugin.Encoding;

internal static class Nv12Converter
{
    public static IntPtr ConvertBgraToNv12(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        int ySize = width * height;
        int uvSize = width * height / 2;
        int totalSize = ySize + uvSize;

        IntPtr nv12Buffer = Marshal.AllocHGlobal(totalSize);
        try
        {
            ConvertBgraToNv12Core(bitmap, nv12Buffer, width, height);
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
        ConvertBgraToNv12Core(bitmap, nv12Buffer, bitmap.Width, bitmap.Height);
    }

    public static int CalculateNv12BufferSize(int width, int height)
    {
        return width * height + width * height / 2;
    }

    private static unsafe void ConvertBgraToNv12Core(SKBitmap bitmap, IntPtr nv12Buffer, int width, int height)
    {
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();

        fixed (byte* src = pixels)
        {
            byte* yPtr = (byte*)nv12Buffer;
            byte* uvPtr = yPtr + width * height;

            if (Avx2.IsSupported)
            {
                ConvertYAvx2(src, yPtr, width, height);
                ConvertUvSse41(src, uvPtr, width, height);
            }
            else if (Sse41.IsSupported)
            {
                ConvertY_Sse41(src, yPtr, width, height);
                ConvertUvSse41(src, uvPtr, width, height);
            }
            else if (Sse2.IsSupported)
            {
                ConvertY_Sse2(src, yPtr, width, height);
                ConvertUvSse2(src, uvPtr, width, height);
            }
            else
            {
                ConvertYScalar(src, yPtr, width, height);
                ConvertUvScalar(src, uvPtr, width, height);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte YScalar(uint pixel)
    {
        int b = (int)(pixel & 0xFF);
        int g = (int)((pixel >> 8) & 0xFF);
        int r = (int)((pixel >> 16) & 0xFF);
        return (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Y4Sse41(Vector128<int> p)
    {
        Vector128<int> r = Sse2.ShiftRightLogical(p, 16);
        Vector128<int> g = Sse2.And(Sse2.ShiftRightLogical(p, 8), Vector128.Create(0xFF));
        Vector128<int> b = Sse2.And(p, Vector128.Create(0xFF));
        Vector128<int> sum = Sse2.Add(
            Sse2.Add(Sse41.MultiplyLow(r, Vector128.Create(66)), Sse41.MultiplyLow(g, Vector128.Create(129))),
            Sse41.MultiplyLow(b, Vector128.Create(25)));
        return Sse2.Add(Sse2.ShiftRightArithmetic(Sse2.Add(sum, Vector128.Create(128)), 8), Vector128.Create(16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Y8Avx2(Vector256<int> p)
    {
        Vector256<int> r = Avx2.ShiftRightLogical(p, 16);
        Vector256<int> g = Avx2.And(Avx2.ShiftRightLogical(p, 8), Vector256.Create(0xFF));
        Vector256<int> b = Avx2.And(p, Vector256.Create(0xFF));
        Vector256<int> sum = Avx2.Add(
            Avx2.Add(Avx2.MultiplyLow(r, Vector256.Create(66)), Avx2.MultiplyLow(g, Vector256.Create(129))),
            Avx2.MultiplyLow(b, Vector256.Create(25)));
        return Avx2.Add(Avx2.ShiftRightArithmetic(Avx2.Add(sum, Vector256.Create(128)), 8), Vector256.Create(16));
    }

    private static unsafe void ConvertY_Sse41(byte* src, byte* dst, int width, int height)
    {
        int total = width * height;
        int s = total / 4 * 4;
        int i = 0;
        for (; i < s; i += 4)
        {
            Vector128<int> y = Y4Sse41(Sse2.LoadVector128((int*)(src + i * 4)));
            dst[i] = (byte)(uint)y.GetElement(0);
            dst[i + 1] = (byte)(uint)y.GetElement(1);
            dst[i + 2] = (byte)(uint)y.GetElement(2);
            dst[i + 3] = (byte)(uint)y.GetElement(3);
        }
        for (; i < total; i++)
        {
            int si = i * 4;
            dst[i] = (byte)(((66 * src[si + 2] + 129 * src[si + 1] + 25 * src[si] + 128) >> 8) + 16);
        }
    }

    private static unsafe void ConvertYAvx2(byte* src, byte* dst, int width, int height)
    {
        int total = width * height;
        int s = total / 8 * 8;
        int i = 0;
        for (; i < s; i += 8)
        {
            Vector256<int> y = Y8Avx2(Avx.LoadVector256((int*)(src + i * 4)));
            dst[i] = (byte)(uint)y.GetElement(0);
            dst[i + 1] = (byte)(uint)y.GetElement(1);
            dst[i + 2] = (byte)(uint)y.GetElement(2);
            dst[i + 3] = (byte)(uint)y.GetElement(3);
            dst[i + 4] = (byte)(uint)y.GetElement(4);
            dst[i + 5] = (byte)(uint)y.GetElement(5);
            dst[i + 6] = (byte)(uint)y.GetElement(6);
            dst[i + 7] = (byte)(uint)y.GetElement(7);
        }
        for (; i < total; i++)
        {
            int si = i * 4;
            dst[i] = (byte)(((66 * src[si + 2] + 129 * src[si + 1] + 25 * src[si] + 128) >> 8) + 16);
        }
    }

    private static unsafe void ConvertY_Sse2(byte* src, byte* dst, int width, int height)
    {
        int total = width * height;
        int s = total / 4 * 4;
        int i = 0;
        for (; i < s; i += 4)
        {
            Vector128<int> p = Sse2.LoadVector128((int*)(src + i * 4));
            dst[i] = YScalar((uint)p.GetElement(0));
            dst[i + 1] = YScalar((uint)p.GetElement(1));
            dst[i + 2] = YScalar((uint)p.GetElement(2));
            dst[i + 3] = YScalar((uint)p.GetElement(3));
        }
        for (; i < total; i++)
        {
            int si = i * 4;
            dst[i] = (byte)(((66 * src[si + 2] + 129 * src[si + 1] + 25 * src[si] + 128) >> 8) + 16);
        }
    }

    private static unsafe void ConvertYScalar(byte* src, byte* dst, int width, int height)
    {
        int total = width * height;
        for (int i = 0; i < total; i++)
        {
            int si = i * 4;
            dst[i] = YScalar((uint)(src[si] | (src[si + 1] << 8) | (src[si + 2] << 16) | (src[si + 3] << 24)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UvScalar2(uint p0, uint p1, out byte u0, out byte v0, out byte u1, out byte v1)
    {
        int r0 = (int)((p0 >> 16) & 0xFF), g0 = (int)((p0 >> 8) & 0xFF), b0 = (int)(p0 & 0xFF);
        int r1 = (int)((p1 >> 16) & 0xFF), g1 = (int)((p1 >> 8) & 0xFF), b1 = (int)(p1 & 0xFF);
        u0 = (byte)(((-38 * r0 - 74 * g0 + 112 * b0 + 128) >> 8) + 128);
        v0 = (byte)(((112 * r0 - 94 * g0 - 18 * b0 + 128) >> 8) + 128);
        u1 = (byte)(((-38 * r1 - 74 * g1 + 112 * b1 + 128) >> 8) + 128);
        v1 = (byte)(((112 * r1 - 94 * g1 - 18 * b1 + 128) >> 8) + 128);
    }

    private static unsafe void ConvertUvSse2(byte* src, byte* dst, int width, int height)
    {
        int stride = width * 4, uvStride = width;
        for (int y = 0; y < height; y += 2)
        {
            byte* row = src + y * stride, uv = dst + (y / 2) * uvStride;
            int x = 0;
            for (; x <= width - 4; x += 4)
            {
                long d = *(long*)(row + x * 4);
                UvScalar2((uint)(ulong)d, (uint)((ulong)d >> 32), out byte u0, out byte v0, out byte u1, out byte v1);
                uv[x] = u0; uv[x + 1] = v0; uv[x + 2] = u1; uv[x + 3] = v1;
            }
            for (; x < width; x += 2)
            {
                int si = x * 4;
                byte b = row[si], g = row[si + 1], r = row[si + 2];
                uv[x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uv[x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }

    private static unsafe void ConvertUvSse41(byte* src, byte* dst, int width, int height)
    {
        int stride = width * 4, uvStride = width;
        var maskFF = Vector128.Create(0xFF);
        var cU = (Vector128.Create(-38), Vector128.Create(-74), Vector128.Create(112));
        var cV = (Vector128.Create(112), Vector128.Create(-94), Vector128.Create(-18));
        var r128 = Vector128.Create(128);
        for (int y = 0; y < height; y += 2)
        {
            byte* row = src + y * stride, uv = dst + (y / 2) * uvStride;
            int x = 0;
            for (; x <= width - 4; x += 4)
            {
                var p = Vector128.Create(*(long*)(row + x * 4)).AsInt32();
                var r = Sse2.And(Sse2.ShiftRightLogical(p, 16), maskFF);
                var g = Sse2.And(Sse2.ShiftRightLogical(p, 8), maskFF);
                var b = Sse2.And(p, maskFF);
                var u = Sse2.Add(Sse2.ShiftRightArithmetic(Sse2.Add(
                    Sse2.Add(Sse41.MultiplyLow(r, cU.Item1), Sse41.MultiplyLow(g, cU.Item2)), Sse41.MultiplyLow(b, cU.Item3)), 8), r128);
                var v = Sse2.Add(Sse2.ShiftRightArithmetic(Sse2.Add(
                    Sse2.Add(Sse41.MultiplyLow(r, cV.Item1), Sse41.MultiplyLow(g, cV.Item2)), Sse41.MultiplyLow(b, cV.Item3)), 8), r128);
                uv[x] = (byte)(uint)u.GetElement(0);
                uv[x + 1] = (byte)(uint)v.GetElement(0);
                uv[x + 2] = (byte)(uint)u.GetElement(1);
                uv[x + 3] = (byte)(uint)v.GetElement(1);
            }
            for (; x < width; x += 2)
            {
                int si = x * 4;
                byte b = row[si], g = row[si + 1], r = row[si + 2];
                uv[x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uv[x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }

    private static unsafe void ConvertUvScalar(byte* src, byte* dst, int width, int height)
    {
        int stride = width * 4, uvStride = width;
        for (int y = 0; y < height; y += 2)
        {
            byte* row = src + y * stride, uv = dst + (y / 2) * uvStride;
            for (int x = 0; x < width; x += 2)
            {
                int si = x * 4;
                byte b = row[si], g = row[si + 1], r = row[si + 2];
                uv[x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uv[x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }
}
