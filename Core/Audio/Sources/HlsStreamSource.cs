using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// HLS источник для AAC аудио.
/// Парсит m3u8, загружает TS сегменты, извлекает AAC фреймы.
/// </summary>
public sealed partial class HlsStreamSource : IAudioSource
{
    private const int DefaultPrefetchSegments = 3;
    private const int FrameDurationMs = 23; // ~1024 samples @ 44.1kHz
    
    private readonly string _masterUrl;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;
    private readonly int _prefetchSegments;
    
    private string _currentPlaylistUrl = string.Empty;
    private List<HlsSegment> _segments = [];
    private int _currentSegmentIndex;
    private readonly Queue<AacFrame> _frameBuffer = new();
    private readonly object _bufferLock = new();
    
    private byte[]? _audioSpecificConfig;
    private long _durationMs = -1;
    private long _positionMs;
    private int _sampleRate = 44100;
    private int _channels = 2;
    private bool _initialized;
    private volatile bool _disposed;
    
    private CancellationTokenSource? _operationCts;
    private Task? _prefetchTask;
    private readonly HashSet<int> _downloadedSegments = [];
    private readonly HashSet<int> _loadingSegments = [];
    
    public HlsStreamSource(
        string masterUrl,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        int prefetchSegments = DefaultPrefetchSegments)
    {
        _masterUrl = masterUrl ?? throw new ArgumentNullException(nameof(masterUrl));
        _httpClient = httpClient ?? Http.SharedHttpClient.Instance;
        _urlRefresher = urlRefresher;
        _prefetchSegments = prefetchSegments;
    }
    
    public long DurationMs => _durationMs;
    public long PositionMs => Volatile.Read(ref _positionMs);
    public bool CanSeek => _segments.Count > 0;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig => _audioSpecificConfig;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    
    public double BufferProgress
    {
        get
        {
            lock (_bufferLock)
            {
                return _segments.Count > 0
                    ? (double)_downloadedSegments.Count / _segments.Count * 100
                    : 0;
            }
        }
    }
    
    public bool IsFullyBuffered
    {
        get
        {
            lock (_bufferLock)
            {
                return _downloadedSegments.Count >= _segments.Count;
            }
        }
    }
    
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;
        
