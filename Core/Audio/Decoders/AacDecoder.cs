using SharpJaad.AAC;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// AAC декодер на базе SharpJaad.
/// </summary>
public sealed class AacDecoder : IAudioDecoder
{
    private const int DefaultSampleRate = 44100;
    private const int DefaultChannels = 2;
    private const int DefaultMaxFrameSize = 2048;
    
    private Decoder? _decoder;
    private DecoderConfig? _config;
    private SampleBuffer? _sampleBuffer;
    
    private readonly int _requestedSampleRate;
    private readonly int _requestedChannels;
    private bool _initialized;
    private bool _disposed;
    
    public AacDecoder(int sampleRate = DefaultSampleRate, int channels = DefaultChannels)
    {
        _requestedSampleRate = sampleRate;
        _requestedChannels = channels;
        
        Log.Debug($"[AacDecoder] Created: requested {sampleRate}Hz, {channels}ch");
    }
    
    public int SampleRate => _config?.GetSampleFrequency().GetFrequency() ?? _requestedSampleRate;
    public int Channels => (int?)_config?.GetChannelConfiguration() ?? _requestedChannels;
    public int MaxFrameSize => _config?.GetFrameLength() ?? DefaultMaxFrameSize;
    public AudioCodec Codec => AudioCodec.Aac;
    public bool IsInitialized => _initialized;
    
    /// <summary>
    /// Инициализирует декодер с MP4 Decoder Specific Info (Audio Specific Config).
    /// </summary>
    public void Initialize(byte[] decoderSpecificInfo)
    {
        if (_initialized) return;
        
        if (decoderSpecificInfo == null || decoderSpecificInfo.Length < 2)
        {
            throw new ArgumentException("Invalid decoder specific info", nameof(decoderSpecificInfo));
        }
        
        try
        {
            _config = DecoderConfig.ParseMP4DecoderSpecificInfo(decoderSpecificInfo);
            _decoder = new Decoder(_config);
            
            _sampleBuffer = new SampleBuffer();
            _sampleBuffer.SetBigEndian(false);
            
            _initialized = true;
            
            Log.Debug($"[AacDecoder] Initialized with DSI: profile={_config.GetProfile()}, " +
                      $"sampleRate={_config.GetSampleFrequency()}, channels={_config.GetChannelConfiguration()}");
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Инициализирует декодер с явными параметрами.
    /// </summary>
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
            
            _initialized = true;
            
            Log.Debug($"[AacDecoder] Initialized: profile={profile}, rate={sampleFrequency}, ch={channels}");
        }
        catch (Exception ex)
        {
            throw new AudioDecoderException($"Failed to initialize AAC decoder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Инициализирует декодер с int sample rate.
    /// </summary>
    public void Initialize(Profile profile, int sampleRate, int channels)
    {
        Initialize(profile, GetSampleFrequency(sampleRate), channels);
    }
    
    /// <summary>
    /// Авто-инициализация с defaults если ещё не инициализирован.
    /// </summary>
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
            // PLC: возвращаем тишину
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
            
            return sampleCount;
        }
        catch (AACException ex)
        {
            Log.Warn($"[AacDecoder] Decode error: {ex.Message}");
            throw new AudioDecoderException($"AAC decode failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AacDecoder] Unexpected error: {ex.Message}");
            throw new AudioDecoderException($"AAC decode failed: {ex.Message}");
        }
    }
    
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        // SharpJaad не имеет explicit reset, пересоздаём декодер
        if (_initialized && _config != null)
        {
            try
            {
                _decoder = new Decoder(_config);
                _sampleBuffer = new SampleBuffer();
                _sampleBuffer.SetBigEndian(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[AacDecoder] Reset failed: {ex.Message}");
            }
        }
        return Decode(encodedData, outputBuffer);
    }
    
    private static void ConvertBytesToFloat(byte[] pcmBytes, Span<float> output, int sampleCount)
    {
        const float scale = 1f / 32768f;
        int count = Math.Min(sampleCount, output.Length);
        
        for (int i = 0; i < count; i++)
        {
            short sample = BitConverter.ToInt16(pcmBytes, i * sizeof(short));
            output[i] = sample * scale;
        }
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
    }
}