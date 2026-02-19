using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class SampleExtensions
{
    public static void SetSampleTime(IMFSample sample, long hnsSampleTime)
    {
        sample.SampleTime = hnsSampleTime;
    }

    public static void SetSampleDuration(IMFSample sample, long hnsSampleDuration)
    {
        sample.SampleDuration = hnsSampleDuration;
    }
}

internal static class MediaBufferExtensions
{
    public static void SetCurrentLength(IMFMediaBuffer buffer, int length)
    {
        buffer.CurrentLength = length;
    }
}