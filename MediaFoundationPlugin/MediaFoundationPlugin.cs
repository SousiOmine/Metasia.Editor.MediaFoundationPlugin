using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Metasia.Core.Media;
using Metasia.Core.Sounds;
using Metasia.Editor.Plugin;
using SharpGen.Runtime;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

public sealed class MediaFoundationPlugin : IMediaInputPlugin, IDisposable
{
    private const long HundredNanosecondsPerSecond = 10_000_000;
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

    public string PluginIdentifier { get; } = "SousiOmine.MediaFoundationPlugin";
    public string PluginVersion { get; } = "0.1.0";
    public string PluginName { get; } = "MediaFoundation Input";

    public IEnumerable<IEditorPlugin.SupportEnvironment> SupportedEnvironments { get; } =
    [
        IEditorPlugin.SupportEnvironment.Windows_x64,
        IEditorPlugin.SupportEnvironment.Windows_arm64,
    ];

    private static readonly object MediaFoundationSync = new();
    private static int _startupRefCount;
    private readonly ConcurrentDictionary<string, VideoSession> _videoSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Initialize()
    {
        EnsureMediaFoundationStarted();
    }

    public async Task<ImageFileAccessorResult> GetImageAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            using SKBitmap? bitmap = await Task.Run(() => SKBitmap.Decode(path)).ConfigureAwait(false);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            return new ImageFileAccessorResult
            {
                IsSuccessful = true,
                Image = SKImage.FromBitmap(bitmap),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: image load failed. path={path}, error={ex}");
            return new ImageFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, TimeSpan time)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new VideoFileAccessorResult { IsSuccessful = false };
            }

            var session = GetOrCreateSession(path);
            SKImage? image = await session.GetFrameAsync(time).ConfigureAwait(false);
            return new VideoFileAccessorResult
            {
                IsSuccessful = image is not null,
                Image = image,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: video frame load failed. path={path}, time={time}, error={ex}");
            return new VideoFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, int frame)
    {
        if (frame < 0)
        {
            return new VideoFileAccessorResult { IsSuccessful = false };
        }

        try
        {
            if (!File.Exists(path))
            {
                return new VideoFileAccessorResult { IsSuccessful = false };
            }

            var session = GetOrCreateSession(path);
            SKImage? image = await session.GetFrameAsync(frame).ConfigureAwait(false);
            return new VideoFileAccessorResult
            {
                IsSuccessful = image is not null,
                Image = image,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: video frame load by index failed. path={path}, frame={frame}, error={ex}");
            return new VideoFileAccessorResult { IsSuccessful = false };
        }
    }

    public Task<AudioFileAccessorResult> GetAudioAsync(string path, TimeSpan? startTime = null, TimeSpan? duration = null)
    {
        return Task.FromResult(new AudioFileAccessorResult { IsSuccessful = false, Chunk = null });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeVideoSessions();
        ReleaseMediaFoundation();
        GC.SuppressFinalize(this);
    }

    ~MediaFoundationPlugin()
    {
        Dispose();
    }

    private static void EnsureMediaFoundationStarted()
    {
        lock (MediaFoundationSync)
        {
            if (_startupRefCount == 0)
            {
                MediaFactory.MFStartup().CheckError();
            }

            _startupRefCount++;
        }
    }

    private static void ReleaseMediaFoundation()
    {
        lock (MediaFoundationSync)
        {
            if (_startupRefCount <= 0)
            {
                return;
            }

            _startupRefCount--;
            if (_startupRefCount == 0)
            {
                MediaFactory.MFShutdown().CheckError();
            }
        }
    }

    private VideoSession GetOrCreateSession(string path)
    {
        return _videoSessions.GetOrAdd(path, static p => new VideoSession(p));
    }

    private void DisposeVideoSessions()
    {
        foreach (VideoSession session in _videoSessions.Values)
        {
            session.Dispose();
        }

        _videoSessions.Clear();
    }

    private static IMFSourceReader CreateConfiguredSourceReader(string path, out VideoFormat format, out double fps)
    {
        try
        {
            return CreateConfiguredSourceReaderCore(path, useOptimizedPipeline: true, out format, out fps);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: optimized source-reader pipeline unavailable, falling back. path={path}, error={ex.Message}");
            return CreateConfiguredSourceReaderCore(path, useOptimizedPipeline: false, out format, out fps);
        }
    }

    private static IMFSourceReader CreateConfiguredSourceReaderCore(string path, bool useOptimizedPipeline, out VideoFormat format, out double fps)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(6);
        if (useOptimizedPipeline)
        {
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, false).CheckError();
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, false).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableCameraPlugins, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();
        }
        else
        {
            // Compatibility profile: keep behavior close to the previous implementation.
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, true).CheckError();
        }

        IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);

        using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

        using IMFMediaType mediaType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        format = ReadVideoFormat(mediaType);
        fps = ReadFramesPerSecond(mediaType);

        return reader;
    }

    private static double ReadFramesPerSecond(IMFMediaType mediaType)
    {
        ulong frameRate = mediaType.GetUInt64(MediaTypeAttributeKeys.FrameRate);
        uint numerator = (uint)(frameRate >> 32);
        uint denominator = (uint)(frameRate & uint.MaxValue);
        if (numerator == 0 || denominator == 0)
        {
            return 0;
        }

        double fps = numerator / (double)denominator;
        if (!double.IsFinite(fps) || fps <= 0)
        {
            return 0;
        }

        return fps;
    }

    private static VideoFormat ReadVideoFormat(IMFMediaType mediaType)
    {
        Result sizeResult = MediaFactory.MFGetAttributeSize(mediaType, MediaTypeAttributeKeys.FrameSize, out uint width32, out uint height32);
        if (sizeResult.Failure || width32 == 0 || height32 == 0)
        {
            throw new InvalidOperationException("MediaFoundationPlugin: failed to resolve frame size.");
        }

        int width = checked((int)width32);
        int height = checked((int)height32);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("MediaFoundationPlugin: invalid frame size.");
        }

        int destinationStride = checked(width * 4);
        uint defaultStrideU32 = MediaFactory.MFGetAttributeUInt32(
            mediaType,
            MediaTypeAttributeKeys.DefaultStride,
            (uint)destinationStride);
        int defaultStride = unchecked((int)defaultStrideU32);
        if (defaultStride == 0)
        {
            defaultStride = destinationStride;
        }

        int sourceStride = Math.Abs(defaultStride);
        if (sourceStride < destinationStride)
        {
            sourceStride = destinationStride;
        }

        return new VideoFormat(width, height, defaultStride, sourceStride, destinationStride);
    }

    private static SKImage? ConvertSampleToSkImage(IMFSample sample, VideoFormat format, UnmanagedFrameBufferPool pixelBufferPool)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();

        buffer.Lock(out IntPtr scanline, out int _, out int currentLength);
        try
        {
            if (scanline == IntPtr.Zero || currentLength <= 0)
            {
                return null;
            }

            int destinationRowBytes = format.DestinationStride;
            int sourceRowBytes = format.SourceStride;

            int requiredBytes = checked(sourceRowBytes * checked(format.Height - 1) + destinationRowBytes);
            if (currentLength < requiredBytes)
            {
                int tightlyPackedRequired = checked(destinationRowBytes * format.Height);
                if (currentLength < tightlyPackedRequired)
                {
                    Debug.WriteLine($"MediaFoundationPlugin: buffer too small. len={currentLength}, required={requiredBytes}, packed={tightlyPackedRequired}");
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
                    CopyMemory(destination, scanline, copyLength);
                }
                else
                {
                    for (int y = 0; y < format.Height; y++)
                    {
                        int sourceY = format.DefaultStride < 0 ? (format.Height - 1 - y) : y;
                        IntPtr sourceRow = IntPtr.Add(scanline, checked(sourceY * sourceRowBytes));
                        IntPtr destinationRow = IntPtr.Add(destination, checked(y * destinationRowBytes));
                        CopyMemory(destinationRow, sourceRow, destinationRowBytes);
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
        finally
        {
            buffer.Unlock();
        }
    }

    private static unsafe void CopyMemory(IntPtr destination, IntPtr source, int length)
    {
        Buffer.MemoryCopy(source.ToPointer(), destination.ToPointer(), length, length);
    }

    private static long ResolveFrameDurationTicks100ns(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0)
        {
            return TimeSpan.FromMilliseconds(16).Ticks;
        }

        double frameDuration = TimeSpan.TicksPerSecond / fps;
        if (!double.IsFinite(frameDuration) || frameDuration < 1)
        {
            return 1;
        }

        if (frameDuration >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(frameDuration);
    }

    private static long ConvertToTimestamp100ns(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return 0;
        }

        double timestamp = time.TotalSeconds * HundredNanosecondsPerSecond;
        if (!double.IsFinite(timestamp) || timestamp <= 0)
        {
            return 0;
        }

        if (timestamp >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(timestamp);
    }

    private static UnmanagedFrameBufferPool CreatePixelBufferPool(VideoFormat format)
    {
        int bufferSize = checked(format.DestinationStride * format.Height);
        // Keep a small pool to avoid per-frame unmanaged allocations.
        const int maxRetainedBuffers = 4;
        return new UnmanagedFrameBufferPool(bufferSize, maxRetainedBuffers);
    }

    private readonly record struct VideoFormat(
        int Width,
        int Height,
        int DefaultStride,
        int SourceStride,
        int DestinationStride);

    private sealed class UnmanagedFrameBufferPool : IDisposable
    {
        private readonly ConcurrentBag<IntPtr> _buffers = new();
        private readonly int _bufferSizeBytes;
        private readonly int _maxRetainedBuffers;
        private int _retainedCount;
        private bool _disposed;

        public UnmanagedFrameBufferPool(int bufferSizeBytes, int maxRetainedBuffers)
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

    private sealed class VideoSession : IDisposable
    {
        private static readonly TimeSpan MinimumSeekJumpThreshold = TimeSpan.FromMilliseconds(900);

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

        public VideoSession(string path)
        {
            _reader = CreateConfiguredSourceReader(path, out _format, out _fps);
            _pixelBufferPool = CreatePixelBufferPool(_format);
            _frameDurationTicks100ns = ResolveFrameDurationTicks100ns(_fps);
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
            }

            GC.SuppressFinalize(this);
        }

        private SKImage? GetFrame(TimeSpan requestTime)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));

                long targetTimestamp = ConvertToTimestamp100ns(requestTime);
                if (ShouldSeek(targetTimestamp))
                {
                    SeekTo(targetTimestamp);
                }

                return ReadFrameAtOrAfter(targetTimestamp);
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

        private void SeekTo(long targetTimestamp)
        {
            _reader.SetCurrentPosition(targetTimestamp);
            _lastDecodedTimestamp = long.MinValue;
            _endOfStream = false;
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
                    if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        _endOfStream = true;
                        return null;
                    }

                    if ((streamFlags & (SourceReaderFlag.CurrentMediaTypeChanged | SourceReaderFlag.NativeMediaTypeChanged)) != 0)
                    {
                        RefreshMediaFormat();
                    }

                    if ((streamFlags & SourceReaderFlag.StreamTick) != 0 || sample is null)
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
            VideoFormat nextFormat = ReadVideoFormat(mediaType);
            if (nextFormat.DestinationStride != _format.DestinationStride || nextFormat.Height != _format.Height)
            {
                UnmanagedFrameBufferPool oldPool = _pixelBufferPool;
                _pixelBufferPool = CreatePixelBufferPool(nextFormat);
                oldPool.Dispose();
            }

            _format = nextFormat;
        }
    }
}
