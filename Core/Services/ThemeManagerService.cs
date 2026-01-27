using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.Core.Services;

/// <summary>
/// Настройки темы приложения.
/// Все цвета хранятся в HEX-формате (#RRGGBB или #AARRGGBB).
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>Имя темы для отображения</summary>
    public string Name { get; set; } = "Paralax Purple";

    // BACKGROUNDS - Фоновые цвета

    /// <summary>Основной фон окна</summary>
    public string BgPrimary { get; set; } = "#0F0B15";

    /// <summary>Фон карточек и сайдбара</summary>
    public string BgSecondary { get; set; } = "#1A1625";

    /// <summary>Фон диалогов и меню</summary>
    public string BgElevated { get; set; } = "#252033";

    /// <summary>Разделители и границы</summary>
    public string BgHighlight { get; set; } = "#322A45";

    /// <summary>Hover-состояние</summary>
    public string BgHover { get; set; } = "#3E3456";

    // SKELETON / LOADING - Цвета загрузки

    /// <summary>Скелетон светлый</summary>
    public string BgSkeleton { get; set; } = "#2A2438";

    /// <summary>Скелетон темный</summary>
    public string BgSkeletonDeep { get; set; } = "#15121C";

    /// <summary>Оверлей (полупрозрачный)</summary>
    public string BgOverlay { get; set; } = "#CC0A080F";

    // ACCENT - Акцентные цвета бренда

    /// <summary>Основной акцентный цвет (кнопки, ссылки)</summary>
    public string AccentColor { get; set; } = "#8A2BE2";

    /// <summary>Акцент при наведении</summary>
    public string AccentHover { get; set; } = "#A560F0";

    // SEMANTIC - Системные цвета

    /// <summary>Цвет ошибки</summary>
    public string SystemError { get; set; } = "#FF5555";

    /// <summary>Фон ошибки</summary>
    public string SystemErrorBg { get; set; } = "#331010";

    /// <summary>Информационный цвет</summary>
    public string SystemInfoBlue { get; set; } = "#8BE9FD";

    /// <summary>Предупреждение</summary>
    public string SystemWarnOrange { get; set; } = "#FFB86C";

    // TEXT - Цвета текста

    /// <summary>Основной текст</summary>
    public string TextPrimary { get; set; } = "#F8F8F2";

    /// <summary>Вторичный текст (подзаголовки)</summary>
    public string TextSecondary { get; set; } = "#BFB6D3";

    /// <summary>Приглушенный текст</summary>
    public string TextMuted { get; set; } = "#6272A4";

    /// <summary>Темный текст (на светлом фоне)</summary>
    public string TextDark { get; set; } = "#0F0B15";

    // SERIALIZATION

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// Сервис управления темами приложения.
/// Отвечает за загрузку, сохранение и применение тем.
/// </summary>
public sealed class ThemeManagerService
{
    private ThemeSettings? _cachedTheme;

    // PUBLIC API

    /// <summary>
    /// Загружает и применяет тему при старте приложения
    /// </summary>
    public void LoadAndApplyThemeOnStartup()
    {
        var theme = LoadThemeFromDisk();
        ApplyTheme(theme);
    }

    /// <summary>
    /// Применяет тему к ресурсам приложения
    /// </summary>
    public void ApplyTheme(ThemeSettings theme)
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        _cachedTheme = theme;

        // Backgrounds
        SetColor(resources, "BgPrimary", theme.BgPrimary);
        SetColor(resources, "BgSecondary", theme.BgSecondary);
        SetColor(resources, "BgElevated", theme.BgElevated);
        SetColor(resources, "BgHighlight", theme.BgHighlight);
        SetColor(resources, "BgHover", theme.BgHover);
        SetColor(resources, "BgSkeleton", theme.BgSkeleton);
        SetColor(resources, "BgSkeletonDeep", theme.BgSkeletonDeep);
        SetColor(resources, "BgOverlay", theme.BgOverlay);

        // Accent
        SetColor(resources, "Accent", theme.AccentColor);
        SetColor(resources, "AccentHover", theme.AccentHover);

        // Semantic
        SetColor(resources, "SystemError", theme.SystemError);
        SetColor(resources, "SystemErrorBg", theme.SystemErrorBg);
        SetColor(resources, "SystemInfoBlue", theme.SystemInfoBlue);
        SetColor(resources, "SystemWarnOrange", theme.SystemWarnOrange);

        // Text
        SetColor(resources, "TextPrimary", theme.TextPrimary);
        SetColor(resources, "TextSecondary", theme.TextSecondary);
        SetColor(resources, "TextMuted", theme.TextMuted);
        SetColor(resources, "TextDark", theme.TextDark);

        // System accent compatibility
        if (TryParseColor(theme.AccentColor, out var accent))
        {
            resources["SystemAccentColor"] = accent;
            if (TryParseColor(theme.AccentHover, out var accentHover))
                resources["SystemAccentColorLight1"] = accentHover;
        }

