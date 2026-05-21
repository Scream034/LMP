using System.Buffers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Парсер MP4/M4A контейнера для извлечения AAC фреймов.
/// Поддерживает обычный MP4 и Fragmented MP4 (fMP4).
/// </summary>
public sealed class Mp4ContainerParser : IContainerParser
{
    #region Fields

    private readonly Stream _stream;

    private long _durationMs;
    private byte[]? _decoderConfig;
    private int _sampleRate;
    private int _channels;

    // Для обычного MP4
    private List<SampleInfo> _samples = [];
    private int _currentSampleIndex;

    // Для fMP4
    private bool _isFragmented;
    private uint _trackTimeScale = 1;
    private uint _defaultSampleDuration;
    private uint _defaultSampleSize;
    private readonly Queue<SampleInfo> _fragmentSamples = new();

    // Для seek в fMP4
    private readonly List<SegmentInfo> _segments = [];

    // Для валидации смещений mdat
    private long _lastMdatDataStart;
    private long _lastMdatDataEnd;

    private bool _disposed;

    /// <summary>
    /// Владелец арендованного буфера текущего фрейма.
    /// Возвращается в <see cref="MemoryPool{T}.Shared"/> при чтении следующего фрейма.
    /// Аналог паттерна из <see cref="WebMContainerParser"/>.
    /// </summary>
    private IMemoryOwner<byte>? _currentFrameOwner;

    #endregion

    #region Properties

    public long DurationMs => _durationMs;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig => _decoderConfig;
    public int SampleRate => _sampleRate > 0 ? _sampleRate : 44100;
    public int Channels => _channels > 0 ? _channels : 2;

    #endregion

    public Mp4ContainerParser(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    #region Header Parsing

    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        try
        {
            _stream.Position = 0;

            bool foundMoov = false;
            bool foundMoof = false;
            bool foundSidx = false;

            while (_stream.Position < _stream.Length)
            {
                ct.ThrowIfCancellationRequested();

                if (_stream.Length - _stream.Position < 8)
                    break;

                long boxStart = _stream.Position;
                var (size, type, headerSize) = await ReadBoxHeaderAsync(ct);

                if (size == 0)
                    size = _stream.Length - boxStart;

                long boxEnd = boxStart + size;

                switch (type)
                {
                    case "moov":
                        await ParseMoovAsync(boxEnd, ct);
                        foundMoov = true;
                        break;

                    case "sidx":
                        await ParseSidxAsync(boxStart, boxEnd, ct);
                        foundSidx = true;
                        break;

                    case "moof":
                        _isFragmented = true;
                        foundMoof = true;
                        if (_decoderConfig != null)
                            await ParseMoofAsync(boxStart, boxEnd, headerSize, ct);
                        else
                            await SkipToAsync(boxEnd, ct);
                        break;

                    default:
                        await SkipToAsync(boxEnd, ct);
                        break;
                }

                // Для обычного MP4: хватает moov
                if (foundMoov && _samples.Count > 0 && !_isFragmented)
                    break;

                // Для fMP4: нужен moov + (sidx или moof)
                if (foundMoov && _isFragmented && (foundSidx || foundMoof))
                    break;
            }

            if (_isFragmented && !foundSidx && _durationMs <= 0)
                await EstimateDurationFromMoofsAsync(ct);

            if (_isFragmented)
            {
                Log.Info($"[Mp4Parser] Fragmented MP4: rate={_sampleRate}, channels={_channels}, " +
                        $"duration={_durationMs}ms, segments={_segments.Count}");

                // Перемотка к первому moof для начала чтения
                _stream.Position = 0;
                await ScanToFirstMoofAsync(ct);
                return _decoderConfig != null;
            }

            if (_samples.Count == 0)
            {
                Log.Error("[Mp4Parser] No samples found");
                return false;
            }

            Log.Debug($"[Mp4Parser] Regular MP4: {_samples.Count} samples, duration={_durationMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Mp4Parser] Parse failed: {ex.Message}", ex);
            return false;
        }
    }

    #endregion

    #region Frame Reading

    /// <summary>
    /// Читает следующий AAC-фрейм из контейнера.
    /// </summary>
    /// <remarks>
    /// <para><b>Zero-alloc per frame.</b> Буфер арендуется из <see cref="MemoryPool{T}.Shared"/>
    /// и возвращается при следующем вызове (аналогично <see cref="WebMContainerParser"/>).
    /// Устраняет ~43 heap-аллокации/сек при воспроизведении AAC.</para>
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        // Возвращаем ПРЕДЫДУЩИЙ буфер в пул
        ReleaseCurrentFrame();

        return _isFragmented
            ? await ReadNextFragmentedFrameAsync(ct).ConfigureAwait(false)
            : await ReadNextRegularFrameAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<AudioFrame?> ReadNextRegularFrameAsync(CancellationToken ct)
    {
        if (_currentSampleIndex >= _samples.Count)
            return null;

        var sample = _samples[_currentSampleIndex++];
        return await ReadSampleAsFrameAsync(sample, ct);
    }

