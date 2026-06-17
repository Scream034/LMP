// Core/Audio/Sources/LocalFileSource.cs

using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник для локальных аудио файлов (полностью закэшированных или скачанных).
/// 
/// <para><b>Характеристики:</b></para>
/// <list type="bullet">
///   <item>Всегда fully buffered (BufferProgress = 100%)</item>
///   <item>Seek через точки контейнера (Cluster/moof), без линейной интерполяции</item>
///   <item>Поддерживает WebM/Opus, MP4/AAC, Ogg/Opus</item>
/// </list>
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <c>ReadFrameAsync</c> и <c>SeekAsync</c> НЕ потокобезопасны между собой.
/// Вызывающий код (<see cref="AudioPipeline"/>) гарантирует последовательный доступ:
/// <c>StopDecoding → Seek → StartDecoding</c>.
/// </summary>
public sealed class LocalFileSource : IAudioSource
{
    private const long StreamStartPosition = 0;

    private readonly string _filePath;
    private readonly long _expectedSize;
    private readonly string? _trackId; // Храним trackId для точечной инвалидации кэша

    private FileStream? _fileStream;
    private IContainerParser? _parser;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;


    /// <summary>
    /// Создаёт источник из локального файла.
    /// </summary>
    /// <param name="filePath">Путь к аудио файлу.</param>
    /// <param name="expectedSize">Ожидаемый размер файла в байтах.</param>
    /// <param name="trackId">Идентификатор трека (если файл открыт из кэша).</param>
    public LocalFileSource(string filePath, long expectedSize = 0, string? trackId = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _expectedSize = expectedSize;
        _trackId = trackId;

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }

    #region Properties

    /// <inheritdoc/>
    public long DurationMs { get; private set; } = UnknownDurationMs;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;

    /// <inheritdoc/>
    /// <remarks>Всегда 100% — файл полностью доступен.</remarks>
    public double BufferProgress => FullBufferProgressPercent;

    /// <inheritdoc/>
    /// <remarks>Всегда true — файл полностью доступен.</remarks>
    public bool IsFullyBuffered => true;

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? UnknownSampleRate;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? UnknownChannels;