        Log.Info($"Theme '{theme.Name}' applied.");
    }

    /// <summary>
    /// Сохраняет тему на диск
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(theme, G.Json.Beautiful);
            File.WriteAllText(G.File.Theme, json);
            _cachedTheme = theme;
            Log.Info($"Theme '{theme.Name}' saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает текущую загруженную тему
    /// </summary>
    public ThemeSettings GetCurrentTheme()
    {
        return _cachedTheme ?? LoadThemeFromDisk();
    }

    /// <summary>
    /// Возвращает дефолтную тему (Paralax Purple)
    /// </summary>
    public static ThemeSettings GetDefaultTheme() => new() { IsBuiltIn = true };

    /// <summary>
    /// Сбрасывает тему к дефолтной
    /// </summary>
    public void ResetToDefault()
    {
        try
        {
            if (File.Exists(G.File.Theme))
                File.Delete(G.File.Theme);
        }
        catch { /* Игнорируем ошибку удаления */ }

        var def = GetDefaultTheme();
        SaveTheme(def);
        ApplyTheme(def);
    }

    /// <summary>
    /// Возвращает список встроенных пресетов тем
    /// </summary>
    public static IReadOnlyList<ThemeSettings> GetBuiltInPresets() =>
    [
        // ═══ 1. PARALAX PURPLE (Default) ═══
        new ThemeSettings { IsBuiltIn = true },

        // ═══ 2. CLASSIC GREEN (Spotify-like) ═══
        new ThemeSettings
        {
            Name = "Classic Green",
            IsBuiltIn = true,
            BgPrimary = "#121212",
            BgSecondary = "#1E1E1E",
            BgElevated = "#282828",
            BgHighlight = "#404040",
            BgHover = "#505050",
            BgSkeleton = "#282828",
            BgSkeletonDeep = "#1a1a1a",
            BgOverlay = "#CC121212",
            AccentColor = "#1DB954",
            AccentHover = "#1ED760",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#B3B3B3",
            TextMuted = "#888888",
            TextDark = "#000000"
        },

        // ═══ 3. OCEAN DEEP ═══
        new ThemeSettings
        {
            Name = "Ocean Deep",
            IsBuiltIn = true,
            BgPrimary = "#001219",
            BgSecondary = "#001f2d",
            BgElevated = "#002d42",
            BgHighlight = "#003b57",
            BgHover = "#00506b",
            BgSkeleton = "#002535",
            BgSkeletonDeep = "#000d12",
            BgOverlay = "#CC001219",
            AccentColor = "#00B4D8",
            AccentHover = "#48CAE4",
            TextPrimary = "#E0FBFC",
            TextSecondary = "#98C1D9",
            TextMuted = "#5B8FA8",
            TextDark = "#001219"
        },

        // ═══ 4. AMOLED BLACK ═══
        new ThemeSettings
        {
            Name = "AMOLED Black",
            IsBuiltIn = true,
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgElevated = "#141414",
            BgHighlight = "#1F1F1F",
            BgHover = "#2A2A2A",
            BgSkeleton = "#141414",
            BgSkeletonDeep = "#050505",
            BgOverlay = "#CC000000",
            AccentColor = "#FFFFFF",
            AccentHover = "#E0E0E0",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#A0A0A0",
            TextMuted = "#606060",
            TextDark = "#000000"
        },

        // ═══ 5. WARM SUNSET ═══
        new ThemeSettings
        {
            Name = "Warm Sunset",
            IsBuiltIn = true,
            BgPrimary = "#1A1210",
            BgSecondary = "#261A16",
            BgElevated = "#33221C",
            BgHighlight = "#4A3228",
            BgHover = "#5C3F32",
            BgSkeleton = "#2A1C18",
            BgSkeletonDeep = "#120E0C",
            BgOverlay = "#CC1A1210",
            AccentColor = "#FF6B35",
            AccentHover = "#FF8C5A",
            TextPrimary = "#FFF5F0",
            TextSecondary = "#D4B5A5",
            TextMuted = "#8B7265",
            TextDark = "#1A1210"
        },

        // ═══ 6. DRACULA ═══
        new ThemeSettings
        {
            Name = "Dracula",
            IsBuiltIn = true,
            BgPrimary = "#282a36",
            BgSecondary = "#21222c",
            BgElevated = "#343746",
            BgHighlight = "#44475a",
            BgHover = "#4d5066",
            BgSkeleton = "#343746",
            BgSkeletonDeep = "#1e1f29",
            BgOverlay = "#CC282a36",
            AccentColor = "#bd93f9",
            AccentHover = "#d4b8ff",
            TextPrimary = "#f8f8f2",
            TextSecondary = "#bfbfbf",
            TextMuted = "#6272a4",
            TextDark = "#282a36"
        }
    ];

    // PRIVATE HELPERS

    private ThemeSettings LoadThemeFromDisk()
    {
        try
        {
            if (File.Exists(G.File.Theme))
            {
                var json = File.ReadAllText(G.File.Theme);
                var theme = JsonSerializer.Deserialize<ThemeSettings>(json, G.Json.Beautiful);
                if (theme != null)
                {
                    _cachedTheme = theme;
                    return theme;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load theme: {ex.Message}");
        }

        // Сохраняем дефолтную при первом запуске
        var def = GetDefaultTheme();
        SaveTheme(def);
        return def;
    }

    private static void SetColor(IResourceDictionary resources, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            Log.Error($"Invalid color: {key}={hex}");
            color = Colors.Magenta; // Яркий цвет для отладки
        }

        resources[key] = color;
        resources[$"{key}Brush"] = new SolidColorBrush(color);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}