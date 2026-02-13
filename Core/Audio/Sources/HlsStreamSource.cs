using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Logger;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// HLS источник для AAC аудио.
/// Парсит m3u8, загружает TS сегменты, извлекает AAC фреймы.
/// </summary>
public sealed partial class HlsStreamSource : IAudioSource
{
    private readonly string _masterUrl;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private string _currentPlaylistUrl = string.Empty;
    private List<HlsSegment> _segments = [];
    private int _currentSegmentIndex;
    private Queue<AacFrame> _frameBuffer = new();

    private byte[]? _audioSpecificConfig;
    private long _durationMs = -1;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;

    private CancellationTokenSource? _downloadCts;
    private Task? _prefetchTask;

    // Буферизация
    private readonly int _prefetchSegments;
    private readonly HashSet<int> _downloadedSegments = [];
    private readonly object _segmentLock = new();

    public HlsStreamSource(
        string masterUrl,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        int prefetchSegments = 3)
    {
        _masterUrl = masterUrl ?? throw new ArgumentNullException(nameof(masterUrl));
        _httpClient = httpClient ?? CreateDefaultClient();
        _urlRefresher = urlRefresher;
        _prefetchSegments = prefetchSegments;
    }

    public long DurationMs => _durationMs;
    public long PositionMs => _positionMs;
    public bool CanSeek => _segments.Count > 0;
    public AudioCodec Codec => AudioCodec.Aac;
    public double BufferProgress => _segments.Count > 0
        ? (double)_downloadedSegments.Count / _segments.Count * 100
        : 0;
    public bool IsFullyBuffered => _downloadedSegments.Count >= _segments.Count;

    /// <summary>
    /// Возвращает Audio Specific Config для инициализации AacDecoder.
    /// </summary>
    public byte[]? AudioSpecificConfig => _audioSpecificConfig;

    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            Log.Debug($"[HLS] Initializing: {_masterUrl[..Math.Min(60, _masterUrl.Length)]}...");

            // Загружаем master playlist
            var masterContent = await _httpClient.GetStringAsync(_masterUrl, ct);

            // Парсим и выбираем лучший audio-only вариант
            var audioPlaylistUrl = ParseMasterPlaylist(masterContent, _masterUrl);

            if (string.IsNullOrEmpty(audioPlaylistUrl))
            {
                // Возможно это уже media playlist
                audioPlaylistUrl = _masterUrl;
            }

            _currentPlaylistUrl = audioPlaylistUrl;

            // Загружаем media playlist
            var mediaContent = await _httpClient.GetStringAsync(audioPlaylistUrl, ct);
            _segments = ParseMediaPlaylist(mediaContent, audioPlaylistUrl);

            if (_segments.Count == 0)
                throw new AudioSourceException("No segments found in HLS playlist");

            // Считаем общую длительность
            _durationMs = (long)_segments.Sum(s => s.DurationMs);

            // Загружаем первый сегмент для получения ASC
            await LoadSegmentAsync(0, ct);

            _initialized = true;

            // Запускаем prefetch
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _prefetchTask = Task.Run(() => PrefetchLoopAsync(_downloadCts.Token));

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

        // Берём фрейм из буфера
        while (_frameBuffer.Count == 0)
        {
            if (_currentSegmentIndex >= _segments.Count)
                return null; // End of stream

            // Загружаем следующий сегмент если нужно
            if (!_downloadedSegments.Contains(_currentSegmentIndex))
            {
                await LoadSegmentAsync(_currentSegmentIndex, ct);
            }

            if (_frameBuffer.Count == 0)
                _currentSegmentIndex++;
        }

        var frame = _frameBuffer.Dequeue();
        _positionMs = frame.TimestampMs;