        try
        {
            Log.Debug($"[HLS] Initializing: {_masterUrl[..Math.Min(60, _masterUrl.Length)]}...");
            
            var masterContent = await _httpClient.GetStringAsync(_masterUrl, ct);
            var audioPlaylistUrl = ParseMasterPlaylist(masterContent, _masterUrl);
            
            if (string.IsNullOrEmpty(audioPlaylistUrl))
            {
                audioPlaylistUrl = _masterUrl;
            }
            
            _currentPlaylistUrl = audioPlaylistUrl;
            
            var mediaContent = await _httpClient.GetStringAsync(audioPlaylistUrl, ct);
            _segments = ParseMediaPlaylist(mediaContent, audioPlaylistUrl);
            
            if (_segments.Count == 0)
                throw new InvalidOperationException("No segments found in HLS playlist");
            
            _durationMs = (long)_segments.Sum(s => s.DurationMs);
            
            // Load first segment to get ASC
            await LoadSegmentAsync(0, ct);
            
            _initialized = true;
            
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _prefetchTask = Task.Run(() => PrefetchLoopAsync(_operationCts.Token));
            
            Log.Info($"[HLS] Initialized: {_segments.Count} segments, duration={_durationMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[HLS] Init failed: {ex.Message}", ex);
            return false;
        }
    }
    
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized)
            throw new InvalidOperationException("Not initialized");
        
        while (true)
        {
            // Try to get frame from buffer
            lock (_bufferLock)
            {
                if (_frameBuffer.Count > 0)
                {
                    var frame = _frameBuffer.Dequeue();
                    Volatile.Write(ref _positionMs, frame.TimestampMs);
                    
                    return new AudioFrame
                    {
                        Data = frame.Data,
                        TimestampMs = frame.TimestampMs,
                        DurationMs = frame.DurationMs,
                        IsKeyFrame = true
                    };
                }
            }
            
            // Check if we've reached end
            if (_currentSegmentIndex >= _segments.Count)
                return null;
            
            // Load next segment
            bool isDownloaded;
            lock (_bufferLock)
            {
                isDownloaded = _downloadedSegments.Contains(_currentSegmentIndex);
            }
            
            if (!isDownloaded)
            {
                await LoadSegmentAsync(_currentSegmentIndex, ct);
            }
            
            // If still no frames, move to next segment
            lock (_bufferLock)
            {
                if (_frameBuffer.Count == 0)
                {
                    _currentSegmentIndex++;
                }
            }
        }
    }
    
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_segments.Count == 0) return false;
        
        // Find target segment by time
        long accumulated = 0;
        int targetSegment = 0;
        
        for (int i = 0; i < _segments.Count; i++)
        {
            if (accumulated + _segments[i].DurationMs > positionMs)
            {
                targetSegment = i;
                break;
            }
            accumulated += (long)_segments[i].DurationMs;
            targetSegment = i;
        }
        
        Log.Debug($"[HLS] Seek to {positionMs}ms → segment {targetSegment}");
        
        // Clear buffer and reset state
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
            // Mark segment as not downloaded to force reload
            _downloadedSegments.Remove(targetSegment);
        }
        
        _currentSegmentIndex = targetSegment;
        Volatile.Write(ref _positionMs, accumulated);
        
        // Load the target segment
        await LoadSegmentAsync(targetSegment, ct);
        
        return true;
    }
    
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_segments.Count == 0 || _durationMs <= 0) return [];
        
        var ranges = new List<(double, double)>();
        
        lock (_bufferLock)
        {
            int? rangeStart = null;
            long startMs = 0;
            long currentMs = 0;
            
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_downloadedSegments.Contains(i))
                {
                    if (rangeStart == null)
                    {
                        rangeStart = i;
                        startMs = currentMs;
                    }
                }
                else if (rangeStart.HasValue)
                {
                    double start = (double)startMs / _durationMs;
                    double end = (double)currentMs / _durationMs;
                    ranges.Add((start, end));
                    rangeStart = null;
                }
                
                currentMs += (long)_segments[i].DurationMs;
            }
            
            if (rangeStart.HasValue)
            {
                ranges.Add(((double)startMs / _durationMs, 1.0));
            }
        }
        
        return ranges;
    }
    
    public void ReleaseRamBuffers()
    {
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }
    }
    
    public void CancelPendingOperations()
    {
        _operationCts?.Cancel();
    }
    
    #region Parsing
    
    private static string? ParseMasterPlaylist(string content, string baseUrl)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? audioUrl = null;
        int maxBandwidth = 0;
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            if (line.StartsWith("#EXT-X-MEDIA:") && line.Contains("TYPE=AUDIO"))
            {
                var uriMatch = UriRegex().Match(line);
                if (uriMatch.Success)
                {
                    audioUrl = ResolveUrl(baseUrl, uriMatch.Groups[1].Value);
                }
            }
            
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var bwMatch = BandwidthRegex().Match(line);
                int bw = bwMatch.Success ? int.Parse(bwMatch.Groups[1].Value) : 0;
                
                if (i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
                {
                    var url = lines[i + 1].Trim();
                    if (bw > maxBandwidth || audioUrl == null)
                    {
                        maxBandwidth = bw;
                        audioUrl = ResolveUrl(baseUrl, url);
                    }
                }
            }
        }
        
        return audioUrl;
    }
    
    private static List<HlsSegment> ParseMediaPlaylist(string content, string baseUrl)
    {
        var segments = new List<HlsSegment>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        double duration = 0;
        long accumulatedMs = 0;
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            if (line.StartsWith("#EXTINF:"))
            {
                var durationStr = line[8..].Split(',')[0];
                duration = double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!line.StartsWith("#") && !string.IsNullOrEmpty(line))
            {
                var url = ResolveUrl(baseUrl, line);
                var durationMs = duration * 1000;
                
                segments.Add(new HlsSegment
                {
                    Url = url,
                    DurationMs = durationMs,
                    StartMs = accumulatedMs
                });
                
                accumulatedMs += (long)durationMs;
                duration = 0;
            }
        }
        
        return segments;
    }
    
    private static string ResolveUrl(string baseUrl, string relativeUrl)
    {
        if (relativeUrl.StartsWith("http://") || relativeUrl.StartsWith("https://"))
            return relativeUrl;
        
        var baseUri = new Uri(baseUrl);
        return new Uri(baseUri, relativeUrl).ToString();
    }
    
    [GeneratedRegex(@"URI=""([^""]+)""")]
    private static partial Regex UriRegex();
    
    [GeneratedRegex(@"BANDWIDTH=(\d+)")]
    private static partial Regex BandwidthRegex();
    
    #endregion
    
    #region Segment Loading
    
    private async Task LoadSegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _segments.Count) return;
        
        // Check if already downloaded or loading
        lock (_bufferLock)
        {
            if (_downloadedSegments.Contains(index)) return;
            if (_loadingSegments.Contains(index)) return;
            _loadingSegments.Add(index);
        }
        
        var segment = _segments[index];
        
        try
        {
            Log.Debug($"[HLS] Loading segment {index}: {segment.Url[..Math.Min(50, segment.Url.Length)]}...");
            
            var data = await _httpClient.GetByteArrayAsync(segment.Url, ct);
            
            // Extract AAC frames from TS
            var frames = ExtractAacFramesFromTs(data, segment.StartMs);
            
            lock (_bufferLock)
            {
                foreach (var frame in frames)
                {
                    _frameBuffer.Enqueue(frame);
                }
                
                _downloadedSegments.Add(index);
                _loadingSegments.Remove(index);
            }
            
            Log.Debug($"[HLS] Segment {index}: {frames.Count} frames extracted");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_bufferLock)
            {
                _loadingSegments.Remove(index);
            }
            Log.Warn($"[HLS] Segment {index} failed: {ex.Message}");
            throw;
        }
    }
    
    private List<AacFrame> ExtractAacFramesFromTs(byte[] tsData, long baseTimestampMs)
    {
        var frames = new List<AacFrame>();
        int pos = 0;
        long currentMs = baseTimestampMs;
        
        while (pos + 188 <= tsData.Length)
        {
            // TS packet = 188 bytes, sync byte = 0x47
            if (tsData[pos] != 0x47)
            {
                pos++;
                continue;
            }
            
            bool hasPayload = (tsData[pos + 3] & 0x10) != 0;
            bool hasAdaptation = (tsData[pos + 3] & 0x20) != 0;
            
            int payloadStart = pos + 4;
            if (hasAdaptation)
            {
                int adaptLength = tsData[payloadStart];
                payloadStart += 1 + adaptLength;
            }
            
            if (hasPayload && payloadStart < pos + 188)
            {
                int payloadLen = pos + 188 - payloadStart;
                var payload = tsData.AsSpan(payloadStart, payloadLen);
                
                int adtsPos = 0;
                while (adtsPos + 7 <= payload.Length)
                {
                    // ADTS sync: 0xFFF
                    if (payload[adtsPos] == 0xFF && (payload[adtsPos + 1] & 0xF0) == 0xF0)
                    {
                        int frameLength = ((payload[adtsPos + 3] & 0x03) << 11) |
                                          (payload[adtsPos + 4] << 3) |
                                          ((payload[adtsPos + 5] & 0xE0) >> 5);
                        
                        if (frameLength > 0 && adtsPos + frameLength <= payload.Length)
                        {
                            // Extract ASC if not yet
                            if (_audioSpecificConfig == null)
                            {
                                _audioSpecificConfig = ExtractAscFromAdts(payload.Slice(adtsPos, 7));
                                ParseAscForSampleInfo();
                            }
                            
                            // AAC raw data (without ADTS header)
                            int headerLen = (payload[adtsPos + 1] & 0x01) == 0 ? 9 : 7;
                            int dataLen = frameLength - headerLen;
                            
                            if (dataLen > 0 && adtsPos + headerLen + dataLen <= payload.Length)
                            {
                                var frameData = payload.Slice(adtsPos + headerLen, dataLen).ToArray();
                                
                                frames.Add(new AacFrame
                                {
                                    Data = frameData,
                                    TimestampMs = currentMs,
                                    DurationMs = FrameDurationMs
                                });
                                
                                currentMs += FrameDurationMs;
                            }
                            
                            adtsPos += frameLength;
                        }
                        else
                        {
                            adtsPos++;
                        }
                    }
                    else
                    {
                        adtsPos++;
                    }
                }
            }
            
            pos += 188;
        }
        
        return frames;
    }
    
    private static byte[] ExtractAscFromAdts(ReadOnlySpan<byte> adtsHeader)
    {
        int profile = ((adtsHeader[2] & 0xC0) >> 6) + 1;
        int sampleRateIndex = (adtsHeader[2] & 0x3C) >> 2;
        int channelConfig = ((adtsHeader[2] & 0x01) << 2) | ((adtsHeader[3] & 0xC0) >> 6);
        
        int asc = (profile << 11) | (sampleRateIndex << 7) | (channelConfig << 3);
        
        return [(byte)(asc >> 8), (byte)(asc & 0xFF)];
    }
    
    private void ParseAscForSampleInfo()
    {
        if (_audioSpecificConfig == null || _audioSpecificConfig.Length < 2) return;
        
        int asc = (_audioSpecificConfig[0] << 8) | _audioSpecificConfig[1];
        int sampleRateIndex = (asc >> 7) & 0x0F;
        int channelConfig = (asc >> 3) & 0x0F;
        
        _sampleRate = sampleRateIndex switch
        {
            0 => 96000, 1 => 88200, 2 => 64000, 3 => 48000,
            4 => 44100, 5 => 32000, 6 => 24000, 7 => 22050,
            8 => 16000, 9 => 12000, 10 => 11025, 11 => 8000,
            _ => 44100
        };
        
        _channels = channelConfig switch
        {
            1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 8,
            _ => 2
        };
    }
    
    #endregion
    
    #region Prefetch
    
    private async Task PrefetchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                
                int current = _currentSegmentIndex;
                
                for (int i = 0; i < _prefetchSegments; i++)
                {
                    int targetIndex = current + i;
                    
                    if (targetIndex >= _segments.Count) break;
                    
                    bool shouldLoad;
                    lock (_bufferLock)
                    {
                        shouldLoad = !_downloadedSegments.Contains(targetIndex) &&
                                     !_loadingSegments.Contains(targetIndex);
                    }
                    
                    if (shouldLoad)
                    {
                        await LoadSegmentAsync(targetIndex, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"[HLS] Prefetch error: {ex.Message}");
            }
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _operationCts?.Cancel();
        
        if (_prefetchTask != null)
        {
            try { await _prefetchTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }
        
        _operationCts?.Dispose();
        
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }
    }
    
    #region Types
    
    private sealed class HlsSegment
    {
        public required string Url { get; init; }
        public double DurationMs { get; init; }
        public long StartMs { get; init; }
    }
    
    private readonly struct AacFrame
    {
        public required byte[] Data { get; init; }
        public long TimestampMs { get; init; }
        public int DurationMs { get; init; }
    }
    
    #endregion
}