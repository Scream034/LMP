using Avalonia.Data.Converters;
using Avalonia.Media;
using MyLiteMusicPlayer.Models;
using System.Globalization;
using Material.Icons;
using Avalonia.Input; // Убедитесь, что этот using есть

namespace MyLiteMusicPlayer.Converters;

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
                if (targetType == typeof(IBrush))
                {
                    try { return SolidColorBrush.Parse(resultString); } catch { }
                }

                return resultString;
            }
        }
        return null; // или значение по умолчанию
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

/// <summary>
/// Конвертирует строку в верхний регистр.
/// </summary>
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