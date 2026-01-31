using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

public class CachedImageLoader(ImageCacheService cache, ImageQuality defaultQuality = ImageQuality.Low) : IAsyncImageLoader
{
    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        return await cache.GetImageAsync(url, defaultQuality);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}