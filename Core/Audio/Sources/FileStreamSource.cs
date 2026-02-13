using LMP.Core.Audio.Interfaces;
using LMP.Core.Helpers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио из локального файла (WebM/Opus).
/// </summary>
public sealed class FileStreamSource : IAudioSource
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private WebMParser? _parser;
    
    private long _durationMs = -1;
    private long _positionMs;
    private long _fileLength;
    private bool _initialized;
    private bool _disposed;
    
    public FileStreamSource(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }
    
    public long DurationMs => _durationMs;
    public long PositionMs => _positionMs;
    public bool CanSeek => true;
    public AudioCodec Codec => AudioCodec.Opus;
    public double BufferProgress => 100; // Файл всегда "загружен"
    public bool IsFullyBuffered => true;
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            Log.Debug($"[FileSource] Opening: {_filePath}");
            
            _fileStream = new FileStream(
                _filePath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            
            _fileLength = _fileStream.Length;
            
            _parser = new WebMParser(_fileStream);
            
            if (!await _parser.ParseHeadersAsync(ct))
            {
                Log.Error("[FileSource] Failed to parse WebM headers");
                return false;
            }
            
            _durationMs = _parser.DurationMs;
            _initialized = true;
            
            Log.Info($"[FileSource] Initialized: duration={_durationMs}ms, size={_fileLength}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[FileSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Not initialized");
        
        var block = await _parser.ReadNextBlockAsync(ct);
        
        if (block == null)
            return null;
        
        _positionMs = block.Value.TimestampMs;
        
        return new AudioFrame
        {
            Data = block.Value.Data,
            TimestampMs = block.Value.TimestampMs,
            DurationMs = 20, // Typical Opus frame
            IsKeyFrame = block.Value.IsKeyFrame
        };
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _fileStream == null) return false;
        
        var byteOffset = _parser.FindSeekPosition(positionMs);
        
        if (byteOffset == null && _fileLength > 0 && _durationMs > 0)
        {
            // Approximate seek
            byteOffset = (long)(_fileLength * ((double)positionMs / _durationMs));
        }
        
        if (byteOffset == null) return false;
        
        Log.Debug($"[FileSource] Seek to {positionMs}ms (offset: {byteOffset})");
        
        _fileStream.Position = byteOffset.Value;
        
        // Reinitialize parser at new position
        _parser.Dispose();
        _parser = new WebMParser(_fileStream);
        await _parser.ParseHeadersAsync(ct);
        
        _positionMs = positionMs;
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        return [(0.0, 1.0)]; // Весь файл доступен
    }
    
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