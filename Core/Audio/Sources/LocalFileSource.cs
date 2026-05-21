// Core/Audio/Sources/LocalFileSource.cs

using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
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
    private readonly string _filePath;
    private FileStream? _fileStream;
    private IContainerParser? _parser;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Создаёт источник из локального файла.
    /// </summary>
    /// <param name="filePath">Путь к аудио файлу.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is null.</exception>
    /// <exception cref="FileNotFoundException">Файл не найден.</exception>
    public LocalFileSource(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }

    #region Properties

    /// <inheritdoc/>
    public long DurationMs { get; private set; } = -1;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;

    /// <inheritdoc/>
    /// <remarks>Всегда 100% — файл полностью доступен.</remarks>
    public double BufferProgress => 100;

    /// <inheritdoc/>
    /// <remarks>Всегда true — файл полностью доступен.</remarks>
    public bool IsFullyBuffered => true;

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? 0;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? 0;

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

            _fileStream!.Position = 0;

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
    /// НЕ потокобезопасен с <see cref="SeekAsync"/>.
    /// Вызывающий код должен остановить decoder loop перед seek.
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        var frame = await _parser.ReadNextFrameAsync(ct);

        if (frame == null)
            return null;

        Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
        return frame;
    }

    #endregion

    #region Seeking

    /// <inheritdoc/>
    /// <remarks>
    /// <para>НЕ потокобезопасен с <see cref="ReadFrameAsync"/>.
    /// Вызывающий код должен остановить decoder loop перед seek.</para>
    /// 
    /// <para>Использует только точки seek из контейнера (Cluster boundaries для WebM,
    /// moof atoms для fMP4). Не использует линейную интерполяцию — она ненадёжна
    /// для VBR потоков и попадает в середину фреймов.</para>
    /// </remarks>
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

        Log.Debug($"[LocalFileSource] Seek: target={positionMs}ms, " +
                  $"actual={seekInfo.Value.TimestampMs}ms, " +
                  $"byte={seekInfo.Value.BytePosition}");

        _fileStream.Position = seekInfo.Value.BytePosition;
        _parser.Reset();
        Volatile.Write(ref _positionMs, seekInfo.Value.TimestampMs);

        return ValueTask.FromResult(true);
    }

    #endregion

    #region Buffer Info (No-op for local files)

    /// <inheritdoc/>
    /// <remarks>Всегда возвращает полный диапазон [0, 1].</remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => [(0.0, 1.0)];

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