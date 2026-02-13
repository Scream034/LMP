using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using LMP.Core.Helpers;

namespace LMP.Core.Audio;

/// <summary>
/// Опции AudioPlayer.
/// </summary>
public sealed class AudioPlayerOptions
{
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxRetryAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public bool UseNullBackend { get; init; }
}

/// <summary>
/// Координатор воспроизведения: source → decoder → backend.
/// </summary>
public sealed class AudioPlayer : IAudioPlayer
{
    #region Constants
    
    private const int BufferSizeFrames = 48000 * 4; // ~4 seconds @ 48kHz
    private const int MinBufferMs = 200;
    
    #endregion
    
    #region Fields
    
    private readonly AudioPlayerOptions _options;
    private readonly HttpClient _httpClient;
    
    // Components
    private IAudioSource? _source;
    private IAudioDecoder? _decoder;
    private IPlaybackBackend? _backend;
    
    // PCM buffer
    private readonly CircularBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    
    // State (volatile для 32-bit, Interlocked для 64-bit)
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile float _volume = 1.0f;
    private volatile bool _disposed;
    
    private long _positionMs;
    private long _durationMs;
    
    private string? _currentTrackId;
    private string? _currentUrl;
    
    // Tasks
    private CancellationTokenSource? _playbackCts;
    private Task? _decoderTask;
    private Timer? _positionTimer;
    
    // Sync
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    
    #endregion
    
