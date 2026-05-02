using System.Diagnostics;
using SharpGen.Runtime;
using SharpGen.Runtime.Win32;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class SourceReaderFactory
{
    private const int DefaultAudioSampleRate = 44100;
    private const int DefaultAudioChannelCount = 2;
    private const int DefaultPcmBitsPerSample = 16;

    public static IMFSourceReader CreateVideoReader(string path, bool useOptimizedPipeline, out VideoFormat format, out double fps)
    {
        try
        {
            if (useOptimizedPipeline)
            {
                return CreateVideoReaderCore(path, optimized: true, out format, out fps);
            }

            return CreateVideoReaderCore(path, optimized: false, out format, out fps);
        }
        catch (Exception ex) when (useOptimizedPipeline)
        {
            Debug.WriteLine($"MediaFoundationPlugin: optimized source-reader pipeline unavailable, falling back. path={path}, error={ex.Message}");
            return CreateVideoReaderCore(path, optimized: false, out format, out fps);
        }
    }

    private static IMFSourceReader CreateVideoReaderCore(string path, bool optimized, out VideoFormat format, out double fps)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(optimized ? 6u : 2u);
        if (optimized)
        {
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, false).CheckError();
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, false).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableCameraPlugins, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();
        }
        else
        {
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, true).CheckError();
        }

        IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

            using IMFMediaType mediaType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            format = MediaFormatParser.ReadVideoFormat(mediaType);
            fps = MediaFormatParser.ReadFramesPerSecond(mediaType);

            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public static IMFSourceReader CreateAudioReader(
        string path,
        out AudioReaderFormat format,
        out long duration100ns)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, false).CheckError();
        attributes.Set(SourceReaderAttributeKeys.DisableDxva, true).CheckError();

        IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, outputType);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

            using IMFMediaType actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            format = ReadAudioReaderFormat(actualType);

            duration100ns = 0;
            try
            {
                Variant durationVariant = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
                if (durationVariant.Value is long durationValue)
                {
                    duration100ns = durationValue;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediaFoundationPlugin: failed to get duration. error={ex.Message}");
            }

            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static AudioReaderFormat ReadAudioReaderFormat(IMFMediaType mediaType)
    {
        Guid subtype = mediaType.GetGUID(MediaTypeAttributeKeys.Subtype);
        int sampleRate = (int)MediaFactory.MFGetAttributeUInt32(
            mediaType,
            MediaTypeAttributeKeys.AudioSamplesPerSecond,
            DefaultAudioSampleRate);
        int channelCount = (int)MediaFactory.MFGetAttributeUInt32(
            mediaType,
            MediaTypeAttributeKeys.AudioNumChannels,
            DefaultAudioChannelCount);
        int bitsPerSample = (int)MediaFactory.MFGetAttributeUInt32(
            mediaType,
            MediaTypeAttributeKeys.AudioBitsPerSample,
            subtype == AudioFormatGuids.Float ? 32u : DefaultPcmBitsPerSample);
        int blockAlignment = (int)MediaFactory.MFGetAttributeUInt32(
            mediaType,
            MediaTypeAttributeKeys.AudioBlockAlignment,
            (uint)Math.Max(1, channelCount * Math.Max(1, bitsPerSample / 8)));

        return new AudioReaderFormat(
            sampleRate > 0 ? sampleRate : DefaultAudioSampleRate,
            channelCount > 0 ? channelCount : DefaultAudioChannelCount,
            bitsPerSample > 0 ? bitsPerSample : DefaultPcmBitsPerSample,
            blockAlignment > 0 ? blockAlignment : Math.Max(1, channelCount * Math.Max(1, bitsPerSample / 8)),
            subtype);
    }
}

internal readonly record struct AudioReaderFormat(
    int SampleRate,
    int ChannelCount,
    int BitsPerSample,
    int BlockAlignment,
    Guid Subtype);
