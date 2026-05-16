using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Lock-free потокобезопасный битовый массив.
/// </summary>
public sealed class ConcurrentBitArray(int length)
{
    private readonly int[] _data = new int[(length + 31) >> 5];

    public int Length { get; } = length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        if ((uint)index >= (uint)Length) return false;
        return (Volatile.Read(ref _data[index >> 5]) & (1 << (index & 31))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, bool value)
    {
        if ((uint)index >= (uint)Length) return;

        int word = index >> 5;
        int bit = 1 << (index & 31);

        if (value)
        {
            Interlocked.Or(ref _data[word], bit);
        }
        else
        {
            Interlocked.And(ref _data[word], ~bit);
        }
    }

    /// <summary>
    /// Подсчитывает количество установленных битов.
    /// <see cref="BitOperations.PopCount(uint)"/> компилируется в аппаратную инструкцию POPCNT
    /// на поддерживаемых платформах (x86 SSE4.2+, ARM).
    /// <see cref="Unsafe.Add{T}(ref T, int)"/> исключает bounds check в цикле.
    /// Volatile.Read не используется — допускается eventual consistency для агрегатного подсчёта.
    /// </summary>
    public int PopCount()
    {
        int count = 0;
        ref int dataRef = ref MemoryMarshal.GetArrayDataReference(_data);

        for (int i = 0; i < _data.Length; i++)
        {
            count += BitOperations.PopCount((uint)Unsafe.Add(ref dataRef, i));
        }

        return Math.Min(count, Length);
    }

    public void Clear()
    {
        Array.Clear(_data);
    }

    /// <summary>
    /// Сериализует в Base64 для сохранения.
    /// Использует <see cref="ArrayPool{T}"/> для промежуточного буфера, избегая аллокаций.
    /// </summary>
    public string ToBase64()
    {
        int byteCount = _data.Length * sizeof(int);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Buffer.BlockCopy(_data, 0, rented, 0, byteCount);
            return Convert.ToBase64String(rented, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Загружает из Base64.
    /// </summary>
    public void FromBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            Buffer.BlockCopy(bytes, 0, _data, 0, Math.Min(bytes.Length, _data.Length * sizeof(int)));
        }
        catch
        {
            // Ignore invalid data
        }
    }
}