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

public sealed class BoolToSelectorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string options)
        {
            var parts = options.Split('|');
            if (parts.Length == 2)
            {
                var resultString = b ? parts[0] : parts[1];

                // Если просят иконку
                if (targetType == typeof(MaterialIconKind) || targetType == typeof(object))
                {
                    if (Enum.TryParse<MaterialIconKind>(resultString, true, out var iconKind))
                        return iconKind;
                }

                // Если просят цвет (IBrush)
                if (typeof(IBrush).IsAssignableFrom(targetType))
                {
                    // 1. Пытаемся найти в ресурсах приложения
                    if (Avalonia.Application.Current != null && Avalonia.Application.Current.TryFindResource(resultString, out var res))
                    {
                        if (res is IBrush brush) return brush;
                        if (res is Color color) return new SolidColorBrush(color);
                    }

                    // 2. Если не ресурс, пытаемся распарсить как HEX
                    try
                    {
                        return SolidColorBrush.Parse(resultString);
                    }
                    catch
                    {
                        return Brushes.Transparent;
                    }
                }

                return resultString;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
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
            var loc = LocalizationService.Instance;
            return preference switch
            {
                AudioQualityPreference.BestAvailable => loc["Quality_BestAvailable"],
                AudioQualityPreference.Standard => loc["Quality_Standard"],
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
            if (uriStr.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;

            if (uriStr.StartsWith("avares://"))
            {
                try
                {
                    var uri = new Uri(uriStr);
                    if (!AssetLoader.Exists(uri)) return null;

                    using var stream = AssetLoader.Open(uri);
                    return new Bitmap(stream);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class StringStartsWithConverter : IValueConverter
{
    public static readonly StringStartsWithConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && parameter is string prefix)
        {
            return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
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
        return value is RepeatMode mode && mode == RepeatMode.RepeatOne 
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
                "RepeatAll" => mode == RepeatMode.RepeatAll,
                "RepeatOne" => mode == RepeatMode.RepeatOne,
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
/// Параметр: "TrueIcon|FalseIcon", например "Heart|HeartOutline"
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string options)
        {
            var parts = options.Split('|');
            if (parts.Length == 2)
            {
                var iconName = b ? parts[0] : parts[1];
                if (Enum.TryParse<MaterialIconKind>(iconName, true, out var iconKind))
                    return iconKind;
            }
        }
        return MaterialIconKind.Help; // fallback
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