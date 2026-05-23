namespace LMP.Core.Audio.Parsers;

public sealed partial class Mp4ContainerParser
{
    /// <summary>
    /// Разбирает <c>moov</c> box и его дочерние элементы.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseMoovAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            ct.ThrowIfCancellationRequested();

            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_TRAK:
                    await ParseTrakAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_MVHD:
                    await ParseMvhdAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_MVEX:
                    _isFragmented = true;
                    await ParseMvexAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                default:
                    await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Разбирает <c>mvhd</c> box и извлекает длительность контейнера.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseMvhdAsync(long boxEnd, CancellationToken ct)
    {
        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false);

        uint timescale;
        long duration;

        if (version == 1)
        {
            await _reader.SkipBytesAsync(16, ct).ConfigureAwait(false);
            timescale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            duration = (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await _reader.SkipBytesAsync(8, ct).ConfigureAwait(false);
            timescale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            duration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        }

        if (_trackTimeScale == 1 && timescale > 0)
            _trackTimeScale = timescale;

        if (duration > 0 && timescale > 0)
            _durationMs = duration * 1000 / timescale;

        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разбирает <c>mvex</c> box и извлекает default sample параметры для fMP4.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseMvexAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_TREX)
                await ParseTrexAsync(ct).ConfigureAwait(false);

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Разбирает <c>trex</c> box и извлекает значения по умолчанию для sample duration/size.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseTrexAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // track_ID
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // default_sample_description_index
        _defaultSampleDuration = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        _defaultSampleSize = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разбирает <c>trak</c> box.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseTrakAsync(long boxEnd, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_MDIA)
            {
                var (isAudio, timeScale) = await ParseMdiaAsync(header.Size - header.HeaderSize, ct).ConfigureAwait(false);
                if (isAudio && _trackTimeScale == 1)
                    _trackTimeScale = timeScale;
            }
            else
            {
                await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Разбирает <c>mdia</c> box, определяя audio track и его time scale.
    /// </summary>
    /// <param name="boxSize">Размер содержимого box без заголовка.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Кортеж: является ли track аудио-дорожкой и её time scale.
    /// </returns>
    private async Task<(bool IsAudio, uint TimeScale)> ParseMdiaAsync(long boxSize, CancellationToken ct)
    {
        long boxEnd = _reader.Position + boxSize;
        bool isAudio = false;
        uint timeScale = 1;

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_HDLR:
                    await _reader.SkipBytesAsync(8, ct).ConfigureAwait(false);
                    uint handlerType = await _reader.ReadFourCCAsync(ct).ConfigureAwait(false);
                    isAudio = handlerType == Mp4BinaryReader.FCC_SOUN;
                    break;

                case Mp4BinaryReader.FCC_MDHD:
                    {
                        int version = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
                        await _reader.SkipBytesAsync(3, ct).ConfigureAwait(false);
                        await _reader.SkipBytesAsync(version == 1 ? 16 : 8, ct).ConfigureAwait(false);
                        timeScale = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
                        if (isAudio || _trackTimeScale == 1)
                            _trackTimeScale = timeScale;
                        break;
                    }

                case Mp4BinaryReader.FCC_MINF when isAudio:
                    await ParseMinfAsync(childEnd, timeScale, ct).ConfigureAwait(false);
                    break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        return (isAudio, timeScale);
    }

    /// <summary>
    /// Разбирает <c>minf</c> box и делегирует разбор sample table.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="timeScale">Time scale текущего трека.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseMinfAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            if (header.Type == Mp4BinaryReader.FCC_STBL)
                await ParseStblAsync(childEnd, timeScale, ct).ConfigureAwait(false);
            else
                await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Разбирает <c>stbl</c> box и собирает данные для построения sample table.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="timeScale">Time scale текущего трека.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseStblAsync(long boxEnd, uint timeScale, CancellationToken ct)
    {
        List<uint> sampleSizes = [];
        List<(uint firstChunk, uint samplesPerChunk, uint descIdx)> stsc = [];
        List<long> chunkOffsets = [];
        List<(uint count, uint delta)> stts = [];

        while (_reader.Position < boxEnd)
        {
            long childStart = _reader.Position;
            var header = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (header.IsEmpty)
                break;

            long childEnd = childStart + header.Size;

            switch (header.Type)
            {
                case Mp4BinaryReader.FCC_STSD:
                    await ParseStsdAsync(childEnd, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STSZ:
                    sampleSizes = await ParseStszAsync(ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STSC:
                    stsc = await ParseStscAsync(ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STCO:
                    chunkOffsets = await ParseStcoAsync(false, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_CO64:
                    chunkOffsets = await ParseStcoAsync(true, ct).ConfigureAwait(false);
                    break;

                case Mp4BinaryReader.FCC_STTS:
                    stts = await ParseSttsAsync(ct).ConfigureAwait(false);
                    break;
            }

            await _reader.SkipToAsync(childEnd, ct).ConfigureAwait(false);
        }

        BuildSampleTable(sampleSizes, stsc, chunkOffsets, stts, timeScale);
    }

    /// <summary>
    /// Разбирает <c>stsd</c> box и извлекает параметры AAC sample entry.
    /// </summary>
    /// <param name="boxEnd">Абсолютная позиция конца box.</param>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseStsdAsync(long boxEnd, CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint entryCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        if (entryCount == 0)
        {
            await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
            return;
        }

        long entryStart = _reader.Position;
        var entryHeader = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
        if (entryHeader.IsEmpty)
        {
            await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
            return;
        }

        long entryEnd = entryStart + entryHeader.Size;

        await _reader.SkipBytesAsync(6 + 2 + 8, ct).ConfigureAwait(false);
        _channels = await _reader.ReadUInt16BEAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(2 + 4, ct).ConfigureAwait(false);
        _sampleRate = (int)(await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false) >> 16);

        while (_reader.Position < entryEnd)
        {
            long nestedStart = _reader.Position;
            var nestedHeader = await _reader.ReadBoxHeaderAsync(ct).ConfigureAwait(false);
            if (nestedHeader.IsEmpty)
                break;

            long nestedEnd = nestedStart + nestedHeader.Size;

            if (nestedHeader.Type == Mp4BinaryReader.FCC_ESDS)
            {
                await ParseEsdsAsync(ct).ConfigureAwait(false);
                break;
            }

            await _reader.SkipToAsync(nestedEnd, ct).ConfigureAwait(false);
        }

        await _reader.SkipToAsync(boxEnd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разбирает <c>esds</c> descriptor и извлекает AAC Audio Specific Config.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    private async Task ParseEsdsAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags

        int tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x03)
            return;

        await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false); // ES_ID

        int flags = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if ((flags & 0x80) != 0)
            await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false);

        if ((flags & 0x40) != 0)
        {
            int extLen = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
            await _reader.SkipBytesAsync(extLen, ct).ConfigureAwait(false);
        }

        if ((flags & 0x20) != 0)
            await _reader.SkipBytesAsync(2, ct).ConfigureAwait(false);

        tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x04)
            return;

        await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        await _reader.SkipBytesAsync(13, ct).ConfigureAwait(false);

        tag = await _reader.ReadByteAsync(ct).ConfigureAwait(false);
        if (tag != 0x05)
            return;

        int dsiLength = await _reader.ReadExpandableLengthAsync(ct).ConfigureAwait(false);
        if (dsiLength <= 0 || dsiLength > 64)
            return;

        byte[] fullDsi = await _reader.ReadBytesAsync(dsiLength, ct).ConfigureAwait(false);

        int realLength = dsiLength;
        while (realLength > 2 && fullDsi[realLength - 1] == 0)
            realLength--;

        realLength = Math.Max(2, realLength);
        _decoderConfig = realLength < dsiLength ? fullDsi[..realLength] : fullDsi;

        ParseAudioSpecificConfig(_decoderConfig);
    }