    #region Properties
    
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            if (_backend != null)
                _backend.Volume = _volume;
        }
    }
    
    public TimeSpan Position => TimeSpan.FromMilliseconds(Interlocked.Read(ref _positionMs));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Interlocked.Read(ref _durationMs));
    public PlaybackState State => _state;
    
    public double BufferProgress => _source?.BufferProgress ?? 0;
    public bool IsFullyBuffered => _source?.IsFullyBuffered ?? false;
    
    #endregion
    
    #region Events
    
    public event Action<TimeSpan>? PositionChanged;
    public event Action<PlaybackState>? StateChanged;
    public event Action? TrackEnded;
    public event Action<Exception>? ErrorOccurred;
    
    #endregion
    
    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();
        _httpClient = CreateHttpClient();
        
        // Buffer for ~4 seconds at 48kHz stereo
        _pcmBuffer = new CircularBuffer<float>(BufferSizeFrames * 2);
        _decodeBuffer = new float[48000 * 2]; // 1 second max
    }
    
    #region Playback Control
    
    public async Task PlayAsync(string url, string? trackId = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        await _stateLock.WaitAsync(ct);
        try
        {
            await StopInternalAsync();
            
            _currentUrl = url;
            _currentTrackId = trackId;
            
            SetState(PlaybackState.Loading);
            
            await InitializePlaybackAsync(url, ct);
            
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _decoderTask = Task.Run(() => DecoderLoopAsync(_playbackCts.Token));
            
            // Wait for buffer to fill
            await WaitForBufferAsync(_playbackCts.Token);
            
            _backend!.Start();
            
            // Правильный конструктор Timer
            int intervalMs = (int)_options.PositionUpdateInterval.TotalMilliseconds;
            _positionTimer = new Timer(
                callback: _ => UpdatePosition(),
                state: null,
                dueTime: 0,
                period: intervalMs);
            
            SetState(PlaybackState.Playing);
            
            Log.Info($"[AudioPlayer] Started: {trackId ?? "unknown"}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(ex);
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
        Log.Debug("[AudioPlayer] Paused");
    }
    
    public void Resume()
    {
        if (_state != PlaybackState.Paused) return;
        
        _backend?.Start();
        SetState(PlaybackState.Playing);
        Log.Debug("[AudioPlayer] Resumed");
    }
    
    public void Stop()
    {
        if (_state == PlaybackState.Stopped) return;
        
        _stateLock.Wait();
        try
        {
            StopInternalAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _stateLock.Release();
        }
        
        Log.Info("[AudioPlayer] Stopped");
    }
    
    public async ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_source == null || !_source.CanSeek)
        {
            Log.Warn("[AudioPlayer] Seek not supported");
            return;
        }
        
        await _stateLock.WaitAsync(ct);
        try
        {
            var wasPlaying = _state == PlaybackState.Playing;
            
            SetState(PlaybackState.Buffering);
            _backend?.Stop();
            _pcmBuffer.Clear();
            
            long positionMs = (long)position.TotalMilliseconds;
            bool success = await _source.SeekAsync(positionMs, ct);
            
            if (success)
            {
                Interlocked.Exchange(ref _positionMs, positionMs);
                Log.Debug($"[AudioPlayer] Seeked to {positionMs}ms");
            }
            
            if (wasPlaying)
            {
                await WaitForBufferAsync(ct);
                _backend?.Start();
                SetState(PlaybackState.Playing);
            }
            else
            {
                SetState(PlaybackState.Paused);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    #endregion
    
    #region Internal
    
    private async Task InitializePlaybackAsync(string url, CancellationToken ct)
    {
        bool isHls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        
        if (isHls)
        {
            var hlsSource = new HlsStreamSource(url, _httpClient, CreateUrlRefresher());
            _source = hlsSource;
            
            if (!await _source.InitializeAsync(ct))
                throw new AudioSourceException("Failed to initialize HLS source");
            
            // Create AAC decoder
            var aacDecoder = new AacDecoder();
            if (hlsSource.AudioSpecificConfig != null)
            {
                aacDecoder.Initialize(hlsSource.AudioSpecificConfig);
            }
            _decoder = aacDecoder;
        }
        else
        {
            // Assume WebM/Opus
            var contentLength = await GetContentLengthAsync(url, ct);
            if (contentLength <= 0)
                throw new AudioSourceException("Cannot determine content length");
            
            _source = new CachedStreamSource(
                _currentTrackId ?? Guid.NewGuid().ToString(),
                url,
                contentLength,
                _httpClient,
                CreateUrlRefresher());
            
            if (!await _source.InitializeAsync(ct))
                throw new AudioSourceException("Failed to initialize source");
            
            _decoder = new OpusDecoder(48000, 2);
        }
        
        Interlocked.Exchange(ref _durationMs, _source.DurationMs);
        
        // Initialize backend
        _backend = _options.UseNullBackend
            ? new NullAudioBackend()
            : new MiniaudioBackend();
        
        _backend.Initialize(_decoder.SampleRate, _decoder.Channels, AudioCallback);
        _backend.Volume = _volume;
    }
    
    private async Task DecoderLoopAsync(CancellationToken ct)
    {
        int retryCount = 0;
        
        try
        {
            while (!ct.IsCancellationRequested && _source != null && _decoder != null)
            {
                // Wait for space in buffer
                while (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels * 2)
                {
                    await Task.Delay(5, ct);
                }
                
                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct);
                    retryCount = 0;
                }
                catch (UrlExpiredException)
                {
                    if (await TryRefreshUrlAsync(ct))
                        continue;
                    throw;
                }
                catch (AudioSourceException) when (retryCount < _options.MaxRetryAttempts)
                {
                    retryCount++;
                    Log.Warn($"[AudioPlayer] Read error, retry {retryCount}");
                    await Task.Delay(_options.RetryDelay, ct);
                    continue;
                }
                
                if (frame == null)
                {
                    Log.Debug("[AudioPlayer] End of stream");
                    
                    // Wait for buffer to drain
                    while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct);
                    }
                    
                    OnTrackEnded();
                    return;
                }
                
                // Decode
                int samplesDecoded = _decoder.Decode(
                    frame.Value.Data.Span,
                    _decodeBuffer);
                
                if (samplesDecoded > 0)
                {
                    int totalSamples = samplesDecoded * _decoder.Channels;
                    _pcmBuffer.Write(_decodeBuffer.AsSpan(0, totalSamples));
                    Interlocked.Exchange(ref _positionMs, frame.Value.TimestampMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Decoder error: {ex.Message}", ex);
            ErrorOccurred?.Invoke(ex);
            SetState(PlaybackState.Error);
        }
    }
    
    private int AudioCallback(Span<float> buffer)
    {
        if (_state != PlaybackState.Playing)
        {
            buffer.Clear();
            return 0;
        }
        
        int samplesRead = _pcmBuffer.Read(buffer);
        
        // Apply volume
        if (Math.Abs(_volume - 1.0f) > 0.001f)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                buffer[i] *= _volume;
            }
        }
        
        // Clear remainder
        if (samplesRead < buffer.Length)
        {
            buffer[samplesRead..].Clear();
        }
        
        return samplesRead / 2; // Return frame count
    }
    
    private async Task WaitForBufferAsync(CancellationToken ct)
    {
        if (_decoder == null) return;
        
        int minSamples = _decoder.SampleRate * _decoder.Channels * MinBufferMs / 1000;
        
        while (_pcmBuffer.Count < minSamples && !ct.IsCancellationRequested)
        {
            await Task.Delay(10, ct);
        }
    }
    
    private async Task<bool> TryRefreshUrlAsync(CancellationToken ct)
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return false;
        
        Log.Info($"[AudioPlayer] Refreshing URL for {_currentTrackId}");
        
        try
        {
            var newUrl = await _options.UrlRefreshCallback(_currentTrackId, ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
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
        
        return async ct =>
        {
            var result = await callback(trackId, ct);
            return result;
        };
    }
    
    private void UpdatePosition()
    {
        if (_state == PlaybackState.Playing || _state == PlaybackState.Paused)
        {
            PositionChanged?.Invoke(Position);
        }
    }
    
    private async Task StopInternalAsync()
    {
        _playbackCts?.Cancel();
        
        if (_decoderTask != null)
        {
            try { await _decoderTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        
        _positionTimer?.Dispose();
        _positionTimer = null;
        
        _backend?.Stop();
        _backend?.Dispose();
        _backend = null;
        
        _decoder?.Dispose();
        _decoder = null;
        
        if (_source != null)
        {
            await _source.DisposeAsync();
            _source = null;
        }
        
        _pcmBuffer.Clear();
        Interlocked.Exchange(ref _positionMs, 0);
        Interlocked.Exchange(ref _durationMs, 0);
        _currentTrackId = null;
        _currentUrl = null;
        
        _playbackCts?.Dispose();
        _playbackCts = null;
        _decoderTask = null;
        
        SetState(PlaybackState.Stopped);
    }
    
    private void OnTrackEnded()
    {
        SetState(PlaybackState.Stopped);
        TrackEnded?.Invoke();
    }
    
    private void SetState(PlaybackState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(newState);
    }
    
    private async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }
    
    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
    
    #endregion
    
    #region Dispose
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Stop();
        _httpClient.Dispose();
        _stateLock.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        await _stateLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _stateLock.Release();
            _httpClient.Dispose();
            _stateLock.Dispose();
        }
    }
    
    #endregion
}