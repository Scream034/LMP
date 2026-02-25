using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Core.Models;

public sealed class BootstrapSettings
{
    public string LanguageCode { get; set; } = "en";
    public bool IsFirstRun { get; set; } = true;
    public string? ThemeJson { get; set; }
    
    [JsonIgnore]
    private static readonly string FilePath = G.FilePath.Bootstrap;

    public static BootstrapSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<BootstrapSettings>(json);
                if (settings != null)
                {
                    // ═══ ПРОВЕРКА ПЕРВОГО ЗАПУСКА ═══
                    if (settings.IsFirstRun)
                    {
                        // Автоопределение языка при первом запуске
                        settings.LanguageCode = G.SystemInfo.DetectSystemLanguage();
                        settings.IsFirstRun = false;
                        settings.Save();
                        Log.Info($"[Bootstrap] First run, detected language: {settings.LanguageCode}");
                    }
                    else
                    {
                        Log.Info($"[Bootstrap] Loaded: lang={settings.LanguageCode}");
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Bootstrap] Failed to load: {ex.Message}");
        }

        // ═══ ПЕРВЫЙ ЗАПУСК (файла нет) ═══
        var defaults = new BootstrapSettings
        {
            IsFirstRun = false,
            LanguageCode = G.SystemInfo.DetectSystemLanguage()
        };
        
        Log.Info($"[Bootstrap] First run (no file), detected language: {defaults.LanguageCode}");
        defaults.Save();
        
        return defaults;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, G.Json.Beautiful);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[Bootstrap] Failed to save: {ex.Message}");
        }
    }
    
    public static BootstrapSettings Current { get; private set; } = new();
    
    public static void Initialize()
    {
        Current = Load();
    }
}