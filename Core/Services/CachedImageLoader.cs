// Services/CachedImageLoader.cs
using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Кастомный загрузчик для AsyncImageLoader с поддержкой нашего кэша
/// </summary>
public class CachedImageLoader(ImageCacheService cache) : IAsyncImageLoader
{
    private readonly ImageCacheService _cache = cache;

    public void Dispose()
    {
        _cache.Dispose();
    }

    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        // ИСПРАВЛЕНИЕ: Игнорируем локальные ресурсы и пустые строки, 
        // чтобы HttpClient не падал с ошибкой "Scheme not supported"
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _cache.GetImageAsync(url);
    }
}
