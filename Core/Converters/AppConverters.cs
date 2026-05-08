using Avalonia.Data.Converters;
using Avalonia.Media;
using LMP.Core.Models;
using System.Globalization;
using Material.Icons;
using Avalonia.Input;
using LMP.Core.Services;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia;

namespace LMP.Core.Converters;

public sealed class TimeSpanToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        return "0:00";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value is bool b && b) ? 1.0 : 0.5;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "invert";
        bool isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
        return invert ? isNull : !isNull;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public sealed class VolumeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float vol)
        {
            return vol switch
            {
                0 => MaterialIconKind.VolumeOff,
                < 0.33f => MaterialIconKind.VolumeLow,
                < 0.66f => MaterialIconKind.VolumeMedium,
                _ => MaterialIconKind.VolumeHigh
            };
        }
        return MaterialIconKind.VolumeHigh;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Конвертирует bool в одно из двух значений по параметру "TrueValue|FalseValue".
/// Кэширует результат парсинга параметра: Split + TryParse/Find вызываются
/// один раз на уникальный параметр вместо каждого вызова конвертера.
/// </summary>
public sealed class BoolToSelectorConverter : IValueConverter
{
    /// <summary>
    /// Кэш разобранных пар параметров. Ключ — строка параметра, значение — пара строк.
    /// Параметров конечное число (~10-15 в приложении).
    /// </summary>
    private static readonly Dictionary<string, (string TrueVal, string FalseVal)> _pairCache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || parameter is not string key) return null;

        if (!_pairCache.TryGetValue(key, out var pair))
        {
            var sep = key.IndexOf('|');
            pair = sep >= 0
                ? (key[..sep], key[(sep + 1)..])
                : (key, key);
            _pairCache[key] = pair;
        }

        var chosen = b ? pair.TrueVal : pair.FalseVal;

        // MaterialIconKind
        if (targetType == typeof(MaterialIconKind) || targetType == typeof(object))
        {
            if (Enum.TryParse<MaterialIconKind>(chosen, true, out var iconKind))
                return iconKind;
        }

        // IBrush
        if (typeof(IBrush).IsAssignableFrom(targetType))
        {
            if (Application.Current?.TryFindResource(chosen, out var res) == true)
            {
                if (res is IBrush brush) return brush;
                if (res is Color color) return new SolidColorBrush(color);
            }

            try { return SolidColorBrush.Parse(chosen); }
            catch { return Brushes.Transparent; }
        }

        return chosen;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class BoolToCursorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && parameter is string param)
        {
            var parts = param.Split('|');
            if (parts.Length == 2)
            {
                var cursorName = boolValue ? parts[0] : parts[1];
                return cursorName switch
                {
                    "Hand" => new Cursor(StandardCursorType.Hand),
                    "Arrow" => new Cursor(StandardCursorType.Arrow),
                    "Wait" => new Cursor(StandardCursorType.Wait),
                    _ => Cursor.Default
                };
            }
        }
        return Cursor.Default;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class ToUpperConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str.ToUpper(culture);
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class AudioQualityToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AudioQualityPreference preference)
        {
            var L = LocalizationService.Instance;
            return preference switch
            {
                AudioQualityPreference.BestAvailable => L["Quality_BestAvailable"],
                AudioQualityPreference.Standard => L["Quality_Standard"],
                _ => preference.ToString()
            };
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class BitmapAssetValueConverter : IValueConverter
{
    public static readonly BitmapAssetValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string uriStr && !string.IsNullOrEmpty(uriStr))
        {
            // 1. HTTP/HTTPS — пропускаем (обрабатывается AsyncImageLoader)
            if (uriStr.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                // 2. avares:// — ресурсы Avalonia
                if (uriStr.StartsWith("avares://"))
                {
                    var uri = new Uri(uriStr);
                    if (AssetLoader.Exists(uri))
                    {
                        using var stream = AssetLoader.Open(uri);
                        return new Bitmap(stream);
                    }
                    return null;
                }

                // 3. file:// URI — конвертируем в локальный путь
                if (uriStr.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(uriStr, UriKind.Absolute, out var fileUri))
                    {
                        uriStr = fileUri.LocalPath;
                    }
                }

                // 4. Локальный путь к файлу
                if (Path.IsPathRooted(uriStr) && File.Exists(uriStr))
                {
                    // ВАЖНО: Используем FileStream с FileShare.Read чтобы не блокировать файл
                    using var stream = new FileStream(uriStr, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new Bitmap(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[BitmapConverter] Failed to load image '{uriStr}': {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class StringStartsWithConverter : IValueConverter
{
    public static readonly StringStartsWithConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && parameter is string prefix)
        {
            bool negate = false;
            if (prefix.StartsWith('!'))
            {
                negate = true;
                prefix = prefix[1..];
            }

            bool result = s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            return negate ? !result : result;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class WebUrlConverter : IValueConverter
{
    public static readonly WebUrlConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 &&
            values[0] is double progress &&
            values[1] is double containerWidth)
        {
            return Math.Max(0, containerWidth * progress);
        }
        return 0.0;
    }
}

public sealed class GreaterThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Проверяет, больше ли значение (value) чем параметр (parameter)
        if (value is double d && double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double threshold))
        {
            return d > threshold;
        }
        if (value is int i && int.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out int iThreshold))
        {
            return i > iThreshold;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Конвертер для иконки режима повтора.
/// </summary>
public sealed class RepeatModeIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is RepeatMode mode && mode == RepeatMode.One
            ? MaterialIconKind.RepeatOne
            : MaterialIconKind.Repeat;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Конвертер для цвета иконки режима повтора.
/// </summary>
public sealed class RepeatModeActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is RepeatMode mode && mode != RepeatMode.None;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Конвертер для проверки конкретного режима повтора.
/// </summary>
public sealed class RepeatModeEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RepeatMode mode && parameter is string modeStr)
        {
            return modeStr switch
            {
                "RepeatAll" => mode == RepeatMode.All,
                "RepeatOne" => mode == RepeatMode.One,
                "None" => mode == RepeatMode.None,
                _ => false
            };
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Сравнивает Enum значение с параметром. Возвращает true, если совпадают.
/// Используется для RadioButton GroupBinding.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;

        string checkValue = value.ToString() ?? "";
        string targetValue = parameter.ToString() ?? "";

        return checkValue.Equals(targetValue, StringComparison.InvariantCultureIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // OneWay binding обычно достаточно для RadioButton + Command, 
        // но если нужно TwoWay, здесь можно возвращать Enum.Parse
        return value is bool b && b ? parameter : BindingOperations.DoNothing;
    }
}

/// <summary>
/// Конвертирует bool в FontWeight (true = Bold, false = Normal)
/// </summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return FontWeight.Bold;
        return FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Конвертирует bool в MaterialIconKind.
/// Параметр: "TrueIcon|FalseIcon", например "Heart|HeartOutline".
/// 
/// Кэширует результат парсинга параметра: string.Split('|') + Enum.TryParse
/// вызываются один раз на уникальный параметр вместо ~160 раз за цикл рециклинга
/// (8 конвертеров × 20 видимых элементов).
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    /// <summary>
    /// Кэш: "Heart|HeartOutline" → (MaterialIconKind.Heart, MaterialIconKind.HeartOutline).
    /// Параметров конечное число (~5-8 в приложении), словарь не растёт бесконтрольно.
    /// </summary>
    private static readonly Dictionary<string, (MaterialIconKind TrueIcon, MaterialIconKind FalseIcon)?> _cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || parameter is not string key)
            return MaterialIconKind.Help;

        if (!_cache.TryGetValue(key, out var pair))
        {
            pair = ParseIconPair(key);
            _cache[key] = pair;
        }

        return pair.HasValue
            ? (b ? pair.Value.TrueIcon : pair.Value.FalseIcon)
            : MaterialIconKind.Help;
    }

    private static (MaterialIconKind, MaterialIconKind)? ParseIconPair(string options)
    {
        var separatorIndex = options.IndexOf('|');
        if (separatorIndex < 0) return null;

        var truePart = options.AsSpan(0, separatorIndex);
        var falsePart = options.AsSpan(separatorIndex + 1);

        if (Enum.TryParse<MaterialIconKind>(truePart, true, out var trueIcon) &&
            Enum.TryParse<MaterialIconKind>(falsePart, true, out var falseIcon))
        {
            return (trueIcon, falseIcon);
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Конвертирует bool в видимость window buttons
/// </summary>
public sealed class WindowButtonVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isMaximized && parameter is string mode)
        {
            return mode switch
            {
                "Maximize" => !isMaximized,
                "Restore" => isMaximized,
                _ => true
            };
        }
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Конвертирует NotificationSeverity в Color из текущей темы.
/// Error → SystemError, Warning → SystemWarnOrange, Info → SystemInfoBlue, Success → Accent.
/// </summary>
public sealed class SeverityToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value switch
        {
            NotificationSeverity s => s,
            int i => (NotificationSeverity)i,
            _ => NotificationSeverity.Info
        };

        var resourceKey = severity switch
        {
            NotificationSeverity.Error => "SystemError",
            NotificationSeverity.Warning => "SystemWarnOrange",
            NotificationSeverity.Info => "SystemInfoBlue",
            NotificationSeverity.Success => "Accent",
            _ => "TextMuted"
        };

        if (Avalonia.Application.Current?.Resources.TryGetResource(resourceKey, null, out var resource) == true)
        {
            if (resource is Color color) return color;
            if (resource is SolidColorBrush brush) return brush.Color;
        }

        // Fallback
        return severity switch
        {
            NotificationSeverity.Error => Color.Parse("#FF5555"),
            NotificationSeverity.Warning => Color.Parse("#FFB86C"),
            NotificationSeverity.Info => Color.Parse("#8BE9FD"),
            NotificationSeverity.Success => Color.Parse("#50C878"),
            _ => Color.Parse("#6272A4")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Возвращает URL изображения только когда скролл остановлен.
/// При быстром скролле возвращает null → ImageLoader не загружает картинку,
/// отображается фоновый placeholder (BgSkeletonBrush).
/// 
/// values[0] = string ThumbnailUrl
/// values[1] = bool IsScrollingFast
/// </summary>
public sealed class DeferredUrlConverter : IMultiValueConverter
{
    public static readonly DeferredUrlConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Count: >= 2 })
            return null;

        if (values[0] is not string url || string.IsNullOrEmpty(url))
            return null;

        // values[1] может быть UnsetValue при recycling в VirtualizingStackPanel
        var isScrollingFast = values[1] is true;

        return isScrollingFast ? null : url;
    }
}