using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Экстремально быстрый Lock-Free кольцевой буфер для сценария Single-Producer / Single-Consumer.
/// </summary>
/// <remarks>
/// <para><b>Архитектурные особенности:</b></para>
/// <list type="bullet">
///   <item><b>Zero Locks:</b> Полное отсутствие блокировок, использует только атомарные барьеры памяти (Volatile).</item>
///   <item><b>False Sharing Protection:</b> Указатели Head и Tail разнесены по разным кэш-линиям
///   процессора через wrapper-структуры с явным размером 128 байт (2 cache lines).
///   Это предотвращает деградацию производительности,
///   когда потоки декодера и аудио-вывода работают на разных ядрах CPU.</item>
///   <item><b>Bitwise Modulo:</b> Размер буфера строго выравнивается до степени двойки,
///   что позволяет заменить медленную операцию остатка от деления (<c>%</c>)
///   на быструю побитовую маску (<c>&amp;</c>).</item>
///   <item><b>Cached Counters:</b> Каждая сторона (Producer/Consumer) кэширует
///   индекс противоположной стороны, минимизируя volatile reads между ядрами.</item>
/// </list>
/// 
/// <para><b>Ограничения:</b></para>
/// <list type="bullet">
///   <item>Строго один Producer (decoder loop) и один Consumer (audio callback)</item>
///   <item>Ёмкость всегда степень двойки (автоматическое округление)</item>
///   <item>1 слот зарезервирован для отличия Full от Empty состояний</item>
/// </list>
/// </remarks>
public sealed class LockFreeRingBuffer<T> where T : unmanaged
{
    // ═══════════════════════════════════════════════════════════════════
    // Cache Line Padding Strategy:
    //
    // CLR не позволяет [StructLayout(Explicit)] на generic types,
    // поэтому используем wrapper-структуры с фиксированным размером.
    //
    // Каждая PaddedXxx структура занимает ровно 128 байт (2 cache lines),
    // что гарантирует что _producer и _consumer НЕ попадут в одну линию
    // даже при самом неблагоприятном выравнивании.
    //
    // Почему 128, а не 64:
    // - Intel Spatial Prefetcher загружает ПАРЫ соседних cache lines
    // - ARM Cortex имеет cache lines от 32 до 128 байт
    // - 128 байт покрывает все современные архитектуры
    //
    // Layout в памяти (гарантированный через [StructLayout]):
    //   [_buffer ref][_mask][_capacity]    ← shared, read-only после init
    //   [_producer ~~~ 128 bytes ~~~]      ← Producer: head + cachedTail
    //   [_consumer ~~~ 128 bytes ~~~]      ← Consumer: tail + cachedHead
    // ═══════════════════════════════════════════════════════════════════

