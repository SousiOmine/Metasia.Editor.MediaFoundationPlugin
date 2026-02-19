using System;

namespace MediaFoundationPlugin.Encoding;

internal static class EncodingConstants
{
    public const int AudioSampleRate = 48000;
    public const int AudioChannelCount = 2;
    public const int AudioBitsPerSample = 16;
    public const int AudioBitrate = 192000;
    public const int DefaultVideoBitrate = 8000000;

    public static readonly TimeSpan TimeSpanScale = TimeSpan.FromTicks(1);
    public const long HundredNanosecondsPerSecond = 10000000;

    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
}