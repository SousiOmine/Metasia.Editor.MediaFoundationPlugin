using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal sealed class VideoSession : IDisposable
{
    private const int MaxRetainedBuffers = 4;
    private const int MaxCachedFrames = 10;
    private static readonly TimeSpan MinimumSeekJumpThreshold = TimeSpan.FromMilliseconds(900);
    private static readonly SKImageRasterReleaseDelegate ReleasePixelBuffer = static (pixels, context) =>
    {
        if (pixels == IntPtr.Zero)
        {
            return;
        }

        if (context is UnmanagedFrameBufferPool pool)
        {
            pool.Return(pixels);
            return;
        }

        Marshal.FreeHGlobal(pixels);
    };

    private readonly object _sync = new();
    private readonly IMFSourceReader _reader;
    private readonly double _fps;
    private readonly long _frameDurationTicks100ns;
    private readonly long _seekJumpThresholdTicks100ns;

    private UnmanagedFrameBufferPool _pixelBufferPool;
    private VideoFormat _format;
    private long _lastDecodedTimestamp = long.MinValue;
    private bool _endOfStream;
    private bool _disposed;

    private readonly Dictionary<long, SKImage> _frameCache = new();
    private readonly Queue<long> _cacheTimestampQueue = new();

    internal long LastAccessTicks;

    public VideoSession(string path)
    {
        _reader = SourceReaderFactory.CreateVideoReader(path, useOptimizedPipeline: true, out _format, out _fps);
        _pixelBufferPool = CreatePixelBufferPool(_format);
        _frameDurationTicks100ns = TimestampUtility.ResolveFrameDurationTicks100ns(_fps);
        _seekJumpThresholdTicks100ns = Math.Max(_frameDurationTicks100ns * 45L, MinimumSeekJumpThreshold.Ticks);
    }

    public Task<SKImage?> GetFrameAsync(TimeSpan time)
    {
        return Task.Run(() => GetFrame(time));
    }

    public Task<SKImage?> GetFrameAsync(int frame)
    {
        if (frame < 0 || _fps <= 0 || !double.IsFinite(_fps))
        {
            return Task.FromResult<SKImage?>(null);
        }

        TimeSpan time = TimeSpan.FromSeconds(frame / _fps);
        return GetFrameAsync(time);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reader.Dispose();
            _pixelBufferPool.Dispose();
            ClearCache();
        }

        GC.SuppressFinalize(this);
    }

    private SKImage? GetFrame(TimeSpan requestTime)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));
            LastAccessTicks = DateTime.UtcNow.Ticks;

            long targetTimestamp = TimestampUtility.ConvertToTimestamp100ns(requestTime);

            if (_frameCache.TryGetValue(targetTimestamp, out SKImage? cached))
            {
                return CloneSKImage(cached);
            }

            if (ShouldSeek(targetTimestamp))
            {
                if (!SeekTo(targetTimestamp))
                {
                    return null;
                }
            }

            SKImage? frame = ReadFrameAtOrAfter(targetTimestamp);
            if (frame is not null)
            {
                SKImage cacheEntry = CloneSKImage(frame);
                if (cacheEntry is not null)
                {
                    AddToCache(targetTimestamp, cacheEntry);
                }
            }
            return frame;
        }
    }

    private bool ShouldSeek(long targetTimestamp)
    {
        if (_lastDecodedTimestamp == long.MinValue)
        {
            return true;
        }

        if (targetTimestamp + (_frameDurationTicks100ns * 2L) < _lastDecodedTimestamp)
        {
            return true;
        }

        long forwardDelta = targetTimestamp - _lastDecodedTimestamp;
        if (forwardDelta > _seekJumpThresholdTicks100ns)
        {
            return true;
        }

        if (_endOfStream && targetTimestamp >= _lastDecodedTimestamp)
        {
            return true;
        }

        return false;
    }

    private bool SeekTo(long targetTimestamp)
    {
        ClearCache();
        try
        {
            _reader.SetCurrentPosition(targetTimestamp);
            _lastDecodedTimestamp = long.MinValue;
            _endOfStream = false;
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            _endOfStream = true;
            return false;
        }
    }

    private SKImage? ReadFrameAtOrAfter(long targetTimestamp)
    {
        while (true)
        {
            IMFSample? sample = _reader.ReadSample(
                SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None,
                out int _,
                out SourceReaderFlag streamFlags,
                out long timestamp);

            using (sample)
            {
                if (streamFlags.IsEndOfStream())
                {
                    _endOfStream = true;
                    return null;
                }

                if (streamFlags.IsMediaTypeChanged())
                {
                    RefreshMediaFormat();
                }

                if (streamFlags.IsStreamTick() || sample is null)
                {
                    continue;
                }

                _lastDecodedTimestamp = timestamp;
                if (timestamp + (_frameDurationTicks100ns / 2L) < targetTimestamp)
                {
                    continue;
                }

                return ConvertSampleToSkImage(sample, _format, _pixelBufferPool);
            }
        }
    }

    private void RefreshMediaFormat()
    {
        using IMFMediaType mediaType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        VideoFormat nextFormat = MediaFormatParser.ReadVideoFormat(mediaType);
        if (nextFormat.DestinationStride != _format.DestinationStride || nextFormat.Height != _format.Height)
        {
            UnmanagedFrameBufferPool oldPool = _pixelBufferPool;
            _pixelBufferPool = CreatePixelBufferPool(nextFormat);
            oldPool.Dispose();
        }

        _format = nextFormat;
    }

    private static UnmanagedFrameBufferPool CreatePixelBufferPool(VideoFormat format)
    {
        int bufferSize = checked(format.DestinationStride * format.Height);
        return new UnmanagedFrameBufferPool(bufferSize, MaxRetainedBuffers);
    }

    private void AddToCache(long timestamp, SKImage frame)
    {
        if (_cacheTimestampQueue.Count >= MaxCachedFrames)
        {
            long oldestTimestamp = _cacheTimestampQueue.Dequeue();
            if (_frameCache.Remove(oldestTimestamp, out SKImage? evicted))
            {
                evicted.Dispose();
            }
        }
        _cacheTimestampQueue.Enqueue(timestamp);
        _frameCache[timestamp] = frame;
    }

    private void ClearCache()
    {
        foreach (SKImage image in _frameCache.Values)
        {
            image.Dispose();
        }
        _frameCache.Clear();
        _cacheTimestampQueue.Clear();
    }

    private static SKImage CloneSKImage(SKImage source)
    {
        var info = source.Info;
        int rowBytes = info.RowBytes;
        int size = info.BytesSize;
        IntPtr pixels = Marshal.AllocHGlobal(size);
        try
        {
            source.ReadPixels(info, pixels, rowBytes, 0, 0);
        }
        catch
        {
            Marshal.FreeHGlobal(pixels);
            throw;
        }

        using var pixmap = new SKPixmap(info, pixels, rowBytes);
        SKImage image = SKImage.FromPixels(pixmap, static (p, _) => Marshal.FreeHGlobal(p), null);
        return image;
    }

    private static SKImage? ConvertSampleToSkImage(IMFSample sample, VideoFormat format, UnmanagedFrameBufferPool pixelBufferPool)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        using BufferLockContext lockContext = BufferHelper.LockBuffer(buffer);

        if (lockContext.Data == IntPtr.Zero || lockContext.CurrentLength <= 0)
        {
            return null;
        }

        int destinationRowBytes = format.DestinationStride;
        int sourceRowBytes = format.SourceStride;

        int requiredBytes = checked(sourceRowBytes * checked(format.Height - 1) + destinationRowBytes);
        if (lockContext.CurrentLength < requiredBytes)
        {
            int tightlyPackedRequired = checked(destinationRowBytes * format.Height);
            if (lockContext.CurrentLength < tightlyPackedRequired)
            {
                return null;
            }

            sourceRowBytes = destinationRowBytes;
        }

        if (sourceRowBytes < destinationRowBytes)
        {
            return null;
        }

        IntPtr destination = pixelBufferPool.Rent();
        bool shouldReturnBuffer = true;
        try
        {
            if (destination == IntPtr.Zero)
            {
                return null;
            }

            if (format.DefaultStride > 0 && sourceRowBytes == destinationRowBytes)
            {
                int copyLength = checked(destinationRowBytes * format.Height);
                BufferHelper.CopyMemory(destination, lockContext.Data, copyLength);
            }
            else
            {
                for (int y = 0; y < format.Height; y++)
                {
                    int sourceY = format.DefaultStride < 0 ? (format.Height - 1 - y) : y;
                    IntPtr sourceRow = IntPtr.Add(lockContext.Data, checked(sourceY * sourceRowBytes));
                    IntPtr destinationRow = IntPtr.Add(destination, checked(y * destinationRowBytes));
                    BufferHelper.CopyMemory(destinationRow, sourceRow, destinationRowBytes);
                }
            }

            var imageInfo = new SKImageInfo(format.Width, format.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var pixmap = new SKPixmap(imageInfo, destination, destinationRowBytes);
            SKImage? image = SKImage.FromPixels(pixmap, ReleasePixelBuffer, pixelBufferPool);
            if (image is null)
            {
                return null;
            }

            shouldReturnBuffer = false;
            return image;
        }
        finally
        {
            if (shouldReturnBuffer)
            {
                pixelBufferPool.Return(destination);
            }
        }
    }
}