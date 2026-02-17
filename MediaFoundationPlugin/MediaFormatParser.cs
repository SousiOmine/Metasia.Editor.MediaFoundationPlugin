using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal readonly record struct VideoFormat(
    int Width,
    int Height,
    int DefaultStride,
    int SourceStride,
    int DestinationStride);

internal static class MediaFormatParser
{
    public static double ReadFramesPerSecond(IMFMediaType mediaType)
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

    public static VideoFormat ReadVideoFormat(IMFMediaType mediaType)
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
}