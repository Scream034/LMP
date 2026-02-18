using System.Net;
using System.Text.RegularExpressions;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Youtube.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

public sealed partial class HlsStreamSource : IAudioSource
{
    private readonly string _masterUrl;
    private readonly string? _trackId;
    private readonly HttpClient _httpClient;

    private string _currentPlaylistUrl = string.Empty;
    private List<HlsSegment> _segments = [];
    private int _currentSegmentIndex;
    private readonly Queue<AacFrame> _frameBuffer = new();
    private readonly Lock _bufferLock = new();
    private long _positionMs;
    private bool _initialized;
    private volatile bool _disposed;

    private CancellationTokenSource? _operationCts;
    private Task? _prefetchTask;
    private readonly HashSet<int> _downloadedSegments = [];
    private readonly HashSet<int> _loadingSegments = [];

    public HlsStreamSource(string masterUrl, HttpClient? httpClient = null, string? trackId = null)
    {
        _masterUrl = masterUrl ?? throw new ArgumentNullException(nameof(masterUrl));
        _httpClient = httpClient ?? Http.SharedHttpClient.Instance;
        _trackId = trackId;
    }

    public long DurationMs { get; private set; } = -1;
    public long PositionMs => Volatile.Read(ref _positionMs);
    public bool CanSeek => _segments.Count > 0;
    public AudioCodec Codec => AudioCodec.Aac;
    public byte[]? DecoderConfig { get; private set; }
    public int SampleRate { get; private set; } = 44100;
    public int Channels { get; private set; } = 2;

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
            Log.Debug($"[HLS] Initializing: {TruncateUrl(_masterUrl)}");

            _currentPlaylistUrl = await ResolvePlaylistUrlAsync(ct);

            var mediaContent = await GetStringWithErrorHandlingAsync(_currentPlaylistUrl, ct);
            _segments = ParseMediaPlaylist(mediaContent, _currentPlaylistUrl);

            if (_segments.Count == 0)
                throw new InvalidOperationException("No segments found in HLS playlist");

            DurationMs = (long)_segments.Sum(s => s.DurationMs);

            await LoadSegmentAsync(0, ct);

            _initialized = true;

            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _prefetchTask = Task.Run(() => PrefetchLoopAsync(_operationCts.Token), ct);

