using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Универсальный источник для локальных файлов (WebM, MP4, M4A, Ogg).
/// </summary>
public sealed class LocalFileSource : IAudioSource
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private IContainerParser? _parser;
    
    private long _durationMs = -1;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;
    
    public LocalFileSource(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }
    
    public long DurationMs => _durationMs;
    public long PositionMs => Interlocked.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;
    public double BufferProgress => 100;
    public bool IsFullyBuffered => true;
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            Log.Debug($"[LocalFileSource] Opening: {_filePath}");
            
            _fileStream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            
            // Определяем формат
            var header = new byte[12];
            int totalRead = 0;
            while (totalRead < 12)
            {
                int read = await _fileStream.ReadAsync(header.AsMemory(totalRead, 12 - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }
            _fileStream.Position = 0;
            
            var format = AudioSourceFactory.DetectFormatByMagic(header);
            
            if (format == AudioFormat.Unknown)
            {
                var ext = Path.GetExtension(_filePath).ToLowerInvariant();
                format = ext switch
                {
                    ".webm" => AudioFormat.WebM,
                    ".m4a" or ".mp4" or ".aac" => AudioFormat.Mp4,
                    ".ogg" or ".opus" => AudioFormat.Ogg,
                    _ => AudioFormat.Unknown
                };
            }
            
            Log.Debug($"[LocalFileSource] Format: {format}");
            
            _parser = format switch
            {
                AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_fileStream),
                AudioFormat.Mp4 => new Mp4ContainerParser(_fileStream),
                _ => throw new NotSupportedException($"Unsupported format: {format}")
            };
            
            if (!await _parser.ParseHeadersAsync(ct))
            {
                Log.Error("[LocalFileSource] Failed to parse headers");
                return false;
            }
            
            _durationMs = _parser.DurationMs;
            Codec = _parser.Codec;
            _initialized = true;
            
            Log.Info($"[LocalFileSource] Initialized: duration={_durationMs}ms, codec={Codec}, size={_fileStream.Length / 1024}KB");
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[LocalFileSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Not initialized");
        
        var frame = await _parser.ReadNextFrameAsync(ct);
        
        if (frame == null)
            return null;
        
        Interlocked.Exchange(ref _positionMs, frame.Value.TimestampMs);
        return frame;
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _fileStream == null) return false;
        
        var seekInfo = _parser.FindSeekPosition(positionMs);
        
        if (seekInfo == null && _fileStream.Length > 0 && _durationMs > 0)
        {
            long approxPosition = (long)(_fileStream.Length * ((double)positionMs / _durationMs));
            seekInfo = (approxPosition, positionMs);
        }
        
        if (seekInfo == null) return false;
        
        Log.Debug($"[LocalFileSource] Seek to {positionMs}ms (byte: {seekInfo.Value.BytePosition})");
        
        _fileStream.Position = seekInfo.Value.BytePosition;
        _parser.Reset();
        
        Interlocked.Exchange(ref _positionMs, positionMs);
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => [(0.0, 1.0)];
    
    public void ReleaseRamBuffers() { }
    
    public void CancelPendingOperations() { }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _parser?.Dispose();
        _fileStream?.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _parser?.Dispose();
        if (_fileStream != null)
            await _fileStream.DisposeAsync();
    }
}