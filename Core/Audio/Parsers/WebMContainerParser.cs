using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Helpers;
using System.Buffers;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Адаптер WebMParser для IContainerParser.
/// Инкапсулирует логику управления памятью (Zero-Allocation) для аудио-фреймов.
/// </summary>
public sealed class WebMContainerParser : IContainerParser
{
    private readonly WebMParser _parser;
    private bool _disposed;
    
    // Хранит ссылку на арендованный буфер текущего фрейма, чтобы вернуть его при чтении следующего
    private IMemoryOwner<byte>? _currentFrameOwner;
    
    public long DurationMs => _parser.DurationMs;
    public AudioCodec Codec => AudioCodec.Opus;
    public byte[]? DecoderConfig => _parser.CodecPrivate;
    public int SampleRate => _parser.SampleRate > 0 ? _parser.SampleRate : 48000;
    public int Channels => _parser.Channels > 0 ? _parser.Channels : 2;
    
    public WebMContainerParser(Stream stream)
    {
        _parser = new WebMParser(stream);
    }
    
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        return await _parser.ParseHeadersAsync(ct);
    }
    
    public async ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        // ═══ GC ZERO: Возвращаем ПРЕДЫДУЩИЙ буфер в пул ═══
        // Декодер уже отработал с ним в предыдущей итерации цикла.
        ReleaseCurrentFrame();
        
        var block = await _parser.ReadNextBlockAsync(ct);
        
        if (block == null)
            return null;
            
        // Сохраняем владельца, чтобы освободить его при следующем вызове
        _currentFrameOwner = block.Value.Owner;
        
        return new AudioFrame
        {
            // Передаём только ту часть памяти, где реально лежат данные фрейма
            Data = _currentFrameOwner.Memory.Slice(0, block.Value.Length),
            TimestampMs = block.Value.TimestampMs,
            DurationMs = 20, // Typical Opus frame
            IsKeyFrame = block.Value.IsKeyFrame
        };
    }
    
    /// <summary>
    /// Ищет ближайшую точку входа для seek.
    /// </summary>
    /// <returns>
    /// BytePosition — абсолютная позиция в потоке (начало Cluster).
    /// TimestampMs — РЕАЛЬНЫЙ timestamp из cue point (не target!).
    /// Это позиция откуда начнётся декодирование. Decoder пропустит фреймы
    /// до targetMs через skip frames mechanism.
    /// </returns>
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        var cuePoints = _parser.CuePoints;
        if (cuePoints.Count == 0)
            return null;

        // Бинарный поиск ближайшего cue point ≤ targetMs
        int low = 0, high = cuePoints.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (cuePoints[mid].TimeMs <= targetMs)
                low = mid;
            else
                high = mid - 1;
        }

        var cue = cuePoints[low];
        long bytePos = _parser.FindSeekPosition(targetMs) ?? 0;

        // Возвращаем РЕАЛЬНЫЙ timestamp из cue point.
        // CachingStreamSource.SeekAsync установит positionMs = cue.TimeMs,
        // и AudioPipeline.SetDecodedSamplesPosition пересчитает sample position.
        return (bytePos, cue.TimeMs);
    }
    
    public void Reset()
    {
        // Очищаем текущий фрейм при перемотке
        ReleaseCurrentFrame();
        
        // ═══ Сбрасываем состояние парсера ═══
        // _currentClusterTimecode должен обновиться из первого Cluster после seek.
        // WebMParser.ReadNextBlockAsync сам обновит его при чтении TIMECODE_ID.
        // Никаких дополнительных действий не нужно — Reset фрейма достаточно.
    }
    
    private void ReleaseCurrentFrame()
    {
        _currentFrameOwner?.Dispose();
        _currentFrameOwner = null;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        ReleaseCurrentFrame();
        _parser.Dispose();
    }
    
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}