            Log.Info($"[HLS] Initialized: {_segments.Count} segments, duration={DurationMs}ms");
            return true;
        }
        catch (StreamUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Error($"[HLS] Init failed: 403 Forbidden");
            throw new StreamUnavailableException(
                "HLS manifest returned 403 Forbidden",
                _trackId ?? "unknown",
                StreamUnavailableReason.Forbidden403,
                httpStatusCode: 403,
                wasHlsFallback: true);
        }
        catch (Exception ex)
        {
            Log.Error($"[HLS] Init failed: {ex.Message}", ex);
            throw;
        }
    }

    private async Task<string> GetStringWithErrorHandlingAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _httpClient.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StreamUnavailableException(
                "HLS request returned 403 Forbidden",
                _trackId ?? "unknown",
                StreamUnavailableReason.Forbidden403,
                httpStatusCode: 403,
                wasHlsFallback: true);
        }
    }

    private async Task<string> ResolvePlaylistUrlAsync(CancellationToken ct)
    {
        var masterContent = await GetStringWithErrorHandlingAsync(_masterUrl, ct);
        var audioPlaylistUrl = ParseMasterPlaylist(masterContent, _masterUrl);
        return string.IsNullOrEmpty(audioPlaylistUrl) ? _masterUrl : audioPlaylistUrl;
    }

    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
            throw new InvalidOperationException("Not initialized");

        while (true)
        {
            if (TryDequeueFrame(out var frame))
            {
                Volatile.Write(ref _positionMs, frame.TimestampMs);
                return new AudioFrame
                {
                    Data = frame.Data,
                    TimestampMs = frame.TimestampMs,
                    DurationMs = frame.DurationMs,
                    IsKeyFrame = true
                };
            }

            if (_currentSegmentIndex >= _segments.Count)
                return null;

            if (!IsSegmentDownloaded(_currentSegmentIndex))
            {
                await LoadSegmentAsync(_currentSegmentIndex, ct);
            }

            if (IsFrameBufferEmpty())
            {
                _currentSegmentIndex++;
            }
        }
    }

    private bool TryDequeueFrame(out AacFrame frame)
    {
        lock (_bufferLock)
        {
            if (_frameBuffer.Count > 0)
            {
                frame = _frameBuffer.Dequeue();
                return true;
            }
            frame = default;
            return false;
        }
    }

    private bool IsSegmentDownloaded(int index)
    {
        lock (_bufferLock)
        {
            return _downloadedSegments.Contains(index);
        }
    }

    private bool IsFrameBufferEmpty()
    {
        lock (_bufferLock)
        {
            return _frameBuffer.Count == 0;
        }
    }

    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_segments.Count == 0) return false;

        var (targetSegment, segmentStartMs) = FindSegmentByTime(positionMs);

        Log.Debug($"[HLS] Seek to {positionMs}ms → segment {targetSegment}");

        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }

        _currentSegmentIndex = targetSegment;
        Volatile.Write(ref _positionMs, segmentStartMs);

        if (!IsSegmentDownloaded(targetSegment))
        {
            await LoadSegmentAsync(targetSegment, ct);
        }

        return true;
    }

    private (int Index, long StartMs) FindSegmentByTime(long positionMs)
    {
        long accumulated = 0;

        for (int i = 0; i < _segments.Count; i++)
        {
            if (accumulated + _segments[i].DurationMs > positionMs)
            {
                return (i, accumulated);
            }
            accumulated += (long)_segments[i].DurationMs;
        }

        return (_segments.Count - 1, accumulated);
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_segments.Count == 0 || DurationMs <= 0) return [];

        var ranges = new List<(double, double)>();

        lock (_bufferLock)
        {
            int? rangeStart = null;
            long startMs = 0;
            long currentMs = 0;

            for (int i = 0; i < _segments.Count; i++)
            {
                bool isDownloaded = _downloadedSegments.Contains(i);

                if (isDownloaded && !rangeStart.HasValue)
                {
                    rangeStart = i;
                    startMs = currentMs;
                }
                else if (!isDownloaded && rangeStart.HasValue)
                {
                    ranges.Add(ToNormalizedRange(startMs, currentMs));
                    rangeStart = null;
                }

                currentMs += (long)_segments[i].DurationMs;
            }

            if (rangeStart.HasValue)
            {
                ranges.Add(ToNormalizedRange(startMs, DurationMs));
            }
        }

        return ranges;
    }

    private (double Start, double End) ToNormalizedRange(long startMs, long endMs)
    {
        return ((double)startMs / DurationMs, (double)endMs / DurationMs);
    }

    public void ReleaseRamBuffers()
    {
        lock (_bufferLock)
        {
            _frameBuffer.Clear();
        }
    }

    public void CancelPendingOperations() => _operationCts?.Cancel();

    #region Playlist Parsing

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
                    return ResolveUrl(baseUrl, uriMatch.Groups[1].Value);
                }
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:") && i + 1 < lines.Length && !lines[i + 1].StartsWith("#"))
            {
                var bwMatch = BandwidthRegex().Match(line);
                int bw = bwMatch.Success ? int.Parse(bwMatch.Groups[1].Value) : 0;

                var url = lines[i + 1].Trim();
                if (bw > maxBandwidth || audioUrl == null)
                {
                    maxBandwidth = bw;
                    audioUrl = ResolveUrl(baseUrl, url);
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

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith("#EXTINF:"))
            {
                var durationStr = line[8..].Split(',')[0];
                duration = double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!line.StartsWith("#") && !string.IsNullOrEmpty(line))
            {
                var durationMs = duration * 1000;

                segments.Add(new HlsSegment(
                    Url: ResolveUrl(baseUrl, line),
                    DurationMs: durationMs,
                    StartMs: accumulatedMs));

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

        return new Uri(new Uri(baseUrl), relativeUrl).ToString();
    }

    private static string TruncateUrl(string url) => url[..Math.Min(60, url.Length)] + "...";

    [GeneratedRegex(@"URI=""([^""]+)""")]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"BANDWIDTH=(\d+)")]
    private static partial Regex BandwidthRegex();

    #endregion

    #region Segment Loading

    private async Task LoadSegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _segments.Count) return;

        lock (_bufferLock)
        {
            if (_downloadedSegments.Contains(index) || _loadingSegments.Contains(index))
                return;
            _loadingSegments.Add(index);
        }

        var segment = _segments[index];

        try
        {
            Log.Debug($"[HLS] Loading segment {index}");

            var data = await GetBytesWithErrorHandlingAsync(segment.Url, index, ct);
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

            Log.Debug($"[HLS] Segment {index}: {frames.Count} frames");
        }
        catch (StreamUnavailableException)
        {
            lock (_bufferLock) { _loadingSegments.Remove(index); }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_bufferLock) { _loadingSegments.Remove(index); }
            Log.Warn($"[HLS] Segment {index} failed: {ex.Message}");
            throw;
        }
    }

    private async Task<byte[]> GetBytesWithErrorHandlingAsync(string url, int segmentIndex, CancellationToken ct)
    {
        try
        {
            return await _httpClient.GetByteArrayAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Warn($"[HLS] Segment {segmentIndex} failed: 403 Forbidden");
            throw new StreamUnavailableException(
                $"HLS segment {segmentIndex} returned 403 Forbidden",
                _trackId ?? "unknown",
                StreamUnavailableReason.Forbidden403,
                httpStatusCode: 403,
                wasHlsFallback: true);
        }
    }

    private List<AacFrame> ExtractAacFramesFromTs(byte[] tsData, long baseTimestampMs)
    {
        var frames = new List<AacFrame>();
        int pos = 0;
        long currentMs = baseTimestampMs;

        while (pos + 188 <= tsData.Length)
        {
            if (tsData[pos] != 0x47)
            {
                pos++;
                continue;
            }

            var payload = ExtractTsPayload(tsData, pos);
            if (payload.Length > 0)
            {
                ExtractAdtsFrames(payload, ref currentMs, frames);
            }

            pos += 188;
        }

        return frames;
    }

    private static ReadOnlySpan<byte> ExtractTsPayload(byte[] tsData, int packetStart)
    {
        bool hasPayload = (tsData[packetStart + 3] & 0x10) != 0;
        bool hasAdaptation = (tsData[packetStart + 3] & 0x20) != 0;

        if (!hasPayload) return [];

        int payloadStart = packetStart + 4;
        if (hasAdaptation)
        {
            payloadStart += 1 + tsData[payloadStart];
        }

        if (payloadStart >= packetStart + 188) return [];

        return tsData.AsSpan(payloadStart, packetStart + 188 - payloadStart);
    }

    private void ExtractAdtsFrames(ReadOnlySpan<byte> payload, ref long currentMs, List<AacFrame> frames)
    {
        int pos = 0;

        while (pos + 7 <= payload.Length)
        {
            if (payload[pos] != 0xFF || (payload[pos + 1] & 0xF0) != 0xF0)
            {
                pos++;
                continue;
            }

            int frameLength = ((payload[pos + 3] & 0x03) << 11) |
                              (payload[pos + 4] << 3) |
                              ((payload[pos + 5] & 0xE0) >> 5);

            if (frameLength <= 0 || pos + frameLength > payload.Length)
            {
                pos++;
                continue;
            }

            if (DecoderConfig == null)
            {
                DecoderConfig = ExtractAscFromAdts(payload.Slice(pos, 7));
                ParseAscForSampleInfo();
            }

            int headerLen = (payload[pos + 1] & 0x01) == 0 ? 9 : 7;
            int dataLen = frameLength - headerLen;

            if (dataLen > 0 && pos + headerLen + dataLen <= payload.Length)
            {
                frames.Add(new AacFrame
                {
                    Data = payload.Slice(pos + headerLen, dataLen).ToArray(),
                    TimestampMs = currentMs,
                    DurationMs = HlsAacFrameDurationMs
                });

                currentMs += HlsAacFrameDurationMs;
            }

            pos += frameLength;
        }
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
        if (DecoderConfig == null || DecoderConfig.Length < 2) return;

        int asc = (DecoderConfig[0] << 8) | DecoderConfig[1];
        int sampleRateIndex = (asc >> 7) & 0x0F;
        int channelConfig = (asc >> 3) & 0x0F;

        SampleRate = GetAacSampleRate(sampleRateIndex);
        Channels = GetAacChannels(channelConfig);
    }

    #endregion

    #region Prefetch

    private async Task PrefetchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HlsPrefetchIntervalMs, ct);
                await PrefetchSegmentsAhead(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (StreamUnavailableException ex)
            {
                Log.Warn($"[HLS] Prefetch stopped due to 403: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[HLS] Prefetch error: {ex.Message}");
            }
        }
    }

    private async Task PrefetchSegmentsAhead(CancellationToken ct)
    {
        int current = _currentSegmentIndex;

        for (int i = 0; i < HlsPrefetchSegments && current + i < _segments.Count; i++)
        {
            int targetIndex = current + i;

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

    #endregion

    #region Dispose

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

    #endregion

    private sealed record HlsSegment(string Url, double DurationMs, long StartMs);
    private readonly record struct AacFrame(byte[] Data, long TimestampMs, int DurationMs);
}