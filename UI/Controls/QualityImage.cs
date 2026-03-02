using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Attached properties для загрузки изображений с контролем качества.
/// 
/// <para><b>Поддерживаемые источники:</b></para>
/// <list type="bullet">
///   <item>HTTP/HTTPS URL → загрузка через <see cref="ImageCacheService"/></item>
///   <item>Локальный путь (абсолютный) → прямое декодирование из файла</item>
///   <item>avares:// URI → загрузка встроенного ресурса через <see cref="Avalonia.Platform.AssetLoader"/></item>
/// </list>
/// </summary>
public static class QualityImage
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(QualityImage));

    public static readonly AttachedProperty<ImageQuality> QualityProperty =
        AvaloniaProperty.RegisterAttached<Image, ImageQuality>("Quality", typeof(QualityImage),
            defaultValue: ImageQuality.Low);

    static QualityImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
        QualityProperty.Changed.AddClassHandler<Image>(OnQualityChanged);
    }

    // Source
    public static string? GetSource(Image element) => element.GetValue(SourceProperty);
    public static void SetSource(Image element, string? value) => element.SetValue(SourceProperty, value);

    // Quality (enum)
    public static ImageQuality GetQuality(Image element) => element.GetValue(QualityProperty);
    public static void SetQuality(Image element, ImageQuality value) => element.SetValue(QualityProperty, value);

    private static void OnSourceChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        LoadImage(image);
    }

    private static void OnQualityChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        LoadImage(image);
    }

    private static async void LoadImage(Image image)
    {
        var url = GetSource(image);

        if (string.IsNullOrEmpty(url))
        {
            image.Source = null;
            return;
        }

        try
        {
            // ═══ 1. HTTP/HTTPS → ImageCacheService ═══
            if (url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                var cache = Program.Services.GetService<ImageCacheService>();
                if (cache == null) return;

                int decodeWidth = (int)GetQuality(image);
                var bitmap = await cache.GetImageAsync(url, decodeWidth);

                // Проверяем, что URL не изменился пока грузили
                if (GetSource(image) == url)
                {
                    image.Source = bitmap;
                }
                return;
            }

            // ═══ 2. avares:// → встроенный ресурс Avalonia ═══
            if (url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
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
                        image.Source = bitmap;
                    }
                }
                return;
            }

            // ═══ 3. Локальный файл (абсолютный путь или file:// URI) ═══
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
                    image.Source = bitmap;
                }
                return;
            }

            // ═══ 4. Нераспознанный источник ═══
            image.Source = null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[QualityImage] Failed to load '{url}': {ex.Message}");
            image.Source = null;
        }
    }

    /// <summary>
    /// Преобразует file:// URI или абсолютный путь в локальный путь файловой системы.
    /// </summary>
    /// <returns>Локальный путь или null если не удалось распознать.</returns>
    private static string? ResolveLocalPath(string url)
    {
        // file:// URI
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }

        // Абсолютный путь файловой системы (C:\..., /home/...)
        if (Path.IsPathRooted(url))
            return url;

        return null;
    }
}