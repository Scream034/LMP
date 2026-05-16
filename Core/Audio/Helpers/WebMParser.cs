using System.Buffers;
using System.Buffers.Binary;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Парсер WebM/Matroska контейнера для извлечения OPUS пакетов.
/// </summary>
/// <remarks>
/// <para><b>Оптимизация I/O:</b></para>
/// <para>Использует внутренний read-ahead буфер для минимизации async вызовов.
/// Побайтовое чтение EBML-заголовков (VINT ID + VINT Size) происходит из буфера синхронно.
/// Async ReadAsync к нижележащему потоку выполняется только при исчерпании буфера,
/// что сокращает количество state machine allocations на порядки.</para>
/// </remarks>
public sealed class WebMParser : IDisposable
{
    // EBML Element IDs
    private const uint EBML_ID = 0x1A45DFA3;
    private const uint SEGMENT_ID = 0x18538067;
    private const uint CLUSTER_ID = 0x1F43B675;
    private const uint SIMPLE_BLOCK_ID = 0xA3;
    private const uint BLOCK_GROUP_ID = 0xA0;
    private const uint TIMECODE_ID = 0xE7;
    private const uint INFO_ID = 0x1549A966;
    private const uint DURATION_ID = 0x4489;
    private const uint TIMECODE_SCALE_ID = 0x2AD7B1;
    private const uint TRACKS_ID = 0x1654AE6B;
    private const uint TRACK_ENTRY_ID = 0xAE;
    private const uint TRACK_NUMBER_ID = 0xD7;
    private const uint TRACK_TYPE_ID = 0x83;
    private const uint CODEC_PRIVATE_ID = 0x63A2;
    private const uint AUDIO_ID = 0xE1;
    private const uint SAMPLING_FREQUENCY_ID = 0xB5;
    private const uint CHANNELS_ID = 0x9F;
    private const uint CUES_ID = 0x1C53BB6B;
    private const uint CUE_POINT_ID = 0xBB;
    private const uint CUE_TIME_ID = 0xB3;
    private const uint CUE_TRACK_POSITIONS_ID = 0xB7;
    private const uint CUE_CLUSTER_POSITION_ID = 0xF1;

    // Lacing types
    private const int LACING_NONE = 0;
    private const int LACING_XIPH = 1;
    private const int LACING_FIXED = 2;
    private const int LACING_EBML = 3;

    private const long DEFAULT_TIMECODE_SCALE = 1_000_000; // 1ms в наносекундах
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>Максимальный размер EBML элемента, который мы готовы буферизировать целиком (ID + Size + данные).</summary>
    private const int InlineDataLimit = 8;

    private readonly Stream _stream;

    // ═══ Buffered Reader State ═══
    // Внутренний буфер для минимизации async вызовов.
    // Все побайтовые чтения (VINT parsing, flags, timecodes) обслуживаются синхронно из буфера.
    // Async refill вызывается только когда буфер исчерпан.
    private readonly byte[] _readBuffer;
    private int _bufPos;
    private int _bufLen;

    private readonly List<CuePoint> _cuePoints = [];

    private long _segmentOffset;
    private long _currentClusterTimecode;
    private long _timecodeScale = DEFAULT_TIMECODE_SCALE;
    private long _duration;
    private int _audioTrackNumber = 1;
    private bool _headersParsed;

    public readonly record struct CuePoint(long TimeMs, long ClusterOffset);

    /// <summary>
    /// Блок аудиоданных. 
    /// Owner предоставляет арендованную память. Вызывающий код обязан вызвать Owner.Dispose(), когда данные больше не нужны.
    /// </summary>
    public readonly record struct AudioBlock(
        IMemoryOwner<byte> Owner,
        int Length,
        long TimestampMs,
        bool IsKeyFrame
    );

    public WebMParser(Stream stream, int bufferSize = ReadBufferSize)
    {
        _stream = stream;
        _readBuffer = new byte[bufferSize];
    }

    public long DurationMs => _duration * _timecodeScale / 1_000_000;
    public IReadOnlyList<CuePoint> CuePoints => _cuePoints;
    public byte[]? CodecPrivate { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; } = 2;

    #region Buffered Reader — синхронное чтение из внутреннего буфера

