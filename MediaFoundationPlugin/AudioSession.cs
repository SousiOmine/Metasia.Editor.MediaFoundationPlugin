using System.Runtime.InteropServices;
using Metasia.Core.Sounds;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal sealed class AudioSession : IDisposable
{
    private const int TargetChannelCount = 2;
    private const int TimestampJitterToleranceFrames = 2;
    private const long HundredNanosecondsPerSecond = 10_000_000;
    private const long CacheWindowSamples = 176400;
    private const long CachePrerollSamples = 6615;

    private readonly object _sync = new();
    private readonly IMFSourceReader _reader;
    private readonly int _readerSampleRate;
    private readonly int _readerChannelCount;
    private bool _disposed;
    private AudioChunkCache? _cache;

    internal long LastAccessTicks;

    public AudioSession(string path)
    {
        _reader = SourceReaderFactory.CreateAudioReader(path, out int sampleRate, out int channelCount, out _);
        _readerSampleRate = sampleRate > 0 ? sampleRate : 44100;
        _readerChannelCount = channelCount > 0 ? channelCount : TargetChannelCount;
    }

    public Task<AudioChunk?> GetAudioAsync(TimeSpan? startTime, TimeSpan? duration)
    {
        int defaultSampleRate = 44100;
        long startSample = (long)((startTime ?? TimeSpan.Zero).TotalSeconds * defaultSampleRate);
        long sampleCount = duration.HasValue ? (long)(duration.Value.TotalSeconds * defaultSampleRate) : long.MaxValue;
        
        return Task.Run(() => GetAudioBySample(startSample, sampleCount, defaultSampleRate));
    }
    
    public Task<AudioChunk?> GetAudioBySampleAsync(long startSample, long sampleCount, int sampleRate)
    {
        return Task.Run(() => GetAudioBySample(startSample, sampleCount, sampleRate));
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
        }

        GC.SuppressFinalize(this);
    }

    private AudioChunk? GetAudioBySample(long startSample, long sampleCount, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));
            LastAccessTicks = DateTime.UtcNow.Ticks;

            if (startSample < 0)
            {
                startSample = 0;
            }

            long startTimestamp = SampleToTimestamp100ns(startSample, sampleRate);
            long requestedDuration = sampleCount > 0 && sampleCount < long.MaxValue 
                ? SampleToTimestamp100ns(sampleCount, sampleRate) 
                : long.MaxValue;

            if (requestedDuration == long.MaxValue)
            {
                if (!TrySetPosition(Math.Max(startTimestamp, 0)))
                {
                    return null;
                }
                return ReadAudioSamplesAsRequestedFormat(startTimestamp, requestedDuration, sampleRate);
            }

            long requiredFrames = sampleCount;
            if (requiredFrames <= 0)
            {
                return new AudioChunk(new AudioFormat(sampleRate, TargetChannelCount), 0);
            }

            if (_cache is not null && _cache.SampleRate == sampleRate && TryReadFromCache(startSample, requiredFrames, out AudioChunk? cachedChunk) && cachedChunk is not null)
            {
                return cachedChunk;
            }

            long prerollTimestamp = SampleToTimestamp100ns(CachePrerollSamples, sampleRate);
            long cacheWindowDuration = SampleToTimestamp100ns(CacheWindowSamples, sampleRate);
            long expandedDuration = requestedDuration > long.MaxValue / 4 ? long.MaxValue : requestedDuration * 4;
            long windowDuration = Math.Max(cacheWindowDuration, expandedDuration);
            long windowStartTimestamp = Math.Max(0, startTimestamp - prerollTimestamp);
            long windowStartSample = Timestamp100nsToSample(windowStartTimestamp, sampleRate);

            if (!TrySetPosition(windowStartTimestamp))
            {
                return null;
            }
            AudioChunk? windowChunk = ReadAudioSamplesAsRequestedFormat(windowStartTimestamp, windowDuration, sampleRate);
            if (windowChunk is null)
            {
                return null;
            }

            _cache = new AudioChunkCache(windowStartSample, sampleRate, windowChunk);
            return SliceChunk(windowChunk, windowStartSample, startSample, requiredFrames);
        }
    }

    private static long SampleToTimestamp100ns(long sample, int sampleRate)
    {
        return sample * HundredNanosecondsPerSecond / sampleRate;
    }

    private static long Timestamp100nsToSample(long timestamp100ns, int sampleRate)
    {
        return timestamp100ns * sampleRate / HundredNanosecondsPerSecond;
    }

    private AudioChunk? ReadAudioSamplesAsRequestedFormat(long startTimestamp, long requestedDuration, int requestedSampleRate)
    {
        AudioChunk? decoded = ReadAudioSamplesDecoded(startTimestamp, requestedDuration, _readerSampleRate, _readerChannelCount);
        if (decoded is null)
        {
            return null;
        }

        if (decoded.Format.SampleRate == requestedSampleRate && decoded.Format.ChannelCount == TargetChannelCount)
        {
            return decoded;
        }

        long targetFrames;
        if (requestedDuration == long.MaxValue)
        {
            targetFrames = RescaleFrameCount(decoded.Length, decoded.Format.SampleRate, requestedSampleRate);
        }
        else
        {
            targetFrames = Timestamp100nsToSample(requestedDuration, requestedSampleRate);
        }

        if (targetFrames < 0)
        {
            targetFrames = 0;
        }

        var targetFormat = new AudioFormat(requestedSampleRate, TargetChannelCount);
        IAudioChunk converted = AudioChunkConverter.ConvertToFormat(decoded, targetFormat, targetFrames);
        return converted is AudioChunk convertedChunk ? convertedChunk : new AudioChunk(converted.Format, converted.Samples);
    }

    private AudioChunk? ReadAudioSamplesDecoded(long startTimestamp, long requestedDuration, int sampleRate, int channelCount)
    {
        if (requestedDuration == long.MaxValue)
        {
            return ReadAudioSamplesUnbounded(startTimestamp, sampleRate, channelCount);
        }

        long targetFrames = Timestamp100nsToSample(requestedDuration, sampleRate);
        if (targetFrames <= 0)
        {
            return new AudioChunk(new AudioFormat(sampleRate, channelCount), 0);
        }

        long maxArrayFrames = int.MaxValue / channelCount;
        if (targetFrames > maxArrayFrames)
        {
            targetFrames = maxArrayFrames;
        }

        var format = new AudioFormat(sampleRate, channelCount);
        var output = new AudioChunk(format, targetFrames);
        int channels = channelCount;
        long nextExpectedFrame = 0;
        bool hasExpectedFrame = false;

        while (true)
        {
            IMFSample? sample = _reader.ReadSample(
                SourceReaderIndex.FirstAudioStream,
                SourceReaderControlFlag.None,
                out int _,
                out SourceReaderFlag streamFlags,
                out long timestamp);

            using (sample)
            {
                if (streamFlags.IsEndOfStream())
                {
                    break;
                }

                if (streamFlags.IsMediaTypeChanged())
                {
                    continue;
                }

                if (streamFlags.IsStreamTick() || sample is null)
                {
                    continue;
                }

                float[]? audioData = ExtractAudioData(sample);
                if (audioData is null || audioData.Length == 0)
                {
                    continue;
                }

                int sampleFrames = audioData.Length / channels;
                if (sampleFrames <= 0)
                {
                    continue;
                }

                long sampleStartFrameByTimestamp = TimestampDeltaToFrameRounded(timestamp - startTimestamp, sampleRate);
                long sampleStartFrame = ResolveSampleStartFrame(sampleStartFrameByTimestamp, nextExpectedFrame, hasExpectedFrame);
                long sampleEndFrame = SaturatingAdd(sampleStartFrame, sampleFrames);
                hasExpectedFrame = true;
                nextExpectedFrame = sampleEndFrame;

                if (sampleEndFrame <= 0)
                {
                    continue;
                }

                if (sampleStartFrame >= targetFrames)
                {
                    break;
                }

                long destinationStartFrame = Math.Max(0, sampleStartFrame);
                long sourceStartFrame = destinationStartFrame - sampleStartFrame;
                long copyFrames = Math.Min(targetFrames, sampleEndFrame) - destinationStartFrame;
                if (copyFrames <= 0)
                {
                    continue;
                }

                CopyInterleavedFrames(
                    audioData,
                    (int)sourceStartFrame,
                    output.Samples,
                    destinationStartFrame,
                    (int)copyFrames,
                    channels);
            }
        }

        return output;
    }

    private AudioChunk? ReadAudioSamplesUnbounded(long startTimestamp, int sampleRate, int channelCount)
    {
        var sampleBuffers = new List<float[]>();
        int totalSamples = 0;
        int channels = channelCount;
        long nextExpectedFrame = 0;
        bool hasExpectedFrame = false;

        while (true)
        {
            IMFSample? sample = _reader.ReadSample(
                SourceReaderIndex.FirstAudioStream,
                SourceReaderControlFlag.None,
                out int _,
                out SourceReaderFlag streamFlags,
                out long timestamp);

            using (sample)
            {
                if (streamFlags.IsEndOfStream())
                {
                    break;
                }

                if (streamFlags.IsMediaTypeChanged() || streamFlags.IsStreamTick() || sample is null)
                {
                    continue;
                }

                float[]? audioData = ExtractAudioData(sample);
                if (audioData is null || audioData.Length == 0)
                {
                    continue;
                }

                int sampleFrames = audioData.Length / channels;
                if (sampleFrames <= 0)
                {
                    continue;
                }

                long sampleStartFrameByTimestamp = TimestampDeltaToFrameRounded(timestamp - startTimestamp, sampleRate);
                long sampleStartFrame = ResolveSampleStartFrame(sampleStartFrameByTimestamp, nextExpectedFrame, hasExpectedFrame);
                long sampleEndFrame = SaturatingAdd(sampleStartFrame, sampleFrames);
                hasExpectedFrame = true;
                nextExpectedFrame = sampleEndFrame;
                if (sampleEndFrame <= 0)
                {
                    continue;
                }

                if (sampleStartFrame >= 0)
                {
                    sampleBuffers.Add(audioData);
                    totalSamples += audioData.Length;
                    continue;
                }

                int sourceStartFrame = (int)Math.Min(sampleFrames, -sampleStartFrame);
                int copyFrames = sampleFrames - sourceStartFrame;
                if (copyFrames <= 0)
                {
                    continue;
                }

                int copySamples = copyFrames * channels;
                var trimmed = new float[copySamples];
                Buffer.BlockCopy(
                    audioData,
                    sourceStartFrame * channels * sizeof(float),
                    trimmed,
                    0,
                    copySamples * sizeof(float));
                sampleBuffers.Add(trimmed);
                totalSamples += copySamples;
            }
        }

        return totalSamples == 0
            ? new AudioChunk(new AudioFormat(sampleRate, channelCount), 0)
            : CreateAudioChunk(sampleBuffers, totalSamples, sampleRate, channelCount);
    }

    private static float[]? ExtractAudioData(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        using BufferLockContext lockContext = BufferHelper.LockBuffer(buffer);

        if (lockContext.Data == IntPtr.Zero || lockContext.CurrentLength <= 0)
        {
            return null;
        }

        int sampleCount = lockContext.CurrentLength / sizeof(float);
        var audioData = new float[sampleCount];
        Marshal.Copy(lockContext.Data, audioData, 0, sampleCount);
        return audioData;
    }

    private static AudioChunk CreateAudioChunk(List<float[]> sampleBuffers, int totalSamples, int sampleRate, int channelCount)
    {
        var samples = new double[totalSamples];
        int offset = 0;
        foreach (float[] buffer in sampleBuffers)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                samples[offset + i] = buffer[i];
            }
            offset += buffer.Length;
        }

        return new AudioChunk(new AudioFormat(sampleRate, channelCount), samples);
    }

    private static void CopyInterleavedFrames(float[] source, int sourceStartFrame, double[] destination, long destinationStartFrame, int frameCount, int channels)
    {
        int sourceStartIndex = sourceStartFrame * channels;
        int destinationStartIndex = checked((int)(destinationStartFrame * channels));

        for (int frame = 0; frame < frameCount; frame++)
        {
            int sourceBase = sourceStartIndex + (frame * channels);
            int destinationBase = destinationStartIndex + (frame * channels);
            for (int channel = 0; channel < channels; channel++)
            {
                destination[destinationBase + channel] = source[sourceBase + channel];
            }
        }
    }

    private bool TryReadFromCache(long requestStartSample, long requestSampleCount, out AudioChunk? chunk)
    {
        chunk = null;
        if (_cache is null)
        {
            return false;
        }

        long requestEndSample = requestStartSample + requestSampleCount;
        if (requestStartSample < _cache.StartSample || requestEndSample > _cache.EndSample)
        {
            return false;
        }

        chunk = SliceChunk(_cache.Chunk, _cache.StartSample, requestStartSample, requestSampleCount);
        return true;
    }

    private static AudioChunk SliceChunk(AudioChunk source, long sourceStartSample, long requestStartSample, long requestSampleCount)
    {
        var format = source.Format;
        var output = new AudioChunk(format, requestSampleCount);
        if (requestSampleCount <= 0 || source.Length <= 0)
        {
            return output;
        }

        long offsetFrames = requestStartSample - sourceStartSample;
        if (offsetFrames < 0)
        {
            offsetFrames = 0;
        }

        long copyFrames = Math.Min(requestSampleCount, source.Length - offsetFrames);
        if (copyFrames <= 0)
        {
            return output;
        }

        int channels = format.ChannelCount;
        for (long frame = 0; frame < copyFrames; frame++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                long srcIndex = ((offsetFrames + frame) * channels) + ch;
                long dstIndex = (frame * channels) + ch;
                output.Samples[dstIndex] = source.Samples[srcIndex];
            }
        }

        return output;
    }

    private static long TimestampDeltaToFrameRounded(long timestampDelta100ns, int sampleRate)
    {
        double frame = timestampDelta100ns * (double)sampleRate / HundredNanosecondsPerSecond;
        if (!double.IsFinite(frame))
        {
            return 0;
        }

        if (frame >= long.MaxValue)
        {
            return long.MaxValue;
        }

        if (frame <= long.MinValue)
        {
            return long.MinValue;
        }

        return (long)Math.Round(frame, MidpointRounding.AwayFromZero);
    }

    private static long ResolveSampleStartFrame(long frameFromTimestamp, long nextExpectedFrame, bool hasExpectedFrame)
    {
        if (!hasExpectedFrame)
        {
            return frameFromTimestamp;
        }

        long drift = frameFromTimestamp - nextExpectedFrame;
        return Math.Abs(drift) <= TimestampJitterToleranceFrames ? nextExpectedFrame : frameFromTimestamp;
    }

    private static long SaturatingAdd(long value, int addend)
    {
        if (addend >= 0 && value > long.MaxValue - addend)
        {
            return long.MaxValue;
        }

        if (addend < 0 && value < long.MinValue - addend)
        {
            return long.MinValue;
        }

        return value + addend;
    }

    private static long RescaleFrameCount(long sourceFrameCount, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceFrameCount <= 0 || sourceSampleRate <= 0 || targetSampleRate <= 0)
        {
            return 0;
        }

        double scaled = sourceFrameCount * (double)targetSampleRate / sourceSampleRate;
        if (!double.IsFinite(scaled))
        {
            return long.MaxValue;
        }

        if (scaled >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private bool TrySetPosition(long position)
    {
        try
        {
            _reader.SetCurrentPosition(position);
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    private sealed class AudioChunkCache(long startSample, int sampleRate, AudioChunk chunk)
    {
        public long StartSample { get; } = startSample;
        public int SampleRate { get; } = sampleRate;
        public AudioChunk Chunk { get; } = chunk;
        public long EndSample => StartSample + Chunk.Length;
    }
}
