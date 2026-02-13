using System.Buffers.Binary;

namespace LMP.Core.Helpers;

/// <summary>
/// Парсер WebM/Matroska контейнера для извлечения OPUS пакетов.
/// Поддерживает инкрементальный парсинг для стриминга.
/// </summary>
public sealed class WebMParser : IDisposable
{
    // EBML Element IDs
    private const uint EBML_ID = 0x1A45DFA3;
    private const uint SEGMENT_ID = 0x18538067;
    private const uint CLUSTER_ID = 0x1F43B675;
    private const uint SIMPLE_BLOCK_ID = 0xA3;
    private const uint BLOCK_GROUP_ID = 0xA0;
    private const uint BLOCK_ID = 0xA1;
    private const uint TIMECODE_ID = 0xE7;
    private const uint INFO_ID = 0x1549A966;
    private const uint DURATION_ID = 0x4489;
    private const uint TIMECODE_SCALE_ID = 0x2AD7B1;
    private const uint TRACKS_ID = 0x1654AE6B;
    private const uint TRACK_ENTRY_ID = 0xAE;
    private const uint CUES_ID = 0x1C53BB6B;
    private const uint CUE_POINT_ID = 0xBB;
    private const uint CUE_TIME_ID = 0xB3;
    private const uint CUE_TRACK_POSITIONS_ID = 0xB7;
    private const uint CUE_CLUSTER_POSITION_ID = 0xF1;
    private const uint CODEC_ID = 0x86;
    
    private readonly Stream _stream;
    private readonly byte[] _readBuffer;
    private readonly List<CuePoint> _cuePoints = new();
    
    private long _segmentOffset;
    private long _currentClusterTimecode;
    private long _timecodeScale = 1_000_000; // По умолчанию наносекунды
    private long _duration; // В единицах timecodeScale
    private bool _headersParsed;
    
    /// <summary>
    /// Информация о точке поиска (cue point)
    /// </summary>
    public readonly record struct CuePoint(long TimeMs, long ClusterOffset);
    
    /// <summary>
    /// Извлечённый аудио-блок
    /// </summary>
    public readonly record struct AudioBlock(
        ReadOnlyMemory<byte> Data,
        long TimestampMs,
        bool IsKeyFrame
    );
    
    public WebMParser(Stream stream, int bufferSize = 64 * 1024)
    {
        _stream = stream;
        _readBuffer = new byte[bufferSize];
    }
    
    /// <summary>Длительность в миллисекундах</summary>
    public long DurationMs => (long)(_duration * _timecodeScale / 1_000_000);
    
    /// <summary>Точки поиска для seeking</summary>
    public IReadOnlyList<CuePoint> CuePoints => _cuePoints;
    
    /// <summary>
    /// Парсит заголовки WebM файла.
    /// </summary>
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        if (_headersParsed) return true;
        
