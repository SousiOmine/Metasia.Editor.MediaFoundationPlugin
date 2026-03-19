using Vortice.MediaFoundation;
using Vortice.Multimedia;

namespace MediaFoundationPlugin.Encoding;

public sealed record AudioEncodingConfiguration(int SampleRate, int ChannelCount, int BitsPerSample, int Bitrate)
{
    public int AvgBytesPerSecond => Bitrate / 8;
}

public sealed class MediaTypeFactory
{
    private readonly int _audioSampleRate;
    private readonly int _audioChannelCount;
    private readonly int _audioBitsPerSample;
    private readonly int _audioBitrate;
    private readonly int _videoBitrate;

    public MediaTypeFactory(
        int audioSampleRate = EncodingConstants.AudioSampleRate,
        int audioChannelCount = EncodingConstants.AudioChannelCount,
        int audioBitsPerSample = EncodingConstants.AudioBitsPerSample,
        int audioBitrate = EncodingConstants.AudioBitrate,
        int videoBitrate = EncodingConstants.DefaultVideoBitrate)
    {
        _audioSampleRate = audioSampleRate;
        _audioChannelCount = audioChannelCount;
        _audioBitsPerSample = NormalizeAudioBitsPerSample(audioBitsPerSample);
        _audioBitrate = MediaFoundationOutputSettings.NormalizeAudioBitrate(audioBitrate);
        _videoBitrate = videoBitrate;
    }

    public IMFMediaType CreateVideoInputMediaType(int width, int height, double framerate)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        mediaType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
        mediaType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio((int)(framerate * 10000), 10000));
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        mediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
        return mediaType;
    }

    public IMFMediaType CreateVideoOutputMediaType(int width, int height, double framerate)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        mediaType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
        mediaType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio((int)(framerate * 10000), 10000));
        mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, _videoBitrate);
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        return mediaType;
    }

    public IMFMediaType CreateAudioInputMediaType(AudioEncodingConfiguration configuration)
    {
        WaveFormat waveFormat = new(configuration.SampleRate, configuration.BitsPerSample, configuration.ChannelCount);
        return MediaFactory.MFCreateAudioMediaType(ref waveFormat);
    }

    public IMFMediaType CreateAudioOutputMediaType(AudioEncodingConfiguration configuration)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, configuration.SampleRate);
        mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, configuration.ChannelCount);
        mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, configuration.BitsPerSample);
        mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, configuration.AvgBytesPerSecond);
        return mediaType;
    }

    public IReadOnlyList<AudioEncodingConfiguration> GetAudioEncodingCandidates()
    {
        var sampleRates = new[] { _audioSampleRate, 44100, 48000 }
            .Where(sampleRate => sampleRate is 44100 or 48000)
            .Distinct()
            .ToArray();

        var bitrates = MediaFoundationOutputSettings.SupportedAudioBitrates
            .OrderBy(bitrate => bitrate == _audioBitrate ? -1 : 0)
            .ThenBy(bitrate => Math.Abs(bitrate - _audioBitrate))
            .ToArray();

        List<AudioEncodingConfiguration> candidates = [];
        foreach (int sampleRate in sampleRates)
        {
            foreach (int bitrate in bitrates)
            {
                candidates.Add(new AudioEncodingConfiguration(sampleRate, _audioChannelCount, _audioBitsPerSample, bitrate));
            }
        }

        return candidates;
    }

    public int AudioSampleRate => _audioSampleRate;
    public int AudioChannelCount => _audioChannelCount;
    public int AudioBitsPerSample => _audioBitsPerSample;

    private static int NormalizeAudioBitsPerSample(int audioBitsPerSample)
    {
        return audioBitsPerSample == EncodingConstants.AudioBitsPerSample
            ? audioBitsPerSample
            : EncodingConstants.AudioBitsPerSample;
    }
}
