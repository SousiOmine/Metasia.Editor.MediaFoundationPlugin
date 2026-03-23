namespace MediaFoundationPlugin;

public sealed record MediaFoundationOutputSettings(
    int VideoBitrate,
    int AudioBitrate,
    int? OutputWidth,
    int? OutputHeight)
{
    public static IReadOnlyList<int> SupportedAudioBitrates { get; } = [96_000, 128_000, 160_000, 192_000];
    public static IReadOnlyList<AudioBitrateOption> AudioBitrateOptions { get; } =
        SupportedAudioBitrates.Select(bitrate => new AudioBitrateOption(bitrate / 1000)).ToArray();

    public static MediaFoundationOutputSettings Default { get; } = new(8_000_000, 192_000, null, null);

    public static int NormalizeAudioBitrate(int bitrate)
    {
        int normalized = SupportedAudioBitrates[0];
        int bestDistance = Math.Abs(bitrate - normalized);

        foreach (int candidate in SupportedAudioBitrates.Skip(1))
        {
            int distance = Math.Abs(bitrate - candidate);
            if (distance < bestDistance)
            {
                normalized = candidate;
                bestDistance = distance;
            }
        }

        return normalized;
    }

    public static int NormalizeResolutionDimension(int value)
    {
        value = Math.Max(2, value);
        return value % 2 == 0 ? value : value + 1;
    }
}

public sealed record AudioBitrateOption(int BitrateKbps)
{
    public int Bitrate => BitrateKbps * 1000;

    public override string ToString()
    {
        return $"{BitrateKbps} kbps";
    }
}

internal static class MediaFoundationOutputFormatInfo
{
    public const string DisplayName = "MediaFoundation Video";
    public static readonly string[] SupportedExtensions = ["*.mp4", "*.mov", "*.m4v"];
}
