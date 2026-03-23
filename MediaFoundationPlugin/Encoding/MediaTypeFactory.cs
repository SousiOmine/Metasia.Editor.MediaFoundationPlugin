using SharpGen.Runtime;
using Vortice.MediaFoundation;
using Vortice.Multimedia;

namespace MediaFoundationPlugin.Encoding;

public sealed record AudioEncodingConfiguration(int SampleRate, int ChannelCount, int BitsPerSample, int Bitrate)
{
    public int AvgBytesPerSecond => Bitrate / 8;
    public int BlockAlignment => ChannelCount * (BitsPerSample / 8);
    public int PcmAvgBytesPerSecond => SampleRate * BlockAlignment;
    public int PcmAvgBitrate => PcmAvgBytesPerSecond * 8;
}

public sealed record AudioOutputTypeCandidate(IMFMediaType MediaType, int SampleRate, int ChannelCount, int BitsPerSample, int AvgBytesPerSecond)
{
    public string Label => $"sampleRate={SampleRate}, channels={ChannelCount}, bits={BitsPerSample}, bitrate={AvgBytesPerSecond * 8}";
}

public sealed record VideoEncodingConfiguration(int? H264Profile, int? H264Level)
{
    public string ProfileLabel => H264Profile switch
    {
        100 => "high",
        77 => "main",
        66 => "base",
        _ => "default",
    };

    public string LevelLabel => H264Level?.ToString() ?? "default";
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
        mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        mediaType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
        mediaType.Set(MediaTypeAttributeKeys.SampleSize, checked((uint)(width * height * 3 / 2)));
        mediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
        return mediaType;
    }

    public IMFMediaType CreateVideoOutputMediaType(int width, int height, double framerate, VideoEncodingConfiguration configuration)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        mediaType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
        mediaType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio((int)(framerate * 10000), 10000));
        mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
        mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, _videoBitrate);
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);

        if (configuration.H264Profile is int profile)
        {
            mediaType.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile);
        }

        if (configuration.H264Level is int level)
        {
            mediaType.Set(MediaTypeAttributeKeys.Mpeg2Level, level);
        }

        return mediaType;
    }

    public IMFMediaType CreateAudioInputMediaType(AudioEncodingConfiguration configuration)
    {
        WaveFormat waveFormat = new(configuration.SampleRate, configuration.BitsPerSample, configuration.ChannelCount);
        IMFMediaType mediaType = MediaFactory.MFCreateAudioMediaType(ref waveFormat);
        mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, configuration.PcmAvgBitrate);
        mediaType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
        mediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
        return mediaType;
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
        mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1);
        mediaType.Set(MediaTypeAttributeKeys.AacPayloadType, 0);
        mediaType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29);
        mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, configuration.Bitrate);
        mediaType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
        mediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0);
        return mediaType;
    }

    public IReadOnlyList<AudioOutputTypeCandidate> GetAudioOutputTypeCandidates(AudioEncodingConfiguration configuration)
    {
        MediaFactory.MFTranscodeGetAudioOutputAvailableTypes(AudioFormatGuids.Aac, 0, null, out IMFCollection availableTypes).CheckError();

        using (availableTypes)
        {
            List<AudioOutputTypeCandidate> candidates = [];

            int count = (int)availableTypes.ElementCount;
            for (int index = 0; index < count; index++)
            {
                using ComObject unknown = (ComObject)availableTypes.GetElement(index);
                using var sourceType = unknown.QueryInterface<IMFMediaType>();

                int sampleRate = (int)MediaFactory.MFGetAttributeUInt32(sourceType, MediaTypeAttributeKeys.AudioSamplesPerSecond, 0);
                int channelCount = (int)MediaFactory.MFGetAttributeUInt32(sourceType, MediaTypeAttributeKeys.AudioNumChannels, 0);
                int bitsPerSample = (int)MediaFactory.MFGetAttributeUInt32(sourceType, MediaTypeAttributeKeys.AudioBitsPerSample, 0);
                int avgBytesPerSecond = (int)MediaFactory.MFGetAttributeUInt32(sourceType, MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 0);

                if (sampleRate <= 0 || channelCount <= 0 || avgBytesPerSecond <= 0)
                {
                    continue;
                }

                IMFMediaType clonedType = MediaFactory.MFCreateMediaType();
                sourceType.CopyAllItems(clonedType).CheckError();
                candidates.Add(new AudioOutputTypeCandidate(clonedType, sampleRate, channelCount, bitsPerSample, avgBytesPerSecond));
            }

            if (candidates.Count == 0)
            {
                return [new AudioOutputTypeCandidate(CreateAudioOutputMediaType(configuration), configuration.SampleRate, configuration.ChannelCount, configuration.BitsPerSample, configuration.AvgBytesPerSecond)];
            }

            return candidates
                .OrderByDescending(candidate => candidate.SampleRate == configuration.SampleRate)
                .ThenByDescending(candidate => candidate.ChannelCount == configuration.ChannelCount)
                .ThenByDescending(candidate => candidate.BitsPerSample == 0 || candidate.BitsPerSample == configuration.BitsPerSample)
                .ThenBy(candidate => Math.Abs(candidate.AvgBytesPerSecond * 8 - configuration.Bitrate))
                .ToArray();
        }
    }

    public IReadOnlyList<VideoEncodingConfiguration> GetVideoEncodingCandidates(int width, int height, double framerate)
    {
        int? recommendedLevel = GetRecommendedH264Level(width, height, framerate);

        return
        [
            new VideoEncodingConfiguration(100, recommendedLevel),
            new VideoEncodingConfiguration(77, recommendedLevel),
            new VideoEncodingConfiguration(66, recommendedLevel),
            new VideoEncodingConfiguration(null, recommendedLevel),
            new VideoEncodingConfiguration(100, null),
            new VideoEncodingConfiguration(77, null),
            new VideoEncodingConfiguration(66, null),
            new VideoEncodingConfiguration(null, null),
        ];
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

    private static int? GetRecommendedH264Level(int width, int height, double framerate)
    {
        // 3840x2160@60 requires H.264 Level 5.2 on paper. Lower levels are kept to encoder defaults.
        if (width >= 3840 && height >= 2160 && framerate >= 59.5)
        {
            return 52;
        }

        if (width >= 3840 && height >= 2160)
        {
            return 51;
        }

        return null;
    }
}