    /// <summary>
    /// Разбирает AAC Audio Specific Config и обновляет sample rate / channels.
    /// </summary>
    /// <param name="asc">Сырые байты Audio Specific Config.</param>
    private void ParseAudioSpecificConfig(byte[] asc)
    {
        if (asc.Length < 2)
            return;

        int audioObjectType = (asc[0] >> 3) & 0x1F;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 0x01);
        int channelConfig = (asc[1] >> 3) & 0x0F;

        int baseSampleRate = AudioConstants.GetAacSampleRate(samplingFrequencyIndex);
        bool isHeAac = audioObjectType is 5 or 29;

        if (baseSampleRate > 0)
            _sampleRate = isHeAac ? baseSampleRate * 2 : baseSampleRate;

        _channels = AudioConstants.GetAacChannels(channelConfig);
    }

    /// <summary>
    /// Разбирает <c>stsz</c> box.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Список размеров семплов.</returns>
    private async Task<List<uint>> ParseStszAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint defaultSize = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<uint>((int)count);

        if (defaultSize != 0)
        {
            for (uint i = 0; i < count; i++)
                result.Add(defaultSize);

            return result;
        }

        for (uint i = 0; i < count; i++)
            result.Add(await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false));

        return result;
    }

    /// <summary>
    /// Разбирает <c>stsc</c> box.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Список записей sample-to-chunk.</returns>
    private async Task<List<(uint firstChunk, uint samplesPerChunk, uint descIdx)>> ParseStscAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint entryCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<(uint firstChunk, uint samplesPerChunk, uint descIdx)>((int)entryCount);
        for (uint i = 0; i < entryCount; i++)
        {
            uint firstChunk = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint samplesPerChunk = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint descIdx = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            result.Add((firstChunk, samplesPerChunk, descIdx));
        }

        return result;
    }

    /// <summary>
    /// Разбирает <c>stco</c> или <c>co64</c> box.
    /// </summary>
    /// <param name="use64BitOffsets">
    /// <c>true</c> для <c>co64</c>, <c>false</c> для <c>stco</c>.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Список абсолютных смещений чанков.</returns>
    private async Task<List<long>> ParseStcoAsync(bool use64BitOffsets, CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<long>((int)count);
        for (uint i = 0; i < count; i++)
        {
            long value = use64BitOffsets
                ? (long)await _reader.ReadUInt64BEAsync(ct).ConfigureAwait(false)
                : await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Разбирает <c>stts</c> box.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Список пар (count, delta) для time-to-sample.</returns>
    private async Task<List<(uint count, uint delta)>> ParseSttsAsync(CancellationToken ct)
    {
        await _reader.SkipBytesAsync(4, ct).ConfigureAwait(false); // version + flags
        uint count = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);

        var result = new List<(uint count, uint delta)>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint sampleCount = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            uint sampleDelta = await _reader.ReadUInt32BEAsync(ct).ConfigureAwait(false);
            result.Add((sampleCount, sampleDelta));
        }

        return result;
    }

    /// <summary>
    /// Строит sample table для обычного MP4 без промежуточного списка timestamps.
    /// </summary>
    /// <param name="sizes">Размеры семплов.</param>
    /// <param name="stsc">Правила sample-to-chunk.</param>
    /// <param name="offsets">Смещения чанков.</param>
    /// <param name="stts">Time-to-sample таблица.</param>
    /// <param name="timeScale">Time scale трека.</param>
    private void BuildSampleTable(
        List<uint> sizes,
        List<(uint firstChunk, uint samplesPerChunk, uint descIdx)> stsc,
        List<long> offsets,
        List<(uint count, uint delta)> stts,
        uint timeScale)
    {
        if (sizes.Count == 0 || offsets.Count == 0)
            return;

        _samples = new List<SampleInfo>(sizes.Count);

        long currentTime = 0;
        int sttsIndex = 0;
        uint sttsConsumed = 0;
        int sampleIndex = 0;
        int stscIndex = 0;

        for (int chunkIndex = 0; chunkIndex < offsets.Count && sampleIndex < sizes.Count; chunkIndex++)
        {
            while (stscIndex + 1 < stsc.Count && stsc[stscIndex + 1].firstChunk - 1 <= chunkIndex)
                stscIndex++;

            uint samplesInChunk = stsc.Count > 0 ? stsc[stscIndex].samplesPerChunk : 1;
            long offset = offsets[chunkIndex];

            for (uint i = 0; i < samplesInChunk && sampleIndex < sizes.Count; i++)
            {
                uint delta = 20;
                if (sttsIndex < stts.Count)
                {
                    delta = stts[sttsIndex].delta;
                    sttsConsumed++;

                    if (sttsConsumed >= stts[sttsIndex].count)
                    {
                        sttsIndex++;
                        sttsConsumed = 0;
                    }
                }

                long timestampMs = timeScale > 0 ? currentTime * 1000 / timeScale : sampleIndex * 20L;
                int durationMs = timeScale > 0 ? (int)(delta * 1000 / timeScale) : 20;

                _samples.Add(new SampleInfo(offset, (int)sizes[sampleIndex], timestampMs, durationMs, true));

                offset += sizes[sampleIndex];
                currentTime += delta;
                sampleIndex++;
            }
        }

        if (_samples.Count > 0)
            _durationMs = _samples[^1].TimestampMs + _samples[^1].DurationMs;
    }

    /// <summary>
    /// Находит индекс семпла для seek по timestamp.
    /// </summary>
    /// <param name="targetMs">Целевая позиция в миллисекундах.</param>
    /// <returns>Индекс ближайшего семпла слева.</returns>
    private int BinarySearchSample(long targetMs)
    {
        int left = 0;
        int right = _samples.Count - 1;

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
}