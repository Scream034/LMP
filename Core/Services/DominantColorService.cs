using System.Collections.Concurrent;
using Avalonia.Media;
using SkiaSharp;

namespace LMP.Core.Services;

/// <summary>
/// Извлекает доминантный цвет из обложек для градиентных фонов.
/// Алгоритм: загрузить → resize 50x50 → фильтрация по L → вес по S → усреднение.
/// </summary>
public sealed class DominantColorService
{
    private readonly ImageCacheService _imageCache;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Color> _cache = new();

    private const float MinLuminance = 0.1f;
    private const float MaxLuminance = 0.9f;
    private const float MinSaturation = 0.15f;
    private const int SampleSize = 50;

    public DominantColorService(ImageCacheService imageCache)
    {
        _imageCache = imageCache;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Получает доминантный цвет из изображения.
    /// Кэшируется в памяти по URL.
    /// </summary>
    public async Task<Color?> GetDominantColorAsync(string? imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        if (_cache.TryGetValue(imageUrl, out var cached))
            return cached;

        try
        {
            var color = await ExtractColorAsync(imageUrl, ct);

            if (color.HasValue)
            {
                _cache[imageUrl] = color.Value;
                Log.Debug($"[DominantColor] Extracted: R={color.Value.R} G={color.Value.G} B={color.Value.B} from {imageUrl[..Math.Min(60, imageUrl.Length)]}");
            }

            return color;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DominantColor] Failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Загружает изображение через disk-cache ImageCacheService
    /// и декодирует напрямую через SkiaSharp (без Avalonia Bitmap).
    /// </summary>
    private async Task<Color?> ExtractColorAsync(string imageUrl, CancellationToken ct)
    {
        // Путь к файлу на диске через рефлексию hash-метода ImageCacheService
        // Или загружаем сами напрямую
        byte[]? imageData = null;

        // Пробуем загрузить из disk-cache ImageCacheService
        // ImageCacheService хранит файлы в G.Folder.ImageCache с именем = SHA256(url)[..32]
        var diskKey = GetDiskCacheKey(imageUrl);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskKey);

        if (File.Exists(diskPath))
        {
            imageData = await File.ReadAllBytesAsync(diskPath, ct);
        }
        else
        {
            // Сначала попросим ImageCacheService загрузить (это создаст файл на диске)
            var bitmap = await _imageCache.GetImageAsync(imageUrl, ImageQuality.Low, ct);

            // Теперь файл должен быть на диске
            if (File.Exists(diskPath))
            {
                imageData = await File.ReadAllBytesAsync(diskPath, ct);
            }
            else
            {
                // Fallback: скачиваем сами
                try
                {
                    imageData = await _http.GetByteArrayAsync(imageUrl, ct);
                }
                catch
                {
                    return null;
                }
            }
        }

        if (imageData == null || imageData.Length == 0)
            return null;

        return await Task.Run(() =>
        {
            using var skBitmap = SKBitmap.Decode(imageData);
            if (skBitmap == null)
            {
                Log.Debug("[DominantColor] SKBitmap.Decode returned null");
                return (Color?)null;
            }

            // Resize до 50x50
            using var resized = skBitmap.Resize(
                new SKImageInfo(SampleSize, SampleSize),
                SKFilterQuality.Low);

            if (resized == null)
                return null;

            return (Color?)AnalyzePixels(resized);
        }, ct);
    }

    /// <summary>
    /// Анализ пикселей: фильтрация → взвешивание по насыщенности → усреднение.
    /// </summary>
    private static Color AnalyzePixels(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        if (pixels.Length == 0)
            return Colors.Gray;

        double totalR = 0, totalG = 0, totalB = 0;
        double totalWeight = 0;

        foreach (var pixel in pixels)
        {
            float r = pixel.Red / 255f;
            float g = pixel.Green / 255f;
            float b = pixel.Blue / 255f;
            float a = pixel.Alpha / 255f;

            if (a < 0.5f) continue;

            RgbToHsl(r, g, b, out _, out float s, out float l);

            if (l < MinLuminance || l > MaxLuminance)
                continue;

            // Вес = насыщенность. Ненасыщенные серые имеют малый вклад
            float weight = s < MinSaturation ? s * 0.1f : s;

            totalR += r * weight;
            totalG += g * weight;
            totalB += b * weight;
            totalWeight += weight;
        }

        if (totalWeight < 0.001)
        {
            // Все пиксели отфильтрованы — вернём средний серый-тёмный
            return Color.FromRgb(60, 60, 80);
        }

        byte avgR = (byte)Math.Clamp(totalR / totalWeight * 255, 0, 255);
        byte avgG = (byte)Math.Clamp(totalG / totalWeight * 255, 0, 255);
        byte avgB = (byte)Math.Clamp(totalB / totalWeight * 255, 0, 255);

        return Color.FromRgb(avgR, avgG, avgB);
    }

    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        h = 0;
        l = (max + min) / 2f;

        if (delta < 0.001f)
        {
            s = 0;
            return;
        }

        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

        if (max == r)
            h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
        else if (max == g)
            h = ((b - r) / delta + 2f) / 6f;
        else
            h = ((r - g) / delta + 4f) / 6f;
    }

    /// <summary>
    /// Тот же алгоритм хеширования что в ImageCacheService.
    /// </summary>
    private static string GetDiskCacheKey(string url)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    /// <summary>
    /// Создаёт 3-stop градиент для header фона.
    /// </summary>
    public static LinearGradientBrush CreateHeaderGradient(Color dominant)
    {
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(200, dominant.R, dominant.G, dominant.B), 0.0),
                new GradientStop(Color.FromArgb(80, dominant.R, dominant.G, dominant.B), 0.5),
                new GradientStop(Color.FromArgb(0, dominant.R, dominant.G, dominant.B), 1.0)
            }
        };
    }

    public void ClearCache() => _cache.Clear();
}