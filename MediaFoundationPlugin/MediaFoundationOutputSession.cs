using Metasia.Editor.Plugin;
using MediaFoundationPlugin.Encoding;

namespace MediaFoundationPlugin;

public sealed class MediaFoundationOutputSession : IMediaOutputSession
{
    public string Name => "MediaFoundation MP4";
    public string[] SupportedExtensions => ["*.mp4"];
    public Avalonia.Controls.Control? SettingsView { get; }

    private readonly MediaFoundationOutputSettingsViewModel _viewModel;

    public MediaFoundationOutputSession()
    {
        _viewModel = new MediaFoundationOutputSettingsViewModel();
        SettingsView = new MediaFoundationOutputSettingsView
        {
            DataContext = _viewModel
        };
    }

    public Metasia.Core.Encode.EncoderBase CreateEncoderInstance()
    {
        var settings = _viewModel.CreateSettings();
        var mediaTypeFactory = new MediaTypeFactory(
            audioBitrate: settings.AudioBitrate,
            videoBitrate: settings.VideoBitrate);
        return new MediaFoundationOutputEncoder(mediaTypeFactory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
