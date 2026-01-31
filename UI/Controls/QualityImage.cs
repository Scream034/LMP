using Avalonia;
using Avalonia.Controls;
using LMP.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Attached properties для загрузки изображений с контролем качества.
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

    private static void OnDecodeSizeChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        LoadImage(image);
    }

    private static async void LoadImage(Image image)
    {
        var url = GetSource(image);

        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            image.Source = null;
            return;
        }

        try
        {
            var cache = Program.Services.GetService<ImageCacheService>();
            if (cache == null) return;

            // Приоритет: DecodeSize (числовой) > Quality (enum)
            int decodeWidth = (int)GetQuality(image);
            
            var bitmap = await cache.GetImageAsync(url, decodeWidth);

            // Проверяем, что URL не изменился пока грузили
            if (GetSource(image) == url)
            {
                image.Source = bitmap;
            }
        }
        catch
        {
            image.Source = null;
        }
    }
}