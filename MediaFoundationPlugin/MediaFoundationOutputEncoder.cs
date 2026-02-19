using System.Diagnostics;
using System.Runtime.InteropServices;
using Metasia.Core.Encode;
using Metasia.Core.Media;
using Metasia.Core.Objects;
using Metasia.Core.Project;
using Metasia.Core.Sounds;
using MediaFoundationPlugin.Encoding;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

public sealed class MediaFoundationOutputEncoder : EncoderBase
{
    public string Name { get; } = "MediaFoundation MP4";
    public string[] SupportedExtensions { get; } = ["*.mp4"];
    public override double ProgressRate { get; protected set; }

    public override event EventHandler<EventArgs> StatusChanged = delegate { };
    public override event EventHandler<EventArgs> EncodeStarted = delegate { };
    public override event EventHandler<EventArgs> EncodeCompleted = delegate { };
    public override event EventHandler<EventArgs> EncodeFailed = delegate { };

    private readonly object _syncLock = new();
    private CancellationTokenSource _cts = new();
    private Task? _encodingTask;
    private int _width;
    private int _height;
    private double _framerate;
    private readonly MediaTypeFactory _mediaTypeFactory;

    public MediaFoundationOutputEncoder() : this(new MediaTypeFactory())
    {
    }

    public MediaFoundationOutputEncoder(MediaTypeFactory mediaTypeFactory)
    {
        _mediaTypeFactory = mediaTypeFactory;
    }

