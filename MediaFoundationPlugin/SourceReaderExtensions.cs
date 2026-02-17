using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class SourceReaderExtensions
{
    public static bool IsEndOfStream(this SourceReaderFlag flags)
    {
        return (flags & SourceReaderFlag.EndOfStream) != 0;
    }

    public static bool IsMediaTypeChanged(this SourceReaderFlag flags)
    {
        return (flags & (SourceReaderFlag.CurrentMediaTypeChanged | SourceReaderFlag.NativeMediaTypeChanged)) != 0;
    }

    public static bool IsStreamTick(this SourceReaderFlag flags)
    {
        return (flags & SourceReaderFlag.StreamTick) != 0;
    }
}