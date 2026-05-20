namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка над <see cref="CachingStreamSource"/> для парсеров контейнеров (WebM/MP4).
    ///
    /// <para><b>Почему отдельный CTS на каждый Read:</b> seek из парсера вызывает <see cref="Position"/> setter,
    /// который немедленно отменяет текущий <see cref="ReadAsync"/> через <see cref="_readCts"/>,
    /// не допуская блокировки decoder thread на устаревшем I/O.</para>
    /// </summary>
    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;

        /// <summary>
        /// CTS, привязанный к текущей логической позиции потока.
        /// Заменяется при каждом <see cref="Position"/> setter — все pending ReadAsync немедленно отменяются.
        /// </summary>
        private CancellationTokenSource _readCts = new();

        /// <summary>
        /// Создаёт Stream-обёртку над источником.
        /// </summary>
        /// <param name="source">Родительский <see cref="CachingStreamSource"/>.</param>
        public AsyncCachingReadStream(CachingStreamSource source)
        {
            _source = source;
        }

        #region Stream Properties

        /// <inheritdoc/>
        public override bool CanRead => true;

        /// <inheritdoc/>
        public override bool CanSeek => true;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _source._contentLength;

        /// <inheritdoc/>
        public override long Position
        {
            get => Volatile.Read(ref _position);
            set
            {
                var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }

                // Deferred Dispose: reader-поток может держать ссылку на oldCts
                // между Volatile.Read(ref _readCts) и .Token — немедленный Dispose
                // вызовет ObjectDisposedException в reader'е.
                // Планируем Dispose через ThreadPool, давая reader'у время завершить доступ.
                ThreadPool.QueueUserWorkItem(static state =>
                {
                    try { ((CancellationTokenSource)state!).Dispose(); }
                    catch (ObjectDisposedException) { }
                }, oldCts);

                Volatile.Write(ref _position, value);
            }
        }

        #endregion

        #region Read

        /// <summary>
        /// Безопасно извлекает <see cref="CancellationToken"/> из текущего <see cref="_readCts"/>.
        /// Обрабатывает гонку с <see cref="Position"/> setter, который может задиспозить CTS
        /// между <see cref="Volatile.Read{T}"/> и обращением к <c>.Token</c>.
        /// </summary>
        /// <returns>Актуальный токен или отменённый токен при гонке.</returns>
        private CancellationToken GetCurrentReadToken()
        {
            try
            {
                return Volatile.Read(ref _readCts)?.Token ?? CancellationToken.None;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Sync-over-async: на случай если парсер использует синхронный Read.
            // Основной путь — ReadAsync.
            try
            {
                return ReadAsyncCore(buffer.AsMemory(offset, count), GetCurrentReadToken())
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) { return 0; }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { return 0; }
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var readToken = GetCurrentReadToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readToken);
            return await ReadAsyncCore(buffer, linkedCts.Token);
        }

        /// <summary>
        /// Ядро чтения: делегирует в <see cref="CachingStreamSource.ReadAtAsync"/> и
        /// атомарно продвигает позицию только если seek не произошёл во время чтения.
        /// </summary>
        /// <param name="buffer">Целевой буфер.</param>
        /// <param name="ct">Токен отмены (включает readToken текущей позиции).</param>
        /// <returns>Количество прочитанных байт.</returns>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct);

            // Если seek произошёл во время I/O — позиция уже обновлена Position setter'ом.
            // Возвращаем 0, чтобы парсер не продвинулся по устаревшим данным.
            if (Volatile.Read(ref _position) != posBefore)
                return 0;

            Interlocked.CompareExchange(ref _position, posBefore + read, posBefore);
            return read;
        }

        #endregion

        #region Seek

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long length = _source._contentLength;
            long newPos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End     => length + offset,
                _                  => Volatile.Read(ref _position)
            };

            newPos = Math.Clamp(newPos, 0, length);
            Position = newPos;
            return newPos;
        }

        #endregion

        #region Not Supported

        /// <inheritdoc/>
        public override void Flush() { }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        #endregion

        #region Dispose

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var cts = Interlocked.Exchange(ref _readCts, null!);
                if (cts != null)
                {
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                    try { cts.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}