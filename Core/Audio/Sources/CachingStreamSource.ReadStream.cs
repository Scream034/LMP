using System.Buffers;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка над <see cref="CachingStreamSource"/> для парсеров контейнеров (WebM/MP4).
    ///
    /// <para>Предыдущая реализация создавала новый CTS в <c>Position</c> setter —
    /// каждый вызов <c>_stream.Position += N</c> из Mp4Parser (SkipBytesAsync)
    /// аллоцировал CTS + Cancel + ThreadPool.QueueUserWorkItem (~десятки раз per fragment).
    /// Теперь:</para>
    /// <list type="bullet">
    ///   <item><c>Position setter</c> — лёгкий <c>Volatile.Write</c>, без CTS</item>
    ///   <item><see cref="SeekAndCancelPendingReads"/> — явный cancel для внешнего seek
    ///     (вызывается только из <see cref="SeekAsync"/>)</item>
    /// </list>
    /// <para>Безопасность: парсер однопоточен (read → skip → read), во время skip
    /// нет pending ReadAsync. Внешний seek использует <see cref="SeekAndCancelPendingReads"/>,
    /// который корректно отменяет in-flight ReadAsync через CTS.</para>
    /// </summary>
    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;

        /// <summary>
        /// CTS, привязанный к текущей логической позиции потока.
        /// Заменяется только через <see cref="SeekAndCancelPendingReads"/> — при внешнем seek.
        /// Все pending ReadAsync немедленно отменяются.
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
        /// <remarks>
        /// <para>Вызывается из парсера (SkipBytesAsync) десятки раз per fragment.
        /// Прежняя реализация аллоцировала CTS + Cancel + QueueUserWorkItem на каждый вызов.
        /// Теперь cancel CTS выполняется только через <see cref="SeekAndCancelPendingReads"/>.</para>
        /// </remarks>
        public override long Position
        {
            get => Volatile.Read(ref _position);
            set => Volatile.Write(ref _position, value);
        }

        /// <summary>
        /// Устанавливает позицию потока И отменяет все pending ReadAsync.
        /// Вызывается ТОЛЬКО из <see cref="SeekAsync"/>
        /// при внешнем seek — не из парсера.
        /// </summary>
        /// <remarks>
        /// <para>Создаёт новый CTS, cancel'ит старый. Deferred dispose через ThreadPool
        /// предотвращает ObjectDisposedException в reader-потоке, который может держать
        /// ссылку на старый CTS между <see cref="Volatile.Read{T}"/> и <c>.Token</c>.</para>
        /// </remarks>
        /// <param name="position">Новая абсолютная позиция в байтах.</param>
        internal void SeekAndCancelPendingReads(long position)
        {
            var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

            try { oldCts.Cancel(); }
            catch (ObjectDisposedException) { }

            DeferDisposeCancellationTokenSource(oldCts, 500);

            Volatile.Write(ref _position, position);
        }

        /// <summary>
        /// Мгновенно отменяет активный CTS чтения, прерывая любые заблокированные сетевые запросы.
        /// Выполняется асинхронно для защиты от блокировок сокетов.
        /// </summary>
        internal void CancelActiveReads()
        {
            var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

            // Делегируем закрытие потоков и сокетов пулу
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { }
            }, oldCts);

            DeferDisposeCancellationTokenSource(oldCts, 500);
        }

        #endregion

        #region Read

        /// <summary>
        /// Безопасно извлекает <see cref="CancellationToken"/> из текущего <see cref="_readCts"/>.
        /// Обрабатывает гонку с <see cref="SeekAndCancelPendingReads"/>, который может задиспозить CTS
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
        /// <remarks>
        /// <para><b>Оптимизированный гибридный метод:</b> Если данные чанка уже доступны локально 
        /// (в оперативной памяти или на диске), чтение происходит абсолютно синхронно без аллокаций
        /// и без блокировок Task-планировщика.</para>
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength) return 0;

            int chunkIndex = (int)(posBefore / _source._chunkSize);

            // Проверяем локальную доступность чанка в кэше
            if (_source.IsChunkAvailable(chunkIndex))
            {
                int offsetInChunk = (int)(posBefore % _source._chunkSize);

                // А. Прямое быстрое чтение из RAM-кэша (Lock-Free)
                if (_source._ramChunks.TryGetValue(chunkIndex, out var ramEntry))
                {
                    int read = CopyFromChunk(ramEntry.Memory.Span, ramEntry.Length, offsetInChunk, buffer.AsMemory(offset, count));
                    Volatile.Write(ref _position, posBefore + read);
                    return read;
                }

                // Б. Прямое СИНХРОННОЕ чтение с диска (без async-планировщика и .GetResult())
                try
                {
                    var entry = _source._cacheEntry;
                    if (entry != null)
                    {
                        var filePath = _source._cacheManager.GetCachePath(_source._cacheKey);
                        if (File.Exists(filePath))
                        {
                            long chunkOffset = (long)chunkIndex * entry.ChunkSize;
                            int size = (int)Math.Min(entry.ChunkSize, entry.TotalSize - chunkOffset);
                            if (size > 0)
                            {
                                using var fs = new FileStream(
                                    filePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite,
                                    bufferSize: 4096,
                                    useAsync: false); // Чисто синхронный дескриптор ввода-вывода

                                fs.Position = chunkOffset;

                                byte[] chunkBuf = ArrayPool<byte>.Shared.Rent(size);
                                try
                                {
                                    int totalRead = 0;
                                    while (totalRead < size)
                                    {
                                        int readBytes = fs.Read(chunkBuf, totalRead, size - totalRead);
                                        if (readBytes == 0) break;
                                        totalRead += readBytes;
                                    }

                                    if (totalRead == size)
                                    {
                                        int read = CopyFromChunk(chunkBuf.AsSpan(0, size), size, offsetInChunk, buffer.AsMemory(offset, count));
                                        Volatile.Write(ref _position, posBefore + read);

                                        _source._cacheManager.Touch(_source._cacheKey);
                                        return read;
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(chunkBuf);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ReadStream] Synchronous disk read failed for {_source._cacheKey}: {ex.Message}");
                }
            }

            // Медленный путь (сетевой запрос) с контролируемым Sync-over-Async.
            // На горячем пути WebM/Opus воспроизведения этот метод не вызывается (вызывается ReadAsync).
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
            catch (Exception ex) when (ex is IOException || ex is System.Net.Sockets.SocketException)
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var readToken = GetCurrentReadToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readToken);
            return await ReadAsyncCore(buffer, linkedCts.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Ядро чтения: делегирует в <see cref="ReadAtAsync"/> и
        /// атомарно продвигает позицию только если seek не произошёл во время чтения.
        /// </summary>
        /// <param name="buffer">Целевой буфер.</param>
        /// <param name="ct">Токен отмены (включает readToken текущей позиции).</param>
        /// <returns>Количество прочитанных байт.</returns>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct).ConfigureAwait(false);

            // Если seek произошёл во время I/O — позиция уже обновлена.
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
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End => length + offset,
                _ => Volatile.Read(ref _position)
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