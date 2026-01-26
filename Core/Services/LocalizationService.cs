using Avalonia.Platform;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace LMP.Core.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance = new();

    private string _currentLanguage = "en"; // Дефолт - английский
    private Dictionary<string, string> _resources = [];
    private bool _isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    public List<LanguageItem> AvailableLanguages { get; } =
    [
        new() { Code = "en", Name = "English" },
        new() { Code = "ru", Name = "Русский" }
    ];

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value && AvailableLanguages.Any(l => l.Code == value))
            {
                Log.Info($"Changing language: {_currentLanguage} → {value}");
                _currentLanguage = value;
                LoadLanguage(value);

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                LanguageChanged?.Invoke(this, value);
            }
        }
    }

    // Приватный конструктор - НЕ загружаем язык автоматически!
    private LocalizationService()
    {
        // Загружаем английский как fallback
        LoadLanguage("en");
        Log.Info("Service created with default language: en");
    }

    /// <summary>
    /// Инициализация с сохранённым языком. Вызывать при старте приложения!
    /// </summary>
    public void Initialize(string? savedLanguageCode)
    {
        if (_isInitialized) return;

        string langToUse = "en";

        // 1. Приоритет: сохранённые настройки
        if (!string.IsNullOrEmpty(savedLanguageCode) &&
            AvailableLanguages.Any(l => l.Code == savedLanguageCode))
        {
            langToUse = savedLanguageCode;
            Log.Info($"Using saved language: {langToUse}");
        }
        // 2. Fallback: системная локаль
        else
        {
            var sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (AvailableLanguages.Any(l => l.Code == sysLang))
            {
                langToUse = sysLang;
                Log.Info($"Using system language: {langToUse}");
            }
            else
            {
                Log.Info($"System language '{sysLang}' not supported, using: en");
            }
        }

        _currentLanguage = langToUse;
        LoadLanguage(langToUse);
        _isInitialized = true;
    }

    private void LoadLanguage(string langCode)
    {
        try
        {
            var uri = new Uri($"avares://LMP/Assets/Localization/{langCode}.json");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                _resources = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                             ?? new Dictionary<string, string>();
                Log.Info($"Loaded {langCode}.json ({_resources.Count} keys)");
            }
            else
            {
                Log.Info($"File not found: {uri}");
                if (langCode != "en") LoadLanguage("en");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Error loading '{langCode}': {ex.Message}");
            if (langCode != "en") LoadLanguage("en");
        }
    }

    /// <summary>
    /// Индексатор для получения строки
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (_resources.TryGetValue(key, out var value))
                return value;

            Log.Info($"Missing key: {key}");
            return $"[{key}]";
        }
    }

    /// <summary>
    /// Альтернативный метод получения строки
    /// </summary>
    public string Get(string key, string? fallback = null)
    {
        if (_resources.TryGetValue(key, out var value))
            return value;
        return fallback ?? $"[{key}]";
    }

    public string RawGet(string key) => _resources[key];

    public string GetPlural(string key, int number)
    {
        if (!_resources.TryGetValue(key, out var val)) return key;

        var forms = val.Split('|', StringSplitOptions.RemoveEmptyEntries);
        int n = Math.Abs(number);

        // Логика для Русского (3 формы: 1 трек, 2 трека, 5 треков)
        if (_currentLanguage == "ru")
        {
            if (forms.Length < 3) return $"{number} {forms[0]}";

            int n100 = n % 100;
            int n10 = n % 10;

            if (n100 > 10 && n100 < 20) return $"{number} {forms[2]}";
            if (n10 > 1 && n10 < 5) return $"{number} {forms[1]}";
            if (n10 == 1) return $"{number} {forms[0]}";
            return $"{number} {forms[2]}";
        }

        // Логика для Английского (2 формы: 1 track, 2 tracks)
        // И общий фоллбек
        if (forms.Length >= 2)
        {
            return n == 1 ? $"{number} {forms[0]}" : $"{number} {forms[1]}";
        }

        return $"{number} {forms[0]}";
    }
}

public class LanguageItem
{
    public required string Code { get; set; }
    public required string Name { get; set; }

    public override string ToString() => Name;
}
