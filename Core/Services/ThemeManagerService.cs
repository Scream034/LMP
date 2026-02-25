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
        // ═══ ЗАЩИТА: проверка готовности Application ═══
        if (Application.Current?.Resources is not { } resources)
        {
            Log.Warn("Application.Current.Resources not available yet, deferring theme application");
            _cachedTheme = theme; // Сохраняем для повторной попытки
            return;
        }

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

        // ═══ КОНТРАСТНЫЙ ТЕКСТ ДЛЯ ACCENT КНОПОК ═══
        // Автоматически определяем чёрный или белый текст на акцентном фоне
        _ = TryParseColor(theme.AccentColor, out var accent);
        var accentButtonText = GetContrastingTextColor(accent);

        // Полный цвет
        resources["AccentButtonText"] = accentButtonText;
        resources["AccentButtonTextBrush"] = new SolidColorBrush(accentButtonText);

        // Прозрачная версия для плавных анимаций border (тот же RGB, альфа = 0)
        // Предотвращает белые вспышки при BrushTransition от/к "невидимому" состоянию
        var accentButtonTextTransparent = new Color(0, accentButtonText.R, accentButtonText.G, accentButtonText.B);
        resources["AccentButtonTextTransparent"] = accentButtonTextTransparent;
        resources["AccentButtonTextTransparentBrush"] = new SolidColorBrush(accentButtonTextTransparent);

        // ═══ AVALONIA FLUENT THEME COMPATIBILITY ═══
        // Переопределяем системные ресурсы для корректной работы стандартных контролов
        ApplyFluentOverrides(resources, theme);

        Log.Info($"Theme '{theme.Name}' applied.");
    }

    /// <summary>
    /// Определяет контрастный цвет текста для данного фона.
    /// Использует формулу относительной яркости WCAG 2.0.
    /// </summary>
    /// <param name="background">Цвет фона</param>
    /// <returns>Чёрный или белый цвет, обеспечивающий максимальный контраст</returns>
    private static Color GetContrastingTextColor(Color background)
    {
        // Линеаризация sRGB канала по WCAG 2.0
        double Linearize(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linearize(background.R)
                      + 0.7152 * Linearize(background.G)
                      + 0.0722 * Linearize(background.B);

        // Порог 0.35 — оптимален для тёмных тем, обеспечивает читаемость
        return luminance > 0.35
            ? Color.FromRgb(0, 0, 0)        // Тёмный текст на светлом фоне
            : Color.FromRgb(255, 255, 255);  // Светлый текст на тёмном фоне
    }

    /// <summary>
    /// Применяет переопределения для Fluent темы Avalonia.
    /// Необходимо для корректной работы стандартных контролов (CheckBox, ToggleSwitch, 
    /// ComboBox, Slider, диалоговые окна и т.д.) с кастомными цветами темы.
    /// 
    /// ВАЖНО: AccentButton* ресурсы используют accentButtonText для foreground,
    /// что гарантирует контрастность на ЛЮБОЙ теме (включая AMOLED с белым акцентом).
    /// </summary>
    private static void ApplyFluentOverrides(IResourceDictionary resources, ThemeSettings theme)
    {
        _ = TryParseColor(theme.AccentColor, out var accent);
        _ = TryParseColor(theme.AccentHover, out var accentHover);
        _ = TryParseColor(theme.TextPrimary, out var textPrimary);
        _ = TryParseColor(theme.TextDark, out var textDark);
        _ = TryParseColor(theme.TextSecondary, out var textSecondary);
        _ = TryParseColor(theme.BgPrimary, out var bgPrimary);
        _ = TryParseColor(theme.BgSecondary, out var bgSecondary);
        _ = TryParseColor(theme.BgElevated, out var bgElevated);
        _ = TryParseColor(theme.BgHighlight, out var bgHighlight);
        _ = TryParseColor(theme.BgHover, out var bgHover);
        _ = TryParseColor(theme.TextMuted, out var textMuted);

        // Контрастный текст для акцентных элементов
        var accentButtonText = GetContrastingTextColor(accent);

        // ═══ SYSTEM ACCENT COLORS ═══
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorDark1"] = accentHover;
        resources["SystemAccentColorDark2"] = accentHover;
        resources["SystemAccentColorDark3"] = accentHover;
        resources["SystemAccentColorLight1"] = accentHover;
        resources["SystemAccentColorLight2"] = accentHover;
        resources["SystemAccentColorLight3"] = accentHover;

        // ═══ TEXT ON ACCENT (для CheckBox, ToggleSwitch, диалоговых кнопок) ═══
        resources["TextOnAccentFillColorPrimary"] = accentButtonText;
        resources["TextOnAccentFillColorSecondary"] = accentButtonText;
        resources["TextOnAccentFillColorDisabled"] = textSecondary;
        resources["TextOnAccentFillColorSelectedText"] = accentButtonText;

        // ═══ GENERAL TEXT ═══
        resources["TextFillColorPrimary"] = textPrimary;
        resources["TextFillColorSecondary"] = textSecondary;
        resources["TextFillColorTertiary"] = textSecondary;
        resources["TextFillColorDisabled"] = textSecondary;

        // ═══ CONTROL BACKGROUNDS ═══
        resources["ControlFillColorDefault"] = bgElevated;
        resources["ControlFillColorSecondary"] = bgElevated;
        resources["ControlFillColorTertiary"] = bgHighlight;
        resources["ControlFillColorInputActive"] = bgHover;
        resources["ControlFillColorDisabled"] = bgHighlight;

        resources["ControlStrokeColorDefault"] = bgHighlight;
        resources["ControlStrokeColorSecondary"] = bgHighlight;
        resources["ControlStrongStrokeColorDefault"] = textSecondary;
        resources["ControlStrongStrokeColorDisabled"] = bgHighlight;

        // ═══ SUBTLE FILLS ═══
        resources["SubtleFillColorTransparent"] = Colors.Transparent;
        resources["SubtleFillColorSecondary"] = bgHover;
        resources["SubtleFillColorTertiary"] = bgHighlight;
        resources["SubtleFillColorDisabled"] = Colors.Transparent;

        // ═══ ACCENT FILLS — для системных контролов ═══
        resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        resources["AccentFillColorSecondary"] = accentHover;
        resources["AccentFillColorTertiary"] = accent;
        resources["AccentFillColorDisabled"] = bgHighlight;

        // ═══ ACCENT BUTTON — для системных диалогов (ContentDialog) ═══
        // Foreground ВСЕГДА = accentButtonText, даже при PointerOver и Pressed
        resources["AccentButtonBackground"] = accent;
        resources["AccentButtonBackgroundPointerOver"] = accent;
        resources["AccentButtonBackgroundPressed"] = accentHover;
        resources["AccentButtonBackgroundDisabled"] = bgHighlight;

        resources["AccentButtonForeground"] = accentButtonText;
        resources["AccentButtonForegroundPointerOver"] = accentButtonText;
        resources["AccentButtonForegroundPressed"] = accentButtonText;
        resources["AccentButtonForegroundDisabled"] = textSecondary;

        resources["AccentButtonBorderBrush"] = Colors.Transparent;
        resources["AccentButtonBorderBrushPointerOver"] = accentButtonText;
        resources["AccentButtonBorderBrushPressed"] = Colors.Transparent;
        resources["AccentButtonBorderBrushDisabled"] = Colors.Transparent;

        // ═══ CHECKBOX ═══
        resources["CheckGlyphForeground"] = accentButtonText;
        resources["CheckGlyphForegroundChecked"] = accentButtonText;
        resources["CheckGlyphForegroundCheckedPointerOver"] = accentButtonText;
        resources["CheckGlyphForegroundCheckedPressed"] = accentButtonText;
        resources["CheckGlyphForegroundCheckedDisabled"] = textSecondary;
        resources["CheckGlyphForegroundIndeterminate"] = accentButtonText;

        // ═══ TOGGLESWITCH: убираем ВСЕ возможные фоны ═══
        // Основные контейнеры
        resources["ToggleSwitchContainerBackground"] = Colors.Transparent;
        resources["ToggleSwitchContainerBackgroundPointerOver"] = Colors.Transparent;
        resources["ToggleSwitchContainerBackgroundPressed"] = Colors.Transparent;
        resources["ToggleSwitchContainerBackgroundDisabled"] = Colors.Transparent;

        // Content Grid
        resources["ToggleSwitchContentGridBackground"] = Colors.Transparent;
        resources["ToggleSwitchContentGridBackgroundPointerOver"] = Colors.Transparent;
        resources["ToggleSwitchContentGridBackgroundPressed"] = Colors.Transparent;
        resources["ToggleSwitchContentGridBackgroundDisabled"] = Colors.Transparent;

        // Track background (область между knob)
        resources["ToggleSwitchTrackBackground"] = Colors.Transparent;
        resources["ToggleSwitchTrackBackgroundPointerOver"] = Colors.Transparent;
        resources["ToggleSwitchTrackBackgroundPressed"] = Colors.Transparent;
        resources["ToggleSwitchTrackBackgroundDisabled"] = Colors.Transparent;

        // Knob background (под самой ручкой)
        resources["ToggleSwitchKnobBackground"] = Colors.Transparent;
        resources["ToggleSwitchKnobBackgroundPointerOver"] = Colors.Transparent;
        resources["ToggleSwitchKnobBackgroundPressed"] = Colors.Transparent;
        resources["ToggleSwitchKnobBackgroundDisabled"] = Colors.Transparent;

        // Pill (переключатель) — OFF state
        resources["ToggleSwitchFillOff"] = bgHighlight;
        resources["ToggleSwitchFillOffPointerOver"] = bgHighlight;
        resources["ToggleSwitchFillOffPressed"] = bgHighlight;
        resources["ToggleSwitchFillOffDisabled"] = bgHighlight;

        // Pill — ON state
        resources["ToggleSwitchFillOn"] = accent;
        resources["ToggleSwitchFillOnPointerOver"] = accent;
        resources["ToggleSwitchFillOnPressed"] = accent;
        resources["ToggleSwitchFillOnDisabled"] = bgHighlight;

        // Border stroke — OFF
        resources["ToggleSwitchStrokeOff"] = bgHighlight;
        resources["ToggleSwitchStrokeOffPointerOver"] = accent;
        resources["ToggleSwitchStrokeOffPressed"] = accent;
        resources["ToggleSwitchStrokeOffDisabled"] = bgHighlight;

        // Border stroke — ON
        resources["ToggleSwitchStrokeOn"] = accent;
        resources["ToggleSwitchStrokeOnPointerOver"] = accentHover;
        resources["ToggleSwitchStrokeOnPressed"] = accent;
        resources["ToggleSwitchStrokeOnDisabled"] = bgHighlight;

        // Knob fill (сама ручка)
        resources["ToggleSwitchKnobFillOff"] = textSecondary;
        resources["ToggleSwitchKnobFillOffPointerOver"] = textPrimary;
        resources["ToggleSwitchKnobFillOffPressed"] = textSecondary;
        resources["ToggleSwitchKnobFillOffDisabled"] = textSecondary;

        resources["ToggleSwitchKnobFillOn"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnPointerOver"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnPressed"] = accentButtonText;
        resources["ToggleSwitchKnobFillOnDisabled"] = textSecondary;

        // Header и дополнительные элементы
        resources["ToggleSwitchHeaderForeground"] = textPrimary;
        resources["ToggleSwitchHeaderForegroundDisabled"] = textSecondary;
        resources["ToggleSwitchOnContentForeground"] = textPrimary;
        resources["ToggleSwitchOffContentForeground"] = textPrimary;

        // ═══ SLIDER ═══
        resources["SliderTrackFill"] = bgHighlight;
        resources["SliderTrackFillPointerOver"] = bgHighlight;
        resources["SliderTrackFillPressed"] = bgHighlight;
        resources["SliderTrackFillDisabled"] = bgHighlight;

        resources["SliderTrackValueFill"] = accent;
        resources["SliderTrackValueFillPointerOver"] = accentHover;
        resources["SliderTrackValueFillPressed"] = accent;
        resources["SliderTrackValueFillDisabled"] = bgHighlight;

        resources["SliderThumbBackground"] = textPrimary;
        resources["SliderThumbBackgroundPointerOver"] = accent;
        resources["SliderThumbBackgroundPressed"] = accentHover;
        resources["SliderThumbBackgroundDisabled"] = textSecondary;

        // ═══ COMBOBOX ═══
        resources["ComboBoxBackground"] = bgElevated;
        resources["ComboBoxBackgroundPointerOver"] = bgHover;
        resources["ComboBoxBackgroundPressed"] = bgHighlight;
        resources["ComboBoxBackgroundDisabled"] = bgHighlight;

        resources["ComboBoxForeground"] = textPrimary;
        resources["ComboBoxForegroundPointerOver"] = textPrimary;
        resources["ComboBoxForegroundPressed"] = textPrimary;
        resources["ComboBoxForegroundDisabled"] = textSecondary;

        resources["ComboBoxDropDownBackground"] = bgElevated;
        resources["ComboBoxDropDownBorderBrush"] = bgHighlight;

        resources["ComboBoxItemForeground"] = textPrimary;
        resources["ComboBoxItemForegroundPointerOver"] = textPrimary;
        resources["ComboBoxItemForegroundPressed"] = textPrimary;
        resources["ComboBoxItemForegroundDisabled"] = textSecondary;

        resources["ComboBoxItemForegroundSelected"] = accentButtonText;
        resources["ComboBoxItemForegroundSelectedPointerOver"] = accentButtonText;
        resources["ComboBoxItemForegroundSelectedPressed"] = accentButtonText;
        resources["ComboBoxItemForegroundSelectedDisabled"] = textSecondary;

        resources["ComboBoxItemBackgroundSelected"] = accent;
        resources["ComboBoxItemBackgroundSelectedPointerOver"] = accentHover;
        resources["ComboBoxItemBackgroundSelectedPressed"] = accent;

        // ═══ LISTBOX ═══
        resources["ListBoxItemForeground"] = textPrimary;
        resources["ListBoxItemForegroundSelected"] = accentButtonText;
        resources["ListBoxItemForegroundSelectedPointerOver"] = accentButtonText;
        resources["ListBoxItemBackgroundSelected"] = accent;
        resources["ListBoxItemBackgroundSelectedPointerOver"] = accentHover;

        // ═══ BACKGROUNDS ═══
        resources["SolidBackgroundFillColorBase"] = bgPrimary;
        resources["SolidBackgroundFillColorSecondary"] = bgSecondary;
        resources["SolidBackgroundFillColorTertiary"] = bgElevated;
        resources["SolidBackgroundFillColorQuarternary"] = bgHighlight;

        resources["LayerFillColorDefault"] = bgElevated;
        resources["LayerFillColorAlt"] = bgSecondary;
        resources["LayerOnMicaBaseAltFillColorDefault"] = bgPrimary;

        resources["CardBackgroundFillColorDefault"] = bgElevated;
        resources["CardBackgroundFillColorSecondary"] = bgSecondary;
        resources["CardStrokeColorDefault"] = bgHighlight;

        resources["DividerStrokeColorDefault"] = bgHighlight;

        // ═══ FLYOUT / POPUP ═══
        resources["FlyoutBackground"] = bgElevated;
        resources["FlyoutBorderThemeBrush"] = new SolidColorBrush(bgHighlight);

        // ═══ MENU ═══
        resources["MenuFlyoutPresenterBackground"] = bgElevated;
        resources["MenuFlyoutPresenterBorderBrush"] = new SolidColorBrush(bgHighlight);
        resources["MenuFlyoutItemBackground"] = Colors.Transparent;
        resources["MenuFlyoutItemBackgroundPointerOver"] = bgHighlight;
        resources["MenuFlyoutItemForeground"] = textPrimary;
        resources["MenuFlyoutItemForegroundPointerOver"] = textPrimary;

        // ═══ BUTTON — обычные кнопки ═══
        // Foreground фиксируется для всех состояний = textPrimary
        resources["ButtonBackground"] = bgElevated;
        resources["ButtonBackgroundPointerOver"] = bgElevated;
        resources["ButtonBackgroundPressed"] = bgHighlight;
        resources["ButtonBackgroundDisabled"] = bgHighlight;

        resources["ButtonForeground"] = textPrimary;
        resources["ButtonForegroundPointerOver"] = textPrimary;
        resources["ButtonForegroundPressed"] = textPrimary;
        resources["ButtonForegroundDisabled"] = textSecondary;

        resources["ButtonBorderBrush"] = bgHighlight;
        resources["ButtonBorderBrushPointerOver"] = accent;
        resources["ButtonBorderBrushPressed"] = accentHover;
        resources["ButtonBorderBrushDisabled"] = bgHighlight;

        // ═══ CONTENT DIALOG ═══
        resources["ContentDialogBackground"] = bgElevated;
        resources["ContentDialogTopOverlay"] = bgElevated;
        resources["ContentDialogBorderBrush"] = bgHighlight;
        resources["ContentDialogForeground"] = textPrimary;

        // ═══ ANTI-FLASH: Fluent hover → border вместо фона ═══

        // ComboBox: hover = border, не фон
        resources["ComboBoxBackgroundPointerOver"] = bgElevated;
        resources["ComboBoxBackgroundPressed"] = bgElevated;
        resources["ComboBoxBorderBrushPointerOver"] = accent;

        // ComboBoxItem: border прозрачный по умолчанию, виден при hover
        var bgHighlightTransparent = new Color(0, bgHighlight.R, bgHighlight.G, bgHighlight.B);
        resources["ComboBoxItemBackgroundPointerOver"] = Colors.Transparent;
        resources["ComboBoxItemBackgroundPressed"] = Colors.Transparent;
        resources["ComboBoxItemBorderBrush"] = bgHighlightTransparent;
        resources["ComboBoxItemBorderBrushPointerOver"] = bgHighlight;
        resources["ComboBoxItemBorderBrushPressed"] = bgHighlight;
        resources["ComboBoxItemBorderBrushSelected"] = accent;
        resources["ComboBoxItemBorderBrushSelectedPointerOver"] = accentHover;

        // CheckBox: hover фон не меняется, border = акцент
        resources["CheckBoxCheckBackgroundFillUnchecked"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillUncheckedPressed"] = bgElevated;
        resources["CheckBoxCheckBackgroundFillChecked"] = accent;
        resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = accent;
        resources["CheckBoxCheckBackgroundFillCheckedPressed"] = accent;

        resources["CheckBoxCheckBackgroundStrokeUnchecked"] = bgHighlight;
        resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = accent;
        resources["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = accent;
        resources["CheckBoxCheckBackgroundStrokeChecked"] = accent;
        resources["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = accentHover;
        resources["CheckBoxCheckBackgroundStrokeCheckedPressed"] = accent;

        // RadioButton: контейнер без фона
        resources["RadioButtonBackground"] = Colors.Transparent;
        resources["RadioButtonBackgroundPointerOver"] = Colors.Transparent;
        resources["RadioButtonBackgroundPressed"] = Colors.Transparent;
        resources["RadioButtonBackgroundDisabled"] = Colors.Transparent;

        // RadioButton: hover фон эллипса не меняется
        resources["RadioButtonOuterEllipseFill"] = bgElevated;
        resources["RadioButtonOuterEllipseFillPointerOver"] = bgElevated;
        resources["RadioButtonOuterEllipseFillPressed"] = bgElevated;
        resources["RadioButtonOuterEllipseStroke"] = bgHighlight;
        resources["RadioButtonOuterEllipseStrokePointerOver"] = accent;
        resources["RadioButtonOuterEllipseStrokePressed"] = accent;

        resources["RadioButtonOuterEllipseCheckedFill"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedFillPointerOver"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedFillPressed"] = bgElevated;
        resources["RadioButtonOuterEllipseCheckedStroke"] = accent;
        resources["RadioButtonOuterEllipseCheckedStrokePointerOver"] = accentHover;
        resources["RadioButtonOuterEllipseCheckedStrokePressed"] = accent;

        // ToggleSwitch: hover = border, не фон
        resources["ToggleSwitchContainerBackground"] = bgHighlight;
        resources["ToggleSwitchContainerBackgroundPointerOver"] = bgHighlight;
        resources["ToggleSwitchContainerBackgroundPressed"] = bgHighlight;

        // ListBoxItem: hover = border, не фон
        resources["ListBoxItemBackgroundPointerOver"] = Colors.Transparent;
        resources["ListBoxItemBackgroundPressed"] = Colors.Transparent;
        resources["ListBoxItemBackgroundSelected"] = accent;
        resources["ListBoxItemBackgroundSelectedPointerOver"] = accent;
        resources["ListBoxItemBackgroundSelectedPressed"] = accent;
        resources["ListBoxItemBorderBrush"] = bgHighlightTransparent;
        resources["ListBoxItemBorderBrushPointerOver"] = bgHighlight;

        // Slider thumb
        resources["SliderThumbBackground"] = textPrimary;
        resources["SliderThumbBackgroundPointerOver"] = accent;
        resources["SliderThumbBackgroundPressed"] = accentHover;

        // MenuItem: hover = border
        resources["MenuFlyoutItemBackgroundPointerOver"] = Colors.Transparent;
        resources["MenuFlyoutItemBackgroundPressed"] = Colors.Transparent;

        // FOCUS VISUAL
        resources["SystemControlFocusVisualPrimaryBrush"] = new SolidColorBrush(accent) { Opacity = 0.85 };
        resources["SystemControlFocusVisualSecondaryBrush"] = new SolidColorBrush(Colors.Transparent);
        resources["SystemControlFocusVisualPrimaryThickness"] = new Thickness(2);
        resources["SystemControlFocusVisualSecondaryThickness"] = new Thickness(0);
    }

    /// <summary>
    /// Сохраняет тему на диск
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(theme, G.Json.Beautiful);
            File.WriteAllText(G.FilePath.Theme, json);
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
            if (File.Exists(G.FilePath.Theme))
                File.Delete(G.FilePath.Theme);
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
            AccentHover = "#20e063",
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
            AccentHover = "#45c9e4",
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
            AccentHover = "#CCCCCC",
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
            if (File.Exists(G.FilePath.Theme))
            {
                var json = File.ReadAllText(G.FilePath.Theme);
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
            color = Colors.Magenta;
        }

        resources[key] = color;
        resources[$"{key}Brush"] = new SolidColorBrush(color);

        // Прозрачная версия для анимаций (альфа = 0, но тот же RGB)
        var transparent = new Color(0, color.R, color.G, color.B);
        resources[$"{key}Transparent"] = transparent;
        resources[$"{key}TransparentBrush"] = new SolidColorBrush(transparent);
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