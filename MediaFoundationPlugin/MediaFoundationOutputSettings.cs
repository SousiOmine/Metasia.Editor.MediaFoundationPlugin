namespace MediaFoundationPlugin;

public sealed record MediaFoundationOutputSettings(int VideoBitrate, int AudioBitrate)
{
    public static IReadOnlyList<int> SupportedAudioBitrates { get; } = [96_000, 128_000, 160_000, 192_000];
    public static IReadOnlyList<AudioBitrateOption> AudioBitrateOptions { get; } =
        SupportedAudioBitrates.Select(bitrate => new AudioBitrateOption(bitrate / 1000)).ToArray();

    public static MediaFoundationOutputSettings Default { get; } = new(8_000_000, 192_000);

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
}

public sealed record AudioBitrateOption(int BitrateKbps)
{
    public int Bitrate => BitrateKbps * 1000;

    public override string ToString()
    {
        return $"{BitrateKbps} kbps";
    }
}
