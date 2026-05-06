using System.Runtime.InteropServices;
using MediaFoundationPlugin.Encoding;
using SkiaSharp;

namespace MediaFoundationPlugin.Tests;

public class Nv12ConverterTests
{
    [Test]
    public void ConvertToNv12InPlace_ConvertsKnownBgra2x2Pixels()
    {
        const int width = 2;
        const int height = 2;
        const int rowBytes = width * 4;
        byte[] source =
        [
            0, 0, 0, 255,
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
        ];

        byte[] actual = Convert(source, width, height, rowBytes, SKColorType.Bgra8888);

        byte[] expected =
        [
            Y(0, 0, 0),
            Y(255, 0, 0),
            Y(0, 255, 0),
            Y(0, 0, 255),
            U(0, 0, 0),
            V(0, 0, 0),
        ];
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ConvertToNv12InPlace_ConvertsKnownBgra4x2Pixels()
    {
        const int width = 4;
        const int height = 2;
        const int rowBytes = width * 4;
        byte[] source =
        [
            0, 0, 0, 255,
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            255, 255, 255, 255,
            128, 128, 128, 255,
            30, 20, 10, 255,
            200, 150, 100, 255,
        ];

        byte[] actual = Convert(source, width, height, rowBytes, SKColorType.Bgra8888);

        byte[] expected =
        [
            Y(0, 0, 0),
            Y(255, 0, 0),
            Y(0, 255, 0),
            Y(0, 0, 255),
            Y(255, 255, 255),
            Y(128, 128, 128),
            Y(10, 20, 30),
            Y(100, 150, 200),
            U(0, 0, 0),
            V(0, 0, 0),
            U(0, 255, 0),
            V(0, 255, 0),
        ];
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ConvertToNv12InPlace_HandlesPaddedRows()
    {
        const int width = 2;
        const int height = 2;
        const int rowBytes = 12;
        byte[] source = Enumerable.Repeat((byte)0xEE, rowBytes * height).ToArray();
        WriteBgra(source, rowBytes, 0, 0, 10, 20, 30);
        WriteBgra(source, rowBytes, 1, 0, 40, 50, 60);
        WriteBgra(source, rowBytes, 0, 1, 70, 80, 90);
        WriteBgra(source, rowBytes, 1, 1, 100, 110, 120);

        byte[] actual = Convert(source, width, height, rowBytes, SKColorType.Bgra8888);
        byte[] expected = ConvertReference(source, width, height, rowBytes, rgba: false);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ConvertToNv12InPlace_HandlesRgbaInput()
    {
        const int width = 2;
        const int height = 2;
        const int rowBytes = width * 4;
        byte[] source =
        [
            10, 20, 30, 255,
            40, 50, 60, 255,
            70, 80, 90, 255,
            100, 110, 120, 255,
        ];

        byte[] actual = Convert(source, width, height, rowBytes, SKColorType.Rgba8888);
        byte[] expected = ConvertReference(source, width, height, rowBytes, rgba: true);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ConvertToNv12InPlace_ParallelPathMatchesReference()
    {
        const int width = 1024;
        const int height = 1024;
        const int rowBytes = width * 4 + 16;
        byte[] source = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WriteBgra(source, rowBytes, x, y, (x * 3 + y) & 0xFF, (x + y * 5) & 0xFF, (x * 7 + y * 11) & 0xFF);
            }
        }

        byte[] actual = Convert(source, width, height, rowBytes, SKColorType.Bgra8888);
        byte[] expected = ConvertReference(source, width, height, rowBytes, rgba: false);

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static byte[] Convert(byte[] source, int width, int height, int rowBytes, SKColorType colorType)
    {
        byte[] destination = new byte[Nv12Converter.CalculateNv12BufferSize(width, height)];
        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        GCHandle destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            Nv12Converter.ConvertToNv12InPlace(
                sourceHandle.AddrOfPinnedObject(),
                width,
                height,
                rowBytes,
                colorType,
                destinationHandle.AddrOfPinnedObject());
            return destination;
        }
        finally
        {
            destinationHandle.Free();
            sourceHandle.Free();
        }
    }

    private static byte[] ConvertReference(byte[] source, int width, int height, int rowBytes, bool rgba)
    {
        byte[] destination = new byte[Nv12Converter.CalculateNv12BufferSize(width, height)];
        int uvOffset = width * height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ReadRgb(source, rowBytes, x, y, rgba, out int r, out int g, out int b);
                destination[y * width + x] = Y(r, g, b);
            }
        }

        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                ReadRgb(source, rowBytes, x, y, rgba, out int r, out int g, out int b);
                int offset = uvOffset + (y / 2) * width + x;
                destination[offset] = U(r, g, b);
                destination[offset + 1] = V(r, g, b);
            }
        }

        return destination;
    }

    private static void WriteBgra(byte[] destination, int rowBytes, int x, int y, int r, int g, int b)
    {
        int offset = y * rowBytes + x * 4;
        destination[offset] = (byte)b;
        destination[offset + 1] = (byte)g;
        destination[offset + 2] = (byte)r;
        destination[offset + 3] = 255;
    }

    private static void ReadRgb(byte[] source, int rowBytes, int x, int y, bool rgba, out int r, out int g, out int b)
    {
        int offset = y * rowBytes + x * 4;
        if (rgba)
        {
            r = source[offset];
            g = source[offset + 1];
            b = source[offset + 2];
            return;
        }

        b = source[offset];
        g = source[offset + 1];
        r = source[offset + 2];
    }

    private static byte Y(int r, int g, int b)
    {
        return (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    }

    private static byte U(int r, int g, int b)
    {
        return (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
    }

    private static byte V(int r, int g, int b)
    {
        return (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
    }
}