        try
        {
            // Читаем EBML header
            var (id, size) = await ReadElementHeaderAsync(ct);
            if (id != EBML_ID) return false;
            
            await SkipBytesAsync(size, ct);
            
            // Читаем Segment
            (id, size) = await ReadElementHeaderAsync(ct);
            if (id != SEGMENT_ID) return false;
            
            _segmentOffset = _stream.Position;
            
            // Парсим элементы Segment до первого Cluster
            while (!ct.IsCancellationRequested)
            {
                long elementStart = _stream.Position;
                (id, size) = await ReadElementHeaderAsync(ct);
                
                switch (id)
                {
                    case INFO_ID:
                        await ParseInfoAsync(size, ct);
                        break;
                    
                    case TRACKS_ID:
                        await ParseTracksAsync(size, ct);
                        break;
                    
                    case CUES_ID:
                        await ParseCuesAsync(size, ct);
                        break;
                    
                    case CLUSTER_ID:
                        // Вернуться к началу Cluster для последующего чтения
                        if (_stream.CanSeek)
                        {
                            _stream.Position = elementStart;
                        }
                        _headersParsed = true;
                        return true;
                    
                    default:
                        if (size > 0 && size < int.MaxValue)
                        {
                            await SkipBytesAsync(size, ct);
                        }
                        break;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Читает следующий аудио-блок из потока.
    /// </summary>
    public async ValueTask<AudioBlock?> ReadNextBlockAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var (id, size) = await ReadElementHeaderAsync(ct);
            
            if (id == 0) return null; // EOF
            
            switch (id)
            {
                case CLUSTER_ID:
                    // Cluster не имеет фиксированного размера в streaming режиме
                    continue;
                
                case TIMECODE_ID:
                    var timecodeData = new byte[size];
                    await ReadExactAsync(timecodeData, ct);
                    _currentClusterTimecode = ReadVInt(timecodeData);
                    continue;
                
                case SIMPLE_BLOCK_ID:
                    return await ParseSimpleBlockAsync((int)size, ct);
                
                case BLOCK_GROUP_ID:
                    // Block Group содержит Block и дополнительные элементы
                    // Пропускаем для простоты, читаем как SimpleBlock
                    await SkipBytesAsync(size, ct);
                    continue;
                
                default:
                    if (size > 0 && size < int.MaxValue)
                    {
                        await SkipBytesAsync(size, ct);
                    }
                    continue;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Ищет позицию ближайшего кластера для указанного времени.
    /// </summary>
    public long? FindSeekPosition(long targetMs)
    {
        if (_cuePoints.Count == 0) return null;
        
        // Бинарный поиск
        int low = 0, high = _cuePoints.Count - 1;
        
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_cuePoints[mid].TimeMs <= targetMs)
                low = mid;
            else
                high = mid - 1;
        }
        
        return _segmentOffset + _cuePoints[low].ClusterOffset;
    }
    
    private async ValueTask<AudioBlock> ParseSimpleBlockAsync(int size, CancellationToken ct)
    {
        // Track Number (1-4 bytes variable)
        int trackNumberSize = 1;
        int trackNumber = await ReadByteAsync(ct);
        
        // Timecode offset (2 bytes, signed)
        var timecodeOffsetData = new byte[2];
        await ReadExactAsync(timecodeOffsetData, ct);
        short timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(timecodeOffsetData);
        
        // Flags (1 byte)
        int flags = await ReadByteAsync(ct);
        bool isKeyFrame = (flags & 0x80) != 0;
        
        // Остальное - аудио данные
        int dataSize = size - trackNumberSize - 2 - 1;
        var audioData = new byte[dataSize];
        await ReadExactAsync(audioData, ct);
        
        long timestampMs = _currentClusterTimecode + timecodeOffset;
        
        return new AudioBlock(audioData, timestampMs, isKeyFrame);
    }
    
    private async ValueTask ParseInfoAsync(long size, CancellationToken ct)
    {
        long endPosition = _stream.Position + size;
        
        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);
            
            switch (id)
            {
                case TIMECODE_SCALE_ID:
                    var scaleData = new byte[elementSize];
                    await ReadExactAsync(scaleData, ct);
                    _timecodeScale = ReadVInt(scaleData);
                    break;
                
                case DURATION_ID:
                    var durationData = new byte[elementSize];
                    await ReadExactAsync(durationData, ct);
                    _duration = (long)ReadFloat(durationData);
                    break;
                
                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
    }
    
    private async ValueTask ParseTracksAsync(long size, CancellationToken ct)
    {
        // Для простоты пропускаем детальный парсинг треков
        // В production версии нужно извлечь codec info
        await SkipBytesAsync(size, ct);
    }
    
    private async ValueTask ParseCuesAsync(long size, CancellationToken ct)
    {
        long endPosition = _stream.Position + size;
        
        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);
            
            if (id == CUE_POINT_ID)
            {
                var cuePoint = await ParseCuePointAsync(elementSize, ct);
                if (cuePoint.HasValue)
                {
                    _cuePoints.Add(cuePoint.Value);
                }
            }
            else
            {
                await SkipBytesAsync(elementSize, ct);
            }
        }
    }
    
    private async ValueTask<CuePoint?> ParseCuePointAsync(long size, CancellationToken ct)
    {
        long endPosition = _stream.Position + size;
        long time = 0;
        long clusterPosition = 0;
        
        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);
            
