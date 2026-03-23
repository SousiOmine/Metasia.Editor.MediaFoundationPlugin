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

    public bool UseCustomResolution
    {
        get => _useCustomResolution;
        set => this.RaiseAndSetIfChanged(ref _useCustomResolution, value);
    }

    public int CustomWidth
    {
        get => _customWidth;
        set => this.RaiseAndSetIfChanged(ref _customWidth, MediaFoundationOutputSettings.NormalizeResolutionDimension(value));
    }

    public int CustomHeight
    {
        get => _customHeight;
        set => this.RaiseAndSetIfChanged(ref _customHeight, MediaFoundationOutputSettings.NormalizeResolutionDimension(value));
    }

    private int _videoBitrateKbps = MediaFoundationOutputSettings.Default.VideoBitrate / 1000;
    private AudioBitrateOption? _selectedAudioBitrateOption =
        MediaFoundationOutputSettings.AudioBitrateOptions.FirstOrDefault(option => option.Bitrate == MediaFoundationOutputSettings.Default.AudioBitrate)
        ?? MediaFoundationOutputSettings.AudioBitrateOptions[^1];
    private bool _useCustomResolution;
    private int _customWidth = 1920;
    private int _customHeight = 1080;

    public MediaFoundationOutputSettings CreateSettings()
    {
        int audioBitrate = MediaFoundationOutputSettings.NormalizeAudioBitrate(
            SelectedAudioBitrateOption?.Bitrate ?? MediaFoundationOutputSettings.Default.AudioBitrate);

        int? outputWidth = UseCustomResolution
            ? MediaFoundationOutputSettings.NormalizeResolutionDimension(CustomWidth)
            : null;
        int? outputHeight = UseCustomResolution
            ? MediaFoundationOutputSettings.NormalizeResolutionDimension(CustomHeight)
            : null;

        return new MediaFoundationOutputSettings(VideoBitrateKbps * 1000, audioBitrate, outputWidth, outputHeight);
    }
}
