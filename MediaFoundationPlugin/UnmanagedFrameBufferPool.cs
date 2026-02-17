using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MediaFoundationPlugin;

internal sealed class UnmanagedFrameBufferPool : IDisposable
{
    private readonly ConcurrentBag<IntPtr> _buffers = new();
    private readonly int _bufferSizeBytes;
    private readonly int _maxRetainedBuffers;
    private int _retainedCount;
    private bool _disposed;

    public UnmanagedFrameBufferPool(int bufferSizeBytes, int maxRetainedBuffers = 4)
    {
        if (bufferSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes));
        }

        _bufferSizeBytes = bufferSizeBytes;
        _maxRetainedBuffers = Math.Max(1, maxRetainedBuffers);
    }

    public IntPtr Rent()
    {
        lock (_buffers)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(UnmanagedFrameBufferPool));
            if (_buffers.TryTake(out IntPtr buffer))
            {
                _retainedCount--;
                return buffer;
            }
        }

        return Marshal.AllocHGlobal(_bufferSizeBytes);
    }

    public void Return(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero)
        {
            return;
        }

        lock (_buffers)
        {
            if (_disposed)
            {
                Marshal.FreeHGlobal(buffer);
                return;
            }

            if (_retainedCount >= _maxRetainedBuffers)
            {
                Marshal.FreeHGlobal(buffer);
                return;
            }

            _buffers.Add(buffer);
            _retainedCount++;
        }
    }

    public void Dispose()
    {
        lock (_buffers)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_buffers.TryTake(out IntPtr buffer))
            {
                Marshal.FreeHGlobal(buffer);
                _retainedCount--;
            }
        }

        GC.SuppressFinalize(this);
    }
}