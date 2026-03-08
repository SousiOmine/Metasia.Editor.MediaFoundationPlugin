using ReactiveUI;

namespace MediaFoundationPlugin;

public sealed class MediaFoundationOutputSettingsViewModel : ReactiveObject
{
    public int VideoBitrateKbps
    {
        get => _videoBitrateKbps;
        set => this.RaiseAndSetIfChanged(ref _videoBitrateKbps, Math.Max(1000, value));
    }

    public int AudioBitrateKbps
    {
        get => _audioBitrateKbps;
        set => this.RaiseAndSetIfChanged(ref _audioBitrateKbps, Math.Max(64, value));
    }

    private int _videoBitrateKbps = MediaFoundationOutputSettings.Default.VideoBitrate / 1000;
    private int _audioBitrateKbps = MediaFoundationOutputSettings.Default.AudioBitrate / 1000;

    public MediaFoundationOutputSettings CreateSettings()
    {
        return new MediaFoundationOutputSettings(VideoBitrateKbps * 1000, AudioBitrateKbps * 1000);
    }
}