    public override void Initialize(
        MetasiaProject project,
        TimelineObject timeline,
        IImageFileAccessor imageFileAccessor,
        IVideoFileAccessor videoFileAccessor,
        IAudioFileAccessor audioFileAccessor,
        string projectPath,
        string outputPath)
    {
        base.Initialize(project, timeline, imageFileAccessor, videoFileAccessor, audioFileAccessor, projectPath, outputPath);

        _width = (int)project.Info.Size.Width;
        _height = (int)project.Info.Size.Height;
        _framerate = (double)project.Info.Framerate;

        if (_framerate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(project), "フレームレートは0より大きい必要があります。");
        }
    }

    public override void Start()
    {
        if (Status != IEncoder.EncoderState.Waiting)
        {
            throw new InvalidOperationException("エンコーダーが待機状態ではありません。");
        }

        if (string.IsNullOrWhiteSpace(_outputPath))
        {
            throw new InvalidOperationException("出力先パスが指定されていません。");
        }

        _cts.Dispose();
        _cts = new CancellationTokenSource();

        Status = IEncoder.EncoderState.Encoding;
        ProgressRate = 0;
        RaiseStatusChanged();
        EncodeStarted.Invoke(this, EventArgs.Empty);

        _encodingTask = Task.Run(() => EncodeAsync(_cts.Token));
        _encodingTask.ContinueWith(t =>
        {
            if (t.Exception is not null)
            {
                Debug.WriteLine($"MediaFoundationエンコードタスク失敗: {t.Exception}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public override void CancelRequest()
    {
        _cts.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task EncodeAsync(CancellationToken cancellationToken)
    {
        IMFSinkWriter? sinkWriter = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            sinkWriter = CreateSinkWriter(_outputPath!);

            int videoStreamIndex;
            int audioStreamIndex;
            ConfigureStreams(sinkWriter, _width, _height, _framerate, out videoStreamIndex, out audioStreamIndex);

            sinkWriter.BeginWriting();

            await WriteVideoFramesAsync(sinkWriter, videoStreamIndex, _framerate, cancellationToken).ConfigureAwait(false);
            await WriteAudioSamplesAsync(sinkWriter, audioStreamIndex, cancellationToken).ConfigureAwait(false);

            sinkWriter.Finalize();
            sinkWriter.Dispose();

            ProgressRate = 1.0;
            Status = IEncoder.EncoderState.Completed;
            RaiseStatusChanged();
            EncodeCompleted.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = IEncoder.EncoderState.Canceled;
            RaiseStatusChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundation MP4出力失敗: {ex}");
            Status = IEncoder.EncoderState.Failed;
            RaiseStatusChanged();
            EncodeFailed.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            sinkWriter?.Dispose();
        }
    }

    private IMFSinkWriter CreateSinkWriter(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);

        return MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
    }

    private void ConfigureStreams(IMFSinkWriter sinkWriter, int width, int height, double framerate, out int videoStreamIndex, out int audioStreamIndex)
    {
        using IMFMediaType videoInputType = _mediaTypeFactory.CreateVideoInputMediaType(width, height, framerate);
        using IMFMediaType videoOutputType = _mediaTypeFactory.CreateVideoOutputMediaType(width, height, framerate);
        using IMFMediaType audioInputType = _mediaTypeFactory.CreateAudioInputMediaType();
        using IMFMediaType audioOutputType = _mediaTypeFactory.CreateAudioOutputMediaType();

        videoStreamIndex = sinkWriter.AddStream(videoOutputType);
        audioStreamIndex = sinkWriter.AddStream(audioOutputType);

        sinkWriter.SetInputMediaType(videoStreamIndex, videoInputType, null);
        sinkWriter.SetInputMediaType(audioStreamIndex, audioInputType, null);
    }

    private async Task WriteVideoFramesAsync(IMFSinkWriter sinkWriter, int streamIndex, double framerate, CancellationToken cancellationToken)
    {
        long frameDuration100ns = (long)(EncodingConstants.HundredNanosecondsPerSecond / framerate);
        long currentTimestamp = 0;

        int frameIndex = 0;
        await foreach (var frame in GetFramesAsync(0, FrameCount - 1, cancellationToken).ConfigureAwait(false))
        {
            using (frame)
            {
                IMFSample? sample = CreateVideoSampleFromSkImage(frame, frameDuration100ns, currentTimestamp);
                if (sample is not null)
                {
                    using (sample)
                    {
                        sinkWriter.WriteSample(streamIndex, sample);
                    }
                }
            }

            currentTimestamp += frameDuration100ns;
            frameIndex++;
            SetProgress(0.5 * frameIndex / FrameCount);
        }
    }

    private unsafe IMFSample? CreateVideoSampleFromSkImage(SKImage image, long duration100ns, long timestamp100ns)
    {
        using var bitmap = SKBitmap.FromImage(image);
        if (bitmap is null)
        {
            return null;
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        int totalSize = Nv12Converter.CalculateNv12BufferSize(width, height);

        IntPtr nv12Buffer = Marshal.AllocHGlobal(totalSize);
        try
        {
            Nv12Converter.ConvertBgraToNv12InPlace(bitmap, nv12Buffer);

            return MediaSampleBuilder.CreateSampleFromBuffer(nv12Buffer, totalSize, timestamp100ns, duration100ns);
        }
        finally
        {
            Marshal.FreeHGlobal(nv12Buffer);
        }
    }

    private async Task WriteAudioSamplesAsync(IMFSinkWriter sinkWriter, int streamIndex, CancellationToken cancellationToken)
    {
        int sampleRate = _mediaTypeFactory.AudioSampleRate;
        int channelCount = _mediaTypeFactory.AudioChannelCount;
        int bitsPerSample = _mediaTypeFactory.AudioBitsPerSample;

        long totalSamples = (long)Math.Ceiling((FrameCount / _framerate) * sampleRate);
        long samplesWritten = 0;
        long currentSamplePosition = 0;

        while (currentSamplePosition < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long chunkSampleCount = Math.Min(1024 * 10, totalSamples - currentSamplePosition);

            var chunk = await GetAudioChunkAsync(
                currentSamplePosition,
                chunkSampleCount,
                sampleRate,
                channelCount,
                cancellationToken).ConfigureAwait(false);

            if (chunk.Length <= 0)
            {
                break;
            }

            IMFSample? audioSample = CreateAudioSampleFromChunk(chunk, currentSamplePosition, sampleRate, channelCount, bitsPerSample);
            if (audioSample is not null)
            {
                using (audioSample)
                {
                    sinkWriter.WriteSample(streamIndex, audioSample);
                }
            }

            currentSamplePosition += chunk.Length;
            samplesWritten += chunk.Length;

            double progress = 0.5 + 0.5 * samplesWritten / totalSamples;
            SetProgress(progress);
        }
    }

    private IMFSample? CreateAudioSampleFromChunk(IAudioChunk chunk, long samplePosition, int sampleRate, int channelCount, int bitsPerSample)
    {
        int sampleCount = (int)Math.Min(chunk.Length, int.MaxValue);
        if (sampleCount <= 0)
        {
            return null;
        }

        int bufferSize = AudioToPcmConverter.CalculateBufferSize(sampleCount, channelCount, bitsPerSample);
        byte[] pcmData = AudioToPcmConverter.ConvertToPcm(chunk, sampleCount, channelCount, bitsPerSample);

        long timestamp100ns = (long)(samplePosition * EncodingConstants.HundredNanosecondsPerSecond / sampleRate);
        long duration100ns = (long)(sampleCount * EncodingConstants.HundredNanosecondsPerSecond / sampleRate);

        return MediaSampleBuilder.CreateSampleFromManagedBuffer(pcmData, bufferSize, timestamp100ns, duration100ns);
    }

    private void SetProgress(double value)
    {
        ProgressRate = Math.Clamp(value, 0, 1);
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        StatusChanged.Invoke(this, EventArgs.Empty);
    }
}