            switch (id)
            {
                case CUE_TIME_ID:
                    var timeData = new byte[elementSize];
                    await ReadExactAsync(timeData, ct);
                    time = ReadVInt(timeData);
                    break;
                
                case CUE_TRACK_POSITIONS_ID:
                    long posEnd = _stream.Position + elementSize;
                    while (_stream.Position < posEnd)
                    {
                        var (posId, posSize) = await ReadElementHeaderAsync(ct);
                        if (posId == CUE_CLUSTER_POSITION_ID)
                        {
                            var posData = new byte[posSize];
                            await ReadExactAsync(posData, ct);
                            clusterPosition = ReadVInt(posData);
                        }
                        else
                        {
                            await SkipBytesAsync(posSize, ct);
                        }
                    }
                    break;
                
                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
        
        long timeMs = time * _timecodeScale / 1_000_000;
        return new CuePoint(timeMs, clusterPosition);
    }
    
    private async ValueTask<(uint Id, long Size)> ReadElementHeaderAsync(CancellationToken ct)
    {
        int firstByte = await ReadByteAsync(ct);
        if (firstByte < 0) return (0, 0);
        
        // Parse VINT ID
        int idLength = GetVIntLength((byte)firstByte);
        var idBytes = new byte[idLength];
        idBytes[0] = (byte)firstByte;
        if (idLength > 1)
        {
            await ReadExactAsync(idBytes.AsMemory(1, idLength - 1), ct);
        }
        uint id = (uint)ReadVIntRaw(idBytes);
        
        // Parse VINT Size
        firstByte = await ReadByteAsync(ct);
        if (firstByte < 0) return (0, 0);
        
        int sizeLength = GetVIntLength((byte)firstByte);
        var sizeBytes = new byte[sizeLength];
        sizeBytes[0] = (byte)firstByte;
        if (sizeLength > 1)
        {
            await ReadExactAsync(sizeBytes.AsMemory(1, sizeLength - 1), ct);
        }
        long size = ReadVInt(sizeBytes);
        
        return (id, size);
    }
    
    private static int GetVIntLength(byte firstByte)
    {
        if ((firstByte & 0x80) != 0) return 1;
        if ((firstByte & 0x40) != 0) return 2;
        if ((firstByte & 0x20) != 0) return 3;
        if ((firstByte & 0x10) != 0) return 4;
        if ((firstByte & 0x08) != 0) return 5;
        if ((firstByte & 0x04) != 0) return 6;
        if ((firstByte & 0x02) != 0) return 7;
        return 8;
    }
    
    private static long ReadVIntRaw(ReadOnlySpan<byte> data)
    {
        long value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }
        return value;
    }
    
    private static long ReadVInt(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        
        // Убираем маркерный бит
        int length = GetVIntLength(data[0]);
        byte mask = (byte)(0xFF >> length);
        
        long value = data[0] & mask;
        for (int i = 1; i < data.Length; i++)
        {
            value = (value << 8) | data[i];
        }
        
        return value;
    }
    
    private static double ReadFloat(ReadOnlySpan<byte> data)
    {
        return data.Length switch
        {
            4 => BinaryPrimitives.ReadSingleBigEndian(data),
            8 => BinaryPrimitives.ReadDoubleBigEndian(data),
            _ => 0
        };
    }
    
    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        int read = await _stream.ReadAsync(_readBuffer.AsMemory(0, 1), ct);
        return read == 1 ? _readBuffer[0] : -1;
    }
    
    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                throw new EndOfStreamException();
            totalRead += read;
        }
    }
    
    private async ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        if (_stream.CanSeek)
        {
            _stream.Position += count;
            return;
        }
        
        while (count > 0)
        {
            int toRead = (int)Math.Min(count, _readBuffer.Length);
            int read = await _stream.ReadAsync(_readBuffer.AsMemory(0, toRead), ct);
            if (read == 0) break;
            count -= read;
        }
    }
    
    public void Dispose()
    {
        // Stream управляется внешним кодом
        GC.SuppressFinalize(this);
    }
}