    private async ValueTask<AudioFrame?> ReadNextFragmentedFrameAsync(CancellationToken ct)
    {
        // Если очередь фреймов пуста — нужно распарсить следующий фрагмент
        while (_fragmentSamples.Count == 0)
        {
            if (_stream.Position >= _stream.Length)
                return null;

            if (!await ScanAndParseNextFragmentAsync(ct))
                return null;
        }

        var sample = _fragmentSamples.Dequeue();

        // Валидация: offset и size должны быть в пределах файла
        if (!ValidateSample(sample))
        {
            Log.Warn($"[Mp4Parser] Skipping invalid sample: offset={sample.Offset}, " +
                    $"size={sample.Size}, streamLen={_stream.Length}");
            // Пропускаем невалидный sample, пробуем следующий
            return _fragmentSamples.Count > 0
                ? await ReadNextFragmentedFrameAsync(ct)
                : null;
        }

        return await ReadSampleAsFrameAsync(sample, ct);
    }

    /// <summary>
    /// Проверяет что sample offset и size находятся в допустимых границах.
    /// </summary>
    private bool ValidateSample(SampleInfo sample)
    {
        if (sample.Offset < 0 || sample.Size <= 0)
            return false;

        if (sample.Offset + sample.Size > _stream.Length)
            return false;

        return true;
    }

