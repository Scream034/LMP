using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Helpers;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Адаптер WebMParser для IContainerParser.
/// </summary>
public sealed class WebMContainerParser : IContainerParser
{
    private readonly WebMParser _parser;
    private bool _disposed;
    
    public long DurationMs => _parser.DurationMs;
    public AudioCodec Codec => AudioCodec.Opus;
    public byte[]? DecoderConfig => _parser.CodecPrivate;
    public int SampleRate => _parser.SampleRate > 0 ? _parser.SampleRate : 48000;
    public int Channels => _parser.Channels > 0 ? _parser.Channels : 2;
    
    public WebMContainerParser(Stream stream)
    {
        _parser = new WebMParser(stream);
    }
    
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        return await _parser.ParseHeadersAsync(ct);
    }
    
    public async ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        var block = await _parser.ReadNextBlockAsync(ct);
        
        if (block == null)
            return null;
        
        return new AudioFrame
        {
            Data = block.Value.Data,
            TimestampMs = block.Value.TimestampMs,
            DurationMs = 20, // Typical Opus frame
            IsKeyFrame = block.Value.IsKeyFrame
        };
    }
    
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        var bytePos = _parser.FindSeekPosition(targetMs);
        return bytePos.HasValue ? (bytePos.Value, targetMs) : null;
    }
    
    public void Reset()
    {
        // WebMParser stateless between clusters
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.Dispose();
    }
    
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}