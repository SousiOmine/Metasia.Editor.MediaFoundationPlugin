using ReactiveUI;

namespace MediaFoundationPlugin;

public sealed class MediaFoundationOutputSettingsViewModel : ReactiveObject
{
    public IReadOnlyList<AudioBitrateOption> AudioBitrateOptions { get; } = MediaFoundationOutputSettings.AudioBitrateOptions;

    public int VideoBitrateKbps
    {
        get => _videoBitrateKbps;
        set => this.RaiseAndSetIfChanged(ref _videoBitrateKbps, Math.Max(1000, value));
    }

    public AudioBitrateOption? SelectedAudioBitrateOption
    {
        get => _selectedAudioBitrateOption;
        set
        {
            if (value is null)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedAudioBitrateOption, value);
        }
    }

    private int _videoBitrateKbps = MediaFoundationOutputSettings.Default.VideoBitrate / 1000;
    private AudioBitrateOption? _selectedAudioBitrateOption =
        MediaFoundationOutputSettings.AudioBitrateOptions.FirstOrDefault(option => option.Bitrate == MediaFoundationOutputSettings.Default.AudioBitrate)
        ?? MediaFoundationOutputSettings.AudioBitrateOptions[^1];

    public MediaFoundationOutputSettings CreateSettings()
    {
        int audioBitrate = MediaFoundationOutputSettings.NormalizeAudioBitrate(
            SelectedAudioBitrateOption?.Bitrate ?? MediaFoundationOutputSettings.Default.AudioBitrate);
        return new MediaFoundationOutputSettings(VideoBitrateKbps * 1000, audioBitrate);
    }
}
