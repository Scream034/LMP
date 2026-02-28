using System.Buffers;

namespace LMP.Core.Audio.Memory;

/// <summary>
/// Пул переиспользуемых буферов для чанков стриминга.
/// Разработан специально для избежания аллокаций в Large Object Heap (LOH).
/// Массивы больше 85KB по умолчанию попадают в LOH, что вызывает фрагментацию и долгие паузы GC.
/// </summary>
public sealed class ChunkPool
{
    /// <summary>
    /// Глобальный экземпляр пула для всего аудио движка.
    /// </summary>
    public static ChunkPool Shared { get; } = new(
        maxPooledChunkSize: 512 * 1024,  // Поддержка чанков до 512KB
        maxPooledPerBucket: 32);         // До 32 буферов на каждый размер

    private readonly ArrayPool<byte> _pool;
    private readonly int _maxSize;

    /// <summary>
    /// Инициализирует новый пул чанков.
    /// </summary>
    public ChunkPool(int maxPooledChunkSize, int maxPooledPerBucket)
    {
        _maxSize = maxPooledChunkSize;
        _pool = ArrayPool<byte>.Create(maxPooledChunkSize, maxPooledPerBucket);
    }

    /// <summary>
    /// Арендует буфер размером не менее запрошенного.
    /// </summary>
    /// <remarks>
    /// <b>ВНИМАНИЕ:</b> Возвращённый массив почти всегда БОЛЬШЕ запрошенного размера <paramref name="minimumSize"/>.
    /// При работе с ним всегда используйте <c>.AsSpan(0, realLength)</c> или <c>.AsMemory(0, realLength)</c>.
    /// </remarks>
    /// <param name="minimumSize">Минимально необходимый размер буфера.</param>
    /// <returns>Массив байт.</returns>
    public byte[] Rent(int minimumSize)
    {
        if (minimumSize > _maxSize)
        {
            // Fallback для экстремально больших запросов (крайне редкий кейс, уходит в LOH)
            return new byte[minimumSize];
        }

        return _pool.Rent(minimumSize);
    }

    /// <summary>
    /// Возвращает буфер обратно в пул для повторного использования.
    /// </summary>
    /// <param name="buffer">Массив, который нужно вернуть.</param>
    /// <param name="clearArray">Укажите <c>true</c>, если данные содержат конфиденциальную информацию (не нужно для аудио).</param>
    public void Return(byte[] buffer, bool clearArray = false)
    {
        if (buffer.Length <= _maxSize)
        {
            _pool.Return(buffer, clearArray);
        }
        // Буферы больше _maxSize были созданы через new byte[], GC соберёт их сам.
    }
}