    private readonly T[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;

    // --- Producer state (только decoder loop пишет сюда) ---
    // 128-байтная структура гарантирует изоляцию от consumer cache line
    private ProducerState _producer;

    // --- Consumer state (только audio callback пишет сюда) ---
    // 128-байтная структура гарантирует изоляцию от producer cache line
    private ConsumerState _consumer;

    /// <summary>
    /// Состояние Producer (decoder loop). Занимает 128 байт = 2 cache lines.
    /// Только producer пишет в Head и CachedTail.
    /// </summary>
    /// <remarks>
    /// [StructLayout(Sequential)] + [Size=128] гарантирует что CLR
    /// не переупакует поля и структура займёт ровно 128 байт.
    /// Это работает для struct (в отличие от class, где CLR может менять layout).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ProducerState
    {
        /// <summary>Индекс записи (producer двигает вперёд).</summary>
        public int Head;

        /// <summary>
        /// Кэшированное значение Tail от consumer.
        /// Обновляется только когда producer видит "буфер полон" по локальному кэшу.
        /// Минимизирует cross-core volatile reads.
        /// </summary>
        public int CachedTail;
    }

    /// <summary>
    /// Состояние Consumer (audio callback / NAudio). Занимает 128 байт = 2 cache lines.
    /// Только consumer пишет в Tail и CachedHead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ConsumerState
    {
        /// <summary>Индекс чтения (consumer двигает вперёд).</summary>
        public int Tail;

        /// <summary>
        /// Кэшированное значение Head от producer.
        /// Обновляется только когда consumer видит "буфер пуст" по локальному кэшу.
        /// </summary>
        public int CachedHead;
    }

    /// <summary>
    /// Создаёт циклический буфер.
    /// Ёмкость автоматически округляется до ближайшей большей степени двойки.
    /// </summary>
    /// <param name="requestedCapacity">Минимальная требуемая ёмкость.</param>
    public LockFreeRingBuffer(int requestedCapacity)
    {
        _capacity = RoundUpToPowerOf2(Math.Max(requestedCapacity, 16));
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
    }

    /// <summary>Текущее количество элементов в буфере.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int head = Volatile.Read(ref _producer.Head);
            int tail = Volatile.Read(ref _consumer.Tail);
            return (head - tail + _capacity) & _mask;
        }
    }

    /// <summary>Ёмкость буфера (всегда степень двойки).</summary>
    public int Capacity => _capacity;

    /// <summary>Свободное место в буфере (1 слот зарезервирован).</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - Count;
    }

    /// <summary>Буфер пуст.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _producer.Head) == Volatile.Read(ref _consumer.Tail);
    }

    /// <summary>
    /// Записывает данные в буфер.
    /// Вызывается ТОЛЬКО Producer-ом (decoder loop).
    /// </summary>
    /// <param name="data">Данные для записи.</param>
    /// <returns>Количество фактически записанных элементов.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<T> data)
    {
        int head = _producer.Head; // Локальное чтение (мы единственный writer)
        int tail = _producer.CachedTail;
        int count = (head - tail + _capacity) & _mask;
        int available = _capacity - 1 - count;

        // Если по локальному кэшу места нет — обновляем, читая реальный Tail
        if (available < data.Length)
        {
            tail = Volatile.Read(ref _consumer.Tail);
            _producer.CachedTail = tail;
            count = (head - tail + _capacity) & _mask;
            available = _capacity - 1 - count;
        }

        int toWrite = Math.Min(data.Length, available);
        if (toWrite == 0) return 0;

        int headIndex = head & _mask;
        int firstPart = Math.Min(toWrite, _capacity - headIndex);

        // Копируем первую часть (до конца массива)
        data[..firstPart].CopyTo(_buffer.AsSpan(headIndex, firstPart));

        // Копируем вторую часть (с начала массива) при wrap-around
        if (toWrite > firstPart)
        {
            data[firstPart..toWrite]
                .CopyTo(_buffer.AsSpan(0, toWrite - firstPart));
        }

        // Memory Barrier: публикуем новый Head ПОСЛЕ записи данных.
        // Consumer увидит обновлённый Head только когда все данные уже в буфере.
        Volatile.Write(ref _producer.Head, (head + toWrite) & _mask);
        return toWrite;
    }

    /// <summary>
    /// Читает данные из буфера.
    /// Вызывается ТОЛЬКО Consumer-ом (audio callback / NAudio).
    /// </summary>
    /// <param name="output">Буфер для записи прочитанных данных.</param>
    /// <returns>Количество фактически прочитанных элементов.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<T> output)
    {
        int tail = _consumer.Tail; // Локальное чтение (мы единственный reader)
        int head = _consumer.CachedHead;
        int count = (head - tail + _capacity) & _mask;

        // Если по локальному кэшу данных нет — обновляем, читая реальный Head
        if (count < output.Length)
        {
            head = Volatile.Read(ref _producer.Head);
            _consumer.CachedHead = head;
            count = (head - tail + _capacity) & _mask;
        }

        int toRead = Math.Min(output.Length, count);
        if (toRead == 0) return 0;

        int tailIndex = tail & _mask;
        int firstPart = Math.Min(toRead, _capacity - tailIndex);

        // Копируем первую часть
        _buffer.AsSpan(tailIndex, firstPart).CopyTo(output[..firstPart]);

        // Копируем вторую часть при wrap-around
        if (toRead > firstPart)
        {
            _buffer.AsSpan(0, toRead - firstPart)
                .CopyTo(output[firstPart..toRead]);
        }

        // Memory Barrier: освобождаем место (двигаем Tail) ПОСЛЕ чтения.
        // Producer увидит обновлённый Tail только когда чтение завершено.
        Volatile.Write(ref _consumer.Tail, (tail + toRead) & _mask);
        return toRead;
    }

    /// <summary>
    /// Очищает буфер. Вызывать ТОЛЬКО при остановленном воспроизведении!
    /// </summary>
    /// <remarks>
    /// НЕ потокобезопасно для concurrent read/write.
    /// Безопасно вызывать между StopDecoding и StartDecoding.
    /// Volatile.Write гарантирует visibility новых значений Head/Tail для обоих потоков.
    /// </remarks>
    public void Clear()
    {
        Volatile.Write(ref _producer.Head, 0);
        Volatile.Write(ref _consumer.Tail, 0);
        _consumer.CachedHead = 0;
        _producer.CachedTail = 0;
    }

    /// <summary>
    /// Округляет значение вверх до ближайшей степени двойки.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundUpToPowerOf2(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}