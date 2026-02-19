using System;
using Metasia.Core.Sounds;

namespace MediaFoundationPlugin.Encoding;

internal static class AudioToPcmConverter
{
    public static byte[] ConvertToPcm(IAudioChunk chunk, int sampleCount, int channelCount, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        int bufferSize = sampleCount * channelCount * bytesPerSample;
        byte[] pcmData = new byte[bufferSize];

        for (int i = 0; i < sampleCount * channelCount; i++)
        {
            double audioSample = Math.Clamp(chunk.Samples[i], -1.0, 1.0);
            short pcmValue = (short)(audioSample * short.MaxValue);

            int byteIndex = i * bytesPerSample;
            pcmData[byteIndex] = (byte)(pcmValue & 0xFF);
            pcmData[byteIndex + 1] = (byte)((pcmValue >> 8) & 0xFF);
        }

        return pcmData;
    }

    public static int CalculateBufferSize(int sampleCount, int channelCount, int bitsPerSample)
    {
        return sampleCount * channelCount * bitsPerSample / 8;
    }
}