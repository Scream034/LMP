using System.Buffers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Парсер MP4/M4A контейнера для извлечения AAC фреймов.
/// Поддерживает обычный MP4 и базовую поддержку fragmented MP4 (fMP4).
/// </summary>
public sealed class Mp4ContainerParser : IContainerParser
{
    private readonly Stream _stream;
    
    private long _durationMs;
    private byte[]? _decoderConfig;
    private int _sampleRate;
    private int _channels;
    
    private List<SampleInfo> _samples = [];
    private int _currentSampleIndex;
    
    private uint _timeScale = 1;
    private bool _isFragmented;
    private bool _disposed;
    
    public long DurationMs => _durationMs;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig => _decoderConfig;
    public int SampleRate => _sampleRate > 0 ? _sampleRate : 44100;
    public int Channels => _channels > 0 ? _channels : 2;
    
    public Mp4ContainerParser(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        try
        {
            _stream.Position = 0;
            
            bool foundMoov = false;
            bool foundMdat = false;
            
            while (_stream.Position < _stream.Length)
            {
                ct.ThrowIfCancellationRequested();
                
                if (_stream.Length - _stream.Position < 8)
                    break;
                
                var (size, type) = await ReadBoxHeaderAsync(ct);
                
                if (size == 0)
                    size = _stream.Length - _stream.Position + 8;
                
                long boxEnd = _stream.Position - 8 + size;
                
                switch (type)
                {
                    case "moov":
                        await ParseMoovAsync(boxEnd, ct);
                        foundMoov = true;
                        break;
                    
                    case "moof":
                        _isFragmented = true;
                        // Для fMP4 без moov — логируем и продолжаем
                        if (!foundMoov)
                        {
                            Log.Debug("[Mp4Parser] Found moof before moov, fragmented MP4");
                        }
                        await SkipToAsync(boxEnd, ct);
                        break;
                    
                    case "mdat":
                        foundMdat = true;
                        await SkipToAsync(boxEnd, ct);
                        break;
                    
                    case "sidx":
                        // Segment index для fMP4 — пропускаем
                        await SkipToAsync(boxEnd, ct);
                        break;
                    
                    default:
                        await SkipToAsync(boxEnd, ct);
                        break;
                }
                
                // Для обычного MP4: нашли moov и mdat
                if (foundMoov && foundMdat && _samples.Count > 0)
                    break;
            }
            
            if (_samples.Count == 0)
            {
                if (_isFragmented)
                {
                    Log.Warn("[Mp4Parser] Fragmented MP4 detected, sequential reading only");
                    return _decoderConfig != null;
                }
                
                Log.Error("[Mp4Parser] No samples found");
                return false;
            }
            
            Log.Debug($"[Mp4Parser] Parsed: {_samples.Count} samples, duration={_durationMs}ms, " +
                      $"rate={_sampleRate}, channels={_channels}, fragmented={_isFragmented}");
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Mp4Parser] Parse failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public async ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        if (_currentSampleIndex >= _samples.Count)
            return null;
        
        var sample = _samples[_currentSampleIndex++];
        
        _stream.Position = sample.Offset;
        
        var data = ArrayPool<byte>.Shared.Rent(sample.Size);
        try
        {
            await ReadExactlyAsync(data.AsMemory(0, sample.Size), ct);
            
            // Копируем в новый массив нужного размера
            var frameData = new byte[sample.Size];
            Buffer.BlockCopy(data, 0, frameData, 0, sample.Size);
            
            return new AudioFrame
            {
                Data = frameData,
                TimestampMs = sample.TimestampMs,
                DurationMs = sample.DurationMs,
                IsKeyFrame = true
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
    
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        if (_samples.Count == 0) return null;
        
        int left = 0, right = _samples.Count - 1;
        
        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            if (_samples[mid].TimestampMs <= targetMs)
                left = mid;
            else
                right = mid - 1;
        }
        
        var sample = _samples[left];
        _currentSampleIndex = left;
        
        return (sample.Offset, sample.TimestampMs);
    }
    
    public void Reset()
    {
        _currentSampleIndex = 0;
    }
    
    #region Exact Reading
    
    /// <summary>
    /// Читает ровно указанное количество байт или выбрасывает исключение.
    /// </summary>
    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                throw new EndOfStreamException($"Expected {buffer.Length} bytes, got {totalRead}");
            totalRead += read;
        }
    }
    
    #endregion
    
    #region Box Parsing
    
    private async ValueTask<(long size, string type)> ReadBoxHeaderAsync(CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            await ReadExactlyAsync(header.AsMemory(0, 8), ct);
            
            uint size = ReadUInt32BE(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            
            if (size == 1)
            {
                var extSize = ArrayPool<byte>.Shared.Rent(8);
                try
                {
                    await ReadExactlyAsync(extSize.AsMemory(0, 8), ct);
                    return ((long)ReadUInt64BE(extSize), type);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(extSize);
                }
            }
            
            return (size, type);
        }
        catch (EndOfStreamException)
        {
            return (0, "");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }
    
    private async Task ParseMoovAsync(long boxEnd, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();
            
            var (size, type) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;
            
            long childEnd = _stream.Position - 8 + size;
            
            if (type == "trak")
            {
                await ParseTrakAsync(childEnd, ct);
            }
            else if (type == "mvhd")
            {
                await ParseMvhdAsync(childEnd, ct);
            }
            else
            {
                await SkipToAsync(childEnd, ct);
            }
        }
    }
    
    private async Task ParseMvhdAsync(long boxEnd, CancellationToken ct)
    {
        int version = _stream.ReadByte();
        await SkipBytesAsync(3, ct); // flags
        
        if (version == 1)
        {
            await SkipBytesAsync(16, ct);
            _timeScale = await ReadUInt32BEAsync(ct);
            long duration = (long)await ReadUInt64BEAsync(ct);
            _durationMs = duration * 1000 / _timeScale;
        }
        else
        {
            await SkipBytesAsync(8, ct);
            _timeScale = await ReadUInt32BEAsync(ct);
            uint duration = await ReadUInt32BEAsync(ct);
            _durationMs = duration * 1000 / _timeScale;
        }
        
        await SkipToAsync(boxEnd, ct);
    }
    
    private async Task ParseTrakAsync(long boxEnd, CancellationToken ct)
    {
        bool isAudioTrack = false;
        uint trackTimeScale = 1;
        
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();
            
            var (size, type) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;
            
            long childEnd = _stream.Position - 8 + size;
            
            if (type == "mdia")
            {
                while (_stream.Position < childEnd)
                {
                    var (mSize, mType) = await ReadBoxHeaderAsync(ct);
                    if (mSize == 0) break;
                    
                    long mChildEnd = _stream.Position - 8 + mSize;
                    
                    if (mType == "hdlr")
                    {
                        await SkipBytesAsync(8, ct);
                        var handlerBytes = ArrayPool<byte>.Shared.Rent(4);
                        try
                        {
                            await ReadExactlyAsync(handlerBytes.AsMemory(0, 4), ct);
                            string handlerType = System.Text.Encoding.ASCII.GetString(handlerBytes, 0, 4);
                            isAudioTrack = handlerType == "soun";
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(handlerBytes);
                        }
                        await SkipToAsync(mChildEnd, ct);
                    }
                    else if (mType == "mdhd")
                    {
                        int version = _stream.ReadByte();
                        await SkipBytesAsync(3, ct);
                        
                        if (version == 1)
                        {
                            await SkipBytesAsync(16, ct);
                            trackTimeScale = await ReadUInt32BEAsync(ct);
                        }
                        else
                        {
                            await SkipBytesAsync(8, ct);
                            trackTimeScale = await ReadUInt32BEAsync(ct);
                        }
                        await SkipToAsync(mChildEnd, ct);
                    }
                    else if (mType == "minf" && isAudioTrack)
                    {
                        await ParseMinfAsync(mChildEnd, trackTimeScale, ct);
                    }
                    else
                    {
                        await SkipToAsync(mChildEnd, ct);
                    }
                }
            }
            else
            {
                await SkipToAsync(childEnd, ct);
            }
        }
    }
    
    private async Task ParseMinfAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            var (size, type) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;
            
            long childEnd = _stream.Position - 8 + size;
            
            if (type == "stbl")
            {
                await ParseStblAsync(childEnd, timeScale, ct);
            }
            else
            {
                await SkipToAsync(childEnd, ct);
            }
        }
    }
    
    private async Task ParseStblAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        List<uint> sampleSizes = [];
        List<(uint firstChunk, uint samplesPerChunk, uint descriptionIndex)> stsc = [];
        List<long> chunkOffsets = [];
        List<(uint count, uint delta)> stts = [];
        
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();
            
            var (size, type) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;
            
            long childEnd = _stream.Position - 8 + size;
            
            switch (type)
            {
                case "stsd":
                    await ParseStsdAsync(childEnd, ct);
                    break;
                
                case "stsz":
                    await SkipBytesAsync(4, ct);
                    uint defaultSize = await ReadUInt32BEAsync(ct);
                    uint count = await ReadUInt32BEAsync(ct);
                    
                    for (uint i = 0; i < count; i++)
                    {
                        sampleSizes.Add(defaultSize == 0 ? await ReadUInt32BEAsync(ct) : defaultSize);
                    }
                    break;
                
                case "stsc":
                    await SkipBytesAsync(4, ct);
                    uint entryCount = await ReadUInt32BEAsync(ct);
                    
                    for (uint i = 0; i < entryCount; i++)
                    {
                        uint firstChunk = await ReadUInt32BEAsync(ct);
                        uint samplesPerChunk = await ReadUInt32BEAsync(ct);
                        uint descIndex = await ReadUInt32BEAsync(ct);
                        stsc.Add((firstChunk, samplesPerChunk, descIndex));
                    }
                    break;
                
                case "stco":
                    await SkipBytesAsync(4, ct);
                    uint offsetCount = await ReadUInt32BEAsync(ct);
                    
                    for (uint i = 0; i < offsetCount; i++)
                    {
                        chunkOffsets.Add(await ReadUInt32BEAsync(ct));
                    }
                    break;
                
                case "co64":
                    await SkipBytesAsync(4, ct);
                    uint offset64Count = await ReadUInt32BEAsync(ct);
                    
                    for (uint i = 0; i < offset64Count; i++)
                    {
                        chunkOffsets.Add((long)await ReadUInt64BEAsync(ct));
                    }
                    break;
                
                case "stts":
                    await SkipBytesAsync(4, ct);
                    uint sttsCount = await ReadUInt32BEAsync(ct);
                    
                    for (uint i = 0; i < sttsCount; i++)
                    {
                        uint sampleCount = await ReadUInt32BEAsync(ct);
                        uint sampleDelta = await ReadUInt32BEAsync(ct);
                        stts.Add((sampleCount, sampleDelta));
                    }
                    break;
                
                default:
                    await SkipToAsync(childEnd, ct);
                    break;
            }
            
            await SkipToAsync(childEnd, ct);
        }
        
        BuildSampleTable(sampleSizes, stsc, chunkOffsets, stts, timeScale);
    }
    
    private async Task ParseStsdAsync(long boxEnd, CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint entryCount = await ReadUInt32BEAsync(ct);
        
        if (entryCount == 0) return;
        
        var (size, type) = await ReadBoxHeaderAsync(ct);
        if (size == 0) return;
        
        long entryEnd = _stream.Position - 8 + size;
        
        await SkipBytesAsync(6, ct); // reserved
        await SkipBytesAsync(2, ct); // data_reference_index
        await SkipBytesAsync(8, ct); // reserved
        
        _channels = await ReadUInt16BEAsync(ct);
        await SkipBytesAsync(2, ct); // sample_size
        await SkipBytesAsync(4, ct); // pre_defined, reserved
        _sampleRate = (int)(await ReadUInt32BEAsync(ct) >> 16);
        
        // Find esds box
        while (_stream.Position < entryEnd)
        {
            var (boxSize, boxType) = await ReadBoxHeaderAsync(ct);
            if (boxSize == 0) break;
            
            long nestedEnd = _stream.Position - 8 + boxSize;
            
            if (boxType == "esds")
            {
                await ParseEsdsAsync(ct);
                break;
            }
            
            await SkipToAsync(nestedEnd, ct);
        }
        
        await SkipToAsync(entryEnd, ct);
    }
    
    private async Task ParseEsdsAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct); // version, flags
        
        if (_stream.ReadByte() != 0x03) return;
        await ReadDescriptorLengthAsync(ct);
        await SkipBytesAsync(2, ct); // ES_ID
        await SkipBytesAsync(1, ct); // flags
        
        if (_stream.ReadByte() != 0x04) return;
        await ReadDescriptorLengthAsync(ct);
        await SkipBytesAsync(1, ct); // objectTypeIndication
        await SkipBytesAsync(4, ct); // streamType, bufferSizeDB
        await SkipBytesAsync(4, ct); // maxBitrate
        await SkipBytesAsync(4, ct); // avgBitrate
        
        if (_stream.ReadByte() != 0x05) return;
        int dsiLength = await ReadDescriptorLengthAsync(ct);
        
        _decoderConfig = new byte[dsiLength];
        await ReadExactlyAsync(_decoderConfig, ct);
        
        Log.Debug($"[Mp4Parser] Decoder config: {BitConverter.ToString(_decoderConfig)}");
    }
    
    private async ValueTask<int> ReadDescriptorLengthAsync(CancellationToken ct)
    {
        int length = 0;
        int b;
        
        do
        {
            b = _stream.ReadByte();
            if (b < 0) break;
            length = (length << 7) | (b & 0x7F);
        } while ((b & 0x80) != 0);
        
        return length;
    }
    
    private void BuildSampleTable(
        List<uint> sampleSizes,
        List<(uint firstChunk, uint samplesPerChunk, uint descriptionIndex)> stsc,
        List<long> chunkOffsets,
        List<(uint count, uint delta)> stts,
        uint timeScale)
    {
        if (sampleSizes.Count == 0 || chunkOffsets.Count == 0) return;
        
        _samples = new List<SampleInfo>(sampleSizes.Count);
        
        var timestamps = new List<long>(sampleSizes.Count);
        long time = 0;
        
        foreach (var (count, delta) in stts)
        {
            for (uint i = 0; i < count; i++)
            {
                timestamps.Add(time);
                time += delta;
            }
        }
        
        int sampleIndex = 0;
        int stscIndex = 0;
        
        for (int chunkIndex = 0; chunkIndex < chunkOffsets.Count && sampleIndex < sampleSizes.Count; chunkIndex++)
        {
            while (stscIndex + 1 < stsc.Count && stsc[stscIndex + 1].firstChunk - 1 <= chunkIndex)
            {
                stscIndex++;
            }
            
            uint samplesInChunk = stsc.Count > 0 ? stsc[stscIndex].samplesPerChunk : 1;
            long offset = chunkOffsets[chunkIndex];
            
            for (uint i = 0; i < samplesInChunk && sampleIndex < sampleSizes.Count; i++)
            {
                long timestampMs = timestamps.Count > sampleIndex
                    ? timestamps[sampleIndex] * 1000 / timeScale
                    : sampleIndex * 20;
                
                int durationMs = sampleIndex + 1 < timestamps.Count
                    ? (int)((timestamps[sampleIndex + 1] - timestamps[sampleIndex]) * 1000 / timeScale)
                    : 20;
                
                _samples.Add(new SampleInfo
                {
                    Offset = offset,
                    Size = (int)sampleSizes[sampleIndex],
                    TimestampMs = timestampMs,
                    DurationMs = durationMs
                });
                
                offset += sampleSizes[sampleIndex];
                sampleIndex++;
            }
        }
        
        if (_samples.Count > 0)
        {
            _durationMs = _samples[^1].TimestampMs + _samples[^1].DurationMs;
        }
    }
    
    #endregion
    
    #region Helpers
    
    private static uint ReadUInt32BE(ReadOnlySpan<byte> bytes)
    {
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }
    
    private static ulong ReadUInt64BE(ReadOnlySpan<byte> bytes)
    {
        return ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) |
               ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32) |
               ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) |
               ((ulong)bytes[6] << 8) | bytes[7];
    }
    
    private async ValueTask<uint> ReadUInt32BEAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            await ReadExactlyAsync(buffer.AsMemory(0, 4), ct);
            return ReadUInt32BE(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private async ValueTask<ulong> ReadUInt64BEAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            await ReadExactlyAsync(buffer.AsMemory(0, 8), ct);
            return ReadUInt64BE(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private async ValueTask<ushort> ReadUInt16BEAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(2);
        try
        {
            await ReadExactlyAsync(buffer.AsMemory(0, 2), ct);
            return (ushort)((buffer[0] << 8) | buffer[1]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private async ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        if (_stream.CanSeek)
        {
            _stream.Position += count;
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min((int)count, 8192));
            try
            {
                while (count > 0)
                {
                    int toRead = (int)Math.Min(count, buffer.Length);
                    int read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
    
    private async ValueTask SkipToAsync(long position, CancellationToken ct)
    {
        if (_stream.Position < position)
        {
            await SkipBytesAsync(position - _stream.Position, ct);
        }
    }
    
    #endregion
    
    private struct SampleInfo
    {
        public long Offset;
        public int Size;
        public long TimestampMs;
        public int DurationMs;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stream управляется извне
    }
    
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}