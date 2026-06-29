using System.Buffers;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Предоставляет высокопроизводительные методы расширения для потоков ввода-вывода <see cref="Stream"/>.
/// </summary>
internal static class StreamExtensions
{
    extension(Stream source)
    {
        /// <summary>
        /// Асинхронно копирует содержимое текущего потока в другой поток с поддержкой уведомления о прогрессе и отмены операции.
        /// <para>Безопасно обрабатывает не-seekable потоки (сетевые сокеты, HTTP-стримы скачивания), в которых свойство <see cref="Stream.Length"/> недоступно.</para>
        /// </summary>
        /// <param name="destination">Поток назначения.</param>
        /// <param name="progress">Интерфейс уведомления о прогрессе (передаёт значение в диапазоне [0..1] для seekable потоков, либо абсолютное количество записанных байт для не-seekable).</param>
        /// <param name="cancellationToken">Токен отмены операции.</param>
        /// <exception cref="ArgumentNullException">Генерируется, если поток назначения равен null.</exception>
        public async ValueTask CopyToAsync(
            Stream destination,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);

            using var buffer = MemoryPool<byte>.Shared.Rent(81920);

            // Безопасная проверка: CanSeek указывает на возможность вызова свойства Length
            bool canDetermineLength = source.CanSeek;
            long streamLength = canDetermineLength ? source.Length : -1L;

            var totalBytesRead = 0L;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.Memory, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                await destination.WriteAsync(buffer.Memory[..bytesRead], cancellationToken).ConfigureAwait(false);

                totalBytesRead += bytesRead;

                if (progress is not null)
                {
                    if (streamLength > 0)
                        progress.Report(1.0 * totalBytesRead / streamLength);
                    else
                        progress.Report(totalBytesRead); // Передаем наверх объем, если поток не поддерживает Length
                }
            }
        }
    }
}