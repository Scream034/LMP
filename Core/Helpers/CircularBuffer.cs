using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Lock-free циклический буфер для single-producer-single-consumer сценария.
/// Оптимизирован для аудио данных с правильными memory barriers.
/// </summary>
/// <typeparam name="T">Тип элементов (unmanaged)</typeparam>
public sealed class CircularBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;
    
    private int _head; // Позиция записи (producer)
    private int _tail; // Позиция чтения (consumer)
    
    /// <summary>
    /// Создаёт циклический буфер указанной ёмкости.
    /// </summary>
    /// <param name="capacity">Минимальная ёмкость (округляется до степени 2)</param>
    public CircularBuffer(int capacity)
    {
        _capacity = RoundUpToPowerOf2(Math.Max(capacity, 16));
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
    }
    
    /// <summary>Текущее количество элементов в буфере</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Читаем head первым (producer пишет сюда)
            int head = Volatile.Read(ref _head);
            // Затем tail (consumer пишет сюда)
            int tail = Volatile.Read(ref _tail);
            return (head - tail + _capacity) & _mask;
        }
    }
    
    /// <summary>Ёмкость буфера</summary>
    public int Capacity => _capacity;
    
    /// <summary>Свободное место в буфере (оставляем 1 слот для различения full/empty)</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - Count - 1;
    }
    
    /// <summary>Буфер пуст</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _head) == Volatile.Read(ref _tail);
    }
    
    /// <summary>
    /// Записывает данные в буфер (producer).
    /// Thread-safe для одного producer.
    /// </summary>
    /// <param name="data">Данные для записи</param>
    /// <returns>Количество записанных элементов</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<T> data)
    {
        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);
        
        int available = (_capacity - 1) - ((head - tail + _capacity) & _mask);
        int toWrite = Math.Min(data.Length, available);
        
        if (toWrite == 0) return 0;
        
        int firstPart = Math.Min(toWrite, _capacity - head);
        
        // Копируем первую часть (до конца массива)
        data[..firstPart].CopyTo(_buffer.AsSpan(head, firstPart));
        
        // Копируем вторую часть (с начала массива) если нужно
        if (toWrite > firstPart)
        {
            data.Slice(firstPart, toWrite - firstPart)
                .CopyTo(_buffer.AsSpan(0, toWrite - firstPart));
        }
        
        // Release fence: все записи в буфер видны перед обновлением head
        Volatile.Write(ref _head, (head + toWrite) & _mask);
        
        return toWrite;
    }
    
    /// <summary>
    /// Читает данные из буфера (consumer).
    /// Thread-safe для одного consumer.
    /// </summary>
    /// <param name="output">Буфер для чтения</param>
    /// <returns>Количество прочитанных элементов</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<T> output)
    {
        int tail = Volatile.Read(ref _tail);
        int head = Volatile.Read(ref _head);
        
        int count = (head - tail + _capacity) & _mask;
        int toRead = Math.Min(output.Length, count);
        
        if (toRead == 0) return 0;
        
        int firstPart = Math.Min(toRead, _capacity - tail);
        
        // Копируем первую часть
        _buffer.AsSpan(tail, firstPart).CopyTo(output[..firstPart]);
        
        // Копируем вторую часть если нужно
        if (toRead > firstPart)
        {
            _buffer.AsSpan(0, toRead - firstPart)
                .CopyTo(output.Slice(firstPart, toRead - firstPart));
        }
        
        // Release fence: все чтения завершены перед обновлением tail
        Volatile.Write(ref _tail, (tail + toRead) & _mask);
        
        return toRead;
    }
    
    /// <summary>
    /// Читает данные без удаления из буфера (peek).
    /// </summary>
    public int Peek(Span<T> output)
    {
        int tail = Volatile.Read(ref _tail);
        int head = Volatile.Read(ref _head);
        
        int count = (head - tail + _capacity) & _mask;
        int toRead = Math.Min(output.Length, count);
        
        if (toRead == 0) return 0;
        
        int firstPart = Math.Min(toRead, _capacity - tail);
        _buffer.AsSpan(tail, firstPart).CopyTo(output[..firstPart]);
        
        if (toRead > firstPart)
        {
            _buffer.AsSpan(0, toRead - firstPart)
                .CopyTo(output.Slice(firstPart, toRead - firstPart));
        }
        
        return toRead;
    }
    
    /// <summary>
    /// Пропускает указанное количество элементов.
    /// </summary>
    public int Skip(int count)
    {
        int tail = Volatile.Read(ref _tail);
        int head = Volatile.Read(ref _head);
        
        int available = (head - tail + _capacity) & _mask;
        int toSkip = Math.Min(count, available);
        
        if (toSkip > 0)
        {
            Volatile.Write(ref _tail, (tail + toSkip) & _mask);
        }
        
        return toSkip;
    }
    
    /// <summary>
    /// Очищает буфер.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
    
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