using System.Runtime.InteropServices;

namespace MediaFoundationPlugin;

internal sealed class UnmanagedFrameBufferPool : IDisposable
{
    private readonly Stack<IntPtr> _buffers = new();
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
            if (_buffers.Count > 0)
            {
                _retainedCount--;
                return _buffers.Pop();
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

            _buffers.Push(buffer);
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
            while (_buffers.Count > 0)
            {
                Marshal.FreeHGlobal(_buffers.Pop());
                _retainedCount--;
            }
        }

        GC.SuppressFinalize(this);
    }
}