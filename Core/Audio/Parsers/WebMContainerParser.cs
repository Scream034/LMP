using LMP.Core.Audio.Interfaces;
using LMP.Core.Helpers;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Адаптер WebMParser для IContainerParser.
/// </summary>
public sealed class WebMContainerParser : IContainerParser
{
    private readonly WebMParser _parser;
    
    public long DurationMs => _parser.DurationMs;
    public AudioCodec Codec => AudioCodec.Opus;
    public byte[]? DecoderConfig => null; // Opus не требует explicit config
    
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
        // WebMParser doesn't have explicit reset
    }
    
    public void Dispose()
    {
        _parser.Dispose();
    }
}