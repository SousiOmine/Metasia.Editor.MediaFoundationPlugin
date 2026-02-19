using System;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin.Encoding;

internal static class MediaSampleBuilder
{
    public static unsafe IMFSample CreateSampleFromBuffer(IntPtr sourceData, int dataSize, long timestamp100ns, long duration100ns)
    {
        IMFSample sample = MediaFactory.MFCreateSample();
        SampleExtensions.SetSampleTime(sample, timestamp100ns);
        SampleExtensions.SetSampleDuration(sample, duration100ns);

        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(dataSize);
        buffer.Lock(out IntPtr bufferData, out int _, out int _);

        Buffer.MemoryCopy(sourceData.ToPointer(), bufferData.ToPointer(), dataSize, dataSize);

        buffer.Unlock();
        MediaBufferExtensions.SetCurrentLength(buffer, dataSize);
        sample.AddBuffer(buffer);
        buffer.Dispose();

        return sample;
    }

    public static IMFSample CreateSampleFromManagedBuffer(byte[] data, int dataSize, long timestamp100ns, long duration100ns)
    {
        IMFSample sample = MediaFactory.MFCreateSample();
        SampleExtensions.SetSampleTime(sample, timestamp100ns);
        SampleExtensions.SetSampleDuration(sample, duration100ns);

        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(dataSize);
        buffer.Lock(out IntPtr bufferData, out int _, out int _);

        Marshal.Copy(data, 0, bufferData, dataSize);

        buffer.Unlock();
        MediaBufferExtensions.SetCurrentLength(buffer, dataSize);
        sample.AddBuffer(buffer);
        buffer.Dispose();

        return sample;
    }
}