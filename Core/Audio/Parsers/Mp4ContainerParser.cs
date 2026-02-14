using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Парсер MP4/M4A контейнера для извлечения AAC фреймов.
/// </summary>
public sealed class Mp4ContainerParser : IContainerParser
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    
    private long _durationMs;
    private byte[]? _decoderConfig;
    private int _sampleRate;
    private int _channels;
    
    // Sample table
    private List<SampleInfo> _samples = [];
    private int _currentSampleIndex;
    private long _mdatOffset;
    private long _mdatSize;
    
    // Track info
    private uint _timeScale = 1;
    
    public long DurationMs => _durationMs;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig => _decoderConfig;
    
    public Mp4ContainerParser(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }
    
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        try
        {
            _stream.Position = 0;
            
            while (_stream.Position < _stream.Length)
            {
                ct.ThrowIfCancellationRequested();
                
                if (_stream.Length - _stream.Position < 8)
                    break;
                
                var (size, type) = ReadBoxHeader();
                
                if (size == 0) // Box extends to end of file
                    size = _stream.Length - _stream.Position + 8;
                
                long boxEnd = _stream.Position - 8 + size;
                
                switch (type)
                {
                    case "moov":
                        await ParseMoovAsync(boxEnd, ct);
                        break;
                    
                    case "mdat":
                        _mdatOffset = _stream.Position;
                        _mdatSize = size - 8;
                        _stream.Position = boxEnd;
                        break;
                    
                    default:
                        _stream.Position = boxEnd;
                        break;
                }
                
                // Если нашли и moov и mdat - готово
                if (_samples.Count > 0 && _mdatOffset > 0)
                    break;
            }
            
            if (_samples.Count == 0)
            {
                Log.Error("[Mp4Parser] No samples found");
                return false;
            }
            
            Log.Debug($"[Mp4Parser] Parsed: {_samples.Count} samples, duration={_durationMs}ms, " +
                      $"rate={_sampleRate}, channels={_channels}");
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Mp4Parser] Parse failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        if (_currentSampleIndex >= _samples.Count)
            return ValueTask.FromResult<AudioFrame?>(null);
        
        var sample = _samples[_currentSampleIndex++];
        
        _stream.Position = sample.Offset;
        var data = _reader.ReadBytes(sample.Size);
        
        return ValueTask.FromResult<AudioFrame?>(new AudioFrame
        {
            Data = data,
            TimestampMs = sample.TimestampMs,
            DurationMs = sample.DurationMs,
            IsKeyFrame = true // AAC frames are always keyframes
        });
    }
    
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        if (_samples.Count == 0) return null;
        
        // Binary search for nearest sample
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
    
    #region Box Parsing
    
    private (long size, string type) ReadBoxHeader()
    {
        uint size = ReadUInt32BE();
        string type = new string(_reader.ReadChars(4));
        
        if (size == 1)
        {
            // Extended size
            size = (uint)ReadUInt64BE();
        }
        
        return (size, type);
    }
    
    private async Task ParseMoovAsync(long boxEnd, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();
            
            var (size, type) = ReadBoxHeader();
            long childEnd = _stream.Position - 8 + size;
            
            if (type == "trak")
            {
                await ParseTrakAsync(childEnd, ct);
            }
            else if (type == "mvhd")
            {
                ParseMvhd((int)(childEnd - _stream.Position));
            }
            else
            {
                _stream.Position = childEnd;
            }
        }
    }
    
    private void ParseMvhd(int remaining)
    {
        int version = _reader.ReadByte();
        _stream.Position += 3; // flags
        
        if (version == 1)
        {
            _stream.Position += 16; // creation_time, modification_time
            _timeScale = ReadUInt32BE();
            long duration = (long)ReadUInt64BE();
            _durationMs = duration * 1000 / _timeScale;
        }
        else
        {
            _stream.Position += 8;
            _timeScale = ReadUInt32BE();
            uint duration = ReadUInt32BE();
            _durationMs = duration * 1000 / _timeScale;
        }
        
        // Skip rest
        _stream.Position += remaining - (version == 1 ? 28 : 16);
    }
    
    private async Task ParseTrakAsync(long boxEnd, CancellationToken ct)
    {
        bool isAudioTrack = false;
        uint trackTimeScale = 1;
        
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();
            
            var (size, type) = ReadBoxHeader();
            long childEnd = _stream.Position - 8 + size;
            
            switch (type)
            {
                case "mdia":
                    // Parse mdia to find audio track
                    while (_stream.Position < childEnd)
                    {
                        var (mSize, mType) = ReadBoxHeader();
                        long mChildEnd = _stream.Position - 8 + mSize;
                        
                        if (mType == "hdlr")
                        {
                            _stream.Position += 8; // version, flags, pre_defined
                            string handlerType = new string(_reader.ReadChars(4));
                            isAudioTrack = handlerType == "soun";
                            _stream.Position = mChildEnd;
                        }
                        else if (mType == "mdhd")
                        {
                            int version = _reader.ReadByte();
                            _stream.Position += 3;
                            
                            if (version == 1)
                            {
                                _stream.Position += 16;
                                trackTimeScale = ReadUInt32BE();
                            }
                            else
                            {
                                _stream.Position += 8;
                                trackTimeScale = ReadUInt32BE();
                            }
                            _stream.Position = mChildEnd;
                        }
                        else if (mType == "minf" && isAudioTrack)
                        {
                            await ParseMinfAsync(mChildEnd, trackTimeScale, ct);
                        }
                        else
                        {
                            _stream.Position = mChildEnd;
                        }
                    }
                    break;
                
                default:
                    _stream.Position = childEnd;
                    break;
            }
        }
    }
    
    private async Task ParseMinfAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            var (size, type) = ReadBoxHeader();
            long childEnd = _stream.Position - 8 + size;
            
            if (type == "stbl")
            {
                await ParseStblAsync(childEnd, timeScale, ct);
            }
            else
            {
                _stream.Position = childEnd;
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
            
            var (size, type) = ReadBoxHeader();
            long childEnd = _stream.Position - 8 + size;
            
            switch (type)
            {
                case "stsd":
                    ParseStsd((int)(childEnd - _stream.Position));
                    break;
                
                case "stsz":
                    _stream.Position += 4; // version, flags
                    uint defaultSize = ReadUInt32BE();
                    uint count = ReadUInt32BE();
                    
                    for (uint i = 0; i < count; i++)
                    {
                        sampleSizes.Add(defaultSize == 0 ? ReadUInt32BE() : defaultSize);
                    }
                    break;
                
                case "stsc":
                    _stream.Position += 4;
                    uint entryCount = ReadUInt32BE();
                    
                    for (uint i = 0; i < entryCount; i++)
                    {
                        uint firstChunk = ReadUInt32BE();
                        uint samplesPerChunk = ReadUInt32BE();
                        uint descIndex = ReadUInt32BE();
                        stsc.Add((firstChunk, samplesPerChunk, descIndex));
                    }
                    break;
                
                case "stco":
                    _stream.Position += 4;
                    uint offsetCount = ReadUInt32BE();
                    
                    for (uint i = 0; i < offsetCount; i++)
                    {
                        chunkOffsets.Add(ReadUInt32BE());
                    }
                    break;
                
                case "co64":
                    _stream.Position += 4;
                    uint offset64Count = ReadUInt32BE();
                    
                    for (uint i = 0; i < offset64Count; i++)
                    {
                        chunkOffsets.Add((long)ReadUInt64BE());
                    }
                    break;
                
                case "stts":
                    _stream.Position += 4;
                    uint sttsCount = ReadUInt32BE();
                    
                    for (uint i = 0; i < sttsCount; i++)
                    {
                        uint sampleCount = ReadUInt32BE();
                        uint sampleDelta = ReadUInt32BE();
                        stts.Add((sampleCount, sampleDelta));
                    }
                    break;
                
                default:
                    _stream.Position = childEnd;
                    break;
            }
            
            _stream.Position = childEnd;
        }
        
        // Build sample table
        BuildSampleTable(sampleSizes, stsc, chunkOffsets, stts, timeScale);
    }
    
    private void ParseStsd(int remaining)
    {
        _stream.Position += 4; // version, flags
        uint entryCount = ReadUInt32BE();
        
        if (entryCount == 0) return;
        
        var (size, type) = ReadBoxHeader();
        long entryEnd = _stream.Position - 8 + size;
        
        // Skip to sample description
        _stream.Position += 6; // reserved
        _stream.Position += 2; // data_reference_index
        _stream.Position += 8; // reserved
        
        _channels = ReadUInt16BE();
        _stream.Position += 2; // sample_size
        _stream.Position += 4; // pre_defined, reserved
        _sampleRate = (int)(ReadUInt32BE() >> 16);
        
        // Find esds box for decoder config
        while (_stream.Position < entryEnd)
        {
            var (boxSize, boxType) = ReadBoxHeader();
            long boxEnd = _stream.Position - 8 + boxSize;
            
            if (boxType == "esds")
            {
                ParseEsds((int)(boxEnd - _stream.Position));
                break;
            }
            
            _stream.Position = boxEnd;
        }
        
        _stream.Position = entryEnd;
    }
    
    private void ParseEsds(int remaining)
    {
        _stream.Position += 4; // version, flags
        
        // ES Descriptor
        if (_reader.ReadByte() != 0x03) return;
        ReadDescriptorLength();
        _stream.Position += 2; // ES_ID
        _stream.Position += 1; // flags
        
        // Decoder Config Descriptor
        if (_reader.ReadByte() != 0x04) return;
        ReadDescriptorLength();
        _stream.Position += 1; // objectTypeIndication
        _stream.Position += 4; // streamType, bufferSizeDB
        _stream.Position += 4; // maxBitrate
        _stream.Position += 4; // avgBitrate
        
        // Decoder Specific Info
        if (_reader.ReadByte() != 0x05) return;
        int dsiLength = ReadDescriptorLength();
        
        _decoderConfig = _reader.ReadBytes(dsiLength);
        
        Log.Debug($"[Mp4Parser] Decoder config: {BitConverter.ToString(_decoderConfig)}");
    }
    
    private int ReadDescriptorLength()
    {
        int length = 0;
        int b;
        
        do
        {
            b = _reader.ReadByte();
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
        
        // Calculate timestamps
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
        
        // Build sample info
        int sampleIndex = 0;
        int stscIndex = 0;
        
        for (int chunkIndex = 0; chunkIndex < chunkOffsets.Count && sampleIndex < sampleSizes.Count; chunkIndex++)
        {
            // Find samples per chunk
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
                    : sampleIndex * 20; // Fallback to ~20ms per frame
                
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
    
    private uint ReadUInt32BE()
    {
        var bytes = _reader.ReadBytes(4);
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }
    
    private ulong ReadUInt64BE()
    {
        var bytes = _reader.ReadBytes(8);
        return ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) |
               ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32) |
               ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) |
               ((ulong)bytes[6] << 8) | bytes[7];
    }
    
    private ushort ReadUInt16BE()
    {
        var bytes = _reader.ReadBytes(2);
        return (ushort)((bytes[0] << 8) | bytes[1]);
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
        _reader.Dispose();
    }
}