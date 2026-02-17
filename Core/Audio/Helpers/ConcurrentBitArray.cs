using System.Numerics;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Lock-free потокобезопасный битовый массив.
/// </summary>
public sealed class ConcurrentBitArray(int length)
{
    private readonly int[] _data = new int[(length + 31) / 32];

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
    
    public int PopCount()
    {
        int count = 0;
        for (int i = 0; i < _data.Length; i++)
        {
            count += BitOperations.PopCount((uint)Volatile.Read(ref _data[i]));
        }
        return Math.Min(count, Length);
    }
    
    public void Clear()
    {
        Array.Clear(_data);
    }
    
    /// <summary>
    /// Сериализует в Base64 для сохранения.
    /// </summary>
    public string ToBase64()
    {
        var bytes = new byte[_data.Length * 4];
        Buffer.BlockCopy(_data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
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
            Buffer.BlockCopy(bytes, 0, _data, 0, Math.Min(bytes.Length, _data.Length * 4));
        }
        catch
        {
            // Ignore invalid data
        }
    }
}