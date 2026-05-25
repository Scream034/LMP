using System.Runtime.InteropServices;
using SharpJaad.AAC;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// AAC декодер на базе SharpJaad с поддержкой HE-AAC (SBR).
/// Оптимизирован с SIMD конвертацией и пулингом буферов.
/// </summary>
public sealed class AacDecoder : IAudioDecoder
{
    private const int DefaultSampleRate = 44100;
    private const int DefaultChannels = 2;
    private const int DefaultMaxFrameSize = 2048;
    private const int MaxConsecutiveSilentErrors = 15;

    private Decoder? _decoder;
    private DecoderConfig? _config;
    private SampleBuffer? _sampleBuffer;
    private byte[]? _originalAsc;

    private readonly int _requestedSampleRate;
    private readonly int _requestedChannels;
    private bool _initialized;
    private bool _disposed;

    private bool _isHeAac;
    private int _outputSampleRate;
    private int _consecutiveErrors;

    public AacDecoder(int sampleRate = DefaultSampleRate, int channels = DefaultChannels)
    {
        _requestedSampleRate = sampleRate;
        _requestedChannels = channels;

        Log.Debug($"[AacDecoder] Created: requested {sampleRate}Hz, {channels}ch");
    }

    public int SampleRate => _outputSampleRate > 0 ? _outputSampleRate :
                             _config?.GetSampleFrequency().GetFrequency() ?? _requestedSampleRate;
    public int Channels => (int?)_config?.GetChannelConfiguration() ?? _requestedChannels;
    public int MaxFrameSize => _isHeAac ? DefaultMaxFrameSize * 2 :
                               _config?.GetFrameLength() ?? DefaultMaxFrameSize;
    public AudioCodec Codec => AudioCodec.Aac;
    public bool IsInitialized => _initialized;

    public void Initialize(byte[] decoderSpecificInfo)
    {
        if (_initialized) return;

        if (decoderSpecificInfo == null || decoderSpecificInfo.Length < 2)
            throw new ArgumentException("Invalid decoder specific info", nameof(decoderSpecificInfo));

        try
        {
            _originalAsc = (byte[])decoderSpecificInfo.Clone();
            var ascInfo = ParseAudioSpecificConfig(decoderSpecificInfo);

            Log.Debug($"[AacDecoder] ASC analysis: objectType={ascInfo.ObjectType}, " +
                     $"baseRate={ascInfo.BaseSampleRate}, outputRate={ascInfo.OutputSampleRate}, " +
                     $"channels={ascInfo.Channels}, isHE-AAC={ascInfo.IsHeAac}");

            if (ascInfo.IsHeAac)
                InitializeHeAac(ascInfo);
            else
                InitializeStandard(decoderSpecificInfo);

            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);
            _initialized = true;
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }

    private void InitializeHeAac(AscInfo ascInfo)
    {
        _isHeAac = true;
        _outputSampleRate = ascInfo.OutputSampleRate;

        byte[] modifiedAsc = CreateAacLcAsc(ascInfo);

        Log.Debug($"[AacDecoder] HE-AAC: using modified ASC for core decoder: " +
                 $"{BitConverter.ToString(modifiedAsc)}");

        _config = DecoderConfig.ParseMP4DecoderSpecificInfo(modifiedAsc);
        _config.SetSBRPresent(true);
        _decoder = new Decoder(_config);

        Log.Info($"[AacDecoder] HE-AAC initialized: core={ascInfo.BaseSampleRate}Hz, " +
                $"output={_outputSampleRate}Hz, channels={ascInfo.Channels}");
    }

    private void InitializeStandard(byte[] decoderSpecificInfo)
    {
        _isHeAac = false;

        _config = DecoderConfig.ParseMP4DecoderSpecificInfo(decoderSpecificInfo);
        _decoder = new Decoder(_config);
        _outputSampleRate = _config.GetSampleFrequency().GetFrequency();

        Log.Debug($"[AacDecoder] AAC-LC initialized: profile={_config.GetProfile()}, " +
                 $"sampleRate={_config.GetSampleFrequency()}, channels={_config.GetChannelConfiguration()}");
    }

