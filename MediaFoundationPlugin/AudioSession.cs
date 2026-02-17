using System.Runtime.InteropServices;
using Metasia.Core.Sounds;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal sealed class AudioSession : IDisposable
{
    private const int TargetSampleRate = 44100;
    private const int TargetChannelCount = 2;

    private readonly object _sync = new();
    private readonly IMFSourceReader _reader;
    private bool _disposed;

    public AudioSession(string path)
    {
        _reader = SourceReaderFactory.CreateAudioReader(path, out _, out _, out _);
    }

    public Task<AudioChunk?> GetAudioAsync(TimeSpan? startTime, TimeSpan? duration)
    {
        return Task.Run(() => GetAudio(startTime, duration));
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

    private AudioChunk? GetAudio(TimeSpan? startTime, TimeSpan? duration)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));

            long startTimestamp = ResolveStartTimestamp(startTime);
            long requestedDuration = ResolveRequestedDuration(duration);

            _reader.SetCurrentPosition(Math.Max(startTimestamp, 0));

            return ReadAudioSamples(startTimestamp, requestedDuration);
        }
    }

    private static long ResolveStartTimestamp(TimeSpan? startTime)
    {
        return startTime.HasValue && startTime.Value > TimeSpan.Zero
            ? TimestampUtility.ConvertToTimestamp100ns(startTime.Value)
            : 0;
    }

    private static long ResolveRequestedDuration(TimeSpan? duration)
    {
        return duration.HasValue && duration.Value > TimeSpan.Zero
            ? TimestampUtility.ConvertToTimestamp100ns(duration.Value)
            : long.MaxValue;
    }

    private AudioChunk? ReadAudioSamples(long startTimestamp, long requestedDuration)
    {
        var sampleBuffers = new List<float[]>();
        
        long samplesEndTimestamp;
        if (startTimestamp > 0 && requestedDuration > long.MaxValue - startTimestamp)
        {
            samplesEndTimestamp = long.MaxValue;
        }
        else
        {
            samplesEndTimestamp = startTimestamp + requestedDuration;
        }
        int totalSamples = 0;

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

                if (timestamp >= samplesEndTimestamp)
                {
                    break;
                }

                float[]? audioData = ExtractAudioData(sample);
                if (audioData is null)
                {
                    continue;
                }

                sampleBuffers.Add(audioData);
                totalSamples += audioData.Length;
            }
        }

        if (totalSamples == 0)
        {
            return new AudioChunk(new AudioFormat(TargetSampleRate, TargetChannelCount), 0);
        }

        return CreateAudioChunk(sampleBuffers, totalSamples);
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

    private static AudioChunk CreateAudioChunk(List<float[]> sampleBuffers, int totalSamples)
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

        return new AudioChunk(new AudioFormat(TargetSampleRate, TargetChannelCount), samples);
    }
}