using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed class AudioPlayerOptions
{
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPositionUpdateIntervalMs);
    public int MaxRetryAttempts { get; init; } = AudioConstants.MaxRetryAttempts;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(RetryDelayMs);
    public bool UseNullBackend { get; init; }
}

public sealed class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _seekLock = new(1, 1);

    private IAudioSource? _source;
    private IAudioDecoder? _decoder;
    private IPlaybackBackend? _backend;

    private CircularBuffer<float>? _pcmBuffer;
    private float[]? _decodeBuffer;

    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile float _volume = 1.0f;
    private volatile bool _disposed;

    private int _seekVersion;
    private int _skipFramesCounter;

    private AudioStreamInfo _currentStreamInfo = AudioStreamInfo.Empty;
    private long _playedSamples;
    private string? _currentTrackId;
    private int _currentBitrateHint;

    private CancellationTokenSource? _playbackCts;
    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private Timer? _positionTimer;
    private Timer? _bufferTimer;

    #endregion

    #region Properties

    public AudioPlayerEvents Events => _events;
    public AudioStreamInfo StreamInfo => _currentStreamInfo;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            if (_backend != null)
                _backend.Volume = Math.Min(_volume, 1f);
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_decoder == null || _decoder.SampleRate <= 0 || _backend == null)
                return TimeSpan.Zero;

            long totalWritten = Volatile.Read(ref _playedSamples);
            int backendBuffered = _backend.BufferedSamples;
            long heardSamples = Math.Max(0, totalWritten - backendBuffered);

            double seconds = (double)heardSamples / (_decoder.SampleRate * _decoder.Channels);

            var duration = Duration;
            if (duration.TotalSeconds > 0 && seconds > duration.TotalSeconds)
                seconds = duration.TotalSeconds;

            return TimeSpan.FromSeconds(seconds);
        }
    }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(_currentStreamInfo.DurationMs);
    public PlaybackState State => _state;

    #endregion

    #region Legacy Events

    public event Action<TimeSpan>? PositionChanged
    {
        add => _events.PositionChanged += value;
        remove => _events.PositionChanged -= value;
    }

    public event Action<PlaybackState>? StateChanged
    {
        add => _events.StateChanged += value;
        remove => _events.StateChanged -= value;
    }

    public event Action? TrackEnded
    {
        add => _events.TrackEnded += value;
        remove => _events.TrackEnded -= value;
    }

    public event Action<Exception>? ErrorOccurred;

    #endregion

    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();
        _events.ErrorOccurred += err => ErrorOccurred?.Invoke(err.Exception ?? new Exception(err.Message));
    }

    #region Public Methods

    /// <summary>
    /// Начинает воспроизведение.
    /// </summary>
    /// <param name="url">URL потока.</param>
    /// <param name="trackId">ID трека.</param>
    /// <param name="bitrateHint">Подсказка битрейта (kbps). 0 = автоопределение.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task PlayAsync(string url, string? trackId = null, int bitrateHint = 0, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateLock.WaitAsync(ct);
        try
        {
            await StopInternalAsync();

            _currentTrackId = trackId;
            _currentBitrateHint = bitrateHint;
            Interlocked.Exchange(ref _skipFramesCounter, 0);
            _currentStreamInfo = AudioStreamInfo.Empty;

            SetState(PlaybackState.Loading);

            await InitializePipelineAsync(url, ct);

            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            StartDecoderLoop();

            SetState(PlaybackState.Buffering);
            await WaitForInitialBufferAsync(_playbackCts.Token);

            _backend!.Start();

            StartTimers();

            SetState(PlaybackState.Playing);
            Log.Info($"[AudioPlayer] Started track: {trackId ?? "unknown"}, bitrate hint={bitrateHint}kbps");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlaybackState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
            await StopInternalAsync();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void Pause()
    {
        if (_state != PlaybackState.Playing) return;
        _backend?.Stop();
        SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        if (_state != PlaybackState.Paused) return;
        _backend?.Start();
        SetState(PlaybackState.Playing);
    }

    public void Stop()
    {
        if (_state == PlaybackState.Stopped) return;
        _ = StopAsync();
    }

    public async Task StopAsync()
    {
        if (_state == PlaybackState.Stopped) return;

        await _stateLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _stateLock.Release();
        }
        Log.Info("[AudioPlayer] Stopped");
    }

    public async ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed) return;

        if (_source == null || !_source.CanSeek || _decoder == null)
            return;

        if (!await _seekLock.WaitAsync(0, ct))
            return;

        try
        {
            int currentSeekVersion = Interlocked.Increment(ref _seekVersion);
            bool wasPlaying = _state == PlaybackState.Playing;
            long posMs = (long)position.TotalMilliseconds;

            await StopDecoderLoopGracefullyAsync();

            if (_disposed || Interlocked.CompareExchange(ref _seekVersion, 0, 0) != currentSeekVersion)
                return;

            _backend?.Stop();
            await Task.Delay(20, ct);
            _backend?.Flush();
            _pcmBuffer?.Clear();

            int skipFrames = _source.Codec == AudioCodec.Aac
                ? SkipFramesAfterSeekAac
                : SkipFramesAfterSeekOpus;
            Interlocked.Exchange(ref _skipFramesCounter, skipFrames);

            bool success = await _source.SeekAsync(posMs, ct);

            if (_disposed || Interlocked.CompareExchange(ref _seekVersion, 0, 0) != currentSeekVersion)
                return;

            if (success)
            {
                long targetSamples = (long)(posMs / 1000.0 * _decoder.SampleRate * _decoder.Channels);
                Volatile.Write(ref _playedSamples, targetSamples);

                StartDecoderLoop();

                if (wasPlaying && !_disposed)
                {
                    try
                    {
                        await WaitForMinimalBufferAsync(ct);
                    }
                    catch { }

                    if (!_disposed)
                    {
                        _backend?.Start();
                        SetState(PlaybackState.Playing);
                    }
                }
                else if (!_disposed)
                {
                    SetState(PlaybackState.Paused);
                }

                _events.RaiseSeekCompleted(position);
                Log.Debug($"[AudioPlayer] Seeked to {posMs}ms");
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Seek error: {ex.Message}");
        }
        finally
        {
            try { _seekLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region Pipeline Initialization

    private async Task InitializePipelineAsync(string url, CancellationToken ct)
    {
        _source = await AudioSourceFactory.CreateAsync(
            url,
            SharedHttpClient.Instance,
            CreateUrlRefresher(),
            _currentTrackId,
            _currentBitrateHint,
            ct);

        if (!await _source.InitializeAsync(ct))
            throw new AudioSourceException("Failed to initialize audio source");

        _decoder = CreateDecoder(_source);

        _currentStreamInfo = BuildStreamInfo(_source, _currentTrackId);
        _events.RaiseStreamInfo(_currentStreamInfo);

        int bufferSize = _decoder.SampleRate * _decoder.Channels * BufferSizeSeconds;
        _pcmBuffer = new CircularBuffer<float>(bufferSize);
        _decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * _decoder.Channels);

        _backend = CreateBackend();
        _backend.Initialize(_decoder.SampleRate, _decoder.Channels, AudioCallback);
        _backend.Volume = Math.Min(_volume, 1f);

        Log.Debug($"[AudioPlayer] Initialized: {_currentStreamInfo.FormatDisplay}");
    }

    private AudioStreamInfo BuildStreamInfo(IAudioSource source, string? trackId)
    {
        CacheEntry? cacheEntry = null;
        string container = "";
        int bitrate = 0;

        if (!string.IsNullOrEmpty(trackId))
        {
            var cached = AudioSourceFactory.FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                cacheEntry = cached.Value.Entry;
                container = cacheEntry.Format.ToString();
                bitrate = cacheEntry.Bitrate;
            }
        }

        if (source is Sources.CachingStreamSource cachingSource)
        {
            bitrate = cachingSource.Bitrate;

            if (string.IsNullOrEmpty(container))
            {
                container = source.Codec switch
                {
                    AudioCodec.Opus => "WebM",
                    AudioCodec.Aac => "Mp4",
                    _ => "Unknown"
                };
            }
        }

        // bitrateHint имеет приоритет
        if (_currentBitrateHint > 0)
            bitrate = _currentBitrateHint;

        return new AudioStreamInfo
        {
            TrackId = trackId ?? "",
            Container = container,
            Codec = source.Codec.ToString(),
            Bitrate = bitrate > 0 ? bitrate : EstimateBitrate(source),
            SampleRate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate,
            Channels = source.Channels > 0 ? source.Channels : DefaultChannels,
            DurationMs = source.DurationMs,
            IsFromCache = cacheEntry?.IsComplete ?? false
        };
    }

    private static int EstimateBitrate(IAudioSource source) => source.Codec switch
    {
        AudioCodec.Opus => 128,
        AudioCodec.Aac => 96,
        _ => 128
    };

    private IAudioDecoder CreateDecoder(IAudioSource source)
    {
        int rate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate;
        int ch = source.Channels > 0 ? source.Channels : DefaultChannels;

        return source.Codec switch
        {
            AudioCodec.Opus => new OpusDecoder(rate, ch),
            AudioCodec.Aac => CreateAacDecoder(source, rate, ch),
            _ => throw new NotSupportedException($"Codec {source.Codec} not supported")
        };
    }

    private static AacDecoder CreateAacDecoder(IAudioSource source, int rate, int ch)
    {
        var dec = new AacDecoder(rate, ch);
        if (source.DecoderConfig != null)
            dec.Initialize(source.DecoderConfig);
        return dec;
    }

    private IPlaybackBackend CreateBackend()
    {
        if (_options.UseNullBackend)
            return new NullAudioBackend();

        try
        {
            return new NAudioBackend();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio init failed: {ex.Message}, using NullBackend");
            return new NullAudioBackend();
        }
    }

    #endregion

    #region Decoder Loop

    private void StartDecoderLoop()
    {
        _decoderCts?.Dispose();

        _decoderCts = _playbackCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(_playbackCts.Token)
            : new CancellationTokenSource();

        _decoderTask = Task.Run(
            () => DecoderLoopAsync(_decoderCts.Token),
            _decoderCts.Token);
    }

    private async Task StopDecoderLoopGracefullyAsync()
    {
        if (_decoderCts == null || _decoderTask == null)
            return;

        _decoderCts.Cancel();

        try
        {
            await _decoderTask.WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs));
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPlayer] Decoder loop graceful stop timeout");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Decoder loop stop error: {ex.Message}");
        }

        _decoderCts.Dispose();
        _decoderCts = null;
        _decoderTask = null;
    }

    private async Task DecoderLoopAsync(CancellationToken ct)
    {
        if (_source == null || _decoder == null || _pcmBuffer == null || _decodeBuffer == null)
            return;

        int retryCount = 0;
        bool useResetDecode = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int skipCount = Interlocked.CompareExchange(ref _skipFramesCounter, 0, 0);
                if (skipCount > 0)
                    useResetDecode = true;

                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    await Task.Delay(5, ct);
                    continue;
                }

                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct);
                    retryCount = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (UrlExpiredException)
                {
                    if (await TryRefreshUrlAsync(ct)) continue;
                    throw;
                }
                catch (Exception ex) when (retryCount++ < _options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPlayer] Read retry {retryCount}/{_options.MaxRetryAttempts}: {ex.Message}");
                    await Task.Delay(_options.RetryDelay, ct);
                    continue;
                }

                if (frame == null)
                {
                    await DrainBufferAsync(ct);
                    OnTrackEnded();
                    break;
                }

                ct.ThrowIfCancellationRequested();

                try
                {
                    int samplesDecoded;

                    if (useResetDecode)
                    {
                        samplesDecoded = _decoder.DecodeWithReset(
                            frame.Value.Data.Span, _decodeBuffer);
                        useResetDecode = false;
                    }
                    else
                    {
                        samplesDecoded = _decoder.Decode(
                            frame.Value.Data.Span, _decodeBuffer);
                    }

                    skipCount = Interlocked.CompareExchange(ref _skipFramesCounter, 0, 0);
                    if (skipCount > 0)
                    {
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
                    }

                    if (samplesDecoded > 0 && !ct.IsCancellationRequested)
                    {
                        _pcmBuffer.Write(
                            _decodeBuffer.AsSpan(0, samplesDecoded * _decoder.Channels));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioPlayer] Decode error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Decoder loop fatal: {ex.Message}", ex);
            SetState(PlaybackState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }

    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (_pcmBuffer is { IsEmpty: false } && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }
    }

    #endregion

    #region Audio Callback

    private int AudioCallback(Span<float> buffer)
    {
        if (_state != PlaybackState.Playing || _pcmBuffer == null)
        {
            buffer.Clear();
            return 0;
        }

        int read = _pcmBuffer.Read(buffer);

        if (read > 0)
        {
            Interlocked.Add(ref _playedSamples, read);

            if (_volume > 1.0f)
                ApplyVolumeSimd(buffer[..read], _volume);
        }

        if (read < buffer.Length)
            buffer[read..].Clear();

        return read / (_decoder?.Channels ?? 2);
    }

    private static void ApplyVolumeSimd(Span<float> data, float volume)
    {
        if (MathF.Abs(volume - 1.0f) < 0.001f) return;

        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            var vecVol = new Vector<float>(volume);
            var vecMin = new Vector<float>(-1.0f);
            var vecMax = new Vector<float>(1.0f);

            var vectors = MemoryMarshal.Cast<float, Vector<float>>(data);
            for (int j = 0; j < vectors.Length; j++)
                vectors[j] = Vector.Min(Vector.Max(vectors[j] * vecVol, vecMin), vecMax);

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < data.Length; i++)
            data[i] = Math.Clamp(data[i] * volume, -1f, 1f);
    }

    #endregion

    #region Timers

    private void StartTimers()
    {
        _positionTimer = new Timer(
            _ => _events.RaisePositionChanged(Position),
            null, 0,
            (int)_options.PositionUpdateInterval.TotalMilliseconds);

        _bufferTimer = new Timer(
            _ => RaiseBufferState(),
            null, 0, BufferStateUpdateIntervalMs);
    }

    private void RaiseBufferState()
    {
        if (_source == null) return;

        var state = new BufferState(
            _source.BufferProgress,
            _source.IsFullyBuffered,
            _source.GetBufferedRanges());

        _events.RaiseBufferState(state);
    }

    #endregion

    #region Buffering

    private async Task WaitForInitialBufferAsync(CancellationToken ct)
    {
        if (_decoder == null || _pcmBuffer == null) return;

        int threshold = _decoder.SampleRate * _decoder.Channels * MinBufferMs / 1000;

        while (_pcmBuffer.Count < threshold
               && !ct.IsCancellationRequested
               && _source?.IsFullyBuffered == false)
        {
            await Task.Delay(5, ct);
        }
    }

    private async Task WaitForMinimalBufferAsync(CancellationToken ct)
    {
        if (_decoder == null || _pcmBuffer == null) return;

        int minSamples = _decoder.SampleRate * _decoder.Channels * MinSeekResumeBufferMs / 1000;
        int waited = 0;

        while (_pcmBuffer.Count < minSamples && waited < 300 && !ct.IsCancellationRequested && !_disposed)
        {
            await Task.Delay(10, ct);
            waited += 10;
        }
    }

    #endregion

    #region URL Refresh

    private async Task<bool> TryRefreshUrlAsync(CancellationToken ct)
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return false;

        try
        {
            var newUrl = await _options.UrlRefreshCallback(_currentTrackId, ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                Log.Info("[AudioPlayer] URL refreshed");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] URL refresh failed: {ex.Message}");
        }

        return false;
    }

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher()
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return null;

        var trackId = _currentTrackId;
        var callback = _options.UrlRefreshCallback;
        return ct => callback(trackId, ct).AsTask();
    }

    #endregion

    #region Lifecycle

    private async Task StopInternalAsync()
    {
        _playbackCts?.Cancel();
        await StopDecoderLoopGracefullyAsync();

        _positionTimer?.Dispose();
        _positionTimer = null;

        _bufferTimer?.Dispose();
        _bufferTimer = null;

        _backend?.Dispose();
        _backend = null;

        _decoder?.Dispose();
        _decoder = null;

        if (_source != null)
        {
            await _source.DisposeAsync();
            _source = null;
        }

        if (_decodeBuffer != null)
        {
            ArrayPool<float>.Shared.Return(_decodeBuffer);
            _decodeBuffer = null;
        }

        _pcmBuffer = null;

        Volatile.Write(ref _playedSamples, 0);
        Interlocked.Exchange(ref _skipFramesCounter, 0);
        _currentTrackId = null;
        _currentBitrateHint = 0;
        _currentStreamInfo = AudioStreamInfo.Empty;

        _playbackCts?.Dispose();
        _playbackCts = null;

        SetState(PlaybackState.Stopped);
    }

    private void OnTrackEnded()
    {
        if (_state != PlaybackState.Stopped)
            _events.RaiseTrackEnded();
        SetState(PlaybackState.Stopped);
    }

    private void SetState(PlaybackState newState)
    {
        if (_state == newState) return;
        _state = newState;
        _events.RaiseStateChanged(newState);
    }

    #endregion

    #region Statistics

    public double BufferProgress => _source?.BufferProgress ?? 0;
    public bool IsFullyBuffered => _source?.IsFullyBuffered ?? false;

    public long GetDownloadedBytes()
    {
        return _source switch
        {
            Sources.CachingStreamSource caching => caching.DownloadedBytes,
            Sources.LocalFileSource => _source.IsFullyBuffered ? EstimateTotalBytes() : 0,
            _ => (long)(_source?.BufferProgress / 100.0 * EstimateTotalBytes() ?? 0)
        };
    }

    private long EstimateTotalBytes()
    {
        if (_currentStreamInfo.DurationMs <= 0) return 0;
        return _currentStreamInfo.DurationMs * _currentStreamInfo.Bitrate / 8;
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        return _source?.GetBufferedRanges() ?? [];
    }

    public string CurrentCodec => _currentStreamInfo.Codec;

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _stateLock.Dispose();
        _seekLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _stateLock.Dispose();
        _seekLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}