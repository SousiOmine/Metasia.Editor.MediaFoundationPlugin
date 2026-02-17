using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class BufferHelper
{
    public static unsafe void CopyMemory(IntPtr destination, IntPtr source, int length)
    {
        Buffer.MemoryCopy(source.ToPointer(), destination.ToPointer(), length, length);
    }

    public static BufferLockContext LockBuffer(IMFMediaBuffer buffer)
    {
        buffer.Lock(out IntPtr scanline, out int maxLength, out int currentLength);
        return new BufferLockContext(buffer, scanline, maxLength, currentLength);
    }
}

internal readonly struct BufferLockContext : IDisposable
{
    private readonly IMFMediaBuffer _buffer;
    public IntPtr Data { get; }
    public int MaxLength { get; }
    public int CurrentLength { get; }

    public BufferLockContext(IMFMediaBuffer buffer, IntPtr data, int maxLength, int currentLength)
    {
        _buffer = buffer;
        Data = data;
        MaxLength = maxLength;
        CurrentLength = currentLength;
    }

    public void Dispose()
    {
        _buffer.Unlock();
    }
}