    /// <summary>
    /// Полностью пересоздаёт внутренний декодер из оригинального ASC.
    /// Вызывается после seek или при неисправимых ошибках.
    /// </summary>
    private void RecreateDecoder()
    {
        if (_originalAsc == null || _config == null) return;

        try
        {
            if (_isHeAac)
            {
                var ascInfo = ParseAudioSpecificConfig(_originalAsc);
                byte[] modifiedAsc = CreateAacLcAsc(ascInfo);
                _config = DecoderConfig.ParseMP4DecoderSpecificInfo(modifiedAsc);
                _config.SetSBRPresent(true);
            }
            else
            {
                _config = DecoderConfig.ParseMP4DecoderSpecificInfo(_originalAsc);
            }

            _decoder = new Decoder(_config);
            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);
            _consecutiveErrors = 0;

            Log.Debug("[AacDecoder] Decoder recreated successfully");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AacDecoder] Recreate failed: {ex.Message}");
        }
    }

    private static byte[] CreateAacLcAsc(AscInfo info)
    {
        int freqIndex = GetSampleFrequencyIndex(info.BaseSampleRate);
        int objectType = 2; // AAC-LC

        byte byte0 = (byte)((objectType << 3) | ((freqIndex >> 1) & 0x07));
        byte byte1 = (byte)(((freqIndex & 0x01) << 7) | ((info.Channels & 0x0F) << 3));

        return [byte0, byte1];
    }

    private static AscInfo ParseAudioSpecificConfig(byte[] asc)
    {
        if (asc.Length < 2)
        {
            return new AscInfo
            {
                ObjectType = 2,
                BaseSampleRate = 44100,
                OutputSampleRate = 44100,
                Channels = 2,
                IsHeAac = false
            };
        }

        int audioObjectType = (asc[0] >> 3) & 0x1F;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 0x01);
        int channelConfig = (asc[1] >> 3) & 0x0F;

        int baseSampleRate = GetSampleRateFromIndex(samplingFrequencyIndex);

        bool isHeAac = audioObjectType == 5 || audioObjectType == 29;
        int outputSampleRate = baseSampleRate;
        if (isHeAac && baseSampleRate > 0)
            outputSampleRate = baseSampleRate * 2;

        int channels = channelConfig switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 5,
            6 => 6,
            7 => 8,
            _ => 2
        };

        return new AscInfo
        {
            ObjectType = audioObjectType,
            BaseSampleRate = baseSampleRate,
            OutputSampleRate = outputSampleRate,
            Channels = channels,
            IsHeAac = isHeAac
        };
    }

    private static int GetSampleRateFromIndex(int index)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
                       16000, 12000, 11025, 8000, 7350, 0, 0, 0];
        return index >= 0 && index < rates.Length ? rates[index] : 44100;
    }

    private static int GetSampleFrequencyIndex(int sampleRate)
    {
        return sampleRate switch
        {
            96000 => 0,
            88200 => 1,
            64000 => 2,
            48000 => 3,
            44100 => 4,
            32000 => 5,
            24000 => 6,
            22050 => 7,
            16000 => 8,
            12000 => 9,
            11025 => 10,
            8000 => 11,
            7350 => 12,
            _ => 4
        };
    }

    private readonly record struct AscInfo
    {
        public int ObjectType { get; init; }
        public int BaseSampleRate { get; init; }
        public int OutputSampleRate { get; init; }
        public int Channels { get; init; }
        public bool IsHeAac { get; init; }
    }

    public void Initialize(Profile profile, SampleFrequency sampleFrequency, int channels)
    {
        if (_initialized) return;

        try
        {
            _config = new DecoderConfig();
            _config.SetProfile(profile);
            _config.SetSampleFrequency(sampleFrequency);
            _config.SetChannelConfiguration((ChannelConfiguration)channels);

            _decoder = new Decoder(_config);
            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);

            _outputSampleRate = sampleFrequency.GetFrequency();
            _initialized = true;

            Log.Debug($"[AacDecoder] Initialized: profile={profile}, rate={sampleFrequency}, ch={channels}");
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }

    public void Initialize(Profile profile, int sampleRate, int channels)
    {
        Initialize(profile, GetSampleFrequency(sampleRate), channels);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        Initialize(Profile.AAC_LC, GetSampleFrequency(_requestedSampleRate), _requestedChannels);
    }

    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (encodedData.IsEmpty)
        {
            int silenceSamples = MaxFrameSize;
            int totalSamples = silenceSamples * Channels;
            outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
            return silenceSamples;
        }

        try
        {
            byte[] frameData = encodedData.ToArray();
            _decoder!.DecodeFrame(frameData, _sampleBuffer!);

            byte[] pcmData = _sampleBuffer!.Data;
            int sampleCount = pcmData.Length / (sizeof(short) * Channels);

            ConvertBytesToFloat(pcmData, outputBuffer, sampleCount * Channels);

            _consecutiveErrors = 0;
            return sampleCount;
        }
        catch (Exception ex) when (ex is AACException or InvalidCastException or IndexOutOfRangeException)
        {
            _consecutiveErrors++;

            if (_consecutiveErrors <= MaxConsecutiveSilentErrors)
            {
                if (_consecutiveErrors <= 3)
                    Log.Debug($"[AacDecoder] Decode error (attempt {_consecutiveErrors}): {ex.Message}");

                // Каждые 5 ошибок пересоздаём декодер
                if (_consecutiveErrors % 5 == 0)
                    RecreateDecoder();

                int silenceSamples = MaxFrameSize;
                int totalSamples = silenceSamples * Channels;
                outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
                return silenceSamples;
            }

            // Последняя попытка: полное пересоздание
            RecreateDecoder();

            Log.Warn($"[AacDecoder] Persistent decode error after {_consecutiveErrors} attempts: {ex.Message}");

            // Возвращаем тишину вместо исключения — не крашим воспроизведение
            int silence = MaxFrameSize;
            int total = silence * Channels;
            outputBuffer[..Math.Min(total, outputBuffer.Length)].Clear();
            return silence;
        }
    }

    /// <summary>
    /// Лёгкий сброс внутреннего состояния декодера без полного пересоздания.
    /// </summary>
    /// <remarks>
    /// <para><b>Индустриальная практика:</b></para>
    /// <list type="bullet">
    ///   <item>FFmpeg: <c>avcodec_flush_buffers</c> — обнуление internal buffers
    ///     через <c>memset</c> на <c>ChannelElement</c> без пересоздания контекста.</item>
    ///   <item>VLC: flush сбрасывает error state, позволяя продолжить после seek.</item>
    ///   <item>ExoPlayer: <c>canKeepCodec</c> — reuse codec, flush при seek.</item>
    /// </list>
    ///
    /// <para><b>Предыдущий подход (<see cref="RecreateDecoder"/>):</b>
    /// <c>ParseMP4DecoderSpecificInfo</c> + <c>new Decoder</c> + <c>new SampleBuffer</c>
    /// = ~2ms + 10 аллокаций. Вызывался на КАЖДОМ skip-фрейме (5 раз для AAC)
    /// = ~10ms + 50 аллокаций на seek.</para>
    ///
    /// <para><b>Новый подход:</b> <c>Decoder.Flush()</c> обнуляет SyntacticElements
    /// (prediction/LTP state) через <c>Array.Clear</c>. FilterBank overlap
    /// вытесняется skip-frames. Стоимость: ~0.1ms + 1 аллокация.</para>
    /// </remarks>
    public void FlushState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _decoder == null) return;

        _decoder.Flush();
        _consecutiveErrors = 0;
    }

    /// <summary>
    /// Декодирует фрейм с предварительным flush внутреннего state.
    /// Вызывается ОДИН РАЗ после seek через <c>_decoderResetNeeded</c> CAS-флаг.
    /// </summary>
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        FlushState();
        return Decode(encodedData, outputBuffer);
    }

    /// <summary>
    /// SIMD-оптимизированная конвертация byte[] (little-endian PCM16) → float[].
    /// </summary>
    /// <remarks>
    /// <para>SharpJaad возвращает PCM данные как byte[] с int16 сэмплами.
    /// Конвертация вызывается на каждый фрейм (~50 раз/сек), поэтому оптимизирована через SIMD.</para>
    /// </remarks>
    internal static void ConvertBytesToFloat(byte[] pcmBytes, Span<float> output, int sampleCount)
    {
        int count = Math.Min(sampleCount, output.Length);

        // Интерпретируем byte[] как short[] без копирования
        var shorts = MemoryMarshal.Cast<byte, short>(pcmBytes.AsSpan(0, count * sizeof(short)));

        // Используем общую SIMD-конвертацию из OpusDecoder
        OpusDecoder.ConvertShortToFloat(shorts[..count], output);
    }

    private static SampleFrequency GetSampleFrequency(int rate)
    {
        return rate switch
        {
            96000 => SampleFrequency.SAMPLE_FREQUENCY_96000,
            88200 => SampleFrequency.SAMPLE_FREQUENCY_88200,
            64000 => SampleFrequency.SAMPLE_FREQUENCY_64000,
            48000 => SampleFrequency.SAMPLE_FREQUENCY_48000,
            44100 => SampleFrequency.SAMPLE_FREQUENCY_44100,
            32000 => SampleFrequency.SAMPLE_FREQUENCY_32000,
            24000 => SampleFrequency.SAMPLE_FREQUENCY_24000,
            22050 => SampleFrequency.SAMPLE_FREQUENCY_22050,
            16000 => SampleFrequency.SAMPLE_FREQUENCY_16000,
            12000 => SampleFrequency.SAMPLE_FREQUENCY_12000,
            11025 => SampleFrequency.SAMPLE_FREQUENCY_11025,
            8000 => SampleFrequency.SAMPLE_FREQUENCY_8000,
            _ => SampleFrequency.SAMPLE_FREQUENCY_44100
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _decoder = null;
        _config = null;
        _sampleBuffer = null;
        _originalAsc = null;
    }
}