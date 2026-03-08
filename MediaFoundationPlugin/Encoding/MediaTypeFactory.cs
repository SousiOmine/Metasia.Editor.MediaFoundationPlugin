using Vortice.MediaFoundation;

namespace MediaFoundationPlugin.Encoding;

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
        _audioBitsPerSample = audioBitsPerSample;
        _audioBitrate = audioBitrate;
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

    public IMFMediaType CreateAudioInputMediaType()
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, EncodingConstants.AudioFormatPcm);
        mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, _audioSampleRate);
        mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, _audioChannelCount);
        mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, _audioBitsPerSample);
        mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, _audioChannelCount * _audioBitsPerSample / 8);
        mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, _audioSampleRate * _audioChannelCount * _audioBitsPerSample / 8);
        return mediaType;
    }

    public IMFMediaType CreateAudioOutputMediaType()
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, EncodingConstants.AudioFormatAac);
        mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, _audioSampleRate);
        mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, _audioChannelCount);
        mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
        mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, _audioBitrate / 8);
        mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1);
        mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, _audioBitrate);
        mediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0);
        return mediaType;
    }

    public int AudioSampleRate => _audioSampleRate;
    public int AudioChannelCount => _audioChannelCount;
    public int AudioBitsPerSample => _audioBitsPerSample;
}
