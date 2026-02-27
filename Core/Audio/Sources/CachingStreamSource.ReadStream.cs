namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка для парсеров контейнеров (WebM/MP4).
    /// Seek отменяет текущие Read через <see cref="_readCts"/>.
    /// </summary>
    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource? _source;
        private readonly Stream? _fileStream;
        private long _position;
        private CancellationTokenSource _readCts = new();

        public AsyncCachingReadStream(CachingStreamSource source)
        {
            _source = source;
        }

        public AsyncCachingReadStream(CachingStreamSource source, Stream fileStream)
        {
            _source = source;
            _fileStream = fileStream;
        }

        #region Stream Properties

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _fileStream?.Length ?? _source?._contentLength ?? 0;

        public override long Position
        {
            get => _fileStream?.Position ?? Volatile.Read(ref _position);
            set
            {
                if (_fileStream != null)
                {
                    _fileStream.Position = value;
                    return;
                }

                var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }
                try { oldCts.Dispose(); }
                catch (ObjectDisposedException) { }

                Volatile.Write(ref _position, value);
            }
        }

        #endregion

        #region Read

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_fileStream != null)
                return _fileStream.Read(buffer, offset, count);

            try
            {
                var token = Volatile.Read(ref _readCts).Token;
                return ReadAsyncCore(buffer.AsMemory(offset, count), token)
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_fileStream != null)
                return await _fileStream.ReadAsync(buffer, ct);

            var readToken = Volatile.Read(ref _readCts).Token;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readToken);

            return await ReadAsyncCore(buffer, linkedCts.Token);
        }

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            if (_source == null) return 0;

            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct);

            long posAfter = Volatile.Read(ref _position);
            if (posAfter != posBefore)
                return 0;

            Interlocked.CompareExchange(ref _position, posBefore + read, posBefore);
            return read;
        }

        #endregion

        #region Seek

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_fileStream != null)
                return _fileStream.Seek(offset, origin);

            long length = _source?._contentLength ?? 0;
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End => length + offset,
                _ => Volatile.Read(ref _position)
            };

            newPos = Math.Clamp(newPos, 0, length);
            Volatile.Write(ref _position, newPos);
            return newPos;
        }

        #endregion

        #region Not Supported

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        #endregion

        #region Dispose

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fileStream?.Dispose();

                var cts = Interlocked.Exchange(ref _readCts, null!);
                if (cts != null)
                {
                    try { cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                    try { cts.Dispose(); }
                    catch (ObjectDisposedException) { }
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}