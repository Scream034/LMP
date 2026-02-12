// Core/Services/Streaming/MediaStreamBase.cs

using System.Net;
using System.Runtime.CompilerServices;

namespace LMP.Core.Services.Streaming;

/// <summary>
/// Базовый класс для потоковых медиа-источников с поддержкой отмены.
/// </summary>
public abstract class MediaStreamBase : Stream
{
    #region Fields

    protected readonly HttpClient Http;
    protected readonly CancellationTokenSource DisposeCts = new();

    private long _position;
    private long _totalLength;
    private volatile bool _disposed;
    private volatile bool _isPaused;
    private volatile bool _playbackStarted;

    #endregion

    #region Stream Properties

    public sealed override bool CanRead => !_disposed;
    public sealed override bool CanSeek => !_disposed;
    public sealed override bool CanWrite => false;

    public sealed override long Length => Volatile.Read(ref _totalLength);

    public sealed override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    #endregion

    #region Protected Accessors

    protected long TotalLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _totalLength);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _totalLength, value);
    }

    protected long CurrentPosition
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _position);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _position, value);
    }

    protected bool IsPaused => _isPaused;
    protected bool PlaybackStarted => _playbackStarted;
    public bool IsDisposed => _disposed;

    #endregion

    #region Abstract Members

    public abstract ValueTask<bool> InitializeAsync(CancellationToken ct = default);
    public abstract ValueTask<bool> PreBufferAsync(CancellationToken ct = default);
    public abstract double DownloadProgress { get; }
    public abstract long BufferedBytes { get; }
    public abstract bool IsFullyDownloaded { get; }
    public abstract IReadOnlyList<(double Start, double End)> GetBufferedRanges();

    #endregion

    #region Virtual Members

    protected virtual void OnSeekInternal(long newBytePosition) { }
    protected virtual void OnDispose() { }
    protected virtual void OnPauseStateChanged(bool paused) { }
    protected virtual void OnPlaybackStarted() { }
    public virtual void ReleaseRamBuffers() { }

    /// <summary>
    /// Вызывается для немедленной отмены всех операций (seek, stop, dispose).
    /// </summary>
    public virtual void CancelPendingReads() { }

    #endregion

    #region Constructor

    protected MediaStreamBase(HttpClient http)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
    }

    #endregion

    #region Public Control Methods

    public void NotifyPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        OnPauseStateChanged(paused);
    }

    /// <summary>
    /// Уведомление о seek. Вызывает немедленную отмену текущих операций.
    /// </summary>
    public void NotifySeek(long positionMs)
    {
        // Сначала отмена — потом расчёт позиции
        CancelPendingReads();
        OnSeekInternal(positionMs);
    }

    public void NotifyPlaybackStarted()
    {
        if (_playbackStarted) return;
        _playbackStarted = true;
        OnPlaybackStarted();
    }

    #endregion

    #region Seek Implementation

    public sealed override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;

        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => CurrentPosition + offset,
            SeekOrigin.End => TotalLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        newPos = Math.Clamp(newPos, 0, Math.Max(0, TotalLength));
        CurrentPosition = newPos;

        return newPos;
    }

    #endregion

    #region Not Supported

    public sealed override void Flush() { }
    public sealed override void SetLength(long value) => throw new NotSupportedException();
    public sealed override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion

    #region HTTP Helpers

    protected HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        Dictionary<string, string>? headers = null,
        (long From, long To)? range = null)
    {
        var request = new HttpRequestMessage(method, url);

        if (headers != null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (range.HasValue)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                range.Value.From, range.Value.To);
        }

        return request;
    }

    #endregion

    #region Dispose

    protected sealed override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try { DisposeCts.Cancel(); } catch { }
            CancelPendingReads();
            OnDispose();
            try { DisposeCts.Dispose(); } catch { }
        }

        base.Dispose(disposing);
    }

    #endregion
}