    /// <summary>
    /// Читает данные sample из stream и возвращает AudioFrame.
    /// </summary>
    /// <remarks>
    /// <para> Использует <see cref="MemoryPool{T}.Shared"/> вместо <c>new byte[]</c>.
    /// Буфер возвращается в пул при следующем <see cref="ReadNextFrameAsync"/>.</para>
    /// </remarks>
    private async ValueTask<AudioFrame?> ReadSampleAsFrameAsync(SampleInfo sample, CancellationToken ct)
    {
        try
        {
            _stream.Position = sample.Offset;

            var owner = MemoryPool<byte>.Shared.Rent(sample.Size);
            var memory = owner.Memory[..sample.Size];
            int totalRead = 0;

            while (totalRead < sample.Size)
            {
                int read = await _stream.ReadAsync(memory[totalRead..], ct).ConfigureAwait(false);

                if (read == 0)
                {
                    Log.Warn($"[Mp4Parser] Unexpected EOF at offset {sample.Offset + totalRead}, " +
                            $"expected {sample.Size} bytes, got {totalRead}");
                    owner.Dispose();
                    return null;
                }

                totalRead += read;
            }

            // Передаём владение: буфер будет возвращён в пул при следующем ReadNextFrameAsync.
            _currentFrameOwner = owner;

            return new AudioFrame
            {
                Data = memory,
                TimestampMs = sample.TimestampMs,
                DurationMs = sample.DurationMs,
                IsKeyFrame = sample.IsKeyFrame
            };
        }
        catch (EndOfStreamException)
        {
            Log.Warn($"[Mp4Parser] EOF reading sample at offset={sample.Offset}, size={sample.Size}");
            return null;
        }
        catch (IOException ex)
        {
            Log.Warn($"[Mp4Parser] IO error reading sample: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Fragment Scanning

    /// <summary>
    /// Сканирует поток в поисках следующей пары moof+mdat.
    /// </summary>
    private async Task<bool> ScanAndParseNextFragmentAsync(CancellationToken ct)
    {
        long moofStart = -1;
        long moofEnd = -1;

        while (_stream.Position < _stream.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (_stream.Length - _stream.Position < 8)
                return false;

            long boxStart = _stream.Position;
            var (size, type, headerSize) = await ReadBoxHeaderAsync(ct);

            if (size == 0)
                size = _stream.Length - boxStart;

            long boxEnd = boxStart + size;

            // Защита от невалидного размера
            if (boxEnd > _stream.Length)
            {
                Log.Warn($"[Mp4Parser] Box '{type}' extends past stream end: " +
                        $"start={boxStart}, size={size}, streamLen={_stream.Length}");
                return false;
            }

            switch (type)
            {
                case "moof":
                    moofStart = boxStart;
                    moofEnd = boxEnd;
                    await ParseMoofAsync(boxStart, boxEnd, headerSize, ct);
                    break;

                case "mdat":
                    _lastMdatDataStart = _stream.Position; // сразу после header mdat
                    _lastMdatDataEnd = boxEnd;

                    if (moofStart >= 0 && _fragmentSamples.Count > 0)
                    {
                        ValidateAndCorrectSampleOffsets(moofStart, moofEnd);
                        // Позиционируемся после mdat для следующего вызова
                        await SkipToAsync(boxEnd, ct);
                        return _fragmentSamples.Count > 0;
                    }

                    await SkipToAsync(boxEnd, ct);
                    break;

                default:
                    await SkipToAsync(boxEnd, ct);
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Проверяет и корректирует offsets samples относительно текущего mdat.
    /// </summary>
    private void ValidateAndCorrectSampleOffsets(long moofStart, long moofEnd)
    {
        if (_fragmentSamples.Count == 0)
            return;

        var samples = _fragmentSamples.ToArray();
        _fragmentSamples.Clear();

        long firstOffset = samples[0].Offset;

        // Case 1: offsets уже корректны (в пределах mdat)
        if (firstOffset >= _lastMdatDataStart && firstOffset < _lastMdatDataEnd)
        {
            foreach (var s in samples)
            {
                if (s.Offset >= _lastMdatDataStart
                    && s.Offset + s.Size <= _lastMdatDataEnd)
                {
                    _fragmentSamples.Enqueue(s);
                }
                else
                {
                    Log.Warn($"[Mp4Parser] Dropping out-of-bounds sample: " +
                            $"offset={s.Offset}, size={s.Size}, " +
                            $"mdat=[{_lastMdatDataStart}..{_lastMdatDataEnd})");
                }
            }
            return;
        }

        // Case 2: offsets относительны — корректируем
        long correction = _lastMdatDataStart - firstOffset;

        // Дополнительная эвристика: если firstOffset == 0 или маленький,
        // скорее всего data_offset от начала moof
        if (firstOffset >= moofStart && firstOffset < moofEnd)
        {
            // data_offset относительно moof — пересчитываем
            // (ничего не нужно, correction уже правильный)
        }

        foreach (var sample in samples)
        {
            var corrected = sample with { Offset = sample.Offset + correction };

            if (corrected.Offset >= _lastMdatDataStart
                && corrected.Offset + corrected.Size <= _lastMdatDataEnd)
            {
                _fragmentSamples.Enqueue(corrected);
            }
            else
            {
                Log.Warn($"[Mp4Parser] Dropping corrected sample: " +
                        $"original={sample.Offset}, corrected={corrected.Offset}, " +
                        $"size={sample.Size}, mdat=[{_lastMdatDataStart}..{_lastMdatDataEnd})");
            }
        }

        if (_fragmentSamples.Count == 0 && samples.Length > 0)
        {
            Log.Error($"[Mp4Parser] ALL samples dropped after correction! " +
                     $"firstOffset={firstOffset}, correction={correction}, " +
                     $"mdat=[{_lastMdatDataStart}..{_lastMdatDataEnd})");
        }
    }

    #endregion

    #region Seek

    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        if (_isFragmented)
            return FindSeekPositionFragmented(targetMs);

        if (_samples.Count == 0)
            return null;

        int index = BinarySearchSample(targetMs);
        var sample = _samples[index];
        _currentSampleIndex = index;

        return (sample.Offset, sample.TimestampMs);
    }

    /// <summary>
    /// Находит позицию для seek в fMP4 по индексу сегментов (sidx).
    /// </summary>
    private (long BytePosition, long TimestampMs)? FindSeekPositionFragmented(long targetMs)
    {
        if (_segments.Count == 0)
        {
            Log.Warn("[Mp4Parser] No segment index for fMP4 seek");
            return (0, 0);
        }

        long targetTime = targetMs * _trackTimeScale / 1000;
        int segmentIndex = BinarySearchSegment(targetTime);

        if (segmentIndex < 0 || segmentIndex >= _segments.Count)
            return (0, 0);

        var segment = _segments[segmentIndex];
        long timestampMs = segment.TimeOffset * 1000 / _trackTimeScale;

        Log.Debug($"[Mp4Parser] Seek to segment {segmentIndex}: " +
                 $"byteOffset={segment.ByteOffset}, timeMs={timestampMs} (target={targetMs}ms)");

        return (segment.ByteOffset, timestampMs);
    }

    public void Reset()
    {
        _currentSampleIndex = 0;
        _fragmentSamples.Clear();
        ReleaseCurrentFrame();
        _lastMdatDataStart = 0;
        _lastMdatDataEnd = 0;
    }

    #endregion

    #region sidx Parsing

    private async Task ParseSidxAsync(long boxStart, long boxEnd, CancellationToken ct)
    {
        int version = await ReadByteAsync(ct);
        await SkipBytesAsync(3, ct); // flags

        await SkipBytesAsync(4, ct); // reference_ID
        uint timescale = await ReadUInt32BEAsync(ct);

        if (timescale == 0)
            timescale = _trackTimeScale > 0 ? _trackTimeScale : 44100;

        long firstOffset;

        if (version == 0)
        {
            await ReadUInt32BEAsync(ct); // earliest_presentation_time
            firstOffset = await ReadUInt32BEAsync(ct);
        }
        else
        {
            await ReadUInt64BEAsync(ct); // earliest_presentation_time
            firstOffset = (long)await ReadUInt64BEAsync(ct);
        }

        await SkipBytesAsync(2, ct); // reserved
        ushort referenceCount = await ReadUInt16BEAsync(ct);

        _segments.Clear();
        long totalDuration = 0;
        // sidx указывает смещения от конца sidx бокса + firstOffset
        long currentByteOffset = boxEnd + firstOffset;

        for (int i = 0; i < referenceCount; i++)
        {
            uint referenceInfo = await ReadUInt32BEAsync(ct);
            uint referencedSize = referenceInfo & 0x7FFFFFFF;

            uint subsegmentDuration = await ReadUInt32BEAsync(ct);
            await SkipBytesAsync(4, ct); // SAP info

            _segments.Add(new SegmentInfo
            {
                ByteOffset = currentByteOffset,
                TimeOffset = totalDuration,
                Duration = subsegmentDuration,
                Size = referencedSize
            });

            currentByteOffset += referencedSize;
            totalDuration += subsegmentDuration;
        }

        long durationMs = totalDuration * 1000 / timescale;

        Log.Debug($"[Mp4Parser] sidx: {referenceCount} segments, timescale={timescale}, duration={durationMs}ms");

        if (durationMs > _durationMs)
            _durationMs = durationMs;

        if (_trackTimeScale == 1)
            _trackTimeScale = timescale;

        await SkipToAsync(boxEnd, ct);
    }

    #endregion

    #region moof/traf/trun Parsing

    private async Task ParseMoofAsync(long moofStart, long moofEnd, int moofHeaderSize, CancellationToken ct)
    {
        _fragmentSamples.Clear();

        while (_stream.Position < moofEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "traf")
                await ParseTrafAsync(childEnd, moofStart, moofEnd, ct);
            else
                await SkipToAsync(childEnd, ct);
        }
    }

    private async Task ParseTrafAsync(long boxEnd, long moofStart, long moofEnd, CancellationToken ct)
    {
        uint sampleDuration = _defaultSampleDuration;
        uint sampleSize = _defaultSampleSize;
        long baseDataOffset = moofStart;
        long baseMediaDecodeTime = 0;

        long savedPosition = _stream.Position;

        // First pass: find tfhd
        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "tfhd")
            {
                var tfhd = await ParseTfhdAsync(ct);

                if (tfhd.HasBaseDataOffset)
                    baseDataOffset = tfhd.BaseDataOffset;
                else if (tfhd.DefaultBaseIsMoof)
                    baseDataOffset = moofStart;

                if (tfhd.HasDefaultSampleDuration)
                    sampleDuration = tfhd.DefaultSampleDuration;
                if (tfhd.HasDefaultSampleSize)
                    sampleSize = tfhd.DefaultSampleSize;
                break;
            }

            await SkipToAsync(childEnd, ct);
        }

        // Second pass: parse tfdt + trun
        _stream.Position = savedPosition;

        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            switch (type)
            {
                case "tfdt":
                    baseMediaDecodeTime = await ParseTfdtAsync(ct);
                    break;

                case "trun":
                    await ParseTrunAsync(baseDataOffset, moofEnd, baseMediaDecodeTime,
                                        sampleDuration, sampleSize, ct);
                    break;
            }

            await SkipToAsync(childEnd, ct);
        }
    }

    private async Task ParseTrunAsync(
        long baseDataOffset,
        long moofEnd,
        long baseMediaDecodeTime,
        uint defaultDuration,
        uint defaultSize,
        CancellationToken ct)
    {
        uint versionFlags = await ReadUInt32BEAsync(ct);
        uint version = versionFlags >> 24;
        uint flags = versionFlags & 0xFFFFFF;

        uint sampleCount = await ReadUInt32BEAsync(ct);
        var flagsInfo = new TrunFlags(flags);

        int dataOffset = 0;
        if (flagsInfo.HasDataOffset)
            dataOffset = await ReadInt32BEAsync(ct);

        if (flagsInfo.HasFirstSampleFlags)
            await SkipBytesAsync(4, ct);

        long currentOffset = baseDataOffset + dataOffset;
        long currentTime = baseMediaDecodeTime;

        for (uint i = 0; i < sampleCount; i++)
        {
            uint duration = defaultDuration;
            uint size = defaultSize;
            uint sampleFlags = 0;
            int compositionTimeOffset = 0;

            if (flagsInfo.HasSampleDuration) duration = await ReadUInt32BEAsync(ct);
            if (flagsInfo.HasSampleSize) size = await ReadUInt32BEAsync(ct);
            if (flagsInfo.HasSampleFlags) sampleFlags = await ReadUInt32BEAsync(ct);

            if (flagsInfo.HasSampleCompositionTimeOffset)
            {
                compositionTimeOffset = version == 0
                    ? (int)await ReadUInt32BEAsync(ct)
                    : await ReadInt32BEAsync(ct);
            }

            long timestampMs = _trackTimeScale > 0
                ? (currentTime + compositionTimeOffset) * 1000 / _trackTimeScale
                : 0;

            int durationMs = _trackTimeScale > 0
                ? (int)(duration * 1000 / _trackTimeScale)
                : 23;

            bool isKeyFrame = (sampleFlags & 0x00010000) == 0;

            _fragmentSamples.Enqueue(new SampleInfo
            {
                Offset = currentOffset,
                Size = (int)size,
                TimestampMs = timestampMs,
                DurationMs = durationMs,
                IsKeyFrame = isKeyFrame
            });

            currentOffset += size;
            currentTime += duration;
        }
    }

    #endregion

    #region moov Parsing

    private async Task ParseMoovAsync(long boxEnd, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();

            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            switch (type)
            {
                case "trak":
                    await ParseTrakAsync(childEnd, ct);
                    break;
                case "mvhd":
                    await ParseMvhdAsync(childEnd, ct);
                    break;
                case "mvex":
                    _isFragmented = true;
                    await ParseMvexAsync(childEnd, ct);
                    break;
                default:
                    await SkipToAsync(childEnd, ct);
                    break;
            }
        }
    }

