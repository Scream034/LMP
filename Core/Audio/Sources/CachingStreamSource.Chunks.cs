using System.Buffers;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Nested Types

    private enum RangeDownloadResult
    {
        Success, Forbidden403, NetworkError, Fatal, Cancelled, SlotTimeout, OutOfRange
    }

    internal sealed class RamRangeBlock : IDisposable
    {
        private IMemoryOwner<byte>? _owner;
        private int _disposed;
        public long StartOffset { get; }
        public long EndOffsetExclusive => StartOffset + Length;
        public int Length { get; }
        public Memory<byte> Memory { get; }

        public RamRangeBlock(long startOffset, IMemoryOwner<byte> owner, int actualLength)
        {
            StartOffset = startOffset;
            _owner = owner;
            Length = actualLength;
            Memory = owner.Memory[..actualLength];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner?.Dispose();
            _owner = null;
        }
    }

    internal sealed class SlidingRamCache : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<RamRangeBlock> _blocks = new(32);
        private long _totalBytes;

        public long TotalBytes
        {
            get { lock (_lock) return _totalBytes; }
        }

        public bool TryAdd(RamRangeBlock block)
        {
            lock (_lock)
            {
                int insertAt = _blocks.Count;
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive <= block.StartOffset) continue;
                    if (block.EndOffsetExclusive <= current.StartOffset) { insertAt = i; break; }
                    return false;
                }
                _blocks.Insert(insertAt, block);
                _totalBytes += block.Length;
                return true;
            }
        }

        public bool TryRead(long position, Memory<byte> destination, out int read)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    int offsetInBlock = (int)(position - current.StartOffset);
                    read = Math.Min(destination.Length, current.Length - offsetInBlock);
                    if (read <= 0) { read = 0; return false; }
                    current.Memory.Span.Slice(offsetInBlock, read).CopyTo(destination.Span);
                    return true;
                }
            }
            read = 0; return false;
        }

        public bool ContainsRange(long startOffset, int length)
        {
            long endExclusive = startOffset + length;
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > startOffset) break;
                    if (current.EndOffsetExclusive <= startOffset) continue;
                    return current.StartOffset <= startOffset && current.EndOffsetExclusive >= endExclusive;
                }
            }
            return false;
        }

        public long GetContiguousBytesFrom(long position)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    return current.EndOffsetExclusive - position;
                }
            }
            return 0;
        }

        public bool TryRemoveContaining(long position, out RamRangeBlock? block)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    _blocks.RemoveAt(i);
                    _totalBytes -= current.Length;
                    block = current;
                    return true;
                }
            }
            block = null; return false;
        }

        public RamRangeBlock[] GetRangesSnapshot()
        {
            lock (_lock) { return _blocks.Count == 0 ? [] : _blocks.ToArray(); }
        }

        public void Trim(long centerOffset, long evictionWindowBytes, long maxRamBytes)
        {
            lock (_lock)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive < centerOffset - evictionWindowBytes || current.StartOffset > centerOffset + evictionWindowBytes)
                    {
                        _blocks.RemoveAt(i);
                        _totalBytes -= current.Length;
                        current.Dispose();
                    }
                }
                while (_totalBytes > maxRamBytes && _blocks.Count > 0)
                {
                    int removeIndex = ChooseFarthestIndex(centerOffset);
                    var removed = _blocks[removeIndex];
                    _blocks.RemoveAt(removeIndex);
                    _totalBytes -= removed.Length;
                    removed.Dispose();
                }
            }
        }

        public void DisposeAll()
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++) _blocks[i].Dispose();
                _blocks.Clear();
                _totalBytes = 0;
            }
        }

        public void Dispose() => DisposeAll();

        private int ChooseFarthestIndex(long centerOffset)
        {
            int chosen = 0;
            long farthestDistance = long.MinValue;
            for (int i = 0; i < _blocks.Count; i++)
            {
                long blockCenter = _blocks[i].StartOffset + (_blocks[i].Length >> 1);
                long distance = blockCenter >= centerOffset ? blockCenter - centerOffset : centerOffset - blockCenter;
                if (distance > farthestDistance) { farthestDistance = distance; chosen = i; }
            }
            return chosen;
        }
    }

    private sealed class ActiveRangeDownload
    {
        public long Start { get; }
        public int Length { get; }
        public long EndExclusive => Start + Length;
        public Lazy<Task<RangeDownloadResult>> LazyTask { get; }

        public ActiveRangeDownload(long start, int length, Lazy<Task<RangeDownloadResult>> lazyTask)
        {
            Start = start;
            Length = length;
            LazyTask = lazyTask;
        }
    }

    private readonly record struct DownloadPlan(long Start, int Length);

    #endregion

    #region Adaptive Metrics

    private void SaveLatency(double latencyMs)
    {
        double currentAverage;
        lock (_latencyLock)
        {
            _latency2 = _latency1; _latency1 = _latency0; _latency0 = latencyMs;
            currentAverage = GetAverageLatencyInternal();
        }
        Log.Debug($"[CachingSource] Latency: {latencyMs:F1}ms (Average Trend: {currentAverage:F1}ms)");
    }

    private void SaveBandwidth(double speedBytesPerSec)
    {
        double currentSpeed;
        lock (_latencyLock)
        {
            if (_estimatedBandwidthBytesPerSec <= 0) _estimatedBandwidthBytesPerSec = speedBytesPerSec;
            else _estimatedBandwidthBytesPerSec = (0.3 * speedBytesPerSec) + (0.7 * _estimatedBandwidthBytesPerSec);
            currentSpeed = _estimatedBandwidthBytesPerSec;
        }
        double speedMbps = (currentSpeed * 8) / 1000_000.0;
        Log.Debug($"[CachingSource] Throughput: {speedMbps:F2} Mbps ({currentSpeed / 1024.0:F1} KB/s)");
    }

    private double GetAverageLatencyInternal()
    {
        if (_latency0 <= 0) return 0;
        if (_latency1 <= 0) return _latency0;
        if (_latency2 <= 0) return (_latency0 + _latency1) / 2.0;
        return (_latency0 + _latency1 + _latency2) / 3.0;
    }

    private double GetThrottleTargetBytesPerSec()
    {
        double multiplier = _config.ThrottleMultiplier;
        if (multiplier <= 0) return 0;
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return bitrateBytesPerSec * multiplier;
    }

    private int ComputePacingDelayMs(int downloadedBytes, double elapsedMs)
    {
        double targetBps = GetThrottleTargetBytesPerSec();
        if (targetBps <= 0) return 0;
        double targetMs = (downloadedBytes / targetBps) * 1000.0;
        int delayMs = (int)(targetMs - elapsedMs);
        return delayMs > 10 ? delayMs : 0;
    }

    #endregion

    #region Range Planning

    private DownloadPlan BuildDownloadPlan(long requestedPosition, int minimumLength, bool isCritical)
    {
        long start = AlignDown(requestedPosition, _requestAlignmentBytes);

        long localAvailable = GetBufferedBytesAhead(start);
        if (localAvailable > 0)
        {
            long adjustedStart = AlignUp(start + localAvailable, _requestAlignmentBytes);
            if (adjustedStart < _contentLength && adjustedStart <= requestedPosition + minimumLength)
                start = adjustedStart;
        }

        int minLengthAligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);

        long currentPosition = Volatile.Read(ref _currentReadOffset);
        if (currentPosition < 0 || currentPosition >= _contentLength)
            currentPosition = requestedPosition;

        long bufferedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentPosition);
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        double currentBufferSec = bufferedAheadBytes / bitrateBytesPerSec;

        int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
        double targetBufferSec = adaptiveTargetBufferMs / 1000.0;
        double demandBytes = bitrateBytesPerSec * Math.Max(0, targetBufferSec - currentBufferSec);

        double avgLatencyMs;
        double estimatedBandwidth;
        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            estimatedBandwidth = _estimatedBandwidthBytesPerSec;
        }

        var degradation = GetNetworkDegradationLevel();
        int effectiveMaxLength = _config.MaxRequestSizeBytes;

        if (!isCritical)
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 2),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                _ => effectiveMaxLength
            };
        }
        else
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 6),
                _ => effectiveMaxLength
            };
        }

        double throttleBps = GetThrottleTargetBytesPerSec();
        if (throttleBps > 0)
        {
            double pacingWindow = Math.Max(1.0, _config.PreloadIntervalMs / 1000.0);
            int throttleCap = AlignUp((int)(throttleBps * pacingWindow), _requestAlignmentBytes);
            effectiveMaxLength = Math.Min(throttleCap, effectiveMaxLength);
        }

        int maxLength = Math.Max(minLengthAligned, effectiveMaxLength);

        double safeBytes = 0;
        if (estimatedBandwidth > 0)
        {
            double timeLeftSec = Math.Max(0.050, currentBufferSec - (avgLatencyMs / 1000.0));
            safeBytes = estimatedBandwidth * timeLeftSec;
        }

        long selectedBytes;
        if (safeBytes > 0 && demandBytes > 0) selectedBytes = (long)Math.Min(demandBytes, safeBytes);
        else if (demandBytes > 0) selectedBytes = (long)demandBytes;
        else if (safeBytes > 0) selectedBytes = (long)safeBytes;
        else selectedBytes = minLengthAligned;

        if (selectedBytes < minLengthAligned) selectedBytes = minLengthAligned;

        if (estimatedBandwidth > 0 && avgLatencyMs > 600)
        {
            double latencySec = avgLatencyMs / 1000.0;
            long bdpBytes = (long)(estimatedBandwidth * latencySec);
            long bdpFloor = AlignUp(Math.Max(minLengthAligned, bdpBytes * 2), _requestAlignmentBytes);
            if (bdpFloor > selectedBytes) selectedBytes = bdpFloor;
        }

        int bufferedAheadMs = ConvertBufferedBytesToMs(bufferedAheadBytes);
        bool lowBuffer = bufferedAheadMs < CriticalRefillBufferMs;

        if (!isCritical && !lowBuffer && avgLatencyMs < 800 && demandBytes > 0)
        {
            long demandAligned = AlignUp((long)demandBytes, _requestAlignmentBytes);
            long softCap = demandAligned + _requestAlignmentBytes;
            if (selectedBytes > softCap) selectedBytes = softCap;
        }

        if (isCritical || lowBuffer)
        {
            long criticalFloor = minLengthAligned;
            if (avgLatencyMs > 1200) criticalFloor = Math.Max(criticalFloor, Math.Min(maxLength, minLengthAligned * 2L));
            if (selectedBytes < criticalFloor) selectedBytes = criticalFloor;
        }

        if (selectedBytes > maxLength) selectedBytes = maxLength;
        selectedBytes = AlignUp(selectedBytes, _requestAlignmentBytes);

        long remaining = _contentLength - start;
        if (remaining <= 0) return new DownloadPlan(start, 0);

        if (selectedBytes > remaining) selectedBytes = remaining;
        if (selectedBytes < minimumLength) selectedBytes = Math.Min(AlignUp(minimumLength, _requestAlignmentBytes), remaining);

        int plannedLength = (int)selectedBytes;

        // если хвост планируемого aligned range уже есть локально / in-flight,
        // скачиваем только первый missing gap, а не весь chunk целиком.
        // Иначе RAM cache отвергнет overlap, и успешно скачанные байты исчезнут.
        int trimmedLength = TrimLengthToFirstKnownCoverage(start, plannedLength, includeInflight: true);
        if (trimmedLength > 0 && trimmedLength < plannedLength)
        {
            plannedLength = trimmedLength;
        }

        return new DownloadPlan(start, plannedLength);
    }

    private long GetBufferedBytesAhead(long position)
    {
        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        return ramBytes >= diskBytes ? ramBytes : diskBytes;
    }

    private long GetBufferedBytesAheadIncludingInflight(long position)
    {
        long localBuffered = GetBufferedBytesAhead(position);
        long endExclusive = position + localBuffered;
        if (endExclusive >= _contentLength) return _contentLength - position;

        bool extended;
        do
        {
            extended = false;
            lock (_activeDownloadsLock)
            {
                foreach (var active in _activeDownloads.Values)
                {
                    if (active.Start > endExclusive) continue;
                    if (active.EndExclusive <= endExclusive) continue;
                    if (active.EndExclusive <= position) continue;
                    endExclusive = active.EndExclusive;
                    extended = true;
                }
            }
        }
        while (extended && endExclusive < _contentLength);

        return endExclusive - position;
    }

    private bool IsRangeLocallyAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (position < 0 || position >= _contentLength) return false;
        if (_ramCache.ContainsRange(position, length)) return true;
        return _cacheEntry?.IsRangeDownloaded(position, length) == true;
    }

    private int GetAlignedReadLength(long position, int minimumLength)
    {
        if (position >= _contentLength) return 0;
        long remaining = _contentLength - position;
        int aligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        if (aligned > remaining) aligned = (int)remaining;
        return aligned;
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
    private static int AlignUp(int value, int alignment) { int remainder = value % alignment; return remainder == 0 ? value : value + (alignment - remainder); }
    private static long AlignUp(long value, int alignment) { long remainder = value % alignment; return remainder == 0 ? value : value + (alignment - remainder); }

    #endregion

    #region ReadAtAsync

    /// <summary>
    /// Создаёт фатальное исключение для диапазона, который source не смог получить
    /// после исчерпания всех локальных и сетевых стратегий.
    /// </summary>
    /// <param name="position">Позиция чтения, на которой зафиксирован terminal failure.</param>
    /// <returns>
    /// Экземпляр <see cref="ChunkDownloadFatalException"/>, сигнализирующий верхнему слою,
    /// что повторять чтение этого же диапазона на уровне decoder loop больше нельзя.
    /// </returns>
    private ChunkDownloadFatalException CreateReadAtFatalException(long position)
    {
        long alignedStart = AlignDown(position, _requestAlignmentBytes);
        int logicalIndex = (int)(alignedStart / _requestAlignmentBytes);
        int consecutive403 = Volatile.Read(ref _consecutive403Count);

        Exception inner = _lastDownloadException
            ?? new IOException($"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries");

        return new ChunkDownloadFatalException(
            message: $"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries",
            chunkIndex: logicalIndex,
            consecutiveFailures: consecutive403,
            reason: ChunkDownloadFailureReason.MaxRetriesExceeded,
            trackId: _trackId,
            httpStatusCode: null,
            innerException: inner);
    }

    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;
        int requiredLength = (int)Math.Min(buffer.Length, _contentLength - position);

        if (_ramCache.TryRead(position, buffer, out int ramRead)) return ramRead;

        int diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct).ConfigureAwait(false);
        if (diskRead > 0) return diskRead;

        for (int attempt = 0; attempt < ReadAtMaxEpochRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);

                var result = await EnsureRangeAsync(position, requiredLength, linkedCts.Token, isCritical: true)
                    .ConfigureAwait(false);

                if (result == RangeDownloadResult.OutOfRange) return 0;

                if (_ramCache.TryRead(position, buffer, out ramRead)) return ramRead;

                diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct).ConfigureAwait(false);
                if (diskRead > 0) return diskRead;
            }
            catch (ChunkDownloadFatalException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Debug($"[CachingSource] ReadAt at {position}: epoch changed, retry {attempt + 1}/{ReadAtMaxEpochRetries}");
                await Task.Delay(ReadAtEpochRetryDelayMs, ct).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw CreateReadAtFatalException(position);
    }

    private async Task<int> TryLoadRangeFromDiskAsync(long position, int minimumLength, Memory<byte> target, CancellationToken ct)
    {
        if (_cacheEntry == null) return 0;
        if (!_cacheEntry.TryGetContainingRange(position, out long rangeStart, out long rangeEndExclusive)) return 0;

        long loadStart = AlignDown(position, _requestAlignmentBytes);
        if (loadStart < rangeStart) loadStart = rangeStart;

        int desiredLength = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        long available = rangeEndExclusive - loadStart;
        if (available <= 0) return 0;
        if (desiredLength > available) desiredLength = (int)available;

        var diskResult = await _cacheManager.ReadRangeAsync(_cacheKey, loadStart, desiredLength, ct).ConfigureAwait(false);
        if (!diskResult.HasValue) return 0;

        var (owner, length) = diskResult.Value;
        var block = new RamRangeBlock(loadStart, owner, length);
        int copied = CopyFromBlock(block.Memory.Span, (int)(position - loadStart), target);

        if (!_ramCache.TryAdd(block)) block.Dispose();
        return copied;
    }

    private static int CopyFromBlock(ReadOnlySpan<byte> blockData, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, blockData.Length - offset);
        if (available <= 0) return 0;
        blockData.Slice(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region Active Download Coordination

    private int GetActiveDownloadCount() { lock (_activeDownloadsLock) return _activeDownloads.Count; }

    /// <summary>
    /// Строгая защита от пересекающихся (overlapping) загрузок. 
    /// Это критически важно на слабой сети, чтобы не качать одни и те же байты дважды.
    /// </summary>
    private bool TryGetOverlappingActiveDownload(long start, int length, out ActiveRangeDownload? active)
    {
        long endExclusive = start + length;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (start < current.EndExclusive && endExclusive > current.Start)
                {
                    active = current;
                    return true;
                }
            }
        }
        active = null; return false;
    }

    private ActiveRangeDownload RegisterOrGetActiveDownload(ActiveRangeDownload candidate)
    {
        long candidateEnd = candidate.EndExclusive;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (candidate.Start < current.EndExclusive && candidateEnd > current.Start)
                    return current;
            }

            if (_activeDownloads.TryGetValue(candidate.Start, out var sameStart))
                return sameStart;

            _activeDownloads.Add(candidate.Start, candidate);
            return candidate;
        }
    }

    private void RemoveActiveDownloadIfOwner(long key, ActiveRangeDownload owner)
    {
        lock (_activeDownloadsLock)
        {
            if (_activeDownloads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                _activeDownloads.Remove(key);
        }
    }

    private static async Task WaitForActiveDownloadAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChunkDownloadFatalException) { throw; }
        catch { }
    }

    #endregion

    #region EnsureRangeAsync

    /// <summary>
    /// Гарантирует наличие диапазона данных, предотвращая бесконечные ретраи (Retry Storm).
    /// </summary>
    private async Task<RangeDownloadResult> EnsureRangeAsync(long position, int minimumLength, CancellationToken ct, bool isCritical = false)
    {
        if (position < 0 || position >= _contentLength) return RangeDownloadResult.OutOfRange;
        if (minimumLength <= 0) return RangeDownloadResult.Success;

        minimumLength = (int)Math.Min(minimumLength, _contentLength - position);
        long alignedStart = AlignDown(position, _requestAlignmentBytes);

        if (IsRangeLocallyAvailable(position, minimumLength)) return RangeDownloadResult.Success;

        ct.ThrowIfCancellationRequested();

        // Стандарт: максимум ретраев на один конкретный чанк
        int maxAttempts = _config.MaxNetworkRetries;
        int chunkIoExceptions = 0;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (IsRangeLocallyAvailable(position, minimumLength)) return RangeDownloadResult.Success;

            if (TryGetOverlappingActiveDownload(position, minimumLength, out var overlapping))
            {
                await WaitForActiveDownloadAsync(overlapping!.LazyTask.Value, ct).ConfigureAwait(false);
                continue;
            }

            var plan = BuildDownloadPlan(position, minimumLength, isCritical);
            var ownerLazy = new Lazy<Task<RangeDownloadResult>>(() => DownloadRangeCoreAsync(plan, ct, isCritical), LazyThreadSafetyMode.ExecutionAndPublication);
            var candidate = new ActiveRangeDownload(plan.Start, plan.Length, ownerLazy);
            var actual = RegisterOrGetActiveDownload(candidate);

            if (ReferenceEquals(actual, candidate))
            {
                RangeDownloadResult result;
                try
                {
                    result = await actual.LazyTask.Value.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                finally { RemoveActiveDownloadIfOwner(plan.Start, actual); }

                switch (result)
                {
                    case RangeDownloadResult.Success:
                        return RangeDownloadResult.Success;

                    case RangeDownloadResult.Forbidden403:
                        await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
                        continue;

                    case RangeDownloadResult.NetworkError:
                        chunkIoExceptions++;

                        // Если это не первая ошибка I/O для этого чанка — URL подозрителен.
                        // Принудительно обновляем URL, чтобы сменить GGC узел (Google Global Cache).
                        if (chunkIoExceptions >= 2)
                        {
                            Log.Warn($"[CachingSource] Chunk {plan.Start} failed repeatedly. Forcing URL refresh...");
                            await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
                            // Сбрасываем эпоху, чтобы закрыть все старые сокеты
                            ResetDownloadEpoch();
                        }

                        // Экспоненциальный Backoff: 100ms, 400ms, 900ms...
                        int delay = (int)Math.Min(2000, 100 * Math.Pow(attempt + 1, 2));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue;

                    case RangeDownloadResult.Cancelled:
                        ct.ThrowIfCancellationRequested();
                        return result;

                    default:
                        return result;
                }
            }
            else
            {
                await WaitForActiveDownloadAsync(actual.LazyTask.Value, ct).ConfigureAwait(false);
            }
        }

        return RangeDownloadResult.NetworkError;
    }

    private void CheckCircuitBreaker(int logicalIndex)
    {
        int failures = Volatile.Read(ref _consecutive403Count);
        if (failures >= _config.Max403BeforeCircuitBreak)
            throw new ChunkDownloadFatalException($"Circuit breaker OPEN: {failures} consecutive 403s", chunkIndex: logicalIndex, consecutiveFailures: failures, reason: ChunkDownloadFailureReason.Forbidden403, trackId: _trackId, httpStatusCode: 403);
    }

    private int ComputeRetryDelay(int attempt)
    {
        int baseDelay = _config.NetworkRetryBaseDelayMs;
        return _config.UseExponentialBackoff ? Math.Min(baseDelay * (1 << attempt), 10_000) : baseDelay;
    }

    #endregion

    #region DownloadRangeCoreAsync

    private async Task<RangeDownloadResult> DownloadRangeCoreAsync(DownloadPlan plan, CancellationToken ct, bool isCritical)
    {
        if (_disposed) return RangeDownloadResult.Cancelled;
        if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;
        bool gotSlot = false;

        try
        {
            if (!isCritical)
            {
                gotSlot = await _downloadSlots.WaitAsync(_config.DownloadSlotTimeoutMs, ct).ConfigureAwait(false);
                if (!gotSlot) return RangeDownloadResult.SlotTimeout;
            }

            if (_disposed) return RangeDownloadResult.Cancelled;
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;
            if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;

            return await DownloadRangeHttpAsync(plan, ct).ConfigureAwait(false);
        }
        catch (ChunkDownloadFatalException) { throw; }
        catch (ObjectDisposedException) { return RangeDownloadResult.Cancelled; }
        catch (OperationCanceledException) { return RangeDownloadResult.Cancelled; }
        catch (Exception) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Range {plan.Start} unexpected: {ex.Message}");
            return RangeDownloadResult.NetworkError;
        }
        finally
        {
            if (gotSlot) { try { _downloadSlots.Release(); } catch (ObjectDisposedException) { } }
        }
    }

    /// <summary>
    /// Вычисляет длину префикса скачанного блока, который можно безопасно закоммитить в RAM без overlap.
    /// </summary>
    /// <param name="start">Начало скачанного диапазона.</param>
    /// <param name="actualLength">Фактически скачанная длина.</param>
    /// <returns>
    /// Длина начального non-overlapping gap.
    /// Может быть меньше <paramref name="actualLength"/>, если хвост диапазона уже есть локально.
    /// </returns>
    private int ComputeRamCommitLength(long start, int actualLength)
    {
        if (actualLength <= 0) return 0;
        return TrimLengthToFirstKnownCoverage(start, actualLength, includeInflight: false);
    }

    private async Task<RangeDownloadResult> DownloadRangeHttpAsync(DownloadPlan plan, CancellationToken ct)
    {
        int rn = Interlocked.Increment(ref _requestSequenceNumber);
        long end = plan.Start + plan.Length - 1;
        int logicalIndex = (int)(plan.Start / _requestAlignmentBytes);

        Log.Debug($"[CachingSource] Range {plan.Start}-{end}: GET rn={rn}");

        using var request = CreateRangeRequest(logicalIndex, plan.Start, end, rn);
        HttpResponseMessage response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode) SaveLatency(sw.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (HttpRequestException ex) when (IsCancelledSendFailure(ex, ct, _disposed)) { return RangeDownloadResult.Cancelled; }
        catch (TaskCanceledException) { return RangeDownloadResult.NetworkError; }
        catch (HttpRequestException) { return RangeDownloadResult.NetworkError; }

        using (response)
        {
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Interlocked.Increment(ref _consecutive403Count);
                await LogAndDiagnose403Async(logicalIndex, request, response).ConfigureAwait(false);
                return RangeDownloadResult.Forbidden403;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Debug($"[CachingSource] Range {plan.Start}-{end}: 416 (EOF)");
                return RangeDownloadResult.OutOfRange;
            }

            Interlocked.Exchange(ref _consecutive403Count, 0);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
                throw new ChunkDownloadFatalException("YouTube returned encrypted UMP format", chunkIndex: logicalIndex, consecutiveFailures: 0, reason: ChunkDownloadFailureReason.UmpFormat, trackId: _trackId);

            response.EnsureSuccessStatusCode();

            try
            {
                using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(plan.Length);
                int actualLength;
                var bodySw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    actualLength = await ReadStreamFullyAsync(contentStream, memoryOwner.Memory[..plan.Length], ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    memoryOwner.Dispose();
                    throw new OperationCanceledException("HTTP stream disposed during read (seek/stop)");
                }
                catch (OperationCanceledException)
                {
                    memoryOwner.Dispose();
                    throw;
                }
                catch (Exception ex) when (ct.IsCancellationRequested || _disposed || ex is IOException || ex is System.Net.Sockets.SocketException)
                {
                    _lastDownloadException = ex;
                    memoryOwner.Dispose();

                    if (ct.IsCancellationRequested || _disposed) return RangeDownloadResult.Cancelled;

                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read I/O error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                catch (Exception ex)
                {
                    _lastDownloadException = ex;
                    memoryOwner.Dispose();

                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                finally
                {
                    bodySw.Stop();
                }

                if (actualLength > 0 && bodySw.Elapsed.TotalMilliseconds > 5)
                {
                    double speedBytesPerSec = actualLength / (bodySw.Elapsed.TotalMilliseconds / 1000.0);
                    SaveBandwidth(speedBytesPerSec);
                }

                if (ct.IsCancellationRequested)
                {
                    memoryOwner.Dispose();
                    return RangeDownloadResult.Cancelled;
                }

                if (actualLength == 0)
                {
                    memoryOwner.Dispose();
                    Log.Warn($"[CachingSource] Range {plan.Start}-{end}: empty response");
                    return RangeDownloadResult.NetworkError;
                }

                if (actualLength < plan.Length)
                {
                    bool isNearEof = plan.Start + actualLength >= _contentLength;
                    if (!isNearEof)
                    {
                        memoryOwner.Dispose();
                        Log.Warn($"[CachingSource] Range {plan.Start}-{end} incomplete: {actualLength}/{plan.Length}");
                        return RangeDownloadResult.NetworkError;
                    }
                }

                // disk copy создаётся всегда, независимо от того, удастся ли закоммитить блок в RAM.
                // Иначе overlap-case превращается в "скачал и выбросил", после чего тот же range
                // будет запрошен повторно бесконечно.
                byte[] diskCopy = ArrayPool<byte>.Shared.Rent(actualLength);
                memoryOwner.Memory.Span[..actualLength].CopyTo(diskCopy.AsSpan(0, actualLength));

                int ramCommitLength = ComputeRamCommitLength(plan.Start, actualLength);
                bool committedToRam = false;

                if (ramCommitLength > 0)
                {
                    var block = new RamRangeBlock(plan.Start, memoryOwner, ramCommitLength);
                    if (_ramCache.TryAdd(block))
                    {
                        committedToRam = true;
                    }
                    else
                    {
                        block.Dispose();
                    }
                }
                else
                {
                    memoryOwner.Dispose();
                }

                _ = WriteToDiskFireAndForgetAsync(plan.Start, diskCopy, actualLength);

                if (!committedToRam)
                {
                    Log.Debug($"[CachingSource] Range {plan.Start}-{end}: RAM overlap avoided, " +
                              $"committed_prefix={ramCommitLength}/{actualLength}, disk_write=scheduled");
                }

                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    _ramCache.Trim(Volatile.Read(ref _currentReadOffset), _config.RamEvictionWindowBytes, _config.MaxRamBytes);

                return RangeDownloadResult.Success;
            }
            catch (OperationCanceledException)
            {
                return RangeDownloadResult.Cancelled;
            }
        }
    }

    private static async ValueTask<int> ReadStreamFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private async Task WriteToDiskFireAndForgetAsync(long offset, byte[] rentedCopy, int length)
    {
        try { await _cacheManager.WriteRangeAsync(_cacheKey, offset, rentedCopy.AsMemory(0, length), CancellationToken.None).ConfigureAwait(false); }
        catch (IOException ex) { Log.Warn($"[CachingSource] Disk write range {offset}-{offset + length - 1}: {ex.Message}"); }
        finally { ArrayPool<byte>.Shared.Return(rentedCopy); }
    }

    #endregion

    #region 403 Diagnostics

    private async Task LogAndDiagnose403Async(int logicalIndex, HttpRequestMessage request, HttpResponseMessage response)
    {
        int count = Volatile.Read(ref _consecutive403Count);
        var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
        var reqUa = request.Headers.UserAgent.ToString();

        Log.Warn($"[CachingSource] 403 DIAGNOSTIC range@{logicalIndex} (consecutive={count})");
        Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
        Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} (len={nParam?.Length ?? 0}, looks_encrypted={nParam?.Length is > 15 and < 25})");

        if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
            Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

        string responseBody = "";
        try { responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        if (responseBody.Length > 0) Log.Warn($"[CachingSource]   Body: {responseBody[..Math.Min(responseBody.Length, 200)]}");
        Log.Warn("[CachingSource]══");
    }

    #endregion

    #region URL Refresh

    private async Task CoordinatedRefreshAsync(CancellationToken ct)
    {
        int refreshFailures = Volatile.Read(ref _consecutiveRefreshFailures);
        if (refreshFailures >= MaxRefreshFailuresBeforeCircuitBreak)
            throw new ChunkDownloadFatalException($"URL refresh circuit breaker OPEN: {refreshFailures} consecutive failures", chunkIndex: -1, consecutiveFailures: Volatile.Read(ref _consecutive403Count), reason: ChunkDownloadFailureReason.Forbidden403, trackId: _trackId, httpStatusCode: 403);

        if (_disposed) return;

        bool acquired;
        try { acquired = await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false); } catch (ObjectDisposedException) { return; }

        if (!acquired)
        {
            Log.Debug("[CachingSource] Waiting for concurrent refresh...");
            try { await _refreshLock.WaitAsync(ct).ConfigureAwait(false); _refreshLock.Release(); } catch (ObjectDisposedException) { return; }

            if (Volatile.Read(ref _consecutiveRefreshFailures) >= MaxRefreshFailuresBeforeCircuitBreak)
                throw new ChunkDownloadFatalException("URL refresh circuit breaker OPEN after concurrent refresh", chunkIndex: -1, consecutiveFailures: Volatile.Read(ref _consecutive403Count), reason: ChunkDownloadFailureReason.Forbidden403, trackId: _trackId, httpStatusCode: 403);

            await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var elapsed = DateTime.UtcNow - _lastRefreshTime;
            if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
            {
                int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
                Log.Debug($"[CachingSource] Refresh cooldown: {waitMs}ms");
                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }

            var previousUrl = _currentUrl;
            await RefreshUrlAsync(ct).ConfigureAwait(false);
            _lastRefreshTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _consecutive403Count, 0);
            Log.Info("[CachingSource] 403 counter reset after URL refresh");

            var newNToken = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
            var oldNToken = UrlEx.TryGetQueryParameterValue(previousUrl, "n");

            if (!string.IsNullOrEmpty(newNToken) && string.Equals(newNToken, oldNToken, StringComparison.Ordinal))
            {
                int failures = Interlocked.Increment(ref _consecutiveRefreshFailures);
                Log.Warn($"[CachingSource] n-token unchanged after refresh (attempt {failures}/{MaxRefreshFailuresBeforeCircuitBreak})");
            }
            else Interlocked.Exchange(ref _consecutiveRefreshFailures, 0);

            await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
        }
        finally { try { _refreshLock.Release(); } catch (ObjectDisposedException) { } }
    }

    #endregion

    #region HTTP Request Building

    private HttpRequestMessage CreateRangeRequest(int logicalIndex, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains("googlevideo.com/videoplayback", StringComparison.Ordinal);

        if (isYouTube)
        {
            string url = BuildYouTubeRangeUrl(_currentUrl, rn);
            LogRangeRequestParams(logicalIndex, url);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            SharedHttpClient.ApplyUserAgentFromUrl(request, url);
            string ua = request.Headers.UserAgent.ToString();
            Log.Debug($"[CachingSource] Range {logicalIndex} UA: {ua[..Math.Min(60, ua.Length)]}...");
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        genericRequest.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);
        return genericRequest;
    }

    private static void LogRangeRequestParams(int logicalIndex, string url)
    {
        var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");
        Log.Debug($"[CachingSource] Range {logicalIndex} URL: {url[..Math.Min(url.Length, 300)]}");
        Log.Debug($"[CachingSource] Range {logicalIndex} params: c={cParam ?? "MISSING"}, n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., sig={(sigParam is not null ? $"{sigParam.Length}chars" : "MISSING")}");
    }

    private static string BuildYouTubeRangeUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Проверяет, покрыт ли диапазон полностью уже локально либо активной in-flight загрузкой.
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    /// <returns><c>true</c>, если диапазон уже доступен локально или гарантированно качается.</returns>
    private bool IsRangeKnownAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (IsRangeLocallyAvailable(position, length)) return true;
        return IsRangeCoveredByInflight(position, length);
    }

    /// <summary>
    /// Проверяет, покрыт ли диапазон полностью уже зарегистрированной активной загрузкой.
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    /// <returns><c>true</c>, если диапазон полностью лежит внутри in-flight range.</returns>
    private bool IsRangeCoveredByInflight(long position, int length)
    {
        long endExclusive = position + length;

        lock (_activeDownloadsLock)
        {
            foreach (var active in _activeDownloads.Values)
            {
                if (position < active.Start) continue;
                if (endExclusive > active.EndExclusive) continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Обрезает планируемую длину загрузки до первого уже известного aligned coverage впереди.
    /// </summary>
    /// <param name="start">Начало диапазона.</param>
    /// <param name="length">Исходная длина диапазона.</param>
    /// <param name="includeInflight">
    /// <c>true</c> — учитывать in-flight загрузки как покрытие;
    /// <c>false</c> — учитывать только локально доступные данные.
    /// </param>
    /// <returns>
    /// Длину первого непрерывного "gap" от <paramref name="start"/> до ближайшего уже доступного aligned диапазона.
    /// Если покрытия впереди нет — возвращает исходную <paramref name="length"/>.
    /// </returns>
    private int TrimLengthToFirstKnownCoverage(long start, int length, bool includeInflight)
    {
        if (length <= _requestAlignmentBytes) return length;

        long endExclusive = start + length;
        long probe = start + _requestAlignmentBytes;

        while (probe < endExclusive)
        {
            int probeLength = (int)Math.Min(_requestAlignmentBytes, endExclusive - probe);
            bool covered = includeInflight
                ? IsRangeKnownAvailable(probe, probeLength)
                : IsRangeLocallyAvailable(probe, probeLength);

            if (covered)
                return (int)(probe - start);

            probe += _requestAlignmentBytes;
        }

        return length;
    }

    private static bool IsCancelledSendFailure(HttpRequestException exception, CancellationToken ct, bool disposed)
    {
        if (disposed || ct.IsCancellationRequested) return true;
        Exception? current = exception;
        while (current != null)
        {
            switch (current)
            {
                case ObjectDisposedException: return true;
                case System.Net.Sockets.SocketException socketException when socketException.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted or System.Net.Sockets.SocketError.Interrupted: return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    #endregion
}