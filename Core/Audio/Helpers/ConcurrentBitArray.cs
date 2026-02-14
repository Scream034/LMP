// Core/Audio/Helpers/ConcurrentBitArray.cs

using System.Numerics;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Lock-free потокобезопасный битовый массив.
/// </summary>
public sealed class ConcurrentBitArray
{
    private readonly int[] _data;
    private readonly int _length;
    
    public ConcurrentBitArray(int length)
    {
        _length = length;
        _data = new int[(length + 31) / 32];
    }
    
    public int Length => _length;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        if ((uint)index >= (uint)_length) return false;
        return (Volatile.Read(ref _data[index >> 5]) & (1 << (index & 31))) != 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, bool value)
    {
        if ((uint)index >= (uint)_length) return;
        
        int word = index >> 5;
        int bit = 1 << (index & 31);
        int current, desired;
        
        do
        {
            current = Volatile.Read(ref _data[word]);
            desired = value ? (current | bit) : (current & ~bit);
            if (desired == current) return;
        } while (Interlocked.CompareExchange(ref _data[word], desired, current) != current);
    }
    
    public int PopCount()
    {
        int count = 0;
        for (int i = 0; i < _data.Length; i++)
        {
            count += BitOperations.PopCount((uint)Volatile.Read(ref _data[i]));
        }
        return Math.Min(count, _length);
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
        catch { }
    }
}