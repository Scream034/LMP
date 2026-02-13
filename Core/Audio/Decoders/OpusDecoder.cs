using Concentus;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Logger;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// OPUS декодер на базе Concentus.
/// </summary>
public sealed class OpusDecoder : IAudioDecoder
{
    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxFrameSize;
    private bool _disposed;
    
    public OpusDecoder(int sampleRate = 48000, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _maxFrameSize = sampleRate * 120 / 1000; // 120ms max
        _shortBuffer = new short[_maxFrameSize * channels];
        
        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        
        Log.Debug($"[OpusDecoder] Created: {sampleRate}Hz, {channels}ch");
    }
    
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public int MaxFrameSize => _maxFrameSize;
    public AudioCodec Codec => AudioCodec.Opus;
    
    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (encodedData.IsEmpty)
            return DecodePLC(outputBuffer);
        
        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, _maxFrameSize * _channels);
            int samples = _decoder.Decode(encodedData, shortSpan, _maxFrameSize);
            
            ConvertToFloat(shortSpan[..(samples * _channels)], outputBuffer);
            return samples;
        }
        catch (Exception ex)
        {
            Log.Warn($"[OpusDecoder] Decode error: {ex.Message}");
            throw new AudioDecoderException($"OPUS decode failed: {ex.Message}");
        }
    }
    
    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        _decoder.ResetState();
        return Decode(encodedData, outputBuffer);
    }
    
    private int DecodePLC(Span<float> outputBuffer)
    {
        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan();
            int samples = _decoder.Decode(ReadOnlySpan<byte>.Empty, shortSpan, _maxFrameSize);
            ConvertToFloat(shortSpan[..(samples * _channels)], outputBuffer);
            return samples;
        }
        catch
        {
            outputBuffer[..(_channels * 960)].Clear();
            return 960;
        }
    }
    
    private static void ConvertToFloat(ReadOnlySpan<short> src, Span<float> dst)
    {
        const float scale = 1f / 32768f;
        int len = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < len; i++)
            dst[i] = src[i] * scale;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decoder.ResetState();
        if (_decoder is IDisposable d) d.Dispose();
    }
}