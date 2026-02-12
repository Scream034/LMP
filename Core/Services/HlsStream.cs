// Core/Services/Streaming/HlsStream.cs

using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Services.Streaming;

/// <summary>
/// HLS → Stream адаптер с поддержкой отмены.
/// </summary>
public sealed partial class HlsStream : MediaStreamBase
{
    #region Constants

    private const int MaxConcurrentDownloads = 3;
    private const int PreloadSegmentCount = 3;
    private const int DownloadTimeoutMs = 30_000;
    private const int MaxRetries = 3;
    private const int ManifestRefreshIntervalMs = 55_000;
    private const int WaitPollIntervalMs = 50;

    #endregion

    #region Fields

    private readonly string _initialManifestUrl;
    private readonly Dictionary<string, string> _headers;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private readonly List<HlsSegment> _segments = [];
    private readonly ConcurrentDictionary<int, byte[]> _downloadedData = new();
    private readonly ConcurrentDictionary<int, Task> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Локальный сигнал для ожидания данных
    private readonly ManualResetEventSlim _dataAvailable = new(false);

    // Версионирование для отмены
    private volatile int _sessionId;
    private CancellationTokenSource _sessionCts;
    private readonly Lock _sessionLock = new();

    private string _baseUrl = "";
    private string _currentManifestUrl;
    private DateTime _manifestFetchedAt;
    private bool _initialized;
    private Task? _preloadTask;

    #endregion

    #region Segment

    private sealed class HlsSegment
    {
        public required int Index { get; init; }
        public required string Url { get; set; }
        public required double Duration { get; init; }
        public long StartOffset { get; set; }
        public long EstimatedSize { get; set; }
        public long ActualSize { get; set; }
        public long EffectiveSize => ActualSize > 0 ? ActualSize : EstimatedSize;
    }

    #endregion

    #region Properties

    public override double DownloadProgress =>
        _segments.Count == 0 ? 0 : Math.Min((double)_downloadedData.Count / _segments.Count * 100, 100);

    public override long BufferedBytes =>
        _downloadedData.Values.Sum(d => (long)d.Length);

    public override bool IsFullyDownloaded =>
        _segments.Count > 0 && _downloadedData.Count >= _segments.Count;

    #endregion

    #region Constructor

    public HlsStream(
        string manifestUrl,
        HttpClient http,
        Dictionary<string, string>? headers = null,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
        : base(http)
    {
        _initialManifestUrl = manifestUrl;
        _currentManifestUrl = manifestUrl;
        _headers = headers ?? new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            ["Referer"] = "https://www.youtube.com/",
            ["Origin"] = "https://www.youtube.com"
        };
        _urlRefresher = urlRefresher;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(DisposeCts.Token);

        ExtractBaseUrl(manifestUrl);
        Log.Debug($"[HLS] Created: {manifestUrl}");
    }

    #endregion

    #region Session Control

