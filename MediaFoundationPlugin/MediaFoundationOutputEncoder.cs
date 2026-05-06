using System.Diagnostics;
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
    private const int AudioChunkSampleCount = 1024 * 10;

    private readonly record struct SinkWriterConfiguration(bool EnableHardwareTransforms, bool AddAudioStreamFirst);
    private readonly record struct VideoSampleTiming(double PixelMs, double FallbackMs, double ConvertMs, double SampleMs);
    private sealed class AudioWriteState
    {
        public long CurrentSamplePosition { get; set; }
        public int ChunkIndex { get; set; }
    }

    public string Name { get; } = MediaFoundationOutputFormatInfo.DisplayName;
    public string[] SupportedExtensions { get; } = MediaFoundationOutputFormatInfo.SupportedExtensions;
    public override double ProgressRate { get; protected set; }

    public override event EventHandler<EventArgs> StatusChanged = delegate { };
    public override event EventHandler<EventArgs> EncodeStarted = delegate { };
    public override event EventHandler<EventArgs> EncodeCompleted = delegate { };
    public override event EventHandler<EventArgs> EncodeFailed = delegate { };

    private readonly object _syncLock = new();
    private CancellationTokenSource _cts = new();
    private Task? _encodingTask;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _outputWidth;
    private int _outputHeight;
    private double _framerate;
    private readonly MediaTypeFactory _mediaTypeFactory;
    private readonly MediaFoundationOutputSettings _settings;
    private AudioEncodingConfiguration? _activeAudioConfiguration;
    private string? _workingOutputPath;
    private SKBitmap? _scratchBitmap;

    public MediaFoundationOutputEncoder() : this(new MediaTypeFactory(), MediaFoundationOutputSettings.Default)
    {
    }

    public MediaFoundationOutputEncoder(MediaTypeFactory mediaTypeFactory, MediaFoundationOutputSettings? settings = null)
    {
        _mediaTypeFactory = mediaTypeFactory;
        _settings = settings ?? MediaFoundationOutputSettings.Default;
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

        _sourceWidth = (int)project.Info.Size.Width;
        _sourceHeight = (int)project.Info.Size.Height;
        _outputWidth = _settings.OutputWidth ?? _sourceWidth;
        _outputHeight = _settings.OutputHeight ?? _sourceHeight;
        _framerate = (double)project.Info.Framerate;

        if (_framerate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(project), "フレームレートは0より大きい必要があります。");
        }

        _scratchBitmap?.Dispose();
        _scratchBitmap = null;
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
            _scratchBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task EncodeAsync(CancellationToken cancellationToken)
    {
        var encodeSw = Stopwatch.StartNew();
        IMFSinkWriter? sinkWriter = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine($"[MediaFoundation] [Perf] [Encode-Start] frames={FrameCount} source={_sourceWidth}x{_sourceHeight} output={_outputWidth}x{_outputHeight} framerate={_framerate}");

            int videoStreamIndex;
            int audioStreamIndex;
            var setupSw = Stopwatch.StartNew();
            sinkWriter = CreateConfiguredSinkWriter(_outputPath!, _outputWidth, _outputHeight, _framerate, out videoStreamIndex, out audioStreamIndex);
            setupSw.Stop();
            Debug.WriteLine($"[MediaFoundation] [Perf] [SinkWriter-Setup] time={setupSw.ElapsedMilliseconds}ms");

            sinkWriter.BeginWriting();

            var mediaSw = Stopwatch.StartNew();
            await WriteInterleavedSamplesAsync(sinkWriter, videoStreamIndex, audioStreamIndex, _framerate, cancellationToken).ConfigureAwait(false);
            mediaSw.Stop();
            Debug.WriteLine($"[MediaFoundation] [Perf] [Media-Write] time={mediaSw.ElapsedMilliseconds}ms");

            var finalizeSw = Stopwatch.StartNew();
            sinkWriter.Finalize();
            sinkWriter.Dispose();
            finalizeSw.Stop();
            Debug.WriteLine($"[MediaFoundation] [Perf] [SinkWriter-Finalize] time={finalizeSw.ElapsedMilliseconds}ms");

            SetProgress(0.92);
            CommitWorkingOutput();
            SetProgress(1.0);

            var totalMs = encodeSw.ElapsedMilliseconds;
            Debug.WriteLine($"[MediaFoundation] [Perf] [Encode-Completed] totalTime={totalMs}ms setupTime={setupSw.ElapsedMilliseconds}ms mediaTime={mediaSw.ElapsedMilliseconds}ms finalizeTime={finalizeSw.ElapsedMilliseconds}ms");
            ProgressRate = 1.0;
            Status = IEncoder.EncoderState.Completed;
            RaiseStatusChanged();
            EncodeCompleted.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            CleanupWorkingOutput();
            Status = IEncoder.EncoderState.Canceled;
            RaiseStatusChanged();
            Debug.WriteLine($"[MediaFoundation] [Perf] [Encode-Canceled] elapsedTime={encodeSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaFoundation] [Perf] [Encode-Failed] elapsedTime={encodeSw.ElapsedMilliseconds}ms error={ex.Message}");
            Debug.WriteLine($"MediaFoundation出力失敗: {ex}");
            CleanupWorkingOutput();
            Status = IEncoder.EncoderState.Failed;
            RaiseStatusChanged();
            EncodeFailed.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            sinkWriter?.Dispose();
        }
    }

    private IMFSinkWriter CreateSinkWriter(string outputPath, bool enableHardwareTransforms)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, GetContainerTypeFromPath(outputPath));

        if (enableHardwareTransforms)
        {
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
        }

        return MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
    }

    private IMFSinkWriter CreateConfiguredSinkWriter(string outputPath, int width, int height, double framerate, out int videoStreamIndex, out int audioStreamIndex)
    {
        Exception? lastException = null;
        Debug.WriteLine($"MediaFoundation sink-writer setup started: width={width}, height={height}, framerate={framerate}");

        foreach (var sinkWriterConfiguration in GetSinkWriterConfigurations())
        {
            foreach (var videoConfiguration in _mediaTypeFactory.GetVideoEncodingCandidates(width, height, framerate))
            {
                foreach (var audioConfiguration in _mediaTypeFactory.GetAudioEncodingCandidates())
                {
                    IReadOnlyList<AudioOutputTypeCandidate> audioOutputCandidates = _mediaTypeFactory.GetAudioOutputTypeCandidates(audioConfiguration);
                    try
                    {
                        foreach (AudioOutputTypeCandidate audioOutputCandidate in audioOutputCandidates)
                        {
                            string candidateOutputPath = CreateCandidateOutputPath(outputPath);
                            IMFSinkWriter? sinkWriter = null;
                            try
                            {
                                sinkWriter = CreateSinkWriter(candidateOutputPath, sinkWriterConfiguration.EnableHardwareTransforms);
                                ConfigureStreams(sinkWriter, width, height, framerate, audioConfiguration, audioOutputCandidate, videoConfiguration, sinkWriterConfiguration, out videoStreamIndex, out audioStreamIndex);
                                _activeAudioConfiguration = audioConfiguration;
                                _workingOutputPath = candidateOutputPath;
                                Debug.WriteLine($"MediaFoundation configuration selected: hw={sinkWriterConfiguration.EnableHardwareTransforms}, audioFirst={sinkWriterConfiguration.AddAudioStreamFirst}, videoProfile={videoConfiguration.ProfileLabel}, videoLevel={videoConfiguration.LevelLabel}, requestedAudioRate={audioConfiguration.SampleRate}, requestedAudioChannels={audioConfiguration.ChannelCount}, requestedAudioBits={audioConfiguration.BitsPerSample}, requestedAudioBitrate={audioConfiguration.Bitrate}, actualAudio={audioOutputCandidate.Label}");
                                return sinkWriter;
                            }
                            catch (Exception ex)
                            {
                                sinkWriter?.Dispose();
                                TryDeleteFile(candidateOutputPath);
                                Debug.WriteLine($"MediaFoundation configuration rejected: hw={sinkWriterConfiguration.EnableHardwareTransforms}, audioFirst={sinkWriterConfiguration.AddAudioStreamFirst}, videoProfile={videoConfiguration.ProfileLabel}, videoLevel={videoConfiguration.LevelLabel}, requestedAudioRate={audioConfiguration.SampleRate}, requestedAudioChannels={audioConfiguration.ChannelCount}, requestedAudioBits={audioConfiguration.BitsPerSample}, requestedAudioBitrate={audioConfiguration.Bitrate}, actualAudio={audioOutputCandidate.Label}, error={ex.Message}");
                                lastException = ex;
                            }
                        }
                    }
                    finally
                    {
                        foreach (AudioOutputTypeCandidate audioOutputCandidate in audioOutputCandidates)
                        {
                            audioOutputCandidate.MediaType.Dispose();
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException($"有効なMedia Foundationエンコード設定を確立できませんでした。width={width}, height={height}, framerate={framerate}", lastException);
    }

    private void ConfigureStreams(IMFSinkWriter sinkWriter, int width, int height, double framerate, AudioEncodingConfiguration audioConfiguration, AudioOutputTypeCandidate audioOutputCandidate, VideoEncodingConfiguration videoConfiguration, SinkWriterConfiguration sinkWriterConfiguration, out int videoStreamIndex, out int audioStreamIndex)
    {
        using IMFMediaType videoInputType = _mediaTypeFactory.CreateVideoInputMediaType(width, height, framerate);
        using IMFMediaType videoOutputType = _mediaTypeFactory.CreateVideoOutputMediaType(width, height, framerate, videoConfiguration);
        using IMFMediaType audioInputType = _mediaTypeFactory.CreateAudioInputMediaType(audioConfiguration);
        IMFMediaType audioOutputType = audioOutputCandidate.MediaType;

        if (sinkWriterConfiguration.AddAudioStreamFirst)
        {
            audioStreamIndex = sinkWriter.AddStream(audioOutputType);
            videoStreamIndex = sinkWriter.AddStream(videoOutputType);

            TrySetInputMediaType(sinkWriter, audioStreamIndex, audioInputType, "audio-input", audioConfiguration, width, height, framerate);
            TrySetInputMediaType(sinkWriter, videoStreamIndex, videoInputType, "video-input", audioConfiguration, width, height, framerate);
            return;
        }

        videoStreamIndex = sinkWriter.AddStream(videoOutputType);
        audioStreamIndex = sinkWriter.AddStream(audioOutputType);

        TrySetInputMediaType(sinkWriter, videoStreamIndex, videoInputType, "video-input", audioConfiguration, width, height, framerate);
        TrySetInputMediaType(sinkWriter, audioStreamIndex, audioInputType, "audio-input", audioConfiguration, width, height, framerate);
    }

    private async Task WriteInterleavedSamplesAsync(IMFSinkWriter sinkWriter, int videoStreamIndex, int audioStreamIndex, double framerate, CancellationToken cancellationToken)
    {
        AudioEncodingConfiguration audioConfiguration = _activeAudioConfiguration
            ?? new AudioEncodingConfiguration(_mediaTypeFactory.AudioSampleRate, _mediaTypeFactory.AudioChannelCount, _mediaTypeFactory.AudioBitsPerSample, MediaFoundationOutputSettings.Default.AudioBitrate);
        int sampleRate = audioConfiguration.SampleRate;
        int channelCount = audioConfiguration.ChannelCount;
        int bitsPerSample = audioConfiguration.BitsPerSample;
        long totalAudioSamples = (long)Math.Ceiling((FrameCount / framerate) * sampleRate);
        var audioState = new AudioWriteState();

        long frameDuration100ns = (long)(EncodingConstants.HundredNanosecondsPerSecond / framerate);
        long currentTimestamp = 0;
        var frameSw = Stopwatch.StartNew();

        int frameIndex = 0;
        await using var frameEnumerator = GetFramesAsync(0, FrameCount - 1, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var getFrameSw = Stopwatch.StartNew();
            if (!await frameEnumerator.MoveNextAsync().ConfigureAwait(false))
            {
                break;
            }

            getFrameSw.Stop();
            SKImage frame = frameEnumerator.Current;
            double writeMs = 0;
            VideoSampleTiming sampleTiming = default;

            using (frame)
            {
                IMFSample? sample = CreateVideoSampleFromSkImage(frame, _outputWidth, _outputHeight, frameDuration100ns, currentTimestamp, out sampleTiming);

                if (sample is not null)
                {
                    using (sample)
                    {
                        var writeSw = Stopwatch.StartNew();
                        sinkWriter.WriteSample(videoStreamIndex, sample);
                        writeSw.Stop();
                        writeMs = writeSw.Elapsed.TotalMilliseconds;
                    }
                }

                if (frameIndex % 30 == 0 || frameIndex == FrameCount - 1)
                {
                    var elapsedMs = frameSw.ElapsedMilliseconds;
                    Debug.WriteLine($"[MediaFoundation] [Perf] [Frame-Render] frame={frameIndex + 1}/{FrameCount} elapsedMs={elapsedMs}ms avgMs={elapsedMs / (double)(frameIndex + 1):F2}ms fps={(frameIndex + 1) / (elapsedMs / 1000.0):F1} getFrameMs={getFrameSw.Elapsed.TotalMilliseconds:F2}ms sampleMs={sampleTiming.SampleMs:F2}ms pixelMs={sampleTiming.PixelMs:F2}ms fallbackMs={sampleTiming.FallbackMs:F2}ms nv12ConvertMs={sampleTiming.ConvertMs:F2}ms writeMs={writeMs:F2}ms");
                }
            }

            long videoEndSample = (long)Math.Ceiling((frameIndex + 1) * sampleRate / framerate);
            if (audioState.CurrentSamplePosition < videoEndSample)
            {
                long audioTargetSample = Math.Min(totalAudioSamples, videoEndSample + AudioChunkSampleCount);
                await WriteAudioSamplesUntilAsync(
                    sinkWriter,
                    audioStreamIndex,
                    audioTargetSample,
                    totalAudioSamples,
                    sampleRate,
                    channelCount,
                    bitsPerSample,
                    cancellationToken,
                    audioState).ConfigureAwait(false);
            }

            currentTimestamp += frameDuration100ns;
            frameIndex++;
            SetProgress(0.8 * frameIndex / FrameCount);
        }

        await WriteAudioSamplesUntilAsync(
            sinkWriter,
            audioStreamIndex,
            totalAudioSamples,
            totalAudioSamples,
            sampleRate,
            channelCount,
            bitsPerSample,
            cancellationToken,
            audioState).ConfigureAwait(false);
    }

    private IMFSample? CreateVideoSampleFromSkImage(SKImage image, int outputWidth, int outputHeight, long duration100ns, long timestamp100ns, out VideoSampleTiming timing)
    {
        int totalSize = Nv12Converter.CalculateNv12BufferSize(outputWidth, outputHeight);

        var pixelSw = Stopwatch.StartNew();
        using SKPixmap pixmap = image.PeekPixels();
        pixelSw.Stop();
        if (pixmap is not null &&
            pixmap.Width == outputWidth &&
            pixmap.Height == outputHeight &&
            IsSupportedDirectPixelFormat(pixmap.ColorType))
        {
            double convertMs = 0;
            var sampleSw = Stopwatch.StartNew();
            IMFSample sample = MediaSampleBuilder.CreateSampleWithWritableBuffer(
                totalSize,
                timestamp100ns,
                duration100ns,
                (destination, _) =>
                {
                    var convertSw = Stopwatch.StartNew();
                    Nv12Converter.ConvertToNv12InPlace(
                        pixmap.GetPixels(),
                        outputWidth,
                        outputHeight,
                        pixmap.RowBytes,
                        pixmap.ColorType,
                        destination);
                    convertSw.Stop();
                    convertMs = convertSw.Elapsed.TotalMilliseconds;
                });
            sampleSw.Stop();
            timing = new VideoSampleTiming(pixelSw.Elapsed.TotalMilliseconds, 0, convertMs, sampleSw.Elapsed.TotalMilliseconds);
            return sample;
        }

        var fallbackSw = Stopwatch.StartNew();
        SKBitmap bitmap = CreateOutputBitmap(image, outputWidth, outputHeight);
        fallbackSw.Stop();
        double fallbackConvertMs = 0;
        var fallbackSampleSw = Stopwatch.StartNew();
        IMFSample fallbackSample = MediaSampleBuilder.CreateSampleWithWritableBuffer(
            totalSize,
            timestamp100ns,
            duration100ns,
            (destination, _) =>
            {
                var convertSw = Stopwatch.StartNew();
                Nv12Converter.ConvertToNv12InPlace(bitmap, destination);
                convertSw.Stop();
                fallbackConvertMs = convertSw.Elapsed.TotalMilliseconds;
            });
        fallbackSampleSw.Stop();
        timing = new VideoSampleTiming(pixelSw.Elapsed.TotalMilliseconds, fallbackSw.Elapsed.TotalMilliseconds, fallbackConvertMs, fallbackSampleSw.Elapsed.TotalMilliseconds);
        return fallbackSample;
    }

    private SKBitmap CreateOutputBitmap(SKImage image, int outputWidth, int outputHeight)
    {
        SKBitmap bitmap = GetOrCreateScratchBitmap(outputWidth, outputHeight);
        if (image.Width == outputWidth && image.Height == outputHeight)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap is not null && image.ReadPixels(pixmap))
            {
                return bitmap;
            }
        }

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        canvas.DrawImage(image, new SKRect(0, 0, outputWidth, outputHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Flush();
        return bitmap;
    }

    private SKBitmap GetOrCreateScratchBitmap(int width, int height)
    {
        if (_scratchBitmap is not null && _scratchBitmap.Width == width && _scratchBitmap.Height == height)
        {
            return _scratchBitmap;
        }

        _scratchBitmap?.Dispose();
        _scratchBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        return _scratchBitmap;
    }

    private static bool IsSupportedDirectPixelFormat(SKColorType colorType)
    {
        return colorType is SKColorType.Bgra8888 or SKColorType.Rgba8888;
    }

    private async Task WriteAudioSamplesUntilAsync(
        IMFSinkWriter sinkWriter,
        int streamIndex,
        long targetSamplePosition,
        long totalSamples,
        int sampleRate,
        int channelCount,
        int bitsPerSample,
        CancellationToken cancellationToken,
        AudioWriteState state)
    {
        targetSamplePosition = Math.Min(targetSamplePosition, totalSamples);
        while (state.CurrentSamplePosition < targetSamplePosition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSw = Stopwatch.StartNew();

            long chunkSampleCount = Math.Min(AudioChunkSampleCount, targetSamplePosition - state.CurrentSamplePosition);

            var chunk = await GetAudioChunkAsync(
                state.CurrentSamplePosition,
                chunkSampleCount,
                sampleRate,
                channelCount,
                cancellationToken).ConfigureAwait(false);

            var getAudioMs = chunkSw.ElapsedMilliseconds;

            if (chunk.Length <= 0)
            {
                break;
            }

            IMFSample? audioSample = CreateAudioSampleFromChunk(chunk, state.CurrentSamplePosition, sampleRate, channelCount, bitsPerSample);
            if (audioSample is not null)
            {
                using (audioSample)
                {
                    sinkWriter.WriteSample(streamIndex, audioSample);
                }
            }

            state.CurrentSamplePosition += chunk.Length;
            state.ChunkIndex++;

            if (state.ChunkIndex % 5 == 0 || state.CurrentSamplePosition >= totalSamples)
            {
                Debug.WriteLine($"[MediaFoundation] [Perf] [Audio-Chunk] chunk={state.ChunkIndex} samples={state.CurrentSamplePosition}/{totalSamples} getAudioMs={getAudioMs}ms chunkTotalMs={chunkSw.ElapsedMilliseconds}ms");
            }
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

    private static IReadOnlyList<SinkWriterConfiguration> GetSinkWriterConfigurations()
    {
        return
        [
            new SinkWriterConfiguration(true, false),
            new SinkWriterConfiguration(false, false),
            new SinkWriterConfiguration(true, true),
            new SinkWriterConfiguration(false, true)
        ];
    }

    private static void TrySetInputMediaType(
        IMFSinkWriter sinkWriter,
        int streamIndex,
        IMFMediaType mediaType,
        string label,
        AudioEncodingConfiguration audioConfiguration,
        int width,
        int height,
        double framerate)
    {
        try
        {
            sinkWriter.SetInputMediaType(streamIndex, mediaType, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"MediaFoundation SetInputMediaType failed: label={label}, streamIndex={streamIndex}, width={width}, height={height}, framerate={framerate}, audioSampleRate={audioConfiguration.SampleRate}, audioChannels={audioConfiguration.ChannelCount}, audioBits={audioConfiguration.BitsPerSample}, audioBitrate={audioConfiguration.Bitrate}, mediaType={DescribeMediaType(mediaType)}, error={ex}");
            throw;
        }
    }

    private static string DescribeMediaType(IMFMediaType mediaType)
    {
        static uint ReadUInt32(IMFMediaType type, Guid key)
        {
            return MediaFactory.MFGetAttributeUInt32(type, key, 0);
        }

        Guid majorType = mediaType.GetGUID(MediaTypeAttributeKeys.MajorType);
        Guid subtype = mediaType.GetGUID(MediaTypeAttributeKeys.Subtype);
        uint sampleRate = ReadUInt32(mediaType, MediaTypeAttributeKeys.AudioSamplesPerSecond);
        uint channels = ReadUInt32(mediaType, MediaTypeAttributeKeys.AudioNumChannels);
        uint bitsPerSample = ReadUInt32(mediaType, MediaTypeAttributeKeys.AudioBitsPerSample);
        uint avgBytesPerSecond = ReadUInt32(mediaType, MediaTypeAttributeKeys.AudioAvgBytesPerSecond);

        uint width = 0;
        uint height = 0;
        if (MediaFactory.MFGetAttributeSize(mediaType, MediaTypeAttributeKeys.FrameSize, out width, out height).Success)
        {
            return $"major={majorType}, subtype={subtype}, frameSize={width}x{height}, audioRate={sampleRate}, audioChannels={channels}, audioBits={bitsPerSample}, audioAvgBytes={avgBytesPerSecond}";
        }

        return $"major={majorType}, subtype={subtype}, audioRate={sampleRate}, audioChannels={channels}, audioBits={bitsPerSample}, audioAvgBytes={avgBytesPerSecond}";
    }

    private string CreateCandidateOutputPath(string finalOutputPath)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Metasia.MediaFoundation");
        Directory.CreateDirectory(directory);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(finalOutputPath);
        string extension = Path.GetExtension(finalOutputPath);
        string candidate = Path.Combine(directory, $"{fileNameWithoutExtension}.mf-{Guid.NewGuid():N}{extension}");
        TryDeleteFile(candidate);
        return candidate;
    }

    private void CommitWorkingOutput()
    {
        if (string.IsNullOrWhiteSpace(_workingOutputPath) || string.IsNullOrWhiteSpace(_outputPath))
        {
            return;
        }

        if (File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }

        File.Move(_workingOutputPath, _outputPath);
        _workingOutputPath = null;
    }

    private void CleanupWorkingOutput()
    {
        if (string.IsNullOrWhiteSpace(_workingOutputPath))
        {
            return;
        }

        TryDeleteFile(_workingOutputPath);
        _workingOutputPath = null;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundation temporary file cleanup failed: path={path}, error={ex.Message}");
        }
    }

    private static Guid GetContainerTypeFromPath(string outputPath)
    {
        string extension = Path.GetExtension(outputPath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".mov" or ".m4v" => TranscodeContainerTypeGuids.Mpeg4,
            _ => throw new NotSupportedException($"未対応の出力形式です: {extension}"),
        };
    }
}
