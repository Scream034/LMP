// Core/Audio/Sources/CachingStreamSource.ReadStream.cs

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка для парсеров контейнеров (WebM/MP4).
    /// 
    /// <para><b>Проблема:</b></para>
    /// Парсеры вызывают <see cref="Stream.Read"/> (sync) и <see cref="Stream.ReadAsync"/> (async).
    /// При seek нужно прервать текущее чтение и начать с новой позиции.
    /// Парсеры интерпретируют <c>Read() == 0</c> как EOF → <c>EndOfStreamException</c>.
    /// 
    /// <para><b>Решение:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="Position"/> setter отменяет внутренний <see cref="_readCts"/>,
    ///     будя все застрявшие <see cref="ReadAsync"/> вызовы
    ///   </item>
    ///   <item>
    ///     Sync <see cref="Read"/> ловит <see cref="OperationCanceledException"/>
    ///     и возвращает 0 — парсер перечитает с новой позиции (после <c>Reset()</c>)
    ///   </item>
    ///   <item>
    ///     <see cref="ReadAsync"/> проверяет position до и после чтения —
    ///     если position изменился (seek), возвращает 0
    ///   </item>
    ///   <item>
    ///     <see cref="ReadAtAsync"/> НИКОГДА не возвращает 0
    ///     для промежуточных позиций — ждёт или бросает
    ///   </item>
    /// </list>
    /// </summary>
    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource? _source;
        private readonly Stream? _fileStream;
        private long _position;

        /// <summary>
        /// CTS для отмены текущих операций чтения при seek.
        /// Пересоздаётся в setter <see cref="Position"/>.
        /// </summary>
        private CancellationTokenSource _readCts = new();

        /// <summary>
        /// Создаёт стрим для онлайн-режима (чтение через <see cref="ReadAtAsync"/>).
        /// </summary>
        public AsyncCachingReadStream(CachingStreamSource source)
        {
            _source = source;
        }

        /// <summary>
        /// Создаёт стрим для офлайн-режима (делегирование к файловому стриму).
        /// </summary>
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

        /// <summary>
        /// Позиция чтения в потоке.
        /// </summary>
        /// <remarks>
        /// <para><b>Setter (критический для seek):</b></para>
        /// <list type="number">
        ///   <item>Отменяет <see cref="_readCts"/> — все застрявшие Read/ReadAsync получают OCE</item>
        ///   <item>Создаёт новый <see cref="_readCts"/></item>
        ///   <item>Устанавливает позицию через <see cref="Volatile.Write"/></item>
        /// </list>
        /// <para>
        /// Это безопасно, потому что:
        /// - Sync <see cref="Read"/> ловит OCE → возвращает 0
        /// - Async <see cref="ReadAsync"/> проверяет position после чтения
        /// - <see cref="IContainerParser.Reset"/> вызывается после set Position
        /// </para>
        /// </remarks>
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

                // Отменяем все текущие операции чтения
                var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }

                try { oldCts.Dispose(); }
                catch (ObjectDisposedException) { }

                Volatile.Write(ref _position, value);
            }
        }

        #endregion

        #region Read (Sync)

        /// <summary>
        /// Синхронное чтение — вызывается парсерами контейнеров.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Использует sync-over-async через <c>.GetAwaiter().GetResult()</c>.
        /// При seek (Position setter отменяет <see cref="_readCts"/>)
        /// ловит <see cref="OperationCanceledException"/> и возвращает 0.
        /// </para>
        /// <para>
        /// Возврат 0 безопасен здесь, потому что после seek вызывается
        /// <see cref="IContainerParser.Reset"/>, и парсер начинает чтение заново.
        /// </para>
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_fileStream != null)
                return _fileStream.Read(buffer, offset, count);

            try
            {
                // Берём snapshot токена — если Position setter создаст новый,
                // мы получим отмену старого
                var token = Volatile.Read(ref _readCts).Token;

                return ReadAsyncCore(buffer.AsMemory(offset, count), token)
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                // Position изменился (seek) — возвращаем 0.
                // Парсер перечитает после Reset().
                return 0;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                return 0;
            }
        }

        #endregion

        #region ReadAsync

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        }

        /// <summary>
        /// Асинхронное чтение с защитой от race condition при seek.
        /// </summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_fileStream != null)
                return await _fileStream.ReadAsync(buffer, ct);

            // Связываем внешний ct с внутренним _readCts
            var readToken = Volatile.Read(ref _readCts).Token;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readToken);

            return await ReadAsyncCore(buffer, linkedCts.Token);
        }

        /// <summary>
        /// Ядро чтения — делегирует к <see cref="ReadAtAsync"/>
        /// с проверкой consistency позиции.
        /// </summary>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            if (_source == null) return 0;

            // Фиксируем позицию ДО чтения
            long posBefore = Volatile.Read(ref _position);

            int read = await _source.ReadAtAsync(posBefore, buffer, ct);

            // Проверяем: если position изменился пока мы ждали — данные невалидны.
            // Не обновляем позицию — вызывающий код перечитает после Reset().
            long posAfter = Volatile.Read(ref _position);
            if (posAfter != posBefore)
                return 0;

            // Атомарно обновляем позицию
            // CompareExchange гарантирует что мы не перезапишем позицию,
            // которую setter уже изменил
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
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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