        return new AudioFrame
        {
            Data = frame.Data,
            TimestampMs = frame.TimestampMs,
            DurationMs = frame.DurationMs,
            IsKeyFrame = true
        };
    }

    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_segments.Count == 0) return false;

        // Находим сегмент по времени
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

        _frameBuffer.Clear();
        _currentSegmentIndex = targetSegment;
        _positionMs = accumulated;

        // Загружаем сегмент
        if (!_downloadedSegments.Contains(targetSegment))
        {
            await LoadSegmentAsync(targetSegment, ct);
        }

        return true;
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_segments.Count == 0 || _durationMs <= 0) return [];

        var ranges = new List<(double, double)>();
        int? rangeStart = null;
        long startMs = 0;
        long currentMs = 0;

        for (int i = 0; i < _segments.Count; i++)
        {
            if (_downloadedSegments.Contains(i))
            {
                rangeStart ??= i;
                startMs = rangeStart == i ? currentMs : startMs;
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

        return ranges;
    }

    public void ReleaseRamBuffers()
    {
        _frameBuffer.Clear();
    }

    public void CancelPendingOperations()
    {
        _downloadCts?.Cancel();
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

            // Ищем аудио-only стримы
            if (line.StartsWith("#EXT-X-MEDIA:") && line.Contains("TYPE=AUDIO"))
            {
                var uriMatch = UriRegex().Match(line);
                if (uriMatch.Success)
                {
                    audioUrl = ResolveUrl(baseUrl, uriMatch.Groups[1].Value);
                }
            }

            // Или берём первый stream с аудио
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
        if (_downloadedSegments.Contains(index)) return;

        var segment = _segments[index];

        try
        {
            Log.Debug($"[HLS] Loading segment {index}: {segment.Url[..Math.Min(50, segment.Url.Length)]}...");

            var data = await _httpClient.GetByteArrayAsync(segment.Url, ct);

            // Извлекаем AAC фреймы из TS
            var frames = ExtractAacFramesFromTs(data, segment.StartMs);

            lock (_segmentLock)
            {
                foreach (var frame in frames)
                    _frameBuffer.Enqueue(frame);

                _downloadedSegments.Add(index);
            }

            Log.Debug($"[HLS] Segment {index}: {frames.Count} frames extracted");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"[HLS] Segment {index} failed: {ex.Message}");
            throw;
        }
    }

    private List<AacFrame> ExtractAacFramesFromTs(byte[] tsData, long baseTimestampMs)
    {
        var frames = new List<AacFrame>();
        int pos = 0;
        long currentMs = baseTimestampMs;
        const int frameDurationMs = 23; // ~1024 samples @ 44.1kHz

        while (pos + 188 <= tsData.Length)
        {
            // TS packet = 188 bytes, sync byte = 0x47
            if (tsData[pos] != 0x47)
            {
                pos++;
                continue;
            }

            // Parse TS header
            int pid = ((tsData[pos + 1] & 0x1F) << 8) | tsData[pos + 2];
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
                // Ищем ADTS header в payload
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
                            // Извлекаем ASC из ADTS если ещё нет
                            if (_audioSpecificConfig == null)
                            {
                                _audioSpecificConfig = ExtractDsiFromAdts(payload.Slice(adtsPos, 7));
                            }

                            // AAC raw data (без ADTS header)
                            int headerLen = (payload[adtsPos + 1] & 0x01) == 0 ? 9 : 7;
                            int dataLen = frameLength - headerLen;

                            if (dataLen > 0 && adtsPos + headerLen + dataLen <= payload.Length)
                            {
                                var frameData = payload.Slice(adtsPos + headerLen, dataLen).ToArray();

                                frames.Add(new AacFrame
                                {
                                    Data = frameData,
                                    TimestampMs = currentMs,
                                    DurationMs = frameDurationMs
                                });

                                currentMs += frameDurationMs;
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

    private static byte[] ExtractDsiFromAdts(ReadOnlySpan<byte> adtsHeader)
    {
        // Extract profile, sample rate index, channels from ADTS header
        // ADTS header format:
        // Byte 2: [profile:2][sample_rate_index:4][private:1][channel_config_high:1]
        // Byte 3: [channel_config_low:2][original:1][home:1][...]

        int profile = ((adtsHeader[2] & 0xC0) >> 6) + 1; // Object type = profile + 1
        int sampleRateIndex = (adtsHeader[2] & 0x3C) >> 2;
        int channelConfig = ((adtsHeader[2] & 0x01) << 2) | ((adtsHeader[3] & 0xC0) >> 6);

        // Build AudioSpecificConfig (ISO 14496-3)
        // 5 bits: object type
        // 4 bits: sample rate index  
        // 4 bits: channel configuration
        // = 13 bits, padded to 2 bytes

        int asc = (profile << 11) | (sampleRateIndex << 7) | (channelConfig << 3);

        return
        [
            (byte)(asc >> 8),
            (byte)(asc & 0xFF)
        ];
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

                // Prefetch segments ahead
                for (int i = 0; i < _prefetchSegments; i++)
                {
                    int targetIndex = _currentSegmentIndex + i;

                    if (targetIndex >= _segments.Count) break;
                    if (_downloadedSegments.Contains(targetIndex)) continue;

                    await LoadSegmentAsync(targetIndex, ct);
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

    private static HttpClient CreateDefaultClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 4
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _frameBuffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _downloadCts?.Cancel();

        if (_prefetchTask != null)
        {
            try { await _prefetchTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { /* ignore */ }
        }

        _downloadCts?.Dispose();
        _frameBuffer.Clear();
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