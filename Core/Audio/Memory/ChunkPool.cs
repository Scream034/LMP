using System.Buffers;

namespace LMP.Core.Audio.Memory;

/// <summary>
/// Тонкая обёртка над <see cref="ArrayPool{T}.Shared"/> для аудио-буферов стриминга.
/// </summary>
/// <remarks>
/// <para>Делегирует в <see cref="ArrayPool{T}.Shared"/>, который использует
/// <c>[ThreadStatic]</c> TLS как первый уровень кэша (zero-lock в common case, ~6 ns)
/// и per-core partitioned стеки как второй уровень.</para>
/// <para>В отличие от <c>ArrayPool.Create()</c> (<c>ConfigurableArrayPool</c>),
/// <c>Shared</c> автоматически освобождает неиспользуемые массивы через Gen2 GC callback,
/// не раздувая LOH на зафиксированных буферах.</para>
/// <para>Запросы больше <c>maxPooledChunkSize</c> аллоцируются через <c>new byte[]</c>
/// (экстремально редкий случай) и собираются GC самостоятельно.</para>
/// </remarks>
public sealed class ChunkPool
{
    /// <summary>Глобальный экземпляр пула для всего аудио движка.</summary>
    public static ChunkPool Shared { get; } = new(maxPooledChunkSize: 512 * 1024);

    private readonly int _maxPooledSize;

    /// <param name="maxPooledChunkSize">
    /// Максимальный размер буфера, обслуживаемого через пул.
    /// Запросы выше этого лимита аллоцируются напрямую.
    /// </param>
    public ChunkPool(int maxPooledChunkSize)
    {
        _maxPooledSize = maxPooledChunkSize;
    }

    /// <summary>
    /// Арендует буфер размером не менее <paramref name="minimumSize"/>.
    /// Возвращённый массив может быть больше запрошенного — используйте
    /// <c>.AsSpan(0, actualLength)</c> для работы с реальными данными.
    /// </summary>
    public byte[] Rent(int minimumSize)
    {
        if (minimumSize > _maxPooledSize)
            return new byte[minimumSize];

        return ArrayPool<byte>.Shared.Rent(minimumSize);
    }

    /// <summary>
    /// Возвращает арендованный буфер в пул. <c>null</c> безопасно игнорируется.
    /// Буферы, выходящие за <c>maxPooledChunkSize</c>, собираются GC автоматически.
    /// </summary>
    /// <param name="clearArray">
    /// <c>true</c> для обнуления содержимого перед возвратом.
    /// Для аудио данных очистка не требуется.
    /// </param>
    public void Return(byte[]? buffer, bool clearArray = false)
    {
        if (buffer is null) return;

        if (buffer.Length <= _maxPooledSize)
            ArrayPool<byte>.Shared.Return(buffer, clearArray);
    }
}