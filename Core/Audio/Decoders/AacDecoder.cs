using SharpJaad.AAC;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Logger;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// AAC декодер на базе SharpJaad.
/// </summary>
public sealed class AacDecoder : IAudioDecoder
{
    private Decoder? _decoder;
    private DecoderConfig? _config;
    private SampleBuffer? _sampleBuffer;
    
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxFrameSize;
    private bool _initialized;
    private bool _disposed;
    
    public AacDecoder(int sampleRate = 44100, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _maxFrameSize = 2048; // Типичный AAC frame
        
        Log.Debug($"[AacDecoder] Created: {sampleRate}Hz, {channels}ch");
    }
    
    public int SampleRate => _config?.GetSampleFrequency().GetFrequency() ?? _sampleRate;
    public int Channels => (int?)_config?.GetChannelConfiguration() ?? _channels;
    public int MaxFrameSize => _config?.GetFrameLength() ?? _maxFrameSize;
    public AudioCodec Codec => AudioCodec.Aac;
    
    /// <summary>
    /// Инициализирует декодер с MP4 Decoder Specific Info (Audio Specific Config).
    /// </summary>
    public void Initialize(byte[] decoderSpecificInfo)
    {
        if (_initialized) return;
        
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
    /// Инициализирует декодер с явными параметрами (с int sample rate).
    /// </summary>
    public void Initialize(Profile profile, int sampleRate, int channels)
    {
        Initialize(profile, GetSampleFrequency(sampleRate), channels);
    }
    
    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized)
        {
            // Auto-init с defaults (AAC-LC, 44100, stereo)
            Initialize(Profile.AAC_LC, SampleFrequency.SAMPLE_FREQUENCY_44100, _channels);
        }
        
        if (encodedData.IsEmpty)
        {
            // Silence для PLC
            int silenceSamples = 1024 * _channels;
            outputBuffer[..Math.Min(silenceSamples, outputBuffer.Length)].Clear();
            return 1024;
        }
        
        try
        {
            byte[] frameData = encodedData.ToArray();
            _decoder!.DecodeFrame(frameData, _sampleBuffer!);
            
            byte[] pcmData = _sampleBuffer!.Data;
            int sampleCount = pcmData.Length / (sizeof(short) * Channels);
            
            // Convert byte[] → float[]
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