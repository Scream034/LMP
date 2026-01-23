using System.Text.Json;

namespace MyLiteMusicPlayer.Core.Models;

/// <summary>
/// Отслеживает, какие байтовые диапазоны файла уже скачаны.
/// Поддерживает сохранение/загрузку для persistence между сессиями.
/// </summary>
public class RangeMap
{
    private readonly List<(long Start, long End)> _ranges = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Общее количество скачанных байт
    /// </summary>
    public long DownloadedBytes
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Sum(r => r.End - r.Start);
            }
        }
    }

    /// <summary>
    /// Проверяет, скачан ли полностью указанный диапазон
    /// </summary>
    public bool IsRangeComplete(long start, long end)
    {
        lock (_lock)
        {
            foreach (var range in _ranges)
            {
                if (range.Start <= start && range.End >= end)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Находит первый не скачанный участок в диапазоне
    /// </summary>
    public (long Start, long End)? FindMissingRange(long start, long end)
    {
        lock (_lock)
        {
            long current = start;
            
            var sortedRanges = _ranges.OrderBy(r => r.Start).ToList();
            
            foreach (var range in sortedRanges)
            {
                if (range.Start <= current && range.End > current)
                {
                    current = range.End;
                }
                else if (range.Start > current)
                {
                    return (current, Math.Min(range.Start, end));
                }
                
                if (current >= end)
                    return null;
            }
            
            if (current < end)
                return (current, end);
                
            return null;
        }
    }

    /// <summary>
    /// Помечает диапазон как скачанный
    /// </summary>
    public void MarkComplete(long start, long end)
    {
        lock (_lock)
        {
            _ranges.Add((start, end));
            MergeRanges();
        }
    }

    /// <summary>
    /// Объединяет пересекающиеся диапазоны
    /// </summary>
    private void MergeRanges()
    {
        if (_ranges.Count < 2) return;

        var sorted = _ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(long Start, long End)>();

        var current = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Start <= current.End)
            {
                current = (current.Start, Math.Max(current.End, sorted[i].End));
            }
            else
            {
                merged.Add(current);
                current = sorted[i];
            }
        }
        merged.Add(current);

        _ranges.Clear();
        _ranges.AddRange(merged);
    }

    /// <summary>
    /// Проверяет, полностью ли скачан файл
    /// </summary>
    public bool IsFullyDownloaded(long totalLength)
    {
        lock (_lock)
        {
            return _ranges.Count == 1 && 
                   _ranges[0].Start == 0 && 
                   _ranges[0].End >= totalLength;
        }
    }

    /// <summary>
    /// Сериализация для сохранения на диск
    /// </summary>
    public string Serialize()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_ranges);
        }
    }

    /// <summary>
    /// Десериализация из файла
    /// </summary>
    public static RangeMap Deserialize(string json)
    {
        var map = new RangeMap();
        try
        {
            var ranges = JsonSerializer.Deserialize<List<(long, long)>>(json);
            if (ranges != null)
            {
                lock (map._lock)
                {
                    map._ranges.AddRange(ranges);
                }
            }
        }
        catch { }
        return map;
    }

    public void Clear()
    {
        lock (_lock) { _ranges.Clear(); }
    }
}
