using Avalonia.Data.Converters;
using Avalonia.Media;
using MyLiteMusicPlayer.Core.Models;
using System.Globalization;
using Material.Icons;
using Avalonia.Input;
using MyLiteMusicPlayer.Core.Services;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Controls;

namespace MyLiteMusicPlayer.Core.Converters;

public class TimeSpanToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        return "0:00";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value is bool b && b) ? 1.0 : 0.5;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "invert";
        bool isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
        return invert ? isNull : !isNull;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public class RepeatModeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is RepeatMode mode ? mode switch
        {
            RepeatMode.None => MaterialIconKind.Repeat,
            RepeatMode.RepeatOne => MaterialIconKind.RepeatOne,
            RepeatMode.RepeatAll => MaterialIconKind.Repeat,
            _ => MaterialIconKind.Repeat
        } : MaterialIconKind.Repeat;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class VolumeToIconConverter : IValueConverter
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

public class BoolToSelectorConverter : IValueConverter
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

public class BoolToCursorConverter : IValueConverter
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

public class BitmapAssetValueConverter : IValueConverter
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

public class StringStartsWithConverter : IValueConverter
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

public class WebUrlConverter : IValueConverter
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

public class ProgressToWidthConverter : IMultiValueConverter
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

public class GreaterThanConverter : IValueConverter
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