    private bool IsSessionValid(int expectedId)
    {
        return !IsDisposed && _sessionId == expectedId;
    }

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock)
        {
            return _sessionCts.Token;
        }
    }

    /// <summary>
    /// Отменяет все ожидающие операции.
    /// </summary>
    public override void CancelPendingReads()
    {
        lock (_sessionLock)
        {
            Interlocked.Increment(ref _sessionId);
            
            try
            {
                _sessionCts.Cancel();
                _sessionCts.Dispose();
            }
            catch { }
            
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(DisposeCts.Token);
            _dataAvailable.Set();
            _activeDownloads.Clear();
        }
    }

    #endregion

    #region Initialization

    public override async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return true;

            if (!await FetchAndParseManifestAsync(_currentManifestUrl, ct))
                return false;

            if (_segments.Count == 0)
            {
                Log.Error("[HLS] No segments");
                return false;
            }

            await ProbeSegmentSizesAsync(ct);
            RecalculateOffsets();

            _initialized = true;
            _manifestFetchedAt = DateTime.UtcNow;

            Log.Info($"[HLS] Ready: {_segments.Count} segments, ~{TotalLength / 1024}KB");

            _preloadTask = Task.Run(() => BackgroundPreloadAsync(GetSessionToken()), GetSessionToken());
            return true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public override async ValueTask<bool> PreBufferAsync(CancellationToken ct = default)
    {
        if (!await InitializeAsync(ct))
            return false;

        var sessionId = _sessionId;
        ScheduleDownload(0);

        var deadline = DateTime.UtcNow.AddMilliseconds(DownloadTimeoutMs);

        while (!HasSegment(0))
        {
            if (!IsSessionValid(sessionId) || ct.IsCancellationRequested)
                return false;
            
            if (DateTime.UtcNow > deadline)
                return false;

            _dataAvailable.Wait(100);
            _dataAvailable.Reset();
        }

        return true;
    }

    #endregion

    #region Manifest

    private void ExtractBaseUrl(string url)
    {
        int lastSlash = url.LastIndexOf('/');
        if (lastSlash > 0) _baseUrl = url[..(lastSlash + 1)];
    }

    private async Task<bool> FetchAndParseManifestAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url, _headers);
            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);

            if (content.Contains("#EXT-X-STREAM-INF"))
            {
                var variantUrl = SelectBestVariant(content);
                if (string.IsNullOrEmpty(variantUrl)) return false;

                variantUrl = MakeAbsoluteUrl(variantUrl);
                ExtractBaseUrl(variantUrl);
                return await FetchAndParseManifestAsync(variantUrl, ct);
            }

            ParseMediaPlaylist(content);
            return _segments.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Error($"[HLS] Manifest failed: {ex.Message}");
            return false;
        }
    }

    private string? SelectBestVariant(string manifest)
    {
        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? bestUrl = null;
        long bestBandwidth = long.MaxValue;

        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:")) continue;

            var match = BandwidthRegex().Match(line);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var bw)) continue;

            var next = lines[i + 1].Trim();
            if (next.StartsWith('#')) continue;

            if (bw < bestBandwidth)
            {
                bestBandwidth = bw;
                bestUrl = next;
            }
        }

        return bestUrl;
    }

    private void ParseMediaPlaylist(string manifest)
    {
        _segments.Clear();
        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        double duration = 0;
        int index = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith("#EXTINF:"))
            {
                var val = line[8..];
                int comma = val.IndexOf(',');
                if (comma > 0) val = val[..comma];
                double.TryParse(val, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out duration);
            }
            else if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
            {
                _segments.Add(new HlsSegment
                {
                    Index = index++,
                    Url = MakeAbsoluteUrl(line),
                    Duration = duration,
                    EstimatedSize = (long)(duration * 16_000)
                });
                duration = 0;
            }
        }
    }

    private string MakeAbsoluteUrl(string url) =>
        url.StartsWith("http://") || url.StartsWith("https://") ? url : _baseUrl + url;

    private async Task ProbeSegmentSizesAsync(CancellationToken ct)
    {
        var probes = _segments.Take(3).Select(s => ProbeOneAsync(s, ct));
        await Task.WhenAll(probes);

        var known = _segments.Where(s => s.ActualSize > 0).Select(s => s.ActualSize).ToList();
        if (known.Count > 0)
        {
            var avg = (long)known.Average();
            foreach (var s in _segments.Where(s => s.ActualSize == 0))
                s.EstimatedSize = avg;
        }
    }

    private async Task ProbeOneAsync(HlsSegment seg, CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Head, seg.Url, _headers);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.Content.Headers.ContentLength is { } len)
            {
                seg.ActualSize = len;
                seg.EstimatedSize = len;
            }
        }
        catch { }
    }

    private void RecalculateOffsets()
    {
        long offset = 0;
        foreach (var seg in _segments)
        {
            seg.StartOffset = offset;
            offset += seg.EffectiveSize;
        }
        TotalLength = offset;
    }

    [GeneratedRegex(@"BANDWIDTH=(\d+)")]
    private static partial Regex BandwidthRegex();

    #endregion

    #region Read

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (IsDisposed || !_initialized) return 0;

        int sessionId = _sessionId;

        long pos = CurrentPosition;
        if (pos >= TotalLength) return 0;

        count = (int)Math.Min(count, TotalLength - pos);
        if (count <= 0) return 0;

        int segIdx = FindSegmentAt(pos);
        if (segIdx < 0) return 0;

        var seg = _segments[segIdx];
        long offsetInSeg = pos - seg.StartOffset;

        if (!WaitForSegment(segIdx, sessionId)) return 0;
        if (!_downloadedData.TryGetValue(segIdx, out var data)) return 0;

        int available = (int)Math.Min(count, data.Length - offsetInSeg);
        if (available <= 0)
        {
            if (segIdx + 1 < _segments.Count)
            {
                CurrentPosition = _segments[segIdx + 1].StartOffset;
                return Read(buffer, offset, count);
            }
            return 0;
        }

        Buffer.BlockCopy(data, (int)offsetInSeg, buffer, offset, available);
        CurrentPosition = pos + available;

        for (int i = 1; i <= PreloadSegmentCount; i++)
            ScheduleDownload(segIdx + i);

        return available;
    }

    private bool WaitForSegment(int index, int sessionId)
    {
        if (HasSegment(index)) return true;

        ScheduleDownload(index);
        var deadline = DateTime.UtcNow.AddMilliseconds(DownloadTimeoutMs);

        while (!HasSegment(index))
        {
            // Проверяем сессию первым делом
            if (!IsSessionValid(sessionId)) return false;
            if (IsDisposed) return false;
            if (DateTime.UtcNow > deadline) return false;

            _dataAvailable.Wait(WaitPollIntervalMs);
            _dataAvailable.Reset();
        }

        return IsSessionValid(sessionId);
    }

    /// <summary>
    /// Вызывается при seek (позиция в миллисекундах для HLS конвертируется в байты).
    /// </summary>
    protected override void OnSeekInternal(long positionMs)
    {
        // Для HLS конвертируем время в байтовую позицию
        if (_segments.Count == 0) return;

        double totalDur = _segments.Sum(s => s.Duration);
        if (totalDur <= 0) return;

        double progress = positionMs / 1000.0 / totalDur;
        long bytePos = (long)(progress * TotalLength);
        bytePos = Math.Clamp(bytePos, 0, TotalLength);
        
        CurrentPosition = bytePos;
        
        int idx = FindSegmentAt(bytePos);
        for (int i = 0; i <= PreloadSegmentCount; i++)
            ScheduleDownload(idx + i);
    }

    #endregion

    #region Download

    private bool HasSegment(int i) => _downloadedData.ContainsKey(i);

    private int FindSegmentAt(long pos)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if (pos >= s.StartOffset && pos < s.StartOffset + s.EffectiveSize)
                return i;
        }
        return _segments.Count - 1;
    }

    private void ScheduleDownload(int index)
    {
        if (index < 0 || index >= _segments.Count) return;
        if (HasSegment(index) || _activeDownloads.ContainsKey(index)) return;

        var ct = GetSessionToken();
        var task = Task.Run(() => DownloadSegmentAsync(index, ct), ct);
        _activeDownloads.TryAdd(index, task);
    }

    private async Task DownloadSegmentAsync(int index, CancellationToken ct)
    {
        if (HasSegment(index))
        {
            _activeDownloads.TryRemove(index, out _);
            return;
        }

        bool gotSlot = false;
        try
        {
            gotSlot = await _downloadSlots.WaitAsync(1000, ct);
            if (!gotSlot)
            {
                _activeDownloads.TryRemove(index, out _);
                return;
            }

            if (HasSegment(index) || ct.IsCancellationRequested) return;

            var seg = _segments[index];

            for (int retry = 0; retry <= MaxRetries; retry++)
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    // Проверяем свежесть URL
                    if (DateTime.UtcNow - _manifestFetchedAt > TimeSpan.FromMilliseconds(ManifestRefreshIntervalMs))
                        await RefreshManifestAsync(ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(DownloadTimeoutMs);

                    using var request = CreateRequest(HttpMethod.Get, seg.Url, _headers);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Log.Warn($"[HLS] Segment {index} got 403");
                        await RefreshManifestAsync(ct);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    if (ct.IsCancellationRequested) return;

                    var data = await response.Content.ReadAsByteArrayAsync(cts.Token);

                    if (ct.IsCancellationRequested) return;

                    _downloadedData[index] = data;

                    if (seg.ActualSize != data.Length)
                    {
                        seg.ActualSize = data.Length;
                        RecalculateOffsetsFrom(index);
                    }

                    _dataAvailable.Set();
                    Log.Debug($"[HLS] Segment {index}: {data.Length} bytes");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[HLS] Segment {index} got 403, refreshing...");
                    await RefreshManifestAsync(ct);
                }
                catch (Exception ex) when (retry < MaxRetries && !ct.IsCancellationRequested)
                {
                    Log.Warn($"[HLS] Segment {index} retry {retry + 1}: {ex.Message}");
                    try { await Task.Delay(500 * (retry + 1), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальная отмена
        }
        finally
        {
            _activeDownloads.TryRemove(index, out _);
            if (gotSlot)
            {
                try { _downloadSlots.Release(); } catch { }
            }
        }
    }

    private async Task RefreshManifestAsync(CancellationToken ct)
    {
        try
        {
            string? newUrl = null;

            if (_urlRefresher != null)
                newUrl = await _urlRefresher(ct);

            newUrl ??= _initialManifestUrl;

            ExtractBaseUrl(newUrl);

            int oldCount = _segments.Count;
            if (await FetchAndParseManifestAsync(newUrl, ct) && _segments.Count == oldCount)
            {
                _manifestFetchedAt = DateTime.UtcNow;
                _currentManifestUrl = newUrl;
                Log.Debug("[HLS] Manifest refreshed");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[HLS] Refresh failed: {ex.Message}");
        }
    }

    private void RecalculateOffsetsFrom(int from)
    {
        if (from >= _segments.Count) return;

        long offset = from > 0
            ? _segments[from - 1].StartOffset + _segments[from - 1].EffectiveSize
            : 0;

        for (int i = from; i < _segments.Count; i++)
        {
            _segments[i].StartOffset = offset;
            offset += _segments[i].EffectiveSize;
        }

        TotalLength = offset;
    }

    private async Task BackgroundPreloadAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused)
                {
                    int cur = FindSegmentAt(CurrentPosition);
                    for (int i = 0; i <= PreloadSegmentCount; i++)
                        ScheduleDownload(cur + i);
                }

                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"[HLS] Preload error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Buffered Ranges

    public override IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_segments.Count == 0 || TotalLength == 0) return [];

        var ranges = new List<(double, double)>();
        int? start = null, end = null;

        for (int i = 0; i < _segments.Count; i++)
        {
            if (HasSegment(i))
            {
                start ??= i;
                end = i;
            }
            else if (start != null)
            {
                AddRange(start.Value, end!.Value);
                start = null;
            }
        }

        if (start != null) AddRange(start.Value, end!.Value);

        return ranges;

        void AddRange(int s, int e)
        {
            double sp = (double)_segments[s].StartOffset / TotalLength;
            double ep = (double)(_segments[e].StartOffset + _segments[e].EffectiveSize) / TotalLength;
            ranges.Add((sp, Math.Min(ep, 1.0)));
        }
    }

    #endregion

    #region Seek by Time (public convenience)

    /// <summary>
    /// Seek по времени в миллисекундах (публичный метод).
    /// </summary>
    public void SeekToTime(long milliseconds)
    {
        if (_segments.Count == 0) return;

        double totalDur = _segments.Sum(s => s.Duration);
        if (totalDur <= 0) return;

        double progress = milliseconds / 1000.0 / totalDur;
        Seek((long)(progress * TotalLength), SeekOrigin.Begin);
    }

    #endregion

    #region Dispose

    protected override void OnDispose()
    {
        // Отменяем все операции
        lock (_sessionLock)
        {
            try { _sessionCts.Cancel(); } catch { }
            try { _sessionCts.Dispose(); } catch { }
        }

        try { _preloadTask?.Wait(500); } catch { }

        _downloadedData.Clear();
        
        try { _downloadSlots.Dispose(); } catch { }
        try { _initLock.Dispose(); } catch { }
        try { _dataAvailable.Dispose(); } catch { }

        Log.Debug("[HLS] Disposed");
    }

    #endregion
}