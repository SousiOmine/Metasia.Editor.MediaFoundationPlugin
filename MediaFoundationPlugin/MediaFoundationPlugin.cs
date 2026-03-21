using System.Collections.Concurrent;
using System.Diagnostics;
using Metasia.Core.Encode;
using Metasia.Core.Media;
using Metasia.Core.Sounds;
using Metasia.Editor.Plugin;
using SkiaSharp;

namespace MediaFoundationPlugin;

public sealed partial class MediaFoundationPlugin : IMediaInputPlugin, IMediaOutputPlugin, IDisposable
{
    public string PluginIdentifier { get; } = "SousiOmine.MediaFoundationPlugin";
    public string PluginVersion { get; } = "0.2.2";
    public string PluginName { get; } = "MediaFoundation Input/Output";

    string IMediaOutputPlugin.Name => "MediaFoundation MP4";
    string[] IMediaOutputPlugin.SupportedExtensions => ["*.mp4"];

    public IEnumerable<IEditorPlugin.SupportEnvironment> SupportedEnvironments { get; } =
    [
        IEditorPlugin.SupportEnvironment.Windows_x64,
        IEditorPlugin.SupportEnvironment.Windows_arm64,
    ];

    private readonly ConcurrentDictionary<string, VideoSession> _videoSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AudioSession> _audioSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Initialize()
    {
        MediaFoundationLifecycle.EnsureStarted();
    }

    public async Task<ImageFileAccessorResult> GetImageAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            using SKBitmap? bitmap = await Task.Run(() => SKBitmap.Decode(path)).ConfigureAwait(false);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            return new ImageFileAccessorResult
            {
                IsSuccessful = true,
                Image = SKImage.FromBitmap(bitmap),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: image load failed. path={path}, error={ex}");
            return new ImageFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, TimeSpan time)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new VideoFileAccessorResult { IsSuccessful = false };
            }

            VideoSession session = GetOrCreateVideoSession(path);
            SKImage? image = await session.GetFrameAsync(time).ConfigureAwait(false);
            return new VideoFileAccessorResult
            {
                IsSuccessful = image is not null,
                Image = image,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: video frame load failed. path={path}, time={time}, error={ex}");
            return new VideoFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, int frame)
    {
        if (frame < 0)
        {
            return new VideoFileAccessorResult { IsSuccessful = false };
        }

        try
        {
            if (!File.Exists(path))
            {
                return new VideoFileAccessorResult { IsSuccessful = false };
            }

            VideoSession session = GetOrCreateVideoSession(path);
            SKImage? image = await session.GetFrameAsync(frame).ConfigureAwait(false);
            return new VideoFileAccessorResult
            {
                IsSuccessful = image is not null,
                Image = image,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: video frame load by index failed. path={path}, frame={frame}, error={ex}");
            return new VideoFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<AudioFileAccessorResult> GetAudioAsync(string path, TimeSpan? startTime = null, TimeSpan? duration = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
            }

            AudioSession session = GetOrCreateAudioSession(path);
            AudioChunk? chunk = await session.GetAudioAsync(startTime, duration).ConfigureAwait(false);
            return new AudioFileAccessorResult
            {
                IsSuccessful = chunk is not null,
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: audio load failed. path={path}, error={ex}");
            return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
        }
    }
    
    public async Task<AudioSampleResult> GetAudioBySampleAsync(string path, long startSample, long sampleCount, int sampleRate)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AudioSampleResult { IsSuccessful = false, Chunk = null };
            }

            AudioSession session = GetOrCreateAudioSession(path);
            AudioChunk? chunk = await session.GetAudioBySampleAsync(startSample, sampleCount, sampleRate).ConfigureAwait(false);
            return new AudioSampleResult
            {
                IsSuccessful = chunk is not null,
                Chunk = chunk,
                ActualStartSample = startSample,
                ActualSampleCount = (int)(chunk?.Length ?? 0),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationPlugin: audio load by sample failed. path={path}, error={ex}");
            return new AudioSampleResult { IsSuccessful = false, Chunk = null };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeVideoSessions();
        DisposeAudioSessions();
        MediaFoundationLifecycle.Release();
        GC.SuppressFinalize(this);
    }

    private VideoSession GetOrCreateVideoSession(string path)
    {
        return _videoSessions.GetOrAdd(path, static p => new VideoSession(p));
    }

    private AudioSession GetOrCreateAudioSession(string path)
    {
        return _audioSessions.GetOrAdd(path, static p => new AudioSession(p));
    }

    private void DisposeVideoSessions()
    {
        foreach (VideoSession session in _videoSessions.Values)
        {
            session.Dispose();
        }

        _videoSessions.Clear();
    }

    private void DisposeAudioSessions()
    {
        foreach (AudioSession session in _audioSessions.Values)
        {
            session.Dispose();
        }

        _audioSessions.Clear();
    }

    public IMediaOutputSession CreateSession()
    {
        return new MediaFoundationOutputSession();
    }
}
