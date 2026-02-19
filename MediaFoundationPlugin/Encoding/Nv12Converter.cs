using System;
using System.Runtime.InteropServices;
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
        IntPtr yPlane = nv12Buffer;
        IntPtr uvPlane = IntPtr.Add(nv12Buffer, width * height);

        byte* yPtr = (byte*)yPlane.ToPointer();
        byte* uvPtr = (byte*)uvPlane.ToPointer();

        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelIndex = y * width + x;
                int srcIndex = pixelIndex * 4;

                byte b = pixels[srcIndex];
                byte g = pixels[srcIndex + 1];
                byte r = pixels[srcIndex + 2];

                int yValue = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                yPtr[pixelIndex] = (byte)Math.Clamp(yValue, 16, 235);

                if ((y % 2) == 0 && (x % 2) == 0)
                {
                    int uvIndex = (y / 2) * width + x;
                    int uValue = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int vValue = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    uvPtr[uvIndex] = (byte)Math.Clamp(uValue, 0, 255);
                    uvPtr[uvIndex + 1] = (byte)Math.Clamp(vValue, 0, 255);
                }
            }
        }
    }
}