using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Адаптер для AsyncImageLoader с автоматическим управлением refCount.
/// 
/// <para><b>ВАЖНО:</b> принимает только HTTP/HTTPS URL.
/// Локальные пути и невалидные строки отклоняются для предотвращения
/// мусорных сетевых запросов (например, hex-хеши парсящиеся как URI).</para>
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
        if (string.IsNullOrEmpty(url))
            return null;

        // Строгая проверка: только http:// и https://
        if (!url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
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