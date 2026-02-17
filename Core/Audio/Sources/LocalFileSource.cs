using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник для локальных аудио файлов.
/// </summary>
public sealed class LocalFileSource : IAudioSource
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private IContainerParser? _parser;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;

    public LocalFileSource(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }

    public long DurationMs { get; private set; } = -1;
    public long PositionMs => Volatile.Read(ref _positionMs);
    public bool CanSeek => true;
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;
    public double BufferProgress => 100;
    public bool IsFullyBuffered => true;
    public byte[]? DecoderConfig => _parser?.DecoderConfig;
    public int SampleRate => _parser?.SampleRate ?? 0;
    public int Channels => _parser?.Channels ?? 0;

    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            _fileStream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);

            var format = await DetectFormatAsync(ct);
            _parser = CreateParser(format);

            if (!await _parser.ParseHeadersAsync(ct))
            {
                Log.Error("[LocalFileSource] Failed to parse headers");
                return false;
            }

            DurationMs = _parser.DurationMs;
            Codec = _parser.Codec;
            _initialized = true;

            Log.Info($"[LocalFileSource] Initialized: duration={DurationMs}ms, " +
                    $"codec={Codec}, size={_fileStream.Length / 1024}KB");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[LocalFileSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Reads next frame. NOT thread-safe — caller must ensure
    /// no concurrent calls with SeekAsync.
    /// </summary>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Not initialized");

        var frame = await _parser.ReadNextFrameAsync(ct);

        if (frame == null)
            return null;

        Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
        return frame;
    }

    /// <summary>
    /// Seeks to position. NOT thread-safe — caller must stop decoder loop first.
    /// </summary>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _fileStream == null) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs)
                       ?? EstimateSeekPosition(positionMs);

        if (seekInfo == null) return false;

        Log.Debug($"[LocalFileSource] Seek: target={positionMs}ms, segment={seekInfo.Value.TimestampMs}ms");

        _fileStream.Position = seekInfo.Value.BytePosition;
        _parser.Reset();

        Volatile.Write(ref _positionMs, seekInfo.Value.TimestampMs);

        return true;
    }

    private async Task<AudioFormat> DetectFormatAsync(CancellationToken ct)
    {
        var header = new byte[12];
        int totalRead = 0;
        while (totalRead < 12)
        {
            int read = await _fileStream!.ReadAsync(header.AsMemory(totalRead, 12 - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }
        _fileStream!.Position = 0;

        var format = AudioSourceFactory.DetectFormatByMagic(header);

        if (format == AudioFormat.Unknown)
        {
            format = Path.GetExtension(_filePath).ToLowerInvariant() switch
            {
                ".webm" => AudioFormat.WebM,
                ".m4a" or ".mp4" or ".aac" => AudioFormat.Mp4,
                ".ogg" or ".opus" => AudioFormat.Ogg,
                _ => AudioFormat.Unknown
            };
        }

        return format;
    }

    private IContainerParser CreateParser(AudioFormat format) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_fileStream!),
        AudioFormat.Mp4 => new Mp4ContainerParser(_fileStream!),
        _ => throw new NotSupportedException($"Unsupported format: {format}")
    };

    private (long BytePosition, long TimestampMs)? EstimateSeekPosition(long positionMs)
    {
        if (_fileStream == null || _fileStream.Length <= 0 || DurationMs <= 0) return null;
        long approxPosition = (long)(_fileStream.Length * ((double)positionMs / DurationMs));
        return (approxPosition, positionMs);
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
        if (_parser != null) await _parser.DisposeAsync();
        if (_fileStream != null) await _fileStream.DisposeAsync();
    }
}