namespace MediaFoundationPlugin;

public sealed record MediaFoundationOutputSettings(int VideoBitrate, int AudioBitrate)
{
    public static MediaFoundationOutputSettings Default { get; } = new(8_000_000, 192_000);
}
