using System.Text.Json;

namespace LMP.Core.Models;

/// <summary>
/// Serializable item for JSON storage
/// </summary>
public class RangeItem
{
    public long Start { get; set; }
    public long End { get; set; }
}

public class RangeMap
{
    // ИСПРАВЛЕНИЕ: Используем класс вместо ValueTuple для надежной сериализации
    private readonly List<RangeItem> _ranges = new();
    private readonly Lock _lock = new();

    public long DownloadedBytes
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Sum(static r => r.End - r.Start);
            }
        }
    }

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

    public void MarkComplete(long start, long end)
    {
        lock (_lock)
        {
            _ranges.Add(new RangeItem { Start = start, End = end });
            MergeRanges();
        }
    }

    private void MergeRanges()
    {
        if (_ranges.Count < 2) return;

        var sorted = _ranges.OrderBy(static r => r.Start).ToList();
        var merged = new List<RangeItem>();

        var current = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Start <= current.End)
            {
                // Расширяем текущий диапазон
                current.End = Math.Max(current.End, sorted[i].End);
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
            if (_ranges.Count == 0)
            {
                // Log.Debug($"IsFullyDownloaded: NO ranges, total={totalLength}");
                return false;
            }

            if (_ranges.Count > 1)
            {
                // Log.Debug($"IsFullyDownloaded: {_ranges.Count} ranges (not merged?), total={totalLength}");
                return false;
            }

            var range = _ranges[0];
            bool result = range.Start == 0 && range.End >= totalLength;

            return result;
        }
    }

    public string Serialize()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_ranges);
        }
    }

    public static RangeMap Deserialize(string json)
    {
        var map = new RangeMap();
        if (string.IsNullOrWhiteSpace(json)) return map;

        try
        {
            var ranges = JsonSerializer.Deserialize<List<RangeItem>>(json);
            if (ranges != null)
            {
                lock (map._lock)
                {
                    map._ranges.AddRange(ranges);
                }
            }
        }
        catch (Exception)
        {
            // Если JSON старого формата (кортежи), пробуем проигнорировать или восстановить.
            // Сейчас просто возвращаем пустой, но новый формат (RangeItem) исправит проблему в будущем.
        }
        return map;
    }
}