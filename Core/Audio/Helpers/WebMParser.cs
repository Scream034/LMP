using System.Buffers;
using System.Buffers.Binary;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Парсер WebM/Matroska контейнера для извлечения OPUS пакетов.
/// </summary>
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

    private readonly Stream _stream;
    private readonly byte[] _readBuffer;
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

            _segmentOffset = _stream.Position;

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
                    var timecodeData = ArrayPool<byte>.Shared.Rent((int)size);
                    try
                    {
                        await ReadExactAsync(timecodeData.AsMemory(0, (int)size), ct);
                        _currentClusterTimecode = ReadUnsignedInt(timecodeData.AsSpan(0, (int)size));
                    }
                    finally { ArrayPool<byte>.Shared.Return(timecodeData); }
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

        int trackNumberByte = await ReadByteAsync(ct);
        if (trackNumberByte < 0) return null;

        int trackNumberLength = GetVIntLength((byte)trackNumberByte);
        long trackNumber;
        if (trackNumberLength == 1)
        {
            trackNumber = trackNumberByte & 0x7F;
        }
        else
        {
            var trackBytes = ArrayPool<byte>.Shared.Rent(trackNumberLength);
            try
            {
                trackBytes[0] = (byte)trackNumberByte;
                await ReadExactAsync(trackBytes.AsMemory(1, trackNumberLength - 1), ct);
                trackNumber = ReadVInt(trackBytes.AsSpan(0, trackNumberLength));
            }
            finally { ArrayPool<byte>.Shared.Return(trackBytes); }
        }

        var timecodeBytes = ArrayPool<byte>.Shared.Rent(2);
        short timecodeOffset;
        try
        {
            await ReadExactAsync(timecodeBytes.AsMemory(0, 2), ct);
            timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(timecodeBytes);
        }
        finally { ArrayPool<byte>.Shared.Return(timecodeBytes); }

        int flags = await ReadByteAsync(ct);
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

        // ═══ Аренда памяти вместо new byte[] ═══
        var memoryOwner = MemoryPool<byte>.Shared.Rent(dataSize);
        try
        {
            await ReadExactAsync(memoryOwner.Memory[..dataSize], ct);
            long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;
            return new AudioBlock(memoryOwner, dataSize, timestampMs, isKeyFrame);
        }
        catch
        {
            memoryOwner.Dispose(); // Защита от утечек при обрыве связи
            throw;
        }
    }

    private async ValueTask<AudioBlock?> ParseLacedBlockAsync(
        int dataSize, int lacingType, short timecodeOffset, bool isKeyFrame, CancellationToken ct)
    {
        int frameCount = await ReadByteAsync(ct);
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
                    int b = await ReadByteAsync(ct);
                    if (b < 0) return null;
                    totalSizeBytes++;
                    firstFrameSize += b;
                    if (b < 255) break;
                }

                for (int i = 1; i < frameCount - 1; i++)
                {
                    while (true)
                    {
                        int b = await ReadByteAsync(ct);
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
                int vintByte = await ReadByteAsync(ct);
                if (vintByte < 0) return null;

                int vintLength = GetVIntLength((byte)vintByte);

                if (vintLength == 1)
                {
                    firstFrameSize = vintByte & 0x7F;
                }
                else
                {
                    var vintBytes = ArrayPool<byte>.Shared.Rent(vintLength);
                    try
                    {
                        vintBytes[0] = (byte)vintByte;
                        await ReadExactAsync(vintBytes.AsMemory(1, vintLength - 1), ct);
                        firstFrameSize = (int)ReadVInt(vintBytes.AsSpan(0, vintLength));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(vintBytes);
                    }
                }

                int skipVintBytes = 0;
                for (int i = 1; i < frameCount - 1; i++)
                {
                    int b = await ReadByteAsync(ct);
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
        long endPosition = _stream.Position + size;

        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case TIMECODE_SCALE_ID:
                    // ReadUnsignedInt!
                    var scaleData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(scaleData.AsMemory(0, (int)elementSize), ct);
                        _timecodeScale = ReadUnsignedInt(scaleData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(scaleData);
                    }
                    break;

                case DURATION_ID:
                    var durationData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(durationData.AsMemory(0, (int)elementSize), ct);
                        _duration = (long)ReadFloat(durationData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(durationData);
                    }
                    break;

                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
    }

    private async ValueTask ParseTracksAsync(long size, CancellationToken ct)
    {
        long endPosition = _stream.Position + size;

        while (_stream.Position < endPosition)
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
        long endPosition = _stream.Position + size;
        int trackNumber = 0;
        int trackType = 0;

        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case TRACK_NUMBER_ID:
                    // ReadUnsignedInt!
                    var numData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(numData.AsMemory(0, (int)elementSize), ct);
                        trackNumber = (int)ReadUnsignedInt(numData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(numData);
                    }
                    break;

                case TRACK_TYPE_ID:
                    // ReadUnsignedInt!
                    var typeData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(typeData.AsMemory(0, (int)elementSize), ct);
                        trackType = (int)ReadUnsignedInt(typeData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(typeData);
                    }
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
        long endPosition = _stream.Position + size;

        while (_stream.Position < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct);

            switch (id)
            {
                case SAMPLING_FREQUENCY_ID:
                    var freqData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(freqData.AsMemory(0, (int)elementSize), ct);
                        SampleRate = (int)ReadFloat(freqData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(freqData);
                    }
                    break;

                case CHANNELS_ID:
                    // ReadUnsignedInt!
                    var chData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(chData.AsMemory(0, (int)elementSize), ct);
                        Channels = (int)ReadUnsignedInt(chData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chData);
                    }
                    break;

                default:
                    await SkipBytesAsync(elementSize, ct);
                    break;
            }
        }
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
                    // ReadUnsignedInt!
                    var timeData = ArrayPool<byte>.Shared.Rent((int)elementSize);
                    try
                    {
                        await ReadExactAsync(timeData.AsMemory(0, (int)elementSize), ct);
                        time = ReadUnsignedInt(timeData.AsSpan(0, (int)elementSize));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(timeData);
                    }
                    break;

                case CUE_TRACK_POSITIONS_ID:
                    long posEnd = _stream.Position + elementSize;
                    while (_stream.Position < posEnd)
                    {
                        var (posId, posSize) = await ReadElementHeaderAsync(ct);
                        if (posId == CUE_CLUSTER_POSITION_ID)
                        {
                            // ReadUnsignedInt!
                            var posData = ArrayPool<byte>.Shared.Rent((int)posSize);
                            try
                            {
                                await ReadExactAsync(posData.AsMemory(0, (int)posSize), ct);
                                clusterPosition = ReadUnsignedInt(posData.AsSpan(0, (int)posSize));
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(posData);
                            }
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

        // ID — это VINT (с маркерным битом)
        int idLength = GetVIntLength((byte)firstByte);
        var idBytes = ArrayPool<byte>.Shared.Rent(idLength);
        uint id;
        try
        {
            idBytes[0] = (byte)firstByte;
            if (idLength > 1)
            {
                await ReadExactAsync(idBytes.AsMemory(1, idLength - 1), ct);
            }
            // ID читаем КАК ЕСТЬ (включая маркер), это Element ID
            id = (uint)ReadUnsignedInt(idBytes.AsSpan(0, idLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(idBytes);
        }

        firstByte = await ReadByteAsync(ct);
        if (firstByte < 0) return (0, 0);

        // Size — это VINT (маркер показывает длину, сам маркер убираем)
        int sizeLength = GetVIntLength((byte)firstByte);
        var sizeBytes = ArrayPool<byte>.Shared.Rent(sizeLength);
        long size;
        try
        {
            sizeBytes[0] = (byte)firstByte;
            if (sizeLength > 1)
            {
                await ReadExactAsync(sizeBytes.AsMemory(1, sizeLength - 1), ct);
            }
            size = ReadVInt(sizeBytes.AsSpan(0, sizeLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sizeBytes);
        }

        return (id, size);
    }

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

    #endregion

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}