    /// <summary>
    /// Парсер контейнера. Доступен для диагностики (resync detection).
    /// </summary>
    internal IContainerParser? Parser => _parser;

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            _fileStream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: CacheFileBufferSize, useAsync: true);

            // Defense-in-depth: если вызывающий код передал ожидаемый размер,
            // проверяем что файл не усечён ДО парсинга заголовков.
            // Основная проверка — в AudioCacheManager.EnsureCacheFileIntegrity,
            // но между lookup и open файл мог измениться (race condition).
            if (_expectedSize > 0 && _fileStream.Length < _expectedSize)
            {
                Log.Error($"[LocalFileSource] Truncated file rejected: " +
                          $"actual={_fileStream.Length}, expected={_expectedSize}, " +
                          $"path={Path.GetFileName(_filePath)}");
                return false;
            }

            var format = await DetectFormatAsync(ct);
            _parser = CreateParser(format);

            if (!await _parser.ParseHeadersAsync(ct))
            {
                Log.Error("[LocalFileSource] Failed to parse container headers");
                return false;
            }

            DurationMs = _parser.DurationMs;
            Codec = _parser.Codec;
            _initialized = true;

            Log.Info($"[LocalFileSource] Initialized: duration={DurationMs}ms, " +
                     $"codec={Codec}, size={_fileStream.Length / BytesPerKilobyte}KB");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[LocalFileSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Определяет формат контейнера по magic bytes, с fallback на расширение файла.
    /// Буфер арендуется из <see cref="System.Buffers.ArrayPool{T}.Shared"/>
    /// для избежания heap-аллокации при инициализации.
    /// </summary>
    private async Task<AudioFormat> DetectFormatAsync(CancellationToken ct)
    {
        var header = System.Buffers.ArrayPool<byte>.Shared.Rent(FormatDetectionHeaderSize);
        try
        {
            int totalRead = 0;

            while (totalRead < FormatDetectionHeaderSize)
            {
                int read = await _fileStream!.ReadAsync(
                    header.AsMemory(totalRead, FormatDetectionHeaderSize - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            _fileStream!.Position = StreamStartPosition;

            var format = AudioSourceFactory.DetectFormatByMagic(header);

            if (format != AudioFormat.Unknown)
                return format;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(header);
        }

        // Fallback на расширение
        return Path.GetExtension(_filePath).ToLowerInvariant() switch
        {
            ".webm" => AudioFormat.WebM,
            ".m4a" or ".mp4" or ".aac" => AudioFormat.Mp4,
            ".ogg" or ".opus" => AudioFormat.Ogg,
            _ => throw new NotSupportedException(
                $"Cannot detect audio format for: {Path.GetFileName(_filePath)}")
        };
    }

    /// <summary>
    /// Создаёт парсер контейнера.
    /// </summary>
    private IContainerParser CreateParser(AudioFormat format) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_fileStream!),
        AudioFormat.Mp4 => new Mp4ContainerParser(_fileStream!),
        _ => throw new NotSupportedException($"Unsupported format: {format}")
    };

    #endregion

    #region Reading

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Surgical Cache Repair:</b> при обнаружении коррупции в файле кэша 
    /// мы НЕ удаляем его целиком. Вместо этого мы точечно сбрасываем бит повреждённого чанка в ноль, 
    /// сохраняя весь остальной файл на диске, и инициируем ретрай. Новый CachingStreamSource откроет 
    /// этот же файл и докачает из сети ТОЛЬКО повреждённый 64KB-сегмент.</para>
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        int maxHealingAttempts = 3;
        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            try
            {
                var frame = await _parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame == null) return null;

                Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                return frame;
            }
            catch (ParserCorruptionException ex)
            {
                if (ct.IsCancellationRequested) throw;

                // Сценарий А: Файл открыт из кэша. Запускаем хирургическую точечную инвалидацию чанка
                if (!string.IsNullOrEmpty(_trackId) && _expectedSize > 0)
                {
                    int chunkSize = ChunkSize;
                    int chunkIndex = (int)(ex.AbsoluteBytePosition / chunkSize);

                    Log.Warn($"[LocalFileSource] Cache corruption detected for track {_trackId} at byte {ex.AbsoluteBytePosition} (Chunk {chunkIndex}). Performing surgical invalidation.");

                    // Хирургически инвалидируем только поврежденный чанк, сохраняя остальной файл целым!
                    AudioSourceFactory.GlobalCache?.InvalidateCacheChunkByTrackId(_trackId, chunkIndex);

                    throw new CacheInvalidatedException(
                        $"Cache corruption detected at byte {ex.AbsoluteBytePosition} (Chunk {chunkIndex})",
                        CacheInvalidationKind.ParserResync,
                        isRecoverable: true,
                        trackId: _trackId,
                        chunkIndex: chunkIndex,
                        inner: ex);
                }

                // Сценарий Б: Это кастомный локальный файл или мы оффлайн. Пропускаем битый чанк
                Log.Warn($"[LocalFileSource] Offline fallback: skipping corrupted range around byte {ex.AbsoluteBytePosition}");

                int chunkIndexFallback = (int)(ex.AbsoluteBytePosition / ChunkSize);
                long nextChunkBoundary = (long)(chunkIndexFallback + 1) * ChunkSize;

                if (nextChunkBoundary >= _fileStream!.Length)
                {
                    return null; // Безопасный EOF
                }

                _fileStream.Position = nextChunkBoundary;
                _parser.RequireResync();

                Volatile.Write(ref _positionMs, DurationMs * nextChunkBoundary / _fileStream.Length);
            }
        }

        throw new InvalidDataException("Unrecoverable local file corruption after maximum resync attempts.");
    }

    #endregion

    #region Seeking

    /// <inheritdoc/>
    public ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _fileStream == null)
            return ValueTask.FromResult(false);

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null)
        {
            Log.Warn($"[LocalFileSource] No seek point for {positionMs}ms");
            return ValueTask.FromResult(false);
        }

        Log.Debug($"[LocalFileSource] Seek: target={positionMs}ms, actual={seekInfo.Value.TimestampMs}ms, byte={seekInfo.Value.BytePosition}");

        _fileStream.Position = seekInfo.Value.BytePosition;
        _parser.Reset();
        Volatile.Write(ref _positionMs, seekInfo.Value.TimestampMs);

        return ValueTask.FromResult(true);
    }

    #endregion

    #region Buffer Info (No-op for local files)

    /// <inheritdoc/>
    /// <remarks>Всегда возвращает полный диапазон [0, 1].</remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => [(BufferedRangeStart, BufferedRangeEnd)];

    /// <inheritdoc/>
    /// <remarks>No-op — нет RAM буферов для локальных файлов.</remarks>
    public void ReleaseRamBuffers() { }

    /// <inheritdoc/>
    /// <remarks>No-op — нет фоновых операций для локальных файлов.</remarks>
    public void CancelPendingOperations() { }

    /// <inheritdoc/>
    public void SetPlaybackActive(bool active) { }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _parser?.Dispose();
        _fileStream?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parser != null)
            await _parser.DisposeAsync();

        if (_fileStream != null)
            await _fileStream.DisposeAsync();
    }

    #endregion
}
