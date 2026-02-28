using System.Numerics;
using System.Runtime.InteropServices;
using Concentus;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Decoders;

/// <summary>
/// OPUS декодер на базе Concentus.
/// Оптимизирован с SIMD конвертацией short→float.
/// </summary>
public sealed class OpusDecoder : IAudioDecoder
{
    private const int MaxFrameDurationMs = 120;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;
    private bool _disposed;

    public OpusDecoder(int sampleRate = 48000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MaxFrameSize = sampleRate * MaxFrameDurationMs / 1000;
        _shortBuffer = new short[MaxFrameSize * channels];

        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);

        Log.Debug($"[OpusDecoder] Created: {sampleRate}Hz, {channels}ch");
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int MaxFrameSize { get; }
    public AudioCodec Codec => AudioCodec.Opus;
    public bool IsInitialized => true; // Opus не требует отдельной инициализации

    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedData.IsEmpty)
            return DecodePLC(outputBuffer);

        try
        {
            Span<short> shortSpan = _shortBuffer.AsSpan(0, MaxFrameSize * Channels);
            int samples = _decoder.Decode(encodedData, shortSpan, MaxFrameSize);

            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
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
            int samples = _decoder.Decode(ReadOnlySpan<byte>.Empty, shortSpan, MaxFrameSize);
            ConvertShortToFloat(shortSpan[..(samples * Channels)], outputBuffer);
            return samples;
        }
        catch
        {
            // Fallback: возвращаем тишину (960 samples = 20ms @ 48kHz)
            const int fallbackSamples = 960;
            int totalSamples = fallbackSamples * Channels;
            outputBuffer[..Math.Min(totalSamples, outputBuffer.Length)].Clear();
            return fallbackSamples;
        }
    }

    /// <summary>
    /// SIMD-оптимизированная конвертация short[] → float[].
    /// На hot path (~50 вызовов/сек × 960 samples = ~48000 операций/сек).
    /// </summary>
    /// <remarks>
    /// <para>При наличии аппаратного SIMD (SSE2/AVX2/NEON) обрабатывает
    /// по 8 (Vector128) или 16 (Vector256) сэмплов за одну инструкцию.</para>
    /// </remarks>
    internal static void ConvertShortToFloat(ReadOnlySpan<short> src, Span<float> dst)
    {
        const float scale = 1f / 32768f;
        int len = Math.Min(src.Length, dst.Length);
        int i = 0;

        // SIMD путь — обрабатываем по Vector<short>.Count сэмплов за раз
        if (Vector.IsHardwareAccelerated && len >= Vector<short>.Count)
        {
            var scaleVec = new Vector<float>(scale);
            var shorts = MemoryMarshal.Cast<short, Vector<short>>(src[..len]);

            // Каждый Vector<short> содержит N shorts, из них получаются 2 Vector<float>
            // (потому что float вдвое шире short)
            int floatVecCount = Vector<float>.Count;

            for (int vi = 0; vi < shorts.Length; vi++)
            {
                // Widen: Vector<short> → 2 × Vector<int>
                Vector.Widen(shorts[vi], out var lo, out var hi);

                // Convert int→float и масштабируем
                var floatLo = Vector.ConvertToSingle(lo) * scaleVec;
                var floatHi = Vector.ConvertToSingle(hi) * scaleVec;

                // Записываем результат
                int baseIdx = vi * Vector<short>.Count;
                if (baseIdx + floatVecCount <= len)
                    floatLo.CopyTo(dst.Slice(baseIdx, floatVecCount));
                if (baseIdx + floatVecCount * 2 <= len)
                    floatHi.CopyTo(dst.Slice(baseIdx + floatVecCount, floatVecCount));
            }

            i = shorts.Length * Vector<short>.Count;
        }

        // Скалярный хвост
        for (; i < len; i++)
        {
            dst[i] = src[i] * scale;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _decoder.ResetState();

        if (_decoder is IDisposable d)
        {
            d.Dispose();
        }
    }
}