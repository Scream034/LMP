using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Адаптер для AsyncImageLoader с автоматическим управлением refCount.
/// ВАЖНО: AsyncImageLoader НЕ вызывает Dispose на возвращённых Bitmap,
/// поэтому мы полагаемся на RefCounting в ImageCacheService.
/// </summary>
public sealed class CachedImageLoader : IAsyncImageLoader
{
    private readonly ImageCacheService _cache;
    private readonly ImageQuality _defaultQuality;

    public CachedImageLoader(ImageCacheService cache, ImageQuality defaultQuality = ImageQuality.Low)
    {
        _cache = cache;
        _defaultQuality = defaultQuality;
    }

    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        // GetImageAsync уже вызывает AddRef() внутри
        return await _cache.GetImageAsync(url, _defaultQuality);
    }

    public void Dispose()
    {
        // Ничего не делаем - ImageCacheService управляет своим lifecycle
        GC.SuppressFinalize(this);
    }
}