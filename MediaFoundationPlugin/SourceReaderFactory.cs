using System.Diagnostics;
using SharpGen.Runtime;
using SharpGen.Runtime.Win32;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class SourceReaderFactory
{
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

    public static IMFSourceReader CreateAudioReader(string path, out int sampleRate, out int channelCount, out long duration100ns)
    {
        const int bitsPerSample = 32;
        const int bytesPerSample = bitsPerSample / 8;
        const int targetSampleRate = 44100;
        const int targetChannelCount = 2;

        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, false).CheckError();
        attributes.Set(SourceReaderAttributeKeys.DisableDxva, true).CheckError();

        IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, bitsPerSample).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, targetSampleRate).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AudioNumChannels, targetChannelCount).CheckError();

            uint blockAlignment = (uint)(targetChannelCount * bytesPerSample);
            uint avgBytesPerSecond = (uint)(targetSampleRate * blockAlignment);
            outputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, blockAlignment).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, avgBytesPerSecond).CheckError();

            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, outputType);

            using IMFMediaType actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            sampleRate = (int)MediaFactory.MFGetAttributeUInt32(actualType, MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)targetSampleRate);
            channelCount = (int)MediaFactory.MFGetAttributeUInt32(actualType, MediaTypeAttributeKeys.AudioNumChannels, (uint)targetChannelCount);

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
}