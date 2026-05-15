using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Attached properties для загрузки изображений с контролем качества и очисткой памяти.
/// </summary>
public static class QualityImage
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(QualityImage));

    public static readonly AttachedProperty<ImageQuality> QualityProperty =
        AvaloniaProperty.RegisterAttached<Image, ImageQuality>("Quality", typeof(QualityImage),
            defaultValue: ImageQuality.Low);

    /// <summary>
    /// Внутренний маркер: true, если Bitmap был создан специально для этого Image (локальный файл).
    /// Если true — мы обязаны задиспозить его при смене Source.
    /// Если false — Bitmap пришел из кэша, его трогать нельзя.
    /// </summary>
    private static readonly AttachedProperty<bool> IsSourceOwnedProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsSourceOwned", typeof(QualityImage));

    static QualityImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
        QualityProperty.Changed.AddClassHandler<Image>(OnQualityChanged);
    }

    public static string? GetSource(Image element) => element.GetValue(SourceProperty);
    public static void SetSource(Image element, string? value) => element.SetValue(SourceProperty, value);

    public static ImageQuality GetQuality(Image element) => element.GetValue(QualityProperty);
    public static void SetQuality(Image element, ImageQuality value) => element.SetValue(QualityProperty, value);

    private static void OnSourceChanged(Image image, AvaloniaPropertyChangedEventArgs e) => LoadImage(image);
    private static void OnQualityChanged(Image image, AvaloniaPropertyChangedEventArgs e) => LoadImage(image);

    private static async void LoadImage(Image image)
    {
        var url = GetSource(image);

        if (string.IsNullOrEmpty(url))
        {
            CleanupCurrentSource(image);
            image.Source = null;
            return;
        }

        try
        {
            ReadOnlySpan<char> urlSpan = url.AsSpan();

            // ═══ 1. HTTP/HTTPS (Shared from Cache) ═══
            if (urlSpan.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var cache = Program.Services.GetService<ImageCacheService>();
                if (cache == null) return;

                int decodeWidth = (int)GetQuality(image);
                var bitmap = await cache.GetImageAsync(url, decodeWidth);

                if (GetSource(image) == url)
                {
                    CleanupCurrentSource(image);
                    image.Source = bitmap;
                    // Маркируем как "не наш": кэш сам управляет временем жизни
                    image.SetValue(IsSourceOwnedProperty, false); 
                }
                return;
            }

            // ═══ 2. avares:// (Owned by Control) ═══
            if (urlSpan.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(url);
                if (Avalonia.Platform.AssetLoader.Exists(uri))
                {
                    using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                    int decodeWidth = (int)GetQuality(image);
                    
                    var bitmap = decodeWidth > 0
                        ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                        : new Bitmap(stream);

                    if (GetSource(image) == url)
                    {
                        CleanupCurrentSource(image);
                        image.Source = bitmap;
                        image.SetValue(IsSourceOwnedProperty, true); // Мы создали — мы диспозим
                    }
                    else
                    {
                        bitmap.Dispose(); // URL уже сменился, пока мы декодировали
                    }
                }
                return;
            }

            // ═══ 3. Local File (Owned by Control) ═══
            var localPath = ResolveLocalPath(url);
            if (localPath != null && File.Exists(localPath))
            {
                int decodeW = (int)GetQuality(image);
                var bitmap = await Task.Run(() =>
                {
                    using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return decodeW > 0
                        ? Bitmap.DecodeToWidth(stream, decodeW, BitmapInterpolationMode.MediumQuality)
                        : new Bitmap(stream);
                });

                if (GetSource(image) == url)
                {
                    CleanupCurrentSource(image);
                    image.Source = bitmap;
                    image.SetValue(IsSourceOwnedProperty, true); // Мы создали — мы диспозим
                }
                else
                {
                    bitmap.Dispose();
                }
                return;
            }

            CleanupCurrentSource(image);
            image.Source = null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[QualityImage] Failed to load '{url}': {ex.Message}");
            CleanupCurrentSource(image);
            image.Source = null;
        }
    }

    /// <summary>
    /// Атомарно проверяет текущий Source. Если он "Owned" (создан локально),
    /// вызывает Dispose() для немедленного освобождения нативной памяти.
    /// </summary>
    private static void CleanupCurrentSource(Image image)
    {
        if (image.Source is Bitmap oldBitmap && image.GetValue(IsSourceOwnedProperty))
        {
            oldBitmap.Dispose();
            image.SetValue(IsSourceOwnedProperty, false);
        }
    }

    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var fileUri) ? fileUri.LocalPath : null;
        }
        return Path.IsPathRooted(url) ? url : null;
    }
}