    /// <summary>
    /// Гарантирует наличие как минимум <paramref name="needed"/> байт в буфере.
    /// Возвращает false при EOF до набора нужного количества.
    /// </summary>
    private async ValueTask<bool> EnsureBufferedAsync(int needed, CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available >= needed) return true;

        // Сдвигаем оставшиеся байты в начало буфера
        if (available > 0 && _bufPos > 0)
        {
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);
        }
        _bufPos = 0;
        _bufLen = available;

        // Дочитываем до нужного количества
        while (_bufLen < needed)
        {
            int toRead = _readBuffer.Length - _bufLen;
            int read = await _stream.ReadAsync(
                _readBuffer.AsMemory(_bufLen, toRead), ct).ConfigureAwait(false);
            if (read == 0) return _bufLen >= needed;
            _bufLen += read;
        }
        return true;
    }

    /// <summary>
    /// Заполняет буфер максимально возможным количеством данных. Для потокового чтения.
    /// </summary>
    private async ValueTask FillBufferAsync(CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available > 0 && _bufPos > 0)
        {
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);
        }
        _bufPos = 0;
        _bufLen = available;

        int toRead = _readBuffer.Length - _bufLen;
        if (toRead > 0)
        {
            int read = await _stream.ReadAsync(
                _readBuffer.AsMemory(_bufLen, toRead), ct).ConfigureAwait(false);
            _bufLen += read;
        }
    }

    /// <summary>
    /// Читает один байт из буфера. Вызывать только после <see cref="EnsureBufferedAsync"/>(1).
    /// </summary>
    private int BufferedReadByte()
    {
        if (_bufPos >= _bufLen) return -1;
        return _readBuffer[_bufPos++];
    }

    /// <summary>
    /// Возвращает <see cref="ReadOnlySpan{T}"/> на <paramref name="count"/> байт из буфера
    /// и продвигает позицию. Вызывать только после <see cref="EnsureBufferedAsync"/>(<paramref name="count"/>).
    /// </summary>
    private ReadOnlySpan<byte> BufferedReadSpan(int count)
    {
        var span = _readBuffer.AsSpan(_bufPos, count);
        _bufPos += count;
        return span;
    }

    /// <summary>
    /// Читает точно <paramref name="buffer"/>.Length байт, комбинируя буфер и async refill.
    /// </summary>
    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        int remaining = buffer.Length;

        // Сначала берём что есть в буфере
        int buffered = Math.Min(remaining, _bufLen - _bufPos);
        if (buffered > 0)
        {
            _readBuffer.AsSpan(_bufPos, buffered).CopyTo(buffer.Span[..buffered]);
            _bufPos += buffered;
            offset += buffered;
            remaining -= buffered;
        }

        // Остальное читаем напрямую из stream, минуя буфер (для больших блоков)
        while (remaining > 0)
        {
            int read = await _stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
            remaining -= read;
        }
    }

    /// <summary>
    /// Пропускает <paramref name="count"/> байт, используя буфер и seek.
    /// </summary>
    private async ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        // Сначала пропускаем буферизированные байты
        int buffered = (int)Math.Min(count, _bufLen - _bufPos);
        _bufPos += buffered;
        count -= buffered;

        if (count <= 0) return;

        if (_stream.CanSeek)
        {
            _stream.Position += count;
            // Инвалидируем буфер — позиция stream изменилась
            _bufPos = 0;
            _bufLen = 0;
            return;
        }

        // Fallback: потоковый skip
        while (count > 0)
        {
            await FillBufferAsync(ct).ConfigureAwait(false);
            int skip = (int)Math.Min(count, _bufLen - _bufPos);
            if (skip == 0) break;
            _bufPos += skip;
            count -= skip;
        }
    }

    #endregion

    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        if (_headersParsed) return true;

        try
        {
            var (id, size) = await ReadElementHeaderAsync(ct);
            if (id != EBML_ID) return false;

            await SkipBytesAsync(size, ct);

            (id, size) = await ReadElementHeaderAsync(ct);
            if (id != SEGMENT_ID) return false;

            _segmentOffset = _stream.Position - (_bufLen - _bufPos);

            while (!ct.IsCancellationRequested)
            {
                long elementStart = _stream.Position - (_bufLen - _bufPos);
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
                        if (_stream.CanSeek)
                        {
                            _stream.Position = elementStart;
                            _bufPos = 0;
                            _bufLen = 0;
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

    public async ValueTask<AudioBlock?> ReadNextBlockAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var (id, size) = await ReadElementHeaderAsync(ct);

            if (id == 0) return null;

            switch (id)
            {
                case CLUSTER_ID:
                    continue;

                case TIMECODE_ID:
                    if (!await EnsureBufferedAsync((int)size, ct)) return null;
                    _currentClusterTimecode = ReadUnsignedInt(BufferedReadSpan((int)size));
                    continue;

                case SIMPLE_BLOCK_ID:
                    return await ParseSimpleBlockAsync((int)size, ct);

                case BLOCK_GROUP_ID:
                    await SkipBytesAsync(size, ct);
                    continue;

                default:
                    if (size > 0 && size < int.MaxValue) await SkipBytesAsync(size, ct);
                    continue;
            }
        }
        return null;
    }

    public long? FindSeekPosition(long targetMs)
    {
        if (_cuePoints.Count == 0) return null;

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

    private async ValueTask<AudioBlock?> ParseSimpleBlockAsync(int size, CancellationToken ct)
    {
        if (size < 4) return null;

        // Заголовок SimpleBlock: trackNum(VINT) + timecodeOffset(2) + flags(1)
        // Максимум 8 + 2 + 1 = 11 байт. Буферизируем минимум 4 байта для начала.
        if (!await EnsureBufferedAsync(Math.Min(size, 12), ct)) return null;

        int trackNumberByte = BufferedReadByte();
        if (trackNumberByte < 0) return null;

        int trackNumberLength = GetVIntLength((byte)trackNumberByte);
        long trackNumber;
        if (trackNumberLength == 1)
        {
            trackNumber = trackNumberByte & 0x7F;
        }
        else
        {
            if (!await EnsureBufferedAsync(trackNumberLength - 1, ct)) return null;
            Span<byte> trackBytes = stackalloc byte[trackNumberLength];
            trackBytes[0] = (byte)trackNumberByte;
            BufferedReadSpan(trackNumberLength - 1).CopyTo(trackBytes[1..]);
            trackNumber = ReadVInt(trackBytes);
        }

        if (!await EnsureBufferedAsync(3, ct)) return null; // 2 timecode + 1 flags
        short timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(BufferedReadSpan(2));
        int flags = BufferedReadByte();
        if (flags < 0) return null;

        bool isKeyFrame = (flags & 0x80) != 0;
        int lacingType = (flags >> 1) & 0x03;

        int headerSize = trackNumberLength + 2 + 1;
        int dataSize = size - headerSize;

        if (dataSize <= 0) return null;

        if (trackNumber != _audioTrackNumber)
        {
            await SkipBytesAsync(dataSize, ct);
            return null;
        }

        if (lacingType != LACING_NONE)
        {
            return await ParseLacedBlockAsync(dataSize, lacingType, timecodeOffset, isKeyFrame, ct);
        }

        var memoryOwner = MemoryPool<byte>.Shared.Rent(dataSize);
        try
        {
            await ReadExactAsync(memoryOwner.Memory[..dataSize], ct);
            long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;
            return new AudioBlock(memoryOwner, dataSize, timestampMs, isKeyFrame);
        }
        catch
        {
            memoryOwner.Dispose();
            throw;
        }
    }

    private async ValueTask<AudioBlock?> ParseLacedBlockAsync(
        int dataSize, int lacingType, short timecodeOffset, bool isKeyFrame, CancellationToken ct)
    {
        if (!await EnsureBufferedAsync(1, ct)) return null;
        int frameCount = BufferedReadByte();
        if (frameCount < 0) return null;
        frameCount++;

        dataSize--;

        int firstFrameSize;
        int bytesToSkip;

        switch (lacingType)
        {
            case LACING_XIPH:
                firstFrameSize = 0;
                int totalSizeBytes = 0;

                while (true)
                {
                    if (!await EnsureBufferedAsync(1, ct)) return null;
                    int b = BufferedReadByte();
                    if (b < 0) return null;
                    totalSizeBytes++;
                    firstFrameSize += b;
                    if (b < 255) break;
                }

                for (int i = 1; i < frameCount - 1; i++)
                {
                    while (true)
                    {
                        if (!await EnsureBufferedAsync(1, ct)) return null;
                        int b = BufferedReadByte();
                        if (b < 0) return null;
                        totalSizeBytes++;
                        if (b < 255) break;
                    }
                }

                bytesToSkip = dataSize - totalSizeBytes - firstFrameSize;
                break;

            case LACING_FIXED:
                firstFrameSize = dataSize / frameCount;
                bytesToSkip = dataSize - firstFrameSize;
                break;

            case LACING_EBML:
                if (!await EnsureBufferedAsync(1, ct)) return null;
                int vintByte = BufferedReadByte();
                if (vintByte < 0) return null;

                int vintLength = GetVIntLength((byte)vintByte);

                if (vintLength == 1)
                {
                    firstFrameSize = vintByte & 0x7F;
                }
                else
                {
                    if (!await EnsureBufferedAsync(vintLength - 1, ct)) return null;
                    Span<byte> vintBytes = stackalloc byte[vintLength];
                    vintBytes[0] = (byte)vintByte;
                    BufferedReadSpan(vintLength - 1).CopyTo(vintBytes[1..]);
                    firstFrameSize = (int)ReadVInt(vintBytes);
                }

                int skipVintBytes = 0;
                for (int i = 1; i < frameCount - 1; i++)
                {
                    if (!await EnsureBufferedAsync(1, ct)) return null;
                    int b = BufferedReadByte();
                    if (b < 0) return null;
                    int len = GetVIntLength((byte)b);
                    skipVintBytes++;
                    if (len > 1)
                    {
                        await SkipBytesAsync(len - 1, ct);
                        skipVintBytes += len - 1;
                    }
                }

                bytesToSkip = dataSize - vintLength - skipVintBytes - firstFrameSize;
                break;

            default:
                await SkipBytesAsync(dataSize, ct);
                return null;
        }

        var memoryOwner = MemoryPool<byte>.Shared.Rent(firstFrameSize);
        try
        {
            await ReadExactAsync(memoryOwner.Memory[..firstFrameSize], ct);
            if (bytesToSkip > 0) await SkipBytesAsync(bytesToSkip, ct);

            long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;
            return new AudioBlock(memoryOwner, firstFrameSize, timestampMs, isKeyFrame);
        }
        catch
        {
            memoryOwner.Dispose();
            throw;
        }
    }

    private async ValueTask ParseInfoAsync(long size, CancellationToken ct)
    {
        long endPosition = _stream.Position - (_bufLen - _bufPos) + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case TIMECODE_SCALE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    _timecodeScale = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case DURATION_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    _duration = (long)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
    }

    private async ValueTask ParseTracksAsync(long size, CancellationToken ct)
    {
        long endPosition = StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            if (id == TRACK_ENTRY_ID)
            {
                await ParseTrackEntryAsync(elementSize, ct);
            }
            else
            {
                await SkipBytesAsync(elementSize, ct);
            }
        }
    }

    private async ValueTask ParseTrackEntryAsync(long size, CancellationToken ct)
    {
        long endPosition = StreamPositionWithBuffer() + size;
        int trackNumber = 0;
        int trackType = 0;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case TRACK_NUMBER_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    trackNumber = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case TRACK_TYPE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    trackType = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CODEC_PRIVATE_ID:
                    CodecPrivate = new byte[elementSize];
                    await ReadExactAsync(CodecPrivate, ct);
                    break;

                case AUDIO_ID:
                    await ParseAudioSettingsAsync(elementSize, ct);
                    break;

                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }

        if (trackType == 2 && trackNumber > 0)
        {
            _audioTrackNumber = trackNumber;
        }
    }

    private async ValueTask ParseAudioSettingsAsync(long size, CancellationToken ct)
    {
        long endPosition = StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case SAMPLING_FREQUENCY_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    SampleRate = (int)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                case CHANNELS_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return;
                    Channels = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
    }

    private async ValueTask ParseCuesAsync(long size, CancellationToken ct)
    {
        long endPosition = StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
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
        long endPosition = StreamPositionWithBuffer() + size;
        long time = 0;
        long clusterPosition = 0;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case CUE_TIME_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct)) return null;
                    time = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CUE_TRACK_POSITIONS_ID:
                    long posEnd = StreamPositionWithBuffer() + elementSize;
                    while (StreamPositionWithBuffer() < posEnd)
                    {
                        var (posId, posSize) = await ReadElementHeaderAsync(ct);
                        if (posId == CUE_CLUSTER_POSITION_ID)
                        {
                            if (!await EnsureBufferedAsync((int)posSize, ct)) return null;
                            clusterPosition = ReadUnsignedInt(BufferedReadSpan((int)posSize));
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

    /// <summary>
    /// Читает EBML Element Header (ID + Size) из буфера.
    /// </summary>
    /// <remarks>
    /// EBML header состоит из двух VINT: Element ID (маркер сохраняется) и Element Size (маркер снимается).
    /// Максимальный размер: 8 (ID) + 8 (Size) = 16 байт.
    /// Буферизация гарантирует, что парсинг заголовка выполняется синхронно
    /// из внутреннего буфера без async state machine allocation.
    /// </remarks>
    private async ValueTask<(uint Id, long Size)> ReadElementHeaderAsync(CancellationToken ct)
    {
        // Заголовок EBML: max 8 (ID VINT) + 8 (Size VINT) = 16 байт.
        // Буферизируем минимум 2 байта (по 1 на каждый VINT минимальной длины).
        if (!await EnsureBufferedAsync(2, ct)) return (0, 0);

        int firstByte = BufferedReadByte();
        if (firstByte < 0) return (0, 0);

        // ID — VINT с маркерным битом (маркер остаётся)
        int idLength = GetVIntLength((byte)firstByte);
        if (idLength > 1 && !await EnsureBufferedAsync(idLength - 1, ct)) return (0, 0);

        uint id;
        if (idLength == 1)
        {
            id = (uint)firstByte;
        }
        else
        {
            Span<byte> idBytes = stackalloc byte[idLength];
            idBytes[0] = (byte)firstByte;
            BufferedReadSpan(idLength - 1).CopyTo(idBytes[1..]);
            id = (uint)ReadUnsignedInt(idBytes);
        }

        // Size — VINT (маркерный бит снимается)
        if (!await EnsureBufferedAsync(1, ct)) return (0, 0);
        firstByte = BufferedReadByte();
        if (firstByte < 0) return (0, 0);

        int sizeLength = GetVIntLength((byte)firstByte);
        if (sizeLength > 1 && !await EnsureBufferedAsync(sizeLength - 1, ct)) return (0, 0);

        long size;
        if (sizeLength == 1)
        {
            size = firstByte & 0x7F;
        }
        else
        {
            Span<byte> sizeBytes = stackalloc byte[sizeLength];
            sizeBytes[0] = (byte)firstByte;
            BufferedReadSpan(sizeLength - 1).CopyTo(sizeBytes[1..]);
            size = ReadVInt(sizeBytes);
        }

        return (id, size);
    }

    /// <summary>
    /// Виртуальная позиция в потоке с учётом буферизированных, но непрочитанных байт.
    /// </summary>
    private long StreamPositionWithBuffer() => _stream.Position - (_bufLen - _bufPos);

    #region Read Helpers

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

    /// <summary>
    /// Читает unsigned integer (big-endian), без снятия маркерного бита.
    /// Используется для data elements (Timecode, TrackNumber и т.д.)
    /// </summary>
    private static long ReadUnsignedInt(ReadOnlySpan<byte> data)
    {
        long value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }
        return value;
    }

    /// <summary>
    /// Читает VINT Size, снимая маркерный бит.
    /// Используется ТОЛЬКО для element size в headers.
    /// </summary>
    private static long ReadVInt(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;

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

    #endregion

    /// <summary>
    /// Сбрасывает состояние парсера для продолжения чтения с новой позиции потока.
    public void Reset()
    {
        _bufPos = 0;
        _bufLen = 0;
        _currentClusterTimecode = 0;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}