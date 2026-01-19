// Services/CachedImageLoader.cs
using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Кастомный загрузчик для AsyncImageLoader с поддержкой нашего кэша
/// </summary>
public class CachedImageLoader : IAsyncImageLoader
{
    private readonly ImageCacheService _cache;

    public CachedImageLoader(ImageCacheService cache)
    {
        _cache = cache;
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        return await _cache.GetImageAsync(url);
    }
}