    private async Task ParseMvhdAsync(long boxEnd, CancellationToken ct)
    {
        int version = await ReadByteAsync(ct);
        await SkipBytesAsync(3, ct);

        uint timescale;
        long duration;

        if (version == 1)
        {
            await SkipBytesAsync(16, ct);
            timescale = await ReadUInt32BEAsync(ct);
            duration = (long)await ReadUInt64BEAsync(ct);
        }
        else
        {
            await SkipBytesAsync(8, ct);
            timescale = await ReadUInt32BEAsync(ct);
            duration = await ReadUInt32BEAsync(ct);
        }

        if (_trackTimeScale == 1)
            _trackTimeScale = timescale;

        if (duration > 0 && timescale > 0)
        {
            _durationMs = duration * 1000 / timescale;
            Log.Debug($"[Mp4Parser] mvhd: timescale={timescale}, duration={_durationMs}ms");
        }

        await SkipToAsync(boxEnd, ct);
    }

    private async Task ParseMvexAsync(long boxEnd, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "trex")
                await ParseTrexAsync(ct);

            await SkipToAsync(childEnd, ct);
        }
    }

    private async Task ParseTrexAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct); // version + flags
        await SkipBytesAsync(4, ct); // track_ID
        await SkipBytesAsync(4, ct); // default_sample_description_index
        _defaultSampleDuration = await ReadUInt32BEAsync(ct);
        _defaultSampleSize = await ReadUInt32BEAsync(ct);
    }

    private async Task ParseTrakAsync(long boxEnd, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();

            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "mdia")
                await ParseMdiaAsync(childEnd, ct);
            else
                await SkipToAsync(childEnd, ct);
        }
    }

    private async Task<(bool IsAudio, uint TimeScale)> ParseMdiaAsync(long boxEnd, CancellationToken ct)
    {
        bool isAudioTrack = false;
        uint trackTimeScale = 1;

        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            switch (type)
            {
                case "hdlr":
                    isAudioTrack = await ParseHdlrAsync(ct);
                    break;
                case "mdhd":
                    trackTimeScale = await ParseMdhdAsync(ct, isAudioTrack);
                    break;
                case "minf" when isAudioTrack:
                    await ParseMinfAsync(childEnd, trackTimeScale, ct);
                    break;
            }

            await SkipToAsync(childEnd, ct);
        }

        return (isAudioTrack, trackTimeScale);
    }

    private async Task<bool> ParseHdlrAsync(CancellationToken ct)
    {
        await SkipBytesAsync(8, ct);
        var buf = new byte[4];
        await ReadExactlyAsync(buf, ct);
        return buf[0] == 's' && buf[1] == 'o' && buf[2] == 'u' && buf[3] == 'n';
    }

    private async Task<uint> ParseMdhdAsync(CancellationToken ct, bool isAudioTrack)
    {
        int version = await ReadByteAsync(ct);
        await SkipBytesAsync(3, ct);

        uint trackTimeScale;

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

        if (isAudioTrack || _trackTimeScale == 1)
            _trackTimeScale = trackTimeScale;

        return trackTimeScale;
    }

    private async Task ParseMinfAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "stbl")
                await ParseStblAsync(childEnd, timeScale, ct);
            else
                await SkipToAsync(childEnd, ct);
        }
    }

    #endregion

    #region stbl Parsing (Regular MP4)

    private async Task ParseStblAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        List<uint> sampleSizes = [];
        List<(uint firstChunk, uint samplesPerChunk, uint descriptionIndex)> stsc = [];
        List<long> chunkOffsets = [];
        List<(uint count, uint delta)> stts = [];

        while (_stream.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();

            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            switch (type)
            {
                case "stsd": await ParseStsdAsync(childEnd, ct); break;
                case "stsz": sampleSizes = await ParseStszAsync(ct); break;
                case "stsc": stsc = await ParseStscAsync(ct); break;
                case "stco": chunkOffsets = await ParseStcoAsync(ct); break;
                case "co64": chunkOffsets = await ParseCo64Async(ct); break;
                case "stts": stts = await ParseSttsAsync(ct); break;
            }

            await SkipToAsync(childEnd, ct);
        }

        BuildSampleTable(sampleSizes, stsc, chunkOffsets, stts, timeScale);
    }

    private async Task<List<uint>> ParseStszAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint defaultSize = await ReadUInt32BEAsync(ct);
        uint count = await ReadUInt32BEAsync(ct);

        var sizes = new List<uint>((int)count);
        for (uint i = 0; i < count; i++)
            sizes.Add(defaultSize == 0 ? await ReadUInt32BEAsync(ct) : defaultSize);
        return sizes;
    }

    private async Task<List<(uint, uint, uint)>> ParseStscAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint entryCount = await ReadUInt32BEAsync(ct);

        var result = new List<(uint, uint, uint)>((int)entryCount);
        for (uint i = 0; i < entryCount; i++)
        {
            uint firstChunk = await ReadUInt32BEAsync(ct);
            uint samplesPerChunk = await ReadUInt32BEAsync(ct);
            uint descIndex = await ReadUInt32BEAsync(ct);
            result.Add((firstChunk, samplesPerChunk, descIndex));
        }
        return result;
    }

    private async Task<List<long>> ParseStcoAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint count = await ReadUInt32BEAsync(ct);

        var offsets = new List<long>((int)count);
        for (uint i = 0; i < count; i++)
            offsets.Add(await ReadUInt32BEAsync(ct));
        return offsets;
    }

    private async Task<List<long>> ParseCo64Async(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint count = await ReadUInt32BEAsync(ct);

        var offsets = new List<long>((int)count);
        for (uint i = 0; i < count; i++)
            offsets.Add((long)await ReadUInt64BEAsync(ct));
        return offsets;
    }

    private async Task<List<(uint, uint)>> ParseSttsAsync(CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint count = await ReadUInt32BEAsync(ct);

        var result = new List<(uint, uint)>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint sampleCount = await ReadUInt32BEAsync(ct);
            uint sampleDelta = await ReadUInt32BEAsync(ct);
            result.Add((sampleCount, sampleDelta));
        }
        return result;
    }

    private async Task ParseStsdAsync(long boxEnd, CancellationToken ct)
    {
        await SkipBytesAsync(4, ct);
        uint entryCount = await ReadUInt32BEAsync(ct);
        if (entryCount == 0) return;

        long entryStart = _stream.Position;
        var (size, _, _) = await ReadBoxHeaderAsync(ct);
        if (size == 0) return;

        long entryEnd = entryStart + size;

        // Skip reserved + data_reference_index
        await SkipBytesAsync(6 + 2 + 8, ct);

        _channels = await ReadUInt16BEAsync(ct);
        await SkipBytesAsync(2 + 4, ct); // sampleSize + reserved
        _sampleRate = (int)(await ReadUInt32BEAsync(ct) >> 16);

        // Parse nested boxes (esds)
        while (_stream.Position < entryEnd)
        {
            long nestedStart = _stream.Position;
            var (boxSize, boxType, _) = await ReadBoxHeaderAsync(ct);
            if (boxSize == 0) break;

            long nestedEnd = nestedStart + boxSize;

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
        await SkipBytesAsync(4, ct); // version + flags

        var tagBuf = new byte[1];

        // ES_Descriptor tag (0x03)
        await ReadExactlyAsync(tagBuf, ct);
        if (tagBuf[0] != 0x03) return;

        await ReadExpandableLengthAsync(ct);
        await SkipBytesAsync(2, ct); // ES_ID

        await ReadExactlyAsync(tagBuf, ct);
        int flags = tagBuf[0];

        if ((flags & 0x80) != 0) await SkipBytesAsync(2, ct);
        if ((flags & 0x40) != 0)
        {
            await ReadExactlyAsync(tagBuf, ct);
            await SkipBytesAsync(tagBuf[0], ct);
        }
        if ((flags & 0x20) != 0) await SkipBytesAsync(2, ct);

        // DecoderConfigDescriptor tag (0x04)
        await ReadExactlyAsync(tagBuf, ct);
        if (tagBuf[0] != 0x04) return;

        await ReadExpandableLengthAsync(ct);
        await SkipBytesAsync(13, ct);

        // DecoderSpecificInfo tag (0x05)
        await ReadExactlyAsync(tagBuf, ct);
        if (tagBuf[0] != 0x05) return;

        int dsiLength = await ReadExpandableLengthAsync(ct);
        if (dsiLength <= 0 || dsiLength > 64) return;

        var fullDsi = new byte[dsiLength];
        await ReadExactlyAsync(fullDsi, ct);

        // Trim trailing zeros
        int realLength = dsiLength;
        while (realLength > 2 && fullDsi[realLength - 1] == 0)
            realLength--;
        realLength = Math.Max(2, realLength);

        _decoderConfig = realLength < dsiLength ? fullDsi[..realLength] : fullDsi;

        if (realLength < dsiLength)
            Log.Debug($"[Mp4Parser] Trimmed ASC: {dsiLength} → {realLength} bytes (removed padding)");

        ParseAudioSpecificConfig(_decoderConfig);
        Log.Debug($"[Mp4Parser] Decoder config ({_decoderConfig.Length} bytes): " +
                 $"{BitConverter.ToString(_decoderConfig)}");
    }

    private void ParseAudioSpecificConfig(byte[] asc)
    {
        if (asc.Length < 2) return;

        int audioObjectType = (asc[0] >> 3) & 0x1F;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 0x01);
        int channelConfig = (asc[1] >> 3) & 0x0F;

        int[] sampleRates =
        [
            96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
            16000, 12000, 11025, 8000, 7350, 0, 0, 0
        ];

        int baseSampleRate = samplingFrequencyIndex < sampleRates.Length
            ? sampleRates[samplingFrequencyIndex] : 0;
        int outputSampleRate = baseSampleRate;

        bool isHeAac = audioObjectType == 5 || audioObjectType == 29;
        if (isHeAac && baseSampleRate > 0)
            outputSampleRate = baseSampleRate * 2;

        Log.Debug($"[Mp4Parser] ASC parsed: objectType={audioObjectType}, " +
                 $"freqIndex={samplingFrequencyIndex}, baseRate={baseSampleRate}, " +
                 $"outputRate={outputSampleRate}, channels={channelConfig}, isHE-AAC={isHeAac}");

        if (outputSampleRate > 0) _sampleRate = outputSampleRate;

        if (channelConfig > 0 && channelConfig <= 7)
        {
            int[] channelCounts = [0, 1, 2, 3, 4, 5, 6, 8];
            _channels = channelCounts[channelConfig];
        }
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
        var timestamps = BuildTimestamps(stts);

        int sampleIndex = 0;
        int stscIndex = 0;

        for (int chunkIndex = 0; chunkIndex < chunkOffsets.Count && sampleIndex < sampleSizes.Count; chunkIndex++)
        {
            while (stscIndex + 1 < stsc.Count && stsc[stscIndex + 1].firstChunk - 1 <= chunkIndex)
                stscIndex++;

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
                    DurationMs = durationMs,
                    IsKeyFrame = true
                });

                offset += sampleSizes[sampleIndex];
                sampleIndex++;
            }
        }

        if (_samples.Count > 0)
            _durationMs = _samples[^1].TimestampMs + _samples[^1].DurationMs;
    }

    private static List<long> BuildTimestamps(List<(uint count, uint delta)> stts)
    {
        int totalSamples = stts.Sum(x => (int)x.count);
        var timestamps = new List<long>(totalSamples);
        long time = 0;

        foreach (var (count, delta) in stts)
        {
            for (uint i = 0; i < count; i++)
            {
                timestamps.Add(time);
                time += delta;
            }
        }

        return timestamps;
    }

    #endregion

    #region Duration Estimation (fallback)

    private async Task EstimateDurationFromMoofsAsync(CancellationToken ct)
    {
        long savedPosition = _stream.Position;
        _stream.Position = 0;

        long lastTimestamp = 0;
        long lastDuration = 0;

        try
        {
            while (_stream.Position < _stream.Length)
            {
                if (_stream.Length - _stream.Position < 8) break;

                long boxStart = _stream.Position;
                var (size, type, _) = await ReadBoxHeaderAsync(ct);
                if (size == 0) break;

                long boxEnd = boxStart + size;

                if (type == "moof")
                {
                    var (timestamp, duration) = await QuickParseMoofForTimingAsync(boxEnd, ct);
                    if (timestamp >= 0)
                    {
                        _segments.Add(new SegmentInfo
                        {
                            ByteOffset = boxStart,
                            TimeOffset = timestamp,
                            Duration = duration,
                            Size = (uint)(boxEnd - boxStart)
                        });

                        lastTimestamp = timestamp;
                        lastDuration = duration;
                    }
                }

                await SkipToAsync(boxEnd, ct);
            }

            if (lastTimestamp > 0 && _trackTimeScale > 0)
            {
                long estimatedMs = (lastTimestamp + lastDuration) * 1000 / _trackTimeScale;
                if (estimatedMs > _durationMs)
                {
                    _durationMs = estimatedMs;
                    Log.Debug($"[Mp4Parser] Estimated duration: {_durationMs}ms, " +
                             $"{_segments.Count} segments");
                }
            }
        }
        finally
        {
            _stream.Position = savedPosition;
        }
    }

    private async Task<(long timestamp, long duration)> QuickParseMoofForTimingAsync(
        long boxEnd, CancellationToken ct)
    {
        long timestamp = -1;
        long totalDuration = 0;

        while (_stream.Position < boxEnd)
        {
            long childStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);
            if (size == 0) break;

            long childEnd = childStart + size;

            if (type == "traf")
            {
                while (_stream.Position < childEnd)
                {
                    long trafChildStart = _stream.Position;
                    var (trafChildSize, trafChildType, _) = await ReadBoxHeaderAsync(ct);
                    if (trafChildSize == 0) break;

                    long trafChildEnd = trafChildStart + trafChildSize;

                    if (trafChildType == "tfdt")
                        timestamp = await ParseTfdtAsync(ct);
                    else if (trafChildType == "trun")
                        totalDuration = await QuickParseTrunDurationAsync(ct);

                    await SkipToAsync(trafChildEnd, ct);
                }
            }

            await SkipToAsync(childEnd, ct);
        }

        return (timestamp, totalDuration);
    }

    private async Task<long> QuickParseTrunDurationAsync(CancellationToken ct)
    {
        uint versionFlags = await ReadUInt32BEAsync(ct);
        uint flags = versionFlags & 0xFFFFFF;

        uint sampleCount = await ReadUInt32BEAsync(ct);

        bool hasDataOffset = (flags & 0x000001) != 0;
        bool hasFirstSampleFlags = (flags & 0x000004) != 0;
        bool hasSampleDuration = (flags & 0x000100) != 0;
        bool hasSampleSize = (flags & 0x000200) != 0;
        bool hasSampleFlags = (flags & 0x000400) != 0;
        bool hasSampleCTO = (flags & 0x000800) != 0;

        if (hasDataOffset) await SkipBytesAsync(4, ct);
        if (hasFirstSampleFlags) await SkipBytesAsync(4, ct);

        long totalDuration = 0;

        for (uint i = 0; i < sampleCount; i++)
        {
            uint duration = _defaultSampleDuration;
            if (hasSampleDuration) duration = await ReadUInt32BEAsync(ct);
            if (hasSampleSize) await SkipBytesAsync(4, ct);
            if (hasSampleFlags) await SkipBytesAsync(4, ct);
            if (hasSampleCTO) await SkipBytesAsync(4, ct);
            totalDuration += duration;
        }

        return totalDuration;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Возвращает буфер предыдущего фрейма в пул.
    /// Декодер уже обработал данные в предыдущей итерации DecoderLoop.
    /// </summary>
    private void ReleaseCurrentFrame()
    {
        _currentFrameOwner?.Dispose();
        _currentFrameOwner = null;
    }

    private async Task ScanToFirstMoofAsync(CancellationToken ct)
    {
        while (_stream.Position < _stream.Length)
        {
            if (_stream.Length - _stream.Position < 8) break;

            long boxStart = _stream.Position;
            var (size, type, _) = await ReadBoxHeaderAsync(ct);

            if (type == "moof")
            {
                _stream.Position = boxStart;
                return;
            }

            if (size == 0) break;
            await SkipToAsync(boxStart + size, ct);
        }
    }

    private readonly record struct TfhdResult(
        bool HasBaseDataOffset, long BaseDataOffset,
        bool DefaultBaseIsMoof,
        bool HasDefaultSampleDuration, uint DefaultSampleDuration,
        bool HasDefaultSampleSize, uint DefaultSampleSize);

    private async Task<TfhdResult> ParseTfhdAsync(CancellationToken ct)
    {
        uint versionFlags = await ReadUInt32BEAsync(ct);
        uint flags = versionFlags & 0xFFFFFF;

        await SkipBytesAsync(4, ct); // track_ID

        long baseDataOffset = 0;
        uint defaultSampleDuration = 0;
        uint defaultSampleSize = 0;

        bool hasBaseDataOffset = (flags & 0x000001) != 0;
        bool hasSampleDescriptionIndex = (flags & 0x000002) != 0;
        bool hasDefaultSampleDuration = (flags & 0x000008) != 0;
        bool hasDefaultSampleSize = (flags & 0x000010) != 0;
        bool hasDefaultSampleFlags = (flags & 0x000020) != 0;
        bool defaultBaseIsMoof = (flags & 0x020000) != 0;

        if (hasBaseDataOffset) baseDataOffset = (long)await ReadUInt64BEAsync(ct);
        if (hasSampleDescriptionIndex) await SkipBytesAsync(4, ct);
        if (hasDefaultSampleDuration) defaultSampleDuration = await ReadUInt32BEAsync(ct);
        if (hasDefaultSampleSize) defaultSampleSize = await ReadUInt32BEAsync(ct);
        if (hasDefaultSampleFlags) await SkipBytesAsync(4, ct);

        return new TfhdResult(
            hasBaseDataOffset, baseDataOffset,
            defaultBaseIsMoof,
            hasDefaultSampleDuration, defaultSampleDuration,
            hasDefaultSampleSize, defaultSampleSize);
    }

    private async Task<long> ParseTfdtAsync(CancellationToken ct)
    {
        int version = await ReadByteAsync(ct);
        await SkipBytesAsync(3, ct);

        return version == 1
            ? (long)await ReadUInt64BEAsync(ct)
            : await ReadUInt32BEAsync(ct);
    }

    private int BinarySearchSegment(long targetTime)
    {
        int left = 0;
        int right = _segments.Count - 1;
        int result = 0;

        while (left <= right)
        {
            int mid = (left + right) / 2;

            if (_segments[mid].TimeOffset <= targetTime)
            {
                result = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return result;
    }

    private int BinarySearchSample(long targetMs)
    {
        int left = 0, right = _samples.Count - 1;

        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            if (_samples[mid].TimestampMs <= targetMs)
                left = mid;
            else
                right = mid - 1;
        }

        return left;
    }

    private async ValueTask<int> ReadExpandableLengthAsync(CancellationToken ct)
    {
        int length = 0;
        var buf = new byte[1];

        for (int i = 0; i < 4; i++)
        {
            await ReadExactlyAsync(buf, ct);
            int b = buf[0];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }

        return length;
    }

    #endregion

    #region Binary Reading Helpers

    private async ValueTask<(long size, string type, int headerSize)> ReadBoxHeaderAsync(
        CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            int read = await _stream.ReadAsync(header.AsMemory(0, 8), ct);
            if (read < 8)
                return (0, "", 0);

            uint size = ReadUInt32BE(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

            if (size == 1)
            {
                var extSize = ArrayPool<byte>.Shared.Rent(8);
                try
                {
                    await ReadExactlyAsync(extSize.AsMemory(0, 8), ct);
                    return ((long)ReadUInt64BE(extSize), type, 16);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(extSize);
                }
            }

            return (size, type, 8);
        }
        catch (EndOfStreamException)
        {
            return (0, "", 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// Читает ровно buffer.Length байт. Бросает EndOfStreamException если невозможно.
    /// </summary>
    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
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

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            return read == 0 ? -1 : buffer[0];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> b) =>
        (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    private static ulong ReadUInt64BE(ReadOnlySpan<byte> b) =>
        ((ulong)b[0] << 56) | ((ulong)b[1] << 48) | ((ulong)b[2] << 40) | ((ulong)b[3] << 32) |
        ((ulong)b[4] << 24) | ((ulong)b[5] << 16) | ((ulong)b[6] << 8) | b[7];

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

    private async ValueTask<int> ReadInt32BEAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            await ReadExactlyAsync(buffer.AsMemory(0, 4), ct);
            return unchecked((int)ReadUInt32BE(buffer));
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
            return;
        }

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

    private async ValueTask SkipToAsync(long position, CancellationToken ct)
    {
        if (_stream.Position < position)
            await SkipBytesAsync(position - _stream.Position, ct);
    }

    #endregion

    #region Types

    private readonly record struct TrunFlags(uint Flags)
    {
        public bool HasDataOffset => (Flags & 0x000001) != 0;
        public bool HasFirstSampleFlags => (Flags & 0x000004) != 0;
        public bool HasSampleDuration => (Flags & 0x000100) != 0;
        public bool HasSampleSize => (Flags & 0x000200) != 0;
        public bool HasSampleFlags => (Flags & 0x000400) != 0;
        public bool HasSampleCompositionTimeOffset => (Flags & 0x000800) != 0;
    }

    private record struct SegmentInfo
    {
        public long ByteOffset;
        public long TimeOffset;
        public long Duration;
        public uint Size;
    }

    private record struct SampleInfo
    {
        public long Offset;
        public int Size;
        public long TimestampMs;
        public int DurationMs;
        public bool IsKeyFrame;
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCurrentFrame();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion
}